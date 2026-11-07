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

    /// <summary>How well a computer player drafts and builds, 0–1 (see the draft bots).</summary>
    public double Skill { get; init; } = DefaultSkill;

    /// <summary>The skill of computer players that have none of their own (quick events, online seats).</summary>
    public const double DefaultSkill = 0.6;

    /// <summary>Every card the player opened or drafted.</summary>
    public List<PoolCard> Pool { get; init; } = new();

    /// <summary>
    /// The deck built from the pool (null until built). Cards of the pool not in it form the sideboard. In a constructed
    /// event: the deck brought, its sideboard in its Sideboard section (changed between games, see <see cref="Registered"/>).
    /// </summary>
    public DeckList? Deck { get; set; }

    /// <summary>
    /// A constructed event's deck as it was registered, main deck and sideboard (null in limited events): between games
    /// the deck may only change with these cards (<see cref="Formats.DeckValidator.ValidateSideboarding"/>).
    /// </summary>
    public DeckList? Registered { get; set; }

    /// <summary>
    /// The sideboard, as a deck list's main part: in limited play the cards of the pool not in the deck (rule 100.4b),
    /// in a constructed event the deck's sideboard (rule 100.4a).
    /// </summary>
    public DeckList SideboardFromPool()
    {
        if (Registered is not null)
        {
            var side = new DeckList();
            side.Main.AddRange(Deck?.Sideboard ?? Registered.Sideboard);
            return side;
        }
        var left = new DeckList();
        var inDeck = (Deck?.Main ?? new List<DeckEntry>()).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count), StringComparer.OrdinalIgnoreCase);
        foreach (var card in Pool)
        {
            if (inDeck.TryGetValue(card.Name, out int n) && n > 0) { inDeck[card.Name] = n - 1; continue; }
            left.Main.Add(card.ToEntry());
        }
        return left;
    }
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

    /// <summary>When the match's first game began (UTC): its round's clock runs from then (see <see cref="LimitedEvent.RoundMinutes"/>).</summary>
    public DateTime? Started { get; set; }

    /// <summary>Waiting for a player who isn't at the table (see <see cref="LimitedEvent.SeatCall"/>); due since <see cref="Due"/>.</summary>
    public bool Waiting { get; set; }

    /// <summary>When the match was due to begin (UTC), for a match that waited for a late player.</summary>
    public DateTime? Due { get; set; }

    /// <summary>The seats that got a game loss for being late.</summary>
    public List<int> Late { get; init; } = new();

    /// <summary>Neither player showed up: a match loss for each.</summary>
    public bool BothLost { get; set; }

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

    /// <summary>A constructed event: everyone brings a deck ("constructed:" source, no boosters).</summary>
    public bool IsConstructed => Source.StartsWith("constructed:", StringComparison.Ordinal);

    /// <summary>A cube's card list (empty for sets).</summary>
    public List<PoolCard> CubeCards { get; init; } = new();

    public int CubeBoosterSize { get; init; } = 15;
    public int BoostersPerPlayer { get; init; } = 3;
    public int BestOf { get; init; } = 1;

    /// <summary>Swiss rounds (the playoff rounds of a <see cref="TopCut"/> come after them).</summary>
    public int RoundsTotal { get; init; } = 3;
    public int Seed { get; init; }

    /// <summary>How many of the Swiss standings play the single-elimination playoff (8: a top 8); 0: none.</summary>
    public int TopCut { get; init; }

    /// <summary>A cut by record after this many Swiss rounds (the end of day 1): seats with fewer than <see cref="CutPoints"/> match points are out. 0: none.</summary>
    public int CutAfter { get; init; }
    public int CutPoints { get; init; }

    /// <summary>
    /// The Swiss rounds' time limit in minutes (0: untimed; playoff rounds are always untimed). Once it is up no new game
    /// begins, and a game in progress ends after the end-of-match procedure's additional turns.
    /// </summary>
    public int RoundMinutes { get; init; }

    public List<EventSeat> Seats { get; init; } = new();
    public EventStage Stage { get; set; }
    public DraftSnapshot? Draft { get; set; }
    public List<List<EventMatch>> Rounds { get; init; } = new();

    /// <summary>Seats no longer paired: dropped, or out at the cut by record. They keep their place in the standings.</summary>
    public SortedSet<int> Dropped { get; init; } = new();

    public int HumanSeat => Seats.FindIndex(s => s.IsHuman);

    /// <summary>The human player's unfinished match in the current round, if any.</summary>
    public EventMatch? CurrentMatch => Rounds.LastOrDefault()?.FirstOrDefault(m => m.Involves(HumanSeat) && !m.Done && m.SeatB is not null);

    /// <summary>Wins needed to take a match.</summary>
    public int WinsNeeded => BestOf / 2 + 1;

    /// <summary>The playoff's rounds (3 for a top 8).</summary>
    public int PlayoffRounds => TopCut < 2 ? 0 : (int)Math.Round(Math.Log2(TopCut));

    /// <summary>Whether a round (0-based) is a playoff round.</summary>
    public bool IsPlayoff(int round) => round >= RoundsTotal;

    /// <summary>Every round, Swiss and playoff, has been paired and played.</summary>
    public bool Complete => Rounds.Count >= RoundsTotal && Rounds.All(r => r.All(m => m.Done))
                            && (Rounds.Count >= RoundsTotal + PlayoffRounds || Rounds.Count == RoundsTotal && PlayoffSeeds().Count < 2);

    private IEnumerable<EventMatch> SwissMatches(int seat) => Rounds.Take(RoundsTotal).SelectMany(r => r).Where(m => m.Done && m.Involves(seat));

    private static bool Won(EventMatch m, int seat) => m.SeatB is null || !m.BothLost && m.WinsOf(seat) > m.WinsOf(m.Opponent(seat)!.Value);

    private static bool Drew(EventMatch m, int seat) => m.SeatB is not null && !m.BothLost && m.WinsOf(seat) == m.WinsOf(m.Opponent(seat)!.Value);

    /// <summary>Match points in the Swiss rounds (3 a win or a bye, 1 a draw), as pairings and standings use them.</summary>
    public int Points(int seat) => SwissMatches(seat).Sum(m => Won(m, seat) ? 3 : Drew(m, seat) ? 1 : 0);

    /// <summary>When a match's time is up (UTC), or null for an untimed one or one not begun.</summary>
    public DateTime? Deadline(EventMatch match) =>
        RoundMinutes > 0 && match.Started is { } started && !IsPlayoff(Rounds.FindIndex(r => r.Contains(match))) ? started.AddMinutes(RoundMinutes) : null;

    /// <summary>Whether a match's time is up at <paramref name="now"/>.</summary>
    public bool TimeUp(EventMatch match, DateTime now) => Deadline(match) is { } deadline && now >= deadline;

    /// <summary>The match's time is up between games: no new game begins, the match ends with the games played.</summary>
    public void EndOnTime(EventMatch match) => match.Done = true;

    /// <summary>
    /// Records one game of a match; the match ends when a player has enough wins (or no games are left), or when its
    /// time is up (<paramref name="timeUp"/>: no new game begins).
    /// </summary>
    public void RecordGame(EventMatch match, int? winnerSeat, bool timeUp)
    {
        RecordGame(match, winnerSeat);
        if (timeUp && !IsPlayoff(Rounds.FindIndex(r => r.Contains(match)))) match.Done = true;
    }

    /// <summary>Records one game of a match; the match ends when a player has enough wins (or no games are left).</summary>
    public void RecordGame(EventMatch match, int? winnerSeat)
    {
        if (winnerSeat == match.SeatA) match.WinsA++;
        else if (winnerSeat == match.SeatB) match.WinsB++;
        else match.Draws++;
        int played = match.WinsA + match.WinsB + match.Draws;
        bool decided = match.WinsA >= WinsNeeded || match.WinsB >= WinsNeeded || played >= BestOf;
        // A playoff match can't end in a draw: the players play on until one of them wins a game.
        if (decided && (match.WinsA != match.WinsB || !IsPlayoff(Rounds.FindIndex(r => r.Contains(match))))) match.Done = true;
    }

    /// <summary>
    /// A seat leaves the event (drops, or doesn't show up): its unfinished match is conceded (its opponent wins it) and
    /// it isn't paired again. It keeps its place in the standings.
    /// </summary>
    public void Drop(int seat)
    {
        Dropped.Add(seat);
        if (Rounds.LastOrDefault()?.FirstOrDefault(m => m.Involves(seat) && !m.Done) is not { } match) return;
        if (match.SeatB is not null)
        {
            if (match.SeatA == seat) match.WinsB = Math.Max(match.WinsB, WinsNeeded);
            else match.WinsA = Math.Max(match.WinsA, WinsNeeded);
        }
        match.Done = true;
    }

    /// <summary>
    /// Tardiness: a player who isn't at the table when the match is due gets a game loss once <see cref="SeatCall"/> has
    /// passed (the time to take one's seat after the pairings are posted), and a match loss, leaving the event, when still
    /// away <see cref="NoShowAfter"/> after it was due.
    /// </summary>
    public static readonly TimeSpan SeatCall = TimeSpan.FromMinutes(1), NoShowAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Game losses for the players late to a match: the opponent wins that game; when both are late each loses a game,
    /// so each wins one.
    /// </summary>
    public void GiveGameLoss(EventMatch match, IReadOnlyCollection<int> late)
    {
        match.Late.AddRange(late);
        if (late.Count == 1)
        {
            RecordGame(match, match.Opponent(late.First()));
            return;
        }
        match.WinsA++;
        match.WinsB++;
        bool decided = match.WinsA >= WinsNeeded && match.WinsB >= WinsNeeded || match.WinsA + match.WinsB + match.Draws >= BestOf;
        if (decided && !IsPlayoff(Rounds.FindIndex(r => r.Contains(match)))) match.Done = true;
    }

    /// <summary>
    /// The players still away <see cref="NoShowAfter"/> after the match was due: a match loss (the opponent wins it, or
    /// neither wins when both are away; in the playoff, then, the next round's opponent goes on without playing), and they
    /// leave the event.
    /// </summary>
    public void NoShow(EventMatch match, IReadOnlyCollection<int> away)
    {
        match.Waiting = false;
        if (away.Count == 2)
        {
            match.BothLost = true;
            match.Done = true;
            foreach (var seat in away) Dropped.Add(seat);
            return;
        }
        foreach (var seat in away)
            if (!match.Done) Drop(seat);
            else Dropped.Add(seat);
    }

    /// <summary>The cut by record, once its rounds are played: seats with fewer than <see cref="CutPoints"/> match points are out.</summary>
    public void ApplyCut()
    {
        if (CutAfter > 0 && Rounds.Count == CutAfter)
            foreach (var seat in Enumerable.Range(0, Seats.Count).Where(s => Points(s) < CutPoints)) Dropped.Add(seat);
    }

    /// <summary>
    /// Pairs the next round. Swiss: seats sorted by points (ties at random), each paired with the highest-placed seat it
    /// hasn't played yet; with an odd number, the lowest-placed seat that hasn't had a bye gets one. After the Swiss
    /// rounds, the playoff: the top of the standings seeded 1 against 8, 4 against 5, 2 against 7, 3 against 6, then the
    /// winners of each pair of matches.
    /// </summary>
    public List<EventMatch> PairNextRound(Random random)
    {
        if (Rounds.Count >= RoundsTotal) return PairPlayoffRound();
        ApplyCut();
        var opponents = Enumerable.Range(0, Seats.Count).Select(_ => new HashSet<int?>()).ToList();
        foreach (var m in Rounds.SelectMany(r => r))
        {
            opponents[m.SeatA].Add(m.SeatB);
            if (m.SeatB is { } b) opponents[b].Add(m.SeatA);
        }
        var points = Enumerable.Range(0, Seats.Count).Select(Points).ToList();
        var left = Enumerable.Range(0, Seats.Count).Where(s => !Dropped.Contains(s))
            .OrderByDescending(s => points[s]).ThenBy(_ => random.Next()).ToList();
        var round = new List<EventMatch>();
        int? bye = null;
        if (left.Count % 2 == 1)
        {
            bye = left.LastOrDefault(s => !opponents[s].Contains(null), left[^1]);
            left.Remove(bye.Value);
        }
        while (left.Count > 1)
        {
            int a = left[0];
            int b = left.Skip(1).FirstOrDefault(x => !opponents[a].Contains(x), left[1]);
            left.Remove(a);
            left.Remove(b);
            round.Add(new EventMatch { SeatA = a, SeatB = b });
        }
        if (bye is { } byeSeat) round.Add(new EventMatch { SeatA = byeSeat, SeatB = null, Done = true });
        Rounds.Add(round);
        return round;
    }

    /// <summary>The seats of the playoff in bracket order (1, 8, 4, 5, 2, 7, 3, 6 for a top 8): the top of the Swiss standings.</summary>
    public List<int> PlayoffSeeds()
    {
        if (TopCut < 2) return new List<int>();
        var swiss = SwissStandings().Where(s => !Dropped.Contains(s)).Take(TopCut).ToList();
        if (swiss.Count < TopCut) return new List<int>();
        var order = new List<int> { 1 };
        while (order.Count < TopCut) order = order.SelectMany(s => new[] { s, 2 * order.Count + 1 - s }).ToList();
        return order.Select(s => swiss[s - 1]).ToList();
    }

    private List<EventMatch> PairPlayoffRound()
    {
        var round = new List<EventMatch>();
        if (Rounds.Count == RoundsTotal)
        {
            var seeds = PlayoffSeeds();
            for (int i = 0; i + 1 < seeds.Count; i += 2) round.Add(new EventMatch { SeatA = seeds[i], SeatB = seeds[i + 1] });
        }
        else
        {
            // A match both players lost keeps its place in the bracket: its first seat stands in, out of the event, and
            // the next opponent wins without playing (both standing in: neither goes on).
            var winners = Rounds[^1].Select(m => m.BothLost ? (Seat: m.SeatA, Gone: true) : (Seat: Won(m, m.SeatA) ? m.SeatA : m.SeatB!.Value, Gone: false)).ToList();
            for (int i = 0; i + 1 < winners.Count; i += 2)
            {
                var (a, b) = (winners[i], winners[i + 1]);
                var match = new EventMatch { SeatA = a.Seat, SeatB = b.Seat };
                if (a.Gone && b.Gone) match.BothLost = match.Done = true;
                else if (a.Gone) (match.WinsB, match.Done) = (WinsNeeded, true);
                else if (b.Gone) (match.WinsA, match.Done) = (WinsNeeded, true);
                round.Add(match);
            }
        }
        Rounds.Add(round);
        return round;
    }

    /// <summary>
    /// The final order: the playoff's players by how far they went (the champion first; those out in the same round by
    /// their Swiss place), then everyone else by the Swiss standings.
    /// </summary>
    public List<int> Standings()
    {
        var swiss = SwissStandings();
        if (Rounds.Count <= RoundsTotal) return swiss;
        var reached = new Dictionary<int, int>(); // seat → playoff rounds won (+1 for the champion's last)
        var lost = new HashSet<int>(); // out in a match both players lost: only standing in after it
        for (int r = RoundsTotal; r < Rounds.Count; r++)
            foreach (var m in Rounds[r])
            {
                foreach (var seat in new[] { m.SeatA, m.SeatB!.Value }.Where(s => !lost.Contains(s)))
                    reached[seat] = r - RoundsTotal + (m.Done && Won(m, seat) ? 1 : 0);
                if (m.BothLost) lost.UnionWith(new[] { m.SeatA, m.SeatB.Value });
            }
        var rank = swiss.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        return reached.Keys.OrderByDescending(s => reached[s]).ThenBy(s => rank[s])
            .Concat(swiss.Where(s => !reached.ContainsKey(s))).ToList();
    }

    /// <summary>
    /// The Swiss standings: match points, then the opponents' match-win percentage, the game-win percentage and the
    /// opponents' game-win percentage (each percentage at least 33%, byes not counted as opponents; a bye is a 2-0 win).
    /// </summary>
    public List<int> SwissStandings()
    {
        int n = Seats.Count;
        var mwp = new double[n];
        var gwp = new double[n];
        var opponents = new List<int>[n];
        for (int s = 0; s < n; s++)
        {
            var matches = SwissMatches(s).ToList();
            opponents[s] = matches.Where(m => m.SeatB is not null).Select(m => m.Opponent(s)!.Value).ToList();
            mwp[s] = matches.Count == 0 ? 1 / 3.0 : Math.Max(1 / 3.0, Points(s) / (3.0 * matches.Count));
            int games = matches.Sum(m => m.SeatB is null ? 2 : m.WinsA + m.WinsB + m.Draws);
            int gamePoints = matches.Sum(m => m.SeatB is null ? 6 : 3 * m.WinsOf(s) + m.Draws);
            gwp[s] = games == 0 ? 1 / 3.0 : Math.Max(1 / 3.0, gamePoints / (3.0 * games));
        }
        double Average(List<int> seats, double[] of) => seats.Count == 0 ? 0 : seats.Average(o => of[o]);
        var points = Enumerable.Range(0, n).Select(Points).ToList();
        return Enumerable.Range(0, n)
            .OrderByDescending(s => points[s])
            .ThenByDescending(s => Average(opponents[s], mwp))
            .ThenByDescending(s => gwp[s])
            .ThenByDescending(s => Average(opponents[s], gwp))
            .ThenBy(s => s)
            .ToList();
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
            if (TopCut > 0) w.WriteNumber("topCut", TopCut);
            if (RoundMinutes > 0) w.WriteNumber("roundMinutes", RoundMinutes);
            if (CutAfter > 0)
            {
                w.WriteNumber("cutAfter", CutAfter);
                w.WriteNumber("cutPoints", CutPoints);
            }
            if (Dropped.Count > 0)
            {
                w.WriteStartArray("dropped");
                foreach (var seat in Dropped) w.WriteNumberValue(seat);
                w.WriteEndArray();
            }
            w.WriteString("stage", Stage.ToString());
            w.WriteStartArray("seats");
            foreach (var seat in Seats)
            {
                w.WriteStartObject();
                w.WriteString("name", seat.Name);
                w.WriteBoolean("human", seat.IsHuman);
                if (!seat.IsHuman) w.WriteNumber("skill", seat.Skill);
                WriteCards(w, "pool", seat.Pool);
                if (seat.Deck is not null) w.WriteString("deck", seat.Deck.Export());
                if (seat.Registered is not null) w.WriteString("registered", seat.Registered.Export());
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
                    if (m.Started is { } started) w.WriteString("started", started.ToString("O"));
                    if (m.Waiting) w.WriteBoolean("waiting", true);
                    if (m.Due is { } due) w.WriteString("due", due.ToString("O"));
                    if (m.Late.Count > 0)
                    {
                        w.WriteStartArray("late");
                        foreach (var seat in m.Late) w.WriteNumberValue(seat);
                        w.WriteEndArray();
                    }
                    if (m.BothLost) w.WriteBoolean("bothLost", true);
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
            TopCut = e.TryGetProperty("topCut", out var topCut) ? topCut.GetInt32() : 0,
            RoundMinutes = e.TryGetProperty("roundMinutes", out var roundMinutes) ? roundMinutes.GetInt32() : 0,
            CutAfter = e.TryGetProperty("cutAfter", out var cutAfter) ? cutAfter.GetInt32() : 0,
            CutPoints = e.TryGetProperty("cutPoints", out var cutPoints) ? cutPoints.GetInt32() : 0,
            Dropped = e.TryGetProperty("dropped", out var dropped) ? new SortedSet<int>(dropped.EnumerateArray().Select(x => x.GetInt32())) : new(),
            Stage = Enum.Parse<EventStage>(e.GetProperty("stage").GetString()!),
            Seats = e.GetProperty("seats").EnumerateArray().Select(s => new EventSeat
            {
                Name = s.GetProperty("name").GetString()!,
                IsHuman = s.GetProperty("human").GetBoolean(),
                Skill = s.TryGetProperty("skill", out var skill) ? skill.GetDouble() : EventSeat.DefaultSkill,
                Pool = ReadCards(s, "pool"),
                Deck = s.TryGetProperty("deck", out var deck) ? DeckList.Parse(deck.GetString()!) : null,
                Registered = s.TryGetProperty("registered", out var registered) ? DeckList.Parse(registered.GetString()!) : null,
            }).ToList(),
            Rounds = e.GetProperty("rounds").EnumerateArray().Select(r => r.EnumerateArray().Select(m => new EventMatch
            {
                SeatA = m.GetProperty("a").GetInt32(),
                SeatB = m.TryGetProperty("b", out var b) ? b.GetInt32() : null,
                WinsA = m.GetProperty("winsA").GetInt32(),
                WinsB = m.GetProperty("winsB").GetInt32(),
                Draws = m.GetProperty("draws").GetInt32(),
                Done = m.GetProperty("done").GetBoolean(),
                Started = m.TryGetProperty("started", out var started)
                    ? DateTime.Parse(started.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                Waiting = m.TryGetProperty("waiting", out var waiting) && waiting.GetBoolean(),
                Due = m.TryGetProperty("due", out var due) ? DateTime.Parse(due.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
                Late = m.TryGetProperty("late", out var late) ? late.EnumerateArray().Select(x => x.GetInt32()).ToList() : new(),
                BothLost = m.TryGetProperty("bothLost", out var bothLost) && bothLost.GetBoolean(),
            }).ToList()).ToList(),
        };
        if (e.TryGetProperty("draft", out var d))
            ev.Draft = new DraftSnapshot(d.GetProperty("seats").GetInt32(), d.GetProperty("rounds").GetInt32(), d.GetProperty("round").GetInt32(),
                d.GetProperty("pick").GetInt32(),
                d.GetProperty("packs").EnumerateArray().Select(ReadCardArray).ToList(),
                d.GetProperty("picks").EnumerateArray().Select(ReadCardArray).ToList());
        // A constructed event saved before decks were registered: the deck it has is the one brought.
        if (ev.IsConstructed)
            foreach (var seat in ev.Seats.Where(s => s.Registered is null && s.Deck is not null)) seat.Registered = DeckList.Parse(seat.Deck!.Export());
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
            if (c.Foil) w.WriteBooleanValue(true); // a foil has a fifth value
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static List<PoolCard> ReadCards(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) ? ReadCardArray(list) : new List<PoolCard>();

    private static List<PoolCard> ReadCardArray(JsonElement list) =>
        list.EnumerateArray().Select(c => new PoolCard(c[0].GetString()!, c[1].GetString()!, c[2].GetString()!, c[3].GetString()!,
            c.GetArrayLength() > 4 && c[4].GetBoolean())).ToList();
}
