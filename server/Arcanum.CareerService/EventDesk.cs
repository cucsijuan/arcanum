// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Arcanum.Data.Career;
using Microsoft.Data.Sqlite;

namespace Arcanum.CareerService;

public sealed record CheckInRequest(string Content, string Event, DateOnly Day, string Name);

public sealed record CheckInResponse(DateTime Start, int CheckedIn);

/// <summary>Before its start: how many have checked in; after: the player's table.</summary>
public sealed record TableResponse(string Status, int CheckedIn, TableView? Table);

public sealed record CodeRequest(string Code);

/// <summary>
/// Online career events at the service (see <see cref="OnlineEvents"/> for the rules): check-in for a start, the tables
/// made when it starts, their lobby codes, the host's report and each person's own result, and the leaderboards made
/// from the confirmed results. Everything is in the career database.
/// </summary>
public sealed class EventDesk
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CareerStore _store;
    private readonly CareerWorlds _worlds;
    private readonly object _seating = new();

    public EventDesk(CareerStore store, CareerWorlds worlds)
    {
        _store = store;
        _worlds = worlds;
        using var db = store.Open();
        CareerStore.Execute(db, """
            CREATE TABLE IF NOT EXISTS checkins (event TEXT NOT NULL, start TEXT NOT NULL, player TEXT NOT NULL, name TEXT NOT NULL, at TEXT NOT NULL,
                PRIMARY KEY (event, start, player));
            CREATE TABLE IF NOT EXISTS seated (event TEXT NOT NULL, start TEXT NOT NULL, PRIMARY KEY (event, start));
            CREATE TABLE IF NOT EXISTS tables (id TEXT PRIMARY KEY, start TEXT NOT NULL, data TEXT NOT NULL, report TEXT);
            CREATE TABLE IF NOT EXISTS results (tableid TEXT NOT NULL, player TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY (tableid, player));
            """);
    }

    private static string Utc(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Checks a player in for the start whose check-in is open; they must have entered the event online, in a career the service checked.</summary>
    public IResult CheckIn(string player, CheckInRequest request, DateTime now)
    {
        if (_worlds.For(request.Content)?.Event(request.Event, request.Day) is not { Online: not null } e) return Results.BadRequest(new { error = "There is no such online event." });
        using var db = _store.Open();
        var career = _store.Career(db, player);
        if (career is null || career.Seal.Count != career.Journal.Count || career.State.Event is not { Online: true } entry || entry.Id != e.Id || entry.Day != request.Day)
            return Results.BadRequest(new { error = "Enter the event online first (and let the career service check the career)." });
        if (OnlineEvents.CheckInOpen(e, request.Day, now) is not { } start) return Results.BadRequest(new { error = "Check-in isn't open." });
        var name = request.Name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 32)] : "Player";
        CareerStore.Execute(db, "INSERT OR IGNORE INTO checkins (event, start, player, name, at) VALUES ($e, $s, $p, $n, $a)",
            ("$e", e.Id), ("$s", Utc(start)), ("$p", player), ("$n", name), ("$a", Utc(now)));
        return Results.Ok(new CheckInResponse(start, CheckedIn(db, e.Id, start)));
    }

    private static int CheckedIn(SqliteConnection db, string ev, DateTime start)
    {
        using var count = CareerStore.Command(db, "SELECT COUNT(*) FROM checkins WHERE event = $e AND start = $s", ("$e", ev), ("$s", Utc(start)));
        return Convert.ToInt32(count.ExecuteScalar());
    }

    /// <summary>The player's table for a start (made for everyone checked in the first time someone asks after it starts).</summary>
    public IResult Table(string player, string content, string ev, DateOnly day, DateTime start, DateTime now)
    {
        if (_worlds.For(content)?.Event(ev, day) is not { Online: not null } e) return Results.BadRequest(new { error = "There is no such online event." });
        start = start.ToUniversalTime();
        using var db = _store.Open();
        if (now < start) return Results.Ok(new TableResponse("waiting", CheckedIn(db, ev, start), null));
        lock (_seating)
        {
            using var seated = CareerStore.Command(db, "SELECT 1 FROM seated WHERE event = $e AND start = $s", ("$e", ev), ("$s", Utc(start)));
            if (seated.ExecuteScalar() is null)
            {
                var people = new List<CheckIn>();
                using (var read = CareerStore.Command(db, "SELECT player, name, at FROM checkins WHERE event = $e AND start = $s", ("$e", ev), ("$s", Utc(start))))
                using (var rows = read.ExecuteReader())
                    while (rows.Read()) people.Add(new CheckIn(rows.GetString(0), rows.GetString(1), DateTime.Parse(rows.GetString(2), null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
                var seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
                using var tx = db.BeginTransaction();
                foreach (var table in OnlineEvents.Seat(e, day, start, people, Math.Max(2, e.Players), seed))
                    CareerStore.Execute(db, "INSERT INTO tables (id, start, data) VALUES ($i, $s, $d)", ("$i", table.Id), ("$s", Utc(start)), ("$d", JsonSerializer.Serialize(table, Json)));
                CareerStore.Execute(db, "INSERT INTO seated (event, start) VALUES ($e, $s)", ("$e", ev), ("$s", Utc(start)));
                tx.Commit();
            }
        }
        var mine = Tables(db, "start = $s", ("$s", Utc(start))).FirstOrDefault(t => t.Table.People.Any(p => p.Player == player));
        return mine.Table is null ? Results.Ok(new TableResponse("missed", CheckedIn(db, ev, start), null)) : Results.Ok(new TableResponse("seated", mine.Table.People.Count, View(db, mine, player, now)));
    }

    public IResult TableById(string player, string id, DateTime now)
    {
        using var db = _store.Open();
        var found = Tables(db, "id = $i", ("$i", id)).FirstOrDefault();
        if (found.Table is null || found.Table.People.All(p => p.Player != player)) return Results.NotFound();
        return Results.Ok(View(db, found, player, now));
    }

    /// <summary>The host opened the table's lobby: its invite code goes to the others.</summary>
    public IResult Code(string player, string id, string code, DateTime now)
    {
        using var db = _store.Open();
        var found = Tables(db, "id = $i", ("$i", id)).FirstOrDefault();
        if (found.Table is not { } table || OnlineEvents.HostAt(table, now) != player) return Results.BadRequest(new { error = "You don't host this table." });
        if (code.Length is < 4 or > 16) return Results.BadRequest(new { error = "That's no invite code." });
        Save(db, table with { Code = code, Host = player });
        return Results.Ok();
    }

    /// <summary>The host's report of the finished table (once).</summary>
    public IResult Report(string player, string id, TableReport report)
    {
        using var db = _store.Open();
        var found = Tables(db, "id = $i", ("$i", id)).FirstOrDefault();
        if (found.Table is not { Code: not null } table || table.Host != player) return Results.BadRequest(new { error = "You don't host this table." });
        if (found.Report is not null) return Results.Conflict(new { error = "The table was reported already." });
        if (report.Results.Count != table.Seats || report.Results.Select(r => r.Seat).Distinct().Count() != table.Seats || report.Results.Any(r => r.Seat < 0 || r.Seat >= table.Seats))
            return Results.BadRequest(new { error = "The report needs one result for each seat." });
        CareerStore.Execute(db, "UPDATE tables SET report = $r WHERE id = $i", ("$r", JsonSerializer.Serialize(report, Json)), ("$i", id));
        return Results.Ok();
    }

    /// <summary>A person's own result (once).</summary>
    public IResult Result(string player, string id, OwnResult result)
    {
        using var db = _store.Open();
        var found = Tables(db, "id = $i", ("$i", id)).FirstOrDefault();
        if (found.Table is null || found.Table.People.All(p => p.Player != player)) return Results.NotFound();
        CareerStore.Execute(db, "INSERT OR IGNORE INTO results (tableid, player, data) VALUES ($i, $p, $d)", ("$i", id), ("$p", player), ("$d", JsonSerializer.Serialize(result, Json)));
        return Results.Ok();
    }

    /// <summary>For the career checker: whether a career's online result is the table's confirmed one.</summary>
    public string? Check(string player, FinishEvent finish)
    {
        using var db = _store.Open();
        var found = Tables(db, "id = $i", ("$i", finish.Table!)).FirstOrDefault();
        if (found.Table is null) return "there is no such table";
        return OnlineEvents.Check(found.Table, OnlineEvents.Confirmed(found.Table, found.Report, Own(db, found.Table.Id), player), finish);
    }

    /// <summary>A leaderboard: "rank" (from matches between people) or "month" (this month's points); the top 20 and the player's row.</summary>
    public IReadOnlyList<LeaderboardRow> Leaderboard(string player, string board, DateTime now)
    {
        using var db = _store.Open();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var tables = (board == "month" ? Tables(db, "report IS NOT NULL AND start >= $s", ("$s", Utc(monthStart))) : Tables(db, "report IS NOT NULL"))
            .Select(t => (t.Table, t.Report!, (IReadOnlyDictionary<string, OwnResult>)Own(db, t.Table.Id))).ToList();
        var scores = board == "month"
            ? OnlineEvents.Points(tables).ToDictionary(p => p.Key, p => p.Value)
            : OnlineEvents.Ranks(tables).ToDictionary(p => p.Key, p => (int)Math.Round(p.Value));
        var names = tables.SelectMany(t => t.Table.People).GroupBy(p => p.Player).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Utc)!.Name);
        var ordered = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select((s, i) => new LeaderboardRow(i + 1, names.GetValueOrDefault(s.Key, "Player"), s.Value, s.Key == player)).ToList();
        return ordered.Take(20).Concat(ordered.Skip(20).Where(r => r.Mine)).ToList();
    }

    private TableView View(SqliteConnection db, (OnlineTable Table, TableReport? Report) found, string player, DateTime now)
    {
        var host = OnlineEvents.HostAt(found.Table, now);
        var table = found.Table with { Host = host };
        var own = Own(db, table.Id);
        return new TableView(table, table.People.ToList().FindIndex(p => p.Player == player), OnlineEvents.Confirmed(found.Table, found.Report, own, player),
            own.ContainsKey(player) || (found.Report is not null && player == found.Table.Host));
    }

    private void Save(SqliteConnection db, OnlineTable table) =>
        CareerStore.Execute(db, "UPDATE tables SET data = $d WHERE id = $i", ("$d", JsonSerializer.Serialize(table, Json)), ("$i", table.Id));

    private static List<(OnlineTable Table, TableReport? Report)> Tables(SqliteConnection db, string where, params (string, object?)[] values)
    {
        var tables = new List<(OnlineTable, TableReport?)>();
        using var read = CareerStore.Command(db, $"SELECT data, report FROM tables WHERE {where}", values);
        using var rows = read.ExecuteReader();
        while (rows.Read())
            tables.Add((JsonSerializer.Deserialize<OnlineTable>(rows.GetString(0), Json)!, rows.IsDBNull(1) ? null : JsonSerializer.Deserialize<TableReport>(rows.GetString(1), Json)));
        return tables;
    }

    private static Dictionary<string, OwnResult> Own(SqliteConnection db, string table)
    {
        var own = new Dictionary<string, OwnResult>();
        using var read = CareerStore.Command(db, "SELECT player, data FROM results WHERE tableid = $i", ("$i", table));
        using var rows = read.ExecuteReader();
        while (rows.Read()) own[rows.GetString(0)] = JsonSerializer.Deserialize<OwnResult>(rows.GetString(1), Json)!;
        return own;
    }
}
