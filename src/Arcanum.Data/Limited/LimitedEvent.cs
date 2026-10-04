// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Data.Decks;

namespace Arcanum.Data.Limited;

public enum LimitedMode { Draft, Sealed }

public enum EventStage { Drafting, Building, Playing, Finished }

/// <summary>A player in a limited event: the person in front of the screen or a computer player.</summary>
public sealed class EventSeat
{
    public required string Name { get; init; }
    public bool IsHuman { get; init; }

    /// <summary>Every card the player opened or drafted.</summary>
    public List<PoolCard> Pool { get; init; } = new();

    /// <summary>The deck built from the pool (null until built). Cards of the pool not in it form the sideboard.</summary>
    public DeckList? Deck { get; set; }
}

/// <summary>A match between two seats; <see cref="SeatB"/> is null for a bye.</summary>
public sealed class EventMatch
{
    public required int SeatA { get; init; }
    public int? SeatB { get; init; }
    public int WinsA { get; set; }
    public int WinsB { get; set; }
    public int Draws { get; set; }
    public bool Done { get; set; }

    public bool Involves(int seat) => SeatA == seat || SeatB == seat;

    public int? Opponent(int seat) => SeatA == seat ? SeatB : SeatB == seat ? SeatA : null;

    public int WinsOf(int seat) => SeatA == seat ? WinsA : WinsB;
}

/// <summary>
/// A draft or sealed event: seats, their pools and decks, the draft in progress and the Swiss rounds that follow.
/// Saved as JSON so it can be resumed.
/// </summary>
public sealed class LimitedEvent
{
    public required LimitedMode Mode { get; init; }

    /// <summary>"set:abc" for a set's boosters, "cube:Name" for a cube.</summary>
    public required string Source { get; init; }

    /// <summary>A cube's card list (empty for sets).</summary>
    public List<PoolCard> CubeCards { get; init; } = new();

    public int CubeBoosterSize { get; init; } = 15;
    public int BoostersPerPlayer { get; init; } = 3;
    public int BestOf { get; init; } = 1;
    public int RoundsTotal { get; init; } = 3;
    public int Seed { get; init; }

    public List<EventSeat> Seats { get; init; } = new();
    public EventStage Stage { get; set; }
    public DraftSnapshot? Draft { get; set; }
    public List<List<EventMatch>> Rounds { get; init; } = new();

    public int HumanSeat => Seats.FindIndex(s => s.IsHuman);

    /// <summary>The human player's unfinished match in the current round, if any.</summary>
    public EventMatch? CurrentMatch => Rounds.LastOrDefault()?.FirstOrDefault(m => m.Involves(HumanSeat) && !m.Done && m.SeatB is not null);

    /// <summary>Wins needed to take a match.</summary>
    public int WinsNeeded => BestOf / 2 + 1;

    /// <summary>Match points (3 a win, 1 a draw), as Swiss pairings and standings use them.</summary>
    public int Points(int seat) => Rounds.SelectMany(r => r).Where(m => m.Done && m.Involves(seat)).Sum(m =>
        m.SeatB is null ? 3 : m.WinsOf(seat) > m.WinsOf(m.Opponent(seat)!.Value) ? 3 : m.WinsOf(seat) == m.WinsOf(m.Opponent(seat)!.Value) ? 1 : 0);

    /// <summary>Records one game of a match; the match ends when a player has enough wins (or no games are left).</summary>
    public void RecordGame(EventMatch match, int? winnerSeat)
    {
        if (winnerSeat == match.SeatA) match.WinsA++;
        else if (winnerSeat == match.SeatB) match.WinsB++;
        else match.Draws++;
        int played = match.WinsA + match.WinsB + match.Draws;
        if (match.WinsA >= WinsNeeded || match.WinsB >= WinsNeeded || played >= BestOf) match.Done = true;
    }

    /// <summary>
    /// Pairs the next Swiss round: seats sorted by points (ties at random), each paired with the highest-placed
    /// seat it hasn't played yet; an odd seat out gets a bye.
    /// </summary>
    public List<EventMatch> PairNextRound(Random random)
    {
        var order = Enumerable.Range(0, Seats.Count).OrderByDescending(Points).ThenBy(_ => random.Next()).ToList();
        bool Played(int a, int b) => Rounds.SelectMany(r => r).Any(m => m.Involves(a) && m.Opponent(a) == b);
        var round = new List<EventMatch>();
        var left = order.ToList();
        while (left.Count > 1)
        {
            int a = left[0];
            int b = left.Skip(1).FirstOrDefault(x => !Played(a, x), left[1]);
            left.Remove(a);
            left.Remove(b);
            round.Add(new EventMatch { SeatA = a, SeatB = b });
        }
        if (left.Count == 1) round.Add(new EventMatch { SeatA = left[0], SeatB = null, Done = true });
        Rounds.Add(round);
        return round;
    }

    /// <summary>Seats ordered by points, then by game win percentage.</summary>
    public List<int> Standings() => Enumerable.Range(0, Seats.Count)
        .OrderByDescending(Points)
        .ThenByDescending(s => GameWinRate(s))
        .ToList();

    private double GameWinRate(int seat)
    {
        var matches = Rounds.SelectMany(r => r).Where(m => m.Done && m.SeatB is not null && m.Involves(seat)).ToList();
        int games = matches.Sum(m => m.WinsA + m.WinsB + m.Draws);
        return games == 0 ? 0 : (double)matches.Sum(m => m.WinsOf(seat)) / games;
    }

    // ---------------------------------------------------------------- saving

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("mode", Mode.ToString());
            w.WriteString("source", Source);
            WriteCards(w, "cube", CubeCards);
            w.WriteNumber("cubeBoosterSize", CubeBoosterSize);
            w.WriteNumber("boostersPerPlayer", BoostersPerPlayer);
            w.WriteNumber("bestOf", BestOf);
            w.WriteNumber("roundsTotal", RoundsTotal);
            w.WriteNumber("seed", Seed);
            w.WriteString("stage", Stage.ToString());
            w.WriteStartArray("seats");
            foreach (var seat in Seats)
            {
                w.WriteStartObject();
                w.WriteString("name", seat.Name);
                w.WriteBoolean("human", seat.IsHuman);
                WriteCards(w, "pool", seat.Pool);
                if (seat.Deck is not null) w.WriteString("deck", seat.Deck.Export());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (Draft is { } d)
            {
                w.WriteStartObject("draft");
                w.WriteNumber("seats", d.Seats);
                w.WriteNumber("rounds", d.Rounds);
                w.WriteNumber("round", d.Round);
                w.WriteNumber("pick", d.PickInRound);
                w.WriteStartArray("packs");
                foreach (var pack in d.Packs) WriteCardArray(w, pack);
                w.WriteEndArray();
                w.WriteStartArray("picks");
                foreach (var picks in d.Picks) WriteCardArray(w, picks);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteStartArray("rounds");
            foreach (var round in Rounds)
            {
                w.WriteStartArray();
                foreach (var m in round)
                {
                    w.WriteStartObject();
                    w.WriteNumber("a", m.SeatA);
                    if (m.SeatB is { } b) w.WriteNumber("b", b);
                    w.WriteNumber("winsA", m.WinsA);
                    w.WriteNumber("winsB", m.WinsB);
                    w.WriteNumber("draws", m.Draws);
                    w.WriteBoolean("done", m.Done);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static LimitedEvent FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var ev = new LimitedEvent
        {
            Mode = Enum.Parse<LimitedMode>(e.GetProperty("mode").GetString()!),
            Source = e.GetProperty("source").GetString()!,
            CubeCards = ReadCards(e, "cube"),
            CubeBoosterSize = e.GetProperty("cubeBoosterSize").GetInt32(),
            BoostersPerPlayer = e.GetProperty("boostersPerPlayer").GetInt32(),
            BestOf = e.GetProperty("bestOf").GetInt32(),
            RoundsTotal = e.GetProperty("roundsTotal").GetInt32(),
            Seed = e.GetProperty("seed").GetInt32(),
            Stage = Enum.Parse<EventStage>(e.GetProperty("stage").GetString()!),
            Seats = e.GetProperty("seats").EnumerateArray().Select(s => new EventSeat
            {
                Name = s.GetProperty("name").GetString()!,
                IsHuman = s.GetProperty("human").GetBoolean(),
                Pool = ReadCards(s, "pool"),
                Deck = s.TryGetProperty("deck", out var deck) ? DeckList.Parse(deck.GetString()!) : null,
            }).ToList(),
            Rounds = e.GetProperty("rounds").EnumerateArray().Select(r => r.EnumerateArray().Select(m => new EventMatch
            {
                SeatA = m.GetProperty("a").GetInt32(),
                SeatB = m.TryGetProperty("b", out var b) ? b.GetInt32() : null,
                WinsA = m.GetProperty("winsA").GetInt32(),
                WinsB = m.GetProperty("winsB").GetInt32(),
                Draws = m.GetProperty("draws").GetInt32(),
                Done = m.GetProperty("done").GetBoolean(),
            }).ToList()).ToList(),
        };
        if (e.TryGetProperty("draft", out var d))
            ev.Draft = new DraftSnapshot(d.GetProperty("seats").GetInt32(), d.GetProperty("rounds").GetInt32(), d.GetProperty("round").GetInt32(),
                d.GetProperty("pick").GetInt32(),
                d.GetProperty("packs").EnumerateArray().Select(ReadCardArray).ToList(),
                d.GetProperty("picks").EnumerateArray().Select(ReadCardArray).ToList());
        return ev;
    }

    private static void WriteCards(Utf8JsonWriter w, string name, IEnumerable<PoolCard> cards)
    {
        w.WritePropertyName(name);
        WriteCardArray(w, cards);
    }

    private static void WriteCardArray(Utf8JsonWriter w, IEnumerable<PoolCard> cards)
    {
        w.WriteStartArray();
        foreach (var c in cards)
        {
            w.WriteStartArray();
            w.WriteStringValue(c.Name);
            w.WriteStringValue(c.Set);
            w.WriteStringValue(c.Number);
            w.WriteStringValue(c.Rarity);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static List<PoolCard> ReadCards(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) ? ReadCardArray(list) : new List<PoolCard>();

    private static List<PoolCard> ReadCardArray(JsonElement list) =>
        list.EnumerateArray().Select(c => new PoolCard(c[0].GetString()!, c[1].GetString()!, c[2].GetString()!, c[3].GetString()!)).ToList();
}
