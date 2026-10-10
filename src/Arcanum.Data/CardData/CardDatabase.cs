// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>A card known to the client: engine definition, support status and the imported data behind it.</summary>
public sealed record CardEntry(CardDefinition Definition, CardSupport Support, CardRecord Record)
{
    public string Name => Definition.Name;
    public int ManaValue => Definition.ManaCost.ManaValue;
}

/// <summary>What to look for in <see cref="CardDatabase.Search"/>. Empty fields don't filter.</summary>
public sealed record CardQuery
{
    /// <summary>Words that must all appear in the name, type line or rules text.</summary>
    public string Text { get; init; } = "";

    /// <summary>Color letters (W, U, B, R, G); a card matches if it has any of them.</summary>
    public IReadOnlySet<string> Colors { get; init; } = new HashSet<string>();

    /// <summary>Also match colorless cards.</summary>
    public bool Colorless { get; init; }

    /// <summary>Card types; a card matches if it has any of them.</summary>
    public CardType Types { get; init; }

    public int? ManaValueMin { get; init; }
    public int? ManaValueMax { get; init; }
    public bool SupportedOnly { get; init; }

    /// <summary>Legality key of a format; only cards legal (or restricted) there match.</summary>
    public string? LegalIn { get; init; }

    /// <summary>Set code; only cards printed in that set match.</summary>
    public string? Set { get; init; }
}

/// <summary>All cards known to the client, looked up by name. Built from a module's imported card data.</summary>
public sealed class CardDatabase : ICardDatabase
{
    private readonly Dictionary<string, CardEntry> _byName;
    private readonly List<CardEntry> _sorted;
    private readonly Dictionary<string, List<CardRecord>> _tokensByName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="scripts">Card scripts by oracle id (from the content module).</param>
    public CardDatabase(IEnumerable<CardRecord> records, IReadOnlyDictionary<string, Scripts.CardScript>? scripts = null)
    {
        _byName = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record.IsToken)
            {
                if (!_tokensByName.TryGetValue(record.Name, out var tokens)) _tokensByName[record.Name] = tokens = new List<CardRecord>();
                tokens.Add(record);
                continue;
            }
            if (_byName.ContainsKey(record.Name)) continue;
            var (definition, support) = CardFactory.Create(record, scripts?.GetValueOrDefault(record.OracleId));
            _byName[record.Name] = new CardEntry(definition, support, record);
        }
        // A card with several faces whose name is its first face's (an adventurer card) is also found by that name.
        foreach (var entry in _byName.Values.Where(e => e.Name != e.Record.Name).ToList())
            _byName.TryAdd(entry.Name, entry);
        _sorted = _byName.Values.Distinct().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Sets = _sorted.SelectMany(e => e.Record.Printings)
            .GroupBy(p => p.Set, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SetInfo(g.Key, g.First().SetName, g.Min(p => p.Released) ?? "", g.First().SetType, g.Count()))
            .OrderByDescending(s => s.Released, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every set any known card was printed in, newest first.</summary>
    public IReadOnlyList<SetInfo> Sets { get; }

    public SetInfo? FindSet(string code) => Sets.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Each printing in a set with its card, in collector number order.</summary>
    public IEnumerable<(CardEntry Card, Printing Printing)> PrintingsIn(string set) =>
        _sorted.SelectMany(e => e.Record.Printings.Where(p => p.Set.Equals(set, StringComparison.OrdinalIgnoreCase)).Select(p => (e, p)))
            .OrderBy(x => x.p.CollectorNumber, Comparer<string>.Create(OracleJsonl.CompareNumbers));

    private readonly Dictionary<string, CardDefinition> _byPrinting = new();

    /// <summary>
    /// The card's definition as printed in a set (its picture and tokens). Without a set, or when the card was never
    /// printed there, the card's default definition.
    /// </summary>
    /// <param name="foil">A foil copy: granted when the printing (or, without one, the default printing) exists in foil.</param>
    public bool TryGet(string name, string? set, string? collectorNumber, out CardDefinition definition) =>
        TryGet(name, set, collectorNumber, foil: false, out definition);

    /// <inheritdoc cref="TryGet(string, string?, string?, out CardDefinition)"/>
    public bool TryGet(string name, string? set, string? collectorNumber, bool foil, out CardDefinition definition)
    {
        if (!_byName.TryGetValue(name, out var entry)) { definition = null!; return false; }
        var printing = set is null ? null : entry.Record.FindPrinting(set, collectorNumber);
        foil &= (printing ?? entry.Record.DefaultPrinting)?.Foil == true;
        if (printing is null && !foil) { definition = entry.Definition; return true; }
        string key = (printing?.Id ?? "default:" + entry.Record.OracleId) + (foil ? "|foil" : "");
        lock (_byPrinting)
        {
            if (!_byPrinting.TryGetValue(key, out definition!))
            {
                definition = printing is null ? entry.Definition : CardFactory.ForPrinting(entry.Definition, printing);
                if (foil) definition = definition with { Foil = true };
                _byPrinting[key] = definition;
            }
        }
        return true;
    }

    /// <summary>Whether the card can be a foil copy in this printing (or, without a set, in its default printing).</summary>
    public bool CanBeFoil(string name, string? set, string? collectorNumber) =>
        _byName.TryGetValue(name, out var entry)
        && ((set is null ? null : entry.Record.FindPrinting(set, collectorNumber)) ?? entry.Record.DefaultPrinting)?.Foil == true;

    public int Count => _sorted.Count;

    private IReadOnlyList<string>? _names;

    /// <summary>Every card name, in alphabetical order (what a player may choose for "choose a card name").</summary>
    /// <remarks>A transforming double-faced card offers either face's name, not both together (rule 712.19).</remarks>
    public IReadOnlyList<string> Names => _names ??= _sorted
        .SelectMany(e => e.Record.Layout == "transform" && e.Record.Faces.Count == 2 ? e.Record.Faces.Select(f => f.Name) : new[] { e.Record.Name })
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private IReadOnlyList<string>? _nonbasicLandNames;

    /// <summary>The names of nonbasic land cards (faces), for "choose a nonbasic land card name".</summary>
    public IReadOnlyList<string> NonbasicLandNames => _nonbasicLandNames ??= _sorted
        .SelectMany(e => e.Definition.BackFace is { } back ? new[] { e.Definition, back } : new[] { e.Definition })
        .Where(f => f.Is(CardType.Land) && (f.Supertypes & Supertype.Basic) == 0)
        .Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private IReadOnlyList<string>? _creatureCardNames;

    /// <summary>The names of creature cards (faces), for "choose a creature card name".</summary>
    public IReadOnlyList<string> CreatureCardNames => _creatureCardNames ??= _sorted
        .SelectMany(e => e.Definition.BackFace is { } back ? new[] { e.Definition, back } : new[] { e.Definition })
        .Where(f => f.Is(CardType.Creature))
        .Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string name, out CardDefinition definition)
    {
        if (_byName.TryGetValue(name, out var entry)) { definition = entry.Definition; return true; }
        definition = null!;
        return false;
    }

    public CardEntry? Find(string name) => _byName.GetValueOrDefault(name);

    public CardSupport SupportOf(string name) => _byName.TryGetValue(name, out var e) ? e.Support : CardSupport.Unsupported;

    public IEnumerable<CardDefinition> All => _sorted.Select(e => e.Definition);

    public IEnumerable<CardEntry> Search(CardQuery query)
    {
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in _sorted)
        {
            var r = entry.Record;
            if (query.SupportedOnly && entry.Support != CardSupport.Full) continue;
            if (query.Types != CardType.None && (entry.Definition.Types & query.Types) == 0) continue;
            if (query.ManaValueMin is { } min && entry.ManaValue < min) continue;
            if (query.ManaValueMax is { } max && entry.ManaValue > max) continue;
            if (query.Colors.Count > 0 || query.Colorless)
            {
                bool colorMatch = r.Colors.Any(query.Colors.Contains) || (query.Colorless && r.Colors.Count == 0);
                if (!colorMatch) continue;
            }
            if (query.LegalIn is { } format && !(r.Legalities.TryGetValue(format, out var status) && status is "legal" or "restricted")) continue;
            if (query.Set is { } set && r.FindPrinting(set) is null) continue;
            if (words.Length > 0 && !words.All(w => Contains(r.Name, w) || Contains(r.TypeLine, w) || Contains(r.OracleText, w)
                                                     || r.Faces.Any(f => Contains(f.TypeLine, w) || Contains(f.OracleText, w)))) continue;
            yield return entry;
        }
    }

    private static bool Contains(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- token pictures

    /// <summary>
    /// Every picture a token can be shown with: the printings of the tokens of its name with its power, toughness and
    /// colors (or, when none matches, of the token printing it already shows), oldest first.
    /// </summary>
    public IReadOnlyList<TokenArt> TokenArts(CardDefinition token)
    {
        if (!_tokensByName.TryGetValue(token.Name, out var named)) return Array.Empty<TokenArt>();
        var colors = token.ColorList.OrderBy(c => c).ToList();
        bool Same(CardRecord r) => r.Power == token.Power?.ToString() && r.Toughness == token.Toughness?.ToString()
                                   && r.Colors.OrderBy(c => c).SequenceEqual(colors);
        var matching = named.Where(Same).ToList();
        if (matching.Count == 0) matching = named.Where(r => r.DefaultPrintingId == token.ImageKey || r.Printings.Any(p => p.Id == token.ImageKey)).ToList();
        return matching
            .SelectMany(r => r.Printings.Count > 0
                ? r.Printings.Select(p => new TokenArt(p.Id, p.SetName, p.Released))
                : r.DefaultPrintingId is { } id ? new[] { new TokenArt(id, "", "") } : Array.Empty<TokenArt>())
            .DistinctBy(a => a.Id)
            .OrderBy(a => a.Released, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The tokens <paramref name="card"/> creates (any face, any ability), one per kind.</summary>
    public static IReadOnlyList<CardDefinition> TokensMadeBy(CardDefinition card)
    {
        var abilities = new List<Engine.Abilities.AbilityDefinition>();
        foreach (var face in new[] { card, card.Adventure, card.BackFace })
        {
            if (face is null) continue;
            if (face.Spell is { } spell) abilities.Add(spell);
            abilities.AddRange(face.Abilities);
        }
        return abilities.SelectMany(Engine.Abilities.EffectTree.All)
            .Select(e => e switch
            {
                Engine.Abilities.CreateTokens create => create.Token,
                Engine.Abilities.Amass amass => amass.Token,
                Engine.Abilities.Recruit recruit => recruit.Token,
                _ => null,
            })
            .OfType<CardDefinition>()
            .DistinctBy(TokenKind)
            .ToList();
    }

    /// <summary>What tells tokens apart for their pictures: "Name|power/toughness|colors" ("Human Soldier|1/1|W").</summary>
    public static string TokenKind(CardDefinition token) =>
        $"{token.Name}|{(token.Power is { } p ? $"{p}/{token.Toughness}" : "")}|{string.Concat(token.ColorList.OrderBy(c => c))}";

    /// <summary>The card with each token it creates shown by the picture <paramref name="pick"/> gives for its kind (null keeps the picture).</summary>
    public static CardDefinition WithTokenArt(CardDefinition card, Func<CardDefinition, string?> pick)
    {
        CardDefinition Pictured(CardDefinition token) => pick(token) is { } id ? token with { ImageKey = id } : token;
        Engine.Abilities.Effect Map(Engine.Abilities.Effect effect) => effect switch
        {
            Engine.Abilities.CreateTokens create => create with { Token = Pictured(create.Token) },
            Engine.Abilities.Amass amass => amass with { Token = Pictured(amass.Token) },
            Engine.Abilities.Recruit recruit => recruit with { Token = Pictured(recruit.Token) },
            _ => effect,
        };
        T? Ability<T>(T? ability) where T : Engine.Abilities.AbilityDefinition =>
            ability is null ? null : (T)Engine.Abilities.EffectTree.Map(ability, Map);
        return card with
        {
            Spell = Ability(card.Spell),
            Abilities = card.Abilities.Select(a => Ability(a)!).ToList(),
            Adventure = card.Adventure is { } adventure ? WithTokenArt(adventure, pick) : null,
            BackFace = card.BackFace is { } back ? WithTokenArt(back, pick) : null,
        };
    }
}

/// <summary>One picture of a token: the printing's id (its image key), its set and when it came out.</summary>
public sealed record TokenArt(string Id, string SetName, string Released);
