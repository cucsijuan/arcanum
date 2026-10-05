// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Arcanum.Net.Services;

/// <summary>
/// Short codes a host shares so friends can find their lobby: six characters from an alphabet without look-alikes
/// (no 0/O, 1/I/L), shown as "ABC-DEF". Case, spaces and dashes don't matter when it's typed.
/// </summary>
public static class InviteCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string New()
    {
        var chars = new char[Length];
        for (int i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>"ABCDEF" shown as "ABC-DEF".</summary>
    public static string Display(string code) => code.Length == Length ? $"{code[..3]}-{code[3..]}" : code;

    /// <summary>Reads what a player typed; false if it can't be a code.</summary>
    public static bool TryParse(string? text, out string code)
    {
        code = "";
        if (text is null) return false;
        var sb = new StringBuilder();
        foreach (var raw in text.ToUpperInvariant())
        {
            if (raw is ' ' or '-' or '_' or '.') continue;
            if (Alphabet.IndexOf(raw) < 0) return false;
            sb.Append(raw);
        }
        if (sb.Length != Length) return false;
        code = sb.ToString();
        return true;
    }
}
