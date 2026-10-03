// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

public class KeywordTests
{
    private static Func<Views.GameView, IReadOnlyList<CardId>, IReadOnlyList<CardId>, IReadOnlyList<BlockDeclaration>> BlockWith(
        params (CardId Blocker, CardId Attacker)[] blocks) =>
        (_, _, _) => blocks.Select(b => new BlockDeclaration(b.Blocker, b.Attacker)).ToList();

    [Fact]
    public async Task FlyingCanOnlyBeBlockedByFlyingOrReach()
    {
        var s = new Scenario();
        var bird = s.Add(P0, Creature("Bird", 2, 2, Keyword.Flying));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var spider = s.Add(P1, Creature("Spider", 1, 3, Keyword.Reach));
        await s.RunUntilTurn();

        var request = s.Defender.LastBlockRequest!;
        Assert.DoesNotContain(bear, request.Blockers);
        Assert.Contains(bird, request.CanBlock[spider]);
    }

    [Fact]
    public async Task BlockingFlyerWithGroundCreatureIsRejected()
    {
        var s = new Scenario();
        var bird = s.Add(P0, Creature("Bird", 2, 2, Keyword.Flying));
        var spider = s.Add(P1, Creature("Spider", 1, 3, Keyword.Reach));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((bear, bird));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => s.RunUntilTurn());
    }

    [Fact]
    public async Task VigilanceAttackersStayUntapped()
    {
        var s = new Scenario();
        var knight = s.Add(P0, Creature("Knight", 2, 2, Keyword.Vigilance));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is AttackerDeclared a && a.Attacker == knight);
        Assert.False(s.Card(knight).Tapped);
    }

    [Fact]
    public async Task DefenderCannotAttack()
    {
        var s = new Scenario();
        var wall = s.Add(P0, Creature("Wall", 0, 4, Keyword.Defender));
        await s.RunUntilTurn();
        Assert.DoesNotContain(s.Game.Log, e => e is AttackerDeclared a && a.Attacker == wall);
    }

    [Fact]
    public void HasteRemovesSummoningSickness()
    {
        var fresh = new Card(new CardId(1), Creature("Raider", 2, 1, Keyword.Haste), P0) { Zone = Zone.Battlefield };
        var plain = new Card(new CardId(2), Creature("Bear", 2, 2), P0) { Zone = Zone.Battlefield };
        Assert.False(fresh.IsSummoningSick);
        Assert.True(plain.IsSummoningSick);
    }

    [Fact]
    public async Task MenaceNeedsTwoBlockers()
    {
        var s = new Scenario();
        var brute = s.Add(P0, Creature("Brute", 3, 3, Keyword.Menace));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((bear, brute));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => s.RunUntilTurn());
    }

    [Fact]
    public async Task MenaceCanBeDoubleBlocked()
    {
        var s = new Scenario();
        var brute = s.Add(P0, Creature("Brute", 3, 3, Keyword.Menace));
        var a = s.Add(P1, Creature("Bear", 2, 2));
        var b = s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((a, brute), (b, brute));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.OfType<BlockerDeclared>().Count());
        Assert.Equal(Zone.Graveyard, s.Card(brute).Zone);
    }

    [Fact]
    public async Task TrampleAssignsExcessToThePlayer()
    {
        var s = new Scenario();
        var wurm = s.Add(P0, Creature("Wurm", 6, 6, Keyword.Trample));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((bear, wurm));
        DamageAssignmentRequest? request = null;
        s.Attacker.AssignDamage = (_, r) => { request = r; return r.Suggested; };
        await s.RunUntilTurn();

        Assert.NotNull(request);
        Assert.True(request!.Trample);
        Assert.Equal(4, request.Suggested.ToPlayer);
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
    }

    [Fact]
    public async Task TrampleCannotSkipLethalDamage()
    {
        var s = new Scenario();
        var wurm = s.Add(P0, Creature("Wurm", 6, 6, Keyword.Trample));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((bear, wurm));
        s.Attacker.AssignDamage = (_, r) => new DamageAssignment(new Dictionary<CardId, int> { [bear] = 1 }, 5);
        await Assert.ThrowsAsync<InvalidDecisionException>(() => s.RunUntilTurn());
    }

    [Fact]
    public async Task DeathtouchKillsWithAnyDamageAndMakesOneLethalForTrample()
    {
        var s = new Scenario();
        var snake = s.Add(P0, Creature("Snake", 3, 3, Keyword.Deathtouch, Keyword.Trample));
        var giant = s.Add(P1, Creature("Giant", 1, 7));
        s.Defender.Block = BlockWith((giant, snake));
        await s.RunUntilTurn();

        Assert.Equal(Zone.Graveyard, s.Card(giant).Zone); // 1 damage from deathtouch is lethal
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // 2 trampled over
    }

    [Fact]
    public async Task IndestructibleSurvivesLethalDamageAndDeathtouch()
    {
        var s = new Scenario();
        var snake = s.Add(P0, Creature("Snake", 1, 1, Keyword.Deathtouch));
        var golem = s.Add(P1, Creature("Golem", 1, 1, Keyword.Indestructible));
        s.Defender.Block = BlockWith((golem, snake));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(snake).Zone);
    }

    [Fact]
    public async Task LifelinkGainsLifeForDamageDealt()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Priest", 3, 3, Keyword.Lifelink));
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task FirstStrikeKillsBlockerBeforeItDealsDamage()
    {
        var s = new Scenario();
        var duelist = s.Add(P0, Creature("Duelist", 2, 2, Keyword.FirstStrike));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Defender.Block = BlockWith((bear, duelist));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(duelist).Zone);
        Assert.Equal(0, s.Card(duelist).Damage);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(2, s.Game.Log.OfType<StepBegan>().Count(st => st.Step == Step.CombatDamage));
    }

    [Fact]
    public async Task DoubleStrikeDealsDamageTwice()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Champion", 3, 3, Keyword.DoubleStrike));
        await s.RunUntilTurn();
        Assert.Equal(14, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task RegularStrikeOnlyCreaturesDealDamageInSecondStep()
    {
        var s = new Scenario();
        var duelist = s.Add(P0, Creature("Duelist", 1, 3, Keyword.FirstStrike));
        var bear = s.Add(P1, Creature("Bear", 2, 4));
        s.Defender.Block = BlockWith((bear, duelist));
        await s.RunUntilTurn();
        // Duelist deals 1 in the first step only; Bear deals 2 in the regular step only.
        Assert.Single(s.Game.Log.OfType<DamageDealt>(), d => d.Source == duelist);
        Assert.Single(s.Game.Log.OfType<DamageDealt>(), d => d.Source == bear);
    }
}
