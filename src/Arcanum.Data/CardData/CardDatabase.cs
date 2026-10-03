// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>All cards known to the client, looked up by name. Built from a module's imported card data.</summary>
public sealed class CardDatabase : ICardDatabase
{
    private readonly Dictionary<string, (CardDefinition Definition, CardSupport Support)> _byName;

    /// <param name="scripts">Card scripts by oracle id (from the content module).</param>
    public CardDatabase(IEnumerable<CardRecord> records, IReadOnlyDictionary<string, Scripts.CardScript>? scripts = null)
    {
        _byName = new Dictionary<string, (CardDefinition, CardSupport)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(r => !r.IsToken))
            _byName.TryAdd(record.Name, CardFactory.Create(record, scripts?.GetValueOrDefault(record.OracleId)));
    }

    public int Count => _byName.Count;

    public bool TryGet(string name, out CardDefinition definition)
    {
        if (_byName.TryGetValue(name, out var entry)) { definition = entry.Definition; return true; }
        definition = null!;
        return false;
    }

    public CardSupport SupportOf(string name) => _byName.TryGetValue(name, out var e) ? e.Support : CardSupport.Unsupported;

    public IEnumerable<CardDefinition> All => _byName.Values.Select(v => v.Definition);
}
