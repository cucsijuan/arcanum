// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Modal double-faced cards (rule 712): either face may be played from hand (a land face as a land, a spell face as a spell),
/// the back face's own characteristics apply once it is up, and the card is front face up again outside the battlefield and
/// the stack. The cards are invented.
/// </summary>
public class ModalDoubleFacedTests
{
    private static CardDefinition Import(Dictionary<string, object?> fields, string? script = null)
    {
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = script is null ? null : CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    /// <summary>An instant front ({1}{R}, 2 damage to target creature) and a land back ("{T}: Add {R}.").</summary>
    private static CardDefinition SpellAndLand()
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = "mdfc-0", ["name"] = "Test Ember Charm // Test Ember Vale", ["layout"] = "modal_dfc", ["mana_cost"] = "{1}{R} // ",
            ["type_line"] = "Instant // Land",
            ["card_faces"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Test Ember Charm", ["mana_cost"] = "{1}{R}", ["type_line"] = "Instant",
                    ["oracle_text"] = "Test Ember Charm deals 2 damage to target creature." },
                new Dictionary<string, object?> { ["name"] = "Test Ember Vale", ["mana_cost"] = "", ["type_line"] = "Land",
                    ["oracle_text"] = "{T}: Add {R}." },
            },
        };
        return Import(fields, """
            { "spell": { "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }], "text": "invented" } }
            """);
    }

    /// <summary>A {1}{G} 2/2 front and a {3}{G}{G} 5/5 back, both creatures.</summary>
    private static CardDefinition CreatureAndCreature()
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = "mdfc-1", ["name"] = "Test Wilder Cub // Test Wilder Brute", ["layout"] = "modal_dfc", ["mana_cost"] = "{1}{G} // ",
            ["type_line"] = "Creature — Beast // Creature — Beast",
            ["card_faces"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Test Wilder Cub", ["mana_cost"] = "{1}{G}", ["type_line"] = "Creature — Beast",
                    ["oracle_text"] = "", ["power"] = "2", ["toughness"] = "2" },
                new Dictionary<string, object?> { ["name"] = "Test Wilder Brute", ["mana_cost"] = "{3}{G}{G}", ["type_line"] = "Creature — Beast",
                    ["oracle_text"] = "", ["power"] = "5", ["toughness"] = "5" },
            },
        };
        return Import(fields);
    }

    /// <summary>A {1}{R} 2/2 front and a {1}{R} instant back: 3 damage to any target.</summary>
    private static CardDefinition CreatureAndInstant()
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = "mdfc-2", ["name"] = "Test Ember Cub // Test Ember Bolt", ["layout"] = "modal_dfc", ["mana_cost"] = "{1}{R} // ",
            ["type_line"] = "Creature — Beast // Instant",
            ["card_faces"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Test Ember Cub", ["mana_cost"] = "{1}{R}", ["type_line"] = "Creature — Beast",
                    ["oracle_text"] = "", ["power"] = "2", ["toughness"] = "2" },
                new Dictionary<string, object?> { ["name"] = "Test Ember Bolt", ["mana_cost"] = "{1}{R}", ["type_line"] = "Instant",
                    ["oracle_text"] = "Test Ember Bolt deals 3 damage to any target." },
            },
        };
        return Import(fields, """
            { "back": { "spell": { "targets": ["any"], "effects": [{ "damage": 3, "to": "target" }], "text": "invented" } } }
            """);
    }

    /// <summary>An invented instant that returns target creature to its owner's hand.</summary>
    private static CardDefinition Unsummon()
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = "mdfc-3", ["name"] = "Test Unsummon", ["layout"] = "normal", ["mana_cost"] = "{R}",
            ["type_line"] = "Instant", ["oracle_text"] = "Return target creature to its owner's hand.",
        };
        return Import(fields, """
            { "spell": { "targets": ["creature"], "effects": [{ "bounce": "target" }], "text": "invented" } }
            """);
    }

    private static void AimAtOpponent(Scenario s) => s.Attacker.Targets = (_, req) =>
    {
        var chosen = new List<Target>();
        for (int i = 0; i < req.Specs.Count; i++)
            chosen.Add(req.LegalAt(i).FirstOrDefault(t => req.IsAllowed(i, t, chosen) && (t.Player == P1 || t.Card is { } c && s.Card(c).Controller == P1)));
        return chosen;
    };

    [Fact]
    public void ASpellAndLandCardInHandOffersItsSpellFaceAndItsBackFaceAsALandDropWhileALandDropIsLeft()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P1, Creature("Bear", 2, 2));
        var card = s.InHand(P0, SpellAndLand());
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        var legal = s.Game.GetLegalActions(P0);
        Assert.Contains(new CastSpell(card), legal);
        Assert.Contains(new PlayLand(card, Back: true), legal);
    }

    [Fact]
    public void WithNoLandDropLeftTheBackFaceIsNotOfferedButTheSpellFaceStill()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P1, Creature("Bear", 2, 2));
        var card = s.InHand(P0, SpellAndLand());
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.PrecombatMain;
        s.Game.State.GetPlayer(P0).LandsPlayedThisTurn = 1;
        var legal = s.Game.GetLegalActions(P0);
        Assert.DoesNotContain(new PlayLand(card, Back: true), legal);
        Assert.Contains(new CastSpell(card), legal);
    }

    [Fact]
    public async Task PlayingTheBackFaceAsALandPutsItOntoTheBattlefieldWithItsLandFaceUp()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var card = s.InHand(P0, SpellAndLand());
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Where(p => p.Card == card && p.Back).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(card).Zone);
        Assert.True(s.Card(card).Transformed);
        Assert.Equal("Test Ember Vale", s.Card(card).Name);
        Assert.True(s.Card(card).Definition.Is(CardType.Land));
    }

    [Fact]
    public void InHandACreatureAndCreatureCardHasItsFrontFacesNameAndManaValue()
    {
        var s = new Scenario();
        var card = s.InHand(P0, CreatureAndCreature());
        Assert.Equal("Test Wilder Cub", s.Card(card).Name);
        Assert.Equal(2, s.Card(card).ManaValue);
    }

    [Fact]
    public async Task CastingTheBackFaceOfACreatureAndCreatureCardPutsItsBackFaceOntoTheBattlefield()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 3);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        var card = s.InHand(P0, CreatureAndCreature());
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Where(c => c.Card == card && c.Back).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(card).Zone);
        Assert.True(s.Card(card).Transformed);
        Assert.Equal(5, s.Card(card).Power);
        Assert.Equal(5, s.Card(card).ManaValue);
    }

    [Fact]
    public async Task CastingTheBackInstantFaceResolvesItAndTheCardGoesToTheGraveyardFrontFaceUp()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        AimAtOpponent(s);
        var card = s.InHand(P0, CreatureAndInstant());
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Where(c => c.Card == card && c.Back).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
        Assert.False(s.Card(card).Transformed);
        Assert.Equal("Test Ember Cub", s.Card(card).Name);
    }

    [Fact]
    public async Task ABackFaceCardThatIsBouncedReturnsToHandWithItsFrontFaceUp()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 3);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        s.Lands(P1, 1);
        var card = s.InHand(P0, CreatureAndCreature());
        var bounce = s.InHand(P1, Unsummon());
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Where(c => c.Card == card && c.Back).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().Where(c => c.Card == bounce).Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        // Read the face at the moment it arrives in hand: the card is discarded at cleanup, since the hand is over its maximum.
        bool? transformedInHand = null;
        string? nameInHand = null;
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved m && m.Card == card && m.From == Zone.Battlefield && m.To == Zone.Hand)
            {
                transformedInHand = s.Card(card).Transformed;
                nameInHand = s.Card(card).Name;
            }
        };
        await s.RunUntilTurn();
        Assert.False(transformedInHand);
        Assert.Equal("Test Wilder Cub", nameInHand);
    }
}
