// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace Arcanum.Net.Protocol;

/// <summary>
/// The card ids one player sees. Real ids follow the order of each deck list, so sending them would tell an opponent
/// which card a hidden one is. Each connection gets its own random ids instead, and a card in a library the player
/// doesn't know (it went in unseen, its library was shuffled, or it was put back in an order they didn't see) gets a new
/// one, so it can't be followed through a hidden zone.
/// </summary>
public sealed class CardAliases
{
    private readonly Dictionary<int, int> _toAlias = new();
    private readonly Dictionary<int, int> _toReal = new();

    public int ToAlias(int real)
    {
        if (_toAlias.TryGetValue(real, out int alias)) return alias;
        do alias = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        while (_toReal.ContainsKey(alias));
        _toAlias[real] = alias;
        _toReal[alias] = real;
        return alias;
    }

    /// <summary>The real id behind <paramref name="alias"/>; false for ids this player was never given (or no longer has).</summary>
    public bool TryToReal(int alias, out int real) => _toReal.TryGetValue(alias, out real);

    /// <summary>The card becomes a new, unknown object for this player: its next appearance gets a new id.</summary>
    public void Forget(int real)
    {
        if (_toAlias.Remove(real, out int alias)) _toReal.Remove(alias);
    }
}
