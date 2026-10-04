// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Mana;

/// <summary>
/// Mana with strings attached (rule 106.6): it can only be spent on what <see cref="OnlyFor"/> allows (spells, and
/// abilities too when <see cref="AbilitiesToo"/>), or something happens when it is spent (<see cref="Rider"/>).
/// </summary>
public sealed record ManaUnit(ManaType Type, Core.CardId Source, Abilities.ObjectFilter? OnlyFor, bool AbilitiesToo, Abilities.ManaRider Rider);

public sealed class ManaPool
{
    private readonly int[] _amounts = new int[6];

    /// <summary>Restricted or rider mana, kept apart so it is only spent where it may be.</summary>
    private readonly List<ManaUnit> _special = new();

    public IReadOnlyList<ManaUnit> Special => _special;

    public void AddSpecial(ManaUnit unit) => _special.Add(unit);

    public void RemoveSpecial(ManaUnit unit)
    {
        if (!_special.Remove(unit)) throw new InvalidOperationException("That mana isn't in the pool.");
    }

    /// <summary>Mana that doesn't empty between steps until the end of the turn ("you don't lose this mana").</summary>
    private readonly int[] _untilEndOfTurn = new int[6];

    /// <summary>Ordinary mana of a type (spendable on anything).</summary>
    public int this[ManaType type] => _amounts[(int)type];

    /// <summary>All mana of a type, including restricted mana.</summary>
    public int AllOf(ManaType type) => _amounts[(int)type] + _special.Count(u => u.Type == type);

    public int Total => _amounts.Sum() + _special.Count;

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
    /// <param name="keep">Mana of this type isn't lost ("you don't lose unspent green mana as steps and phases end").</param>
    public void Clear(bool endOfTurn = true, ManaType? keep = null)
    {
        if (endOfTurn) Array.Clear(_untilEndOfTurn);
        for (int i = 0; i < _amounts.Length; i++)
            if (keep is not { } kept || (int)kept != i) _amounts[i] = Math.Max(_untilEndOfTurn[i], 0);
        _special.RemoveAll(u => keep is not { } k || u.Type != k);
    }

    public ManaPool Clone()
    {
        var copy = new ManaPool();
        _amounts.CopyTo(copy._amounts, 0);
        copy._special.AddRange(_special);
        return copy;
    }
}
