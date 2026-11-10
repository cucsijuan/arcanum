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
/// Academy mechanics: casualty, echo, gravestorm, bestow, skipping turns from coin flips, a delayed main-phase effect,
/// halving life, devoid and protection from multicolored. The cards are invented.
/// </summary>
public class AcademyMechanicsTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"academy-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, script.Length == 0 ? null : CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    private static void CastFirst(Scenario s, CardId card)
    {
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static CardDefinition Gain() =>
        Make("Ember Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 2 }] } }""", "{R}", null, "You gain 2 life.");

    private const string CasualtyText = "Casualty 1\nYou gain 2 life.";

    [Theory]
    [InlineData(true, 24, Zone.Graveyard)]
    [InlineData(false, 22, Zone.Battlefield)]
    public async Task CasualtyCopiesTheSpellWhenYouSacrificeACreatureWithPowerOneOrGreater(bool sacrifice, int life, Zone baitZone)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bait = s.Add(P0, Creature("Bait", 1, 1));
        var spell = s.InHand(P0, Make("Brisk Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 2 }] } }""", "{R}", null, CasualtyText, "Casualty"));
        CastFirst(s, spell);
        s.Attacker.YesNo = (_, r) => !r.Prompt.StartsWith("Casualty") || sacrifice;
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(baitZone, s.Card(bait).Zone);
    }

    [Fact]
    public async Task APermanentCanGiveYourInstantsAndSorceriesCasualty()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Relay Warden", "Creature — Human", """{ "spellsHaveCasualty": { "types": ["instant", "sorcery"] }, "spellsHaveCasualtyN": 1 }""", "{1}", "2/2",
            "Each instant and sorcery spell you cast has casualty 1."));
        CastFirst(s, s.InHand(P0, Gain()));
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
    }

    private static CardDefinition Hound() => Make("Ember Hound", "Creature — Hound", "", "{1}", "2/2", "Echo {1}", "Echo");

    [Fact]
    public async Task EchoSacrificesTheCreatureWhenTheEchoCostIsNotPaid()
    {
        // Cast on turn 1, so its echo is due in the upkeep of player 0's next turn (3).
        var s = new Scenario();
        s.Lands(P0, 2);
        var hound = s.InHand(P0, Hound());
        CastFirst(s, hound);
        s.Attacker.YesNo = (_, r) => !r.Prompt.StartsWith("Pay echo");
        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Graveyard, s.Card(hound).Zone);
    }

    [Fact]
    public async Task EchoStaysWhenThePaymentIsMadeAndIsAskedOnce()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var hound = s.InHand(P0, Hound());
        CastFirst(s, hound);
        int asked = 0;
        s.Attacker.YesNo = (_, r) =>
        {
            if (r.Prompt.StartsWith("Pay echo")) asked++;
            return true;
        };
        await s.RunUntilTurn(5);
        Assert.Equal(1, asked);
        Assert.Equal(Zone.Battlefield, s.Card(hound).Zone);
    }

    [Fact]
    public async Task GravestormCopiesTheSpellOncePerPermanentPutIntoAGraveyardThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Creature("Bait", 1, 1));
        s.Add(P0, Creature("Bait", 1, 1));
        var sweep = s.InHand(P0, Make("Sweep Note", "Sorcery", """{ "spell": { "effects": [{ "destroy": { "each": { "types": ["creature"], "controller": "any" } } }] } }""", "{R}", null, "Destroy each creature."));
        var gravestorm = s.InHand(P0, Make("Ember Verse", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}", null, "Gravestorm\nYou gain 1 life.", "Gravestorm"));
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(sweep).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sweep) is { } first) return first;
            if (s.Card(sweep).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == gravestorm) is { } second) return second;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // the spell and its two copies
    }

    private static CardDefinition Bestowal() => Make("Hollow Spirit", "Enchantment Creature — Spirit",
        """{ "abilities": [{ "static": { "affects": "enchanted", "pump": [1, 1] }, "text": "Enchanted creature gets +1/+1." }] }""", "{1}{W}", "2/2",
        "Bestow {2}{W}\nEnchanted creature gets +1/+1.", "Bestow");

    [Fact]
    public async Task BestowAttachesToACreatureAndIsNotACreatureWhileAttached()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P0, Creature("Bait", 1, 1));
        var spirit = s.InHand(P0, Bestowal());
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bait) };
        CastFirst(s, spirit);
        s.Attacker.YesNo = (_, r) => r.Prompt.Contains("bestowed");
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(spirit).Zone);
        Assert.Equal(bait, s.Card(spirit).AttachedTo);
        Assert.False(s.Card(spirit).IsCreature);
        Assert.Equal(2, s.Card(bait).Power);
    }

    [Fact]
    public async Task WhenTheEnchantedCreatureLeavesTheBestowedCardBecomesACreature()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P0, Creature("Bait", 1, 1));
        var spirit = s.InHand(P0, Bestowal());
        var rift = s.InHand(P0, Make("Rift Note", "Sorcery", """{ "spell": { "effects": [{ "destroy": { "each": { "types": ["creature"], "name": "Bait", "controller": "any" } } }] } }""", "{R}", null,
            "Destroy each creature named Bait."));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bait) };
        CardId? attachedBefore = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(spirit).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spirit) is { } first) return first;
            if (s.Card(spirit).Zone == Zone.Battlefield && s.Card(rift).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == rift) is { } second)
            {
                attachedBefore = s.Card(spirit).AttachedTo;
                return second;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.YesNo = (_, r) => r.Prompt.Contains("bestowed");
        await s.RunUntilTurn();
        Assert.Equal(bait, attachedBefore);
        Assert.Equal(Zone.Graveyard, s.Card(bait).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(spirit).Zone);
        Assert.Null(s.Card(spirit).AttachedTo);
        Assert.True(s.Card(spirit).IsCreature);
        Assert.Equal(2, s.Card(spirit).Power);
    }

    [Fact]
    public async Task BestowedCardEntersAsACreatureWhenItsTargetIsGoneOnResolution()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P0, Creature("Bait", 1, 1));
        var spirit = s.InHand(P0, Bestowal());
        var snap = s.InHand(P0, Make("Snap Note", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}", null, "Destroy target creature."));
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(spirit).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spirit) is { } first) return first;
            if (s.Card(spirit).Zone == Zone.Stack && s.Card(snap).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == snap) is { } response) return response;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.YesNo = (_, r) => r.Prompt.Contains("bestowed");
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bait).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(spirit).Zone);
        Assert.Null(s.Card(spirit).AttachedTo);
        Assert.True(s.Card(spirit).IsCreature);
    }

    [Fact]
    public async Task FlippedHeadsSkipThatManyOfTheOpponentsTurns()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var coins = new List<CoinsFlipped>();
        int skipped = 0, firstOpponentTurn = 0;
        s.Game.EventRaised += e =>
        {
            switch (e)
            {
                case CoinsFlipped flipped: coins.Add(flipped); break;
                case TurnSkipped { Player: var p } when p == P1: skipped++; break;
                case TurnBegan { ActivePlayer: var p, TurnNumber: var n } when p == P1 && firstOpponentTurn == 0: firstOpponentTurn = n; break;
            }
        };
        var flips = s.InHand(P0, Make("Coin Quarrel", "Sorcery", """{ "spell": { "targets": ["opponent"], "effects": [{ "flipCoinsSkipTurns": 5, "who": "target" }] } }""", "{R}", null,
            "Target opponent skips a turn for each heads on five coin flips."));
        CastFirst(s, flips);
        await s.RunUntilTurn(10);
        var flipped = Assert.Single(coins);
        Assert.Equal(5, flipped.Count);
        Assert.True(flipped.Heads > 0, "The seed gives some heads, so turns are skipped.");
        Assert.Equal(flipped.Heads, s.Game.State.GetPlayer(P1).TurnsToSkip + skipped);
        Assert.Equal(flipped.Heads, skipped);
        Assert.Equal(flipped.Heads + 2, firstOpponentTurn); // player 0 takes the skipped turns, then the opponent's turn begins
    }

    [Fact]
    public async Task AtYourNextMainPhaseDelayedEffectHappensOnceInThePostcombatMainPhase()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        CastFirst(s, s.InHand(P0, Make("Daybreak Pledge", "Sorcery", """{ "spell": { "effects": [{ "atYourNextMainPhase": { "effects": [{ "gainLife": "triggerAmount" }], "text": "gain" }, "amount": 3 }] } }""", "{R}", null,
            "At the beginning of your next main phase, you gain 3 life.")));
        var life = new Dictionary<int, int>();
        var steps = new Dictionary<Step, int>();
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan t) life[t.TurnNumber] = s.Game.State.GetPlayer(P0).Life;
            if (e is StepBegan { ActivePlayer: var p, Step: var step } && p == P0 && step is Step.EndCombat or Step.End) steps.TryAdd(step, s.Game.State.GetPlayer(P0).Life);
        };
        await s.RunUntilTurn(4);
        Assert.Equal(20, steps[Step.EndCombat]); // before the postcombat main phase
        Assert.Equal(23, steps[Step.End]);       // gained in the postcombat main phase of turn 1
        Assert.Equal(23, life[2]);
        Assert.Equal(23, life[4]);               // only once
    }

    private static CardDefinition Splitter(string name) => Make(name, "Sorcery", """{ "spell": { "effects": [{ "loseLife": "affectedHalfLifeRoundedDown", "who": "everyone" }] } }""", "{R}", null,
        "Each player loses half their life, rounded down.");

    [Fact]
    public async Task EachPlayerLosesHalfTheirLifeRoundedDown()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        CastFirst(s, s.InHand(P0, Splitter("Splitting Plague")));
        await s.RunUntilTurn();
        Assert.Equal(10, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(10, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task HalfOfAnOddTotalRoundsDown()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Game.State.GetPlayer(P1).Life = 21;
        CastFirst(s, s.InHand(P0, Splitter("Splitting Plague")));
        await s.RunUntilTurn();
        Assert.Equal(10, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(11, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public void DevoidMakesTheCardColorless()
    {
        var card = Make("Hollow Drake", "Creature — Drake", "", "{2}{B}{B}", "4/3", "Devoid\nFlying", "Devoid", "Flying");
        Assert.Empty(card.ColorList);
    }

    [Theory]
    [InlineData("Twinflare", "{R}{W}", false)]
    [InlineData("Ember Lance", "{R}", true)]
    public async Task ProtectionFromMulticoloredStopsOnlyMulticoloredSpells(string name, string cost, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, GenericCards.Plains);
        var warden = s.Add(P1, Make("Prism Warden", "Creature — Warrior", """{ "abilities": [{ "static": { "affects": "self", "pump": [0, 0], "keywords": ["Protection from multicolored"] }, "text": "Protection from multicolored" }] }""",
            "{1}{W}", "1/1", "Protection from multicolored"));
        var spell = s.InHand(P0, Make(name, "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 3, "to": "target" }] } }""", cost, null,
            $"{name} deals 3 damage to target creature."));
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) is { } cast) { offered = true; return cast; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(castable, offered);
        Assert.Equal(castable ? Zone.Graveyard : Zone.Battlefield, s.Card(warden).Zone);
    }
}
