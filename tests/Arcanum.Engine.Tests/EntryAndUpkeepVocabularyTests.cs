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
/// Vocabulary for entering and upkeep: targeting triggered abilities only, artifacts and lands entering tapped, skipping
/// the draw step, a maximum hand size, paying any amount of life, vanishing, and copies that get vanishing.
/// </summary>
public class EntryAndUpkeepVocabularyTests
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

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().LastOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task TriggeredOnlyTargetsCanTargetATriggeredAbilityButNotAnActivatedOne()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var idol = s.Add(P0, Scripted("Gold Idol", "Artifact", "{1}",
            """{ "abilities": [{ "cost": "{0}", "effects": [{ "gainLife": 1 }], "text": "{0}: Gain 1 life." }] }"""));
        var bear = s.InHand(P0, Scripted("Chime Bear", "Creature — Bear", "{1}",
            """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 1 }], "text": "x" }] }""", power: "1", toughness: "1"));
        var counter = s.InHand(P0, Scripted("Hush Note", "Instant", "{R}",
            """{ "spell": { "targets": [{ "kind": "spellOrAbility", "triggeredOnly": true }], "effects": [{ "counter": "target" }] } }"""));
        int step = 0;
        int triggerId = -1, idolId = -1;
        var legal = new List<int?>();
        s.Attacker.Act = (view, actions) =>
        {
            // Cast the bear; once it is on the battlefield its enters trigger is the only thing on the stack: activate the idol over it.
            if (step == 0 && actions.OfType<CastSpell>().FirstOrDefault(c => c.Card == bear) is { } cast) { step = 1; return cast; }
            if (step == 1 && s.Card(bear).Zone == Zone.Battlefield && view.Stack.Count == 1
                && actions.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == idol) is { } activate)
            {
                triggerId = view.Stack[0].Id;
                step = 2;
                return activate;
            }
            // Two abilities on the stack: cast the counter, which is then asked for its targets.
            if (step == 2 && view.Stack.Count == 2 && actions.OfType<CastSpell>().FirstOrDefault(c => c.Card == counter) is { } counterCast)
            {
                idolId = view.Stack[^1].Id; // the top: the idol ability
                step = 3;
                return counterCast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Targets = (view, request) =>
        {
            if (request.Source != counter) return TestController.FirstAllowed(view, request);
            legal = request.LegalAt(0).Select(t => t.StackObject).ToList();
            return new[] { request.LegalAt(0).First(t => t.StackObject == triggerId) };
        };
        await s.RunUntilTurn();
        Assert.Equal(3, step); // the counter was cast
        Assert.Contains(triggerId, legal); // a triggered ability is a legal target
        Assert.DoesNotContain(idolId, legal); // an activated ability isn't
    }

    [Fact]
    public async Task ArtifactsAndLandsEnterTappedUnderAnyPlayersPermanentButCreaturesDont()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P1, Scripted("Tapped Gate", "Enchantment", "{1}", """{ "artifactsAndLandsEnterTapped": true }"""));
        var forest = s.InHand(P0, Arcanum.Cards.GenericCards.Forest);
        var gear = s.InHand(P0, new CardDefinition { Name = "Gear", Types = CardType.Artifact, ManaCost = ManaCost.Parse("{0}") });
        var bear = s.InHand(P0, Creature("Bear", 2, 2));
        s.Attacker.Act = (_, legal) =>
            legal.OfType<PlayLand>().Where(p => p.Card == forest).Cast<PlayerAction>().FirstOrDefault()
            ?? legal.OfType<CastSpell>().Where(c => c.Card == gear || c.Card == bear).Cast<PlayerAction>().FirstOrDefault()
            ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(forest).Zone);
        Assert.True(s.Card(forest).Tapped);
        Assert.Equal(Zone.Battlefield, s.Card(gear).Zone);
        Assert.True(s.Card(gear).Tapped);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.False(s.Card(bear).Tapped);
    }

    [Theory]
    [InlineData(true, 0)]  // skip your draw step: no card is drawn on turn 3
    [InlineData(false, 1)] // without it: the draw step draws one
    public async Task SkipYourDrawStepDrawsNoCardInThatStep(bool skip, int expected)
    {
        var s = new Scenario();
        if (skip) s.Add(P0, Scripted("Quiet Study", "Enchantment", "{1}", """{ "skipYourDrawStep": true }"""));
        int turn = 0, drawn = 0;
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan t) turn = t.TurnNumber;
            if (e is CardDrawn d && d.Player == P0 && turn == 3) drawn++;
        };
        await s.RunUntilTurn(4);
        Assert.Equal(expected, drawn);
    }

    [Theory]
    [InlineData(true, 5)]  // your maximum hand size is five: the six cards in hand discard down to five
    [InlineData(false, 6)] // without it: the seven-card hand less the land played is under the seven limit
    public async Task YourMaximumHandSizeDiscardsDownToItAtCleanup(bool limit, int expected)
    {
        var s = new Scenario();
        if (limit) s.Add(P0, Scripted("Tidy Shelf", "Enchantment", "{1}", """{ "yourMaximumHandSize": 5 }"""));
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Game.State.GetPlayer(P0).Hand.Count);
    }

    [Fact]
    public async Task PayAnyAmountOfLifeThenTheEffectsUseThatNumber()
    {
        var s = Casting();
        s.Attacker.Number = (_, _) => 3;
        s.InHand(P0, Scripted("Bargain Note", "Sorcery", "{0}", """{ "spell": { "effects": [{ "payAnyLife": true, "effects": [{ "draw": "X" }] }] } }"""));
        int life = s.Game.State.GetPlayer(P0).Life;
        int turn = 0, drawn = 0;
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan t) turn = t.TurnNumber;
            if (e is CardDrawn d && d.Player == P0 && turn == 1) drawn++;
        };
        await s.RunUntilTurn();
        Assert.Equal(life - 3, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(3, drawn);
    }

    [Fact]
    public async Task VanishingLosesATimeCounterEachUpkeepAndIsSacrificedWhenTheLastIsRemoved()
    {
        var s = Casting();
        s.Lands(P0, 1);
        var spirit = s.InHand(P0, Scripted("Fading Spirit", "Creature — Spirit", "{1}", """{ "vanishing": 2 }""", power: "2", toughness: "2"));
        int turn = 0, countersAtTurnTwo = -1;
        var removedOn = new List<int>();
        var sacrificedOn = new List<int>();
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan t)
            {
                turn = t.TurnNumber;
                if (turn == 2) countersAtTurnTwo = s.Card(spirit).CounterCount(CounterKind.Time);
            }
            if (e is CountersRemoved { Card: var removedFrom, Kind: CounterKind.Time } && removedFrom == spirit) removedOn.Add(turn);
            if (e is PermanentSacrificed { Card: var sacrificed } && sacrificed == spirit) sacrificedOn.Add(turn);
        };
        await s.RunUntilTurn(6);
        Assert.Equal(2, countersAtTurnTwo); // enters with two time counters
        Assert.Equal(new[] { 3, 5 }, removedOn); // one at each of your upkeeps
        Assert.Equal(new[] { 5 }, sacrificedOn); // the last one is removed on turn 5: sacrificed
        Assert.Equal(Zone.Graveyard, s.Card(spirit).Zone);
    }

    [Fact]
    public async Task ACopyOfACreatureWithoutVanishingGetsVanishingAndItsTimeCounters()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Bear", 2, 2));
        var mirror = s.InHand(P0, Scripted("Shade Mirror", "Artifact", "{1}",
            """{ "entersAsCopy": { "types": ["creature"] }, "copyGetsVanishingIfNone": 3 }"""));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(mirror).Definition.Vanishing);
        Assert.Equal(3, s.Card(mirror).CounterCount(CounterKind.Time));
    }
}
