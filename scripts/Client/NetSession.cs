// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Arcanum.Net.Client;
using Arcanum.Net.Protocol;

namespace Arcanum.Client;

/// <summary>
/// This screen's seat in a game run by a host (another device, or this one through an in-memory connection).
/// Questions from the host become board decisions; views and events come from the host already filtered for this seat.
/// A new connection (after a drop) is attached with <see cref="Attach"/> without the board noticing.
/// </summary>
public sealed class NetSession : IBoardSession
{
    private GameClient _client;
    private readonly UiPlayerController _controller;
    private readonly Dictionary<PlayerId, SeatStatus> _seats = new();

    public DecisionHub Hub { get; } = new();
    public AutoPassPolicy Policy { get; }

    public NetSession(GameClient client, AutoPassPolicy policy, bool confirmManaPayment)
    {
        Policy = policy;
        _controller = new UiPlayerController(client.Seat, Hub, new DecisionLog(), policy, () => Presentation?.Invoke() ?? Task.CompletedTask)
        {
            ConfirmManaPayment = confirmManaPayment,
        };
        Hub.DecisionRequested += _ => Changed?.Invoke();
        _client = client;
        Wire(client);
    }

    public GameClient Client => _client;

    /// <summary>Continues on a new connection to the same seat (the host sends the current view and question again).</summary>
    public void Attach(GameClient client)
    {
        Unwire(_client);
        _client = client;
        Wire(client);
        ConnectionChanged?.Invoke(true);
    }

    private void Wire(GameClient client)
    {
        foreach (var status in client.Seats.Values) _seats[status.Seat] = status;
        client.ViewChanged += OnView;
        client.EventReceived += OnEvent;
        client.SeatChanged += OnSeat;
        client.AnswerRefused += OnRefused;
        client.Disconnected += OnDisconnected;
        client.Failed += OnFailed;
        client.Policy = Policy;
        client.Controller = _controller;
    }

    private void Unwire(GameClient client)
    {
        client.ViewChanged -= OnView;
        client.EventReceived -= OnEvent;
        client.SeatChanged -= OnSeat;
        client.AnswerRefused -= OnRefused;
        client.Disconnected -= OnDisconnected;
        client.Failed -= OnFailed;
        client.Policy = null;
        client.Controller = null;
    }

    private void OnView(GameView _) => Changed?.Invoke();
    private void OnEvent(EventView ev) => EventRaised?.Invoke(ev);
    private void OnRefused(string reason) => Notice?.Invoke(reason);
    private void OnFailed(Exception e) => Failed?.Invoke(e);
    private void OnDisconnected() => ConnectionChanged?.Invoke(false);

    private void OnSeat(SeatStatus status)
    {
        _seats[status.Seat] = status;
        SeatChanged?.Invoke(status);
    }

    public IReadOnlyDictionary<PlayerId, SeatStatus> Seats => _seats;

    public bool IsConnected => _client.IsConnected;

    /// <summary>Who plays each seat changed (someone dropped, the computer took over, they came back).</summary>
    public event Action<SeatStatus>? SeatChanged;

    /// <summary>A message for the player (an answer the host refused).</summary>
    public event Action<string>? Notice;

    /// <summary>The connection to the host was lost (false) or restored (true).</summary>
    public event Action<bool>? ConnectionChanged;

    public int PlayerCount => _client.Players.Count;
    public PlayerId LocalSeat => _client.Seat;
    public GameView ViewFor(PlayerId viewer) => _client.View!;
    public bool RevealAll => false;
    public Decision? CurrentDecision => Hub.Current is { IsAnswered: false } d ? d : null;
    public bool Announces(PlayerId player) => player != LocalSeat;
    public bool FollowsOthers => true;
    public bool IsReplaying => false;
    public bool CanUndo => false;
    public Func<Task>? Presentation { get; set; }

    public event Action? Changed;
    public event Action<Exception>? Failed;
    public event Action<EventView>? EventRaised;

    public void Start() => Changed?.Invoke();

    public void Poll() { } // the online service polls the connection, also between screens

    public void Leave() => App.Instance.Online.Leave();
}
