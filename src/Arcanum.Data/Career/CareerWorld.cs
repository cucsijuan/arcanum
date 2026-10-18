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

    /// <summary>The season's changes to the calendar and prices (see <see cref="CareerSeason"/>).</summary>
    public CareerSeason Season { get; }
    public CardDatabase Cards { get; }
    public IReadOnlyDictionary<string, SetDefinition> Sets { get; }

    /// <summary>The Commander format's rules (the module's, or the usual ones).</summary>
    public Arcanum.Data.Formats.FormatRules CommanderFormat { get; }

    /// <summary>The module's formats by id.</summary>
    public IReadOnlyDictionary<string, Arcanum.Data.Formats.FormatRules> Formats { get; }

    /// <summary>
    /// The format an event's decks must be legal in: Commander for Commander events, the event's format for constructed ones (casual when it
    /// names none). Draft events have no deck rules here (the pool decides).
    /// </summary>
    public Arcanum.Data.Formats.FormatRules FormatOf(EventSpec e) =>
        e.Kind == CareerRules.Commander ? CommanderFormat
        : string.IsNullOrEmpty(e.Format) ? Arcanum.Data.Formats.FormatRules.Casual
        : Formats.TryGetValue(e.Format, out var format) ? format
        : throw new CareerRuleException($"{e.Name} is played in a format this game doesn't know ('{e.Format}').");

    /// <param name="readDeck">A module deck's text by name, or null when there is no such deck.</param>
    /// <param name="formats">The module's formats (for the Commander format's rules).</param>
    /// <param name="season">The season's changes (none: the content's calendar and prices as they are).</param>
    public CareerWorld(CareerContent content, CardDatabase cards, IEnumerable<SetDefinition> sets, Func<string, string?> readDeck,
        IEnumerable<Arcanum.Data.Formats.FormatRules>? formats = null, CareerSeason? season = null)
    {
        Season = season ?? CareerSeason.None;
        CommanderFormat = formats?.FirstOrDefault(f => f.Commander) ?? new Arcanum.Data.Formats.FormatRules
        {
            Id = "commander", Name = "Commander", MinDeckSize = 100, MaxDeckSize = 100, MaxCopies = 1, Commander = true, StartingLife = 40,
        };
        Formats = (formats ?? Array.Empty<Arcanum.Data.Formats.FormatRules>()).GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>A booster kind's price on a day (the season may change it).</summary>
    public int BoosterPrice(string? kind, DateOnly day) => Season.BoosterPrice(Content, kind, day);

    /// <summary>The store's calendar on a day: the content's events with the season's changes in force.</summary>
    public IReadOnlyList<EventSpec> Events(DateOnly day) => Season.Events(Content, day);

    /// <summary>An event of the calendar on a day, by id.</summary>
    public EventSpec? Event(string id, DateOnly day) => Events(day).FirstOrDefault(e => e.Id == id);

    /// <summary>The season's news on a day.</summary>
    public IEnumerable<string> News(DateOnly day) => Season.On(day).Select(c => c.News).OfType<string>();

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

    /// <summary>The set featured in the week of <paramref name="day"/> (weeks start on Monday), unless the season features another.</summary>
    public string FeaturedSet(DateOnly day) => Season.FeaturedSet(Content, day);

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
        if (card.Foil && Cards.IsFracture(card.Name, card.Set, card.Number)) return (int)(Value(card with { Foil = false }) * Content.FractureMultiplier);
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

    // ---------------------------------------------------------------- printings' art

    private readonly Dictionary<string, Dictionary<string, string>> _baseNumbers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _inPlayBoosters;
    private readonly Dictionary<string, List<SingleCandidate>> _singles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A printing's art: a card's first printing in a set is its usual one; its others there are special arts (borderless,
    /// showcase, extended art…), found in play boosters or only in collector boosters (and other products).
    /// </summary>
    public CardArt ArtOf(CardKey card)
    {
        if (card.Set.Length == 0 || card.Number.Length == 0) return CardArt.Usual;
        if (!_baseNumbers.TryGetValue(card.Set, out var bases))
            _baseNumbers[card.Set] = bases = Cards.PrintingsIn(card.Set).GroupBy(x => x.Card.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Printing.CollectorNumber, StringComparer.OrdinalIgnoreCase);
        if (!bases.TryGetValue(card.Name, out var number) || number == card.Number) return CardArt.Usual;
        return InPlayBoosters.Contains($"{card.Set.ToLowerInvariant()}|{card.Number}") ? CardArt.SpecialPlay : CardArt.SpecialCollector;
    }

    /// <summary>Every printing a set's play boosters can hold ("set|number").</summary>
    private HashSet<string> InPlayBoosters => _inPlayBoosters ??= Sets.Values.Where(s => s.Booster is not null)
        .SelectMany(s => s.Booster!.Sheets.Keys.SelectMany(sheet => Generator(s.Code).Sheet(sheet)))
        .Select(c => $"{c.Set.ToLowerInvariant()}|{c.Number}").ToHashSet();

    /// <summary>The cards of a set's boosters (every kind) a single can be, with their art; basic lands and cards that don't work fully left out.</summary>
    public IReadOnlyList<SingleCandidate> Singles(string set)
    {
        if (_singles.TryGetValue(set, out var list)) return list;
        list = new List<SingleCandidate>();
        if (Sets.TryGetValue(set, out var definition))
        {
            var seen = new HashSet<string>();
            foreach (var kind in new string?[] { null }.Concat(definition.Boosters.Keys))
            {
                if (definition.BoosterOf(kind) is not { } spec) continue;
                foreach (var sheet in spec.Sheets.Keys)
                    foreach (var card in Generator(set, kind).Sheet(sheet))
                        if (seen.Add($"{card.Set.ToLowerInvariant()}|{card.Number}") && !IsBasicLand(card.Name) && Cards.SupportOf(card.Name) == CardSupport.Full)
                            list.Add(new SingleCandidate(card with { Foil = false, Surge = false }, ArtOf(CardKey.Of(card with { Foil = false, Surge = false }))));
            }
        }
        return _singles[set] = list;
    }

    /// <summary>The weeks' tournament winners worked out so far (see <see cref="CareerMarket.Meta"/>).</summary>
    internal Dictionary<(ulong Seed, int Week), IReadOnlyList<MetaWin>> MetaCache { get; } = new();
}

/// <summary>A printing's art: the card's usual one, or a special art found in play boosters or only in collector boosters.</summary>
public enum CardArt { Usual, SpecialPlay, SpecialCollector }

/// <summary>A card a single can be (a printing, not foil yet) and its art.</summary>
public sealed record SingleCandidate(PoolCard Card, CardArt Art);

/// <summary>An action the career's rules don't allow (not enough cash, no such booster...); the message is for the player.</summary>
public sealed class CareerRuleException(string message) : Exception(message);
