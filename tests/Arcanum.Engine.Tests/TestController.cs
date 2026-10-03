// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Scriptable controller: greedy defaults (play a land, cast anything, attack with everything,
/// never block) that each test can override.
/// </summary>
public sealed class TestController : IPlayerController
{
    public Func<GameView, int, bool> Keep { get; set; } = (_, _) => true;

    public Func<GameView, IReadOnlyList<PlayerAction>, PlayerAction> Act { get; set; } = Greedy;

    public Func<GameView, IReadOnlyList<CardId>, IReadOnlyList<PlayerId>, IReadOnlyList<AttackDeclaration>> Attack { get; set; } =
        (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();

    public Func<GameView, IReadOnlyList<CardId>, IReadOnlyList<CardId>, IReadOnlyList<BlockDeclaration>> Block { get; set; } =
        (_, _, _) => Array.Empty<BlockDeclaration>();

    /// <summary>Default accepts the engine's auto-pay suggestion.</summary>
    public Func<GameView, ManaPaymentRequest, IReadOnlyList<ManaTap>?> Pay { get; set; } = (_, request) => request.SuggestedTaps;

    public Func<GameView, DamageAssignmentRequest, IReadOnlyDictionary<CardId, int>> AssignDamage { get; set; } =
        (_, request) => request.Suggested;

    public static PlayerAction Greedy(GameView view, IReadOnlyList<PlayerAction> legal) =>
        legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault()
        ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
        ?? PassPriority.Instance;

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => Task.FromResult(Keep(view, mulligansTaken));

    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
        Task.FromResult<IReadOnlyList<CardId>>(view.Self.Hand.TakeLast(count).Select(c => c.Id).ToList());

    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) =>
        Task.FromResult(Act(view, legalActions));

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        Task.FromResult(Pay(view, request));

    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        Task.FromResult(Attack(view, possibleAttackers, defenders));

    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(
        GameView view, IReadOnlyList<CardId> possibleBlockers, IReadOnlyList<CardId> attackers) =>
        Task.FromResult(Block(view, possibleBlockers, attackers));

    public Task<IReadOnlyDictionary<CardId, int>> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Task.FromResult(AssignDamage(view, request));

    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) =>
        Task.FromResult<IReadOnlyList<CardId>>(view.Self.Hand.TakeLast(count).Select(c => c.Id).ToList());
}
