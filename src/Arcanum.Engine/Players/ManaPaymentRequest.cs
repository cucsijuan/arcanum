// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Players;

/// <summary>Tap <paramref name="Source"/> for one mana of <paramref name="Type"/>.</summary>
public sealed record ManaTap(CardId Source, ManaType Type);

public sealed record ManaSourceOption(CardId Source, IReadOnlyList<ManaType> Types);

/// <summary>
/// Asks a player how to pay for a spell (rule 601.2g–h). Floating mana in <see cref="FromPool"/> is applied
/// automatically; the player chooses which sources to tap for the rest. <see cref="SuggestedTaps"/> is the
/// engine's auto-pay solution, which controllers can accept as-is.
/// </summary>
public sealed record ManaPaymentRequest(
    CardId Spell,
    ManaCost Cost,
    IReadOnlyList<ManaType> FromPool,
    ManaCost RemainingAfterPool,
    IReadOnlyList<ManaTap> SuggestedTaps,
    IReadOnlyList<ManaSourceOption> Sources);
