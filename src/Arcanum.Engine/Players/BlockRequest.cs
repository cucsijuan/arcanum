// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// Everything a defending player needs to declare blockers legally: which creatures can block which attackers
/// (flying/reach...), and attackers that need more than one blocker (menace).
/// </summary>
public sealed record BlockRequest(
    IReadOnlyList<CardId> Attackers,
    IReadOnlyList<CardId> Blockers,
    IReadOnlyDictionary<CardId, IReadOnlyList<CardId>> CanBlock,
    IReadOnlyDictionary<CardId, int> MinimumBlockers)
{
    public bool IsLegal(IReadOnlyList<BlockDeclaration> blocks, out string? reason)
    {
        reason = null;
        if (blocks.Select(b => b.Blocker).Distinct().Count() != blocks.Count) { reason = "A creature can block only one attacker."; return false; }
        foreach (var b in blocks)
        {
            if (!CanBlock.TryGetValue(b.Blocker, out var allowed) || !allowed.Contains(b.Attacker))
            {
                reason = "That creature can't block that attacker.";
                return false;
            }
        }
        foreach (var (attacker, minimum) in MinimumBlockers)
        {
            int count = blocks.Count(b => b.Attacker == attacker);
            if (count > 0 && count < minimum)
            {
                reason = $"That attacker can't be blocked except by {minimum} or more creatures.";
                return false;
            }
        }
        return true;
    }
}
