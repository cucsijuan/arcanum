// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// Choose one target for each requirement of a spell or ability. <see cref="Legal"/> lists, per requirement, every
/// legal choice. Casting can be cancelled when <see cref="CanCancel"/> is true (not for triggered abilities).
/// </summary>
public sealed record TargetRequest(
    CardId Source,
    string Text,
    IReadOnlyList<TargetSpec> Specs,
    IReadOnlyList<IReadOnlyList<Target>> Legal,
    bool CanCancel)
{
    /// <summary>
    /// Whether <c>candidate</c> may be the target at <c>index</c> given the targets chosen before it (rules between
    /// targets: different objects, different controllers, attached to an earlier target...). Null: no such rules.
    /// </summary>
    public Func<int, Target, IReadOnlyList<Target>, bool>? Allowed { get; init; }

    /// <summary>At most this many targets in all (an "any number" requirement limited by what can be paid or divided).</summary>
    public int? MaxCount { get; init; }

    /// <summary>Whether another target can still be added after <paramref name="chosen"/> ones.</summary>
    public bool CanAddMore(int chosen) => (chosen < Legal.Count || LastIsAnyNumber) && (MaxCount is not { } max || chosen < max);

    /// <summary>The last requirement takes any number of targets.</summary>
    public bool LastIsAnyNumber => Specs.Count > 0 && Specs[^1].AnyNumber;

    /// <summary>Legal choices for the target at <paramref name="index"/> (extra targets of an "any number" requirement share the last list).</summary>
    public IReadOnlyList<Target> LegalAt(int index) => Legal[Math.Min(index, Legal.Count - 1)];

    public TargetSpec SpecAt(int index) => Specs[Math.Min(index, Specs.Count - 1)];

    public bool IsAllowed(int index, Target candidate, IReadOnlyList<Target> chosenBefore)
    {
        if (!LegalAt(index).Contains(candidate)) return false;
        // The same object can't be chosen twice for one "target" word (rule 115.3).
        for (int i = 0; i < chosenBefore.Count; i++)
            if (!candidate.IsNone && chosenBefore[i] == candidate && SpecAt(i) == SpecAt(index)) return false;
        return Allowed?.Invoke(index, candidate, chosenBefore) ?? true;
    }

    /// <summary>Enough targets chosen to finish (all requirements, the "any number" one at least once unless optional).</summary>
    /// <summary>Enough targets chosen: "any number" includes zero (rule 601.2c).</summary>
    public bool IsComplete(int chosen) => (LastIsAnyNumber ? chosen >= Specs.Count - 1 : chosen == Specs.Count) && (MaxCount is not { } max || chosen <= max);
}
