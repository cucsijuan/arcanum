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
/// Rules first needed by a set of hero, summon and job cards (docs/card-scripts.md, "Heroes, summons and jobs"): stun counters on any
/// untap, finality counters, tiered modes, job select, mana-spent conditions, coin flips, extra end steps, giving control away, damage
/// redirected to one creature, meld and more. The cards are invented; their scripts use the same vocabulary as the module's.
/// </summary>
public class HeroesTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"hero-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
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
    private static Step Activate(Scenario s, string name, int index = -1) =>
        legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name && (index < 0 || a.Index == index));

    private static CardDefinition Bear => Scenario.Creature("Bear", 2, 2);

    // ------------------------------------------------------------------ stun and finality counters

    [Fact]
    public async Task AStunCounterStopsAnUntapEffectToo()
    {
        var wake = Make("Wake Up", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "untap": "target" }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Bear);
        s.Card(bear).Tapped = true;
        s.Card(bear).Counters[CounterKind.Stun] = 2;
        s.InHand(P0, wake);
        s.Attacker.Targets = (_, req) => new[] { Target.Of(bear) };
        Script(s, Cast(s, "Wake Up"));
        await s.RunUntilTurn();
        Assert.True(s.Card(bear).Tapped);
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.Stun)); // the untap step and the spell each removed one instead (rule 122.1d)
    }

    [Fact]
    public async Task APermanentWithAFinalityCounterIsExiledInsteadOfGoingToTheGraveyard()
    {
        var kill = Make("Kill", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Bear);
        s.Card(bear).Counters[CounterKind.Finality] = 1;
        s.InHand(P0, kill);
        Script(s, Cast(s, "Kill"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(bear).Zone);
    }

    // ------------------------------------------------------------------ tiered

    private const string Tiered = """
        { "spell": { "modes": [
          { "text": "Small — {0} — 1 damage.", "cost": "{0}", "targets": ["creature"], "effects": [{ "damage": 1, "to": "target" }] },
          { "text": "Big — {2} — 4 damage.", "cost": "{2}", "targets": ["creature"], "effects": [{ "damage": 4, "to": "target" }] } ] } }
        """;

    [Fact]
    public async Task ATieredSpellOffersOnlyTheModesWhoseCostCanBePaidAndChargesTheChosenOne()
    {
        var bolt = Make("Tier Bolt", "Instant", Tiered, "{R}", oracle: "Tiered", keywords: "Tiered");
        // One land: only the {0} tier.
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Scenario.Creature("Mouse", 1, 1));
        s.InHand(P0, bolt);
        IReadOnlyList<int>? offered = null;
        s.Attacker.Modes = (_, req) => { offered = req.Possible; return new[] { req.Possible[^1] }; };
        Script(s, Cast(s, "Tier Bolt"));
        await s.RunUntilTurn();
        Assert.True(offered is null || offered.SequenceEqual(new[] { 0 })); // a single affordable mode is chosen without asking
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);

        // Three lands: the {2} tier is offered and paid.
        var s2 = new Scenario();
        s2.Lands(P0, 3);
        var bear2 = s2.Add(P1, Scenario.Creature("Big Bear", 3, 3));
        s2.InHand(P0, bolt);
        s2.Attacker.Modes = (_, req) => new[] { req.Possible[^1] };
        Script(s2, Cast(s2, "Tier Bolt"));
        await s2.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s2.Card(bear2).Zone);
        Assert.Equal(3, s2.Game.State.Battlefield.Select(s2.Card).Count(c => c.Name == "Mountain" && c.Tapped));
    }

    // ------------------------------------------------------------------ job select and flavor words

    [Fact]
    public async Task JobSelectCreatesAHeroAndAttachesTheEquipmentToIt()
    {
        var gear = Make("Trainee Sword", "Artifact — Equipment",
            """{ "abilities": [{ "static": { "affects": "equipped", "pump": [1, 0], "addSubtypes": ["Warrior"] }, "text": "Equipped creature gets +1/+0." }] }""",
            "{R}", null, "Job select (When this Equipment enters, create a 1/1 colorless Hero creature token, then attach this to it.)\nEquipped creature gets +1/+0.\nBlade Dance — Equip {2}",
            "Job select", "Equip", "Blade Dance");
        Assert.Contains(gear.Abilities, a => a is ActivatedAbility { IsEquip: true });
        var s = new Scenario();
        s.Lands(P0, 1);
        var sword = s.InHand(P0, gear);
        Script(s, Cast(s, "Trainee Sword"));
        await s.RunUntilTurn();
        var hero = s.Game.State.Battlefield.Select(s.Card).Single(c => c.Name == "Hero");
        Assert.Equal(hero.Id, s.Card(sword).AttachedTo);
        Assert.Equal(2, hero.Power);
        Assert.True(hero.HasSubtype("Warrior"));
    }

    // ------------------------------------------------------------------ mana spent

    [Fact]
    public async Task AMagecraftLikeTriggerChecksTheManaSpentOnTheSpell()
    {
        var engine = Make("Mana Watcher", "Creature — Wizard", """
            { "abilities": [{ "trigger": "castSpell", "filter": { "not": ["creature"] }, "if": { "atLeast": "manaSpent", "value": 4 },
                              "effects": [{ "counters": 1, "what": "self" }] }] }
            """, "{1}", "1/1");
        var small = Make("Small Spell", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}{R}");
        var big = Make("Big Spell", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{3}{R}");
        var s = new Scenario();
        s.Lands(P0, 7);
        var watcher = s.Add(P0, engine);
        s.InHand(P0, small);
        s.InHand(P0, big);
        Script(s, Cast(s, "Small Spell"), Cast(s, "Big Spell"));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(watcher).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ coin flips

    [Fact]
    public async Task TheFirstCoinFlipEachTurnIsWonWhileYouControlTheTwoHeadedCoin()
    {
        var coin = Make("Lucky King", "Creature — Human", """{ "replaces": ["FirstCoinFlipEachTurnWins"] }""", "{1}", "1/1");
        var flipper = Make("Coin Table", "Artifact", """
            { "abilities": [{ "cost": "{1}", "effects": [{ "flipCoin": true, "won": [{ "gainLife": 5 }], "lost": [{ "loseLife": 1 }] }] }] }
            """, "{1}");
        for (int seed = 1; seed <= 6; seed++)
        {
            var s = new Scenario(seed);
            s.Lands(P0, 2);
            s.Add(P0, coin);
            s.Add(P0, flipper);
            Script(s, Activate(s, "Coin Table"), Activate(s, "Coin Table"));
            await s.RunUntilTurn();
            var life = s.Game.State.GetPlayer(P0).Life;
            Assert.True(life is 30 or 24, $"seed {seed}: the first flip is won ({life})"); // 20 + 5, then the second flip is random (+5 or -1)
        }
    }

    // ------------------------------------------------------------------ an additional end step

    [Fact]
    public async Task AnAdditionalEndStepComesOnlyAfterTheFirstEndStep()
    {
        var clock = Make("Twin Clock", "Artifact", """
            { "abilities": [{ "trigger": "endStep", "effects": [{ "gainLife": 1 }, { "additionalEndStep": "ifFirst" }] }] }
            """, "{1}");
        var s = new Scenario();
        s.Add(P0, clock);
        Script(s);
        int endSteps = 0;
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Engine.State.Step.End } && s.Game.State.ActivePlayer == P0) endSteps++; };
        await s.RunUntilTurn();
        Assert.Equal(2, endSteps);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ giving control away

    [Fact]
    public async Task GivingAPermanentToAnOpponentTriggersWhateverWatchesForThat()
    {
        var merchant = Make("Gift Merchant", "Creature — Moogle", """
            { "abilities": [
              { "cost": "{1}", "targets": ["opponent", { "kind": "permanent", "controller": "you", "filter": { "other": true } }],
                "effects": [{ "gainControl": "target2", "to": "target" }, { "if": { "atLeast": "controlGainedThisWay", "value": 1 }, "then": [{ "gainLife": 1 }] }] },
              { "trigger": "opponentGainsControlFromYou", "effects": [{ "gainLife": 3 }] }] }
            """, "{1}", "1/1");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, merchant);
        var gift = s.Add(P0, Bear);
        Script(s, Activate(s, "Gift Merchant"));
        s.Attacker.Targets = (_, req) => new[] { Target.Of(P1), Target.Of(gift) };
        await s.RunUntilTurn();
        Assert.Equal(P1, s.Card(gift).Controller);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life); // 1 for the control given, 3 from the trigger
    }

    // ------------------------------------------------------------------ all damage to one creature

    [Fact]
    public async Task DamageToYouAndYourOtherPermanentsIsDealtToTheTortoiseWhichKeepsItsDamage()
    {
        var tortoise = Make("Shell Tortoise", "Creature — Turtle", """{ "replaces": ["DamageNotRemovedInCleanup", "RedirectYourDamageToSelf"] }""", "{4}", "1/20");
        var blast = Make("Blast", "Sorcery", """{ "spell": { "effects": [{ "damage": 3, "to": "opponents" }, { "damage": 2, "to": { "each": { "types": ["creature"], "controller": "opponent" } } }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var turtle = s.Add(P1, tortoise);
        var bear = s.Add(P1, Bear);
        s.InHand(P0, blast);
        Script(s, Cast(s, "Blast"));
        await s.RunUntilTurn();
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(0, s.Card(bear).Damage);
        Assert.Equal(7, s.Card(turtle).Damage); // 3 for you, 2 for the bear, 2 of its own
        Assert.Equal(Zone.Battlefield, s.Card(turtle).Zone);
    }

    // ------------------------------------------------------------------ mill plus four

    [Fact]
    public async Task OpponentsMillFourMoreWhileYouControlTheCrystal()
    {
        var crystal = Make("Tide Crystal", "Artifact", """{ "replaces": ["OpponentsMillFourMore"] }""", "{4}");
        var mill = Make("Small Mill", "Sorcery", """{ "spell": { "effects": [{ "mill": 1, "who": "opponents" }, { "mill": 1 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, crystal);
        s.InHand(P0, mill);
        Script(s, Cast(s, "Small Mill"));
        await s.RunUntilTurn();
        Assert.Equal(5, s.Game.State.GetPlayer(P1).Graveyard.Count);
        Assert.Contains(s.Game.State.GetPlayer(P0).Graveyard, id => s.Card(id).Name == "Forest"); // you mill only one
        Assert.Equal(2, s.Game.State.GetPlayer(P0).Graveyard.Count); // the spell and one card
    }
    // ------------------------------------------------------------------ meld

    [Fact]
    public async Task TwoCardsMeldIntoOnePermanentWhoseManaValueIsTheirSum()
    {
        var giant = Make("Twin Giant", "Legendary Creature — Giant", "{}", "", "9/9") with { IsMeldResult = true };
        var left = Make("Left Twin", "Legendary Creature — Human", """
            { "meldsWith": "Right Twin", "meldInto": "Twin Giant", "abilities": [{ "trigger": "precombatMain",
              "if": { "control": { "types": ["creature"], "name": "Right Twin", "ownedByYou": true } },
              "effects": [{ "mayPay": "Meld?", "mana": "{1}", "effects": [{ "meld": true }] }] }] }
            """, "{2}", "2/2") with { MeldResult = giant };
        var right = Make("Right Twin", "Legendary Creature — Human", "{}", "{3}", "3/3");
        var s = new Scenario();
        s.Lands(P0, 1);
        var a = s.Add(P0, left);
        var b = s.Add(P0, right);
        Script(s);
        await s.RunUntilTurn();
        var melded = s.Game.State.Battlefield.Select(s.Card).Single(c => c.Name == "Twin Giant");
        Assert.Equal(9, melded.Power);
        Assert.Equal(5, melded.ManaValue);
        Assert.DoesNotContain(s.Game.State.Battlefield, id => s.Card(id).Name is "Left Twin" or "Right Twin");
        Assert.Contains(melded.Id, new[] { a, b });
    }

    // ------------------------------------------------------------------ overload

    [Fact]
    public async Task AnOverloadedSpellReplacesTargetWithEach()
    {
        var zap = Make("Wide Zap", "Instant", """
            { "overload": "{2}{R}",
              "spell": { "targets": ["creature"], "effects": [{ "damage": 1, "to": "target" }] },
              "overloaded": { "effects": [{ "damage": 1, "to": { "each": { "types": ["creature"], "controller": "opponent" } } }] } }
            """, "{R}");
        var s = new Scenario();
        s.Lands(P0, 3);
        var m1 = s.Add(P1, Scenario.Creature("Mouse", 1, 1));
        var m2 = s.Add(P1, Scenario.Creature("Mouse", 1, 1));
        var mine = s.Add(P0, Scenario.Creature("Mouse", 1, 1));
        s.InHand(P0, zap);
        s.Attacker.YesNo = (_, req) => true;
        Script(s, Cast(s, "Wide Zap"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(m1).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(m2).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(mine).Zone);
    }

    // ------------------------------------------------------------------ Phyrexian mana and life for black mana

    [Fact]
    public async Task APhyrexianSymbolCanBePaidWithTwoLife()
    {
        var spell = Make("Life Price", "Sorcery", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{B/P}");
        Assert.Equal(1, spell.ManaCost.ManaValue);
        var s = new Scenario();
        s.InHand(P0, spell);
        Script(s, Cast(s, "Life Price"));
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P0).Life);
        Assert.Contains(s.Game.State.GetPlayer(P0).Graveyard, id => s.Card(id).Name == "Life Price");
    }

    [Fact]
    public async Task WithTheLifeForBlackRuleEachBlackSymbolCanBePaidWithTwoLife()
    {
        var host = Make("Blood Host", "Creature — Avatar", """{ "payLifeForBlackMana": true }""", "{1}", "1/1");
        var spell = Make("Dark Price", "Sorcery", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{B}{B}");
        var s = new Scenario();
        s.Add(P0, host);
        s.InHand(P0, spell);
        Script(s, Cast(s, "Dark Price"));
        await s.RunUntilTurn();
        Assert.Equal(16, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ winning from an empty library

    [Fact]
    public async Task DrawingFromAnEmptyLibraryWinsInsteadWhileYouControlTheManiac()
    {
        var maniac = Make("Lab Keeper", "Creature — Human Wizard", """{ "winsDrawingFromEmptyLibrary": true }""", "{2}", "2/2");
        var spell = Make("Think", "Sorcery", """{ "spell": { "effects": [{ "draw": 1 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, maniac);
        s.InHand(P0, spell);
        foreach (var id in s.Game.State.GetPlayer(P0).Library.ToList()) s.Card(id).Zone = Zone.Exile;
        s.Game.State.GetPlayer(P0).Library.Clear();
        Script(s, Cast(s, "Think"));
        await s.RunUntilTurn();
        Assert.True(s.Game.State.IsGameOver);
        Assert.Equal(P0, s.Game.State.Winner);
    }

    // ------------------------------------------------------------------ surveil triggers, frogs and wizards

    [Fact]
    public async Task SurveillingTriggersYouSurveil()
    {
        var watcher = Make("Seer", "Creature — Human", """{ "abilities": [{ "trigger": "youSurveil", "effects": [{ "gainLife": 1 }] }] }""", "{1}", "1/1");
        var spell = Make("Peek", "Sorcery", """{ "spell": { "effects": [{ "surveil": 2 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, watcher);
        s.InHand(P0, spell);
        Script(s, Cast(s, "Peek"));
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task CreatingTokensAlsoCreatesAOneOneFrog()
    {
        var gourmet = Make("Frog Chef", "Creature — Human", """{ "replaces": ["ExtraFrogWithTokens"] }""", "{2}", "2/2");
        var spell = Make("Two Soldiers", "Sorcery", """
            { "spell": { "effects": [{ "tokens": 2, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "colors": ["W"], "keywords": [] } }] } }
            """, "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, gourmet);
        s.InHand(P0, spell);
        Script(s, Cast(s, "Two Soldiers"));
        await s.RunUntilTurn();
        var tokens = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Definition.IsToken).ToList();
        Assert.Equal(2, tokens.Count(c => c.Name == "Soldier"));
        var frog = Assert.Single(tokens, c => c.Name == "Frog");
        Assert.Equal((1, 1), (frog.Power, frog.Toughness));
    }

    [Fact]
    public async Task YourWizardsDealDoubleDamageToOpponents()
    {
        var lord = Make("Wizard Lord", "Creature — Human", """{ "replaces": ["DoubleWizardDamage"] }""", "{2}", "2/2");
        var pinger = Make("Pinger", "Creature — Human Wizard", """{ "abilities": [{ "cost": "{T}", "effects": [{ "damage": 1, "to": "opponents" }] }] }""", "{1}", "1/1");
        var s = new Scenario();
        s.Add(P0, lord);
        s.Add(P0, pinger);
        Script(s, Activate(s, "Pinger"));
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
    }
}
