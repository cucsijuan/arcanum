// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Career;

/// <summary>A card of a collection: the card, the printing it is (set code in capitals, as in deck lists) and whether it is a foil.</summary>
public readonly record struct CardKey(string Name, string Set, string Number, bool Foil = false)
{
    public static CardKey Of(PoolCard card) => new(card.Name, card.Set.ToUpperInvariant(), card.Number, card.Foil);

    public static CardKey Of(DeckEntry entry) => new(entry.Name, entry.Set?.ToUpperInvariant() ?? "", entry.Number ?? "", entry.Foil);

    /// <summary>"Name|SET|number" ("|F" after it for a foil): the collection's dictionary key in saves.</summary>
    public override string ToString() => Foil ? $"{Name}|{Set}|{Number}|F" : $"{Name}|{Set}|{Number}";

    public static CardKey Parse(string text)
    {
        var parts = text.Split('|');
        return parts.Length switch
        {
            4 => new(parts[0], parts[1], parts[2], parts[3] == "F"),
            3 => new(parts[0], parts[1], parts[2]),
            _ => new(text, "", ""),
        };
    }

    /// <summary>A deck line for one copy of this card.</summary>
    public DeckEntry ToEntry(int count = 1) => new(count, Name, Set.Length > 0 ? Set : null, Number.Length > 0 ? Number : null, Foil);
}

/// <summary>A match that has started and has no result yet.</summary>
/// <param name="OpponentId">The regular played.</param>
/// <param name="Deck">The module deck they play.</param>
/// <param name="Reward">Daily matches: "cash" or "booster", the prize chosen for a win.</param>
public sealed record PendingMatch(string OpponentId, string Deck, string Reward = "")
{
    /// <summary>daily, practice or ante.</summary>
    public string Kind { get; init; } = CareerRules.Daily;

    /// <summary>The featured set when the match started: a booster prize is of this set.</summary>
    public string Set { get; init; } = "";

    /// <summary>The player's deck list (for the week's challenges); empty in matches started before it was kept.</summary>
    public string PlayerDeck { get; init; } = "";

    /// <summary>Ante matches: the card each player antes (<see cref="CardKey"/> text); the player's is out of the collection until the match ends.</summary>
    public string PlayerAnte { get; init; } = "";
    public string OpponentAnte { get; init; } = "";
}

/// <summary>One of a day's offers: a regular sells <see cref="Get"/> for <see cref="Price"/>, or trades it for <see cref="Give"/> (CardKey texts).</summary>
public sealed record CareerOffer(int Index, string Kind, string OpponentId, string Get, string Give, int Price);

/// <summary>An event entered: which, on what day, its seed (its boosters and pairings), the set drafted or the deck played.</summary>
public sealed record EventEntry(string Id, DateOnly Day, ulong Seed, string Set = "", string Deck = "");

/// <summary>Something that happened in the career, newest last (shown on the hub).</summary>
public sealed record CareerNote(DateTime Utc, string Text);

/// <summary>Everything a career has: money, cards, unopened boosters, the day and its daily matches.</summary>
public sealed class CareerState
{
    public ulong Seed { get; set; }
    public DateTime StartedUtc { get; set; }

    /// <summary>The latest time seen: a career's clock never goes back.</summary>
    public DateTime LastUtc { get; set; }

    /// <summary>The current day (UTC) and how many daily matches have been played on it.</summary>
    public DateOnly Day { get; set; }
    public int DailiesPlayed { get; set; }
    public int PracticePlayed { get; set; }
    public int AntesPlayed { get; set; }
    public PendingMatch? Pending { get; set; }

    /// <summary>The week's challenges' progress by "week:id"; a challenge whose progress reached its count is done.</summary>
    public SortedDictionary<string, int> Challenges
    {
        get => _challenges;
        set => _challenges = new(value, StringComparer.Ordinal);
    }

    private SortedDictionary<string, int> _challenges = new(StringComparer.Ordinal);

    /// <summary>Cash in cents.</summary>
    public int Cash { get; set; }

    /// <summary>Owned cards by <see cref="CardKey"/> text.</summary>
    public SortedDictionary<string, int> Collection
    {
        get => _collection;
        set => _collection = new(value, StringComparer.Ordinal); // loaded saves keep the same order
    }

    private SortedDictionary<string, int> _collection = new(StringComparer.Ordinal);

    /// <summary>Unopened boosters by set code (lower case).</summary>
    public SortedDictionary<string, int> Sealed
    {
        get => _sealed;
        set => _sealed = new(value, StringComparer.Ordinal);
    }

    private SortedDictionary<string, int> _sealed = new(StringComparer.Ordinal);

    /// <summary>Boosters opened so far: each one's contents come from the career seed and its number.</summary>
    public int BoostersOpened { get; set; }

    /// <summary>The store's offers of <see cref="OffersDay"/>, made when the day began (from the collection as it was then).</summary>
    public List<CareerOffer> Offers { get; set; } = new();
    public DateOnly OffersDay { get; set; }

    /// <summary>The event the player is in (entered, not finished yet).</summary>
    public EventEntry? Event { get; set; }

    /// <summary>Events played, as "day number:event id" (one entry a day each).</summary>
    public List<string> EventsPlayed { get; set; } = new();

    /// <summary>Qualification tokens won (some events take them as entry).</summary>
    public int Tokens { get; set; }

    /// <summary>The store's offers taken, as "day number:offer index".</summary>
    public List<string> OffersTaken { get; set; } = new();

    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Reputation { get; set; }

    public List<CareerNote> Notes { get; set; } = new();

    [JsonIgnore]
    public int CollectionSize => Collection.Values.Sum();

    public int Owned(CardKey card) => Collection.GetValueOrDefault(card.ToString());

    /// <summary>Copies owned of a card in any printing.</summary>
    public int OwnedByName(string name) =>
        Collection.Where(kv => CardKey.Parse(kv.Key).Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);

    public IEnumerable<(CardKey Card, int Count)> Cards => Collection.Select(kv => (CardKey.Parse(kv.Key), kv.Value));

    internal void AddCard(CardKey card, int count)
    {
        var key = card.ToString();
        int now = Collection.GetValueOrDefault(key) + count;
        if (now > 0) Collection[key] = now;
        else Collection.Remove(key);
    }

    internal void AddSealed(string set, int count)
    {
        int now = Sealed.GetValueOrDefault(set) + count;
        if (now > 0) Sealed[set] = now;
        else Sealed.Remove(set);
    }

    internal void Note(DateTime utc, string text)
    {
        Notes.Add(new CareerNote(utc, text));
        if (Notes.Count > 50) Notes.RemoveAt(0);
    }
}
