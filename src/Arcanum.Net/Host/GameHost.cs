// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Host;

/// <param name="IsComputer">Played by the computer from the start (no person will connect to it).</param>
/// <param name="Token">The secret the player uses to take the seat; null makes a new one.</param>
public sealed record HostSeat(string Name, IReadOnlyList<CardDefinition> Deck, IReadOnlyList<CardDefinition>? Commanders = null, bool IsComputer = false,
    string? Token = null);

/// <summary>One answer given during a game (real card ids), in order: replaying them rebuilds the game exactly.</summary>
public sealed record RecordedAnswer(int Seat, JsonElement Value);

public sealed record HostOptions
{
    /// <summary>How long a disconnected player's seat waits before the computer takes over; null waits for the host.</summary>
    public TimeSpan? Grace { get; init; } = TimeSpan.FromMinutes(2);

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>Optional pause before each visible computer action, so players can follow it.</summary>
    public Func<Task>? ComputerPace { get; init; }

    /// <summary>Must match the players' <see cref="Hello.Version"/> and <see cref="Hello.Content"/>.</summary>
    public string Version { get; init; } = "";
    public string Content { get; init; } = "";

    /// <summary>
    /// Every card name of that content, for choices of "a card name" (rule 201.3, <see cref="GameConfig.CardNames"/>); the games a
    /// lobby or an event starts use it. Null: only names the chooser knows of are offered.
    /// </summary>
    public IReadOnlyList<string>? CardNames { get; init; }

    /// <summary>The nonbasic land card names among them (<see cref="GameConfig.NonbasicLandNames"/>).</summary>
    public IReadOnlyList<string>? NonbasicLandNames { get; init; }

    /// <summary>The creature card names among them (<see cref="GameConfig.CreatureCardNames"/>).</summary>
    public IReadOnlyList<string>? CreatureCardNames { get; init; }

    /// <summary>
    /// How long a connected player may take over one decision before the computer makes it for them (they keep their
    /// seat); null for no limit. Priority passed by the player's own stops never waits.
    /// </summary>
    public TimeSpan? DecisionTime { get; init; }

    /// <summary>Refused answers in a row before a seat is handed to the computer (a broken or hostile client).</summary>
    public int MaxInvalidAnswers { get; init; } = 5;

    /// <summary>Answers of an earlier run of this game (same config and seats), to resume it where it stopped.</summary>
    public IReadOnlyList<RecordedAnswer>? Replay { get; init; }
}

/// <summary>
/// Runs a game for players connected over the network (the host's own player too, through an in-memory connection).
/// The host is the only one with the full game state: each player receives their own view and the events filtered for
/// them, with card ids of their own (see <see cref="CardAliases"/>), and every answer is checked before the game uses it.
/// A player who drops keeps their seat for <see cref="HostOptions.Grace"/>, then the computer plays it until they
/// reconnect. Everything happens on the thread that calls <see cref="Poll"/>.
/// </summary>
public sealed class GameHost
{
    private readonly List<Seat> _seats;
    private readonly List<IConnectionListener> _listeners = new();
    private readonly List<Peer> _greeting = new();
    private readonly Queue<RecordedAnswer> _replay;
    private readonly List<RecordedAnswer> _answers = new();
    private readonly WireFormat _store = new(); // real ids, for the record of answers
    private readonly IReadOnlyList<string> _names;
    private readonly bool _commander;

    internal HostOptions Options { get; }
    internal WireFormat Store => _store;

    public Game Game { get; }

    /// <summary>Every answer so far: save them with the game's setup to resume it later.</summary>
    public IReadOnlyList<RecordedAnswer> Answers => _answers;

    public Task? Running { get; private set; }

    public event Action<Exception>? Failed;

    /// <summary>A seat changed hands (connected, dropped, computer took over); for the host's screen.</summary>
    public event Action<PlayerId, SeatState>? SeatChanged;

    public GameHost(GameConfig config, IReadOnlyList<HostSeat> seats, HostOptions? options = null)
    {
        Options = options ?? new HostOptions();
        _replay = new Queue<RecordedAnswer>(Options.Replay ?? Array.Empty<RecordedAnswer>());
        _names = seats.Select(s => s.Name).ToList();
        _commander = config.Commander is not null;
        _seats = seats.Select((s, i) => new Seat(this, new PlayerId(i), s.IsComputer, s.Token)).ToList();
        Game = new Game(config, seats.Select((s, i) => new PlayerSetup(s.Name, _seats[i], s.Deck, s.Commanders)).ToList());
        foreach (var seat in _seats) seat.UseCardRules(Game);
        Game.EventRaised += OnEvent;
        Game.KnowledgeLost += ForgetHiddenLibraryCards;
    }

    /// <summary>The secret a player presents to take (or retake) this seat.</summary>
    public string TokenOf(PlayerId seat) => _seats[seat.Value].Token;

    public SeatState StateOf(PlayerId seat) => _seats[seat.Value].State;

    public void AddListener(IConnectionListener listener) => _listeners.Add(listener);

    /// <summary>A new connection; it must say <see cref="Hello"/> with a seat's token.</summary>
    /// <param name="received">Messages already read from it (its greeting, when another host routed it here).</param>
    public void Accept(IConnection connection, IEnumerable<NetMessage>? received = null)
    {
        var peer = new Peer(connection, new WireFormat(), Options.Clock);
        if (received is not null) peer.Hold(received);
        _greeting.Add(peer);
    }

    /// <summary>Whether <paramref name="token"/> belongs to a person's seat in this game.</summary>
    public bool HasToken(string token) => _seats.Any(s => !s.IsComputerSeat && Tokens.Same(s.Token, token));

    /// <summary>Closes every connection (the game is over and its players went back elsewhere).</summary>
    public void Close()
    {
        foreach (var peer in _greeting) peer.Close();
        _greeting.Clear();
        foreach (var seat in _seats) seat.Peer?.Close();
    }

    internal bool IsReplaying => _replay.Count > 0;

    public void Start()
    {
        if (Running is not null) return;
        Running = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await Game.RunAsync();
        }
        catch (Exception e)
        {
            Failed?.Invoke(e);
            throw;
        }
        finally
        {
            PushViews();
        }
    }

    /// <summary>The computer plays a disconnected player's seat now instead of waiting for the grace period to end.</summary>
    public bool ReplaceWithComputer(PlayerId seat) => _seats[seat.Value].TakeOver(onlyIfDisconnected: true);

    /// <summary>Handles connections and messages, timeouts, and sends players what changed. Call regularly (every frame).</summary>
    public void Poll()
    {
        foreach (var listener in _listeners)
            while (listener.TryAccept(out var connection)) Accept(connection);

        foreach (var peer in _greeting.ToList())
        {
            var messages = peer.Receive();
            if (messages.Count > 0)
            {
                peer.Hold(messages.Skip(1));
                if (messages[0] is Hello hello) Greet(peer, hello);
                else peer.Close();
            }
            if (!peer.IsOpen) _greeting.Remove(peer);
        }

        foreach (var seat in _seats) seat.Poll();
        PushViews();
    }

    private void Greet(Peer peer, Hello hello)
    {
        _greeting.Remove(peer);
        string? refusal =
            hello.Protocol != WireFormat.ProtocolVersion ? "This game uses a different version of the network protocol." :
            hello.Version != Options.Version ? $"Different game version (host: {Options.Version}, yours: {hello.Version})." :
            hello.Content != Options.Content ? "Different card content than the host: update your cards." :
            null;
        var seat = _seats.FirstOrDefault(s => !s.IsComputerSeat && Tokens.Same(s.Token, hello.Token));
        refusal ??= seat is null ? "No seat in this game for you." : null;
        if (refusal is not null)
        {
            peer.Send(new Rejected(refusal));
            peer.Close();
            return;
        }
        seat!.Connect(peer.Connection, peer.TakeHeld());
    }

    internal void SendWelcome(Seat seat)
    {
        seat.Peer!.Send(new Welcome(seat.Id, _names, _commander));
        foreach (var other in _seats) seat.Peer.Send(other.Status());
    }

    internal void Broadcast(NetMessage message)
    {
        foreach (var seat in _seats) seat.Peer?.Send(message);
    }

    internal void NotifySeatChanged(Seat seat)
    {
        Broadcast(seat.Status());
        SeatChanged?.Invoke(seat.Id, seat.State);
    }

    internal void MarkViewsDirty()
    {
        foreach (var seat in _seats) seat.ViewDirty = true;
    }

    internal void PushViews()
    {
        foreach (var seat in _seats) seat.PushView();
    }

    // ------------------------------------------------------------------ answers record

    internal bool TryReplay<T>(PlayerId seat, out T value)
    {
        value = default!;
        if (!_replay.TryPeek(out var next)) return false;
        if (next.Seat != seat.Value) throw new InvalidOperationException($"Saved game is out of order: expected an answer from seat {next.Seat}, asked seat {seat.Value}.");
        _replay.Dequeue();
        value = _store.FromElement<T>(next.Value)!;
        return true;
    }

    internal void Record<T>(PlayerId seat, T value) => _answers.Add(new RecordedAnswer(seat.Value, _store.ToElement(value)));

    // ------------------------------------------------------------------ events

    private void OnEvent(GameEvent e)
    {
        MarkViewsDirty();
        if (!IsReplaying)
        {
            int tax = Game.Config.Commander?.TaxPerCast ?? 0;
            foreach (var seat in _seats)
                seat.Peer?.Send(new Happened(EventViews.Build(Game.State, e, seat.Id, revealAll: false, tax)));
        }
        ForgetHiddenLibraryCards();
    }

    /// <summary>
    /// A card in a library keeps its id only for the players who know it there (see <see cref="Engine.State.Card.KnownTo"/>):
    /// for everyone else it becomes a new, unknown object, so no one can follow a card into a library, through a shuffle,
    /// or through an order they didn't see.
    /// </summary>
    private void ForgetHiddenLibraryCards()
    {
        foreach (var player in Game.State.Players)
            foreach (var id in player.Library)
            {
                var card = Game.State.GetCard(id);
                foreach (var seat in _seats)
                    if (!card.IsVisibleTo(seat.Id)) seat.Aliases.Forget(id.Value);
            }
    }
}
