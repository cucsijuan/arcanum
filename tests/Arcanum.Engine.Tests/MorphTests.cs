// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Morph and face-down permanents (rules 702.37 and 708): a morph card cast face down as a 2/2 creature for {3} with no
/// characteristics the opponents can see, turned face up for its morph cost as a special action, and revealed when it dies face up.
/// </summary>
public class MorphTests
{
    /// <summary>A creature with morph: cast face down for {3}, turned face up for {3}{R}.</summary>
    private static CardDefinition Colossus() => new()
    {
        Name = "Veiled Colossus",
        ManaCost = ManaCost.Parse("{5}{R}{R}"),
        Types = CardType.Creature,
        Power = 6,
        Toughness = 6,
        Keywords = new[] { "Flying" },
        Morph = ManaCost.Parse("{3}{R}"),
    };

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task FaceDownCastIsA2of2ColorlessCreatureWithNoText()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Colossus());
        await s.RunUntilTurn();
        var card = s.Card(id);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.True(card.TurnedFaceDown);
        Assert.Equal(2, card.Power);
        Assert.Equal(2, card.Toughness);
        Assert.False(card.Has(Keyword.Flying));
        Assert.Equal(0, card.ManaValue);
        Assert.Empty(card.Colors);
    }

    [Fact]
    public async Task OpponentSeesAFaceDownCreatureAndTheControllerSeesTheCard()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Colossus());
        await s.RunUntilTurn();
        var seen = ViewBuilder.Card(s.Game.State, id, P1);
        Assert.True(seen.FaceDown);
        Assert.False(seen.IsHidden);
        Assert.Equal(2, seen.Power);
        Assert.NotEqual("Veiled Colossus", seen.Name);
        Assert.Null(seen.FaceDownCard);
        var own = ViewBuilder.Card(s.Game.State, id, P0);
        Assert.Equal("Veiled Colossus", own.FaceDownCard!.Name);
    }

    [Fact]
    public async Task TurningFaceUpPaysTheMorphCostAndKeepsTheSamePermanent()
    {
        var s = Casting();
        s.Lands(P0, 4);
        var id = s.InHand(P0, Colossus());
        int version = -1;
        var turnedUp = new List<TurnedFaceUp>();
        // Turn 1 casts it with three of the four lands; on turn 3 the four lands untap and the morph cost is paid.
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.Contains(new TurnFaceUp(id)))
            {
                version = s.Card(id).Version;
                return new TurnFaceUp(id);
            }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Game.EventRaised += e => { if (e is TurnedFaceUp t) turnedUp.Add(t); };
        await s.RunUntilTurn(4);
        var card = s.Card(id);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.False(card.TurnedFaceDown);
        Assert.Equal("Veiled Colossus", card.Name);
        Assert.Equal(6, card.Power);
        Assert.Equal(6, card.Toughness);
        Assert.True(card.Has(Keyword.Flying));
        Assert.Equal(version, card.Version); // the same object: no zone change
        Assert.False(card.Tapped);
        Assert.Equal(4, s.Game.State.Battlefield.Count(land => s.Card(land).Tapped)); // the four lands paid {3}{R}
        var turned = Assert.Single(turnedUp);
        Assert.Equal(id, turned.Card);
        Assert.Equal(P0, turned.Player);
    }

    [Fact]
    public async Task CountersStayOnTheTurnedUpPermanent()
    {
        var s = Casting();
        s.Lands(P0, 4);
        var id = s.InHand(P0, Colossus());
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.Contains(new TurnFaceUp(id)))
            {
                s.Card(id).Counters[CounterKind.PlusOnePlusOne] = 1;
                return new TurnFaceUp(id);
            }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        await s.RunUntilTurn(4);
        var card = s.Card(id);
        Assert.False(card.TurnedFaceDown);
        Assert.Equal(1, card.CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(7, card.Power);
        Assert.Equal(7, card.Toughness);
    }

    [Fact]
    public async Task TurningFaceUpIsNotLegalWithoutTheMorphCost()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Colossus());
        bool offered = false;
        // Three lands can cast it face down for {3} but can't pay {3}{R} to turn it face up on turn 3.
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.Contains(new TurnFaceUp(id));
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        await s.RunUntilTurn(4);
        Assert.False(offered);
        Assert.True(s.Card(id).TurnedFaceDown);
    }

    [Fact]
    public void OnlyACardWithMorphCanBeCastFaceDown()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var plain = s.InHand(P0, Creature("Plain Colossus", 6, 6) with { ManaCost = ManaCost.Parse("{5}{R}{R}") });
        var morph = s.InHand(P0, Colossus());
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        var legal = s.Game.GetLegalActions(P0);
        Assert.DoesNotContain(new CastSpell(plain), legal);
        Assert.Contains(new CastSpell(morph), legal);
    }

    [Fact]
    public void FaceDownCastNeedsThreeMana()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var morph = s.InHand(P0, Colossus());
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        Assert.DoesNotContain(new CastSpell(morph), s.Game.GetLegalActions(P0));
    }

    [Fact]
    public async Task AFaceDownPermanentIsRevealedWhenItDies()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var id = s.InHand(P0, Colossus());
        var strike = CardScriptParser.Parse("""{ "spell": { "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }] } }""");
        s.InHand(P0, new CardDefinition { Name = "Ember Strike", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = strike.Spell });
        var revealed = new List<CardId>();
        s.Game.EventRaised += e => { if (e is CardsRevealed r) revealed.AddRange(r.Cards); };
        // Turn 1 casts the face-down creature with all three lands; on turn 3 the strike kills it.
        await s.RunUntilTurn(4);
        var card = s.Card(id);
        Assert.Equal(Zone.Graveyard, card.Zone);
        Assert.False(card.TurnedFaceDown);
        Assert.Equal("Veiled Colossus", card.Name);
        Assert.Contains(id, revealed);
    }
}
