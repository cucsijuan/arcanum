// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;

namespace Arcanum.Engine.Tests;

public class CombatDamageTests
{
    // Wurms (6/4) attack into a Bears deck that double-blocks whenever it can.
    private static readonly IReadOnlyList<Cards.CardDefinition> Wurms = Decks.Of((GenericCards.Forest, 22), (GenericCards.GreatWurm, 18));

    private static TestController DoubleBlocker() => new()
    {
        Attack = (_, _, _) => Array.Empty<AttackDeclaration>(),
        Block = (_, blockers, attackers) => blockers.Count >= 2
            ? blockers.Take(2).Select(b => new BlockDeclaration(b, attackers[0])).ToList()
            : Array.Empty<BlockDeclaration>(),
    };

    [Fact]
    public async Task DoubleBlockAsksAttackerToSplitDamage()
    {
        var requests = new List<DamageAssignmentRequest>();
        var attacker = new TestController { AssignDamage = (_, r) => { requests.Add(r); return r.Suggested; } };
        var game = Decks.NewGame(31, (Wurms, attacker), (Decks.ForestCubs, DoubleBlocker()));
        await game.RunWithTimeout(20);

        var request = Assert.IsType<DamageAssignmentRequest>(requests.FirstOrDefault());
        Assert.Equal(6, request.Power);
        Assert.Equal(2, request.Blockers.Count);
        Assert.Equal(6, request.Suggested.Values.Sum());
        Assert.Equal(2, request.Suggested[request.Blockers[0]]); // lethal to the first bear
    }

    [Fact]
    public async Task AnySplitIsAllowedWithoutDamageAssignmentOrder()
    {
        // Put all 6 on the second blocker: legal since the 2024 rules update, and only that bear dies.
        DamageAssignmentRequest? first = null;
        var attacker = new TestController
        {
            AssignDamage = (_, r) =>
            {
                first ??= r;
                return new Dictionary<CardId, int> { [r.Blockers[0]] = 0, [r.Blockers[1]] = r.Power };
            },
        };
        var game = Decks.NewGame(31, (Wurms, attacker), (Decks.ForestCubs, DoubleBlocker()));
        var died = new List<CardId>();
        game.EventRaised += e => { if (e is CreatureDied d) died.Add(d.Card); };
        await game.RunWithTimeout(20);

        Assert.NotNull(first);
        Assert.Contains(first!.Blockers[1], died);
        Assert.Contains(game.Log, e => e is DamageDealt { Amount: 6 } d && d.TargetCard == first.Blockers[1]);
        Assert.DoesNotContain(game.Log, e => e is DamageDealt d && d.Source == first.Attacker && d.TargetCard == first.Blockers[0]);
    }

    [Fact]
    public async Task WrongTotalIsRejected()
    {
        var attacker = new TestController
        {
            AssignDamage = (_, r) => new Dictionary<CardId, int> { [r.Blockers[0]] = 1 },
        };
        var game = Decks.NewGame(31, (Wurms, attacker), (Decks.ForestCubs, DoubleBlocker()));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => game.RunWithTimeout(20));
    }

    [Fact]
    public async Task SingleBlockerNeedsNoDecision()
    {
        int asked = 0;
        var attacker = new TestController { AssignDamage = (_, r) => { asked++; return r.Suggested; } };
        var singleBlocker = new TestController
        {
            Attack = (_, _, _) => Array.Empty<AttackDeclaration>(),
            Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) },
        };
        var game = Decks.NewGame(32, (Wurms, attacker), (Decks.ForestCubs, singleBlocker));
        await game.RunWithTimeout(20);
        Assert.Equal(0, asked);
        Assert.Contains(game.Log, e => e is BlockerDeclared);
    }
}
