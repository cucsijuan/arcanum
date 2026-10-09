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
/// <see cref="MaxRequirements"/> finds that maximum with an exact search (none is needed without requirements),
/// <see cref="IsLegal"/> checks a declaration and <see cref="Complete"/> turns any wanted declaration into a legal one that
/// keeps as much of it as possible. A board too large for the search gets a greedy legal declaration instead, and its
/// requirement count becomes the maximum.
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

    /// <summary>Blockers that "can block an additional creature each combat": they block up to two attackers (rule 509.1a).</summary>
    /// <remarks>
    /// The restrictions treat them exactly; the search for the most requirements to obey counts them as blocking one attacker,
    /// so a second block that alone would satisfy one more requirement isn't demanded (it is always allowed).
    /// </remarks>
    public IReadOnlyList<CardId> CanBlockAdditional { get; init; } = Array.Empty<CardId>();

    /// <summary>Whether the creature can block more than one attacker.</summary>
    public bool CanBlockSeveral(CardId blocker) => CanBlockAny.Contains(blocker) || CanBlockAdditional.Contains(blocker);

    /// <summary>The most attackers a creature can block.</summary>
    private int Capacity(CardId blocker) => CanBlockAny.Contains(blocker) ? int.MaxValue : CanBlockAdditional.Contains(blocker) ? 2 : 1;

    /// <summary>Blockers that "can't block alone": they block only if another creature also blocks (rule 506.5).</summary>
    public IReadOnlyList<CardId> CantBlockAlone { get; init; } = Array.Empty<CardId>();

    /// <summary>What each blocking creature costs (a block tax, paid as blockers are declared), or null when blocking is free.</summary>
    public string? TaxPerBlocker { get; init; }

    /// <summary>With a block tax: how many blocking creatures the player can pay for now.</summary>
    public int AffordableBlockers { get; init; } = int.MaxValue;

    private int? _maxRequirements;

    /// <summary>The most requirements any legal declaration obeys.</summary>
    public int MaxRequirements => _maxRequirements ??= Search(Array.Empty<BlockDeclaration>()).Requirements;

    /// <summary>Whether this declaration has requirements to obey at all.</summary>
    public bool HasRequirements => MustBeBlocked.Count > 0 || Lures.Any(a => Blockers.Any(b => Able(b, a)));

    private bool Able(CardId blocker, CardId attacker) => CanBlock.TryGetValue(blocker, out var a) && a.Contains(attacker);

    /// <summary>Requirements a declaration obeys: blocked "must be blocked" attackers, and blocks of lures by creatures able to block them.</summary>
    public int ObeyedRequirements(IReadOnlyList<BlockDeclaration> blocks) =>
        MustBeBlocked.Distinct().Count(a => blocks.Any(b => b.Attacker == a))
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
        if (!ObeysRestrictions(blocks, out reason)) return false;
        if (HasRequirements && ObeyedRequirements(blocks) < MaxRequirements)
        {
            reason = Lures.Count > 0 ? "Creatures able to block must block as required." : "Some attackers must be blocked if able.";
            return false;
        }
        return true;
    }

    /// <summary>Whether a declaration breaks no restriction (rule 509.1b), whatever requirements it obeys.</summary>
    private bool ObeysRestrictions(IReadOnlyList<BlockDeclaration> blocks, out string? reason)
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
        if (blocks.GroupBy(b => b.Blocker).Any(g => g.Count() > Capacity(g.Key)))
        {
            reason = "A creature can block only one attacker (two with an additional block).";
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
        return true;
    }


    // ------------------------------------------------------------------ search

    /// <summary>States the exact search may visit before it gives up for the greedy safety net.</summary>
    private const int StateBudget = 400_000;

    /// <summary>The exact search without wanted blocks, once run (null inside: the board was too large for it).</summary>
    private (int Requirements, IReadOnlyList<BlockDeclaration> Blocks)? _bestWithoutWanted;
    private bool _searchedWithoutWanted;

    private (int Requirements, IReadOnlyList<BlockDeclaration> Blocks)? ExactWithoutWanted()
    {
        if (!_searchedWithoutWanted)
        {
            _bestWithoutWanted = new Searcher(this, Array.Empty<BlockDeclaration>()).Run();
            _searchedWithoutWanted = true;
        }
        return _bestWithoutWanted;
    }

    private (int Requirements, IReadOnlyList<BlockDeclaration> Blocks) Search(IReadOnlyList<BlockDeclaration> wanted)
    {
        // Without requirements, legality is only the restrictions: repairing what the player wanted is enough.
        if (!HasRequirements) return (0, Repair(wanted));
        if ((wanted.Count == 0 ? ExactWithoutWanted() : new Searcher(this, wanted).Run()) is { } found) return found;

        // Safety net (a board too large for the exact search): the better of the wanted blocks repaired with requirements
        // added greedily, and the best declaration without wanted blocks (exact if that search fit, else greedy) with as
        // many wanted blocks added as stay legal.
        var exact = ExactWithoutWanted();
        var candidates = new[] { Greedy(wanted), AddWanted(exact?.Blocks ?? Greedy(Array.Empty<BlockDeclaration>()), wanted) };
        var pick = candidates.OrderByDescending(ObeyedRequirements).ThenByDescending(c => Closeness(c, wanted)).First();
        int obeyed = ObeyedRequirements(pick);
        // When the maximum isn't known exactly, the requirements obeyed here are the maximum legality is checked against,
        // so the engine never rejects its own repair.
        if (exact is null) _maxRequirements = _maxRequirements is { } known ? Math.Min(known, obeyed) : obeyed;
        return (obeyed, pick);
    }

    /// <summary>How close a declaration is to the wanted one: each wanted block kept counts 2, each other block -3.</summary>
    private static int Closeness(IReadOnlyList<BlockDeclaration> blocks, IReadOnlyList<BlockDeclaration> wanted) =>
        blocks.Sum(b => wanted.Contains(b) ? 2 : -3);

    /// <summary>
    /// Makes a declaration obey the restrictions, keeping as much of it as possible: impossible blocks go, a creature blocks
    /// one attacker unless it can block any number, an attacker keeps no more blockers than its maximum, an attacker left
    /// under its minimum keeps none, and a creature that can't block alone doesn't block alone.
    /// </summary>
    private List<BlockDeclaration> Repair(IEnumerable<BlockDeclaration> wanted)
    {
        var blocks = wanted.Distinct().Where(b => Able(b.Blocker, b.Attacker)).ToList();
        var busy = new Dictionary<CardId, int>();
        blocks = blocks.Where(b => (busy[b.Blocker] = busy.GetValueOrDefault(b.Blocker) + 1) <= Capacity(b.Blocker)).ToList();
        var count = new Dictionary<CardId, int>();
        blocks = blocks.Where(b =>
        {
            int n = count[b.Attacker] = count.GetValueOrDefault(b.Attacker) + 1;
            return !MaximumBlockers.TryGetValue(b.Attacker, out var max) || n <= max;
        }).ToList();
        var total = blocks.GroupBy(b => b.Attacker).ToDictionary(g => g.Key, g => g.Count());
        blocks = blocks.Where(b => total[b.Attacker] >= MinimumBlockers.GetValueOrDefault(b.Attacker, 1)).ToList();
        var blocking = blocks.Select(b => b.Blocker).Distinct().ToList();
        if (blocking.Count == 1 && CantBlockAlone.Contains(blocking[0])) blocks.Clear();
        return blocks;
    }

    /// <summary>Whether a creature is free to add a block of this attacker to a declaration.</summary>
    private bool Free(List<BlockDeclaration> blocks, CardId blocker, CardId attacker) =>
        Able(blocker, attacker) && !blocks.Contains(new BlockDeclaration(blocker, attacker)) && blocks.Count(x => x.Blocker == blocker) < Capacity(blocker);

    /// <summary>A legal declaration obeying requirements greedily: the repaired wanted blocks, then free creatures added for requirements.</summary>
    private List<BlockDeclaration> Greedy(IReadOnlyList<BlockDeclaration> wanted)
    {
        var blocks = Repair(wanted);
        void Add(CardId attacker, bool asManyAsPossible)
        {
            int have = blocks.Count(x => x.Attacker == attacker), minimum = MinimumBlockers.GetValueOrDefault(attacker, 1);
            if (have > 0 && !asManyAsPossible) return;
            int room = MaximumBlockers.TryGetValue(attacker, out var max) ? max - have : int.MaxValue;
            // Creatures that can block only one attacker first, those that can block any number after.
            var free = Blockers.Distinct().Where(b => Free(blocks, b, attacker)).OrderBy(b => CanBlockAny.Contains(b)).ToList();
            int take = Math.Min(room, asManyAsPossible ? free.Count : Math.Max(0, minimum - have));
            if (take <= 0 || have + take < minimum) return;
            blocks.AddRange(free.Take(take).Select(b => new BlockDeclaration(b, attacker)));
        }
        // Attackers that fewer creatures can block first.
        foreach (var attacker in MustBeBlocked.Distinct().OrderBy(a => Blockers.Count(b => Able(b, a)))) Add(attacker, false);
        foreach (var attacker in Lures.Distinct()) Add(attacker, true);
        return Repair(blocks);
    }

    /// <summary>Adds to a legal declaration the wanted blocks (attacker by attacker) that keep it legal; requirements only grow.</summary>
    private List<BlockDeclaration> AddWanted(IReadOnlyList<BlockDeclaration> legal, IReadOnlyList<BlockDeclaration> wanted)
    {
        var blocks = legal.ToList();
        foreach (var group in wanted.Distinct().GroupBy(w => w.Attacker))
        {
            var add = group.Where(w => Free(blocks, w.Blocker, w.Attacker)).DistinctBy(w => w.Blocker).ToList();
            if (add.Count == 0) continue;
            var trial = blocks.Concat(add).ToList();
            if (ObeysRestrictions(trial, out _)) blocks = trial;
        }
        return blocks;
    }

    /// <summary>
    /// The exact search (rule 509.1c): the most requirements without breaking a restriction, then the declaration closest to
    /// the wanted one. Blockers are decided one at a time. Attackers whose number of blockers matters (must be blocked, a
    /// minimum above one, a maximum) are grouped by everything the search looks at (the creatures able to block them, the
    /// creatures wanted to block them, their requirements and restrictions): members of a group are interchangeable, so the
    /// state keeps only how many of each group have each (capped) number of blockers, and a creature blocking one attacker
    /// tries one member per group and count. A creature that can block any number decides group by group how many members
    /// at each count it blocks, then blocks every other lure it can (more requirements always win) and picks among no other
    /// block, the wanted ones, or one.
    /// </summary>
    private sealed class Searcher
    {
        private const int Unreachable = int.MinValue;

        private readonly HashSet<BlockDeclaration> _wanted;
        private readonly CardId[] _blockers;
        private readonly bool[] _any, _alone;

        // Groups of interchangeable tracked attackers.
        private readonly int _groups;
        private readonly CardId[][] _members;
        private readonly int[] _size, _cap, _max, _min;
        private readonly bool[] _must, _lure;
        // Groups nobody needs or wants blocked: blocking them only makes a creature "blocking" (rule 506.5), and an optimal
        // declaration never blocks more than two of them (dropping a third one's blocks keeps two blocking creatures and is
        // closer to the wanted one), so the search never touches a third.
        private readonly bool[] _inert;
        private readonly bool[][] _able, _wantedBy; // [blocker][group]

        // Other attackers (no count restriction, not "must be blocked"): plain attackers and lures.
        private readonly CardId?[] _bestOther;                 // for a creature blocking one attacker
        private readonly int[] _bestOtherReq, _bestOtherKept;
        private readonly CardId[][] _otherLures, _wantedPlain; // for a creature blocking any number
        private readonly int[] _otherLuresKept;
        private readonly CardId?[] _onePlain;

        private readonly (int Blocker, int Group)[] _steps; // Group -1: the blocker's whole decision, or its last part
        private readonly int[][] _level;                    // [group][count] = members with that many blockers (capped)
        private readonly List<Move>[] _moves;               // per step, reused
        private readonly Dictionary<long, (int Req, int Kept, int Choice)> _memo = new();
        private readonly bool _fits;
        private readonly int[] _last;       // [group] = the last step that can change it (-1: none)
        private readonly int[][] _settled;  // [step] = groups settled once that step is decided
        private bool _exceeded;

        /// <summary>
        /// One choice at a step. Group/Level: one member of a group with that many blockers; Group/Take: how many members of
        /// each count (digits in base size + 1); Plain (last part of a creature blocking any number): -1 no block at all, 0 the
        /// other lures only, 1 and the wanted plain attackers, 2 and one plain attacker.
        /// </summary>
        private readonly record struct Move(int Group, int Level, long Take, int Plain, int Req, int Kept, bool Blocks);

        public Searcher(BlockRequest r, IReadOnlyList<BlockDeclaration> wanted)
        {
            // Creatures that can block any number decide last: decided first, their many choices would multiply the states every
            // later creature goes through.
            _blockers = r.Blockers.Distinct().OrderBy(r.CanBlockAny.Contains).ToArray();
            _wanted = wanted.Where(w => r.Able(w.Blocker, w.Attacker)).ToHashSet();
            var anySet = r.CanBlockAny.ToHashSet();
            var aloneSet = r.CantBlockAlone.ToHashSet();
            var mustSet = r.MustBeBlocked.ToHashSet();
            var lureSet = r.Lures.ToHashSet();
            _any = _blockers.Select(anySet.Contains).ToArray();
            _alone = _blockers.Select(aloneSet.Contains).ToArray();

            var attackers = r.Attackers.Distinct().ToList();
            bool Tracked(CardId a) => mustSet.Contains(a) || r.MinimumBlockers.GetValueOrDefault(a, 1) > 1 || r.MaximumBlockers.ContainsKey(a);
            string Signature(CardId a)
            {
                var sb = new StringBuilder();
                foreach (var b in _blockers) sb.Append(!r.Able(b, a) ? '0' : _wanted.Contains(new BlockDeclaration(b, a)) ? '2' : '1');
                sb.Append('|').Append(mustSet.Contains(a)).Append('|').Append(lureSet.Contains(a))
                  .Append('|').Append(r.MinimumBlockers.GetValueOrDefault(a, 1))
                  .Append('|').Append(r.MaximumBlockers.TryGetValue(a, out var max) ? max : -1);
                return sb.ToString();
            }
            _members = attackers.Where(Tracked).GroupBy(Signature).Select(g => g.ToArray()).ToArray();
            _groups = _members.Length;
            _size = _members.Select(g => g.Length).ToArray();
            _max = _members.Select(g => r.MaximumBlockers.TryGetValue(g[0], out var max) ? Math.Max(0, max) : -1).ToArray();
            _min = _members.Select(g => r.MinimumBlockers.GetValueOrDefault(g[0], 1)).ToArray();
            // Counts past what any rule looks at are equivalent: a maximum is never passed, past the minimum nothing changes.
            _cap = Enumerable.Range(0, _groups).Select(g => _max[g] >= 0 ? _max[g] : Math.Max(1, _min[g])).ToArray();
            _must = _members.Select(g => mustSet.Contains(g[0])).ToArray();
            _lure = _members.Select(g => lureSet.Contains(g[0])).ToArray();
            _able = _blockers.Select(b => _members.Select(g => r.Able(b, g[0])).ToArray()).ToArray();
            _wantedBy = _blockers.Select(b => _members.Select(g => _wanted.Contains(new BlockDeclaration(b, g[0]))).ToArray()).ToArray();
            _inert = Enumerable.Range(0, _groups).Select(g => !_must[g] && !_lure[g] && _wantedBy.All(w => !w[g])).ToArray();
            _level = Enumerable.Range(0, _groups).Select(g => { var l = new int[_cap[g] + 1]; l[0] = _size[g]; return l; }).ToArray();

            var others = attackers.Where(a => !Tracked(a)).ToList();
            int n = _blockers.Length;
            _bestOther = new CardId?[n];
            _bestOtherReq = new int[n];
            _bestOtherKept = new int[n];
            _otherLures = new CardId[n][];
            _otherLuresKept = new int[n];
            _wantedPlain = new CardId[n][];
            _onePlain = new CardId?[n];
            for (int i = 0; i < n; i++)
            {
                var able = others.Where(a => r.Able(_blockers[i], a)).ToList();
                foreach (var a in able)
                {
                    int req = lureSet.Contains(a) ? 1 : 0, kept = Kept(i, a);
                    if (_bestOther[i] is null || Better(req, kept, _bestOtherReq[i], _bestOtherKept[i]))
                        (_bestOther[i], _bestOtherReq[i], _bestOtherKept[i]) = (a, req, kept);
                }
                _otherLures[i] = able.Where(lureSet.Contains).ToArray();
                _otherLuresKept[i] = _otherLures[i].Sum(a => Kept(i, a));
                var plain = able.Where(a => !lureSet.Contains(a)).ToList();
                _wantedPlain[i] = plain.Where(a => _wanted.Contains(new BlockDeclaration(_blockers[i], a))).ToArray();
                _onePlain[i] = plain.Count == 0 ? null : _wantedPlain[i].Length > 0 ? _wantedPlain[i][0] : plain[0];
            }

            var steps = new List<(int, int)>();
            for (int i = 0; i < n; i++)
            {
                if (_any[i])
                    for (int g = 0; g < _groups; g++)
                        if (_able[i][g]) steps.Add((i, g));
                steps.Add((i, -1));
            }
            _steps = steps.ToArray();
            _moves = _steps.Select(_ => new List<Move>()).ToArray();

            // The state must fit in a key: step, creatures blocking (0, 1, 2+), "can't block alone" blocking, current
            // creature blocking, then each group's counts in mixed radix.
            double states = (_steps.Length + 1) * 12.0;
            for (int g = 0; g < _groups; g++) states *= Math.Pow(_size[g] + 1, _cap[g]);
            _fits = states < 9e18;

            // A group no later step can change is settled: checked and counted as soon as its last step is decided, and
            // left out of the state from then on.
            _last = Enumerable.Repeat(-1, _groups).ToArray();
            for (int step = 0; step < _steps.Length; step++)
            {
                var (i, group) = _steps[step];
                for (int g = 0; g < _groups; g++)
                    if (_able[i][g] && (_any[i] ? group == g : true)) _last[g] = step;
            }
            _settled = _steps.Select((_, step) => Enumerable.Range(0, _groups).Where(g => _last[g] == step).ToArray()).ToArray();
        }

        private int Kept(int blocker, CardId attacker) => _wanted.Contains(new BlockDeclaration(_blockers[blocker], attacker)) ? 2 : -3;

        private static bool Better(int req, int kept, int bestReq, int bestKept) => req > bestReq || (req == bestReq && kept > bestKept);

        /// <summary>The best declaration, or null when the board is too large for the exact search.</summary>
        public (int Requirements, IReadOnlyList<BlockDeclaration> Blocks)? Run()
        {
            if (!_fits) return null;
            var (req, _) = Best(0, 0, false, false);
            if (_exceeded || req == Unreachable) return null;
            return (req, Rebuild());
        }

        private long Key(int step, int blocking, bool alone, bool current)
        {
            // The group counts first: settled groups leave the key, so the step (which fixes the digits) comes last.
            long key = 0;
            for (int g = 0; g < _groups; g++)
            {
                if (_last[g] < step) continue; // settled
                var levels = _level[g];
                long radix = _size[g] + 1;
                for (int c = 1; c < levels.Length; c++) key = key * radix + levels[c];
            }
            key = key * 3 + blocking;
            key = key * 2 + (alone ? 1 : 0);
            key = key * 2 + (current ? 1 : 0);
            return key * (_steps.Length + 1) + step;
        }

        private List<Move> Moves(int step, bool current)
        {
            var (i, group) = _steps[step];
            var moves = _moves[step];
            moves.Clear();
            if (!_any[i])
            {
                // Blocking a lure beats not blocking: one more requirement, and one more blocking creature only hurts one
                // that can't block alone.
                if (_bestOtherReq[i] == 0 || _bestOther[i] is null || _alone[i]) moves.Add(new Move(-1, -1, 0, 0, 0, 0, false));
                if (_bestOther[i] is not null) moves.Add(new Move(-1, -1, 0, 0, _bestOtherReq[i], _bestOtherKept[i], true));
                for (int g = 0; g < _groups; g++)
                {
                    if (!_able[i][g]) continue;
                    int req = _lure[g] ? 1 : 0, kept = _wantedBy[i][g] ? 2 : -3;
                    var levels = _level[g];
                    // Blocking a lure (one more requirement) beats blocking a group with no requirement, unless this creature
                    // can't block alone: dropping that block (and the group's other blocks, if it falls under its minimum)
                    // loses no requirement and keeps this creature blocking.
                    if (!_must[g] && !_lure[g] && _bestOtherReq[i] > 0 && _bestOther[i] is not null && !_alone[i]) continue;
                    bool fresh = !_inert[g] || _size[g] - levels[0] < 2;
                    // A block that leaves the counts as they are (past the minimum, no maximum) and brings no requirement is
                    // no better than blocking another attacker.
                    bool idleAtCap = _max[g] < 0 && req == 0 && !_wantedBy[i][g] && _bestOther[i] is not null;
                    for (int c = 0; c < levels.Length; c++)
                        if (levels[c] > 0 && (_max[g] < 0 || c < _max[g]) && (c > 0 || fresh) && !(idleAtCap && c == _cap[g]))
                            moves.Add(new Move(g, c, 0, 0, req, kept, true));
                }
                return moves;
            }
            if (group >= 0)
            {
                // How many members with each count this creature blocks: an odometer over the counts.
                var levels = _level[group];
                long radix = _size[group] + 1;
                int keptEach = _wantedBy[i][group] ? 2 : -3;
                Span<int> take = stackalloc int[levels.Length];
                take.Clear();
                while (true)
                {
                    int total = 0;
                    long code = 0;
                    for (int c = levels.Length - 1; c >= 0; c--) { total += take[c]; code = code * radix + take[c]; }
                    if (!_inert[group] || _size[group] - levels[0] + take[0] <= 2)
                        moves.Add(new Move(group, -1, code, 0, _lure[group] ? total : 0, total * keptEach, total > 0));
                    int d = 0;
                    for (; d < levels.Length; d++)
                    {
                        if ((_max[group] < 0 || d < _max[group]) && take[d] < levels[d]) { take[d]++; break; }
                        take[d] = 0;
                    }
                    if (d == levels.Length) break;
                }
                return moves;
            }
            // Last part: every other lure it can block, and a plain choice.
            int lures = _otherLures[i].Length, luresKept = _otherLuresKept[i];
            if (!current && lures > 0 && _alone[i]) moves.Add(new Move(-1, -1, 0, -1, 0, 0, false));
            moves.Add(new Move(-1, -1, 0, 0, lures, luresKept, lures > 0));
            if (_wantedPlain[i].Length > 0) moves.Add(new Move(-1, -1, 0, 1, lures, luresKept + 2 * _wantedPlain[i].Length, true));
            // One plain attacker only makes it a blocking creature: no use once it blocks anyway.
            if (_onePlain[i] is { } one && !current && lures == 0) moves.Add(new Move(-1, -1, 0, 2, lures, luresKept + Kept(i, one), true));
            return moves;
        }

        /// <summary>Moves the blocked members of a group up a count (or back down, undoing it).</summary>
        private void Apply(in Move move, bool undo)
        {
            if (move.Group < 0) return;
            var levels = _level[move.Group];
            int top = levels.Length - 1, sign = undo ? -1 : 1;
            if (move.Level >= 0)
            {
                levels[move.Level] -= sign;
                levels[Math.Min(move.Level + 1, top)] += sign;
                return;
            }
            // All members move at once: from the highest count down, each count gives before it receives.
            long radix = _size[move.Group] + 1;
            Span<int> take = stackalloc int[levels.Length];
            long code = move.Take;
            for (int c = 0; c < take.Length; c++) { take[c] = (int)(code % radix); code /= radix; }
            for (int c = top; c >= 0; c--)
            {
                levels[c] -= sign * take[c];
                levels[Math.Min(c + 1, top)] += sign * take[c];
            }
        }

        private (int Blocking, bool Alone, bool Current) Next(int step, in Move move, int blocking, bool alone, bool current)
        {
            var (i, group) = _steps[step];
            bool blocks = current || move.Blocks;
            if (_any[i] && group >= 0) return (blocking, alone, blocks);
            int next = Math.Min(2, blocking + (blocks ? 1 : 0));
            // Once two creatures block, "can't block alone" is satisfied for good.
            return (next, next < 2 && (alone || (blocks && _alone[i])), false);
        }

        /// <summary>
        /// Checks the groups settled by this step: none left blocked by fewer than its minimum (rule 509.1b); returns the
        /// "must be blocked" requirements they obey.
        /// </summary>
        private int Settle(int step)
        {
            int req = 0;
            foreach (int g in _settled[step])
                for (int c = 1; c < _level[g].Length; c++)
                {
                    if (_level[g][c] == 0) continue;
                    if (c < _min[g]) return Unreachable;
                    if (_must[g]) req += _level[g][c];
                }
            return req;
        }

        private (int Req, int Kept) Best(int step, int blocking, bool alone, bool current)
        {
            if (step == _steps.Length)
            {
                // Every group was settled at its last step.
                return (alone && blocking < 2 ? Unreachable : 0, 0); // rule 506.5
            }
            if (_exceeded) return (Unreachable, 0);
            long key = Key(step, blocking, alone, current);
            if (_memo.TryGetValue(key, out var known)) return (known.Req, known.Kept);
            if (_memo.Count >= StateBudget) { _exceeded = true; return (Unreachable, 0); }
            int bestReq = Unreachable, bestKept = 0, choice = -1;
            var moves = Moves(step, current);
            for (int m = 0; m < moves.Count; m++)
            {
                var move = moves[m];
                Apply(move, false);
                int settled = Settle(step);
                var (req, kept) = (Unreachable, 0);
                if (settled != Unreachable)
                {
                    var (nb, na, nc) = Next(step, move, blocking, alone, current);
                    (req, kept) = Best(step + 1, nb, na, nc);
                }
                Apply(move, true);
                if (req == Unreachable) continue;
                req += settled + move.Req;
                kept += move.Kept;
                if (choice < 0 || Better(req, kept, bestReq, bestKept)) (bestReq, bestKept, choice) = (req, kept, m);
            }
            _memo[key] = (bestReq, bestKept, choice);
            return (bestReq, bestKept);
        }

        /// <summary>Walks the memo from the start to rebuild the chosen declaration, picking actual members of each group.</summary>
        private List<BlockDeclaration> Rebuild()
        {
            var result = new List<BlockDeclaration>();
            var at = Enumerable.Range(0, _groups).Select(g =>
            {
                var lists = Enumerable.Range(0, _level[g].Length).Select(_ => new List<CardId>()).ToArray();
                lists[0].AddRange(_members[g]);
                return lists;
            }).ToArray();
            int blocking = 0;
            bool alone = false, current = false;
            for (int step = 0; step < _steps.Length; step++)
            {
                var (i, _) = _steps[step];
                var b = _blockers[i];
                var move = Moves(step, current)[_memo[Key(step, blocking, alone, current)].Choice];
                if (move.Group >= 0)
                {
                    // Members taken from each count, then moved up a count (as Apply does).
                    var levels = at[move.Group];
                    int top = levels.Length - 1;
                    long radix = _size[move.Group] + 1, code = move.Take;
                    var picked = new List<(int Count, List<CardId> Members)>();
                    for (int c = 0; c <= top; c++)
                    {
                        int n = move.Level >= 0 ? (c == move.Level ? 1 : 0) : (int)(code % radix);
                        code /= radix;
                        var from = levels[c];
                        picked.Add((c, from.GetRange(from.Count - n, n)));
                        from.RemoveRange(from.Count - n, n);
                    }
                    foreach (var (c, members) in picked)
                    {
                        levels[Math.Min(c + 1, top)].AddRange(members);
                        result.AddRange(members.Select(a => new BlockDeclaration(b, a)));
                    }
                }
                else if (!_any[i])
                {
                    if (move.Blocks) result.Add(new BlockDeclaration(b, _bestOther[i]!.Value));
                }
                else if (move.Plain >= 0)
                {
                    result.AddRange(_otherLures[i].Select(a => new BlockDeclaration(b, a)));
                    if (move.Plain == 1) result.AddRange(_wantedPlain[i].Select(a => new BlockDeclaration(b, a)));
                    if (move.Plain == 2) result.Add(new BlockDeclaration(b, _onePlain[i]!.Value));
                }
                Apply(move, false);
                (blocking, alone, current) = Next(step, move, blocking, alone, current);
            }
            return result;
        }
    }
}
