// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
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
/// Increment (a +1/+1 counter when the mana spent on a spell you cast is greater than the creature's power or toughness) and
/// paradigm (the spell is exiled; after the first one with its name resolves, a free copy each first main phase). The cards are invented.
/// </summary>
public class IncrementParadigmTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"increment-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, script.Length == 0 ? null : CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    private const string IncrementText = "Increment (Whenever you cast a spell, if the amount of mana you spent is greater than this creature's power or toughness, put a +1/+1 counter on this creature.)";

    private static CardDefinition Incrementer(string pt, string oracle = IncrementText) =>
        Make("Lattice Grower", "Creature — Insect", "", "{1}{R}", pt, oracle, "Increment");

    private static CardDefinition Spark(string cost) =>
        Make("Ember Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", cost, null, "You gain 1 life.");

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
    [InlineData("1/4", "{1}{R}", 1)] // 2 mana > power 1
    [InlineData("4/1", "{1}{R}", 1)] // 2 mana > toughness 1
    [InlineData("2/2", "{1}{R}", 0)] // 2 mana is not greater than 2
    [InlineData("2/3", "{2}{R}", 1)] // 3 mana > power 2
    public async Task IncrementPutsACounterWhenTheManaSpentIsGreaterThanPowerOrToughness(string pt, string cost, int counters)
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var grower = s.Add(P0, Incrementer(pt));
        CastFirst(s, s.InHand(P0, Spark(cost)));
        await s.RunUntilTurn();
        Assert.Equal(counters, s.Card(grower).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task IncrementDoesNotTriggerForAnOpponentsSpell()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var grower = s.Add(P1, Incrementer("1/1"));
        CastFirst(s, s.InHand(P0, Spark("{2}{R}")));
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(grower).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task EachInstanceOfIncrementTriggersSeparately()
    {
        // Two instances on a 1/1: both trigger; the second checks again on resolution, when the creature is 2/2 and 3 mana is still greater.
        var s = new Scenario();
        s.Lands(P0, 3);
        var grower = s.Add(P0, Make("Lattice Grower", "Creature — Insect", "", "{1}{R}", "1/1", "Increment, increment", "Increment"));
        CastFirst(s, s.InHand(P0, Spark("{2}{R}")));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(grower).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task IncrementChecksAgainOnResolution()
    {
        // Two instances on a 1/2 with a 2-mana spell: both trigger (2 > 1); after the first counter it is 2/3, so the second does nothing.
        var s = new Scenario();
        s.Lands(P0, 2);
        var grower = s.Add(P0, Make("Lattice Grower", "Creature — Insect", "", "{1}{R}", "1/2", "Increment, increment", "Increment"));
        CastFirst(s, s.InHand(P0, Spark("{1}{R}")));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(grower).CounterCount(CounterKind.PlusOnePlusOne));
    }

    private const string ParadigmText = "You gain 2 life.\nParadigm (Then exile this spell. After you first resolve a spell with this name, you may cast a copy of it from exile without paying its mana cost at the beginning of each of your first main phases.)";

    private static CardDefinition Lesson() =>
        Make("Quiet Seminar", "Sorcery — Lesson", """{ "spell": { "effects": [{ "gainLife": 2 }] } }""", "{R}", null, ParadigmText, "Paradigm");

    private static void CastAll(Scenario s, params CardId[] cards)
    {
        var left = cards.ToList();
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => left.Contains(c.Card)) is { } a) { left.Remove(a.Card); return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    [Fact]
    public async Task ParadigmExilesTheSpellAndCastsAFreeCopyAtEachOfYourPrecombatMainPhases()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var lesson = s.InHand(P0, Lesson());
        CastAll(s, lesson);
        var life = new Dictionary<int, int>();
        var exiled = new Dictionary<int, int>();
        s.Game.EventRaised += e =>
        {
            if (e is not TurnBegan t) return;
            life[t.TurnNumber] = s.Game.State.GetPlayer(P0).Life;
            exiled[t.TurnNumber] = s.Game.State.GetPlayer(P0).Exile.Count;
        };
        await s.RunUntilTurn(6);
        Assert.Equal(22, life[2]);
        Assert.Equal(Zone.Exile, s.Card(lesson).Zone); // exiled, not put into the graveyard
        Assert.Equal(24, life[4]); // the copy cast at the beginning of turn 3's precombat main phase
        Assert.Equal(1, exiled[4]); // the copy was exiled as it resolved and ceased to exist
        Assert.Equal(26, life[6]); // and again on turn 5
    }

    [Fact]
    public async Task OnlyTheFirstSpellWithThatNameToResolveMakesTheDelayedAbility()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        CastAll(s, s.InHand(P0, Lesson()), s.InHand(P0, Lesson()));
        await s.RunUntilTurn();
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
        await s.RunUntilTurn(4);
        Assert.Equal(26, s.Game.State.GetPlayer(P0).Life); // one copy, not two
    }

    [Fact]
    public async Task DecliningTheCopyKeepsTheAbilityForLaterTurns()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        CastAll(s, s.InHand(P0, Lesson()));
        int asked = 0;
        s.Attacker.YesNo = (_, r) => !r.Prompt.StartsWith("Cast the copy") || ++asked > 1;
        await s.RunUntilTurn(6);
        Assert.Equal(2, asked);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life); // declined on turn 3, cast on turn 5
        Assert.Single(s.Game.State.GetPlayer(P0).Exile); // the declined copy ceased to exist
    }

    [Fact]
    public async Task AnOpponentsSpellWithTheSameNameDoesNotCount()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        CastAll(s, s.InHand(P0, Lesson()));
        s.Game.State.ResolvedSpellNames.Add((P1, "Quiet Seminar"));
        await s.RunUntilTurn(4);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
    }
}
