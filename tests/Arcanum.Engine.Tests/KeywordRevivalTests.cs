// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Decayed, flanking, battle cry, mentor, riot, fabricate, eternalize and embalm, suspend from the hand, dredge and demonstrate. The cards are invented.
/// </summary>
public class KeywordRevivalTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"revival-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
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

    /// <summary>Plays lands only, so the hand doesn't overflow at cleanup and spells stay in hand.</summary>
    private static PlayerAction PlayLandsOnly(GameView view, IReadOnlyList<PlayerAction> legal) =>
        legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;

    private static CardId InGraveyard(Scenario s, CardDefinition card)
    {
        var id = s.InHand(P0, card);
        var player = s.Game.State.GetPlayer(P0);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    [Fact]
    public async Task DecayedAttackerDealsItsDamageThenIsSacrificedAtEndOfCombat()
    {
        var s = new Scenario();
        var walker = s.Add(P0, Make("Rot Walker", "Creature — Zombie", "{}", "{1}{B}", "2/2", "Decayed", "Decayed"));
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Graveyard, s.Card(walker).Zone);
    }

    [Fact]
    public async Task DecayedCreatureCantBlock()
    {
        var s = new Scenario();
        s.Add(P0, Make("Plain Brute", "Creature — Ogre", "{}", "{1}", "2/2"));
        var walker = s.Add(P1, Make("Rot Walker", "Creature — Zombie", "{}", "{1}{B}", "3/3", "Decayed", "Decayed"));
        s.Add(P1, Make("Plain Guard", "Creature — Soldier", "{}", "{1}", "1/1"));
        IReadOnlyList<CardId>? offered = null;
        s.Defender.Block = (_, blockers, attackers) =>
        {
            offered = blockers;
            return new[] { new BlockDeclaration(walker, attackers[0]) };
        };
        await Assert.ThrowsAsync<InvalidDecisionException>(() => s.RunUntilTurn()); // the block is refused
        Assert.NotNull(offered);
        Assert.DoesNotContain(walker, offered!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlankingGivesABlockerWithoutFlankingMinusOneMinusOne(bool blockerFlanks)
    {
        var s = new Scenario();
        var ridge = s.Add(P0, Make("Ridge Runner", "Creature — Soldier", "{}", "{1}{W}", "2/2", "Flanking", "Flanking"));
        var blocker = s.Add(P1, blockerFlanks
            ? Make("Ridge Guard", "Creature — Soldier", "{}", "{1}{W}", "2/2", "Flanking", "Flanking")
            : Make("Plain Brute", "Creature — Ogre", "{}", "{1}", "2/2"));
        s.Defender.Block = (_, _, attackers) => new[] { new BlockDeclaration(blocker, attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(blocker).Zone); // 1/1 after -1/-1, or 2/2 with flanking: 2 damage either way
        Assert.Equal(blockerFlanks ? Zone.Graveyard : Zone.Battlefield, s.Card(ridge).Zone);
    }

    [Fact]
    public async Task BattleCryGivesEachOtherAttackingCreaturePlusOneZero()
    {
        var s = new Scenario();
        s.Add(P0, Make("War Cry Hound", "Creature — Dog", "{}", "{R}", "1/1", "Battle cry", "Battle cry"));
        s.Add(P0, Make("Plain Brute", "Creature — Ogre", "{}", "{1}", "2/2"));
        await s.RunUntilTurn();
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life); // 1 from the hound, 3 from the brute with +1/+0
    }

    [Fact]
    public async Task MentorPutsACounterOnAnAttackingCreatureWithLesserPower()
    {
        var s = new Scenario();
        var ram = s.Add(P0, Make("Mentor Ram", "Creature — Ram", "{}", "{2}{W}", "3/3", "Mentor", "Mentor"));
        var pup = s.Add(P0, Make("Young Pup", "Creature — Dog", "{}", "{W}", "1/1"));
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(pup).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(ram).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RiotLetsItsControllerChooseACounterOrHaste(bool counter)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var riot = s.InHand(P0, Make("Rush Hound", "Creature — Dog", "{}", "{2}", "2/2", "Riot", "Riot"));
        bool done = false;
        bool hasted = false;
        s.Attacker.Act = (_, legal) =>
        {
            hasted |= s.Card(riot).Zone == Zone.Battlefield && s.Card(riot).Has(Keyword.Haste);
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == riot) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.YesNo = (_, _) => counter;
        await s.RunUntilTurn();
        Assert.True(done);
        Assert.Equal(counter ? 1 : 0, s.Card(riot).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(!counter, hasted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FabricateTwoPutsCountersOrMakesTwoServos(bool counters)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var smith = s.InHand(P0, Make("Tin Smith", "Creature — Artificer", "{}", "{2}", "1/1", "Fabricate 2", "Fabricate"));
        CastFirst(s, smith);
        s.Attacker.YesNo = (_, _) => counters;
        await s.RunUntilTurn();
        Assert.Equal(counters ? 2 : 0, s.Card(smith).CounterCount(CounterKind.PlusOnePlusOne));
        var servos = s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Servo").ToList();
        if (counters) Assert.Empty(servos);
        else
        {
            Assert.Equal(2, servos.Count);
            Assert.All(servos, c =>
            {
                Assert.True(c.Is(CardType.Artifact));
                Assert.True(c.Is(CardType.Creature));
                Assert.Equal((1, 1), (c.Power, c.Toughness));
            });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EternalizeAndEmbalmMakeATokenCopyFromTheGraveyard(bool eternalize)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        string keyword = eternalize ? "Eternalize" : "Embalm";
        var sentinel = InGraveyard(s, Make("Old Sentinel", "Creature — Construct", "{}", "{3}", "2/1", keyword + " {2}", keyword));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == sentinel) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(done);
        Assert.Equal(Zone.Exile, s.Card(sentinel).Zone);
        var token = Assert.Single(s.Game.State.Battlefield.Select(s.Card).Where(c => c.Name == "Old Sentinel"));
        Assert.Equal(eternalize ? (4, 4) : (2, 1), (token.Power, token.Toughness));
        Assert.Equal(new[] { eternalize ? "B" : "W" }, token.Definition.ColorList);
        Assert.True(token.HasSubtype("Zombie"));
        Assert.Equal(0, token.Definition.ManaCost.ManaValue);
    }

    [Fact]
    public async Task SuspendFromTheHandExilesTheCardWithTimeCountersThenCastsItFreeWhenTheLastIsRemoved()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var far = s.InHand(P0, Make("Far Vision", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 3 }] } }""", "", null, "Suspend 2—{R}\nYou gain 3 life.", "Suspend"));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == far) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.YesNo = (_, _) => true;
        Zone zoneAtTurnTwo = Zone.Hand;
        int timeCountersAtTurnTwo = -1;
        bool suspendedAtTurnTwo = false;
        s.Game.EventRaised += e =>
        {
            if (e is TurnBegan t && t.TurnNumber == 2)
            {
                zoneAtTurnTwo = s.Card(far).Zone;
                timeCountersAtTurnTwo = s.Card(far).CounterCount(CounterKind.Time);
                suspendedAtTurnTwo = s.Card(far).Suspended;
            }
        };
        await s.RunUntilTurn(6); // turn 3 and turn 5 are P0's: one counter comes off each upkeep, the last one casts it
        Assert.True(done);
        Assert.Equal(Zone.Exile, zoneAtTurnTwo);
        Assert.Equal(2, timeCountersAtTurnTwo);
        Assert.True(suspendedAtTurnTwo);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Graveyard, s.Card(far).Zone);
    }

    [Fact]
    public async Task DredgeReturnsTheCardToHandInsteadOfDrawingAndMillsThatMany()
    {
        var s = new Scenario();
        var moss = InGraveyard(s, Make("Grave Moss", "Sorcery", """{ "spell": { "effects": [] } }""", "{G}", null, "Dredge 2", "Dredge"));
        s.Attacker.Act = PlayLandsOnly;
        s.Attacker.Option = (_, request) => request.Options.Any(o => o.StartsWith("Dredge", StringComparison.Ordinal)) ? 1 : 0;
        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Hand, s.Card(moss).Zone);
        Assert.Equal(2, s.Game.State.GetPlayer(P0).Graveyard.Count); // the two milled cards; the dredged card left the graveyard
    }

    [Fact]
    public async Task DecliningDredgeDrawsAsUsualAndTheCardStaysInTheGraveyard()
    {
        var s = new Scenario();
        var moss = InGraveyard(s, Make("Grave Moss", "Sorcery", """{ "spell": { "effects": [] } }""", "{G}", null, "Dredge 2", "Dredge"));
        s.Attacker.Act = PlayLandsOnly;
        s.Attacker.Option = (_, _) => 0;
        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Graveyard, s.Card(moss).Zone);
        Assert.Single(s.Game.State.GetPlayer(P0).Graveyard);
    }

    [Fact]
    public async Task DemonstrateCopiesTheSpellForTheCasterAndAnOpponentWhoKeepTheTargets()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var spark = s.InHand(P0, Make("Twin Spark", "Instant", """{ "spell": { "targets": ["player"], "effects": [{ "damage": 2, "to": "target" }] } }""", "{R}", null,
            "Twin Spark deals 2 damage to target player.\nDemonstrate", "Demonstrate"));
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spark) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, _) => new[] { new Target(null, P1) };
        s.Attacker.YesNo = (_, request) => request.Prompt.StartsWith("Demonstrate", StringComparison.Ordinal); // copy it; no new targets
        s.Defender.YesNo = (_, _) => false; // the opponent's copy keeps its target
        await s.RunUntilTurn();
        Assert.True(done);
        Assert.Equal(14, s.Game.State.GetPlayer(P1).Life); // the original, P0's copy and P1's copy: 2 each
    }
}
