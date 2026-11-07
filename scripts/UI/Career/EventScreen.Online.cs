// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Career;
using Arcanum.UI.Menu;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// An event entered to play online: checking in for a start, waiting for it, the table the career service seated (this
/// player hosts it or joins its host), and after it the result, collected once the career service confirmed it.
/// </summary>
public partial class EventScreen
{
    private bool _onlinePolling;
    private static bool _autoCheckedIn;

    private void ShowOnline(EventSpec spec, EventEntry entry, CareerService.OnlineEntry online)
    {
        _title.Text = $"{spec.Name} · online";
        var now = Career.Now;
        void Line(string text, Color? color = null) => _content.AddChild(CareerStyle.Text(text, 20, color ?? Light, wrap: true));
        var buttons = new HFlowContainer();
        buttons.AddThemeConstantOverride("h_separation", 14);
        void Act(string text, Action action, bool primary = false) => buttons.AddChild(CareerStyle.Button(text, action, 20, primary));

        if (online.Start is null)
        {
            var starts = OnlineEvents.Starts(spec, entry.Day).Where(s => s > now).ToList();
            Line(starts.Count > 0
                ? $"Tables start at {string.Join(", ", starts.Select(s => s.ToLocalTime().ToString("HH:mm")))}. Check in up to {spec.Online!.CheckIn} minutes before a start; the career service seats everyone checked in, and the computer takes the empty seats."
                : "There are no more starts today.");
            // Testing: ARCANUM_CAREER_AUTO=online checks in as soon as check-in opens.
            if (OnlineEvents.CheckInOpen(spec, entry.Day, now) is { } openNow && OS.GetEnvironment("ARCANUM_CAREER_AUTO") == "online" && !_autoCheckedIn)
            {
                _autoCheckedIn = true;
                Callable.From(() => AutoCheckIn(openNow)).CallDeferred();
            }
            else if (OnlineEvents.CheckInOpen(spec, entry.Day, now) is { } open)
                Act($"Check in for {open.ToLocalTime():HH:mm}", async () =>
                {
                    if (await Career.CheckInAsync() is { } problem) MenuKit.Toast(this, problem);
                    Show();
                }, primary: true);
            else if (OnlineEvents.NextStart(spec, entry.Day, now) is { } next)
                Line($"Check-in for {next.ToLocalTime():HH:mm} opens at {next.AddMinutes(-spec.Online!.CheckIn).ToLocalTime():HH:mm}.", CareerStyle.InkDim);
            if (spec.Online is not { PvpOnly: true })
                Act(spec.Online!.Prizes.Count > 0 ? "Play against the computer now (its prizes against the computer)" : $"Play against the computer now ({spec.Online.OfflineScale:P0} of the prizes)",
                    () => { Career.PlayOnlineEventOffline(); Show(); });
            Act("Leave the event", ConfirmLeaveOnline);
            _content.AddChild(buttons);
            PollOnline();
            return;
        }

        if (online.Result is { } mine)
        {
            Line($"Your result: {mine.Wins}-{mine.Losses}{(mine.Draws > 0 ? $"-{mine.Draws}" : "")}, {Ordinal(mine.Place)} place.");
            Line("Waiting for the career service to confirm it (the host's report and yours must agree)…", CareerStyle.InkDim);
            Act("Leave without a result", ConfirmLeaveOnline);
            _content.AddChild(buttons);
            PollOnline(confirm: true);
            return;
        }

        // A two-day event between its days, or out at the cut.
        if (online.Out)
        {
            Line("You didn't make the cut. Your final place is known when the event ends; the career service confirms it then.");
            Act("Leave without a result", ConfirmLeaveOnline);
            _content.AddChild(buttons);
            PollOnline();
            return;
        }
        if (online.NextDay is { } nextDay && now < nextDay)
        {
            Line($"Day 1 is over. Day 2 starts at {nextDay.ToLocalTime():dddd HH:mm}: come back then to play on, in the same seat.");
            _content.AddChild(buttons);
            PollOnline();
            return;
        }

        if (now < online.Start)
        {
            Line($"Checked in for {online.Start.Value.ToLocalTime():HH:mm}. The table is seated when it starts.");
            _content.AddChild(buttons);
            PollOnline();
            return;
        }
        Line("Finding your table…", CareerStyle.InkDim);
        _content.AddChild(buttons);
        PollOnline();
    }

    /// <summary>Checks the career service every few seconds while this screen shows the online event, and acts on the table.</summary>
    private async void PollOnline(bool confirm = false)
    {
        if (_onlinePolling) return;
        _onlinePolling = true;
        try
        {
            // Not checked in: shown again when check-in opens or closes, so its button comes and goes.
            bool? open = Career.Online is { Start: null } && Career.CurrentEvent() is var (spec0, entry0, _)
                ? OnlineEvents.CheckInOpen(spec0, entry0.Day, Career.Now) is not null : null;
            while (IsInsideTree() && Career.Online is { } online)
            {
                if (open is { } wasOpen && online.Start is null && Career.CurrentEvent() is var (spec, entry, _)
                    && (OnlineEvents.CheckInOpen(spec, entry.Day, Career.Now) is not null) != wasOpen)
                {
                    Callable.From(() => Show()).CallDeferred(); // after this loop has stopped
                    return;
                }
                if (online.Start is { } start && Career.Now >= start && await Career.TableAsync() is var (status, _, view))
                {
                    if (status == "missed")
                    {
                        Shown("Your table started without you (you weren't checked in for it).", leave: true);
                        return;
                    }
                    if (view is not null && !IsInsideTree()) return;
                    // Not at the table when it ended (out at the cut, or away): the host's report of this player's result.
                    if (view is { HostResult: not null, Confirmed: null } && online.Result is null)
                    {
                        if (await Career.AdoptHostResultAsync(view) is { } problem) MenuKit.Toast(this, problem);
                        if (IsInsideTree()) Show();
                        return;
                    }
                    // The second day's host, also when out at the cut: the event goes on on this computer.
                    if (view is { Table.Round: 2, Resume: not null } && online.Result is null && !confirm)
                    {
                        Seated(view);
                        return;
                    }
                    // Between the days of a two-day event, or out at the cut: nothing to join.
                    if (online.Out || online.NextDay is { } next && Career.Now < next)
                    {
                        await ToSignal(GetTree().CreateTimer(5), SceneTreeTimer.SignalName.Timeout);
                        continue;
                    }
                    if (view is { Confirmed: { } confirmed } && online.Result is not null)
                    {
                        var result = Career.FinishOnlineEvent(view);
                        MenuKit.Toast(this, $"Confirmed: {confirmed.Wins}-{confirmed.Losses}, {Ordinal(confirmed.Place)}." + (result.Cash > 0 ? $" You won {CareerRules.Money(result.Cash)}." : ""));
                        Leave();
                        return;
                    }
                    if (view is not null && online.Result is null && !confirm)
                    {
                        Seated(view);
                        return;
                    }
                }
                await ToSignal(GetTree().CreateTimer(5), SceneTreeTimer.SignalName.Timeout);
            }
        }
        finally
        {
            _onlinePolling = false;
        }
    }

    /// <summary>The table is seated: its host opens the lobby and tells the service its code; the others join it.</summary>
    private async void Seated(TableView view)
    {
        // Already at the table (back here from its lobby or event): straight back to it.
        if (App.Instance.Online.EventSession is not null || App.Instance.Online.Lobby is not null && App.Instance.Online.HostedTable?.Id != null
            || App.Instance.Online.Lobby is not null && view.Table.Host != view.Table.People[view.Seat].Player)
        {
            App.Instance.GoTo(App.Instance.Online.EventSession is not null ? App.LimitedScene : App.OnlineScene);
            return;
        }
        foreach (var child in _content.GetChildren()) child.QueueFree();
        var people = string.Join(", ", view.Table.People.Select(p => p.Name));
        var me = view.Table.People[view.Seat];
        bool hosting = view.Table.Host == me.Player;
        if (view.Table.Round == 2)
        {
            // The second day: its host goes on from the first day's save; the others come back to their seats.
            if (hosting && view.Resume is not null)
            {
                _content.AddChild(CareerStyle.Text("You host the second day: the event goes on from the first day on this computer.", 20, Light, wrap: true));
                if (App.Instance.Online.HostedTable?.Id != view.Table.Id && !Career.ResumeTable(view))
                {
                    MenuKit.Toast(this, "The first day couldn't be resumed here.");
                    return;
                }
                for (int i = 0; i < 60 && IsInsideTree(); i++)
                {
                    if (await Career.PostTableCodeAsync(view)) break;
                    await ToSignal(GetTree().CreateTimer(2), SceneTreeTimer.SignalName.Timeout);
                }
                if (IsInsideTree()) App.Instance.GoTo(App.LimitedScene);
                return;
            }
            if (view.Table.Code is not null)
            {
                Career.RejoinTable(view);
                App.Instance.GoTo(App.LimitedScene);
                return;
            }
            _content.AddChild(CareerStyle.Text($"Waiting for {view.Table.People.First(p => p.Player == view.Table.Host).Name} to open the second day…", 20, CareerStyle.InkDim, wrap: true));
            await ToSignal(GetTree().CreateTimer(5), SceneTreeTimer.SignalName.Timeout);
            if (IsInsideTree()) Show();
            return;
        }
        _content.AddChild(CareerStyle.Text($"Your table: {people}" + (view.Table.People.Count < view.Table.Seats ? $", and {view.Table.Seats - view.Table.People.Count} computer players." : "."), 20, Light, wrap: true));
        if (hosting)
        {
            _content.AddChild(CareerStyle.Text("You host this table: the lobby opens on this computer, and the event starts when everyone is in.", 20, Light, wrap: true));
            if (App.Instance.Online.HostedTable?.Id != view.Table.Id) Career.HostTable(view);
            // The invite code goes to the others through the career service once the lobby is published.
            for (int i = 0; i < 60 && IsInsideTree(); i++)
            {
                if (await Career.PostTableCodeAsync(view)) break;
                await ToSignal(GetTree().CreateTimer(2), SceneTreeTimer.SignalName.Timeout);
            }
            if (IsInsideTree()) App.Instance.GoTo(App.OnlineScene);
            return;
        }
        if (view.Table.Code is null)
        {
            _content.AddChild(CareerStyle.Text($"Waiting for {view.Table.People.First(p => p.Player == view.Table.Host).Name} to open the table…", 20, CareerStyle.InkDim, wrap: true));
            await ToSignal(GetTree().CreateTimer(5), SceneTreeTimer.SignalName.Timeout);
            if (IsInsideTree()) Show();
            return;
        }
        Career.JoinTable(view);
        App.Instance.GoTo(App.OnlineScene);
    }

    private async void AutoCheckIn(DateTime start)
    {
        if (await Career.CheckInAsync() is { } problem) GD.PrintErr($"Check-in for {start:HH:mm}: {problem}");
        if (IsInsideTree()) Show();
    }

    private void Shown(string text, bool leave)
    {
        foreach (var child in _content.GetChildren()) child.QueueFree();
        _content.AddChild(CareerStyle.Text(text, 20, Light, wrap: true));
        if (leave) _content.AddChild(CareerStyle.Button("Leave the event", ConfirmLeaveOnline, 20, primary: true));
    }

    private void ConfirmLeaveOnline() =>
        MenuKit.Modal(this, "Leave the event?", "You leave it in last place, without prizes.", null,
            ("Stay", () => { }), ("Leave", () => { Career.AbandonOnlineEvent(); Leave(); }));

    private static string Ordinal(int n) => CareerRules.Ordinal(n);
}
