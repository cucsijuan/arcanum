// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Decks;

/// <summary>
/// A deck found by a deck search: its id at the source, name, number of cards (with the sideboard), views and colors
/// (the letters W, U, B, R, G its cards use; null when the source doesn't say).
/// </summary>
public sealed record FoundDeck(long Id, string Name, int Size, long Views, string? Colors = null);

/// <summary>A community deck like the player's, with its list and how many of the player's cards it plays.</summary>
public sealed record SimilarDeck(FoundDeck Found, DeckList List, int Shared);

/// <summary>
/// Suggestions for a deck of a 60-card format from community decks like it (the module's deck search source,
/// <c>sources.json</c> "deckSearch"): decks playing the deck's main cards are found and read, and their cards are
/// suggested by how many of them play each one. The result has the same shape as a commander's statistics, so the
/// deck builder shows both the same way; each similar deck is offered as a "theme" whose id is the deck's id.
/// </summary>
public static class SimilarDecks
{
    /// <summary>Reads a deck search page. Anything missing is left out.</summary>
    public static List<FoundDeck> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var found = new List<FoundDeck>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return found;
        foreach (var r in results.EnumerateArray())
            if (r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            {
                string? colors = null;
                if (r.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object)
                    colors = string.Concat(c.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() > 0).Select(p => p.Name));
                found.Add(new FoundDeck(id.GetInt64(), Str(r, "name"), (int)Num(r, "size"), (long)Num(r, "viewCount"), colors));
            }
        return found;
    }

    /// <summary>
    /// Reads a deck's page into a deck list: cards of the "Sideboard" category go to the sideboard, cards of a category
    /// left out of the deck (a maybeboard) are dropped. Null when the page has no cards.
    /// </summary>
    public static DeckList? ParseDeck(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var outside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
            foreach (var c in categories.EnumerateArray())
                if (c.TryGetProperty("includedInDeck", out var included) && included.ValueKind == JsonValueKind.False)
                    outside.Add(Str(c, "name"));
        if (!root.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array) return null;
        var list = new DeckList();
        foreach (var line in cards.EnumerateArray())
        {
            int count = (int)Num(line, "quantity");
            if (count <= 0 || !line.TryGetProperty("card", out var card) || !card.TryGetProperty("oracleCard", out var oracle)) continue;
            var name = Str(oracle, "name");
            if (name.Length == 0) continue;
            var category = line.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array && cats.GetArrayLength() > 0
                ? cats[0].GetString() ?? "" : "";
            if (outside.Contains(category)) continue;
            DeckList.Adjust(category.Equals("Sideboard", StringComparison.OrdinalIgnoreCase) ? list.Sideboard : list.Main, name, count);
        }
        return list.Main.Count == 0 ? null : list;
    }

    /// <summary>
    /// The deck's cards to search for: its main deck's cards other than basic lands, the most telling first
    /// (<paramref name="rank"/>, higher first: say the card's rarity, as rarer cards say more about a deck and fewer decks
    /// play them), then the most copies.
    /// </summary>
    public static List<string> KeyCards(DeckList deck, Func<string, bool> isBasicLand, int count, Func<string, int>? rank = null) =>
        deck.Main.Where(e => !isBasicLand(e.Name))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Name, Count: g.Sum(e => e.Count)))
            .OrderByDescending(c => rank?.Invoke(c.Name) ?? 0).ThenByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(count).Select(c => c.Name).ToList();

    /// <summary>Whether a found deck's colors fit in <paramref name="colors"/> (any deck fits a colorless one, or when the source gives no colors).</summary>
    public static bool FitsColors(FoundDeck deck, string colors) =>
        colors.Length == 0 || deck.Colors is null || deck.Colors.All(c => colors.Contains(c));

    /// <summary>Number of different cards (basic lands aside) of <paramref name="mine"/> that <paramref name="other"/>'s main deck plays.</summary>
    public static int SharedCards(DeckList mine, DeckList other, Func<string, bool> isBasicLand)
    {
        var names = Names(mine, isBasicLand);
        return Names(other, isBasicLand).Count(names.Contains);
    }

    /// <summary>
    /// Suggestions from the similar decks (the most similar first): every card their main decks play, basic lands aside,
    /// with how many of them play it; its "synergy" is how much more often the decks most like the player's play it.
    /// Groups: cards not in the deck yet, then by card type (<paramref name="typeOf"/> gives a card's type key, as in
    /// <see cref="CommanderInsights.TypeKeys"/>). The themes are the decks <paramref name="themes"/> (by default the same
    /// decks), and the average number of cards of each type is the decks' average.
    /// </summary>
    public static CommanderInsights Suggestions(DeckList mine, IReadOnlyList<SimilarDeck> decks, Func<string, bool> isBasicLand, Func<string, string?> typeOf,
        IReadOnlyList<SimilarDeck>? themes = null)
    {
        var inDeck = Names(mine, isBasicLand);
        // A deck's weight is the share of the player's cards it plays (every deck counts the same for an empty deck).
        double Weight(SimilarDeck d) => inDeck.Count == 0 ? 1 : (double)d.Shared / inDeck.Count;
        double totalWeight = decks.Sum(Weight);
        var cards = new Dictionary<string, (int Decks, double Weight)>(StringComparer.OrdinalIgnoreCase);
        foreach (var deck in decks)
            foreach (var name in Names(deck.List, isBasicLand))
            {
                var (n, w) = cards.GetValueOrDefault(name);
                cards[name] = (n + 1, w + Weight(deck));
            }
        var all = cards.Select(kv =>
        {
            double inclusion = (double)kv.Value.Decks / decks.Count;
            double weighted = totalWeight > 0 ? kv.Value.Weight / totalWeight : inclusion;
            return new InsightCard(kv.Key, weighted - inclusion, kv.Value.Decks, decks.Count);
        }).OrderByDescending(c => c.Decks).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();

        var sections = new List<InsightSection>();
        var missing = all.Where(c => !inDeck.Contains(c.Name)).ToList();
        if (missing.Count > 0 && inDeck.Count > 0) sections.Add(new InsightSection("new", "Not in your deck", missing));
        foreach (var type in CommanderInsights.TypeKeys)
        {
            var ofType = all.Where(c => typeOf(c.Name) == type).ToList();
            if (ofType.Count > 0) sections.Add(new InsightSection(type, TypeTitles.GetValueOrDefault(type, type), ofType));
        }
        var types = new Dictionary<string, int>();
        if (decks.Count > 0)
            foreach (var type in CommanderInsights.TypeKeys)
            {
                double sum = decks.Sum(d => d.List.Main.Where(e => (isBasicLand(e.Name) ? "land" : typeOf(e.Name)) == type).Sum(e => e.Count));
                if (sum > 0) types[type] = (int)Math.Round(sum / decks.Count);
            }
        return new CommanderInsights
        {
            Decks = decks.Count,
            Sections = sections,
            Themes = (themes ?? decks).Select(d => new InsightTheme(d.Found.Id.ToString(), d.Found.Name, d.Shared)).ToList(),
            AverageTypes = types,
        };
    }

    /// <summary>
    /// An average of the decks' main decks, of <paramref name="size"/> cards: the cards with the most copies on average
    /// first, each with its average number of copies (at least one), basic lands included.
    /// </summary>
    public static DeckList Average(IReadOnlyList<SimilarDeck> decks, int size)
    {
        var list = new DeckList();
        if (decks.Count == 0) return list;
        var copies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var deck in decks)
            foreach (var entry in deck.List.Main)
                copies[entry.Name] = copies.GetValueOrDefault(entry.Name) + entry.Count;
        int total = 0;
        foreach (var (name, sum) in copies.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (total >= size) break;
            int count = Math.Min(Math.Max(1, (int)Math.Round((double)sum / decks.Count)), size - total);
            DeckList.Adjust(list.Main, name, count);
            total += count;
        }
        return list;
    }

    private static readonly Dictionary<string, string> TypeTitles = new()
    {
        ["creature"] = "Creatures", ["planeswalker"] = "Planeswalkers", ["instant"] = "Instants", ["sorcery"] = "Sorceries",
        ["artifact"] = "Artifacts", ["enchantment"] = "Enchantments", ["battle"] = "Battles", ["land"] = "Lands",
    };

    private static HashSet<string> Names(DeckList deck, Func<string, bool> isBasicLand) =>
        deck.Main.Where(e => !isBasicLand(e.Name)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
