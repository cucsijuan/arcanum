// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Career;

/// <summary>A card of a collection: the card, the printing it is (set code in capitals, as in deck lists) and whether it is a foil.</summary>
public readonly record struct CardKey(string Name, string Set, string Number, bool Foil = false, bool Surge = false)
{
    public static CardKey Of(PoolCard card) => new(card.Name, card.Set.ToUpperInvariant(), card.Number, card.Foil || card.Surge, card.Surge);

    public static CardKey Of(DeckEntry entry) => new(entry.Name, entry.Set?.ToUpperInvariant() ?? "", entry.Number ?? "", entry.Foil, entry.Surge);

    /// <summary>"Name|SET|number" ("|F" after it for a foil, "|S" for a surge foil): the collection's dictionary key in saves.</summary>
    public override string ToString() => Surge ? $"{Name}|{Set}|{Number}|S" : Foil ? $"{Name}|{Set}|{Number}|F" : $"{Name}|{Set}|{Number}";

    public static CardKey Parse(string text)
    {
        var parts = text.Split('|');
        return parts.Length switch
        {
            4 => new(parts[0], parts[1], parts[2], parts[3] is "F" or "S", parts[3] == "S"),
            3 => new(parts[0], parts[1], parts[2]),
            _ => new(text, "", ""),
        };
    }

    /// <summary>A deck line for one copy of this card.</summary>
    public DeckEntry ToEntry(int count = 1) => new(count, Name, Set.Length > 0 ? Set : null, Number.Length > 0 ? Number : null, Foil) { Surge = Surge };
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

/// <summary>One of the store's singles on a day: the card, its price and its art.</summary>
public sealed record CareerSingle(int Index, CardKey Card, int Price, CardArt Art);

/// <summary>
/// A card another player lists on the forum on a day: who, the card, the price asked, its art, the lowest price they'd
/// take (hidden from the player) and their post's title and text.
/// </summary>
public sealed record CareerListing(int Index, string Seller, CardKey Card, int Price, CardArt Art, int Floor, string Title, string Body);

/// <summary>What became of a player's sale post.</summary>
public enum SaleStatus { Open, Offered, Sold, Expired, TakenDown }

/// <summary>A card the player posted for sale on the forum: out of the collection while it is up.</summary>
public sealed class SalePost
{
    public int Id { get; set; }
    public string Card { get; set; } = "";
    public int Ask { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>The last day the post's luck was looked at (the day it went up, at first).</summary>
    public DateOnly Checked { get; set; }

    /// <summary>Whether that day's midday look was the last one too (or the post went up after midday).</summary>
    public bool CheckedMidday { get; set; }
    public SaleStatus Status { get; set; }

    /// <summary>A buyer's lower offer waiting for an answer (cents), or the price it sold for.</summary>
    public int Offer { get; set; }
    public int SoldFor { get; set; }
}

/// <summary>An event entered: which, on what day, its seed (its boosters and pairings), the set drafted or the deck played.</summary>
/// <param name="Day">The day it was entered; <see cref="Start"/> is the day its run started (an event of several days).</param>
public sealed record EventEntry(string Id, DateOnly Day, ulong Seed, string Set = "", string Deck = "")
{
    public DateOnly Start { get; init; } = Day;

    /// <summary>Entered to play online (checking in with the career service for a table).</summary>
    public bool Online { get; init; }
}

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

    /// <summary>Invitations won: an invitational event's id → the day number its qualifying run starts.</summary>
    public SortedDictionary<string, int> Qualified
    {
        get => _qualified;
        set => _qualified = new(value, StringComparer.Ordinal);
    }

    private SortedDictionary<string, int> _qualified = new(StringComparer.Ordinal);

    /// <summary>The store's offers taken, as "day number:offer index".</summary>
    public List<string> OffersTaken { get; set; } = new();

    /// <summary>Singles bought from the store's case and listings bought on the forum, as "day number:index".</summary>
    public List<string> SinglesBought { get; set; } = new();
    public List<string> ListingsBought { get; set; } = new();

    /// <summary>Offers made on forum listings ("day number:index" → how many), and the sellers' counter-offers (cents).</summary>
    public SortedDictionary<string, int> ListingOffers { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> ListingCounters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The player's sale posts on the forum, oldest first.</summary>
    public List<SalePost> Posts { get; set; } = new();
    public int NextPostId { get; set; } = 1;

    /// <summary>The playmats bought (their ids), and the one the player puts on the table ("" for none).</summary>
    public List<string> Playmats { get; set; } = new();
    public string Playmat { get; set; } = "";

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
