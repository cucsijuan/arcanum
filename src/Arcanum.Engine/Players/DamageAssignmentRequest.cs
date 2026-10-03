// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// An attacker blocked by several creatures divides its combat damage among them (rule 510.1c). Since the
/// 2024 rules update there is no damage assignment order: any split that adds up to <see cref="Power"/> is legal
/// (trample, which needs lethal damage first, arrives with keywords).
/// </summary>
/// <param name="Suggested">Auto split: lethal damage to each blocker in declaration order, the rest to the last.</param>
public sealed record DamageAssignmentRequest(
    CardId Attacker,
    int Power,
    IReadOnlyList<CardId> Blockers,
    IReadOnlyDictionary<CardId, int> Suggested);
