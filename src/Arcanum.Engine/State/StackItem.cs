// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A chosen target plus the version of the object when it was chosen (a changed version is illegal).</summary>
public readonly record struct ChosenTarget(Target Target, int Version);

/// <summary>An object on the stack: a spell or an activated/triggered ability.</summary>
public abstract record StackItem(PlayerId Controller, IReadOnlyList<ChosenTarget> Targets)
{
    /// <summary>The card the spell is, or the source of the ability.</summary>
    public abstract CardId SourceCard { get; }
}

public sealed record SpellOnStack(CardId Card, PlayerId Controller, IReadOnlyList<ChosenTarget>? ChosenTargets = null)
    : StackItem(Controller, ChosenTargets ?? Array.Empty<ChosenTarget>())
{
    public override CardId SourceCard => Card;
}

public sealed record AbilityOnStack(CardId Source, AbilityDefinition Ability, PlayerId Controller, IReadOnlyList<ChosenTarget> ChosenTargets)
    : StackItem(Controller, ChosenTargets)
{
    public override CardId SourceCard => Source;
}
