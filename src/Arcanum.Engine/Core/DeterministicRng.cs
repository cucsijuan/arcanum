// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Core;

/// <summary>
/// xoshiro128** PRNG. Same seed produces the same sequence on every platform and .NET version,
/// which replays, tests and the online server depend on (System.Random gives no such guarantee).
/// </summary>
public sealed class DeterministicRng
{
    private uint _s0, _s1, _s2, _s3;

    public DeterministicRng(ulong seed)
    {
        // Expand the seed with SplitMix64 so that small seeds still produce well-mixed state.
        ulong x = seed;
        ulong a = SplitMix(ref x), b = SplitMix(ref x);
        _s0 = (uint)a; _s1 = (uint)(a >> 32); _s2 = (uint)b; _s3 = (uint)(b >> 32);
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public uint NextUInt()
    {
        uint result = RotL(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotL(_s3, 11);
        return result;
    }

    /// <summary>Uniform integer in [0, maxExclusive) without modulo bias.</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        uint bound = (uint)maxExclusive;
        uint threshold = (uint)(-bound % bound);
        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold) return (int)(r % bound);
        }
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static uint RotL(uint x, int k) => (x << k) | (x >> (32 - k));
}
