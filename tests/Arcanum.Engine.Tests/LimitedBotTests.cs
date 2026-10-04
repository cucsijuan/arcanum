// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots.Limited;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Tests;

/// <summary>Draft picks and limited deck building by computer players.</summary>
public class LimitedBotTests
{
    private static DraftOption Creature(string name, string cost, int power, int toughness, string rarity = "common") =>
        new(new CardDefinition { Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Creature, Power = power, Toughness = toughness }, rarity);

    private static DraftOption Removal(string name, string cost) => new(new CardDefinition
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Instant,
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
    }, "common");

    [Fact]
    public void RemovalAndEfficientCreaturesRateAboveFiller()
    {
        Assert.True(CardRating.Rate(Removal("Doom", "{1}{B}").Card) > CardRating.Rate(Creature("Filler", "{4}{G}", 2, 2).Card));
        Assert.True(CardRating.Rate(Creature("Bear", "{1}{G}", 2, 2).Card) > CardRating.Rate(Creature("Weak", "{3}{G}", 1, 2).Card));
    }

    [Fact]
    public void ThePickerSettlesIntoItsColors()
    {
        var picks = Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? Creature($"G{i}", "{1}{G}", 2, 2) : Creature($"R{i}", "{1}{R}", 2, 2)).ToList();
        Assert.True(DraftPicker.FavoriteColors(picks).SetEquals(new[] { ManaType.Green, ManaType.Red }));
        var pack = new[] { Creature("Blue Bear", "{1}{U}", 2, 3), Creature("Green Bear", "{1}{G}", 2, 2) };
        Assert.Equal(1, DraftPicker.Pick(pack, picks, new Random(1)));
        // With nothing drafted, the better card wins.
        Assert.Equal(0, DraftPicker.Pick(pack, Array.Empty<DraftOption>(), new Random(1)));
    }

    [Fact]
    public void LimitedDecksHaveFortyCardsInTheBestTwoColors()
    {
        var pool = new List<DraftOption>();
        for (int i = 0; i < 14; i++) pool.Add(Creature($"Green {i}", "{1}{G}", 2, 2));
        for (int i = 0; i < 10; i++) pool.Add(Creature($"Black {i}", "{2}{B}", 3, 2));
        for (int i = 0; i < 4; i++) pool.Add(Removal($"Doom {i}", "{1}{B}"));
        for (int i = 0; i < 12; i++) pool.Add(Creature($"Blue {i}", "{4}{U}", 1, 1));
        pool.Add(new DraftOption(new CardDefinition { Name = "Swamp Grove", Types = CardType.Land, TapForMana = new[] { ManaType.Black, ManaType.Green } }, "common"));

        var deck = LimitedDeckBuilder.Build(pool);
        Assert.True(deck.Colors.SetEquals(new[] { ManaType.Green, ManaType.Black }));
        int lands = deck.BasicLands.Values.Sum() + deck.PoolCards.Count(i => pool[i].Card.Is(CardType.Land));
        Assert.Equal(40, deck.PoolCards.Count + deck.BasicLands.Values.Sum());
        Assert.Equal(17, lands);
        Assert.All(deck.PoolCards, i => Assert.True(pool[i].Card.Is(CardType.Land) || CardRating.Castable(pool[i].Card, deck.Colors)));
        Assert.Contains(deck.PoolCards, i => pool[i].Card.Name == "Swamp Grove");
        Assert.Equal(4, deck.PoolCards.Count(i => pool[i].Card.Name.StartsWith("Doom")));
    }
}

/// <summary>How the computer player blocks.</summary>
public class BotBlockingTests
{
    private static Cards.CardDefinition Creature(string name, int power, int toughness, params Cards.Keyword[] keywords) =>
        Scenario.Creature(name, power, toughness, keywords);

    [Fact]
    public async Task FacingCertainDeathItStillKillsWhatItCan()
    {
        var s = new Scenario();
        var p0 = Scenario.P0;
        var p1 = Scenario.P1;
        var menace = s.Add(p0, Creature("Brute", 5, 3, Cards.Keyword.Menace));
        var big = s.Add(p0, Creature("Ogre", 3, 4));
        var flierA = s.Add(p0, Creature("Hawk", 4, 2, Cards.Keyword.Flying));
        var flierB = s.Add(p0, Creature("Sprite", 1, 3, Cards.Keyword.Flying));
        foreach (var (name, p, t) in new[] { ("Cat", 1, 1), ("Lion", 2, 2), ("Hare", 2, 2), ("Soldier", 1, 1), ("Pup", 3, 1) })
            s.Add(p1, Creature(name, p, t));
        s.Game.State.GetPlayer(p1).Life = 5;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new Players.AttackDeclaration(a, defenders[0])).ToList();
        IReadOnlyList<Players.BlockDeclaration>? blocks = null;
        s.Defender.Block = (view, _, _) => blocks = Arcanum.Bots.BotController.PlanBlocks(view, s.Defender.LastBlockRequest!);
        await s.RunUntilTurn();
        Assert.NotNull(blocks);
        // The fliers can't be blocked and are lethal; it still destroys both ground attackers.
        Assert.True(blocks!.Count(b => b.Attacker == menace) >= 2);
        Assert.True(blocks!.Count(b => b.Attacker == big) >= 2);
        int PowerOn(Core.CardId attacker) => blocks.Where(b => b.Attacker == attacker).Sum(b => s.Card(b.Blocker).Power);
        Assert.True(PowerOn(menace) >= 3); // enough to destroy the 5/3
        Assert.True(PowerOn(big) >= 4);    // and the 3/4
    }

    [Fact]
    public async Task ItChumpsToSurviveWhenThatsEnough()
    {
        var s = new Scenario();
        var big = s.Add(Scenario.P0, Creature("Giant", 6, 6));
        var small = s.Add(Scenario.P0, Creature("Bear", 2, 2));
        s.Add(Scenario.P1, Creature("Squire", 1, 1));
        s.Game.State.GetPlayer(Scenario.P1).Life = 7;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new Players.AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (view, _, _) => Arcanum.Bots.BotController.PlanBlocks(view, s.Defender.LastBlockRequest!);
        await s.RunUntilTurn();
        Assert.Equal(5, s.Game.State.GetPlayer(Scenario.P1).Life); // blocked the 6/6, took 2
        Assert.Equal(State.Zone.Battlefield, s.Card(big).Zone);
        Assert.Equal(State.Zone.Battlefield, s.Card(small).Zone);
    }
}
