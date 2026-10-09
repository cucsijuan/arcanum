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
/// "This" in a resolving ability is only the object it came from (rule 400.7), last known information for an intervening
/// "if" about a source that left (603.4, 608.2h), and "up to N targets" for spells, activated and loyalty abilities.
/// </summary>
public class SourceObjectAndUpToTargetsTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"src-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1],
            ["keywords"] = oracle == "" ? Array.Empty<string>() : new[] { oracle },
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private delegate PlayerAction? Step(IReadOnlyList<PlayerAction> legal);

    private static void Script(TestController player, params Step[] steps)
    {
        var q = new Queue<Step>(steps);
        player.Act = (_, legal) =>
        {
            if (q.Count > 0 && q.Peek()(legal) is { } a) { q.Dequeue(); return a; }
            return PassPriority.Instance;
        };
        player.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Step Cast(Scenario s, string name) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);
    private static Step Activate(Scenario s, string name) => legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name);

    /// <summary>Only once the stack holds something (to respond to it).</summary>
    private static Step WhenStack(Scenario s, int count, Step then) => legal => s.Game.State.Stack.Count == count ? then(legal) : null;

    private static void Mountains(Scenario s, PlayerId who, int n) { for (int i = 0; i < n; i++) s.Add(who, GenericCards.Mountain); }

    private static readonly string Flicker = """{ "spell": { "targets": ["creature:you"], "effects": [{ "blink": "target" }] } }""";

    [Fact]
    public async Task AnActivatedAbilityDoesNothingToItsSourceIfItWasFlickeredInResponse()
    {
        var grower = Make("Patient Grower", "Creature — Plant", """{ "abilities": [{ "cost": "{1}", "effects": [{ "counters": 1, "what": "self" }, { "pump": [2, 2], "what": "self" }] }] }""", "{1}", "1/1");
        var flicker = Make("Quick Flicker", "Instant", Flicker, "{1}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        var id = s.Add(P0, grower);
        s.InHand(P0, flicker);
        Script(s.Attacker, Activate(s, "Patient Grower"), WhenStack(s, 1, Cast(s, "Quick Flicker")));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == id && m.To == Zone.Exile);
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
        Assert.Equal(0, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne)); // a new object: the ability can't find it
    }

    [Fact]
    public async Task AnEntersTriggerOfAFlickeredCreatureOnlyAffectsTheObjectItCameFrom()
    {
        var hatchling = Make("Eager Hatchling", "Creature — Bird", """{ "abilities": [{ "trigger": "enters", "effects": [{ "counters": 1, "what": "self" }] }] }""", "{1}", "1/1");
        var flicker = Make("Quick Flicker", "Instant", Flicker, "{1}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        var id = s.InHand(P0, hatchling);
        s.InHand(P0, flicker);
        // The first enters trigger is on the stack: flicker the Hatchling. Its new enters trigger puts one counter on it; the old one does nothing.
        Script(s.Attacker, Cast(s, "Eager Hatchling"), legal => s.Game.State.Stack.Count == 1 && s.Card(id).Zone == Zone.Battlefield ? Cast(s, "Quick Flicker")(legal) : null);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
        Assert.Equal(1, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ASecondBlinkSelfTransformAbilityFindsANewObjectAndDoesNothing()
    {
        var def = Make("Cocoon Beast", "Creature — Beast", """{ "abilities": [{ "cost": "{1}", "effects": [{ "blink": "self", "transformed": true }] }] }""", "{1}", "1/1");
        def = def with { BackFace = Scenario.Creature("Winged Beast", 4, 4) };
        var s = new Scenario();
        Mountains(s, P0, 2);
        var id = s.Add(P0, def);
        Script(s.Attacker, Activate(s, "Cocoon Beast"), WhenStack(s, 1, Activate(s, "Cocoon Beast")));
        await s.RunUntilTurn();
        Assert.True(s.Card(id).Transformed);
        Assert.Single(s.Game.Log.OfType<CardMoved>().Where(m => m.Card == id && m.To == Zone.Exile));
    }

    [Fact]
    public async Task AnAbilityThatMovesItsOwnSourceFollowsItToTheNewObject()
    {
        // "Exile this, then return it to the battlefield. Put a +1/+1 counter on it."
        var def = Make("Phase Hopper", "Creature — Spirit", """{ "abilities": [{ "cost": "{1}", "effects": [{ "blink": "self" }, { "counters": 1, "what": "self" }] }] }""", "{1}", "1/1");
        var s = new Scenario();
        Mountains(s, P0, 1);
        var id = s.Add(P0, def);
        Script(s.Attacker, Activate(s, "Phase Hopper"));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == id && m.To == Zone.Exile);
        Assert.Equal(1, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne));
    }

    private const string Berserker = """
        { "abilities": [
          { "trigger": "combatDamageToPlayer", "if": { "not": "renowned" }, "effects": [{ "counters": 1, "what": "self" }, { "becomeRenowned": "self" }] },
          { "trigger": "opponentCastsSpell", "filter": { "not": ["creature"] }, "if": "renowned", "effects": [{ "damage": 2, "to": "triggeredPlayer" }] } ] }
        """;

    [Theory]
    [InlineData(true, 18)]
    [InlineData(false, 20)]
    public async Task ARenownedConditionUsesLastKnownInformationWhenTheSourceLeft(bool renowned, int defenderLife)
    {
        var berserker = Make("Scab-Clan Berserker", "Creature — Human Berserker", Berserker, "{1}{R}{R}", "2/2", "Haste");
        var study = Make("Quiet Study", "Instant", """{ "spell": { "effects": [{ "gainLife": 0 }] } }""", "{1}");
        var recall = Make("Recall", "Instant", """{ "spell": { "targets": ["creature:you"], "effects": [{ "bounce": "target" }] } }""", "{1}");
        var s = new Scenario();
        Mountains(s, P0, 1);
        Mountains(s, P1, 1);
        var id = s.Add(P0, berserker);
        s.Card(id).Renowned = renowned;
        s.InHand(P0, recall);
        s.InHand(P1, study);
        // The opponent casts a noncreature spell; with the trigger on the stack, the Berserker's controller returns it to hand.
        Script(s.Defender, WhenStack(s, 0, Cast(s, "Quiet Study")));
        Script(s.Attacker, WhenStack(s, 2, Cast(s, "Recall")));
        await s.RunUntilTurn();
        // Not renowned: it doesn't trigger at all (603.4), so there is nothing to respond to.
        Assert.Equal(renowned, s.Game.Log.Any(e => e is CardMoved m && m.Card == id && m.To == Zone.Hand));
        Assert.Equal(defenderLife, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ARenownTriggerDoesNothingToTheNewObjectIfTheCreatureWasFlickered()
    {
        var renown = Make("Brash Squire", "Creature — Human", """
            { "abilities": [{ "cost": "{1}", "effects": [{ "if": { "not": "renowned" }, "then": [{ "counters": 1, "what": "self" }, { "becomeRenowned": "self" }] }] }] }
            """, "{1}", "1/1");
        var flicker = Make("Quick Flicker", "Instant", Flicker, "{1}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        var id = s.Add(P0, renown);
        s.InHand(P0, flicker);
        Script(s.Attacker, Activate(s, "Brash Squire"), WhenStack(s, 1, Cast(s, "Quick Flicker")));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
        Assert.False(s.Card(id).Renowned);
        Assert.Equal(0, s.Card(id).CounterCount(CounterKind.PlusOnePlusOne));
    }

    private static readonly string TapUpToTwo = """{ "spell": { "targets": [{ "kind": "creature", "upTo": 2 }], "effects": [{ "tap": "eachTarget" }] } }""";

    [Fact]
    public async Task ASpellWithUpToTwoTargetsCanBeCastWithNoneAndWithOne()
    {
        var spell = Make("Gentle Halt", "Instant", TapUpToTwo, "{1}");
        // No creature at all: still castable, with no targets (rule 601.2c).
        var s = new Scenario();
        Mountains(s, P0, 1);
        s.InHand(P0, spell);
        Script(s.Attacker, Cast(s, "Gentle Halt"));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved m && s.Card(m.Card).Name == "Gentle Halt" && m.To == Zone.Graveyard);

        // Three creatures: the caster picks only one of the two.
        var s2 = new Scenario();
        Mountains(s2, P0, 1);
        var a = s2.Add(P1, Scenario.Creature("Ox A", 1, 1));
        var b = s2.Add(P1, Scenario.Creature("Ox B", 1, 1));
        s2.InHand(P0, spell);
        TargetRequest? seen = null;
        s2.Attacker.Targets = (_, req) => { seen = req; return new[] { Target.Of(b), Target.None }; };
        Script(s2.Attacker, Cast(s2, "Gentle Halt"));
        await s2.RunUntilTurn();
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Specs.Count);
        Assert.All(seen.Specs, spec => Assert.True(spec.Optional));
        // (checked in the log: the defender's creatures untap as its turn begins)
        Assert.Contains(s2.Game.Log, e => e is PermanentTapped t && t.Card == b);
        Assert.DoesNotContain(s2.Game.Log, e => e is PermanentTapped t && t.Card == a);
    }

    [Fact]
    public async Task AnActivatedAbilityWithUpToXTargetsAnnouncesXFirst()
    {
        var rod = Make("Stilling Rod", "Artifact", """{ "abilities": [{ "cost": "{X}", "targets": [{ "kind": "creature", "upTo": "X" }], "effects": [{ "tap": "eachTarget" }] }] }""", "{1}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        s.Add(P0, rod);
        var a = s.Add(P1, Scenario.Creature("Ox A", 1, 1));
        var b = s.Add(P1, Scenario.Creature("Ox B", 1, 1));
        var c = s.Add(P1, Scenario.Creature("Ox C", 1, 1));
        TargetRequest? seen = null;
        s.Attacker.Targets = (_, req) =>
        {
            seen ??= req;
            var chosen = new List<Target>();
            for (int i = 0; i < req.Specs.Count; i++)
                chosen.Add(req.LegalAt(i).Where(t => !t.IsNone).FirstOrDefault(t => req.IsAllowed(i, t, chosen)));
            return chosen;
        };
        Script(s.Attacker, Activate(s, "Stilling Rod"));
        await s.RunUntilTurn();
        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Specs.Count); // X = 2 (all it could pay)
        Assert.Equal(2, new[] { a, b, c }.Count(id => s.Game.Log.Any(e => e is PermanentTapped t && t.Card == id)));
    }

    [Fact]
    public async Task ALoyaltyAbilityWithUpToOneTargetCanBeActivatedWithNoCreatures()
    {
        var walker = Make("Test Walker", "Legendary Planeswalker — Test", """
            { "abilities": [{ "cost": "+1", "targets": [{ "kind": "creature", "upTo": 1 }], "effects": [{ "pump": [-2, 0], "what": "target" }] }] }
            """, "{3}");
        var s = new Scenario();
        var w = s.Add(P0, walker);
        s.Card(w).Counters[CounterKind.Loyalty] = 3;
        Script(s.Attacker, Activate(s, "Test Walker"));
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(w).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task ATriggerWithUpToOneTargetGoesOnTheStackWithNoLegalTarget()
    {
        var herald = Make("Quiet Herald", "Creature — Human", """
            { "abilities": [{ "trigger": "enters", "targets": [{ "kind": "creature", "controller": "opponent", "upTo": 1 }], "effects": [{ "tap": "eachTarget" }, { "gainLife": 1 }] }] }
            """, "{1}", "1/1");
        var s = new Scenario();
        Mountains(s, P0, 1);
        s.InHand(P0, herald);
        Script(s.Attacker, Cast(s, "Quiet Herald"));
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }
}
