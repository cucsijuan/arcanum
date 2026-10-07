// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// Everything a defending player needs to declare blockers legally (rule 509.1): which creatures can block which
/// attackers (flying/reach...), the restrictions on the declaration (menace and other minimums, "can't be blocked by more
/// than one creature", "can't block alone", one attacker per blocker unless it "can block any number of creatures") and the
/// requirements it must obey as far as possible ("must be blocked if able", "all creatures able to block it do so").
/// </summary>
/// <remarks>
/// Rule 509.1c: the declaration must obey the maximum possible number of requirements without breaking a restriction.
/// <see cref="MaxRequirements"/> finds that maximum with an exact search, <see cref="IsLegal"/> checks a declaration and
/// <see cref="Complete"/> turns any wanted declaration into a legal one that keeps as much of it as possible.
/// </remarks>
public sealed record BlockRequest(
    IReadOnlyList<CardId> Attackers,
    IReadOnlyList<CardId> Blockers,
    IReadOnlyDictionary<CardId, IReadOnlyList<CardId>> CanBlock,
    IReadOnlyDictionary<CardId, int> MinimumBlockers)
{
    /// <summary>Attackers that must be blocked if able: one requirement each, obeyed when it is blocked.</summary>
    public IReadOnlyList<CardId> MustBeBlocked { get; init; } = Array.Empty<CardId>();

    /// <summary>Attackers that can't be blocked by more than this many creatures.</summary>
    public IReadOnlyDictionary<CardId, int> MaximumBlockers { get; init; } = new Dictionary<CardId, int>();

    /// <summary>
    /// "All creatures able to block this creature do so": each creature that can block it has a requirement to block it
    /// (one requirement per such blocker).
    /// </summary>
    public IReadOnlyList<CardId> Lures { get; init; } = Array.Empty<CardId>();

    /// <summary>Blockers that "can block any number of creatures" (rule 509.1a: the others block one attacker each).</summary>
    public IReadOnlyList<CardId> CanBlockAny { get; init; } = Array.Empty<CardId>();

    /// <summary>Blockers that "can't block alone": they block only if another creature also blocks (rule 506.5).</summary>
    public IReadOnlyList<CardId> CantBlockAlone { get; init; } = Array.Empty<CardId>();

    private int? _maxRequirements;

    /// <summary>The most requirements any legal declaration obeys.</summary>
    public int MaxRequirements => _maxRequirements ??= Search(Array.Empty<BlockDeclaration>()).Requirements;

    /// <summary>Whether this declaration has requirements to obey at all.</summary>
    public bool HasRequirements => MustBeBlocked.Count > 0 || Lures.Any(a => Blockers.Any(b => Able(b, a)));

    private bool Able(CardId blocker, CardId attacker) => CanBlock.TryGetValue(blocker, out var a) && a.Contains(attacker);

    /// <summary>Requirements a declaration obeys: blocked "must be blocked" attackers, and blocks of lures by creatures able to block them.</summary>
    public int ObeyedRequirements(IReadOnlyList<BlockDeclaration> blocks) =>
        MustBeBlocked.Count(a => blocks.Any(b => b.Attacker == a))
        + blocks.Distinct().Count(b => Lures.Contains(b.Attacker) && Able(b.Blocker, b.Attacker));

    /// <summary>Kept for callers that only add blockers for requirements: same as <see cref="Complete"/>.</summary>
    public IReadOnlyList<BlockDeclaration> WithRequirements(IReadOnlyList<BlockDeclaration> blocks) => Complete(blocks);

    /// <summary>
    /// A legal declaration obeying as many requirements as possible, keeping as much of <paramref name="wanted"/> as it can
    /// (it adds blocks only when requirements or restrictions need them).
    /// </summary>
    public IReadOnlyList<BlockDeclaration> Complete(IReadOnlyList<BlockDeclaration> wanted) => Search(wanted).Blocks;

    public bool IsLegal(IReadOnlyList<BlockDeclaration> blocks, out string? reason)
    {
        reason = null;
        if (blocks.Distinct().Count() != blocks.Count) { reason = "A creature can block an attacker only once."; return false; }
        foreach (var b in blocks)
        {
            if (!Able(b.Blocker, b.Attacker))
            {
                reason = "That creature can't block that attacker.";
                return false;
            }
        }
        if (blocks.GroupBy(b => b.Blocker).Any(g => g.Count() > 1 && !CanBlockAny.Contains(g.Key)))
        {
            reason = "A creature can block only one attacker.";
            return false;
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
        var blocking = blocks.Select(b => b.Blocker).Distinct().ToList();
        if (blocking.Count == 1 && CantBlockAlone.Contains(blocking[0]))
        {
            reason = "That creature can't block alone.";
            return false;
        }
        if (HasRequirements && ObeyedRequirements(blocks) < MaxRequirements)
        {
            reason = Lures.Count > 0 ? "Creatures able to block must block as required." : "Some attackers must be blocked if able.";
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ exact search

    /// <summary>A choice for one blocker: the attackers it blocks, and what that is worth.</summary>
    private sealed record Option(CardId[] Blocks, int Requirements, int Kept);

    private (int Requirements, IReadOnlyList<BlockDeclaration> Blocks) Search(IReadOnlyList<BlockDeclaration> wanted)
    {
        // Attackers whose number of blockers matters: requirements counted at the end, minimums and maximums.
        var tracked = Attackers.Where(a => MustBeBlocked.Contains(a) || MinimumBlockers.GetValueOrDefault(a, 1) > 1 || MaximumBlockers.ContainsKey(a)).Distinct().ToList();
        var cap = tracked.Select(a => Math.Max(MinimumBlockers.GetValueOrDefault(a, 1), MaximumBlockers.TryGetValue(a, out var max) ? max + 1 : 1)).ToArray();
        var special = tracked.Concat(Lures).ToHashSet();
        var blockers = Blockers.Distinct().ToList();

        var options = new List<Option>[blockers.Count];
        for (int i = 0; i < blockers.Count; i++)
        {
            var b = blockers[i];
            var able = Attackers.Where(a => Able(b, a)).Distinct().ToList();
            var want = wanted.Where(w => w.Blocker == b && able.Contains(w.Attacker)).Select(w => w.Attacker).ToHashSet();
            var specials = able.Where(special.Contains).ToList();
            var plain = able.Where(a => !special.Contains(a)).ToList();
            // Plain attackers differ only in what the player wanted: the wanted ones, or any one of them to be blocking at all.
            var plainChoices = new List<CardId[]> { Array.Empty<CardId>() };
            if (want.Overlaps(plain)) plainChoices.Add(plain.Where(want.Contains).ToArray());
            if (plain.Count > 0) plainChoices.Add(new[] { plain.FirstOrDefault(want.Contains, plain[0]) });
            var list = new List<Option>();
            Option Make(IEnumerable<CardId> set)
            {
                var arr = set.Distinct().ToArray();
                return new Option(arr, arr.Count(a => Lures.Contains(a)), arr.Sum(a => want.Contains(a) ? 2 : -3));
            }
            if (CanBlockAny.Contains(b))
            {
                for (long mask = 0; mask < 1L << specials.Count; mask++)
                {
                    var chosen = specials.Where((_, k) => (mask >> k & 1) == 1).ToList();
                    foreach (var p in plainChoices) list.Add(Make(chosen.Concat(p)));
                }
            }
            else
            {
                list.Add(Make(Array.Empty<CardId>()));
                foreach (var a in specials.Concat(plainChoices.SelectMany(p => p)).Distinct()) list.Add(Make(new[] { a }));
            }
            options[i] = list.DistinctBy(o => string.Join(",", o.Blocks.Select(x => x.Value).OrderBy(x => x))).ToList();
        }

        var memo = new Dictionary<string, ((int Req, int Kept) Score, int Choice)>();
        var counts = new int[tracked.Count];
        var none = (Req: int.MinValue, Kept: int.MinValue);

        static bool Better((int Req, int Kept) x, (int Req, int Kept) y) => x.Req > y.Req || (x.Req == y.Req && x.Kept > y.Kept);

        string Key(int index, int blocking, bool alone)
        {
            var sb = new StringBuilder();
            sb.Append(index).Append('|').Append(blocking).Append(alone ? 'a' : '-');
            // Counts past what any rule looks at are equivalent: the key caps them so equal situations share a result.
            for (int k = 0; k < counts.Length; k++) sb.Append(',').Append(Math.Min(counts[k], cap[k]));
            return sb.ToString();
        }

        (int Req, int Kept) Best(int index, int blocking, bool alone)
        {
            if (index == blockers.Count)
            {
                if (alone && blocking < 2) return none; // 506.5
                int req = 0;
                for (int k = 0; k < tracked.Count; k++)
                {
                    if (counts[k] > 0 && counts[k] < MinimumBlockers.GetValueOrDefault(tracked[k], 1)) return none;
                    if (counts[k] > 0 && MustBeBlocked.Contains(tracked[k])) req++;
                }
                return (req, 0);
            }
            var key = Key(index, blocking, alone);
            if (memo.TryGetValue(key, out var known)) return known.Score;
            var best = none;
            int choice = -1;
            var b = blockers[index];
            for (int o = 0; o < options[index].Count; o++)
            {
                var option = options[index][o];
                bool fits = true;
                foreach (var a in option.Blocks)
                {
                    int k = tracked.IndexOf(a);
                    if (k >= 0 && MaximumBlockers.TryGetValue(a, out var max) && counts[k] + 1 > max) fits = false;
                }
                if (!fits) continue;
                foreach (var a in option.Blocks) { int k = tracked.IndexOf(a); if (k >= 0) counts[k]++; }
                bool blocks = option.Blocks.Length > 0;
                var rest = Best(index + 1, Math.Min(2, blocking + (blocks ? 1 : 0)), alone || (blocks && CantBlockAlone.Contains(b)));
                foreach (var a in option.Blocks) { int k = tracked.IndexOf(a); if (k >= 0) counts[k]--; }
                if (rest.Req == int.MinValue) continue;
                var score = (rest.Req + option.Requirements, rest.Kept + option.Kept);
                if (choice < 0 || Better(score, best)) { best = score; choice = o; }
            }
            memo[key] = (best, choice);
            return best;
        }

        var top = Best(0, 0, false);

        // Walk the memo to rebuild the chosen declaration.
        var result = new List<BlockDeclaration>();
        Array.Clear(counts);
        int blockingNow = 0;
        bool aloneNow = false;
        for (int i = 0; i < blockers.Count; i++)
        {
            var (_, choice) = memo[Key(i, blockingNow, aloneNow)];
            var option = options[i][choice];
            foreach (var a in option.Blocks)
            {
                result.Add(new BlockDeclaration(blockers[i], a));
                int k = tracked.IndexOf(a);
                if (k >= 0) counts[k]++;
            }
            if (option.Blocks.Length > 0)
            {
                blockingNow = Math.Min(2, blockingNow + 1);
                aloneNow |= CantBlockAlone.Contains(blockers[i]);
            }
        }
        return (top.Req, result);
    }
}
