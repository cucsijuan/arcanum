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
/// Returning a permanent as a cost, the player or planeswalker a creature attacks, an additional {C} for tapping for {C}, imprint,
/// tokens fighting the opponents' creatures, reselecting an attack, discover, read ahead, discarding any number, named options as a
/// permanent enters, casting from the top of the library with haste, and cards playable until your next end step.
/// </summary>
public partial class ScriptedVocabularyTests
{
    [Fact]
    public async Task ReturnsAnArtifactToPayAndPutsAnotherIntoPlay()
    {
        var s = new Scenario();
        var bauble = s.Add(P0, Scripted("Bauble", CardType.Artifact, "{1}", "{}"));
        var golem = s.InHand(P0, Scripted("Big Golem", CardType.Artifact | CardType.Creature, "{8}", "{}", 8, 8));
        s.Add(P0, Activated("Transmuter", """[{ "putFromHand": { "types": ["artifact"] }, "one": true }]""", "{0}, returnPermanent:artifact", CardType.Creature) with { Power = 1, Toughness = 2 });
        s.Attacker.Choose = (_, request) => request.Purpose == CardChoicePurpose.ToHand
            ? new[] { bauble } : request.Options.Where(o => o.Id == golem).Select(o => o.Id).ToList();
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id != bauble).Take(count).Select(c => c.Id).ToList(); // keep it at cleanup
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(bauble).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone);
    }

    [Fact]
    public async Task DamagesWhatItAttacks()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Sphere", """
            { "abilities": [{ "trigger": "attacks", "effects": [{ "damage": 2, "to": "attackedBySelf" }] }] }
            """, 1, 1));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(20 - 2 - 1, LifeOf(s, P1));
    }

    [Fact]
    public async Task TappingForColorlessAddsAnAdditionalColorless()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Monument", CardType.Artifact, "{5}", """{ "replaces": ["ExtraColorlessWhenTappedForColorless"] }"""));
        s.Add(P0, GenericCards.BarrenFlats);
        var ogre = s.InHand(P0, Scripted("Colorless Ogre", CardType.Artifact | CardType.Creature, "{2}", "{}", 2, 2));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(ogre).Zone); // one land paid for {2}
    }

    [Fact]
    public async Task ImprintGivesTheExiledCreaturesStatsAndTypes()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var giant = s.Add(P1, Scripted("Giant", CardType.Creature, "{5}", "{}", 7, 6, new[] { "Giant", "Warrior" }));
        var duplicant = s.InHand(P0, Scripted("Duplicate", CardType.Artifact | CardType.Creature, "{1}", """
            { "abilities": [
              { "trigger": "enters", "targets": [{ "kind": "creature", "filter": { "token": false } }], "effects": [{ "may": "Exile?", "effects": [{ "exile": "target", "linked": true }] }] },
              { "static": { "affects": "self", "while": "creatureCardExiledWithThis", "setPower": "lastExiledCreaturePower", "setToughness": "lastExiledCreatureToughness", "creatureTypesOfLastExiled": true } }] }
            """, 2, 4, new[] { "Shapeshifter" }));
        s.Attacker.Targets = (_, request) => new[] { Target.Of(giant) };
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(giant).Zone);
        Assert.Equal((7, 6), (s.Card(duplicant).Power, s.Card(duplicant).Toughness));
        Assert.True(s.Card(duplicant).HasSubtype("Giant") && s.Card(duplicant).HasSubtype("Warrior") && s.Card(duplicant).HasSubtype("Shapeshifter"));
    }

    [Fact]
    public async Task EachTokenFightsADifferentOpposingCreature()
    {
        var s = new Scenario();
        var a = s.Add(P1, Scenario.Creature("Small", 2, 2));
        var b = s.Add(P1, Scenario.Creature("Big", 5, 5));
        s.Add(P0, Activated("Predation", """[{ "tokensFightOpponentsCreatures": { "name": "Beast", "types": "Creature — Beast", "power": 4, "toughness": 4 } }]"""));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(b).Zone);
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Beast")); // the beast that fought the big one died
    }

    [Fact]
    public async Task AnAttackCanBeReselected()
    {
        var s = new Scenario();
        var walker = s.Game.SetupPermanent(P1, new CardDefinition { Name = "Sage", ManaCost = Mana.ManaCost.Parse("{2}"), Types = CardType.Planeswalker, Loyalty = 5 });
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        s.Add(P0, Scenario.Creature("Brute", 3, 3));
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Signpost", CardType.Artifact, "{1}", """
            { "abilities": [{ "trigger": "enters", "when": { "step": "DeclareAttackers" }, "targets": [{ "kind": "creature", "controller": "any", "filter": { "attacking": true } }],
                              "effects": [{ "reselectAttack": "target" }] }] }
            """) with { Keywords = new[] { "Flash" } });
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(x => s.Card(x).Name == "Brute").Select(x => new AttackDeclaration(x, P1)).ToList();
        s.Attacker.Option = (_, request) => request.Options.ToList().FindIndex(o => o.StartsWith("Sage"));
        bool cast = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (!cast && view.Step == Step.DeclareAttackers && legal.OfType<CastSpell>().FirstOrDefault() is { } spell) { cast = true; return spell; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(20, LifeOf(s, P1));
        Assert.Equal(2, s.Card(walker).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task DiscoverCastsOrTakesTheFirstCheapEnoughCard()
    {
        var s = new Scenario();
        var big = s.Game.SetupInLibrary(P0, Scripted("Huge", CardType.Creature, "{9}", "{}", 9, 9));
        var small = s.Game.SetupInLibrary(P0, Scripted("Little", CardType.Creature, "{2}", "{}", 2, 2));
        s.Add(P0, Activated("Lode", """[{ "discover": 3 }, { "tokens": "discoverShortfall", "token": "Treasure", "tapped": true }]"""));
        Acting(s, () => s.Restack(P0, big, small));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(small).Zone); // cast free
        Assert.Equal(Zone.Library, s.Card(big).Zone); // to the bottom
        Assert.Equal(1, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Treasure"));
    }

    [Fact]
    public async Task ReadAheadStartsOnTheChosenChapter()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("War Saga", CardType.Enchantment, "{1}", """
            { "readAhead": true, "abilities": [
              { "trigger": "chapter", "chapters": [1], "effects": [{ "gainLife": 1 }] },
              { "trigger": "chapter", "chapters": [2], "effects": [{ "gainLife": 10 }] },
              { "trigger": "chapter", "chapters": [3], "effects": [{ "gainLife": 100 }] }] }
            """, subtypes: new[] { "Saga" }));
        s.Attacker.Number = (_, request) => 2;
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(30, LifeOf(s, P0)); // chapter I was skipped
    }

    [Fact]
    public async Task DiscardsAnyNumberThenDrawsThatMany()
    {
        var s = new Scenario();
        s.Add(P0, Activated("Rummage", """[{ "discardAnyNumber": true }, { "draw": { "discarded": {} } }]"""));
        s.Attacker.Choose = (_, request) => request.Options.Take(3).Select(c => c.Id).ToList();
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Game.Log.OfType<CardDiscarded>().Count(d => d.Player == P0)); // seven cards still: nothing more at cleanup
        Assert.Equal(7 + 3, Drawn(s, P0));
    }

    [Fact]
    public async Task ANamedOptionChosenAsItEntersDecidesItsAbilities()
    {
        foreach (int option in new[] { 0, 1 })
        {
            var s = new Scenario();
            s.Lands(P0, 1);
            s.InHand(P0, Scripted("Siege", CardType.Enchantment, "{1}", """
                { "chooseOnEnter": "option", "enterOptions": ["Khans", "Dragons"], "abilities": [
                  { "trigger": "endStep", "if": { "chosenOption": "Khans" }, "effects": [{ "gainLife": 1 }] },
                  { "trigger": "endStep", "if": { "chosenOption": "Dragons" }, "effects": [{ "gainLife": 10 }] }] }
                """));
            s.Attacker.Option = (_, _) => option;
            Acting(s);
            await s.RunUntilTurn();
            Assert.Equal(option == 0 ? 21 : 30, LifeOf(s, P0));
        }
    }

    [Fact]
    public async Task CastsBigCreaturesFromTheTopWithHaste()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Scripted("Thunder", CardType.Creature, "{4}", """{ "castFromLibraryTop": { "types": ["creature"], "minPower": 4 }, "castFromLibraryTopHaste": true }""", 4, 4));
        var top = s.Game.SetupInLibrary(P0, Scripted("Charger", CardType.Creature, "{2}", "{}", 4, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(x => x == top).Select(x => new AttackDeclaration(x, P1)).ToList();
        Acting(s, () => s.Restack(P0, top));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(x => x == top).Select(x => new AttackDeclaration(x, P1)).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(top).Zone);
        Assert.Equal(16, LifeOf(s, P1)); // it attacked the turn it was cast
    }

    [Fact]
    public async Task PlayableUntilYourNextEndStepThenDamageForCardsStillExiled()
    {
        var s = new Scenario();
        s.Add(P0, Scenario.Creature("Ogre", 4, 4));
        s.Add(P0, Activated("Hawk", """[{ "exileTopPlayable": 2, "chooseOne": false, "untilYourNextEndStep": true, "damagePerStillExiled": 2 }]"""));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(20 - 2 * 2, LifeOf(s, P1));
        Assert.DoesNotContain(s.Game.State.PlayableFromExile, p => p.Player == P0); // the permission ended with that end step
    }
}
