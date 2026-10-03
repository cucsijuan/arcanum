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
            if (words.Length > 0 && !words.All(w => Contains(r.Name, w) || Contains(r.TypeLine, w) || Contains(r.OracleText, w))) continue;
            yield return entry;
        }
    }

    private static bool Contains(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
