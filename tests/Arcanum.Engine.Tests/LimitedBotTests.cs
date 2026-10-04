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
