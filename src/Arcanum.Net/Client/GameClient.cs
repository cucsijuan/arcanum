// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Client;

/// <param name="Token">The seat's secret, given by the host (the same one is used to reconnect).</param>
/// <param name="Playmat">The built-in playmat this player shows the others (see <see cref="Hello.Playmat"/>).</param>
public sealed record ClientIdentity(string Name, string Token, string Version, string Content, string? Playmat = null);

/// <summary>
/// A player's end of a hosted game: keeps the latest view, reports events, and passes the host's questions to a local
/// <see cref="IPlayerController"/> (the board, or the computer in tests), sending back its answers. Messages are
/// handled on the thread that calls <see cref="Poll"/>.
/// </summary>
public sealed class GameClient
{
    private readonly Peer _peer;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<PlayerId, SeatStatus> _seats = new();
    private readonly Queue<Ask> _unanswered = new();
    private IPlayerController? _controller;
    private AutoPassPolicy? _policy;
    private int _currentAsk;
    private bool _wasOpen = true;

    /// <param name="received">Messages already read from the connection (by the lobby).</param>
    public GameClient(IConnection connection, ClientIdentity identity, Func<DateTime>? clock = null, IEnumerable<NetMessage>? received = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _peer = new Peer(connection, new WireFormat(), _clock);
        if (received is not null) _peer.Hold(received);
        _peer.Send(new Hello(WireFormat.ProtocolVersion, identity.Version, identity.Content, identity.Name, identity.Token, identity.Playmat));
    }

    public PlayerId Seat { get; private set; }
    public bool IsWelcomed { get; private set; }
    public IReadOnlyList<string> Players { get; private set; } = Array.Empty<string>();

    /// <summary>The playmat each seat chose, as far as the host knows (see <see cref="SeatLooks"/>).</summary>
    public IReadOnlyList<string?> Playmats { get; private set; } = Array.Empty<string?>();
    public bool Commander { get; private set; }
    public GameView? View { get; private set; }
    public string? RejectedReason { get; private set; }
    public bool IsConnected => _peer.IsOpen;
    public IReadOnlyDictionary<PlayerId, SeatStatus> Seats => _seats;

    public event Action? Welcomed;
    public event Action<string>? Rejected;
    public event Action<GameView>? ViewChanged;
    public event Action<EventView>? EventReceived;
    public event Action<SeatStatus>? SeatChanged;
    /// <summary>The host refused the last answer and asks again (the reason is shown to the player).</summary>
    public event Action<string>? AnswerRefused;

    /// <summary>The player ran out of time on the current question: the computer answered it.</summary>
    public event Action? QuestionExpired;

    /// <summary>When the current question's time runs out (local clock), or null without a limit.</summary>
    public DateTime? QuestionDeadline { get; private set; }
    public event Action? Disconnected;
    public event Action<Exception>? Failed;

    /// <summary>Answers the host's questions; questions wait until one is set.</summary>
    public IPlayerController? Controller
    {
        get => _controller;
        set
        {
            _controller = value;
            while (_controller is not null && _unanswered.TryDequeue(out var ask)) Answer(ask);
        }
    }

    /// <summary>The player's priority stops, kept in sync with the host (which passes priority for them).</summary>
    public AutoPassPolicy? Policy
    {
        get => _policy;
        set
        {
            if (_policy is not null) _policy.Changed -= SendStops;
            _policy = value;
            if (_policy is not null) _policy.Changed += SendStops;
            SendStops();
        }
    }

    private void SendStops()
    {
        if (_policy is null || !IsWelcomed) return;
        _peer.Send(new Stops(_policy.OwnTurnStops.ToList(), _policy.OpponentTurnStops.ToList(), _policy.FullControl, _policy.PassingTurn));
    }

    public void Poll()
    {
        foreach (var message in _peer.Receive())
        {
            switch (message)
            {
                case Welcome w:
                    Seat = w.Seat;
                    Players = w.Players;
                    Commander = w.Commander;
                    IsWelcomed = true;
                    SendStops();
                    Welcomed?.Invoke();
                    break;
                case SeatLooks looks:
                    Playmats = looks.Playmats;
                    break;
                case Protocol.Rejected r:
                    RejectedReason = r.Reason;
                    Rejected?.Invoke(r.Reason);
                    break;
                case ViewUpdate v:
                    View = v.View;
                    ViewChanged?.Invoke(v.View);
                    break;
                case Happened h:
                    EventReceived?.Invoke(h.Event);
                    break;
                case SeatStatus s:
                    _seats[s.Seat] = s;
                    SeatChanged?.Invoke(s);
                    break;
                case AskExpired expired when expired.Ask == _currentAsk:
                    _currentAsk = -1;
                    QuestionDeadline = null;
                    QuestionExpired?.Invoke();
                    break;
                case Ask ask:
                    _currentAsk = ask.Id;
                    QuestionDeadline = ask.SecondsLeft >= 0 ? _clock() + TimeSpan.FromSeconds(ask.SecondsLeft) : null;
                    if (ask.Error is not null) AnswerRefused?.Invoke(ask.Error);
                    if (_controller is null) _unanswered.Enqueue(ask);
                    else Answer(ask);
                    break;
            }
        }
        if (_wasOpen && !_peer.IsOpen)
        {
            _wasOpen = false;
            Disconnected?.Invoke();
        }
    }

    /// <summary>Leaves the game (the seat waits for a reconnection, then the computer takes over).</summary>
    public void Close() => _peer.Close();

    private async void Answer(Ask ask)
    {
        try
        {
            var c = _controller!;
            var view = View ?? throw new InvalidOperationException("Question before any view.");
            switch (ask.Question)
            {
                case KeepHandQuestion q: Reply(ask, await c.KeepHandAsync(view, q.MulligansTaken)); break;
                case BottomQuestion q: Reply(ask, await c.ChooseCardsToBottomAsync(view, q.Count)); break;
                case ActionQuestion q: Reply(ask, await c.ChooseActionAsync(view, q.Legal)); break;
                case ManaQuestion q: Reply(ask, await c.ChooseManaPaymentAsync(view, q.Request)); break;
                case TargetsQuestion q: Reply(ask, await c.ChooseTargetsAsync(view, q.Request)); break;
                case AttackQuestion q: Reply(ask, await c.DeclareAttackersAsync(view, q.PossibleAttackers, q.Defenders)); break;
                case BlockQuestion q: Reply(ask, await c.DeclareBlockersAsync(view, q.Request)); break;
                case DamageQuestion q: Reply(ask, await c.AssignCombatDamageAsync(view, q.Request)); break;
                case YesNoQuestion q: Reply(ask, await c.ChooseYesNoAsync(view, q.Request)); break;
                case DiscardQuestion q: Reply(ask, await c.ChooseDiscardAsync(view, q.Count)); break;
                case CardsQuestion q: Reply(ask, await c.ChooseCardsAsync(view, q.Request)); break;
                case ModesQuestion q: Reply(ask, await c.ChooseModesAsync(view, q.Request)); break;
                case NumberQuestion q: Reply(ask, await c.ChooseNumberAsync(view, q.Request)); break;
                case OptionQuestion q: Reply(ask, await c.ChooseOptionAsync(view, q.Request)); break;
            }
        }
        catch (Exception e)
        {
            Failed?.Invoke(e);
        }
    }

    private void Reply<T>(Ask ask, T value)
    {
        if (ask.Id != _currentAsk) return; // a newer question replaced this one
        _peer.Send(new Answer(ask.Id, _peer.Format.ToElement(value)));
    }
}
