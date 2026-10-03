// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Arcanum.Engine.Mana;

/// <summary>
/// A mana cost such as {2}{G}{G}. Hybrid, Phyrexian and X costs arrive with the ability system (M4).
/// </summary>
public sealed record ManaCost
{
    public static readonly ManaCost Zero = new(0, Array.Empty<ManaType>());

    public int Generic { get; }

    /// <summary>Specific-type pips (colored or {C}), in printed order.</summary>
    public IReadOnlyList<ManaType> Pips { get; }

    public ManaCost(int generic, IReadOnlyList<ManaType> pips)
    {
        if (generic < 0) throw new ArgumentOutOfRangeException(nameof(generic));
        Generic = generic;
        Pips = pips;
    }

    public int ManaValue => Generic + Pips.Count;

    /// <summary>This cost plus extra generic mana (cost increases such as commander tax).</summary>
    public ManaCost PlusGeneric(int extra) => extra == 0 ? this : new ManaCost(Generic + extra, Pips);

    public static ManaCost Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Zero;
        int generic = 0;
        var pips = new List<ManaType>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] != '{') throw new FormatException($"Invalid mana cost '{text}'.");
            int close = text.IndexOf('}', i);
            if (close < 0) throw new FormatException($"Unclosed symbol in mana cost '{text}'.");
            string symbol = text.Substring(i + 1, close - i - 1);
            if (int.TryParse(symbol, out int n)) generic += n;
            else if (symbol.Length == 1 && ManaTypeExtensions.TryParse(symbol[0], out var type)) pips.Add(type);
            else throw new FormatException($"Unsupported mana symbol '{{{symbol}}}' in '{text}'.");
            i = close + 1;
        }
        return new ManaCost(generic, pips);
    }

    public override string ToString()
    {
        if (ManaValue == 0) return "{0}";
        var sb = new StringBuilder();
        if (Generic > 0) sb.Append('{').Append(Generic).Append('}');
        foreach (var pip in Pips) sb.Append('{').Append(pip.ToSymbol()).Append('}');
        return sb.ToString();
    }

    public bool Equals(ManaCost? other) =>
        other is not null && Generic == other.Generic && Pips.SequenceEqual(other.Pips);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Generic);
        foreach (var pip in Pips) hash.Add(pip);
        return hash.ToHashCode();
    }
}
