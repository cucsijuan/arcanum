// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Sideboards as the cards outside the game (rule 400.11b) and "reveal two cards from outside the game".</summary>
public class OutsideTheGameTests
{
    private static readonly CardDefinition Wish = new()
    {
        Name = "Far Wish", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
        Spell = new SpellAbility { Effects = new Effect[] { new RevealTwoFromOutsideOpponentChooses() } },
    };

    private static Scenario Wishing(params CardDefinition[] sideboard)
    {
        var s = new Scenario(sideboard: sideboard);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public void SideboardCardsStartOutsideTheGame()
    {
        var s = new Scenario(sideboard: new[] { Creature("Spare Bear", 2, 2) });
        var outside = s.Game.State.GetPlayer(P0).Outside;
        Assert.Single(outside);
        Assert.Equal(Zone.Outside, s.Card(outside[0]).Zone);
        Assert.Empty(s.Game.State.GetPlayer(P1).Outside);
    }

    [Fact]
    public async Task OpponentChoosesOneOfTwoRevealedCardsWhichGoesToHand()
    {
        var s = Wishing(Creature("Spare Bear", 2, 2), Creature("Spare Wolf", 3, 3));
        s.Lands(P0, 1);
        s.InHand(P0, Wish);
        await s.RunUntilTurn();
        var moved = s.Game.Log.OfType<CardMoved>().Where(m => m.From == Zone.Outside && m.To == Zone.Hand).ToList();
        var single = Assert.Single(moved);
        Assert.Contains(s.Game.Log, e => e is CardsRevealed r && r.Cards.Count == 2);
        Assert.Single(s.Game.State.GetPlayer(P0).Outside); // the other stays outside
        Assert.NotEqual(Zone.Outside, s.Card(single.Card).Zone);
    }

    [Fact]
    public async Task NothingHappensWithoutTwoDifferentNamesOutside()
    {
        var s = Wishing(Creature("Spare Bear", 2, 2), Creature("Spare Bear", 2, 2));
        s.Lands(P0, 1);
        s.InHand(P0, Wish);
        await s.RunUntilTurn();
        Assert.DoesNotContain(s.Game.Log, e => e is CardMoved { From: Zone.Outside });
        Assert.Equal(2, s.Game.State.GetPlayer(P0).Outside.Count);
    }
}
