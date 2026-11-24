// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;

namespace Arcanum.Bots.Simulation;

/// <summary>
/// Passes every decision of a seat to its real controller and records the answers in order. Because the engine is
/// deterministic, a fresh game with the same setup whose seats replay these answers (<see cref="Replayer"/>) reaches
/// exactly the same position: that is how a bot forks the game it is playing to try its options.
/// </summary>
public sealed class Recorder : IPlayerController
{
    private readonly IPlayerController _inner;
    private readonly object _gate = new();
    private readonly List<object?> _answers = new();

    public Recorder(IPlayerController inner) => _inner = inner;

    /// <summary>The answers given so far, oldest first.</summary>
    public IReadOnlyList<object?> Answers
    {
        get { lock (_gate) return _answers.ToList(); }
    }

    /// <summary>Time spent waiting for the real controller's answers.</summary>
    public TimeSpan Thinking { get; private set; }

    private async Task<T> Record<T>(Task<T> answer)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await answer;
        Thinking += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        lock (_gate) _answers.Add(result);
        return result;
    }

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => Record(_inner.KeepHandAsync(view, mulligansTaken));
    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => Record(_inner.ChooseCardsToBottomAsync(view, count));
    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) => Record(_inner.ChooseActionAsync(view, legalActions));
    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => Record(_inner.ChooseManaPaymentAsync(view, request));
    public Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => Record(_inner.ChooseTargetsAsync(view, request));
    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        Record(_inner.DeclareAttackersAsync(view, possibleAttackers, defenders));
    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) => Record(_inner.DeclareBlockersAsync(view, request));
    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => Record(_inner.AssignCombatDamageAsync(view, request));
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Record(_inner.ChooseYesNoAsync(view, request));
    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => Record(_inner.ChooseDiscardAsync(view, count));
    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => Record(_inner.ChooseCardsAsync(view, request));
    public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => Record(_inner.ChooseModesAsync(view, request));
    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => Record(_inner.ChooseNumberAsync(view, request));
    public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => Record(_inner.ChooseOptionAsync(view, request));
}

/// <summary>
/// Gives a seat's recorded answers back in order; once they run out, the next decision goes to <see cref="Then"/>
/// (after <see cref="ReachedEnd"/> runs, at the position the recording ended in).
/// </summary>
public sealed class Replayer : IPlayerController, IReplayingController
{
    private readonly Queue<object?> _answers;

    public Replayer(IEnumerable<object?> answers, IPlayerController then)
    {
        _answers = new Queue<object?>(answers);
        Then = then;
    }

    /// <summary>Who decides after the recording.</summary>
    public IPlayerController Then { get; set; }

    /// <summary>Runs once, when the first decision after the recording is asked (the fork is at the recorded position).</summary>
    public Func<GameView, Task>? ReachedEnd { get; set; }

    public bool Replaying => _answers.Count > 0;

    /// <summary>The answers not given back yet, oldest first.</summary>
    public IReadOnlyList<object?> Remaining => _answers.ToList();

    private async Task<T> Next<T>(Func<IPlayerController, Task<T>> live, GameView view)
    {
        if (_answers.TryDequeue(out var answer)) return (T)answer!;
        if (ReachedEnd is { } reached)
        {
            ReachedEnd = null;
            await reached(view);
        }
        return await live(Then);
    }

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => Next(c => c.KeepHandAsync(view, mulligansTaken), view);
    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => Next(c => c.ChooseCardsToBottomAsync(view, count), view);
    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) => Next(c => c.ChooseActionAsync(view, legalActions), view);
    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => Next(c => c.ChooseManaPaymentAsync(view, request), view);
    public Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => Next(c => c.ChooseTargetsAsync(view, request), view);
    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        Next(c => c.DeclareAttackersAsync(view, possibleAttackers, defenders), view);
    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) => Next(c => c.DeclareBlockersAsync(view, request), view);
    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => Next(c => c.AssignCombatDamageAsync(view, request), view);
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => Next(c => c.ChooseYesNoAsync(view, request), view);
    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => Next(c => c.ChooseDiscardAsync(view, count), view);
    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => Next(c => c.ChooseCardsAsync(view, request), view);
    public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => Next(c => c.ChooseModesAsync(view, request), view);
    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => Next(c => c.ChooseNumberAsync(view, request), view);
    public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => Next(c => c.ChooseOptionAsync(view, request), view);
}
