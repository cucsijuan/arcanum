// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Mana;

public sealed class ManaPool
{
    private readonly int[] _amounts = new int[6];

    /// <summary>Mana that doesn't empty between steps until the end of the turn ("you don't lose this mana").</summary>
    private readonly int[] _untilEndOfTurn = new int[6];

    public int this[ManaType type] => _amounts[(int)type];

    public int Total => _amounts.Sum();

    public bool IsEmpty => Total == 0;

    public void Add(ManaType type, int amount = 1) => _amounts[(int)type] += amount;

    public void Remove(ManaType type, int amount = 1)
    {
        if (_amounts[(int)type] < amount) throw new InvalidOperationException($"Not enough {type} mana in pool.");
        _amounts[(int)type] -= amount;
        _untilEndOfTurn[(int)type] = Math.Min(_untilEndOfTurn[(int)type], _amounts[(int)type]); // spend ordinary mana first
    }

    /// <summary>Adds mana that stays until the end of the turn.</summary>
    public void AddUntilEndOfTurn(ManaType type, int amount = 1)
    {
        _amounts[(int)type] += amount;
        _untilEndOfTurn[(int)type] += amount;
    }

    /// <summary>Empties the pool between steps (rule 500.4), keeping mana that lasts until end of turn unless <paramref name="endOfTurn"/>.</summary>
    public void Clear(bool endOfTurn = true)
    {
        if (endOfTurn) Array.Clear(_untilEndOfTurn);
        for (int i = 0; i < _amounts.Length; i++) _amounts[i] = _untilEndOfTurn[i];
    }

    public ManaPool Clone()
    {
        var copy = new ManaPool();
        _amounts.CopyTo(copy._amounts, 0);
        return copy;
    }
}
