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

    /// <param name="readDeck">A module deck's text by name, or null when there is no such deck.</param>
    public CareerWorld(CareerContent content, CardDatabase cards, IEnumerable<SetDefinition> sets, Func<string, string?> readDeck)
    {
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

    /// <summary>A card's value in cents, from its printing's rarity (a card without a known printing counts as its rarest one).</summary>
    public int Value(CardKey card)
    {
        if (IsBasicLand(card.Name)) return 0;
        var printings = Cards.Find(card.Name)?.Record.Printings ?? (IReadOnlyList<Printing>)Array.Empty<Printing>();
        var printing = printings.FirstOrDefault(p => p.Set.Equals(card.Set, StringComparison.OrdinalIgnoreCase) && p.CollectorNumber == card.Number);
        var rarities = printing is not null ? new[] { printing.Rarity } : printings.Select(p => p.Rarity).ToArray();
        return rarities.Length == 0 ? Content.Values.GetValueOrDefault("common") : rarities.Max(r => Content.Values.GetValueOrDefault(r.ToLowerInvariant()));
    }

    /// <summary>What the store pays for one copy.</summary>
    public int SellPrice(CardKey card) => (int)Math.Floor(Value(card) * Content.SellRate);
}

/// <summary>An action the career's rules don't allow (not enough cash, no such booster...); the message is for the player.</summary>
public sealed class CareerRuleException(string message) : Exception(message);
