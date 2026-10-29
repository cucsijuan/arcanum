// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.Career;

/// <summary>A person checked in for an online event's start: their account and the name they play under.</summary>
public sealed record CheckIn(string Player, string Name, DateTime Utc);

/// <summary>
/// A table of an online event, made by the career service at the start time: the people checked in at it (in their
/// seat order; the computer takes the other seats), its seed (the event's boosters and pairings), the player who hosts
/// it and, once they have opened the lobby, its invite code.
/// </summary>
public sealed record OnlineTable(string Id, string Event, DateOnly Day, DateTime Start, int Seats, ulong Seed, IReadOnlyList<CheckIn> People, string Host,
    string? Code = null)
{
    /// <summary>Another person played at it: its results pay the online prizes and count for the rank.</summary>
    public bool Pvp => People.Count >= 2;
}

/// <summary>A seat's final result at a table: its record and place (seats are counted from 0, the people's first).</summary>
public sealed record TableResult(int Seat, int Wins, int Losses, int Draws, int Place);

/// <summary>A match played at a table: who sat at each side and the games each won (draws: games drawn).</summary>
public sealed record TableMatch(int Round, int SeatA, int SeatB, int WinsA, int WinsB, int Draws = 0);

/// <summary>The host's report of a finished table: every seat's result and every match.</summary>
public sealed record TableReport(IReadOnlyList<TableResult> Results, IReadOnlyList<TableMatch> Matches);

/// <summary>One person's report of their own result at a table.</summary>
public sealed record OwnResult(int Wins, int Losses, int Draws, int Place);

/// <summary>A table as a player sees it: the table, and their result once confirmed.</summary>
public sealed record TableView(OnlineTable Table, TableResult? Confirmed, bool Reported);

/// <summary>A row of a leaderboard.</summary>
public sealed record LeaderboardRow(int Rank, string Name, int Score, bool Mine);

/// <summary>
/// The rules of online career events (docs/career-mode.md, *Scheduled online events*): when check-in is open, how the
/// people checked in are seated, which results are confirmed, and the rank and monthly standings made from them. The
/// career service runs them; the game shows the same times.
/// </summary>
public static class OnlineEvents
{
    /// <summary>How long after its start a table's host has to open the lobby before the next person hosts instead.</summary>
    public static readonly TimeSpan HostWait = TimeSpan.FromMinutes(10);

    /// <summary>A starting rank, and how much one match moves it.</summary>
    public const int StartRank = 1500, RankStep = 32;

    /// <summary>The start times of an online event on a day (UTC).</summary>
    public static IReadOnlyList<DateTime> Starts(EventSpec e, DateOnly day) =>
        (e.Online?.Starts ?? Array.Empty<string>())
        .Select(s => TimeOnly.TryParseExact(s, "HH:mm", out var t) ? day.ToDateTime(t, DateTimeKind.Utc) : (DateTime?)null)
        .OfType<DateTime>().Order().ToList();

    /// <summary>The start whose check-in is open at <paramref name="now"/> (from <see cref="OnlineSpec.CheckIn"/> minutes before it until it starts), or null.</summary>
    public static DateTime? CheckInOpen(EventSpec e, DateOnly day, DateTime now) =>
        Starts(e, day).Where(s => now < s && now >= s.AddMinutes(-(e.Online?.CheckIn ?? 30))).Select(s => (DateTime?)s).FirstOrDefault();

    /// <summary>The next start not begun yet at <paramref name="now"/>, or null.</summary>
    public static DateTime? NextStart(EventSpec e, DateOnly day, DateTime now) =>
        Starts(e, day).Where(s => s > now).Select(s => (DateTime?)s).FirstOrDefault();

    /// <summary>
    /// Seats the people checked in for a start: as few tables as hold them at <paramref name="seats"/> each, the people
    /// shared out as evenly as possible in the order they checked in; each table's first person hosts it.
    /// </summary>
    public static IReadOnlyList<OnlineTable> Seat(EventSpec e, DateOnly day, DateTime start, IReadOnlyList<CheckIn> people, int seats, ulong seed)
    {
        if (people.Count == 0) return Array.Empty<OnlineTable>();
        var ordered = people.OrderBy(p => p.Utc).ThenBy(p => p.Player, StringComparer.Ordinal).ToList();
        int count = (ordered.Count + seats - 1) / seats;
        var tables = new List<OnlineTable>();
        int next = 0;
        for (int t = 0; t < count; t++)
        {
            int size = ordered.Count / count + (t < ordered.Count % count ? 1 : 0);
            var at = ordered.GetRange(next, size);
            next += size;
            var tableSeed = (ulong)CareerRandom.For(seed, $"table:{e.Id}:{start:O}", t).NextInt64();
            tables.Add(new OnlineTable($"{e.Id}-{start:yyyyMMddHHmm}-{t + 1}", e.Id, day, start, seats, tableSeed, at, at[0].Player));
        }
        return tables;
    }

    /// <summary>
    /// The host to use now: the table's host, or, when no lobby was opened in time, the next person (one more for each
    /// <see cref="HostWait"/> that passes).
    /// </summary>
    public static string HostAt(OnlineTable table, DateTime now)
    {
        if (table.Code is not null) return table.Host;
        int late = now <= table.Start ? 0 : (int)((now - table.Start).Ticks / HostWait.Ticks);
        return table.People[late % table.People.Count].Player;
    }

    /// <summary>
    /// A seat's result is confirmed when the host's report and the person's own report agree; the host's own seat once
    /// another person's result is confirmed (alone at a table, the host's report is all there is).
    /// </summary>
    public static TableResult? Confirmed(OnlineTable table, TableReport? report, IReadOnlyDictionary<string, OwnResult> own, string player)
    {
        int seat = table.People.ToList().FindIndex(p => p.Player == player);
        if (seat < 0 || report?.Results.FirstOrDefault(r => r.Seat == seat) is not { } row) return null;
        if (player == table.Host && table.People.Count == 1) return row;
        if (player == table.Host)
            return table.People.Where(p => p.Player != table.Host).Any(p => Confirmed(table, report, own, p.Player) is not null) ? row : null;
        return own.TryGetValue(player, out var mine) && mine == new OwnResult(row.Wins, row.Losses, row.Draws, row.Place) ? row : null;
    }

    /// <summary>Whether a career's result of an online table is the confirmed one: null when it is, otherwise why not.</summary>
    public static string? Check(OnlineTable table, TableResult? confirmed, FinishEvent finish)
    {
        if (confirmed is null) return "the table's result isn't confirmed for this player";
        if (finish.Players != table.Seats || finish.Pvp != table.Pvp) return "the table isn't as the service seated it";
        if ((finish.Wins, finish.Losses, finish.Draws, finish.Place) != (confirmed.Wins, confirmed.Losses, confirmed.Draws, confirmed.Place))
            return "the record isn't the confirmed one";
        return null;
    }

    /// <summary>
    /// Ranks from the matches people played against each other at tables whose results are confirmed for both, in the
    /// order the tables started: each match moves both ranks by up to <see cref="RankStep"/> (the usual expected-score rule).
    /// </summary>
    public static Dictionary<string, double> Ranks(IEnumerable<(OnlineTable Table, TableReport Report, IReadOnlyDictionary<string, OwnResult> Own)> tables)
    {
        var ranks = new Dictionary<string, double>();
        foreach (var (table, report, own) in tables.OrderBy(t => t.Table.Start).ThenBy(t => t.Table.Id, StringComparer.Ordinal))
        {
            var confirmed = table.People.Select(p => Confirmed(table, report, own, p.Player) is not null).ToList();
            foreach (var match in report.Matches.OrderBy(m => m.Round))
            {
                if (match.SeatA >= table.People.Count || match.SeatB >= table.People.Count || !confirmed[match.SeatA] || !confirmed[match.SeatB]) continue;
                string a = table.People[match.SeatA].Player, b = table.People[match.SeatB].Player;
                double ra = ranks.GetValueOrDefault(a, StartRank), rb = ranks.GetValueOrDefault(b, StartRank);
                double expected = 1 / (1 + Math.Pow(10, (rb - ra) / 400));
                double score = match.WinsA > match.WinsB ? 1 : match.WinsA < match.WinsB ? 0 : 0.5;
                ranks[a] = ra + RankStep * (score - expected);
                ranks[b] = rb + RankStep * (expected - score);
            }
        }
        return ranks;
    }

    /// <summary>Monthly standings: 3 points for each match won and 1 for each drawn, at online tables with confirmed results.</summary>
    public static Dictionary<string, int> Points(IEnumerable<(OnlineTable Table, TableReport Report, IReadOnlyDictionary<string, OwnResult> Own)> tables)
    {
        var points = new Dictionary<string, int>();
        foreach (var (table, report, own) in tables)
            foreach (var person in table.People)
                if (Confirmed(table, report, own, person.Player) is { } row)
                    points[person.Player] = points.GetValueOrDefault(person.Player) + 3 * row.Wins + row.Draws;
        return points;
    }
}
