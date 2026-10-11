// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Paying any amount of one mana type, proliferate, divided damage worked out as it's cast, trample excess from a bite, cards going
/// into the library from a dies-or-exiled trigger, copies until end of turn, extra counters for some entering creatures, counting
/// permanents that entered this turn and static block restrictions.
/// </summary>
public partial class ScriptedVocabularyTests
{
    [Fact]
    public async Task PaysAnyAmountOfRedOnly()
    {
        var s = new Scenario();
        for (int i = 0; i < 3; i++) s.Add(P0, GenericCards.Mountain);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, Activated("Furnace", """
            [{ "mayPayX": "Pay any amount of {R}?", "manaType": "{R}", "effects": [{ "damage": "X", "to": "opponents" }] }]
            """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(17, LifeOf(s, P1)); // three red, the Forest can't help
    }

    [Fact]
    public async Task ProliferateAddsOneOfEachKind()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        var plain = s.Add(P0, Scenario.Creature("Plain", 2, 2));
        s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.Card(bear).Counters[CounterKind.Charge] = 2;
        s.Add(P0, Activated("Spreader", """[{ "proliferate": true }]"""));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(3, s.Card(bear).CounterCount(CounterKind.Charge));
        Assert.Equal(0, s.Card(plain).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task DividedDamageWorkedOutAsItIsCast()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Scenario.Creature("Brute", 5, 5));
        var a = s.Add(P1, Scenario.Creature("Target A", 3, 3));
        var b = s.Add(P1, Scenario.Creature("Target B", 2, 2));
        s.InHand(P0, Scripted("Onslaught", CardType.Sorcery, "{1}", """
            { "spell": { "targets": [{ "kind": "creature", "controller": "opponent", "anyNumber": true }], "effects": [{ "divideDamage": "greatestPower" }] } }
            """));
        s.Attacker.Targets = (_, request) => request.Legal[0].ToList();
        s.Attacker.Number = (_, request) => 3; // three to the first target, the rest (two) to the second
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(b).Zone);
    }

    [Fact]
    public async Task ATramplingBiterSendsTheExcessToTheController()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Scenario.Creature("Rhino", 6, 6, Keyword.Trample));
        var victim = s.Add(P1, Scenario.Creature("Victim", 2, 2));
        s.InHand(P0, Scripted("Ram", CardType.Instant, "{1}", """
            { "spell": { "targets": ["creature:you", "creature:opponent"], "effects": [{ "bite": "target", "to": "target2", "excessIfTrample": true }] } }
            """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(victim).Zone);
        Assert.Equal(16, LifeOf(s, P1));
    }

    [Fact]
    public async Task GoesThirdFromTheTopWhenItDiesOrIsExiled()
    {
        foreach (bool exile in new[] { false, true })
        {
            var s = new Scenario();
            var god = s.Add(P0, Creature("God", """
                { "abilities": [{ "trigger": "diesOrExiled", "effects": [{ "may": "Third from the top?", "effects": [{ "toLibrary": "self", "position": 3 }] }] }] }
                """, 5, 6));
            s.Add(P0, Activated("Ender", exile ? """[{ "exile": { "each": { "types": ["creature"] } } }]""" : """[{ "destroy": { "each": { "types": ["creature"] } } }]"""));
            Acting(s);
            await s.RunUntilTurn();
            Assert.Equal(Zone.Library, s.Card(god).Zone);
            Assert.Equal(god, s.Game.State.GetPlayer(P0).Library[2]);
        }
    }

    [Fact]
    public async Task ACopyUntilEndOfTurnWithHasteEnds()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P1, Scenario.Creature("Dragon", 5, 5, Keyword.Flying));
        var mirror = s.InHand(P0, Scripted("Mirror", CardType.Artifact, "{1}", """{ "entersAsCopy": { "types": ["creature"] }, "entersAsCopyUntilEndOfTurn": true, "entersAsCopyWithHaste": true }"""));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        bool hasteAsCopy = false;
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.BeginCombat }) hasteAsCopy = s.Card(mirror).Has(Keyword.Haste) && s.Card(mirror).Power == 5; };
        Acting(s);
        await s.RunUntilTurn();
        Assert.True(hasteAsCopy);
        Assert.Equal("Mirror", s.Card(mirror).Name);
        Assert.False(s.Card(mirror).IsCreature);
    }

    [Fact]
    public async Task OnlyMatchingCreaturesEnterWithTheExtraCounter()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Scripted("Globe", CardType.Artifact, "{3}", """{ "othersEnterWithCounters": 1, "othersEnterFilter": { "types": ["creature"], "subtype": "Dragon" } }"""));
        var dragon = s.InHand(P0, Scripted("Whelp", CardType.Creature, "{1}", "{}", 2, 2, new[] { "Dragon" }));
        var bear = s.InHand(P0, Scenario.Creature("Bear", 2, 2));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(dragon).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task CelebrationCountsNonlandPermanentsThatEntered()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var reveler = s.Add(P0, Creature("Reveler", """
            { "abilities": [{ "static": { "affects": "self", "while": { "enteredThisTurn": { "not": ["land"] }, "count": 2 }, "setPower": 4, "setToughness": 4, "keywords": ["Flying"] } }] }
            """, 3, 3));
        s.InHand(P0, Scenario.Creature("Guest A", 1, 1));
        s.InHand(P0, Scenario.Creature("Guest B", 1, 1));
        var flying = new List<bool>();
        s.Game.EventRaised += e => { if (e is PriorityGiven { Player.Value: 0 }) flying.Add(s.Card(reveler).Has(Keyword.Flying)); };
        Acting(s);
        await s.RunUntilTurn();
        Assert.False(flying[0]);
        Assert.True(flying.Last());
    }

    [Fact]
    public async Task AStaticAbilityCanKeepWallsFromBlocking()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Juggernaut Lord", CardType.Artifact, "{1}", """{ "abilities": [{ "static": { "affects": "creatures:you", "cantBeBlockedBy": { "subtype": "Wall" } } }] }"""));
        s.Add(P0, Scenario.Creature("Brute", 3, 3));
        s.Add(P1, Scripted("Wall", CardType.Creature, "{1}", "{}", 0, 6, new[] { "Wall" }));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(17, LifeOf(s, P1));
    }
}
