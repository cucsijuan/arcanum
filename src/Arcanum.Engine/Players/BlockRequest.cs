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
    /// <summary>Attackers that must be blocked if able (rule 509.1c: obey as many such requirements as possible).</summary>
    public IReadOnlyList<CardId> MustBeBlocked { get; init; } = Array.Empty<CardId>();

    /// <summary>Attackers that can't be blocked by more than this many creatures.</summary>
    public IReadOnlyDictionary<CardId, int> MaximumBlockers { get; init; } = new Dictionary<CardId, int>();

    private int? _maxRequirements;

    /// <summary>The most "must be blocked" requirements any legal declaration can obey.</summary>
    public int MaxRequirements => _maxRequirements ??= Best(0, new HashSet<CardId>());

    private int Best(int index, HashSet<CardId> used)
    {
        if (index == MustBeBlocked.Count) return 0;
        var attacker = MustBeBlocked[index];
        int best = Best(index + 1, used); // leave it unblocked
        int need = MinimumBlockers.GetValueOrDefault(attacker, 1);
        var able = Blockers.Where(b => !used.Contains(b) && CanBlock.TryGetValue(b, out var a) && a.Contains(attacker)).ToList();
        foreach (var group in Combinations(able, need))
        {
            foreach (var b in group) used.Add(b);
            best = Math.Max(best, 1 + Best(index + 1, used));
            foreach (var b in group) used.Remove(b);
            if (best == MustBeBlocked.Count - index) break;
        }
        return best;
    }

    private static IEnumerable<List<CardId>> Combinations(List<CardId> items, int k, int start = 0)
    {
        if (k == 0) { yield return new List<CardId>(); yield break; }
        for (int i = start; i <= items.Count - k; i++)
            foreach (var rest in Combinations(items, k - 1, i + 1))
            {
                rest.Insert(0, items[i]);
                yield return rest;
            }
    }

    /// <summary>Adds blockers (not already blocking) to attackers that must be blocked if able.</summary>
    public IReadOnlyList<BlockDeclaration> WithRequirements(IReadOnlyList<BlockDeclaration> blocks)
    {
        var result = blocks.ToList();
        foreach (var attacker in MustBeBlocked)
        {
            int need = MinimumBlockers.GetValueOrDefault(attacker, 1) - result.Count(b => b.Attacker == attacker);
            var free = Blockers.Where(b => result.All(x => x.Blocker != b) && CanBlock.TryGetValue(b, out var a) && a.Contains(attacker)).ToList();
            if (need <= 0 || free.Count < need) continue;
            result.AddRange(free.Take(need).Select(b => new BlockDeclaration(b, attacker)));
        }
        return result;
    }

    /// <summary>"Must be blocked" requirements a declaration obeys.</summary>
    public int ObeyedRequirements(IReadOnlyList<BlockDeclaration> blocks) =>
        MustBeBlocked.Count(a => blocks.Count(b => b.Attacker == a) >= MinimumBlockers.GetValueOrDefault(a, 1));

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
        foreach (var (attacker, maximum) in MaximumBlockers)
            if (blocks.Count(b => b.Attacker == attacker) > maximum)
            {
                reason = $"That attacker can't be blocked by more than {maximum} creature{(maximum == 1 ? "" : "s")}.";
                return false;
            }
        if (MustBeBlocked.Count > 0 && ObeyedRequirements(blocks) < MaxRequirements)
        {
            reason = "Some attackers must be blocked if able.";
            return false;
        }
        return true;
    }
}
