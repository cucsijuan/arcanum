// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>A card known to the client: engine definition, support status and the imported data behind it.</summary>
/// <remarks>
/// An entry read from the card cache makes its definition the first time it is asked for (on any thread, once), so
/// startup doesn't have to make every card's.
/// </remarks>
public sealed class CardEntry
{
    private readonly Lazy<CardDefinition> _definition;

    public CardEntry(CardDefinition definition, CardSupport support, CardRecord record)
    {
        _definition = new Lazy<CardDefinition>(definition);
        Name = definition.Name;
        Support = support;
        Record = record;
    }

    /// <param name="name">The definition's name (its first face's for an adventurer card).</param>
    internal CardEntry(string name, Func<CardDefinition> definition, CardSupport support, CardRecord record)
    {
        _definition = new Lazy<CardDefinition>(definition, LazyThreadSafetyMode.ExecutionAndPublication);
        Name = name;
        Support = support;
        Record = record;
    }

    public CardDefinition Definition => _definition.Value;
    public CardSupport Support { get; }
    public CardRecord Record { get; }
    public string Name { get; }
    public int ManaValue => Definition.ManaCost.ManaValue;

    /// <summary>Whether the definition has been made already.</summary>
    internal bool IsMade => _definition.IsValueCreated;
}

/// <summary>What to look for in <see cref="CardDatabase.Search"/>. Empty fields don't filter.</summary>
public sealed record CardQuery
{
    /// <summary>A search in the card search language (<see cref="CardSearch"/>): loose words search names.</summary>
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
    private readonly Dictionary<string, List<CardRecord>> _tokensByName;

    /// <param name="scripts">Card scripts by oracle id (from the content module).</param>
    /// <param name="progress">Called now and then with the records gone through and their total.</param>
    public CardDatabase(IEnumerable<CardRecord> records, IReadOnlyDictionary<string, Scripts.CardScript>? scripts = null,
        Action<int, int>? progress = null)
    {
        _byName = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);
        _tokensByName = new Dictionary<string, List<CardRecord>>(StringComparer.OrdinalIgnoreCase);
        var all = records as IReadOnlyList<CardRecord> ?? records.ToList();
        int done = 0;
        foreach (var record in all)
        {
            if (++done % 500 == 0) progress?.Invoke(done, all.Count);
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
        progress?.Invoke(all.Count, all.Count);
        // Meld pairs (rule 712.4): a card that melds knows the combined back face, which isn't a card of its own.
        foreach (var record in all.Where(r => r.Layout == "meld" && scripts?.GetValueOrDefault(r.OracleId)?.MeldInto is not null).ToList())
        {
            var script = scripts![record.OracleId];
            if (!_byName.TryGetValue(record.Name, out var melder) || !_byName.TryGetValue(script.MeldInto!, out var result)) continue;
            var back = result.Definition with { IsMeldResult = true };
            _byName[result.Record.Name] = new CardEntry(back, result.Support, result.Record);
            _byName[record.Name] = new CardEntry(melder.Definition with { MeldResult = back }, melder.Support, melder.Record);
        }
        // A card with several faces whose name is its first face's (an adventurer card) is also found by that name.
        foreach (var entry in _byName.Values.Where(e => e.Name != e.Record.Name).ToList())
            _byName.TryAdd(entry.Name, entry);
        // A melded permanent's combined face is found by name (to show it) but isn't a card to list, search, collect or build with.
        _sorted = _byName.Values.Distinct().Where(e => !e.Definition.IsMeldResult).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Sets = _sorted.SelectMany(e => e.Record.Printings)
            .GroupBy(p => p.Set, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SetInfo(g.Key, g.First().SetName, g.Min(p => p.Released) ?? "", g.First().SetType, g.Count()))
            .OrderByDescending(s => s.Released, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A database put back together from <see cref="CardCache"/>, whose entries make their definitions when first asked.</summary>
    internal CardDatabase(Dictionary<string, CardEntry> byName, List<CardEntry> sorted, Dictionary<string, List<CardRecord>> tokensByName,
        IReadOnlyList<SetInfo> sets, IReadOnlyList<string> nonbasicLandNames, IReadOnlyList<string> creatureCardNames)
    {
        _byName = byName;
        _sorted = sorted;
        _tokensByName = tokensByName;
        Sets = sets;
        _nonbasicLandNames = nonbasicLandNames;
        _creatureCardNames = creatureCardNames;
    }

    // What CardCache writes: the lookups exactly as built, so reading them back needs no definition.
    internal IReadOnlyDictionary<string, CardEntry> ByName => _byName;
    internal IReadOnlyList<CardEntry> Sorted => _sorted;
    internal IReadOnlyDictionary<string, List<CardRecord>> TokensByName => _tokensByName;

    /// <summary>
    /// Makes every card's definition ahead of need, in the background, so a later search over all cards doesn't wait
    /// for them. Entries made already are skipped; the work stops early when <paramref name="cancel"/> fires.
    /// </summary>
    /// <remarks>
    /// It uses at most half the processors, so the game around it stays responsive: on a device with few of them
    /// (a handheld console), one thread of the lowest priority does it.
    /// </remarks>
    public Task MakeDefinitionsAsync(CancellationToken cancel = default)
    {
        var entries = _byName.Values.Distinct().Where(e => !e.IsMade).ToList();
        int workers = Environment.ProcessorCount / 2;
        if (workers > 1)
            return Task.Run(() =>
            {
                var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = workers };
                try { Parallel.ForEach(entries, options, entry => _ = entry.Definition); }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var entry in entries)
                {
                    if (cancel.IsCancellationRequested) break;
                    _ = entry.Definition;
                }
                done.SetResult();
            }
            catch (Exception e) { done.SetException(e); } // as the pool's version: the task faults, the game goes on
        }) { IsBackground = true, Name = "Card definitions" };
        try { thread.Priority = ThreadPriority.Lowest; }
        catch (Exception e) when (e is PlatformNotSupportedException or ThreadStateException) { }
        thread.Start();
        return done.Task;
    }

    /// <summary>Every set any known card was printed in, newest first.</summary>
    public IReadOnlyList<SetInfo> Sets { get; }

    public SetInfo? FindSet(string code) => Sets.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Each printing in a set with its card, in collector number order.</summary>
    public IReadOnlyList<(CardEntry Card, Printing Printing)> PrintingsIn(string set)
    {
        // Every card's printings are gone through once, the first time any set is asked for: looking through every card
        // for each set takes seconds on a slow device.
        var bySet = _printingsBySet;
        if (bySet is null)
        {
            bySet = _sorted.SelectMany(e => e.Record.Printings.Select(p => (Card: e, Printing: p)))
                .GroupBy(x => x.Printing.Set, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<(CardEntry, Printing)>)g
                    .OrderBy(x => x.Printing.CollectorNumber, Comparer<string>.Create(OracleJsonl.CompareNumbers)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            _printingsBySet = bySet;
        }
        return bySet.TryGetValue(set, out var printings) ? printings : Array.Empty<(CardEntry, Printing)>();
    }

    private Dictionary<string, IReadOnlyList<(CardEntry Card, Printing Printing)>>? _printingsBySet;

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
        foil |= printing?.FoilOnly == true; // a printing made only in foil is always a foil
        if (printing is null && !foil) { definition = entry.Definition; return true; }
        string key = (printing?.Id ?? "default:" + entry.Record.OracleId) + (foil ? "|foil" : "");
        lock (_byPrinting)
        {
            if (!_byPrinting.TryGetValue(key, out definition!))
            {
                definition = printing is null ? entry.Definition : CardFactory.ForPrinting(entry.Definition, printing, Picture(entry.Record, printing));
                if (foil) definition = definition with { Foil = true, Fracture = printing?.Fracture == true };
                _byPrinting[key] = definition;
            }
        }
        return true;
    }

    /// <summary>
    /// The printing whose picture shows a printing: for one made only in foil, a printing of the same artwork that isn't
    /// (the same set's first), so the foil is drawn by the game on the plain picture; otherwise the printing itself.
    /// </summary>
    private static Printing Picture(CardRecord record, Printing printing)
    {
        if (!printing.FoilOnly || printing.Illustration is null) return printing;
        return record.Printings.Where(p => !p.FoilOnly && p.Illustration == printing.Illustration)
            .OrderBy(p => p.Set == printing.Set ? 0 : 1).FirstOrDefault() ?? printing;
    }

    /// <summary>Whether this printing's foils are fracture foils.</summary>
    public bool IsFracture(string name, string? set, string? collectorNumber) =>
        set is not null && _byName.TryGetValue(name, out var entry) && entry.Record.FindPrinting(set, collectorNumber)?.Fracture == true;

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

    /// <summary>Every card's record, in the order of <see cref="All"/>, without making its definition.</summary>
    public IEnumerable<CardRecord> Records => _sorted.Select(e => e.Record);

    /// <summary>The module's curated search tags (null: none, so the values they decide aren't known).</summary>
    public SearchTags? SearchTags
    {
        get => _searchTags;
        set { _searchTags = value; _vocabulary = null; }
    }

    private SearchTags? _searchTags;
    private SearchVocabulary? _vocabulary;

    /// <summary>The tags and the promo kinds the printings have, for <c>is:</c> values.</summary>
    private SearchVocabulary Vocabulary => _vocabulary ??= new SearchVocabulary(_searchTags,
        _sorted.SelectMany(e => e.Record.Printings).SelectMany(p => p.PromoTypes).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Reads a search in the card search language, with this database's sets for release dates.</summary>
    public CardFilter Filter(string text, SearchTerms? extra = null) => CardSearch.Parse(text, FindSet, extra, Vocabulary);

    public IEnumerable<CardEntry> Search(CardQuery query)
    {
        var filter = Filter(query.Text);
        return filter.Sort(Find(query, filter));
    }

    private IEnumerable<CardEntry> Find(CardQuery query, CardFilter filter)
    {
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
            if (!filter.Matches(entry)) continue;
            yield return entry;
        }
    }


    // ---------------------------------------------------------------- token pictures

    /// <summary>
    /// Every picture a token can be shown with: the printings of the tokens of its name with its power, toughness,
    /// colors and keyword abilities (or, when none matches, of the token printing it already shows), oldest first.
    /// </summary>
    public IReadOnlyList<TokenArt> TokenArts(CardDefinition token)
    {
        if (!_tokensByName.TryGetValue(token.Name, out var named)) return Array.Empty<TokenArt>();
        var colors = token.ColorList.OrderBy(c => c).ToList();
        bool Same(CardRecord r) => r.Power == token.Power?.ToString() && r.Toughness == token.Toughness?.ToString()
                                   && r.Colors.OrderBy(c => c).SequenceEqual(colors)
                                   && Engine.Cards.Keywords.ParseAll(r.Keywords).SetEquals(token.KeywordAbilities);
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
                Engine.Abilities.Incubate incubate => incubate.Token,
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
