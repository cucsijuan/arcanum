// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Station (rule 702.184) and station symbols (rule 721): tapping another creature puts charge counters equal to its power on the
/// permanent; at N or more it has the symbol's abilities and, with a power/toughness box, is a creature. The cards are invented.
/// </summary>
public class StationTests
{
    private static int _next;

    private const string ShipOracle = "Station (Tap another creature you control: Put charge counters equal to its power on this Spacecraft. Station only as a sorcery. It's an artifact creature at 5+.)\n5+ | Flying";

    private const string ShipScript = """
        { "abilities": [
          { "static": { "affects": "self", "while": { "counters": 5, "kind": "charge" }, "keywords": ["Flying"], "addTypes": ["creature"], "setPower": 4, "setToughness": 5 },
            "text": "5+ | Flying" } ] }
        """;

    private static CardDefinition Make(string name, string typeLine, string script, string oracle, string? pt = null, params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"station-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = "{3}", ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var (definition, support) = CardFactory.Create(record, CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, support);
        return definition;
    }

    private static CardDefinition Ship() => Make("Test Cutter", "Artifact — Spacecraft", ShipScript, ShipOracle, "4/5", "Station");

    /// <summary>Activates the station ability of <paramref name="ship"/> each time a creature in <paramref name="tapOrder"/> is still to be tapped.</summary>
    private static void StationWith(Scenario s, CardId ship, params CardId[] tapOrder)
    {
        int next = 0;
        s.Attacker.Act = (_, legal) =>
            next < tapOrder.Length && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ship) is { } a ? a : PassPriority.Instance;
        s.Attacker.Choose = (_, request) =>
        {
            var pick = tapOrder[Math.Min(next, tapOrder.Length - 1)];
            next++;
            return new[] { pick };
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    [Fact]
    public void AStationCardHasNoPowerOrToughnessOfItsOwnAndHasTheStationAbilityBeforeItsSymbols()
    {
        var ship = Ship();
        Assert.Null(ship.Power);
        Assert.Null(ship.Toughness);
        Assert.StartsWith("Station", ship.Abilities[0].Text);
        Assert.IsType<StaticAbility>(ship.Abilities[1]);
    }

    [Fact]
    public async Task StationPutsChargeCountersEqualToTheTappedCreaturesPowerAndBelowTheThresholdItIsNoCreature()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Ship());
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        StationWith(s, ship, ox);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(ship).CounterCount(CounterKind.Charge));
        Assert.True(s.Card(ox).Tapped);
        Assert.False(s.Card(ship).IsCreature);
        Assert.False(s.Card(ship).Has(Keyword.Flying));
    }

    [Fact]
    public async Task AtTheThresholdItIsACreatureWithTheBoxsPowerAndToughnessAndTheSymbolsAbilities()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Ship());
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        StationWith(s, ship, ox, bear);
        await s.RunUntilTurn();
        var card = s.Card(ship);
        Assert.Equal(5, card.CounterCount(CounterKind.Charge));
        Assert.True(card.IsCreature);
        Assert.True(card.Types.HasFlag(CardType.Artifact));
        Assert.Equal(4, card.Power);
        Assert.Equal(5, card.Toughness);
        Assert.True(card.Has(Keyword.Flying));
    }

    [Fact]
    public async Task StationNeedsAnotherUntappedCreature()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Ship());
        s.Add(P1, Creature("Their Ox", 3, 3)); // an opponent's creature can't be tapped for it
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => a.Source == ship);
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task ACreatureWithZeroPowerAddsNoCounters()
    {
        var s = new Scenario();
        var ship = s.Add(P0, Ship());
        var wall = s.Add(P0, Creature("Wall", 0, 4));
        StationWith(s, ship, wall);
        await s.RunUntilTurn();
        Assert.True(s.Card(wall).Tapped);
        Assert.Equal(0, s.Card(ship).CounterCount(CounterKind.Charge));
    }

    [Fact]
    public async Task APlanetsSymbolGrantsItsActivatedAbilityOnlyAtTheThreshold()
    {
        var planet = Make("Test World", "Land — Planet", """
            { "abilities": [
              { "static": { "affects": "self", "while": { "counters": 2, "kind": "charge" }, "grants": [ { "cost": "{T}", "effects": [{ "gainLife": 4 }], "text": "{T}: You gain 4 life." } ] },
                "text": "2+ | {T}: You gain 4 life." } ] }
            """, "Station (Tap another creature you control: Put charge counters equal to its power on this Planet. Station only as a sorcery.)\n2+ | {T}: You gain 4 life.", null, "Station");
        var s = new Scenario();
        var world = s.Add(P0, planet);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            var mine = legal.OfType<ActivateAbility>().Where(a => a.Source == world).ToList();
            if (step == 0) Assert.True(mine.Count <= 1); // only station before the counters
            if (step == 0 && mine.Count > 0) { step = 1; return mine[0]; } // station (the Bear is then tapped: no other station)
            if (step == 1 && mine.Count > 0 && s.Card(world).CounterCount(CounterKind.Charge) == 2) { step = 2; return mine[0]; } // the granted ability
            return PassPriority.Instance;
        };
        s.Attacker.Choose = (_, _) => new[] { bear };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(world).CounterCount(CounterKind.Charge));
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
        Assert.False(s.Card(world).IsCreature);
    }
}

/// <summary>Sunburst (rule 702.44): counters for each color of mana spent to cast it. The cards are invented.</summary>
public class SunburstTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string? pt)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"sunburst-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = "{3}", ["type_line"] = typeLine,
            ["oracle_text"] = "Sunburst (This enters with a +1/+1 counter on it for each color of mana spent to cast it.)",
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = new[] { "Sunburst" },
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var (definition, support) = CardFactory.Create(record, null);
        Assert.Equal(CardSupport.Full, support);
        return definition;
    }

    [Theory]
    [InlineData("Artifact Creature — Construct", "0/0", true)]
    [InlineData("Artifact", null, false)]
    public async Task ItEntersWithACounterForEachColorSpentPlusOnePlusOneOnACreatureChargeOtherwise(string typeLine, string? pt, bool creature)
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Mountain);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        var prism = s.InHand(P0, Make("Test Prism", typeLine, pt));
        bool cast = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (cast || legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == prism) is not { } spell) return PassPriority.Instance;
            cast = true;
            return spell;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        var card = s.Card(prism);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal(creature ? 2 : 0, card.CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(creature ? 0 : 2, card.CounterCount(CounterKind.Charge));
    }

    [Fact]
    public void PutOntoTheBattlefieldWithoutBeingCastItGetsNoCounters()
    {
        var s = new Scenario();
        var prism = s.Add(P0, Make("Test Prism", "Artifact", null));
        Assert.Equal(0, s.Card(prism).CounterCount(CounterKind.Charge));
    }
}
