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
/// Damage dealt by the object a trigger is about, permanents leaving with counters, creatures attacking you rather than your
/// planeswalkers, escalate, improvise, myriad, gifts of a card (for permanents too, and targets only a gift gives), doubling power
/// and toughness, alternative costs with taps, taxes only on attacking the player and "that player" of a targeting trigger.
/// </summary>
public partial class ScriptedVocabularyTests
{
    [Fact]
    public async Task TheEnteringCreatureDealsTheDamage()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Surge", CardType.Enchantment, "{1}", """
            { "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "you" },
                              "effects": [{ "damage": "triggeredPower", "to": "opponents", "from": "triggered" }] }] }
            """));
        s.InHand(P0, Scenario.Creature("Leech", 3, 3, Keyword.Lifelink));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(17, LifeOf(s, P1));
        Assert.Equal(23, LifeOf(s, P0)); // the lifelinking creature dealt it
    }

    [Fact]
    public async Task AnotherPermanentLeavingWithCountersTriggers()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Sleuth", """
            { "abilities": [{ "trigger": "permanentLeaves", "filter": { "controller": "you", "other": true }, "if": "triggeredHadCounters", "effects": [{ "gainLife": 5 }] }] }
            """, 2, 3));
        var marked = s.Add(P0, Activated("Marked Trinket", """[{ "gainLife": 0 }]""", "{0}, sacrifice"));
        s.Add(P0, Activated("Plain Trinket", """[{ "gainLife": 0 }]""", "{0}, sacrifice"));
        s.Card(marked).Counters[CounterKind.Charge] = 1;
        Acting(s, activations: 2);
        await s.RunUntilTurn();
        Assert.Equal(25, LifeOf(s, P0));
    }

    [Fact]
    public async Task EscalateCostsMoreForEachExtraMode()
    {
        foreach (int lands in new[] { 2, 3 })
        {
            var s = new Scenario();
            s.Lands(P0, lands);
            s.InHand(P0, Scripted("Choices", CardType.Instant, "{1}", """
                { "escalate": "{1}", "spell": { "chooseCount": 3, "upTo": true, "modes": [
                  { "text": "1", "effects": [{ "gainLife": 1 }] }, { "text": "2", "effects": [{ "gainLife": 2 }] }, { "text": "4", "effects": [{ "gainLife": 4 }] }] } }
                """));
            s.Attacker.Modes = (_, request) => request.Possible.Take(request.Max).ToList();
            Acting(s);
            await s.RunUntilTurn();
            Assert.Equal(lands == 3 ? 27 : 23, LifeOf(s, P0));
        }
    }

    [Fact]
    public async Task ImproviseTapsArtifactsForGenericMana()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var a = s.Add(P0, Scripted("Cog", CardType.Artifact, "{1}", "{}"));
        var b = s.Add(P0, Scripted("Gear", CardType.Artifact, "{1}", "{}"));
        var golem = s.InHand(P0, Scripted("Cannon Golem", CardType.Artifact | CardType.Creature, "{4}", """{ "improvise": true }""", 4, 4));
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(c => c.Id).ToList();
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone);
        Assert.True(s.Card(a).Tapped && s.Card(b).Tapped);
    }

    [Fact]
    public async Task MyriadCopiesAttackTheOtherOpponents()
    {
        var p2 = new PlayerId(2);
        TestController Passive() => new() { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var attacker = new TestController { Act = (_, _) => PassPriority.Instance };
        attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, P1)).ToList();
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, new[]
        {
            new PlayerSetup("Attacker", attacker, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", Passive(), Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Bystander", Passive(), Decks.Of((GenericCards.Forest, 20))),
        });
        var triad = game.SetupPermanent(P0, Creature("Triad", """{ "abilities": [{ "trigger": "attacks", "effects": [{ "myriad": true }] }] }""", 4, 3));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(16, game.State.GetPlayer(P1).Life);
        Assert.Equal(16, game.State.GetPlayer(p2).Life);
        Assert.DoesNotContain(game.State.Battlefield, id => id != triad && game.State.GetCard(id).Name == "Triad"); // exiled at end of combat
    }

    [Fact]
    public async Task APromisedCardIsDrawnAsThePermanentEnters()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Raccoon", CardType.Creature, "{1}", """
            { "gift": "card", "abilities": [{ "trigger": "enters", "if": "giftPromised", "effects": [{ "gainLife": 3 }] }] }
            """, 4, 4));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(23, LifeOf(s, P0));
        Assert.Equal(7 + 1, Drawn(s, P1));
    }

    [Fact]
    public async Task AGiftGivesASpellItsTargetAndTheGreatestPowerIsSacrificed()
    {
        foreach (bool promise in new[] { false, true })
        {
            var s = new Scenario();
            s.Lands(P0, 1);
            var big = s.Add(P1, Scenario.Creature("Big", 5, 5));
            var small = s.Add(P1, Scenario.Creature("Small", 2, 2));
            var dead = s.Add(P0, Scenario.Creature("Fallen", 1, 1));
            s.InHand(P0, Scripted("Greed", CardType.Instant, "{1}", """
                { "gift": "card", "spell": {
                  "targets": ["opponent", { "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"] }, "onlyIfGiftPromised": true }],
                  "effects": [{ "sacrifice": 1, "who": "target", "filter": { "types": ["creature"], "greatestPower": true } }, { "if": "giftPromised", "then": [{ "bounce": "target2" }] }] } }
                """));
            s.Attacker.YesNo = (_, _) => promise;
            s.Attacker.Discard = (view, count) => view.Self.Hand.Take(count).Select(c => c.Id).ToList(); // keep the returned card at cleanup
            Acting(s, () => ToGraveyard(s, dead));
            await s.RunUntilTurn();
            Assert.Equal(Zone.Graveyard, s.Card(big).Zone);
            Assert.Equal(Zone.Battlefield, s.Card(small).Zone);
            Assert.Equal(promise ? Zone.Hand : Zone.Graveyard, s.Card(dead).Zone);
            Assert.Equal(promise ? 8 : 7, Drawn(s, P1));
        }
    }

    [Fact]
    public async Task DoublesPowerAndToughness()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 3));
        s.Add(P0, Activated("Growth", """[{ "doublePowerAndToughness": { "each": { "types": ["creature"], "controller": "you" } } }]"""));
        int power = 0, toughness = 0;
        s.Game.EventRaised += e => { if (e is AbilityResolved) { power = s.Card(bear).Power; toughness = s.Card(bear).Toughness; } };
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal((4, 6), (power, toughness));
    }

    [Fact]
    public async Task AnAlternativeCostCanTapCreatures()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var a = s.Add(P0, Scenario.Creature("Bird", 1, 1, Keyword.Flying));
        var b = s.Add(P0, Scenario.Creature("Bat", 1, 1, Keyword.Flying));
        var blade = s.InHand(P0, Scripted("Blade", CardType.Creature, "{9}", """
            { "alternativeCost": { "cost": "{R}", "extra": { "tap": { "types": ["creature"], "keyword": "Flying" }, "tapCount": 2 } } }
            """, 7, 7));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(blade).Zone);
        Assert.True(s.Card(a).Tapped && s.Card(b).Tapped);
    }

    [Fact]
    public async Task APlayerOnlyAttackTaxLeavesPlaneswalkersFree()
    {
        var s = new Scenario();
        s.Add(P1, Scripted("Propaganda Office", CardType.Enchantment, "{1}", """{ "attackTax": "{2}", "attackTaxPlayerOnly": true }"""));
        var walker = s.Game.SetupPermanent(P1, new CardDefinition { Name = "Sage", ManaCost = Mana.ManaCost.Parse("{2}"), Types = CardType.Planeswalker, Loyalty = 5 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        s.Add(P0, Scenario.Creature("Brute", 3, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, P1, walker)).ToList();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(walker).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task ThatPlayerOfATargetingTriggerIsTheSpellsController()
    {
        var s = new Scenario();
        s.Add(P1, Scripted("Regent", CardType.Creature, "{4}", """
            { "abilities": [{ "trigger": "permanentBecomesTarget", "filter": { "types": ["creature"], "subtype": "Dragon" }, "effects": [{ "damage": 3, "to": "triggeredPlayer" }] }] }
            """, 4, 4, new[] { "Dragon" }));
        s.Add(P0, Activated("Sling", """[{ "damage": 1, "to": "target" }]""", "{0}", CardType.Artifact, """ "targets": ["creature:opponent"], """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(17, LifeOf(s, P0));
    }
}
