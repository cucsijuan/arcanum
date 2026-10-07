// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;

namespace Arcanum.Engine.Tests;

/// <summary>
/// The block declaration search (rule 509.1c) against brute force on small boards, and its speed on large ones.
/// </summary>
public class BlockSearchTests
{
    private static CardId A(int i) => new(i + 1);
    private static CardId B(int i) => new(i + 100);

    private static BlockRequest RandomBoard(Random rng)
    {
        int na = rng.Next(1, 5), nb = rng.Next(1, 5);
        var attackers = Enumerable.Range(0, na).Select(A).ToList();
        var blockers = Enumerable.Range(0, nb).Select(B).ToList();
        var canBlock = blockers.ToDictionary(b => b, b => (IReadOnlyList<CardId>)attackers.Where(_ => rng.Next(4) > 0).ToList());
        var minimum = attackers.Where(_ => rng.Next(3) == 0).ToDictionary(a => a, _ => rng.Next(2, 4));
        var maximum = attackers.Where(_ => rng.Next(4) == 0).ToDictionary(a => a, _ => rng.Next(1, 3));
        return new BlockRequest(attackers, blockers, canBlock, minimum)
        {
            MaximumBlockers = maximum,
            MustBeBlocked = attackers.Where(_ => rng.Next(3) == 0).ToList(),
            Lures = attackers.Where(_ => rng.Next(4) == 0).ToList(),
            CanBlockAny = blockers.Where(_ => rng.Next(5) == 0).ToList(),
            CantBlockAlone = blockers.Where(_ => rng.Next(4) == 0).ToList(),
        };
    }

    /// <summary>Every declaration the blockers could make: one attacker or none each, any set for those that block any number.</summary>
    private static List<List<BlockDeclaration>> AllDeclarations(BlockRequest r)
    {
        var result = new List<List<BlockDeclaration>> { new() };
        foreach (var b in r.Blockers)
        {
            var able = r.CanBlock.TryGetValue(b, out var a) ? a : Array.Empty<CardId>();
            var choices = new List<List<CardId>> { new() };
            if (r.CanBlockAny.Contains(b))
                for (int mask = 1; mask < 1 << able.Count; mask++) choices.Add(able.Where((_, k) => (mask >> k & 1) == 1).ToList());
            else choices.AddRange(able.Select(x => new List<CardId> { x }));
            result = result.SelectMany(d => choices.Select(c => d.Concat(c.Select(x => new BlockDeclaration(b, x))).ToList())).ToList();
        }
        return result;
    }

    /// <summary>The restrictions, checked directly.</summary>
    private static bool Restricted(BlockRequest r, List<BlockDeclaration> d)
    {
        foreach (var a in r.Attackers)
        {
            int n = d.Count(x => x.Attacker == a);
            if (n > 0 && n < r.MinimumBlockers.GetValueOrDefault(a, 1)) return false;
            if (r.MaximumBlockers.TryGetValue(a, out var max) && n > max) return false;
        }
        var blocking = d.Select(x => x.Blocker).Distinct().ToList();
        return !(blocking.Count == 1 && r.CantBlockAlone.Contains(blocking[0]));
    }

    private static int Kept(List<BlockDeclaration> wanted, IEnumerable<BlockDeclaration> d) => d.Sum(x => wanted.Contains(x) ? 2 : -3);

    [Fact]
    public void TheSearchMatchesBruteForceOnSmallBoards()
    {
        var rng = new Random(509);
        for (int board = 0; board < 600; board++)
        {
            var r = RandomBoard(rng);
            var all = AllDeclarations(r);
            var restricted = all.Where(d => Restricted(r, d)).ToList();
            int max = restricted.Max(r.ObeyedRequirements);
            Assert.Equal(r.HasRequirements ? max : 0, r.MaxRequirements);
            foreach (var d in all)
                Assert.Equal(Restricted(r, d) && (!r.HasRequirements || r.ObeyedRequirements(d) == max), r.IsLegal(d, out _));

            // Any wanted declaration (even an impossible one) is completed into a legal one; with requirements, the closest.
            for (int w = 0; w < 3; w++)
            {
                var wanted = r.Blockers.SelectMany(b => r.Attackers.Where(_ => rng.Next(3) == 0).Select(a => new BlockDeclaration(b, a))).ToList();
                var completed = r.Complete(wanted).ToList();
                Assert.True(r.IsLegal(completed, out var reason), reason);
                if (r.HasRequirements)
                    Assert.Equal(restricted.Where(d => r.ObeyedRequirements(d) == max).Max(d => Kept(wanted, d)), Kept(wanted, completed));
            }
        }
    }

    /// <summary>
    /// 20 attackers: 8 with menace (one of them a lure), 4 that must be blocked, 2 lures, 2 that can't be blocked by more
    /// than one creature, 4 flyers; 20 blockers: 2 Palace Guards, 2 that can't block alone, 4 with reach.
    /// </summary>
    private static BlockRequest MixedBoard()
    {
        var a = Enumerable.Range(0, 20).Select(A).ToList();
        var b = Enumerable.Range(0, 20).Select(B).ToList();
        var canBlock = b.Select((x, j) => (x, j)).ToDictionary(p => p.x, p => (IReadOnlyList<CardId>)a.Where((_, i) => i < 16 || p.j >= 16).ToList());
        return new BlockRequest(a, b, canBlock, a.Take(8).ToDictionary(x => x, _ => 2))
        {
            MustBeBlocked = a.Skip(8).Take(4).ToList(),
            Lures = new[] { a[7], a[12], a[13] },
            MaximumBlockers = a.Skip(14).Take(2).ToDictionary(x => x, _ => 1),
            CanBlockAny = new[] { b[0], b[1] },
            CantBlockAlone = new[] { b[2], b[3] },
        };
    }

    [Fact]
    public void AMixedBoardOfTwentyIsDecidedQuickly()
    {
        var warmUp = MixedBoard();
        warmUp.Complete(new[] { new BlockDeclaration(B(5), A(0)) });
        var request = MixedBoard();
        // Two blockers wanted on a menace attacker, one on another (illegal: menace), one on a lure.
        var wanted = new[] { new BlockDeclaration(B(4), A(0)), new BlockDeclaration(B(5), A(0)), new BlockDeclaration(B(6), A(1)), new BlockDeclaration(B(7), A(12)) };
        var watch = Stopwatch.StartNew();
        bool none = request.IsLegal(Array.Empty<BlockDeclaration>(), out _);
        var completed = request.Complete(wanted);
        bool legal = request.IsLegal(completed, out var reason);
        watch.Stop();
        Assert.False(none);
        Assert.True(legal, reason);
        Assert.Contains(new BlockDeclaration(B(7), A(12)), completed);
        // Every must-be-blocked attacker blocked (by the Guards), the 18 other blockers each on a lure, the Guards on all three.
        Assert.Equal(4 + 18 + 2 * 3, request.MaxRequirements);
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ABoardTooLargeForTheSearchStillGetsALegalDeclaration()
    {
        // Many different kinds of attackers and blockers: the safety net answers, and its answer is legal.
        var a = Enumerable.Range(0, 20).Select(A).ToList();
        var b = Enumerable.Range(0, 20).Select(B).ToList();
        var canBlock = b.Select((x, j) => (x, j)).ToDictionary(p => p.x, p => (IReadOnlyList<CardId>)a.Where((_, i) => i % 7 != 6 || p.j % 3 == 0).ToList());
        var request = new BlockRequest(a, b, canBlock, a.Where((_, i) => i % 2 == 0).ToDictionary(x => x, _ => 2))
        {
            MustBeBlocked = a.Where((_, i) => i % 3 == 0).ToList(),
            Lures = a.Where((_, i) => i % 5 == 1).ToList(),
            MaximumBlockers = a.Where((_, i) => i % 8 == 3).ToDictionary(x => x, _ => 1),
            CanBlockAny = b.Where((_, j) => j % 9 == 4).ToList(),
            CantBlockAlone = b.Where((_, j) => j % 6 == 5).ToList(),
        };
        var wanted = Enumerable.Range(0, 20).Select(j => new BlockDeclaration(B(j), A(j / 2 * 2))).ToList();
        var completed = request.Complete(wanted);
        Assert.True(request.IsLegal(completed, out var reason), reason);
        Assert.True(request.IsLegal(request.Complete(Array.Empty<BlockDeclaration>()), out reason), reason);
    }

    [Fact]
    public void AWantedDeclarationMakingEveryAttackerDifferentIsStillCompletedLegally()
    {
        // Pairs of blockers wanted on four menace attackers, single ones on four others: no two menace attackers look
        // alike to the search any more.
        var request = MixedBoard();
        var wanted = Enumerable.Range(4, 8).Select(j => new BlockDeclaration(B(j), A(j / 2 - 2)))
            .Concat(Enumerable.Range(12, 4).Select(j => new BlockDeclaration(B(j), A(j - 8)))).ToList();
        var completed = request.Complete(wanted);
        Assert.True(request.IsLegal(completed, out var reason), reason);
        Assert.Equal(4 + 18 + 2 * 3, request.ObeyedRequirements(completed)); // the exact maximum still
    }

    [Fact]
    public void ManyMenaceAttackersWithoutRequirementsAreDecidedQuickly()
    {
        var a = Enumerable.Range(0, 14).Select(A).ToList();
        var b = Enumerable.Range(0, 14).Select(B).ToList();
        var request = new BlockRequest(a, b, b.ToDictionary(x => x, _ => (IReadOnlyList<CardId>)a), a.ToDictionary(x => x, _ => 2));
        var watch = Stopwatch.StartNew();
        Assert.True(request.IsLegal(Array.Empty<BlockDeclaration>(), out _));
        // One blocker each: none can stay (menace).
        Assert.Empty(request.Complete(b.Select((x, j) => new BlockDeclaration(x, a[j])).ToList()));
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
    }
}
