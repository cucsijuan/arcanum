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

/// <summary>Cards written as script text (invented ones): continuous copies, state triggers, counter bans, per-player targets and more.</summary>
public class ScriptedRulesTests
{
    private static int _next;

    /// <summary>An invented card imported from card-source JSON and scripted.</summary>
    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "")
    {
        var power = pt?.Split('/')[0];
        var toughness = pt?.Split('/')[1];
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"inv-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = power, ["toughness"] = toughness,
            ["keywords"] = oracle.StartsWith("Enchant") || oracle == "" ? Array.Empty<string>() : new[] { oracle },
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private delegate PlayerAction? Step(IReadOnlyList<PlayerAction> legal);

    private static void Script(Scenario s, params Step[] steps)
    {
        var q = new Queue<Step>(steps);
        s.Attacker.Act = (_, legal) =>
        {
            if (q.Count > 0 && q.Peek()(legal) is { } a) { q.Dequeue(); return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Step Cast(Scenario s, string name) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);
    private static Step Activate(Scenario s, string name) => legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name);

    private static void Aim(Scenario s, params Func<Target, bool>[] picks) => s.Attacker.Targets = (_, req) =>
    {
        var chosen = new List<Target>();
        for (int i = 0; i < req.Specs.Count; i++)
        {
            var pred = i < picks.Length ? picks[i] : _ => true;
            chosen.Add(req.LegalAt(i).FirstOrDefault(t => req.IsAllowed(i, t, chosen) && (t.IsNone || pred(t))));
        }
        return chosen;
    };

    private static Func<Target, bool> Named(Scenario s, string name) => t => t.Card is { } c && s.Card(c).Name == name;

    private static void Mountains(Scenario s, int n) { for (int i = 0; i < n; i++) s.Add(P0, GenericCards.Mountain); }

    [Fact]
    public async Task AnEffectLetsADefenderAttackAndAConditionTracksDamageDealt()
    {
        var wall = Make("Brave Wall", "Creature — Wall", """{ "abilities": [{ "static": { "affects": "self", "keywords": ["Can attack despite defender"] } }] }""", "{1}", "2/2", "Defender");
        var plain = Make("Plain Wall", "Creature — Wall", "{}", "{1}", "2/2", "Defender");
        var shy = Make("Shy Drake", "Creature — Drake", """{ "abilities": [{ "static": { "affects": "self", "while": { "not": "dealtDamage" }, "keywords": ["Hexproof"] } }] }""", "{1}", "2/2");

        var s = new Scenario();
        var braveId = s.Add(P0, wall);
        var plainId = s.Add(P0, plain);
        var drake = s.Add(P0, shy);
        Script(s);
        s.Attacker.Attack = (_, a, d) => a.Select(x => new AttackDeclaration(x, d[0])).ToList();
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is AttackerDeclared a && a.Attacker == braveId);
        Assert.DoesNotContain(s.Game.Log, e => e is AttackerDeclared a && a.Attacker == plainId);
        Assert.False(s.Card(drake).Has(Keyword.Hexproof)); // it attacked unblocked and dealt damage
        Assert.True(s.Card(plainId).Has(Keyword.Defender));
    }

    [Fact]
    public async Task QuantitiesTakeTheGreatestManaValueAndNegativeLifeGained()
    {
        var big = Make("Big Gizmo", "Artifact", "{}", "{4}");
        var small = Make("Small Gizmo", "Artifact", "{}", "{2}");
        var study = Make("Gizmo Study", "Sorcery", """{ "spell": { "effects": [{ "draw": { "greatestManaValue": { "types": ["artifact"] } } }] } }""", "{1}");
        var s = new Scenario();
        Mountains(s, 1);
        s.Add(P0, big); s.Add(P0, small);
        s.InHand(P0, study);
        Script(s, Cast(s, "Gizmo Study"));
        await s.RunUntilTurn();
        Assert.Equal(4, s.Game.Log.OfType<CardDrawn>().Count(d => d.Player == P0) - 7);

        var heal = Make("Mend And Sap", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "gainLife": 2 }, { "pump": ["-lifeGained", "-lifeGained"], "what": "target" }] } }""", "{1}");
        var s2 = new Scenario();
        Mountains(s2, 1);
        var victim = s2.Add(P1, Scenario.Creature("Victim", 2, 2));
        s2.InHand(P0, heal);
        Script(s2, Cast(s2, "Mend And Sap"));
        await s2.RunUntilTurn();
        Assert.Contains(s2.Game.Log, e => e is CardMoved m && m.Card == victim && m.To == Zone.Graveyard);
    }

    [Fact]
    public async Task ACounterBanLastsWhileItsSourceStaysAndPlayersLoseTheirCounters()
    {
        var monk = Make("Quiet Monk", "Creature — Human", """
            { "abilities": [{ "trigger": "enters", "modes": [
                { "text": "creature", "targets": ["creature"], "effects": [{ "removeAllCounters": "target" }, { "cantHaveCounters": "target" }] },
                { "text": "player", "targets": ["opponent"], "effects": [{ "removeAllCounters": "target" }, { "cantHaveCounters": "target" }] }], "chooseCount": 1 }] }
            """, "{1}", "1/1");
        for (int mode = 0; mode < 2; mode++)
        {
            var s = new Scenario();
            Mountains(s, 1);
            var target = s.Add(P1, Scenario.Creature("Grown", 2, 2));
            s.Card(target).Counters[CounterKind.PlusOnePlusOne] = 2;
            s.Game.State.GetPlayer(P1).Poison = 3;
            s.InHand(P0, monk);
            int m = mode;
            s.Attacker.Modes = (_, req) => new[] { req.Possible[m] };
            Script(s, Cast(s, "Quiet Monk"));
            await s.RunUntilTurn();
            Assert.Single(s.Game.State.CounterBans);
            Assert.Equal(mode == 0 ? 0 : 2, s.Card(target).CounterCount(CounterKind.PlusOnePlusOne));
            Assert.Equal(mode == 0 ? 3 : 0, s.Game.State.GetPlayer(P1).Poison);
        }
    }

    [Fact]
    public async Task AnAuraCopiesTheCreatureChosenAsItEnters()
    {
        var mimic = Make("Mimic Veil", "Enchantment — Aura", """{ "chooseOnEnter": "creature", "abilities": [{ "static": { "affects": "enchanted", "copyOfChosen": true } }] }""", "{1}", null, "Enchant creature");
        var s = new Scenario();
        Mountains(s, 1);
        var plain = s.Add(P0, Scenario.Creature("Plain One", 1, 1));
        var model = s.Add(P0, Make("Model Flyer", "Creature — Bird", "{}", "{3}", "3/3", "Flying"));
        s.InHand(P0, mimic);
        Script(s, Cast(s, "Mimic Veil"));
        Aim(s, Named(s, "Plain One"));
        s.Attacker.Choose = (_, req) => req.Options.Where(o => o.Name == "Model Flyer").Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal("Model Flyer", s.Card(plain).Name);
        Assert.Equal(3, s.Card(plain).Power);
        Assert.True(s.Card(plain).Has(Keyword.Flying));
        Assert.Equal("Model Flyer", s.Card(model).Name);
    }

    [Fact]
    public async Task ACounterIsPutOnAPermanentAsItEntersAndAStateTriggerSacrificesIt()
    {
        var lich = Make("Bound Lich", "Creature — Zombie", """
            { "entersCounterOn": { "filter": { "types": ["artifact"] }, "kind": "phylactery" },
              "abilities": [{ "trigger": "state", "when": { "not": { "control": { "hasCounterKind": "phylactery" } } }, "effects": [{ "sacrificeIt": "self" }] }] }
            """, "{1}", "5/5");
        var s = new Scenario();
        Mountains(s, 1);
        var jar = s.Add(P0, Make("Soul Jar", "Artifact", "{}", "{1}"));
        var l = s.InHand(P0, lich);
        Script(s, Cast(s, "Bound Lich"));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(jar).CounterCount(CounterKind.Phylactery));
        Assert.Equal(Zone.Battlefield, s.Card(l).Zone);

        var s2 = new Scenario();
        Mountains(s2, 1);
        var l2 = s2.InHand(P0, lich);
        Script(s2, Cast(s2, "Bound Lich"));
        await s2.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s2.Card(l2).Zone); // no artifact: nothing carries a counter, so it triggers at once
    }

    [Fact]
    public async Task CreaturesThatWouldEnterWithoutBeingCastAreExiled()
    {
        var guard = Make("Door Warden", "Creature — Human", """{ "abilities": [{ "cost": "sacrifice", "effects": [{ "exileUncastEntering": { "types": ["creature"], "token": false } }] }] }""", "{1}", "1/1");
        var raise = Make("Call Forth", "Sorcery", """{ "spell": { "effects": [{ "lookAtTop": 1, "take": 1, "filter": { "types": ["creature"] }, "to": "battlefield" }] } }""", "{1}");
        var s = new Scenario();
        Mountains(s, 1);
        s.Add(P0, guard);
        s.InHand(P0, raise);
        var target = s.InHand(P0, Scenario.Creature("Sleeper", 2, 2));
        bool restacked = false;
        Script(s, Activate(s, "Door Warden"), Cast(s, "Call Forth"));
        var inner = s.Attacker.Act;
        s.Attacker.Act = (v, l) => { if (!restacked) { restacked = true; s.Restack(P0, target); } return inner(v, l); };
        s.Attacker.Choose = (_, req) => req.Options.Take(1).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(target).Zone);
    }

    [Fact]
    public async Task ATriggerChoosesATargetForEachPlayerThenThoseSacrificeAndRevealTheTopCard()
    {
        var reaper = Make("Level Reaper", "Creature — Dragon", """
            { "abilities": [{ "trigger": "attacks", "targets": [{ "kind": "permanent", "perPlayer": true }],
              "effects": [{ "sacrificeIt": "eachTarget" }, { "revealTopPut": { "types": ["land", "creature"] }, "who": "sacrificers" }] }] }
            """, "{1}", "2/2");
        var s = new Scenario();
        s.Add(P0, reaper);
        var mine = s.Add(P0, Scenario.Creature("Mine", 1, 1));
        var theirs = s.Add(P1, Scenario.Creature("Theirs", 1, 1));
        Script(s);
        Aim(s, Named(s, "Mine"), Named(s, "Theirs"));
        s.Attacker.Attack = (_, a, d) => a.Where(x => s.Card(x).Name == "Level Reaper").Select(x => new AttackDeclaration(x, d[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(mine).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(theirs).Zone);
        Assert.Equal(1, s.Game.State.PermanentsControlledBy(P0).Count(c => c.Name == "Forest"));
        Assert.Equal(1, s.Game.State.PermanentsControlledBy(P1).Count(c => c.Name == "Forest"));
    }

    [Fact]
    public async Task AReflexiveAbilityCanBeAboutTheCardPutOntoTheBattlefieldThisWay()
    {
        var call = Make("Summon Strike", "Sorcery", """
            { "spell": { "effects": [
                { "lookAtTop": 3, "take": 1, "filter": { "types": ["creature"] }, "to": "battlefield" },
                { "whenYouDo": { "if": { "atLeast": "foundThisWay", "value": 1 }, "about": "found", "targets": [{ "kind": "creature", "controller": "opponent" }], "effects": [{ "bite": "triggered", "to": "target" }] } }] } }
            """, "{1}");
        var s = new Scenario();
        Mountains(s, 1);
        s.InHand(P0, call);
        var brute = s.InHand(P0, Scenario.Creature("Brute", 3, 3));
        var foe = s.Add(P1, Scenario.Creature("Foe", 2, 6));
        bool restacked = false;
        Script(s, Cast(s, "Summon Strike"));
        var inner = s.Attacker.Act;
        s.Attacker.Act = (v, l) => { if (!restacked) { restacked = true; s.Restack(P0, brute); } return inner(v, l); };
        s.Attacker.Choose = (_, req) => req.Options.Take(1).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(brute).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt d && d.Source == brute && d.TargetCard == foe && d.Amount == 3);
    }

    [Fact]
    public async Task ASacrificeCostRemembersWhatItSacrificedForTheSpell()
    {
        var thud = Make("Heavy Toll", "Sorcery", """{ "additionalCost": { "sacrifice": { "types": ["creature"] } }, "spell": { "targets": ["player"], "effects": [{ "damage": "sacrificedPower", "to": "target" }] } }""", "{R}");
        var s = new Scenario();
        Mountains(s, 1);
        s.Add(P0, Scenario.Creature("Fodder", 3, 3));
        s.InHand(P0, thud);
        Script(s, Cast(s, "Heavy Toll"));
        s.Attacker.Choose = (_, req) => req.Options.Take(1).Select(o => o.Id).ToList();
        Aim(s, t => t.Player == P1);
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ExilingAFaceDownLibraryKeepsOnlyTheBottomCardAndAnEmblemWorksAtTheEndStep()
    {
        var wipe = Make("Hollow Out", "Sorcery", """{ "spell": { "targets": ["player"], "effects": [{ "exileLibraryAllBut": 1, "who": "target" }] } }""", "{1}");
        var s = new Scenario();
        Mountains(s, 1);
        s.InHand(P0, wipe);
        Script(s, Cast(s, "Hollow Out"));
        Aim(s, t => t.Player == P1);
        await s.RunUntilTurn();
        Assert.Single(s.Game.State.GetPlayer(P1).Library);

        var walker = Make("Vault Walker", "Legendary Planeswalker — Test", """
            { "abilities": [{ "cost": "-1", "effects": [{ "emblem": "Vault Walker emblem", "abilities": [
                { "trigger": "endStep", "effects": [{ "search": { "types": ["land"] }, "to": "battlefield" }], "text": "At the beginning of your end step, search for a land." }] }] }] }
            """, "{3}", null);
        var s2 = new Scenario();
        var w = s2.Add(P0, walker);
        s2.Card(w).Counters[CounterKind.Loyalty] = 3;
        Script(s2, Activate(s2, "Vault Walker"));
        s2.Attacker.Choose = (_, req) => req.Options.Take(1).Select(o => o.Id).ToList();
        await s2.RunUntilTurn();
        Assert.Contains(s2.Game.Log, e => e is EmblemCreated);
        Assert.Equal(1, s2.Game.State.PermanentsControlledBy(P0).Count(c => c.Name == "Forest"));
    }

    [Fact]
    public async Task BlinkingATransformingCardBringsItBackTransformed()
    {
        var def = Make("Cocoon Beast", "Creature — Beast", """{ "abilities": [{ "cost": "{1}", "effects": [{ "blink": "self", "transformed": true }] }] }""", "{1}", "1/1");
        def = def with { BackFace = Scenario.Creature("Winged Beast", 4, 4) };
        var s = new Scenario();
        Mountains(s, 1);
        var id = s.Add(P0, def);
        Script(s, Activate(s, "Cocoon Beast"));
        await s.RunUntilTurn();
        Assert.True(s.Card(id).Transformed);
        Assert.Equal(Zone.Battlefield, s.Card(id).Zone);
    }
}
