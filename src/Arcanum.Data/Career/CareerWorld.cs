// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Career;

/// <summary>What a career is played against: the module's career settings, its cards, sets and decks.</summary>
public sealed class CareerWorld
{
    private readonly Func<string, string?> _readDeck;
    private readonly Dictionary<string, BoosterGenerator> _generators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeckList> _decks = new(StringComparer.OrdinalIgnoreCase);

    public CareerContent Content { get; }
    public CardDatabase Cards { get; }
    public IReadOnlyDictionary<string, SetDefinition> Sets { get; }

    /// <summary>The Commander format's rules (the module's, or the usual ones).</summary>
    public Formats.FormatRules CommanderFormat { get; }

    /// <param name="readDeck">A module deck's text by name, or null when there is no such deck.</param>
    /// <param name="formats">The module's formats (for the Commander format's rules).</param>
    public CareerWorld(CareerContent content, CardDatabase cards, IEnumerable<SetDefinition> sets, Func<string, string?> readDeck,
        IEnumerable<Formats.FormatRules>? formats = null)
    {
        CommanderFormat = formats?.FirstOrDefault(f => f.Commander) ?? new Formats.FormatRules
        {
            Id = "commander", Name = "Commander", MinDeckSize = 100, MaxDeckSize = 100, MaxCopies = 1, Commander = true, StartingLife = 40,
        };
        Content = content;
        Cards = cards;
        Sets = sets.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        _readDeck = readDeck;
    }

    public BoosterGenerator Generator(string set)
    {
        if (_generators.TryGetValue(set, out var generator)) return generator;
        if (!Sets.TryGetValue(set, out var definition) || definition.Booster is null) throw new CareerRuleException($"There are no boosters of '{set}'.");
        return _generators[set] = new BoosterGenerator(definition, Cards);
    }

    public string SetName(string set) => Sets.TryGetValue(set, out var s) ? s.Name : set.ToUpperInvariant();

    public DeckList Deck(string name)
    {
        if (_decks.TryGetValue(name, out var deck)) return deck;
        var text = _readDeck(name) ?? throw new CareerRuleException($"There is no deck '{name}'.");
        return _decks[name] = DeckList.Parse(text);
    }

    /// <summary>The set featured in the week of <paramref name="day"/> (weeks start on Monday).</summary>
    public string FeaturedSet(DateOnly day) => Content.Featured[day.DayNumber / 7 % Content.Featured.Count];

    /// <summary>The store sells boosters of this set.</summary>
    public bool SellsBoosters(string set) =>
        Content.Sets.Contains(set, StringComparer.OrdinalIgnoreCase) && Sets.TryGetValue(set, out var s) && s.Booster is not null;

    public PreconSpec? Precon(string deck) => Content.Precons.FirstOrDefault(p => p.Deck.Equals(deck, StringComparison.OrdinalIgnoreCase));

    public int PreconPrice(PreconSpec precon) => precon.Price > 0 ? precon.Price : Content.PreconPrice;

    public bool IsBasicLand(string name) =>
        Cards.Find(name) is { } card && (card.Definition.Supertypes & Supertype.Basic) != 0 && card.Definition.Is(CardType.Land);

    /// <summary>A card's value in cents, from its printing's rarity (a card without a known printing counts as its rarest one); a foil is worth more.</summary>
    public int Value(CardKey card)
    {
        if (card.Foil) return (int)(Value(card with { Foil = false }) * Content.FoilMultiplier);
        if (IsBasicLand(card.Name)) return 0;
        var printings = Cards.Find(card.Name)?.Record.Printings ?? (IReadOnlyList<Printing>)Array.Empty<Printing>();
        var printing = printings.FirstOrDefault(p => p.Set.Equals(card.Set, StringComparison.OrdinalIgnoreCase) && p.CollectorNumber == card.Number);
        var rarities = printing is not null ? new[] { printing.Rarity } : printings.Select(p => p.Rarity).ToArray();
        return rarities.Length == 0 ? Content.Values.GetValueOrDefault("common") : rarities.Max(r => Content.Values.GetValueOrDefault(r.ToLowerInvariant()));
    }

    private readonly Dictionary<string, List<(PoolCard Card, int Value)>> _printings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A set's booster cards that work fully in games (one printing each, basic lands left out), with their values.</summary>
    public IReadOnlyList<(PoolCard Card, int Value)> Playable(string set)
    {
        if (_printings.TryGetValue(set, out var list)) return list;
        list = Cards.PrintingsIn(set)
            .Where(x => x.Printing.Booster && x.Card.Support == CardSupport.Full && !IsBasicLand(x.Card.Name))
            .GroupBy(x => x.Card.Name)
            .Select(g => PoolCard.Of(g.First().Card, g.First().Printing))
            .Select(c => (c, Value(CardKey.Of(c))))
            .ToList();
        return _printings[set] = list;
    }

    /// <summary>The weeks' tournament winners worked out so far (see <see cref="CareerMarket.Meta"/>).</summary>
    internal Dictionary<(ulong Seed, int Week), IReadOnlyList<MetaWin>> MetaCache { get; } = new();
}

/// <summary>An action the career's rules don't allow (not enough cash, no such booster...); the message is for the player.</summary>
public sealed class CareerRuleException(string message) : Exception(message);
