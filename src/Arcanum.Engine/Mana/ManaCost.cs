// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Arcanum.Engine.Mana;

/// <summary>A hybrid symbol such as {G/W}: paid with either type of mana.</summary>
public readonly record struct HybridPip(ManaType First, ManaType Second)
{
    public override string ToString() => $"{{{First.ToSymbol()}/{Second.ToSymbol()}}}";
}

/// <summary>
/// A mana cost such as {2}{G}{G}, {X}{R} or {1}{G/W}. Payment works on concrete costs: X is replaced by the chosen
/// number (<see cref="WithX"/>) and each hybrid symbol by one of its halves (<see cref="Variants"/>).
/// Phyrexian symbols aren't supported yet.
/// </summary>
public sealed record ManaCost
{
    public static readonly ManaCost Zero = new(0, Array.Empty<ManaType>());

    public int Generic { get; }

    /// <summary>Specific-type pips (colored or {C}), in printed order.</summary>
    public IReadOnlyList<ManaType> Pips { get; }

    /// <summary>Hybrid symbols, each paid with either of its two types.</summary>
    public IReadOnlyList<HybridPip> Hybrid { get; }

    /// <summary>Number of {X} symbols.</summary>
    public int XCount { get; }

    public ManaCost(int generic, IReadOnlyList<ManaType> pips, IReadOnlyList<HybridPip>? hybrid = null, int xCount = 0)
    {
        if (generic < 0) throw new ArgumentOutOfRangeException(nameof(generic));
        Generic = generic;
        Pips = pips;
        Hybrid = hybrid ?? Array.Empty<HybridPip>();
        XCount = xCount;
    }

    /// <summary>Mana value (X counts as 0 outside the stack, rule 202.3e).</summary>
    public int ManaValue => Generic + Pips.Count + Hybrid.Count;

    public bool IsConcrete => Hybrid.Count == 0 && XCount == 0;

    /// <summary>This cost plus extra generic mana (cost increases such as commander tax).</summary>
    public ManaCost PlusGeneric(int extra) => extra == 0 ? this : new ManaCost(Generic + extra, Pips, Hybrid, XCount);

    /// <summary>This cost with up to <paramref name="amount"/> less generic mana (cost reductions).</summary>
    public ManaCost MinusGeneric(int amount) => amount <= 0 ? this : new ManaCost(Math.Max(0, Generic - amount), Pips, Hybrid, XCount);

    /// <summary>The sum of two costs (a spell's cost plus kicker, for example).</summary>
    public ManaCost Plus(ManaCost other) =>
        new(Generic + other.Generic, Pips.Concat(other.Pips).ToList(), Hybrid.Concat(other.Hybrid).ToList(), XCount + other.XCount);

    /// <summary>The cost with each {X} replaced by <paramref name="x"/> generic mana.</summary>
    public ManaCost WithX(int x) => XCount == 0 ? this : new ManaCost(Generic + x * XCount, Pips, Hybrid);

    /// <summary>Every concrete cost this one can be paid as, choosing a half of each hybrid symbol.</summary>
    public IEnumerable<ManaCost> Variants()
    {
        if (Hybrid.Count == 0)
        {
            yield return this;
            yield break;
        }
        for (int mask = 0; mask < 1 << Hybrid.Count; mask++)
        {
            var pips = Pips.ToList();
            for (int i = 0; i < Hybrid.Count; i++) pips.Add((mask & (1 << i)) == 0 ? Hybrid[i].First : Hybrid[i].Second);
            yield return new ManaCost(Generic, pips, null, XCount);
        }
    }

    /// <summary>Colors of the colored symbols (W, U, B, R, G), in WUBRG order.</summary>
    public IReadOnlyList<string> Colors()
    {
        var types = Pips.Concat(Hybrid.SelectMany(h => new[] { h.First, h.Second })).Where(t => t != ManaType.Colorless).ToHashSet();
        return new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }
            .Where(types.Contains).Select(t => t.ToSymbol().ToString()).ToList();
    }

    public static ManaCost Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Zero;
        int generic = 0, x = 0;
        var pips = new List<ManaType>();
        var hybrid = new List<HybridPip>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '{') throw new FormatException($"Invalid mana cost '{text}'.");
            int close = text.IndexOf('}', i);
            if (close < 0) throw new FormatException($"Unclosed symbol in mana cost '{text}'.");
            string symbol = text.Substring(i + 1, close - i - 1);
            if (int.TryParse(symbol, out int n)) generic += n;
            else if (symbol is "X" or "x") x++;
            else if (symbol.Length == 1 && ManaTypeExtensions.TryParse(symbol[0], out var type)) pips.Add(type);
            else if (symbol.Length == 3 && symbol[1] == '/' && ManaTypeExtensions.TryParse(symbol[0], out var a) && ManaTypeExtensions.TryParse(symbol[2], out var b)
                     && symbol[2] != 'P' && symbol[2] != 'p')
                hybrid.Add(new HybridPip(a, b));
            else throw new FormatException($"Unsupported mana symbol '{{{symbol}}}' in '{text}'.");
            i = close + 1;
        }
        return new ManaCost(generic, pips, hybrid, x);
    }

    public override string ToString()
    {
        if (ManaValue == 0 && XCount == 0) return "{0}";
        var sb = new StringBuilder();
        for (int i = 0; i < XCount; i++) sb.Append("{X}");
        if (Generic > 0) sb.Append('{').Append(Generic).Append('}');
        foreach (var h in Hybrid) sb.Append(h);
        foreach (var pip in Pips) sb.Append('{').Append(pip.ToSymbol()).Append('}');
        return sb.ToString();
    }

    public bool Equals(ManaCost? other) =>
        other is not null && Generic == other.Generic && XCount == other.XCount && Pips.SequenceEqual(other.Pips) && Hybrid.SequenceEqual(other.Hybrid);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Generic);
        hash.Add(XCount);
        foreach (var pip in Pips) hash.Add(pip);
        foreach (var h in Hybrid) hash.Add(h);
        return hash.ToHashCode();
    }
}
