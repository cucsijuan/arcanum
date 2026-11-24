// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>
/// The cards a player could have in a game, guessed from the cards seen from them (never from their deck), for the
/// computer players that imagine an opponent's hidden cards: in a limited game the set being played; otherwise the sets
/// the seen cards (and the commander) were printed in. Each card comes with a weight: how often it turns up next to the
/// others, from the set's win rates when the module has them (better cards are played more) and, in limited, from its
/// rarity (how often a booster has it). Only cards the engine fully supports are offered; basic lands always are.
/// </summary>
public sealed class CardPoolGuess
{
    private readonly CardDatabase _db;
    private readonly string? _limitedSet;
    private readonly Func<string, DraftRatings?> _ratings;
    private readonly Dictionary<string, IReadOnlyList<(CardDefinition Card, double Weight)>> _bySet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DraftRatings?> _ratingsBySet = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<(CardDefinition Card, double Weight)>? _basics;
    private readonly object _gate = new();

    /// <param name="limitedSet">The set of a draft or sealed game; null for constructed and commander games.</param>
    /// <param name="ratings">The win rates of a set (null when the module has none), e.g. ContentModule.LoadDraftRatings.</param>
    public CardPoolGuess(CardDatabase db, string? limitedSet = null, Func<string, DraftRatings?>? ratings = null)
    {
        _db = db;
        _limitedSet = limitedSet;
        _ratings = ratings ?? (_ => null);
    }

    /// <summary>The candidates for a player from whom <paramref name="seen"/> were seen; empty when nothing tells where they come from.</summary>
    public IReadOnlyList<(CardDefinition Card, double Weight)> For(IReadOnlyList<CardDefinition> seen)
    {
        var sets = _limitedSet is { } set ? new[] { set } : SetsOf(seen);
        if (sets.Count == 0) return Array.Empty<(CardDefinition, double)>();
        var best = new Dictionary<string, (CardDefinition Card, double Weight)>();
        foreach (var s in sets)
            foreach (var (card, weight) in InSet(s))
                if (!best.TryGetValue(card.Name, out var had) || had.Weight < weight) best[card.Name] = (card, weight);
        foreach (var basic in Basics())
            best.TryAdd(basic.Card.Name, basic);
        return best.Values.ToList();
    }

    /// <summary>The sets of the printings the seen cards are shown with (basic lands, printed everywhere, say nothing).</summary>
    public IReadOnlyList<string> SetsOf(IEnumerable<CardDefinition> seen) =>
        seen.Where(c => !c.IsToken && !(c.Is(CardType.Land) && (c.Supertypes & Supertype.Basic) != 0))
            .Select(c => _db.Find(c.Name)?.Record is { } record
                ? (record.Printings.FirstOrDefault(p => p.Id == c.ImageKey) ?? record.DefaultPrinting)?.Set
                : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private IReadOnlyList<(CardDefinition Card, double Weight)> InSet(string set)
    {
        lock (_gate)
        {
            if (_bySet.TryGetValue(set, out var cached)) return cached;
            var ratings = Ratings(set);
            bool limited = set.Equals(_limitedSet, StringComparison.OrdinalIgnoreCase);
            var cards = _db.PrintingsIn(set)
                .Where(x => !x.Card.Record.IsToken && x.Card.Support == CardSupport.Full)
                .GroupBy(x => x.Card.Name)
                .Select(g =>
                {
                    var (entry, printing) = g.First();
                    double weight = (limited ? Frequency(g.Select(x => x.Printing.Rarity)) : 1) * Quality(ratings, entry.Name);
                    return (entry.Definition, weight);
                })
                .ToList();
            _bySet[set] = cards;
            return cards;
        }
    }

    private DraftRatings? Ratings(string set)
    {
        if (!_ratingsBySet.TryGetValue(set, out var r)) _ratingsBySet[set] = r = _ratings(set);
        return r;
    }

    /// <summary>
    /// How often a booster has a card of that rarity, next to a common: a pack has ten commons, three uncommons and a rare
    /// or mythic (one in eight), over sets of about 100 commons, 80 uncommons, 60 rares and 20 mythics.
    /// </summary>
    private static double Frequency(IEnumerable<string> rarities) => rarities.Max(r => r switch
    {
        "common" => 1.0,
        "uncommon" => 3 / 80.0 / (10 / 100.0),
        "rare" => 7 / 8.0 / 60 / (10 / 100.0),
        "mythic" => 1 / 8.0 / 20 / (10 / 100.0),
        _ => 0.5,
    });

    /// <summary>How much likelier the card is played than an average one: twice for each spread its win rate is above the set's.</summary>
    private static double Quality(DraftRatings? ratings, string name) =>
        ratings?.Score(name) is { } s ? Math.Pow(2, (s.Score - 5) / 1.5 * s.Confidence) : 1;

    private IReadOnlyList<(CardDefinition Card, double Weight)> Basics()
    {
        lock (_gate)
            return _basics ??= _db.Records
                .Where(r => !r.IsToken && r.TypeLine.StartsWith("Basic Land", StringComparison.Ordinal) && _db.SupportOf(r.Name) == CardSupport.Full)
                .Select(r => _db.Find(r.Name)!.Definition)
                .Where(d => (d.Supertypes & Supertype.Basic) != 0)
                .Select(d => (d, (d.Supertypes & Supertype.Snow) != 0 ? 0.1 : 1.0)).ToList();
    }
}
