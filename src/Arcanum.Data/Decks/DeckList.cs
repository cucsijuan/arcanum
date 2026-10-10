// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Decks;

/// <summary>
/// A deck line: count and card name, optionally the printing (set code and collector number) whose art to show, and
/// whether the copies are foil.
/// </summary>
public sealed record DeckEntry(int Count, string Name, string? Set = null, string? Number = null, bool Foil = false)
{
    /// <summary>Whether two entries name the same card in the same printing and finish.</summary>
    public bool SameCard(string name, string? set, string? number, bool foil = false) =>
        Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Set, set, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Number, number, StringComparison.OrdinalIgnoreCase)
        && Foil == foil;
}

/// <summary>
/// Plain-text deck list: one "count name" per line ("4 Glade Cub"), "#" comments, blank lines ignored.
/// A "Sideboard" line starts the sideboard section. A set code in parentheses and a collector number after the name
/// ("4 Glade Cub (ABC) 123") choose the printing, and with it the card's art; "*F*" at the end makes the copies foil
/// (as deck sites write it). "# token: Name|power/toughness|colors = id id…" lines choose the pictures the deck's tokens
/// of that kind are shown with; to deck sites they are comments.
/// </summary>
public sealed partial class DeckList
{
    public List<DeckEntry> Main { get; } = new();
    public List<DeckEntry> Sideboard { get; } = new();

    /// <summary>Commander(s), for formats that use them.</summary>
    public List<DeckEntry> Commander { get; } = new();

    /// <summary>
    /// Pictures chosen for the deck's tokens, by token kind (<see cref="CardData.CardDatabase.TokenKind"/>): each game
    /// shows that kind with one of them, picked at random. Kinds with none chosen get any picture the token has.
    /// </summary>
    public Dictionary<string, List<string>> TokenArt { get; } = new(StringComparer.OrdinalIgnoreCase);

    private const string TokenLine = "# token:";

    public static DeckList Parse(string text)
    {
        var deck = new DeckList();
        var section = deck.Main;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(TokenLine, StringComparison.OrdinalIgnoreCase) && line[TokenLine.Length..].Split('=', 2) is [var kind, var ids]
                && ids.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } pictures)
            {
                deck.TokenArt[kind.Trim()] = pictures.ToList();
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            if (line.Equals("Sideboard", StringComparison.OrdinalIgnoreCase) || line.Equals("Sideboard:", StringComparison.OrdinalIgnoreCase))
            {
                section = deck.Sideboard;
                continue;
            }
            if (line.Equals("Deck", StringComparison.OrdinalIgnoreCase) || line.Equals("Deck:", StringComparison.OrdinalIgnoreCase))
            {
                section = deck.Main;
                continue;
            }
            if (line.Equals("Commander", StringComparison.OrdinalIgnoreCase) || line.Equals("Commander:", StringComparison.OrdinalIgnoreCase))
            {
                section = deck.Commander;
                continue;
            }
            if (line.Equals("Companion", StringComparison.OrdinalIgnoreCase) || line.Equals("Companion:", StringComparison.OrdinalIgnoreCase))
            {
                section = deck.Sideboard; // companions live in the sideboard
                continue;
            }

            bool foil = line.EndsWith(FoilMark, StringComparison.OrdinalIgnoreCase);
            if (foil) line = line[..^FoilMark.Length].TrimEnd();
            var match = EntryLine().Match(line);
            if (!match.Success) throw new FormatException($"Can't read deck line '{line}'.");
            section.Add(new DeckEntry(int.Parse(match.Groups["count"].Value), match.Groups["name"].Value.Trim(),
                match.Groups["set"].Success && match.Groups["set"].Value.Trim() is { Length: > 0 } set ? set.ToUpperInvariant() : null,
                match.Groups["number"].Success ? match.Groups["number"].Value : null, foil));
        }
        return deck;
    }

    /// <summary>Text form, readable by <see cref="Parse"/> and by common deck sites ("4 Name" lines with sections).</summary>
    public string Export()
    {
        var sb = new System.Text.StringBuilder();
        void Section(string title, List<DeckEntry> entries)
        {
            if (entries.Count == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(title).Append('\n');
            foreach (var e in entries)
            {
                sb.Append(e.Count).Append(' ').Append(e.Name);
                if (e.Set is not null) sb.Append(" (").Append(e.Set).Append(')');
                if (e.Set is not null && e.Number is not null) sb.Append(' ').Append(e.Number);
                if (e.Foil) sb.Append(' ').Append(FoilMark);
                sb.Append('\n');
            }
        }
        Section("Commander", Commander);
        Section("Deck", Main);
        Section("Sideboard", Sideboard);
        if (TokenArt.Count > 0) sb.Append('\n');
        foreach (var (kind, pictures) in TokenArt.Where(t => t.Value.Count > 0).OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(TokenLine).Append(' ').Append(kind).Append(" = ").Append(string.Join(' ', pictures)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The end of a line of foil copies.</summary>
    private const string FoilMark = "*F*";

    /// <summary>Adds (or with a negative <paramref name="delta"/> removes) copies of a card (in a given printing and finish) in a section.</summary>
    public static void Adjust(List<DeckEntry> section, string name, int delta, string? set = null, string? number = null, bool foil = false)
    {
        int index = section.FindIndex(e => e.SameCard(name, set, number, foil));
        int count = (index >= 0 ? section[index].Count : 0) + delta;
        if (index >= 0)
        {
            if (count > 0) section[index] = section[index] with { Count = count };
            else section.RemoveAt(index);
        }
        else if (count > 0)
        {
            section.Add(new DeckEntry(count, name, set, number, foil));
        }
    }

    /// <summary>The definition for a deck line: in its printing (and foil when asked) when the database knows printings.</summary>
    public static bool TryResolve(ICardDatabase database, DeckEntry entry, out CardDefinition definition) =>
        database is CardData.CardDatabase cards
            ? cards.TryGet(entry.Name, entry.Set, entry.Number, entry.Foil, out definition)
            : database.TryGet(entry.Name, out definition);

    /// <summary>One definition per copy for the lines the database knows.</summary>
    public static List<CardDefinition> Definitions(ICardDatabase database, IEnumerable<DeckEntry> entries) =>
        entries.SelectMany(e => TryResolve(database, e, out var d) ? Enumerable.Repeat(d, e.Count) : Enumerable.Empty<CardDefinition>()).ToList();

    /// <summary>
    /// Card definitions for the main deck, their tokens shown with this deck's pictures (<see cref="TokenArt"/>); names
    /// the database doesn't know are returned separately.
    /// </summary>
    public (List<CardDefinition> Cards, List<string> Unknown) Resolve(ICardDatabase database)
    {
        var cards = new List<CardDefinition>();
        var unknown = new List<string>();
        foreach (var entry in Main)
        {
            if (TryResolve(database, entry, out var def)) cards.AddRange(Enumerable.Repeat(WithTokenArt(database, def), entry.Count));
            else unknown.Add(entry.Name);
        }
        return (cards, unknown);
    }

    /// <summary>The commander(s), their tokens shown with this deck's pictures.</summary>
    public List<CardDefinition> ResolveCommanders(ICardDatabase database) =>
        Definitions(database, Commander).Select(d => WithTokenArt(database, d)).ToList();

    /// <summary>The picture each token kind gets in the games of this deck object, picked once so tokens of a kind look alike.</summary>
    private readonly Dictionary<string, string?> _tokenPicks = new(StringComparer.OrdinalIgnoreCase);

    private CardDefinition WithTokenArt(ICardDatabase database, CardDefinition card)
    {
        if (database is not CardData.CardDatabase cards) return card;
        return CardData.CardDatabase.WithTokenArt(card, token =>
        {
            var kind = CardData.CardDatabase.TokenKind(token);
            if (!_tokenPicks.TryGetValue(kind, out var pick))
            {
                var chosen = TokenArt.TryGetValue(kind, out var list) && list.Count > 0 ? list : cards.TokenArts(token).Select(a => a.Id).ToList();
                _tokenPicks[kind] = pick = chosen.Count > 0 ? chosen[Random.Shared.Next(chosen.Count)] : null;
            }
            return pick;
        });
    }

    [GeneratedRegex(@"^(?<count>\d+)x?\s+(?<name>[^(]+?)(\s+\((?<set>[^)]*)\)(\s+(?<number>[^\s*]+))?.*)?$")]
    private static partial Regex EntryLine();
}
