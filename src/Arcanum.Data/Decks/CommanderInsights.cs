// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Arcanum.Data.Decks;

/// <summary>
/// A card suggested for a commander, with how the community plays it: in <see cref="Decks"/> of the
/// <see cref="PotentialDecks"/> decks with this commander that could play it (its colors fit).
/// </summary>
/// <param name="Synergy">How much more often it is played with this commander than in other decks of its colors (0.12 = +12%).</param>
public sealed record InsightCard(string Name, double Synergy, int Decks, int PotentialDecks)
{
    /// <summary>Share of the decks that could play it that do (0 to 1).</summary>
    public double Inclusion => PotentialDecks > 0 ? (double)Decks / PotentialDecks : 0;
}

/// <summary>A group of suggestions (top cards, high synergy, creatures, lands…), in the source's order.</summary>
public sealed record InsightSection(string Key, string Title, IReadOnlyList<InsightCard> Cards);

/// <summary>A deck theme played with the commander (counters, tokens…) and how many decks play it.</summary>
public sealed record InsightTheme(string Slug, string Name, int Decks);

/// <summary>
/// Community deck statistics for a commander: suggested cards by group, the themes it is played with and the average
/// number of cards of each type. Read from the module's commander statistics source (<c>sources.json</c>, "commanderInsights").
/// </summary>
public sealed class CommanderInsights
{
    /// <summary>Number of decks the statistics come from.</summary>
    public int Decks { get; init; }
    public IReadOnlyList<InsightSection> Sections { get; init; } = Array.Empty<InsightSection>();
    public IReadOnlyList<InsightTheme> Themes { get; init; } = Array.Empty<InsightTheme>();

    /// <summary>Average deck's cards of each type ("creature", "instant", "land"…), by lower-case type name.</summary>
    public IReadOnlyDictionary<string, int> AverageTypes { get; init; } = new Dictionary<string, int>();

    /// <summary>The type counts given, in the order a deck is usually read.</summary>
    public static readonly string[] TypeKeys = { "creature", "planeswalker", "instant", "sorcery", "artifact", "enchantment", "battle", "land" };

    /// <summary>Reads a commander page. Anything missing is left empty, so a change in the source shows fewer suggestions instead of failing.</summary>
    public static CommanderInsights Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var sections = new List<InsightSection>();
        int decks = 0;
        if (root.TryGetProperty("container", out var container) && container.TryGetProperty("json_dict", out var dict))
        {
            if (dict.TryGetProperty("card", out var card)) decks = Int(card, "num_decks");
            if (dict.TryGetProperty("cardlists", out var lists) && lists.ValueKind == JsonValueKind.Array)
                foreach (var list in lists.EnumerateArray())
                {
                    if (!list.TryGetProperty("cardviews", out var views) || views.ValueKind != JsonValueKind.Array) continue;
                    var cards = views.EnumerateArray()
                        .Where(v => Str(v, "name").Length > 0)
                        .Select(v => new InsightCard(Str(v, "name"), Num(v, "synergy"), Int(v, "num_decks"), Int(v, "potential_decks")))
                        .ToList();
                    if (cards.Count > 0) sections.Add(new InsightSection(Str(list, "tag"), Str(list, "header"), cards));
                }
        }
        var themes = new List<InsightTheme>();
        if (root.TryGetProperty("tag_counts", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (Str(t, "slug") is { Length: > 0 } slug) themes.Add(new InsightTheme(slug, Str(t, "value") is { Length: > 0 } v ? v : slug, Int(t, "count")));
        var types = new Dictionary<string, int>();
        foreach (var key in TypeKeys)
            if (root.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number) types[key] = n.GetInt32();
        return new CommanderInsights { Decks = decks, Sections = sections, Themes = themes, AverageTypes = types };
    }

    /// <summary>
    /// Reads an average deck page into a deck list: its commander(s) and main deck. Null when the page has no deck.
    /// </summary>
    public static DeckList? ParseAverageDeck(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("deck", out var deck) || deck.ValueKind != JsonValueKind.Object) return null;
        var list = new DeckList();
        if (deck.TryGetProperty("commander", out var commanders) && commanders.ValueKind == JsonValueKind.Array)
            foreach (var c in commanders.EnumerateArray())
                if (c.GetString() is { Length: > 0 } name) list.Commander.Add(new DeckEntry(1, name));
        if (deck.TryGetProperty("cards", out var groups) && groups.ValueKind == JsonValueKind.Object)
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var line in group.Value.EnumerateArray())
                    if (line.ValueKind == JsonValueKind.Array && line.GetArrayLength() >= 2 && line[0].GetString() is { Length: > 0 } name
                        && line[1].ValueKind == JsonValueKind.Number && line[1].GetInt32() > 0)
                        DeckList.Adjust(list.Main, name, line[1].GetInt32());
            }
        return list.Main.Count == 0 ? null : list;
    }

    /// <summary>
    /// The page name for a commander (or a pair of partners): each card's front face name in lower case, accents and
    /// punctuation dropped, words joined by hyphens; partners in alphabetical order, joined by a hyphen.
    /// </summary>
    public static string Slug(IEnumerable<string> commanderNames) =>
        string.Join('-', commanderNames.Select(SlugOf).Where(s => s.Length > 0).Order(StringComparer.Ordinal));

    private static string SlugOf(string name)
    {
        int faces = name.IndexOf("//", StringComparison.Ordinal);
        if (faces >= 0) name = name[..faces];
        var sb = new StringBuilder();
        foreach (char ch in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if ((ch == ' ' || ch == '-') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
