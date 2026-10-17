// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Turn-tracking vocabulary: "divide" quantities, card types in all graveyards, "scry or surveil" triggers and conditions,
/// noncombat damage to opponents, damage bonuses to opponents only, graveyard upkeep triggers and "dealt combat damage".
/// </summary>
public class TurnTrackingVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"turn-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    /// <summary>An instant or sorcery dealing 2 damage with the given "to" subject ("opponents" or "you").</summary>
    private static CardDefinition Burn(string to) => Scripted("Scorch Ping", "Instant", "{R}",
        $$"""{ "spell": { "effects": [{ "damage": 2, "to": "{{to}}" }] } }""");

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().LastOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    /// <summary>Puts a card straight into its owner's graveyard before the game starts.</summary>
    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    [Fact]
    public void DivideQuantityParsesAsPerWithItsDivisor()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "static": { "affects": "self", "pump": [{ "divide": { "graveyard": {} }, "by": 7 }, 0] }, "text": "x" }] }
            """);
        var stat = Assert.IsType<StaticAbility>(Assert.Single(script.Abilities));
        Assert.Equal(QuantityKind.Per, stat.PowerBonus!.Kind);
        Assert.Equal(7, stat.PowerBonus.Value);
    }

    [Fact]
    public async Task DivideQuantityAddsOnePerFullSevenCardsInItsControllersGraveyard()
    {
        var s = new Scenario();
        var bulk = s.Add(P0, Scripted("Grave Bulk", "Creature — Ogre", "{1}",
            """{ "abilities": [{ "static": { "affects": "self", "pump": [{ "divide": { "graveyard": {} }, "by": 7 }, 0] }, "text": "x" }] }""", power: "1", toughness: "1"));
        for (int i = 0; i < 14; i++) PutInGraveyard(s, P0, Arcanum.Cards.GenericCards.Mountain);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(bulk).Power); // base 1, fourteen cards: two more
    }

    [Fact]
    public async Task CardTypesInAllGraveyardsCountsEachTypeOnceAcrossBothGraveyards()
    {
        var s = new Scenario();
        var ogre = s.Add(P0, Scripted("Mirror Ogre", "Creature — Ogre", "{2}",
            """{ "powerFrom": "cardTypesInAllGraveyards", "toughnessFrom": { "sum": ["cardTypesInAllGraveyards", 1] } }""", power: "*", toughness: "*"));
        PutInGraveyard(s, P0, Scripted("Quick Hex", "Instant", "{R}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }"""));
        PutInGraveyard(s, P1, Arcanum.Cards.GenericCards.Mountain);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(ogre).Power); // instant and land
        Assert.Equal(3, s.Card(ogre).Toughness);
    }

    [Fact]
    public async Task ScryOrSurveilTriggersOncePerTurnAndSetsTheFlagForThatTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Lookout Owl", "Creature — Bird", "{1}",
            """{ "abilities": [{ "trigger": "youScryOrSurveil", "oncePerTurn": true, "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.InHand(P0, Scripted("Dig Notes", "Sorcery", "{R}", """{ "spell": { "effects": [{ "surveil": 1 }, { "scry": 1 }] } }""", oracle: "Surveil 1. Scry 1."));
        int life = s.Game.State.GetPlayer(P0).Life;
        bool flagSet = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            flagSet |= s.Game.State.GetPlayer(P0).ScriedOrSurveilledThisTurn; // checked each time P0 has priority, before the turn ends
            return act(view, legal);
        };
        await s.RunUntilTurn();
        Assert.Equal(life + 1, s.Game.State.GetPlayer(P0).Life); // surveil and scry are two events, the trigger is once per turn
        Assert.True(flagSet);
    }

    [Fact]
    public async Task OpponentDealtNoncombatDamageTriggersAndHoldsForThatTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var watcher = s.Add(P0, Scripted("Burn Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "opponentDealtNoncombatDamage", "effects": [{ "gainLife": 1 }], "text": "x" }, { "static": { "affects": "self", "pump": [1, 0], "while": "opponentDealtNoncombatDamageThisTurn" }, "text": "y" }] }""",
            power: "1", toughness: "1"));
        s.InHand(P0, Burn("opponents"));
        int p0 = s.Game.State.GetPlayer(P0).Life, p1 = s.Game.State.GetPlayer(P1).Life;
        int? powerAfterDamage = null;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) =>
        {
            if (powerAfterDamage is null && s.Game.Log.Any(e => e is DamageDealt { IsCombat: false } d && d.TargetPlayer == P1))
                powerAfterDamage = s.Card(watcher).Power;
            return act(view, legal);
        };
        await s.RunUntilTurn();
        Assert.Equal(p1 - 2, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(p0 + 1, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(2, powerAfterDamage); // the condition holds during the turn the damage was dealt
    }

    [Theory]
    [InlineData(true, 3)]  // an opponent: the bonus applies
    [InlineData(false, 2)] // its controller: not an opponent of itself, no bonus
    public async Task NoncombatDamageBonusToOpponentsOnlyAppliesToOpponents(bool toOpponent, int dealtToOpponent)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Ember Ward", "Enchantment", "{1}", """{ "damageBonus": { "sources": {}, "amount": 1, "noncombat": true, "toOpponents": true } }"""));
        s.InHand(P0, Burn(toOpponent ? "opponents" : "you"));
        int p0 = s.Game.State.GetPlayer(P0).Life, p1 = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(p1 - (toOpponent ? dealtToOpponent : 0), s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(p0 - (toOpponent ? 0 : dealtToOpponent), s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task EachUpkeepFromTheGraveyardReturnsTheCardToItsOwnersHand()
    {
        var s = new Scenario();
        var card = PutInGraveyard(s, P0, Scripted("Grave Return", "Creature — Spirit", "{R}",
            """{ "abilities": [{ "trigger": "eachUpkeep", "fromGraveyard": true, "effects": [{ "bounce": "self" }], "text": "x" }] }""", power: "1", toughness: "1"));
        await s.RunUntilTurn(3); // the next upkeep is turn 2's; the stop is at the start of turn 3, after it
        Assert.Equal(Zone.Hand, s.Card(card).Zone);
        Assert.Contains(card, s.Game.State.GetPlayer(P0).Hand);
    }

    [Fact]
    public async Task HexproofWhileTheCreatureHasNotDealtCombatDamage()
    {
        var s = Casting();
        var creature = s.Add(P0, Scripted("Shy Wolf", "Creature — Wolf", "{1}",
            """{ "abilities": [{ "static": { "affects": "self", "keywords": ["Hexproof"], "while": { "not": "dealtCombatDamage" } }, "text": "x" }] }""", power: "1", toughness: "1"));
        Assert.False(s.Card(creature).HasDealtCombatDamage);
        await s.RunUntilTurn();
        Assert.False(s.Card(creature).HasDealtCombatDamage);
        Assert.True(s.Card(creature).Has(Keyword.Hexproof));
    }

    [Fact]
    public async Task HexproofEndsOnceTheCreatureHasDealtCombatDamage()
    {
        var s = new Scenario();
        var creature = s.Add(P0, Scripted("Shy Wolf", "Creature — Wolf", "{1}",
            """{ "abilities": [{ "static": { "affects": "self", "keywords": ["Hexproof"], "while": { "not": "dealtCombatDamage" } }, "text": "x" }] }""", power: "1", toughness: "1"));
        await s.RunUntilTurn();
        Assert.True(s.Card(creature).HasDealtCombatDamage);
        Assert.False(s.Card(creature).Has(Keyword.Hexproof));
    }

    private static CardDefinition Walker(int loyaltyCost) => new()
    {
        Name = "Tester Sage", Types = CardType.Planeswalker, Subtypes = new[] { "Tester" }, Loyalty = 3,
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility { Cost = new AbilityCost(ManaCost.Zero) { Loyalty = loyaltyCost }, Effects = new Effect[] { new GainLife(1, Subject.You) }, Text = "loyalty" },
        },
    };

    [Theory]
    [InlineData(-2, 6)] // two loyalty counters removed: the watcher's "if" holds
    [InlineData(1, 1)]  // a + ability removes none
    public async Task ActivatingALoyaltyAbilityTriggersWithTheCountersRemoved(int loyaltyCost, int lifeGained)
    {
        var s = Casting();
        s.InHand(P0, Walker(loyaltyCost) with { ManaCost = ManaCost.Parse("{R}") });
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Ledger Owl", "Creature — Bird", "{1}",
            """{ "abilities": [{ "trigger": "youActivateLoyaltyAbility", "if": { "atLeast": "triggerAmount", "value": 2 }, "effects": [{ "gainLife": 5 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        int before = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(before + lifeGained, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public void LoyaltyAbilitiesOfTheNamedSubtypeCanBeActivatedAtInstantSpeed()
    {
        var s = new Scenario();
        var walker = s.Add(P0, Walker(1));
        s.Game.State.ActivePlayer = P1;
        s.Game.State.Step = Step.PrecombatMain;
        Assert.DoesNotContain(s.Game.GetLegalActions(P0), a => a is ActivateAbility aa && aa.Source == walker);
        s.Game.State.GetPlayer(P0).InstantLoyaltySubtypes.Add("Tester");
        Assert.Contains(s.Game.GetLegalActions(P0), a => a is ActivateAbility aa && aa.Source == walker);
    }

    [Fact]
    public async Task EachInstanceOfProwessTriggers()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var monk = s.Add(P0, Scripted("Twin Monk", "Creature — Human Monk", "{1}", """{ "extraProwess": 1 }""", oracle: "Prowess, prowess", power: "1", toughness: "1") with { Keywords = new[] { "Prowess" } });
        s.InHand(P0, Burn("opponents"));
        int seen = 0;
        s.Game.EventRaised += e => { if (e is SpellResolved) seen = Math.Max(seen, s.Card(monk).Power); };
        await s.RunUntilTurn();
        Assert.Equal(3, seen);
    }
}
