// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Formats;

/// <summary>
/// Deck construction rules of a format, loaded from a content module's formats/*.json. The engine knows nothing
/// about specific formats; it only applies these generic rules.
/// </summary>
public sealed record FormatRules
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Key into each card's legalities (null: every card is allowed).</summary>
    public string? Legality { get; init; }

    public int MinDeckSize { get; init; } = 60;
    public int? MaxDeckSize { get; init; }
    public int MaxCopies { get; init; } = 4;
    public int SideboardMax { get; init; } = 15;

    /// <summary>Cards with the Basic supertype ignore the copy limit.</summary>
    public bool BasicLandsUnlimited { get; init; } = true;

    /// <summary>Starting life total for games in this format.</summary>
    public int StartingLife { get; init; } = 20;

    public string Description { get; init; } = "";

    public static FormatRules Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var e = doc.RootElement;
        string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new FormatRules
        {
            Id = Str("id") ?? throw new FormatException("Format needs an \"id\"."),
            Name = Str("name") ?? throw new FormatException("Format needs a \"name\"."),
            Legality = Str("legality"),
            MinDeckSize = Int("minDeckSize") ?? 60,
            MaxDeckSize = Int("maxDeckSize"),
            MaxCopies = Int("maxCopies") ?? 4,
            SideboardMax = Int("sideboardMax") ?? 15,
            BasicLandsUnlimited = !e.TryGetProperty("basicLandsUnlimited", out var b) || b.GetBoolean(),
            StartingLife = Int("startingLife") ?? 20,
            Description = Str("description") ?? "",
        };
    }

    /// <summary>A format with no legality list: for casual decks.</summary>
    public static FormatRules Casual { get; } = new() { Id = "casual", Name = "Casual", Description = "Any cards, 60+ cards, up to 4 copies." };
}

public enum IssueSeverity { Error, Warning }

public sealed record DeckIssue(IssueSeverity Severity, string Message, string? Card = null);

public static class DeckValidator
{
    /// <summary>Checks a deck against a format. Errors make it illegal; warnings (e.g. unsupported cards) don't.</summary>
    public static List<DeckIssue> Validate(DeckList deck, FormatRules format, CardDatabase cards)
    {
        var issues = new List<DeckIssue>();
        int main = deck.Main.Sum(e => e.Count);
        int side = deck.Sideboard.Sum(e => e.Count);
        if (main < format.MinDeckSize) issues.Add(new(IssueSeverity.Error, $"Deck has {main} cards; {format.Name} needs at least {format.MinDeckSize}."));
        if (format.MaxDeckSize is { } max && main > max) issues.Add(new(IssueSeverity.Error, $"Deck has {main} cards; {format.Name} allows at most {max}."));
        if (side > format.SideboardMax) issues.Add(new(IssueSeverity.Error, $"Sideboard has {side} cards; at most {format.SideboardMax}."));

        foreach (var group in deck.Main.Concat(deck.Sideboard).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.First().Name;
            int copies = group.Sum(e => e.Count);
            var entry = cards.Find(name);
            if (entry is null)
            {
                issues.Add(new(IssueSeverity.Error, $"Unknown card: {name}.", name));
                continue;
            }
            bool basic = (entry.Definition.Supertypes & Supertype.Basic) != 0;
            string status = format.Legality is { } key ? entry.Record.Legalities.GetValueOrDefault(key, "not_legal") : "legal";
            if (status is "banned" or "not_legal")
                issues.Add(new(IssueSeverity.Error, $"{name} is {(status == "banned" ? "banned" : "not legal")} in {format.Name}.", name));
            else if (status == "restricted" && copies > 1)
                issues.Add(new(IssueSeverity.Error, $"{name} is restricted to one copy in {format.Name}.", name));
            else if (!(basic && format.BasicLandsUnlimited) && copies > format.MaxCopies)
                issues.Add(new(IssueSeverity.Error, $"{copies} copies of {name}; at most {format.MaxCopies}.", name));

            if (entry.Support != CardSupport.Full)
                issues.Add(new(IssueSeverity.Warning, $"{name} isn't fully supported yet; some of its rules won't work.", name));
        }
        return issues;
    }
}
