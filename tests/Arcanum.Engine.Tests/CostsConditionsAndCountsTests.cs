// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Filters needing every type, counts (devotion, half rounded down, graveyards, players who drew, opponents with more),
/// conditions, cost filters, combat damage prevention, early-turn and graveyard casting, and nontoken exile replacement.
/// </summary>
public partial class ScriptedVocabularyTests
{
    private static int LifeOf(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    private static int Drawn(Scenario s, PlayerId p) => s.Game.Log.OfType<Events.CardDrawn>().Count(d => d.Player == p);

    private static CardDefinition Activated(string name, string effects, string cost = "{0}", CardType type = CardType.Artifact, string extra = "") =>
        Scripted(name, type, "{1}", $$"""{ "abilities": [{ "cost": "{{cost}}", {{extra}} "effects": {{effects}} }] }""");

    [Fact]
    public async Task AllTypesFilterNeedsEveryType()
    {
        var s = new Scenario();
        var overseer = s.Add(P0, Activated("Overseer", """[{ "counters": 1, "what": { "each": { "allTypes": ["artifact", "creature"], "controller": "you" } } }]""", "{T}",
            CardType.Artifact | CardType.Creature) with { Power = 1, Toughness = 1 });
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        var trinket = s.Add(P0, Activated("Trinket", """[{ "gainLife": 0 }]""", "{5}"));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(overseer).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(trinket).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task EnteringCreatureGivesLifeEqualToItsToughness()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Avatar", """
            { "abilities": [{ "trigger": "creatureEnters", "filter": { "types": ["creature"], "controller": "you" }, "effects": [{ "gainLife": "triggeredToughness" }] }] }
            """, 5, 5));
        s.InHand(P0, Scenario.Creature("Wall", 1, 4));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(24, LifeOf(s, P0));
    }

    [Fact]
    public async Task HalfRoundedDownAndDevotion()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Shade", CardType.Creature, "{B}{B}", "{}", 1, 1));
        s.Add(P0, Scripted("Imp", CardType.Creature, "{1}{B/R}", "{}", 1, 1));
        s.Add(P0, Scripted("Ogre", CardType.Creature, "{2}{R}", "{}", 1, 1));
        s.Add(P0, Activated("Font", """[{ "gainLife": { "halfDown": { "count": { "types": ["creature"] } } } }, { "gainLife": { "devotion": "B" } }]"""));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(20 + 1 + 3, LifeOf(s, P0)); // three creatures halved and rounded down; {B}{B} and {B/R}
    }

    [Fact]
    public async Task GraveyardCountsOfEveryoneAndOfOpponents()
    {
        var s = new Scenario();
        var mine = s.Add(P0, Scenario.Creature("Mine", 1, 1));
        var theirs = s.Add(P1, Scenario.Creature("Theirs", 1, 1));
        var theirs2 = s.Add(P1, Scenario.Creature("Theirs Too", 1, 1));
        s.Add(P0, Activated("Ledger", """
            [{ "gainLife": { "graveyard": { "types": ["creature"] }, "of": "everyone" } }, { "loseLife": { "graveyard": { "types": ["creature"] }, "of": "opponents" }, "who": "opponents" }]
            """));
        Acting(s, () => { ToGraveyard(s, mine); ToGraveyard(s, theirs); ToGraveyard(s, theirs2); });
        await s.RunUntilTurn();
        Assert.Equal(23, LifeOf(s, P0));
        Assert.Equal(18, LifeOf(s, P1));
    }

    [Fact]
    public async Task StaticCombatDamagePreventionLeavesOtherDamage()
    {
        var s = new Scenario();
        var seraph = s.Add(P0, Creature("Seraph", """{ "abilities": [{ "static": { "affects": "self", "preventDamage": "combat" } }] }""", 3, 3));
        s.Add(P1, Scenario.Creature("Giant", 5, 5));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(seraph).Zone);
        Assert.Equal(0, s.Card(seraph).Damage);
    }

    [Fact]
    public async Task CantBeCastDuringItsOwnersFirstTurns()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var avenger = s.InHand(P0, Scripted("Avenger", CardType.Creature, "{1}", """{ "cantCastDuringFirstTurns": 3 }""", 3, 3));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(avenger).Zone);
    }

    [Fact]
    public async Task ConditionsOnDamageNamesManaValuesAndOpponentsPermanents()
    {
        var s = new Scenario();
        s.Lands(P0, 3); // three Mountains
        s.Add(P0, Scripted("Relic", CardType.Artifact, "{4}", "{}"));
        s.Add(P1, Scripted("Bauble", CardType.Artifact, "{1}", "{}"));
        s.Add(P0, Activated("Judge", """
            [{ "damage": 7, "to": "opponents" },
             { "if": { "opponentDealtDamage": 7 }, "then": [{ "gainLife": 1 }] },
             { "if": { "sameName": { "types": ["land"] }, "count": 3 }, "then": [{ "gainLife": 10 }] },
             { "if": { "greatestManaValue": { "types": ["artifact"] } }, "then": [{ "gainLife": 100 }] },
             { "if": { "opponentControls": { "types": ["artifact"] }, "count": 2 }, "then": [{ "gainLife": 1000 }] }]
            """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(13, LifeOf(s, P1));
        Assert.Equal(20 + 1 + 10 + 100, LifeOf(s, P0)); // the opponent controls only one artifact
    }

    [Fact]
    public async Task EnchantedCreatureUntapsOnlyWhileItsControllerIsTheMonarch()
    {
        foreach (bool monarch in new[] { false, true })
        {
            var s = new Scenario();
            var victim = s.Add(P1, Scenario.Creature("Victim", 2, 2));
            var aura = s.Add(P0, Scripted("Disfavor", CardType.Enchantment, "{1}", """
                { "aura": "creature", "abilities": [{ "static": { "affects": "enchanted", "keywords": ["Doesn't untap"], "while": { "not": "attachedControllerIsMonarch" } } }] }
                """));
            s.Card(aura).AttachedTo = victim;
            s.Card(victim).Tapped = true;
            if (monarch) s.Game.State.Monarch = P1;
            Acting(s);
            await s.RunUntilTurn(3);
            Assert.Equal(!monarch, s.Card(victim).Tapped);
        }
    }

    [Fact]
    public async Task CommanderColorsLifeCostIsZeroWithoutACommander()
    {
        var s = new Scenario();
        s.Add(P0, Activated("Room", """[{ "draw": 1 }]""", "{0}, life:commanderColors", CardType.Land));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(20, LifeOf(s, P0));
        Assert.Equal(7 + 1, Drawn(s, P0)); // the opening hand, then this
    }

    [Fact]
    public async Task DrawsForEachOpponentWhoDrewAndCountsOpponentsWithMoreCards()
    {
        var s = new Scenario();
        s.Add(P0, Activated("Deal", """
            [{ "draw": 1, "who": "opponents" }, { "draw": "playersWhoDrewThisWay" }, { "repeat": { "opponentsWithMore": "cards" }, "effects": [{ "gainLife": 1 }] }]
            """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(7 + 1, Drawn(s, P0)); // the opening hand, then this
        Assert.Equal(7 + 1, Drawn(s, P1));
        Assert.Equal(20, LifeOf(s, P0)); // both hold eight cards as the repeat is counted

    }

    [Fact]
    public async Task CastableFromTheGraveyardWhileTheConditionHolds()
    {
        foreach (bool zombie in new[] { false, true })
        {
            var s = new Scenario();
            s.Lands(P0, 1);
            if (zombie) s.Add(P0, Scripted("Ghoul", CardType.Creature, "{1}", "{}", 1, 1, new[] { "Zombie" }));
            var crawler = s.InHand(P0, Scripted("Crawler", CardType.Creature, "{1}", """{ "graveyardCastIf": { "control": { "subtype": "Zombie" } } }""", 2, 1));
            Acting(s, () => ToGraveyard(s, crawler));
            await s.RunUntilTurn();
            Assert.Equal(zombie ? Zone.Battlefield : Zone.Graveyard, s.Card(crawler).Zone);
        }
    }

    [Fact]
    public async Task SacrificeAndTapCostsTakeFilters()
    {
        var s = new Scenario();
        var green = s.Add(P0, Scripted("Green Bear", CardType.Creature, "{G}", "{}", 2, 2, colors: new[] { "G" }));
        var black = s.Add(P0, Scripted("Black Bear", CardType.Creature, "{B}", "{}", 2, 2, colors: new[] { "B" }));
        s.Add(P0, Activated("Altar", """[{ "gainLife": 1 }]""", "sacrifice:creature", CardType.Artifact, """ "sacrificeFilter": { "types": ["creature"], "colors": ["B"] }, """));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(green).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(black).Zone);

        var t = new Scenario();
        var breaker = t.Add(P0, Scripted("Breaker", CardType.Creature, "{B}", """
            { "abilities": [{ "cost": "tapPermanents:3:creature", "tapFilter": { "types": ["creature"], "subtype": "Zombie" }, "effects": [{ "gainLife": 5 }] }] }
            """, 1, 1, new[] { "Zombie" }));
        var z1 = t.Add(P0, Scripted("Zombie A", CardType.Creature, "{1}", "{}", 2, 2, new[] { "Zombie" }));
        var z2 = t.Add(P0, Scripted("Zombie B", CardType.Creature, "{1}", "{}", 2, 2, new[] { "Zombie" }));
        var other = t.Add(P0, Scenario.Creature("Not a Zombie", 2, 2));
        Acting(t);
        await t.RunUntilTurn();
        Assert.Equal(25, LifeOf(t, P0));
        Assert.True(t.Card(breaker).Tapped && t.Card(z1).Tapped && t.Card(z2).Tapped);
        Assert.False(t.Card(other).Tapped);
    }

    [Fact]
    public async Task OnlyNontokenCreaturesAreExiledInsteadOfDying()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Warden", """
            { "replaces": ["OpponentsNontokenCreaturesExiledInsteadOfDying"],
              "abilities": [{ "trigger": "creatureExiledInstead", "effects": [{ "gainLife": 1 }] }] }
            """, 3, 4));
        var card = s.Add(P1, Scenario.Creature("Card Bear", 2, 2));
        var token = s.Add(P1, Scenario.Creature("Token Bear", 2, 2) with { IsToken = true });
        s.Add(P0, Activated("Wrath", """[{ "destroy": { "each": { "types": ["creature"], "controller": "opponent" } } }]"""));
        Acting(s);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(card).Zone);
        Assert.NotEqual(Zone.Exile, s.Card(token).Zone);
        Assert.Equal(21, LifeOf(s, P0));
    }
}
