// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;

namespace Arcanum.Client;

/// <summary>A question the engine is waiting on a local player to answer through the UI.</summary>
public abstract class Decision
{
    public required PlayerId Player { get; init; }

    public abstract bool IsAnswered { get; }
}

public abstract class Decision<T> : Decision
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _tcs.Task;

    public override bool IsAnswered => _tcs.Task.IsCompleted;

    public void Answer(T value) => _tcs.TrySetResult(value);
}

public sealed class MulliganDecision : Decision<bool>
{
    public required int MulligansTaken { get; init; }
}

public sealed class PriorityDecision : Decision<PlayerAction>
{
    public required IReadOnlyList<PlayerAction> Legal { get; init; }
}

public sealed class ManaPaymentDecision : Decision<IReadOnlyList<ManaTap>?>
{
    public required ManaPaymentRequest Request { get; init; }
}

public sealed class TargetDecision : Decision<IReadOnlyList<Arcanum.Engine.Abilities.Target>?>
{
    public required TargetRequest Request { get; init; }
}

public sealed class AttackDecision : Decision<IReadOnlyList<AttackDeclaration>>
{
    public required IReadOnlyList<CardId> PossibleAttackers { get; init; }
    public required IReadOnlyList<PlayerId> Defenders { get; init; }
}

public sealed class BlockDecision : Decision<IReadOnlyList<BlockDeclaration>>
{
    public required BlockRequest Request { get; init; }
    public IReadOnlyList<CardId> PossibleBlockers => Request.Blockers;
    public IReadOnlyList<CardId> Attackers => Request.Attackers;
}

public sealed class DamageAssignmentDecision : Decision<DamageAssignment>
{
    public required DamageAssignmentRequest Request { get; init; }
}

public sealed class YesNoDecision : Decision<bool>
{
    public required YesNoRequest Request { get; init; }
}

public enum SelectCardsReason { MulliganBottom, Discard }

public sealed class SelectCardsDecision : Decision<IReadOnlyList<CardId>>
{
    public required int Count { get; init; }
    public required SelectCardsReason Reason { get; init; }
}

/// <summary>Pick cards among options the engine shows (scry, surveil, search...).</summary>
public sealed class ChooseCardsDecision : Decision<IReadOnlyList<CardId>>
{
    public required CardChoiceRequest Request { get; init; }
}

/// <summary>Pick modes of a modal spell or ability.</summary>
public sealed class ChooseModesDecision : Decision<IReadOnlyList<int>?>
{
    public required ModeRequest Request { get; init; }
}

/// <summary>Choose a number (the value of X).</summary>
public sealed class ChooseNumberDecision : Decision<int>
{
    public required NumberRequest Request { get; init; }
}

/// <summary>Single queue point for decisions of every local seat (hotseat shares one screen).</summary>
public sealed class DecisionHub
{
    public Decision? Current { get; private set; }

    public event Action<Decision>? DecisionRequested;

    internal Task<T> Ask<T>(Decision<T> decision)
    {
        Current = decision;
        DecisionRequested?.Invoke(decision);
        return decision.Task;
    }
}
