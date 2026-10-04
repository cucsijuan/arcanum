// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Net.Host;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Lobby;

/// <summary>A deck list read by the host: the cards, or why it can't be played in this game.</summary>
public sealed record DeckCheck(IReadOnlyList<CardDefinition> Deck, IReadOnlyList<CardDefinition>? Commanders, string? Problem);

/// <param name="Format">Name of the format, shown to the players.</param>
/// <param name="Seats">Number of players, the host included.</param>
public sealed record LobbySettings(string Format, bool Commander, int StartingLife, int Seats, string Version, string Content);

/// <summary>
/// Gathers the players of a hosted game: people take free seats (the host's own player takes seat 0 with
/// <see cref="HostToken"/>), bring a deck the host checks, and the host fills the other seats with the computer.
/// <see cref="Start"/> hands every connection to a <see cref="GameHost"/>; from then on <see cref="Poll"/> runs the game.
/// </summary>
public sealed class LobbyHost
{
    private sealed class Slot
    {
        public LobbySeatKind Kind;
        public string Name = "";
        public string Token = Tokens.New();
        public Peer? Peer;
        public string? DeckName;
        public DeckCheck? Deck;
    }

    private readonly LobbySettings _settings;
    private readonly Func<string, DeckCheck> _checkDeck;
    private readonly Func<DateTime> _clock;
    private readonly List<Slot> _slots;
    private readonly List<IConnectionListener> _listeners = new();
    private readonly List<Peer> _greeting = new();

    public LobbyHost(LobbySettings settings, Func<string, DeckCheck> checkDeck, Func<DateTime>? clock = null)
    {
        _settings = settings;
        _checkDeck = checkDeck;
        _clock = clock ?? (() => DateTime.UtcNow);
        _slots = Enumerable.Range(0, settings.Seats).Select(_ => new Slot()).ToList();
        _slots[0].Kind = LobbySeatKind.Person; // kept for the host's own player
    }

    public LobbySettings Settings => _settings;

    /// <summary>The token of seat 0, for the host's own player.</summary>
    public string HostToken => _slots[0].Token;

    /// <summary>The game, once started.</summary>
    public GameHost? Game { get; private set; }

    /// <summary>The lobby changed (someone joined, left, chose a deck).</summary>
    public event Action? Changed;

    public LobbyState State => new(_settings.Format, _settings.Commander, _slots.Select(s => new LobbySeat(
        s.Name, s.Kind, s.Kind == LobbySeatKind.Computer || s.Peer is not null, s.DeckName,
        s.Kind == LobbySeatKind.Open ? null : s.Deck is null ? "No deck chosen yet." : s.Deck.Problem)).ToList());

    public void AddListener(IConnectionListener listener)
    {
        _listeners.Add(listener);
        Game?.AddListener(listener);
    }

    /// <summary>The computer plays <paramref name="seat"/> with this deck list.</summary>
    public void SetComputer(int seat, string name, string deckName, string list)
    {
        if (seat == 0 || Game is not null) return;
        var slot = _slots[seat];
        slot.Peer?.Send(new Rejected("The host gave your seat to the computer."));
        slot.Peer?.Close();
        slot.Peer = null;
        slot.Kind = LobbySeatKind.Computer;
        slot.Name = name;
        slot.Token = Tokens.New();
        slot.DeckName = deckName;
        slot.Deck = _checkDeck(list);
        Broadcast();
    }

    /// <summary>Frees a seat (removes the computer, or the person sitting there).</summary>
    public void Open(int seat)
    {
        if (seat == 0 || Game is not null) return;
        var slot = _slots[seat];
        slot.Peer?.Send(new Rejected("The host freed your seat."));
        slot.Peer?.Close();
        _slots[seat] = new Slot();
        Broadcast();
    }

    /// <summary>Why the game can't start yet, or null when it can.</summary>
    public string? StartProblem
    {
        get
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                string who = s.Name.Length > 0 ? s.Name : $"Seat {i + 1}";
                if (s.Kind == LobbySeatKind.Open) return $"Seat {i + 1} is free: wait for a player or add the computer.";
                if (s.Kind == LobbySeatKind.Person && s.Peer is null) return $"{who} isn't connected.";
                if (s.Deck is null) return $"{who} hasn't chosen a deck.";
                if (s.Deck.Problem is { } problem) return $"{who}: {problem}";
            }
            return null;
        }
    }

    /// <summary>Starts the game with every seat as it is now (check <see cref="StartProblem"/> first).</summary>
    public GameHost Start(ulong seed, HostOptions options)
    {
        if (StartProblem is { } problem) throw new InvalidOperationException(problem);
        var config = new GameConfig
        {
            Seed = seed,
            StartingLife = _settings.StartingLife,
            Commander = _settings.Commander ? new CommanderRules() : null,
        };
        var seats = _slots.Select(s => new HostSeat(s.Name, s.Deck!.Deck, s.Deck.Commanders, s.Kind == LobbySeatKind.Computer,
            s.Kind == LobbySeatKind.Person ? s.Token : null)).ToList();
        Game = new GameHost(config, seats, options with { Version = _settings.Version, Content = _settings.Content });
        foreach (var listener in _listeners) Game.AddListener(listener);
        foreach (var slot in _slots.Where(s => s.Peer is not null))
        {
            slot.Peer!.Send(new GameStarting());
            Game.Accept(slot.Peer.Connection); // the player now greets the game with their token
        }
        foreach (var peer in _greeting) peer.Close();
        _greeting.Clear();
        Game.Start();
        return Game;
    }

    public void Poll()
    {
        if (Game is not null)
        {
            Game.Poll();
            return;
        }
        foreach (var listener in _listeners)
            while (listener.TryAccept(out var connection)) _greeting.Add(new Peer(connection, new WireFormat(), _clock));

        foreach (var peer in _greeting.ToList())
        {
            var hello = peer.Receive().FirstOrDefault();
            if (hello is not null)
            {
                _greeting.Remove(peer);
                if (hello is Hello h) Join(peer, h);
                else peer.Close();
            }
            else if (!peer.IsOpen) _greeting.Remove(peer);
        }

        bool changed = false;
        foreach (var slot in _slots.Where(s => s.Peer is not null))
        {
            foreach (var message in slot.Peer!.Receive())
            {
                if (message is SubmitDeck deck)
                {
                    slot.DeckName = deck.Name;
                    slot.Deck = _checkDeck(deck.List);
                    changed = true;
                }
            }
            if (!slot.Peer.IsOpen)
            {
                slot.Peer = null;
                changed = true;
            }
        }
        if (changed) Broadcast();
    }

    private void Join(Peer peer, Hello hello)
    {
        string? refusal =
            hello.Protocol != WireFormat.ProtocolVersion ? "This game uses a different version of the network protocol." :
            hello.Version != _settings.Version ? $"Different game version (host: {_settings.Version}, yours: {hello.Version})." :
            hello.Content != _settings.Content ? "Different card content than the host: update your cards." :
            null;
        // A token takes back a seat (rejoining, or the host's own player); without one, the first free seat.
        var slot = hello.Token.Length > 0
            ? _slots.FirstOrDefault(s => s.Kind == LobbySeatKind.Person && Tokens.Same(s.Token, hello.Token))
            : _slots.FirstOrDefault(s => s.Kind == LobbySeatKind.Open);
        refusal ??= slot is null ? (hello.Token.Length > 0 ? "Your seat is no longer in this game." : "The game is full.") : null;
        if (refusal is not null)
        {
            peer.Send(new Rejected(refusal));
            peer.Close();
            return;
        }
        slot!.Peer?.Close();
        slot.Peer = peer;
        slot.Kind = LobbySeatKind.Person;
        slot.Name = hello.Name.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 32)] : $"Player {_slots.IndexOf(slot) + 1}";
        peer.Send(new Joined(_slots.IndexOf(slot), slot.Token));
        Broadcast();
    }

    private void Broadcast()
    {
        var state = State;
        foreach (var slot in _slots) slot.Peer?.Send(state);
        Changed?.Invoke();
    }
}
