// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Endure N (counters or an N/N Spirit token), mobilize N (attacking Warrior tokens sacrificed at the end step) and harmonize (cast from the
/// graveyard, a creature tapped to pay less, then exiled). The cards are invented.
/// </summary>
public class EndureMobilizeTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"endure-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private const string EntersEndures = """{ "abilities": [{ "trigger": "enters", "effects": [{ "endure": 2 }], "text": "When this creature enters, it endures 2." }] }""";

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnduringPutsTheCountersOrMakesASpiritTokenAsItsControllerChooses(bool counters)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var guard = s.InHand(P0, Make("Hill Guard", "Creature — Soldier", EntersEndures, "{2}", "1/1", "When this creature enters, it endures 2.", "Endure"));
        CastFirst(s, guard);
        s.Attacker.YesNo = (_, _) => counters;
        await s.RunUntilTurn();
        Assert.Equal(counters ? 2 : 0, s.Card(guard).CounterCount(CounterKind.PlusOnePlusOne));
        var spirits = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Spirit").ToList();
        if (counters) Assert.Empty(spirits);
        else
        {
            var spirit = Assert.Single(spirits);
            Assert.Equal((2, 2), (spirit.Power, spirit.Toughness));
            Assert.Equal(P0, spirit.Controller);
            Assert.Contains("W", spirit.Definition.ColorList);
        }
    }

    [Fact]
    public async Task APermanentThatLeftTheBattlefieldStillGivesItsControllerTheToken()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var husk = s.Add(P0, Make("Old Husk", "Creature — Spirit", """
            { "abilities": [
              { "cost": "{1}, sacrifice", "effects": [{ "gainLife": 1 }], "text": "{1}, Sacrifice this creature: You gain 1 life." },
              { "trigger": "dies", "effects": [{ "endure": 3 }], "text": "When this creature dies, it endures 3." } ] }
            """, "{2}", "1/1", "{1}, Sacrifice this creature: You gain 1 life.\nWhen this creature dies, it endures 3.", "Endure"));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == husk) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.YesNo = (_, _) => throw new InvalidOperationException("No counter choice for a permanent that isn't on the battlefield.");
        await s.RunUntilTurn();
        var spirit = Assert.Single(s.Game.State.Battlefield.Select(s.Card), c => c.Name == "Spirit");
        Assert.Equal((3, 3), (spirit.Power, spirit.Toughness));
    }

    [Fact]
    public async Task EnduringZeroDoesNothing()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var guard = s.InHand(P0, Make("Empty Guard", "Creature — Soldier",
            """{ "abilities": [{ "trigger": "enters", "effects": [{ "endure": 0 }], "text": "When this creature enters, it endures X." }] }""", "{2}", "1/1", "When this creature enters, it endures X.", "Endure"));
        CastFirst(s, guard);
        s.Attacker.YesNo = (_, _) => throw new InvalidOperationException("Endure 0 asks nothing.");
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(guard).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.DoesNotContain(s.Game.State.Battlefield, id => s.Card(id).Name == "Spirit");
    }

    [Fact]
    public async Task MobilizeMakesTappedAttackingWarriorsThatAreSacrificedAtTheEndStep()
    {
        var s = new Scenario();
        var packbeast = s.Add(P0, Make("Caravan Ox", "Creature — Ox", "{}", "{2}{W}", "2/2", "Mobilize 2", "Mobilize"));
        Assert.Equal(CardSupport.Full, CardFactory.Create(OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = "mobilize-support", ["name"] = "Caravan Ox", ["layout"] = "normal", ["mana_cost"] = "{2}{W}", ["type_line"] = "Creature — Ox",
            ["oracle_text"] = "Mobilize 2", ["power"] = "2", ["toughness"] = "2", ["keywords"] = new[] { "Mobilize" },
        })))).First(), null).Support);
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int warriors = 0;
        s.Game.EventRaised += _ => warriors = Math.Max(warriors, s.Game.State.Combat?.Attacks.Count(a => s.Card(a.Attacker).Name == "Warrior" && s.Card(a.Attacker).Tapped) ?? 0);
        await s.RunUntilTurn();
        Assert.Equal(2, warriors); // tapped and attacking
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life); // 2 from the ox, 1 from each Warrior
        Assert.DoesNotContain(s.Game.State.Battlefield, id => s.Card(id).Name == "Warrior"); // sacrificed at the end step
        Assert.Contains(packbeast, s.Game.State.Battlefield);
    }

    private static CardId HarmonizeInGraveyard(Scenario s)
    {
        var song = s.InHand(P0, Make("Ember Song", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 3 }] } }""", "{R}", null,
            "You gain 3 life.\nHarmonize {4}{R}", "Harmonize"));
        var player = s.Game.State.GetPlayer(P0);
        player.Hand.Remove(song);
        player.Graveyard.Add(song);
        s.Card(song).Zone = Zone.Graveyard;
        return song;
    }

    [Fact]
    public async Task HarmonizeCastsFromTheGraveyardTappingACreatureForItsPowerThenExiles()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var song = HarmonizeInGraveyard(s);
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        s.Add(P0, Creature("Mouse", 1, 1));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == song) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Choose = (_, request) => request.Options.Any(o => o.Id == ox) ? new[] { ox } : request.Options.Take(request.Min).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.True(done);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // {4}{R} less 3 is {1}{R}: the two lands
        Assert.True(s.Card(ox).Tapped);
        Assert.Equal(Zone.Exile, s.Card(song).Zone);
    }

    [Fact]
    public async Task HarmonizeTappingIsOptionalAndTooLittleManaMeansItCantBeCast()
    {
        var s = new Scenario();
        s.Lands(P0, 5);
        var song = HarmonizeInGraveyard(s);
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == song) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Min).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
        Assert.False(s.Card(ox).Tapped);

        var poor = new Scenario();
        poor.Lands(P0, 1);
        var other = HarmonizeInGraveyard(poor);
        poor.Add(P0, Creature("Ox", 3, 3));
        bool offered = false;
        poor.Attacker.Act = (_, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == other); return PassPriority.Instance; };
        poor.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await poor.RunUntilTurn();
        Assert.False(offered); // {1}{R} after the Ox, one land
    }
}
