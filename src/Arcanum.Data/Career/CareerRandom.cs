// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.Career;

/// <summary>
/// Randomness a career derives instead of storing: the same career seed, purpose and numbers always give the same
/// sequence, on any machine and .NET version (so boosters and opponents can be recomputed to check a career).
/// </summary>
public sealed class CareerRandom : Random
{
    private ulong _state;

    public CareerRandom(ulong seed) => _state = seed;

    /// <summary>The generator for one use: <paramref name="purpose"/> ("booster", "daily"...) and its numbers (day, counter).</summary>
    public static CareerRandom For(ulong careerSeed, string purpose, params long[] numbers)
    {
        ulong h = Mix(careerSeed ^ 0x9E3779B97F4A7C15UL);
        foreach (char c in purpose) h = Mix(h ^ c);
        foreach (long n in numbers) h = Mix(h ^ (ulong)n);
        return new CareerRandom(h);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>SplitMix64.</summary>
    private ulong NextULong()
    {
        _state += 0x9E3779B97F4A7C15UL;
        return Mix(_state);
    }

    public override int Next() => (int)(NextULong() >> 33);

    public override int Next(int maxValue)
    {
        if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return maxValue <= 1 ? 0 : (int)(NextULong() % (ulong)maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
        return minValue + (int)(NextULong() % (ulong)Math.Max(1L, (long)maxValue - minValue));
    }

    public override long NextInt64() => (long)(NextULong() >> 1);

    public override double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    protected override double Sample() => NextDouble();

    public override void NextBytes(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextULong();
    }

    public override void NextBytes(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextULong();
    }
}
