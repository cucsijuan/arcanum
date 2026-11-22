// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Sneak: an alternative cost paid during the declare blockers step by returning an unblocked attacker, the permanent entering
/// tapped and attacking, "if its sneak cost was paid", and casting creature cards from the graveyard using sneak. The cards are invented.
/// </summary>
public class SneakTests
{
    private static int _next;

    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle, string? power = null, string? toughness = null, params string[] keywords)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"sneak-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
        if (keywords.Length > 0) fields["keywords"] = keywords;
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    private static CardDefinition Prowler() => Scripted("Test Prowler", "Creature — Ninja", "{3}{R}", """{ "abilities": [] }""",
        "Sneak {R} (You may cast this spell for {R} if you also return an unblocked attacker you control to hand during the declare blockers step. It enters tapped and attacking.)",
        "3", "3", "Sneak");

    /// <summary>Player 0 attacks with everything; during the declare blockers step it casts <paramref name="card"/> as soon as it can.</summary>
    private static void SneakIn(Scenario s, CardId card, Action<Step>? offered = null)
    {
        bool cast = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } spell)
            {
                offered?.Invoke(s.Game.State.Step);
                if (s.Game.State.Step == Step.DeclareBlockers) { cast = true; return spell; }
            }
            return PassPriority.Instance;
        };
    }

    [Fact]
    public async Task ASneakedCreatureEntersTappedAndAttackingWhatTheReturnedCreatureAttacked()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var prowler = s.InHand(P0, Prowler());
        bool attackingWhenChecked = false, tappedWhenChecked = false, bearInHand = false;
        s.Game.EventRaised += e =>
        {
            if (e is Events.SpellResolved r && r.Card == prowler && s.Game.State.Combat is { } combat)
            {
                attackingWhenChecked = combat.FindAttack(prowler) is { Defender.Value: 1 };
                tappedWhenChecked = s.Card(prowler).Tapped;
                bearInHand = s.Game.State.GetPlayer(P0).Hand.Contains(bear); // the returned attacker (later discarded down to hand size)
            }
        };
        SneakIn(s, prowler);
        await s.RunUntilTurn();
        Assert.True(attackingWhenChecked);
        Assert.True(tappedWhenChecked);
        Assert.True(bearInHand);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);     // only the sneaked creature dealt combat damage
        Assert.True(s.Card(prowler).Sneaked);
    }

    [Fact]
    public async Task SneakIsOfferedOnlyDuringTheDeclareBlockersStepWithAnUnblockedAttacker()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P1, Creature("Wall", 0, 4));
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        var prowler = s.InHand(P0, Prowler());
        var steps = new List<Step>();
        SneakIn(s, prowler, steps.Add);
        await s.RunUntilTurn();
        Assert.DoesNotContain(Step.DeclareBlockers, steps); // its only attacker was blocked
        Assert.Contains(prowler, s.Game.State.GetPlayer(P0).Hand);
    }

    [Fact]
    public async Task ASorceryWithSneakIsCastDuringTheDeclareBlockersStepAndSeesItsSneakCostPaid()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear", 2, 2));
        var lesson = s.InHand(P0, Scripted("Test Lesson", "Sorcery", "{4}{R}",
            """{ "spell": { "effects": [{ "gainLife": 1 }, { "if": "sneaked", "then": [{ "gainLife": 4 }] }], "text": "invented" } }""",
            "Sneak {R}\nYou gain 1 life. If this spell's sneak cost was paid, you gain 4 more life.", keywords: "Sneak"));
        SneakIn(s, lesson);
        await s.RunUntilTurn();
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life); // the bear went back to the hand before damage
    }

    [Fact]
    public async Task AnEntersTriggerSeesTheSneakCostPaid()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Creature("Bear", 2, 2));
        var adept = s.InHand(P0, Scripted("Test Adept", "Creature — Ninja", "{2}{R}",
            """{ "abilities": [{ "trigger": "enters", "if": "sneaked", "effects": [{ "gainLife": 3 }], "text": "invented" }] }""",
            "Sneak {1}{R}\nWhen this creature enters, if its sneak cost was paid, you gain 3 life.", "1", "1", "Sneak"));
        SneakIn(s, adept);
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task CastForItsManaCostTheSneakConditionIsFalse()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var adept = s.InHand(P0, Scripted("Test Adept", "Creature — Ninja", "{2}{R}",
            """{ "abilities": [{ "trigger": "enters", "if": "sneaked", "effects": [{ "gainLife": 3 }], "text": "invented" }] }""",
            "Sneak {1}{R}\nWhen this creature enters, if its sneak cost was paid, you gain 3 life.", "1", "1", "Sneak"));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == adept) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(adept).Zone);
        Assert.False(s.Card(adept).Sneaked);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AGraveyardSneakPermissionCastsACreatureCardFromTheGraveyardForTheGrantedCost()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P0, Scripted("Test Dojo", "Enchantment", "{B}", """{ "graveyardSneak": { "filter": { "types": ["creature"] }, "cost": "{1}{R}" } }""",
            "Creature cards in your graveyard have sneak {1}{R}.\nYou may cast creature spells from your graveyard using their sneak abilities."));
        var ox = s.InHand(P0, Creature("Ox", 4, 4));
        var player = s.Game.State.GetPlayer(P0);
        player.Hand.Remove(ox);
        player.Graveyard.Add(ox);
        s.Card(ox).Zone = Zone.Graveyard;
        var steps = new List<Step>();
        SneakIn(s, ox, steps.Add);
        await s.RunUntilTurn();
        Assert.Equal(new[] { Step.DeclareBlockers }, steps.Distinct()); // castable from the graveyard only with sneak
        Assert.Equal(Zone.Battlefield, s.Card(ox).Zone);
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life);
    }
}
