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

    /// <param name="scripts">Card scripts by oracle id (from the content module).</param>
    public CardDatabase(IEnumerable<CardRecord> records, IReadOnlyDictionary<string, Scripts.CardScript>? scripts = null)
    {
        _byName = new Dictionary<string, CardEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(r => !r.IsToken))
        {
            if (_byName.ContainsKey(record.Name)) continue;
            var (definition, support) = CardFactory.Create(record, scripts?.GetValueOrDefault(record.OracleId));
            _byName[record.Name] = new CardEntry(definition, support, record);
        }
        _sorted = _byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
    public bool TryGet(string name, string? set, string? collectorNumber, out CardDefinition definition)
    {
        if (!_byName.TryGetValue(name, out var entry)) { definition = null!; return false; }
        if (set is null || entry.Record.FindPrinting(set, collectorNumber) is not { } printing) { definition = entry.Definition; return true; }
        lock (_byPrinting)
        {
            if (!_byPrinting.TryGetValue(printing.Id, out definition!))
                _byPrinting[printing.Id] = definition = CardFactory.ForPrinting(entry.Definition, printing);
        }
        return true;
    }

    public int Count => _byName.Count;

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
            if (words.Length > 0 && !words.All(w => Contains(r.Name, w) || Contains(r.TypeLine, w) || Contains(r.OracleText, w))) continue;
            yield return entry;
        }
    }

    private static bool Contains(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
