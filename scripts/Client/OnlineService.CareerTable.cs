// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Career;
using Arcanum.Data.Limited;
using Arcanum.Net.Events;
using Arcanum.Net.Client;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;

namespace Arcanum.Client;

/// <summary>
/// A career event's online table hosted on this device: the career service seated the people, this player hosts. The
/// lobby isn't listed in the browser (the others get its invite code from the service); the event starts when everyone
/// of the table is in, or after <see cref="TableWait"/> with whoever is (the computer takes the other seats).
/// </summary>
public partial class OnlineService
{
    /// <summary>How long a table's host waits for its people before starting without the missing ones.</summary>
    public static readonly TimeSpan TableWait = TimeSpan.FromMinutes(5);

    /// <param name="Build">Makes the event from the table's people (null: a second day, resumed from its save).</param>
    /// <param name="DayEndsAfter">A two-day event: the rounds of its first day (0: one day), and when the second begins.</param>
    private sealed record CareerTableSetup(OnlineTable Table, Func<IReadOnlyList<EventSeat>, LimitedEvent>? Build, DateTime Since, int DayEndsAfter = 0, DateTime? NextDay = null);

    private CareerTableSetup? _careerTable;
    private bool _unlisted;

    /// <summary>The online table this device hosts (null: none).</summary>
    public OnlineTable? HostedTable => _careerTable?.Table;

    /// <summary>
    /// Opens the lobby of a career table this player hosts. <paramref name="build"/> makes the event from the table's
    /// people in their order (seats of the computer follow them).
    /// </summary>
    public void HostCareerTable(OnlineTable table, string title, Func<IReadOnlyList<EventSeat>, LimitedEvent> build, int dayEndsAfter = 0, DateTime? nextDay = null)
    {
        Leave();
        var settings = new LobbySettings("Limited", false, 20, table.Seats, Version, ContentId, Event: title);
        _lobby = new LobbyHost(settings, _ => new DeckCheck(Array.Empty<Arcanum.Engine.Cards.CardDefinition>(), null, null));
        _lobby.Changed += () => Changed?.Invoke();
        if (!Listen(0))
        {
            _lobby = null;
            return;
        }
        _lobby.AddListener(_tcp!);
        _lobby.AddListener(_loopback!);
        _careerTable = new CareerTableSetup(table, build, DateTime.UtcNow, dayEndsAfter, nextDay);
        _unlisted = true;
        ListAddresses();
        OpenRouterPort();
        var host = table.People.First(p => p.Player == table.Host);
        JoinLobby(_loopback!.Connect(), new ClientIdentity(host.Name, _lobby.HostToken, Version, ContentId, OwnPlaymat));
        PublishHostedLobby();
    }

    /// <summary>
    /// The second day of a two-day event at a career table this player hosts now: the event goes on from the first
    /// day's save (no lobby to fill), this player back in their seat with its token; the others come back with theirs
    /// through the invite code the service passes on.
    /// </summary>
    public bool ResumeCareerTable(OnlineTable table, string title, string save, string name, string token)
    {
        Leave();
        EventHost host;
        try
        {
            var ev = LimitedEvent.FromJson(System.Text.Json.Nodes.JsonNode.Parse(save)!["event"]!.ToJsonString());
            host = EventHost.Restore(save, Rules(ev), EventOptions());
        }
        catch (Exception e)
        {
            Status?.Invoke($"The first day can't be resumed: {e.Message}");
            return false;
        }
        if (host.SeatOf(token) < 0)
        {
            Status?.Invoke("Your seat isn't in the first day's save.");
            return false;
        }
        var settings = new LobbySettings("Limited", false, 20, table.Seats, Version, ContentId, Event: title);
        _lobby = new LobbyHost(settings, _ => new DeckCheck(Array.Empty<Arcanum.Engine.Cards.CardDefinition>(), null, null));
        _lobby.Changed += () => Changed?.Invoke();
        if (!Listen(0))
        {
            _lobby = null;
            return false;
        }
        _lobby.AddListener(_tcp!);
        _lobby.AddListener(_loopback!);
        _lobby.ResumeEvent(host);
        _careerTable = new CareerTableSetup(table, null, DateTime.UtcNow);
        _unlisted = true;
        ListAddresses();
        OpenRouterPort();
        PublishHostedLobby();
        StartEventSession(new EventClient(_loopback!.Connect(), new ClientIdentity(name, token, Version, ContentId, OwnPlaymat), greet: true));
        return true;
    }

    /// <summary>Back into a career table's event on its second day, hosted by another player: found by its invite code, the seat by its token.</summary>
    public async void RejoinCareerTable(string name, string code, string token)
    {
        Leave();
        if (!InviteCode.TryParse(code, out var parsed)) return;
        try
        {
            await SignedInAsync();
            if (await Services.Lobbies.FindByCodeAsync(parsed) is not { } listing)
            {
                Status?.Invoke("The table's lobby isn't open (yet).");
                return;
            }
            _route = listing;
            _address = listing.Address ?? "";
            _port = listing.Port;
            var connection = await Services.Network.ConnectAsync(listing);
            StartEventSession(new EventClient(connection, new ClientIdentity(name, token, Version, ContentId, OwnPlaymat), greet: true));
        }
        catch (Exception e)
        {
            _route = null;
            Status?.Invoke($"Couldn't get back to the table: {e.Message}");
        }
    }

    /// <summary>The first day of the hosted table's two-day event, saved to go on the next day (null: none).</summary>
    public string? HostedTableSave => _careerTable is not null ? HostedEvent?.Save() : null;

    /// <summary>Every frame: starts the hosted table's event once its people are in (or the wait is over).</summary>
    private void PollCareerTable()
    {
        if (_careerTable is not { Build: { } build } setup || _lobby is not { Event: null, Game: null } lobby) return;
        var state = lobby.State;
        var present = state.Seats.Where(s => s.Kind == LobbySeatKind.Person && s.Connected).Select(s => s.Name).ToHashSet();
        bool everyone = setup.Table.People.All(p => present.Contains(p.Name));
        if (!everyone && DateTime.UtcNow - setup.Since < TableWait) return;
        foreach (var seat in Enumerable.Range(1, lobby.Settings.Seats - 1).Where(i => state.Seats[i].Kind == LobbySeatKind.Open))
            lobby.SetComputer(seat, $"Computer {seat}", "", "");
        // The event's seats in the table's order: its people (a missing one is played by the computer), then the computer's.
        var lobbySeats = lobby.Setup;
        var seats = new List<EventSeat>();
        var tokens = new List<string?>();
        foreach (var person in setup.Table.People)
        {
            var taken = lobbySeats.FirstOrDefault(s => !s.IsComputer && s.Name == person.Name);
            seats.Add(new EventSeat { Name = person.Name, IsHuman = taken is not null });
            tokens.Add(taken?.Token);
        }
        while (tokens.Count < setup.Table.Seats) tokens.Add(null);
        var ev = build(seats);
        var title = state.Event ?? "Event";
        lobby.StartEvent(_ => new EventHost(ev, tokens, Rules(ev), EventOptions() with { DayEndsAfter = setup.DayEndsAfter, NextDay = setup.NextDay }) { Title = title });
        UpdatePublishedLobby();
    }

    /// <summary>The hosted table's event, once it started (for its report to the career service).</summary>
    public LimitedEvent? HostedTableEvent => _careerTable is not null ? HostedEvent?.Event : null;

    private void DropCareerTable()
    {
        _careerTable = null;
        _unlisted = false;
    }
}
