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

    /// <summary>Opens a set's boosters of a kind (null: its play or draft booster; "set", "collector"...).</summary>
    public BoosterGenerator Generator(string set, string? kind = null)
    {
        var key = CareerRules.SealedKey(set, kind);
        if (_generators.TryGetValue(key, out var generator)) return generator;
        if (!Sets.TryGetValue(set, out var definition) || definition.BoosterOf(kind) is not { } spec)
            throw new CareerRuleException($"There are no {kind} boosters of '{set}'.");
        return _generators[key] = new BoosterGenerator(definition, Cards, spec);
    }

    /// <summary>The kinds of boosters the store sells of a set: null (its play or draft booster) first, then its others with a price.</summary>
    public IReadOnlyList<string?> BoosterKinds(string set) =>
        !SellsBoosters(set) ? Array.Empty<string?>()
            : new string?[] { null }.Concat(Sets[set].Boosters.Keys.Where(Content.BoosterPrices.ContainsKey)).ToList();

    public int BoosterPrice(string? kind) => string.IsNullOrEmpty(kind) ? Content.BoosterPrice : Content.BoosterPrices.GetValueOrDefault(kind);

    /// <summary>"Foundations Play Booster" or "Foundations Collector Booster" for an unopened boosters key.</summary>
    public string BoosterName(string key) =>
        CareerRules.ParseSealed(key) is var (set, kind) && Sets.TryGetValue(set, out var definition) && definition.BoosterOf(kind) is { } spec
            ? $"{definition.Name} {spec.Name}" : SetName(key);

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
        if (card.Surge) return (int)(Value(card with { Foil = false, Surge = false }) * Content.SurgeMultiplier);
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
