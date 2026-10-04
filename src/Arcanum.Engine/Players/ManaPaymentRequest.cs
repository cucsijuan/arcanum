// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Players;

/// <summary>Tap <paramref name="Source"/> for its mana, of <paramref name="Type"/> (one mana, or several for some sources).</summary>
/// <param name="Combination">For an ability that adds mana "in any combination": the mana chosen.</param>
public sealed record ManaTap(CardId Source, ManaType Type, int Option = 0, IReadOnlyList<ManaType>? Combination = null);

/// <param name="Amount">Mana added per activation, all of the chosen type ("Add three mana of any one color").</param>
public sealed record ManaSourceOption(CardId Source, IReadOnlyList<ManaType> Types, int Amount = 1, int Option = 0)
{
    /// <summary>Its mana is chosen in any combination of <see cref="Types"/> ("two mana in any combination of …").</summary>
    public bool Combination { get; init; }
}

/// <summary>
/// Asks a player how to pay for a spell (rule 601.2g–h). Floating mana in <see cref="FromPool"/> is applied
/// automatically; the player chooses which sources to tap for the rest. <see cref="SuggestedTaps"/> is the
/// engine's auto-pay solution, which controllers can accept as-is.
/// </summary>
/// <param name="Source">The spell being cast, or the permanent whose ability is being activated.</param>
public sealed record ManaPaymentRequest(
    CardId Source,
    ManaCost Cost,
    IReadOnlyList<ManaType> FromPool,
    ManaCost RemainingAfterPool,
    IReadOnlyList<ManaTap> SuggestedTaps,
    IReadOnlyList<ManaSourceOption> Sources);
