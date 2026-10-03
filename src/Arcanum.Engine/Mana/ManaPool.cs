// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Mana;

public sealed class ManaPool
{
    private readonly int[] _amounts = new int[6];

    public int this[ManaType type] => _amounts[(int)type];

    public int Total => _amounts.Sum();

    public bool IsEmpty => Total == 0;

    public void Add(ManaType type, int amount = 1) => _amounts[(int)type] += amount;

    public void Remove(ManaType type, int amount = 1)
    {
        if (_amounts[(int)type] < amount) throw new InvalidOperationException($"Not enough {type} mana in pool.");
        _amounts[(int)type] -= amount;
    }

    public void Clear() => Array.Clear(_amounts);

    public ManaPool Clone()
    {
        var copy = new ManaPool();
        _amounts.CopyTo(copy._amounts, 0);
        return copy;
    }
}
