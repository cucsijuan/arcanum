// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>How an attacker's combat damage is divided: among its blockers and, with trample, the defending player.</summary>
public sealed record DamageAssignment(IReadOnlyDictionary<CardId, int> ToBlockers, int ToPlayer = 0)
{
    public int Total => ToBlockers.Values.Sum() + ToPlayer;
}

/// <summary>
/// An attacker blocked by several creatures (or with trample) divides its combat damage (rule 510.1c-d). Since the
/// 2024 rules update there is no damage assignment order: any split adding up to <see cref="Power"/> is legal, except
/// that trample may only send damage to the player once every blocker has been assigned lethal damage (702.19c).
/// </summary>
/// <param name="Lethal">Lethal damage for each blocker, accounting for damage already marked and deathtouch.</param>
/// <param name="Suggested">Auto split: lethal to each blocker in order, the rest to the player (trample) or last blocker.</param>
public sealed record DamageAssignmentRequest(
    CardId Attacker,
    int Power,
    IReadOnlyList<CardId> Blockers,
    IReadOnlyDictionary<CardId, int> Lethal,
    bool Trample,
    PlayerId Defender,
    DamageAssignment Suggested);
