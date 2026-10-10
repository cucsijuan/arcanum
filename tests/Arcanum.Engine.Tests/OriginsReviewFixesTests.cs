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
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Rules found by the skeptical review of Magic Origins, played with the module scripts of the cards involved: last known
/// information of an object that left and came back (400.7, 603.10a, 608.2h), tokens that ceased to exist (111.7), counters
/// a permanent enters with (614.1c), "defending player" once the attacker left combat (508.5) and static rules of a card
/// that lost its abilities (112.10b).
/// </summary>
public class OriginsReviewFixesTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", string[]? keywords = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"rev-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1],
            ["keywords"] = keywords ?? Array.Empty<string>(),
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

    private static Step WhenStack(Scenario s, int count, Step then) => legal => s.Game.State.Stack.Count == count ? then(legal) : null;

    private static Step AfterCombatDamage(Scenario s, Step then) => legal => s.Game.Log.OfType<DamageDealt>().Any() ? then(legal) : null;

    private static void Mountains(Scenario s, PlayerId who, int n) { for (int i = 0; i < n; i++) s.Add(who, GenericCards.Mountain); }

    private static CardDefinition Doom() => Make("Doom", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{1}");

    private static CardDefinition Raise() => Make("Raise", "Instant", """{ "spell": { "targets": [{ "kind": "graveyardCard", "filter": { "types": ["creature"] } }], "effects": [{ "reanimate": "target" }] } }""", "{1}");

    private static CardDefinition Flicker() => Make("Quick Flicker", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "blink": "target" }] } }""", "{1}");

    /// <summary>For each requirement, the first of these cards that is a legal choice.</summary>
    private static Func<GameView, TargetRequest, IReadOnlyList<Target>?> Pick(params CardId[] ids) =>
        (_, r) => Enumerable.Range(0, r.Specs.Count).Select(i => ids.Select(Target.Of).First(t => r.LegalAt(i).Contains(t))).ToList();

    // ------------------------------------------------------------------ Mage-Ring Responder (508.5, 608.2h)

    private const string ResponderScript = """
        { "doesntUntap": true,
          "abilities": [
            { "cost": "{7}", "effects": [{ "untap": "self" }] },
            { "trigger": "attacks",
              "targets": [{ "kind": "creature", "controlledByDefendingPlayer": true }],
              "effects": [{ "damage": 7, "to": "target" }] } ] }
        """;

    [Fact]
    public async Task ResponderStillDealsItsDamageToTheDefendersCreatureAfterItLeftCombat()
    {
        var responder = Make("Mage-Ring Responder", "Artifact Creature — Golem", ResponderScript, "{7}", "7/7");
        var victim = Make("Tall Victim", "Creature — Wall", "{}", "{1}", "1/7");
        var s = new Scenario();
        Mountains(s, P0, 1);
        var attacker = s.Add(P0, responder);
        var target = s.Add(P1, victim);
        s.InHand(P0, Flicker());
        Script(s.Attacker, WhenStack(s, 1, Cast(s, "Quick Flicker")));
        // The attack trigger's target is chosen with the stack empty (the defender's creature); the Flicker's, cast over it, is the Responder.
        s.Attacker.Targets = (v, r) => Pick(s.Game.State.Stack.Count == 0 ? target : attacker)(v, r);
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(attacker, defenders[0]) };
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == attacker && m.To == Zone.Exile); // it left combat before the trigger resolved
        Assert.Equal(Zone.Graveyard, s.Card(target).Zone); // 7 damage to a 1/7
    }

    // ------------------------------------------------------------------ Hangarback Walker (603.10a, 608.2h)

    private const string WalkerScript = """
        { "abilities": [
            { "trigger": "dies",
              "effects": [{ "tokens": { "counters": "+1/+1" },
                            "token": { "name": "Thopter", "types": "Artifact Creature — Thopter", "power": 1, "toughness": 1, "keywords": ["Flying"] } }] } ] }
        """;

    [Fact]
    public async Task HangarbackWalkerMakesAThopterForEachCounterItHadEvenIfItCameBackAndDiedAgain()
    {
        var walker = Make("Hangarback Walker", "Artifact Creature — Construct", WalkerScript, "{X}{X}", "0/0");
        var s = new Scenario();
        Mountains(s, P0, 2);
        var id = s.Add(P0, walker);
        s.Card(id).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.InHand(P0, Doom());
        s.InHand(P0, Raise());
        // The Walker dies with two counters; with its trigger on the stack it comes back as a new 0/0 (no counters), which dies again.
        Script(s.Attacker, Cast(s, "Doom"), WhenStack(s, 1, Cast(s, "Raise")));
        await s.RunUntilTurn();
        var thopters = s.Game.State.Battlefield.Select(s.Card).Count(c => c.Name == "Thopter");
        Assert.Equal(2, thopters);
    }

    // ------------------------------------------------------------------ Sengir Vampire (400.7)

    private const string SengirScript = """
        { "abilities": [
            { "trigger": "creatureDies",
              "filter": { "types": ["creature"], "controller": "any", "damagedBySource": true },
              "effects": [{ "counters": 1, "what": "self" }] } ] }
        """;

    [Theory]
    [InlineData(false, 1)] // the creature Sengir damaged dies: a counter
    [InlineData(true, 0)]  // it became a new object first: Sengir never damaged that one
    public async Task SengirVampireOnlyCountsCreaturesItDamagedAsTheSameObject(bool flickerBeforeItDies, int counters)
    {
        var sengir = Make("Sengir Vampire", "Creature — Vampire", SengirScript, "{3}{B}{B}", "4/4");
        var s = new Scenario();
        Mountains(s, P0, 3);
        var vampire = s.Add(P0, sengir);
        var wall = s.Add(P1, Make("Stone Wall", "Creature — Wall", "{}", "{1}", "0/5"));
        s.InHand(P0, Doom());
        if (flickerBeforeItDies) s.InHand(P0, Flicker());
        Script(s.Attacker,
            legal => flickerBeforeItDies ? AfterCombatDamage(s, Cast(s, "Quick Flicker"))(legal) : AfterCombatDamage(s, _ => null)(legal),
            legal => s.Game.State.Stack.Count == 0 ? AfterCombatDamage(s, Cast(s, "Doom"))(legal) : null);
        if (!flickerBeforeItDies) Script(s.Attacker, AfterCombatDamage(s, Cast(s, "Doom")));
        s.Attacker.Targets = Pick(wall);
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(vampire, defenders[0]) };
        s.Defender.Block = (_, blockers, attackers) => blockers.Select(b => new BlockDeclaration(b, attackers[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(wall).Zone);
        Assert.Equal(counters, s.Card(vampire).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ Liliana, Defiant Necromancer's emblem (111.7)

    private const string LilianaEmblem = """
        { "spell": { "effects": [{ "emblem": "Dead Return",
            "abilities": [{ "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any" },
                            "effects": [{ "atNextEndStepAbout": "triggered", "effects": [{ "reanimate": "triggered" }] }] }] }] } }
        """;

    [Fact]
    public async Task TheEmblemBringsBackACreatureCardButNotATokenThatDied()
    {
        var s = new Scenario();
        Mountains(s, P0, 4);
        var bear = s.Add(P0, Make("Grizzly Bear", "Creature — Bear", "{}", "{1}", "2/2"));
        var grunt = s.Add(P0, Make("Grunt", "Creature — Soldier", "{}", "{1}", "1/1") with { IsToken = true });
        s.InHand(P0, Make("Dead Return", "Sorcery", LilianaEmblem, "{1}"));
        s.InHand(P0, Doom());
        s.InHand(P0, Doom());
        int doomed = 0;
        Step Doom1() => legal => s.Game.State.Stack.Count == 0 && Cast(s, "Doom")(legal) is { } a ? Count(a) : null;
        PlayerAction Count(PlayerAction a) { doomed++; return a; }
        Script(s.Attacker, Cast(s, "Dead Return"), Doom1(), Doom1());
        s.Attacker.Targets = (_, r) => new[] { Target.Of(doomed == 1 ? bear : grunt) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone); // a card: it returns at the next end step
        Assert.NotEqual(Zone.Battlefield, s.Card(grunt).Zone); // a token ceased to exist when it died
    }

    // ------------------------------------------------------------------ Necromantic Summons (614.1c)

    [Fact]
    public async Task ACreatureReanimatedWithCountersHasThemWhenItsEntersTriggerLooksAtIt()
    {
        var summons = Make("Necromantic Summons", "Sorcery",
            """{ "spell": { "targets": [{ "kind": "graveyardCard", "filter": { "types": ["creature"] } }], "effects": [{ "reanimate": "target", "counters": 2 }] } }""", "{1}");
        // "Whenever a creature with power 4 or greater enters, you gain 5 life": a 2/2 that enters with two counters is a 4/4.
        var watcher = Make("Big Fan", "Enchantment",
            """{ "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "any", "minPower": 4 }, "effects": [{ "gainLife": 5 }] }] }""", "{1}");
        var s = new Scenario();
        Mountains(s, P0, 1);
        s.Add(P0, watcher);
        var dead = s.Add(P0, Make("Dead Bear", "Creature — Bear", "{}", "{1}", "2/2"));
        var owner = s.Game.State.GetPlayer(P0);
        owner.Library.Remove(dead); s.Game.State.Battlefield.Remove(dead);
        s.Card(dead).Zone = Zone.Graveyard; owner.Graveyard.Add(dead);
        s.InHand(P0, summons);
        Script(s.Attacker, Cast(s, "Necromantic Summons"));
        int before = s.Game.State.GetPlayer(P0).Life;
        int countersWhenItEntered = -1;
        s.Game.EventRaised += e => { if (e is CardMoved { To: Zone.Battlefield } m && m.Card == dead) countersWhenItEntered = s.Card(dead).CounterCount(CounterKind.PlusOnePlusOne); };
        await s.RunUntilTurn();
        Assert.Equal(2, countersWhenItEntered); // already on it as it entered, before anything reacts to it entering (614.1c)
        Assert.Equal(2, s.Card(dead).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(before + 5, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ Murder Investigation (608.2h)

    [Fact]
    public async Task MurderInvestigationCountsThePowerTheCreatureHadWhenItDiedEvenIfItCameBack()
    {
        var aura = Make("Murder Investigation", "Enchantment — Aura", """
            { "abilities": [{ "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any", "attachedToSource": true },
                              "effects": [{ "tokens": "triggeredPower", "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "colors": ["W"] } }] }] }
            """, "{1}{W}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        var cub = s.Add(P0, Make("Phoenix Cub", "Creature — Bird", "{}", "{1}", "1/1"));
        s.Card(cub).Counters[CounterKind.PlusOnePlusOne] = 2; // a 3/3 as it dies
        var attached = s.Add(P0, aura);
        s.Card(attached).AttachedTo = cub;
        s.InHand(P0, Doom());
        s.InHand(P0, Raise());
        Script(s.Attacker, Cast(s, "Doom"), WhenStack(s, 1, Cast(s, "Raise")));
        await s.RunUntilTurn();
        var soldiers = s.Game.State.Battlefield.Select(s.Card).Count(c => c.Name == "Soldier");
        Assert.Equal(3, soldiers); // not the 1 power of the object that came back
    }

    // ------------------------------------------------------------------ Abilities lost (112.10b)

    private static readonly string TurnToFrog = """
        { "spell": { "targets": ["creature"], "effects": [{ "become": "target", "power": 1, "toughness": 1, "setColors": ["U"], "setSubtypes": ["Frog"], "loseAbilities": true }] } }
        """;

    [Fact]
    public async Task ACreatureThatLostItsAbilitiesCanBeBlockedByAnyCreature()
    {
        var s = new Scenario();
        Mountains(s, P0, 1);
        var spirit = s.Add(P0, OriginsCards.Get("Orchard Spirit"));
        var wall = s.Add(P1, Creature("Plain Wall", 0, 5));
        s.InHand(P0, Make("Turn to Frog", "Instant", TurnToFrog, "{1}"));
        Script(s.Attacker, Cast(s, "Turn to Frog"));
        s.Attacker.Targets = Pick(spirit);
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(spirit, defenders[0]) };
        bool couldBlock = false;
        s.Defender.Block = (_, blockers, attackers) => { couldBlock = blockers.Contains(wall); return Array.Empty<BlockDeclaration>(); };
        await s.RunUntilTurn();
        Assert.True(couldBlock);
    }

    [Fact]
    public async Task AnArchangelOfTithesThatLostItsAbilitiesDoesNotTaxAttackers()
    {
        var s = new Scenario();
        Mountains(s, P0, 1);
        var attacker = s.Add(P0, Creature("Raider", 2, 2));
        var angel = s.Add(P1, OriginsCards.Get("Archangel of Tithes"));
        s.InHand(P0, Make("Turn to Frog", "Instant", TurnToFrog, "{1}"));
        Script(s.Attacker, Cast(s, "Turn to Frog"));
        s.Attacker.Targets = Pick(angel);
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(attacker, defenders[0]) };
        int life = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is DamageDealt d && d.Source == attacker);
        Assert.Equal(life - 2, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ Erebos's Titan (108.4)

    [Fact]
    public async Task ACardThatDiedUnderAnOpponentsControlHasItsGraveyardAbilitiesControlledByItsOwner()
    {
        // "Whenever a creature card leaves an opponent's graveyard, you gain 3 life" from this card's owner's graveyard.
        var watcher = Make("Grave Watcher", "Creature — Spirit", """
            { "abilities": [{ "trigger": "leavesGraveyard", "fromGraveyard": true, "filter": { "types": ["creature"], "controller": "opponent" },
                              "effects": [{ "gainLife": 3 }] }] }
            """, "{1}", "2/2");
        var s = new Scenario();
        Mountains(s, P0, 3);
        var titan = s.Add(P0, watcher);
        s.Card(titan).Controller = P1; // stolen by the other player
        s.Card(titan).BaseController = P1;
        var theirs = s.Add(P1, Make("Their Bear", "Creature — Bear", "{}", "{1}", "2/2"));
        s.InHand(P0, Doom());
        s.InHand(P0, Doom());
        s.InHand(P0, Raise());
        Step Empty(Step then) => legal => s.Game.State.Stack.Count == 0 ? then(legal) : null;
        Script(s.Attacker, Cast(s, "Doom"), Empty(Cast(s, "Doom")), Empty(Cast(s, "Raise")));
        int doom = 0;
        s.Attacker.Targets = (_, r) => new[] { Target.Of(r.LegalAt(0).Contains(Target.Of(theirs)) && doom++ == 0 ? titan : theirs) };
        int life0 = s.Game.State.GetPlayer(P0).Life, life1 = s.Game.State.GetPlayer(P1).Life;
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Card(titan).Controller);
        Assert.Equal(Zone.Battlefield, s.Card(theirs).Zone); // left its owner's graveyard: P1 is P0's opponent
        Assert.Equal(life0 + 3, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(life1, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ Hallowed Moonlight (614.12)

    private const string Moonlight = """
        { "spell": { "effects": [{ "exileUncastEntering": { "types": ["creature"] } }, { "draw": 1 }] } }
        """;

    [Fact]
    public async Task HallowedMoonlightLetsACreatureReturnTransformedIntoAPlaneswalker()
    {
        var front = Make("Young Hero", "Legendary Creature — Human Soldier", """{ "abilities": [{ "cost": "{1}", "effects": [{ "blink": "self", "transformed": true }] }] }""", "{1}{W}", "2/2");
        var back = Make("Hero Ascended", "Legendary Planeswalker — Gideon", "{}", "", null);
        front = front with { BackFace = back with { Loyalty = 3 } };
        var s = new Scenario();
        Mountains(s, P0, 2);
        var hero = s.Add(P0, front);
        s.InHand(P0, Make("Hallowed Moonlight", "Instant", Moonlight, "{1}"));
        Step Empty(Step then) => legal => s.Game.State.Stack.Count == 0 ? then(legal) : null;
        Script(s.Attacker, Cast(s, "Hallowed Moonlight"), Empty(legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == hero)));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(hero).Zone); // it enters as a planeswalker, not a creature
        Assert.True(s.Card(hero).Transformed);
    }

    [Fact]
    public async Task HallowedMoonlightExilesAnEnchantmentThatWouldEnterAsACreature()
    {
        // "As long as you control two or more enchantments, each other non-Aura enchantment you control is a creature with base power and toughness equal to its mana value."
        var starfield = Make("Small Starfield", "Enchantment", """
            { "abilities": [{ "static": { "affects": "permanents:you", "other": true, "filter": { "types": ["enchantment"], "notSubtype": "Aura" },
                                          "while": { "control": { "types": ["enchantment"] }, "count": 2 }, "addTypes": ["creature"],
                                          "setPower": { "manaValue": "affected" }, "setToughness": { "manaValue": "affected" } } }] }
            """, "{2}");
        var s = new Scenario();
        Mountains(s, P0, 2);
        s.Add(P0, starfield);
        s.Add(P0, Make("Plain Charm", "Enchantment", "{}", "{1}"));
        var relic = s.Add(P0, Make("Old Relic", "Enchantment", "{}", "{1}"));
        var owner = s.Game.State.GetPlayer(P0);
        s.Game.State.Battlefield.Remove(relic);
        s.Card(relic).Zone = Zone.Graveyard; owner.Graveyard.Add(relic);
        s.InHand(P0, Make("Hallowed Moonlight", "Instant", Moonlight, "{1}"));
        s.InHand(P0, Make("Raise Relic", "Instant", """{ "spell": { "targets": [{ "kind": "graveyardCard", "filter": { "types": ["enchantment"] } }], "effects": [{ "reanimate": "target" }] } }""", "{1}"));
        Step Empty(Step then) => legal => s.Game.State.Stack.Count == 0 ? then(legal) : null;
        Script(s.Attacker, Cast(s, "Hallowed Moonlight"), Empty(Cast(s, "Raise Relic")));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(relic).Zone);
    }
}
