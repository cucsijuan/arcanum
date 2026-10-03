// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Tests;

public class RngTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new DeterministicRng(42);
        var b = new DeterministicRng(42);
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void DifferentSeedsDiffer() =>
        Assert.NotEqual(new DeterministicRng(1).NextUInt(), new DeterministicRng(2).NextUInt());

    [Fact]
    public void ShuffleIsAPermutation()
    {
        var list = Enumerable.Range(0, 60).ToList();
        new DeterministicRng(7).Shuffle(list);
        Assert.Equal(Enumerable.Range(0, 60), list.OrderBy(x => x));
        Assert.NotEqual(Enumerable.Range(0, 60), list);
    }

    [Fact]
    public void NextStaysInRange()
    {
        var rng = new DeterministicRng(3);
        for (int i = 0; i < 1000; i++) Assert.InRange(rng.Next(6), 0, 5);
    }
}
