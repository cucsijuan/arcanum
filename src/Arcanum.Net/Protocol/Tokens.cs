// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Arcanum.Net.Protocol;

/// <summary>Seat secrets: random, URL-safe, compared in constant time.</summary>
public static class Tokens
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    public static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
