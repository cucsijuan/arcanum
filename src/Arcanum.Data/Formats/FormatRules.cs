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

    /// <summary>
    /// Commander-style deck: a separate commander section (one legendary creature, or two with partner), deck size
    /// counts the commander, and every card must fit the commanders' color identity. Games use commander rules.
    /// </summary>
    public bool Commander { get; init; }

    /// <summary>
    /// Limited (draft, sealed): the deck is built from the player's pool plus any number of basic lands, with no limit
    /// on copies beyond what the pool holds; the rest of the pool is the sideboard.
    /// </summary>
    public bool Limited { get; init; }

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
            Commander = e.TryGetProperty("commander", out var c) && c.GetBoolean(),
            Limited = e.TryGetProperty("limited", out var l) && l.GetBoolean(),
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
        int main = deck.Main.Sum(e => e.Count) + (format.Commander ? deck.Commander.Sum(e => e.Count) : 0);
        if (format.Commander) ValidateCommanders(deck, format, cards, issues);
        int side = deck.Sideboard.Sum(e => e.Count);
        if (main < format.MinDeckSize) issues.Add(new(IssueSeverity.Error, $"Deck has {main} cards; {format.Name} needs at least {format.MinDeckSize}."));
        if (format.MaxDeckSize is { } max && main > max) issues.Add(new(IssueSeverity.Error, $"Deck has {main} cards; {format.Name} allows at most {max}."));
        if (side > format.SideboardMax) issues.Add(new(IssueSeverity.Error, $"Sideboard has {side} cards; at most {format.SideboardMax}."));

        foreach (var group in deck.Main.Concat(deck.Sideboard).Concat(format.Commander ? deck.Commander : Enumerable.Empty<DeckEntry>())
                     .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
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
            else if (!(basic && format.BasicLandsUnlimited) && copies > format.MaxCopies
                     && !entry.Record.OracleText.Contains("A deck can have any number of cards named", StringComparison.Ordinal))
                issues.Add(new(IssueSeverity.Error, $"{copies} copies of {name}; at most {format.MaxCopies}.", name));

            if (entry.Support != CardSupport.Full)
                issues.Add(new(IssueSeverity.Warning, $"{name} isn't fully supported yet; some of its rules won't work.", name));
        }
        return issues;
    }

    /// <summary>
    /// A limited deck (rule 100.2b): at least the format's minimum size, made of cards from <paramref name="pool"/>
    /// (no more copies than the pool has) plus any number of basic lands.
    /// </summary>
    public static List<DeckIssue> ValidateLimited(DeckList deck, FormatRules format, IReadOnlyList<Limited.PoolCard> pool, CardDatabase cards)
    {
        var issues = new List<DeckIssue>();
        int main = deck.Main.Sum(e => e.Count);
        if (main < format.MinDeckSize) issues.Add(new(IssueSeverity.Error, $"Deck has {main} cards; {format.Name} needs at least {format.MinDeckSize}."));
        var available = pool.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in deck.Main.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.First().Name;
            var entry = cards.Find(name);
            if (entry is null) { issues.Add(new(IssueSeverity.Error, $"Unknown card: {name}.", name)); continue; }
            bool basic = (entry.Definition.Supertypes & Supertype.Basic) != 0 && entry.Definition.Is(CardType.Land);
            int copies = group.Sum(e => e.Count);
            int have = available.GetValueOrDefault(name);
            if (!basic && copies > have)
                issues.Add(new(IssueSeverity.Error, have == 0 ? $"{name} isn't in your pool." : $"{copies} copies of {name}; your pool has {have}.", name));
            if (entry.Support != CardSupport.Full)
                issues.Add(new(IssueSeverity.Warning, $"{name} isn't fully supported yet; some of its rules won't work.", name));
        }
        return issues;
    }

    private static void ValidateCommanders(DeckList deck, FormatRules format, CardDatabase cards, List<DeckIssue> issues)
    {
        var commanders = deck.Commander.Select(e => cards.Find(e.Name)).OfType<CardEntry>().ToList();
        int count = deck.Commander.Sum(e => e.Count);
        bool pair = count == 2 && commanders.Count == 2 && CanBeTogether(commanders[0], commanders[1]);
        if (count == 0) issues.Add(new(IssueSeverity.Error, "Choose a commander (Commander section)."));
        else if (count > 2 || (count == 2 && !pair))
            issues.Add(new(IssueSeverity.Error, "Only one commander, or two that can be together: both with partner, partners with each other, "
                                                + "both with friends forever, or a commander that chooses a Background and a Background."));

        foreach (var c in commanders)
        {
            bool legendaryCreature = (c.Definition.Supertypes & Supertype.Legendary) != 0 && c.Definition.Is(CardType.Creature);
            bool allowed = legendaryCreature || c.Record.OracleText.Contains("can be your commander", StringComparison.OrdinalIgnoreCase)
                           || (pair && IsBackground(c)); // a Background is a commander only beside one that chooses it (rule 702.124k)
            if (!allowed) issues.Add(new(IssueSeverity.Error, $"{c.Name} can't be a commander (it isn't a legendary creature).", c.Name));
        }

        // Color identity (rule 903.4): every card's mana symbols must fit the commanders' combined identity.
        var identity = commanders.SelectMany(c => c.Record.ColorIdentity).ToHashSet();
        if (commanders.Count == 0) return;
        foreach (var entry in deck.Main)
        {
            if (cards.Find(entry.Name) is not { } card) continue;
            var outside = card.Record.ColorIdentity.Where(color => !identity.Contains(color)).ToList();
            if (outside.Count > 0)
                issues.Add(new(IssueSeverity.Error, $"{card.Name} is outside your commander's color identity ({string.Join("", outside)}).", card.Name));
        }
    }

    /// <summary>Whether two cards can be commanders together (rules 702.124 and 702.124h–k).</summary>
    public static bool CanBeTogether(CardEntry a, CardEntry b)
    {
        var la = Lines(a);
        var lb = Lines(b);
        bool Has(List<string> lines, string text) => lines.Any(l => l.Equals(text, StringComparison.OrdinalIgnoreCase));
        string? PartnerWith(List<string> lines) => lines.FirstOrDefault(l => l.StartsWith("Partner with ", StringComparison.OrdinalIgnoreCase))?[13..];
        string? PartnerVariant(List<string> lines) => lines.FirstOrDefault(l => l.StartsWith("Partner\u2014", StringComparison.Ordinal))?[8..];
        if (Has(la, "Partner") && Has(lb, "Partner")) return true;
        if (PartnerWith(la) is { } wa && PartnerWith(lb) is { } wb)
            return wa.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && wb.Equals(a.Name, StringComparison.OrdinalIgnoreCase);
        if (PartnerVariant(la) is { } va && PartnerVariant(lb) is { } vb) return va.Equals(vb, StringComparison.OrdinalIgnoreCase);
        if (Has(la, "Friends forever") && Has(lb, "Friends forever")) return true;
        if (Has(la, "Choose a Background") && IsBackground(b) || Has(lb, "Choose a Background") && IsBackground(a)) return true;
        if (Has(la, "Doctor's companion") && IsDoctor(b) || Has(lb, "Doctor's companion") && IsDoctor(a)) return true;
        return false;
    }

    /// <summary>The card's rules text lines without reminder text.</summary>
    private static List<string> Lines(CardEntry c) =>
        System.Text.RegularExpressions.Regex.Replace(c.Record.OracleText, @"\s*\([^)]*\)", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static bool IsBackground(CardEntry c) =>
        (c.Definition.Supertypes & Supertype.Legendary) != 0 && c.Definition.Is(CardType.Enchantment) && c.Definition.Subtypes.Contains("Background");

    /// <summary>A legendary Time Lord Doctor creature with no other creature types (rule 702.124m).</summary>
    private static bool IsDoctor(CardEntry c) =>
        (c.Definition.Supertypes & Supertype.Legendary) != 0 && c.Definition.Is(CardType.Creature)
        && c.Record.TypeLine.Split('\u2014') is { Length: 2 } parts && parts[1].Trim() == "Time Lord Doctor";
}
