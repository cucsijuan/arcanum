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

    /// <summary>Commander(s), for formats that use them.</summary>
    public List<DeckEntry> Commander { get; } = new();

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

            var match = EntryLine().Match(line);
            if (!match.Success) throw new FormatException($"Can't read deck line '{line}'.");
            section.Add(new DeckEntry(int.Parse(match.Groups["count"].Value), match.Groups["name"].Value.Trim()));
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
            foreach (var e in entries) sb.Append(e.Count).Append(' ').Append(e.Name).Append('\n');
        }
        Section("Commander", Commander);
        Section("Deck", Main);
        Section("Sideboard", Sideboard);
        return sb.ToString();
    }

    /// <summary>Adds (or with a negative <paramref name="delta"/> removes) copies of a card in a section.</summary>
    public static void Adjust(List<DeckEntry> section, string name, int delta)
    {
        int index = section.FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        int count = (index >= 0 ? section[index].Count : 0) + delta;
        if (index >= 0)
        {
            if (count > 0) section[index] = section[index] with { Count = count };
            else section.RemoveAt(index);
        }
        else if (count > 0)
        {
            section.Add(new DeckEntry(count, name));
        }
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
