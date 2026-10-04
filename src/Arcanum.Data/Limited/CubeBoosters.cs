// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;

namespace Arcanum.Data.Limited;

/// <summary>
/// A cube: a fixed list of cards dealt into boosters of <see cref="BoosterSize"/> without replacement (each card in
/// the list is opened at most once per event).
/// </summary>
public sealed class CubeBoosters : IBoosterSource
{
    private readonly List<PoolCard> _remaining;

    public string Name { get; }
    public int BoosterSize { get; }

    /// <summary>Cards left to deal.</summary>
    public int Remaining => _remaining.Count;

    public CubeBoosters(string name, IEnumerable<PoolCard> cards, int boosterSize, Random shuffle)
    {
        Name = name;
        BoosterSize = boosterSize;
        _remaining = cards.ToList();
        for (int i = _remaining.Count - 1; i > 0; i--)
        {
            int j = shuffle.Next(i + 1);
            (_remaining[i], _remaining[j]) = (_remaining[j], _remaining[i]);
        }
    }

    /// <summary>The cube's cards from a deck list (main section; counts are copies), with each line's printing when known.</summary>
    public static List<PoolCard> CardsFrom(DeckList list, CardDatabase cards, List<string>? unknown = null)
    {
        var result = new List<PoolCard>();
        foreach (var entry in list.Main)
        {
            if (cards.Find(entry.Name) is not { } card) { unknown?.Add(entry.Name); continue; }
            var printing = entry.Set is { } set ? card.Record.FindPrinting(set, entry.Number) : null;
            printing ??= card.Record.Printings.LastOrDefault();
            var pc = printing is null ? new PoolCard(card.Name, "", "", "") : PoolCard.Of(card, printing);
            result.AddRange(Enumerable.Repeat(pc, entry.Count));
        }
        return result;
    }

    public List<PoolCard> Open(Random random)
    {
        if (_remaining.Count < BoosterSize) throw new InvalidOperationException($"The cube has only {_remaining.Count} cards left; a booster needs {BoosterSize}.");
        var booster = _remaining.Take(BoosterSize).ToList();
        _remaining.RemoveRange(0, BoosterSize);
        return booster;
    }
}
