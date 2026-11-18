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
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Scripted rules for extra upkeep steps, land types, casting limits, power and toughness switching, chosen-color mana,
/// opponents' land plays and destruction by mana value; the cards are invented.
/// </summary>
public class GuestCardMechanicsTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"guest-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, script.Length == 0 ? null : CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    [Fact]
    public async Task AnAdditionalUpkeepStepIsGrantedOnlyAtTheFirstUpkeepOfTheEnchantedPlayersTurn()
    {
        var s = new Scenario();
        var vigil = s.Game.SetupPermanent(P0, Make("Test Vigil", "Enchantment — Aura",
            """{"abilities":[{"trigger":"enchantedPlayersFirstUpkeep","effects":[{"additionalUpkeepStep":true}],"text":"At the beginning of enchanted player's first upkeep each turn, that player gets an additional upkeep step after this step."}]}""",
            "{1}", null, "Enchant player\nAt the beginning of enchanted player's first upkeep each turn, that player gets an additional upkeep step after this step.", "Enchant"));
        s.Card(vigil).AttachedPlayer = P0;
        var upkeeps = new List<int>();
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.Upkeep }) upkeeps.Add(s.Game.State.TurnNumber); };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(5);
        // Player 0's turns (1 and 3) have two upkeeps, player 1's turns one; turn 5's upkeep may be raised before the run stops.
        Assert.Equal(new[] { 1, 1, 2, 3, 3, 4 }, upkeeps.Where(t => t < 5));
    }

    [Fact]
    public async Task NonbasicLandsBecomeMountainsAndLoseTheirOtherLandTypes()
    {
        var s = new Scenario();
        s.Add(P0, Make("Test Sage", "Creature — Test", """{"abilities":[{"static":{"affects":"permanents","filter":{"types":["land"],"notSupertype":"basic"},"setLandType":"Mountain"},"text":"Nonbasic lands are Mountains."}]}""",
            "{1}", "2/2", "Nonbasic lands are Mountains."));
        var mire = s.Add(P0, Make("Test Mire", "Land — Forest Island", "", "", null, ""));
        var forest = s.Add(P0, GenericCards.Forest);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(new[] { "Mountain" }, s.Card(mire).CurrentSubtypes);
        Assert.Equal(new[] { "Forest" }, s.Card(forest).CurrentSubtypes);
    }

    [Fact]
    public async Task EachPlayerCastsOnlyOneNoncreatureSpellEachTurnButStillCastsCreatures()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P0, Make("Test Hush", "Enchantment", """{"rules":["EachPlayerOneNoncreatureSpellPerTurn"]}""", "{1}", null,
            "Each player can't cast more than one noncreature spell each turn."));
        var first = s.InHand(P0, Make("Test Ember", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{1}", null, "You gain 1 life."));
        var second = s.InHand(P0, Make("Test Glow", "Instant", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{1}", null, "You gain 1 life."));
        var bear = s.InHand(P0, Creature("Test Bear", 2, 2));
        bool cast = false, recorded = false;
        var offered = new List<CardId>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == first) is { } a) { cast = true; return a; }
            if (!recorded && s.Card(first).Zone == Zone.Graveyard)
            {
                recorded = true;
                offered = legal.OfType<CastSpell>().Select(c => c.Card).ToList();
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Contains(bear, offered);
        Assert.DoesNotContain(second, offered);
    }

    /// <summary>Activates the abilities in order (each once the stack is empty) and returns the distinct power and toughness seen between them.</summary>
    private static async Task<List<(int Power, int Toughness)>> ActivateInOrder(Scenario s, CardId card, params int[] abilities)
    {
        var queue = new Queue<int>(abilities);
        var seen = new List<(int Power, int Toughness)>();
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Game.State.Stack.Count > 0) return PassPriority.Instance;
            var now = (s.Card(card).Power, s.Card(card).Toughness);
            if (seen.Count == 0 || seen[^1] != now) seen.Add(now);
            if (queue.Count > 0 && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == card && a.Index == queue.Peek()) is { } activate)
            {
                queue.Dequeue();
                return activate;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(2);
        return seen;
    }

    private const string TurncoatScript = """{"abilities":[{"cost":"{0}","effects":[{"switchPowerToughness":"self"}],"text":"{0}: Switch this creature's power and toughness until end of turn."},{"cost":"{1}","effects":[{"pump":[1,0],"what":"self"}],"text":"{1}: This creature gets +1/+0 until end of turn."}]}""";

    [Fact]
    public async Task SwitchingPowerAndToughnessTwiceReturnsTheCreatureToItsPrintedValuesAndTheEffectEndsWithTheTurn()
    {
        var s = new Scenario();
        var turncoat = s.Add(P0, Make("Test Turncoat", "Creature — Test", TurncoatScript, "{1}", "1/4",
            "{0}: Switch this creature's power and toughness until end of turn.\n{1}: This creature gets +1/+0 until end of turn."));
        var seen = await ActivateInOrder(s, turncoat, 0, 0);
        Assert.Equal(new[] { (1, 4), (4, 1), (1, 4) }, seen);
        Assert.Equal((1, 4), (s.Card(turncoat).Power, s.Card(turncoat).Toughness));
    }

    [Fact]
    public async Task APumpBeforeTheSwitchIsIncludedInTheSwitchedValues()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var turncoat = s.Add(P0, Make("Test Turncoat", "Creature — Test", TurncoatScript, "{1}", "1/4",
            "{0}: Switch this creature's power and toughness until end of turn.\n{1}: This creature gets +1/+0 until end of turn."));
        var seen = await ActivateInOrder(s, turncoat, 1, 0);
        Assert.Equal(new[] { (1, 4), (2, 4), (4, 2) }, seen);
    }

    [Fact]
    public async Task ALandWithAChosenColorManaAbilityTapsForThatColorOnly()
    {
        var s = new Scenario();
        var land = s.InHand(P0, Make("Test Prism", "Land", """{"chooseOnEnter":"color","extraMana":[{"types":"chosen"}]}""", "", null,
            "As this land enters, choose a color.\n{T}: Add one mana of the chosen color."));
        bool played = false;
        var types = new List<ManaType>();
        s.Attacker.Option = (_, _) => 3; // Red
        s.Attacker.Act = (_, legal) =>
        {
            if (!played && legal.OfType<PlayLand>().FirstOrDefault(p => p.Card == land) is { } play) { played = true; return play; }
            if (played && types.Count == 0) types.AddRange(legal.OfType<ActivateManaAbility>().Where(a => a.Source == land).Select(a => a.Type).Distinct());
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(new[] { ManaType.Red }, types);
    }

    [Fact]
    public async Task WheneverAnOpponentPlaysALandTheControllerDrawsACard()
    {
        var s = new Scenario();
        s.Add(P0, Make("Test Lookout", "Enchantment", """{"abilities":[{"trigger":"opponentPlaysLand","effects":[{"draw":1}],"text":"Whenever an opponent plays a land, you draw a card."}]}""", "{1}", null,
            "Whenever an opponent plays a land, you draw a card."));
        var draws = new Dictionary<int, int>();
        s.Game.EventRaised += e =>
        {
            if (e is CardDrawn { Player: var p } && p == P0) draws[s.Game.State.TurnNumber] = draws.GetValueOrDefault(s.Game.State.TurnNumber) + 1;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(3); // player 0 plays a land on turn 1, player 1 on turn 2
        Assert.Equal(0, draws.GetValueOrDefault(1)); // the controller's own land plays do not trigger it
        Assert.Equal(1, draws.GetValueOrDefault(2));
    }

    [Fact]
    public async Task DestroysOnlyNonlandPermanentsWhoseManaValueEqualsTheChargeCounters()
    {
        var s = new Scenario();
        var source = s.Add(P0, Make("Test Charge Quarry", "Land",
            """{"abilities":[{"cost":"{0}","effects":[{"destroy":{"each":{"not":["land"],"controller":"any"}},"manaValueIs":{"counters":"charge"}}],"text":"{0}: Destroy each nonland permanent with mana value equal to the number of charge counters on this land."}]}""",
            "", null, "{0}: Destroy each nonland permanent with mana value equal to the number of charge counters on this land."));
        s.Card(source).Counters[CounterKind.Charge] = 2;
        var twoDrop = s.Add(P0, Make("Test Two Drop", "Creature — Test", "", "{1}{1}", "1/1", ""));
        var threeDrop = s.Add(P1, Make("Test Three Drop", "Creature — Test", "", "{2}{1}", "1/1", ""));
        bool used = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!used && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == source) is { } activate) { used = true; return activate; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(twoDrop).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(threeDrop).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(source).Zone);
    }
}
