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
/// Vocabulary for card-wide restrictions (entering permanents, split second, combat lockdown, planeswalkers at 0 loyalty,
/// the legend rule), the "creature blocks" and "attacks alone" triggers, and the cards discarded this turn.
/// </summary>
public class RestrictionVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"rules-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
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

    [Fact]
    public async Task CreatureEnteringDoesNotTriggerWhileTheFlaggedPermanentIsOut()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Quiet Hall", "Enchantment", "{1}", """{ "enteringArtifactsAndCreaturesDontTrigger": true }"""));
        s.InHand(P0, Scripted("Chime Bear", "Creature — Bear", "{1}",
            """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 3 }], "text": "x" }] }""", power: "1", toughness: "1"));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task EnchantmentEnteringStillTriggersWhileAnEnteringCreatureDontTriggerPermanentIsOut()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Quiet Hall", "Enchantment", "{1}", """{ "enteringArtifactsAndCreaturesDontTrigger": true }"""));
        s.InHand(P0, Scripted("Chime Ring", "Enchantment", "{1}",
            """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 3 }], "text": "x" }] }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 3, s.Game.State.GetPlayer(P0).Life);
    }

    [Theory]
    [InlineData(true, false)]  // you control the permanent: your instant on the stack can't be responded to
    [InlineData(false, true)]  // without it, the opponent may respond
    public async Task YourInstantsAndSorceriesHaveSplitSecondWhileYouControlTheFlaggedPermanent(bool withPermanent, bool canRespond)
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Lands(P1, 1);
        if (withPermanent) s.Add(P0, Scripted("Split Ward", "Enchantment", "{1}", """{ "yourInstantsAndSorceriesHaveSplitSecond": true }"""));
        s.InHand(P0, Burn("opponents"));
        s.InHand(P1, Burn("opponents"));
        bool? offered = null;
        s.Defender.Act = (view, legal) =>
        {
            // the first time the opponent has priority with P0's spell on the stack
            if (view.Stack.Count > 0) offered ??= legal.OfType<CastSpell>().Any();
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal<bool?>(canRespond, offered);
    }

    [Theory]
    [InlineData(true, false)]  // you control the permanent: no spell during combat
    [InlineData(false, true)]  // without it, the instant can be cast during combat
    public void NoSpellsOrAbilitiesDuringCombatLeavesManaAbilitiesOnly(bool withPermanent, bool canCast)
    {
        var s = new Scenario();
        if (withPermanent) s.Add(P0, Scripted("Quiet Field", "Enchantment", "{1}", """{ "noSpellsOrAbilitiesDuringCombat": true }"""));
        var mountain = s.Add(P0, Arcanum.Cards.GenericCards.Mountain);
        s.InHand(P0, Burn("opponents"));
        s.Game.State.ActivePlayer = P0;
        s.Game.State.Step = Step.DeclareAttackers;
        var legal = s.Game.GetLegalActions(P0);
        Assert.Contains(legal.OfType<ActivateManaAbility>(), a => a.Source == mountain);
        Assert.Equal(canCast, legal.OfType<CastSpell>().Any());
    }

    [Theory]
    [InlineData(true, Zone.Battlefield)]  // you control the permanent: the planeswalker stays
    [InlineData(false, Zone.Graveyard)]   // without it, the planeswalker with 0 loyalty dies
    public async Task ZeroLoyaltyPlaneswalkerStaysWhileYouControlTheFlaggedPermanent(bool withPermanent, Zone zone)
    {
        var s = new Scenario();
        if (withPermanent) s.Add(P0, Scripted("Walker Guard", "Enchantment", "{1}", """{ "yourPlaneswalkersSurviveZeroLoyalty": true }"""));
        // no loyalty counters: the walker has 0 loyalty
        var walker = s.Add(P0, new CardDefinition { Name = "Tester Sage", Types = CardType.Planeswalker, Subtypes = new[] { "Tester" }, Loyalty = 3 });
        await s.RunUntilTurn();
        Assert.Equal(zone, s.Card(walker).Zone);
    }

    [Fact]
    public async Task NoLegendRuleThisTurnKeepsBothSameNamedLegendsOnTheBattlefield()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var hero = Creature("Tester Hero", 1, 1) with { Supertypes = Supertype.Legendary };
        s.Add(P0, hero);
        var second = s.InHand(P0, hero);
        var sorcery = s.InHand(P0, Scripted("Lift the Rule", "Sorcery", "{R}", """{ "spell": { "effects": [{ "noLegendRuleThisTurn": true }] } }"""));
        int? legends = null;
        s.Attacker.Act = (_, legal) =>
        {
            // checked once the second copy has entered: the legend rule didn't apply, so both are still there
            if (s.Card(second).Zone == Zone.Battlefield) legends ??= s.Game.State.Battlefield.Select(s.Card).Count(c => c.Name == "Tester Hero");
            return legal.OfType<CastSpell>().Where(c => c.Card == sorcery).Cast<PlayerAction>().FirstOrDefault()
                ?? legal.OfType<CastSpell>().Where(c => c.Card == second).Cast<PlayerAction>().FirstOrDefault()
                ?? PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(2, legends);
    }

    [Theory]
    [InlineData(1, 1)] // a 1-power opponent's creature blocks: one damage to that opponent
    [InlineData(2, 0)] // a 2-power one is not within the filter
    public async Task CreatureBlocksTriggersForAnOpponentsCreatureWithinTheFilter(int blockerPower, int damage)
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Scripted("Block Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "creatureBlocks", "filter": { "controller": "opponent", "maxPower": 1 }, "effects": [{ "damage": 1, "to": "triggeredPlayer" }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.Add(P1, Creature("Blocker", blockerPower, 2));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == bear).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(life - damage, s.Game.State.GetPlayer(P1).Life);
    }

    [Theory]
    [InlineData(1, 1)] // one attacker attacks the player alone
    [InlineData(2, 0)] // two attackers attack the same player: neither attacks alone
    public async Task AttacksAloneTriggerNeedsNoOtherCreatureAttackingThatPlayer(int attackers, int lifeGained)
    {
        var s = new Scenario();
        var watch = s.Add(P0, Scripted("Lone Watch", "Creature — Spirit", "{1}",
            """{ "abilities": [{ "trigger": "creatureAttacks", "if": "triggeredAttacksPlayerAlone", "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Creature("Cub", 2, 2));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, possible, defenders) => possible.Where(a => a != watch).Take(attackers).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + lifeGained, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task DiscardedThisTurnCountsTheCardsDiscardedSoFar()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Purge Note", "Sorcery", "{R}", """{ "spell": { "effects": [{ "discard": 2 }, { "gainLife": "discardedThisTurn" }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        await s.RunUntilTurn();
        Assert.Equal(life + 2, s.Game.State.GetPlayer(P0).Life);
    }
}
