// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// An event entered to play online (docs/career-mode.md, *Scheduled online events*): check in with the career service
/// for a start, then at the start the table it seated — this player hosts it or joins its host's lobby — and at the end
/// the result, reported to the service and, once the host's and the player's reports agree, written into the career.
/// </summary>
public sealed partial class CareerService
{
    /// <summary>Where the player stands with the online event entered: the start checked in for, the table.</summary>
    public sealed class OnlineEntry
    {
        public DateTime? Start { get; set; }
        public string? Table { get; set; }
        public bool Reported { get; set; }

        /// <summary>The player's result at the table and the cards they drafted, kept when the event ended.</summary>
        public OwnResult? Result { get; set; }
        public List<string>? Pool { get; set; }

        /// <summary>A two-day event's first day is over: the player's seat token (to get back in), when the second day begins, and whether they're out (the cut).</summary>
        public string? SeatToken { get; set; }
        public DateTime? NextDay { get; set; }
        public bool Out { get; set; }
    }

    /// <summary>
    /// The first day of the two-day event at the table is over: the player keeps their seat's token to come back the
    /// next day (unless they missed the cut), and its host sends the first day's save to the career service, which
    /// hands it to whoever hosts the second day.
    /// </summary>
    private async void OnOnlineDayOver(string token, LimitedEvent ev, int seat, DateTime nextDay)
    {
        if (Online is not { Table: { } id, SeatToken: null } online) return;
        online.SeatToken = token;
        online.NextDay = nextDay;
        online.Out = ev.Dropped.Contains(seat);
        SaveOnline(online);
        if (App.Instance.Online.HostedTable?.Id == id && App.Instance.Online.HostedTableSave is { } save)
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using var service = await ServiceAsync();
                    if (service is not null)
                    {
                        using var reply = await service.Http.PostAsJsonAsync($"events/tables/{Uri.EscapeDataString(id)}/day", new { save }, Web);
                        if (reply.IsSuccessStatusCode) break;
                        GD.Print($"Career: the first day couldn't be saved: {await ErrorOf(reply)}");
                    }
                }
                catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
                {
                    GD.Print($"Career: the first day couldn't be saved yet: {e.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        App.Instance.Online.Leave();
        App.Instance.GoTo(App.EventScene);
    }

    /// <summary>
    /// A player who wasn't at the table when its event ended (out at the cut, or away): their result is the one the
    /// host reported for them, reported back as theirs.
    /// </summary>
    public async Task<string?> AdoptHostResultAsync(TableView view)
    {
        if (Online is not { Result: null } online || view.HostResult is not { } row) return null;
        online.Result = new OwnResult(row.Wins, row.Losses, row.Draws, row.Place);
        online.Pool ??= new List<string>();
        SaveOnline(online);
        return await ReportTableAsync(view);
    }

    /// <summary>
    /// How a game of the career event entered online looks on the board, as offline: the event's table, a big
    /// tournament's mat with its crest, and the broadcast overlay with the round's clock and the players' plates (this
    /// player's first). Null when no such game is being played.
    /// </summary>
    public MatchSetup? OnlineEventLook()
    {
        if (EnteredEvent is not { } spec || State.Event is not { Online: true } || App.Instance.Online.EventSession is not { Current: { } ev } session) return null;
        int me = session.Seat;
        int round = ev.Rounds.FindLastIndex(r => r.Any(m => m.Involves(me) && m.SeatB is not null));
        if (round < 0) return null;
        var match = ev.Rounds[round].First(m => m.Involves(me));
        int opponent = match.Opponent(me)!.Value;
        BroadcastPlate Plate(int seat)
        {
            var (w, l, d) = Record(ev, seat, match);
            return new BroadcastPlate(ev.Seats[seat].Name, d > 0 ? $"{w}-{l}-{d}" : $"{w}-{l}", match.WinsOf(seat));
        }
        return new MatchSetup(Array.Empty<GameSession.Seat>(), 20)
        {
            Label = $"{spec.Name} · {LimitedService.RoundName(ev, round)}",
            Table = EventTable(spec),
            TournamentMat = spec.TournamentMat ? spec.Name : null,
            Broadcast = spec.RoundMinutes > 0 || spec.TournamentMat
                ? new RoundBroadcast(spec.Name, LimitedService.RoundName(ev, round), ev.Deadline(match), new[] { Plate(me), Plate(opponent) }, ev.BestOf)
                : null,
        };
    }

    /// <summary>A seat's record in the matches before <paramref name="current"/> (byes are wins).</summary>
    private static (int Wins, int Losses, int Draws) Record(LimitedEvent ev, int seat, EventMatch current)
    {
        int wins = 0, losses = 0, draws = 0;
        foreach (var m in ev.Rounds.SelectMany(r => r).Where(m => m != current && m.Done && m.Involves(seat)))
        {
            if (m.SeatB is null) { wins++; continue; }
            int mine = m.WinsOf(seat), theirs = m.WinsOf(m.Opponent(seat)!.Value);
            if (mine > theirs) wins++;
            else if (mine < theirs) losses++;
            else draws++;
        }
        return (wins, losses, draws);
    }

    /// <summary>The online event this device played ended: its result is kept and reported to the career service.</summary>
    private async void OnOnlineEventFinished(LimitedEvent ev, int seat)
    {
        if (Online is not { Table: not null, Result: null } online) return;
        var (wins, losses, draws) = Record(ev, seat);
        online.Result = new OwnResult(wins, losses, draws, ev.Standings().IndexOf(seat) + 1);
        online.Pool = seat < ev.Seats.Count ? ev.Seats[seat].Pool.Select(c => CardKey.Of(c).ToString()).ToList() : new List<string>();
        SaveOnline(online);
        try
        {
            if (await TableAsync() is { Table: { } view }) await ReportTableAsync(view);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException)
        {
            GD.Print($"Career: the table's result couldn't be reported yet: {e.Message}");
        }
    }

    private string OnlinePath => ProjectSettings.GlobalizePath($"{Folder}/online.json");

    /// <summary>The online event in progress (null: the event entered, if any, isn't played online).</summary>
    public OnlineEntry? Online
    {
        get
        {
            if (_save?.State.Event is not { Online: true }) return null;
            // No file: the event entered online is played against the computer instead.
            try { return File.Exists(OnlinePath) ? JsonSerializer.Deserialize<OnlineEntry>(File.ReadAllText(OnlinePath)) ?? new() : null; }
            catch (Exception e) when (e is JsonException or IOException) { return new(); }
        }
    }

    private void SaveOnline(OnlineEntry? entry)
    {
        if (entry is null) { if (File.Exists(OnlinePath)) File.Delete(OnlinePath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(OnlinePath)!);
        File.WriteAllText(OnlinePath, JsonSerializer.Serialize(entry));
    }

    /// <summary>Enters today's event to play it online, then lets the career service check the entry (check-in needs it).</summary>
    public async Task EnterOnlineAsync(EventSpec spec, string pay, DeckList? deck = null)
    {
        Events.Abandon();
        if (File.Exists(PodPath)) File.Delete(PodPath);
        _pod = null;
        Do(new EnterEvent(Now, spec.Id, pay, deck?.Export(), Online: true));
        SaveOnline(new OnlineEntry());
        await SyncAsync();
    }

    /// <summary>Plays the event entered online against the computer instead, now (its offline share of the prizes).</summary>
    public void PlayOnlineEventOffline()
    {
        if (CurrentEvent() is not (var spec, var entry, null) || !entry.Online || spec.Online is { PvpOnly: true }) return;
        SaveOnline(null);
        Begin(spec, entry);
    }

    private async Task<HttpClientWithToken?> ServiceAsync()
    {
        if (await App.Instance.Online.IdTokenAsync() is not { } token) return null;
        return new HttpClientWithToken(Service(token));
    }

    /// <summary>A career service connection, disposed after use.</summary>
    private sealed class HttpClientWithToken(System.Net.Http.HttpClient http) : IDisposable
    {
        public System.Net.Http.HttpClient Http { get; } = http;
        public void Dispose() => Http.Dispose();
    }

    private static async Task<string> ErrorOf(System.Net.Http.HttpResponseMessage reply)
    {
        try { return JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() ?? $"({(int)reply.StatusCode})"; }
        catch (Exception) { return $"The career service answered {(int)reply.StatusCode}."; }
    }

    /// <summary>Checks in for the start whose check-in is open; null when done, otherwise why not.</summary>
    public async Task<string?> CheckInAsync()
    {
        if (CurrentEvent() is not var (spec, entry, _) || Online is not { } online) return "You aren't in an online event.";
        if (!IsChecked) await SyncAsync();
        if (!IsChecked) return CheckStatus.Length > 0 ? CheckStatus : "The career service hasn't checked this career yet.";
        using var service = await ServiceAsync();
        if (service is null) return "Sign in with Discord to play online.";
        using var reply = await service.Http.PostAsJsonAsync("events/checkin", new { content = _contentId, @event = spec.Id, day = entry.Day, name = Settings.PlayerName }, Web);
        if (!reply.IsSuccessStatusCode) return await ErrorOf(reply);
        var answer = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
        online.Start = answer.GetProperty("start").GetDateTime().ToUniversalTime();
        SaveOnline(online);
        return null;
    }

    /// <summary>The player's table for the start checked in for: ("waiting", how many are in, null) before it starts.</summary>
    public async Task<(string Status, int CheckedIn, TableView? Table)?> TableAsync()
    {
        if (CurrentEvent() is not var (spec, entry, _) || Online is not { Start: { } start } online) return null;
        using var service = await ServiceAsync();
        if (service is null) return null;
        string Q(string s) => Uri.EscapeDataString(s);
        var path = online.Table is { } id ? $"events/tables/{Q(id)}"
            : $"events/table?content={Q(_contentId)}&event={Q(spec.Id)}&day={entry.Day:yyyy-MM-dd}&start={Q(start.ToString("O"))}";
        using var reply = await service.Http.GetAsync(path);
        if (!reply.IsSuccessStatusCode) return null;
        if (online.Table is not null)
            return ("seated", 0, await reply.Content.ReadFromJsonAsync<TableView>(Web));
        var answer = JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement;
        var status = answer.GetProperty("status").GetString() ?? "";
        var table = answer.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.Object ? t.Deserialize<TableView>(Web) : null;
        if (table is not null)
        {
            online.Table = table.Table.Id;
            SaveOnline(online);
        }
        return (status, answer.GetProperty("checkedIn").GetInt32(), table);
    }

    /// <summary>Opens the table's lobby on this device (this player hosts it); the invite code goes to the service once published.</summary>
    public void HostTable(TableView view)
    {
        if (CurrentEvent() is not var (spec, entry, _)) return;
        // A two-day event: its first day ends with the cut (or halfway through the Swiss rounds) and goes on the next day.
        int dayEnds = spec.Days > 1 ? spec.CutAfter > 0 ? spec.CutAfter : (CareerRules.SwissRounds(spec, view.Table.Seats) + 1) / 2 : 0;
        App.Instance.Online.HostCareerTable(view.Table, $"{spec.Name} · online table", people =>
        {
            // A constructed event: each person plays the deck the career service has for them (from their checked career).
            if (spec.Kind == CareerRules.Constructed)
                people = people.Select((seat, i) => view.Table.Decks?.ElementAtOrDefault(i) is { Length: > 0 } deck
                    ? new EventSeat { Name = seat.Name, IsHuman = seat.IsHuman, Deck = DeckList.Parse(deck) } : seat).ToList();
            return BuildEvent(spec, entry, people, view.Table.Seats, view.Table.Seed).Event;
        }, dayEnds, spec.Days > 1 ? view.Table.Start.AddDays(1) : null);
    }

    /// <summary>The second day: this player hosts it now, going on from the first day's save.</summary>
    public bool ResumeTable(TableView view)
    {
        if (CurrentEvent() is not var (spec, _, _) || Online is not { SeatToken: { } token } || view.Resume is not { } save) return false;
        return App.Instance.Online.ResumeCareerTable(view.Table, $"{spec.Name} · online table", save, view.Table.People[view.Seat].Name, token);
    }

    /// <summary>The second day, hosted by another player: back into this player's seat through the table's invite code.</summary>
    public void RejoinTable(TableView view)
    {
        if (view.Table.Code is not { } code || Online is not { SeatToken: { } token }) return;
        App.Instance.Online.RejoinCareerTable(view.Table.People[view.Seat].Name, code, token);
    }

    /// <summary>Tells the service the hosted table's invite code (once the lobby is published); true when it took it.</summary>
    public async Task<bool> PostTableCodeAsync(TableView view)
    {
        if (App.Instance.Online.PublishedLobby is not { } listing) return false;
        using var service = await ServiceAsync();
        if (service is null) return false;
        using var reply = await service.Http.PostAsJsonAsync($"events/tables/{Uri.EscapeDataString(view.Table.Id)}/code", new { code = listing.InviteCode }, Web);
        return reply.IsSuccessStatusCode;
    }

    /// <summary>Joins the table's lobby with the invite code the host published, under this player's name at the table.</summary>
    public void JoinTable(TableView view)
    {
        if (view.Table.Code is not { } code) return;
        App.Instance.Online.JoinByCode(view.Table.People[view.Seat].Name, code);
    }

    /// <summary>
    /// The online event this device played is over: reports the player's own result (and, hosting, the whole table's) to
    /// the career service. Null when done, otherwise why not.
    /// </summary>
    public async Task<string?> ReportTableAsync(TableView view)
    {
        if (Online is not { } online) return "You aren't in an online event.";
        if (online.Result is not { } own) return "The event isn't over yet.";
        using var service = await ServiceAsync();
        if (service is null) return "Sign in with Discord to report the result.";
        string id = Uri.EscapeDataString(view.Table.Id);
        if (App.Instance.Online.HostedTableEvent is { Stage: EventStage.Finished } hosted && App.Instance.Online.HostedTable?.Id == view.Table.Id)
        {
            var standings = hosted.Standings();
            var results = Enumerable.Range(0, hosted.Seats.Count).Select(s =>
            {
                var (w, l, d) = Record(hosted, s);
                return new TableResult(s, w, l, d, standings.IndexOf(s) + 1);
            }).ToList();
            var matches = hosted.Rounds.SelectMany((round, r) => round.Where(m => m.Done && m.SeatB is not null)
                .Select(m => new TableMatch(r + 1, m.SeatA, m.SeatB!.Value, m.WinsA, m.WinsB, m.Draws))).ToList();
            using var sent = await service.Http.PostAsJsonAsync($"events/tables/{id}/report", new TableReport(results, matches), Web);
            if (!sent.IsSuccessStatusCode && sent.StatusCode != System.Net.HttpStatusCode.Conflict) return await ErrorOf(sent);
        }
        using var reply = await service.Http.PostAsJsonAsync($"events/tables/{id}/result", own, Web);
        if (!reply.IsSuccessStatusCode) return await ErrorOf(reply);
        online.Reported = true;
        SaveOnline(online);
        return null;
    }

    /// <summary>Writes the table's confirmed result into the career (with the drafted cards) and lets the service check it.</summary>
    public CareerResult FinishOnlineEvent(TableView view)
    {
        var confirmed = view.Confirmed ?? throw new CareerRuleException("The table's result isn't confirmed yet.");
        var pool = Online?.Pool ?? new List<string>();
        var result = Do(new FinishEvent(Now, confirmed.Wins, confirmed.Losses, confirmed.Draws, confirmed.Place, pool,
            Table: view.Table.Id, Players: view.Table.Seats, Pvp: view.Table.Pvp));
        SaveOnline(null);
        App.Instance.Online.Leave();
        return result;
    }

    /// <summary>A leaderboard ("rank" or "month"): the top and the player's row; null when the career service can't be reached.</summary>
    public async Task<List<LeaderboardRow>?> LeaderboardAsync(string board)
    {
        try
        {
            using var service = await ServiceAsync();
            if (service is null) return null;
            using var reply = await service.Http.GetAsync($"leaderboard/{board}");
            return reply.IsSuccessStatusCode ? await reply.Content.ReadFromJsonAsync<List<LeaderboardRow>>(Web) : null;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Leaves an online event without a result at a table (missed its start, or the result can't be confirmed): last place, no record.</summary>
    public CareerResult AbandonOnlineEvent()
    {
        var (spec, entry, _) = CurrentEvent() ?? throw new CareerRuleException("You aren't in an event.");
        var result = Do(new FinishEvent(Now, 0, 0, 0, CareerRules.Attendance(spec, entry.Seed)));
        SaveOnline(null);
        App.Instance.Online.Leave();
        return result;
    }
}
