// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Decks;

public sealed record DeckEntry(int Count, string Name);

/// <summary>
/// Plain-text deck list: one "count name" per line ("4 Glade Cub"), "#" comments, blank lines ignored.
/// A "Sideboard" line starts the sideboard section. Set codes in parentheses after the name are ignored.
/// </summary>
public sealed partial class DeckList
{
    public List<DeckEntry> Main { get; } = new();
    public List<DeckEntry> Sideboard { get; } = new();

    public static DeckList Parse(string text)
    {
        var deck = new DeckList();
        var section = deck.Main;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            if (line.Equals("Sideboard", StringComparison.OrdinalIgnoreCase) || line.Equals("Sideboard:", StringComparison.OrdinalIgnoreCase))
            {
                section = deck.Sideboard;
                continue;
            }
            if (line.Equals("Deck", StringComparison.OrdinalIgnoreCase)) continue;

            var match = EntryLine().Match(line);
            if (!match.Success) throw new FormatException($"Can't read deck line '{line}'.");
            section.Add(new DeckEntry(int.Parse(match.Groups["count"].Value), match.Groups["name"].Value.Trim()));
        }
        return deck;
    }

    /// <summary>Card definitions for the main deck; names the database doesn't know are returned separately.</summary>
    public (List<CardDefinition> Cards, List<string> Unknown) Resolve(ICardDatabase database)
    {
        var cards = new List<CardDefinition>();
        var unknown = new List<string>();
        foreach (var entry in Main)
        {
            if (database.TryGet(entry.Name, out var def)) cards.AddRange(Enumerable.Repeat(def, entry.Count));
            else unknown.Add(entry.Name);
        }
        return (cards, unknown);
    }

    [GeneratedRegex(@"^(?<count>\d+)x?\s+(?<name>[^(]+?)(\s+\([^)]*\).*)?$")]
    private static partial Regex EntryLine();
}
