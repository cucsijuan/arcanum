// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Client;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Lobby;

/// <summary>
/// A player in a host's lobby: takes a seat, sends their deck and waits for the host to start. When the game starts,
/// <see cref="JoinGame"/> continues on the same connection as a <see cref="GameClient"/>.
/// </summary>
public sealed class LobbyClient
{
    private readonly IConnection _connection;
    private readonly Peer _peer;
    private readonly Func<DateTime> _clock;
    private bool _wasOpen = true;

    public LobbyClient(IConnection connection, ClientIdentity identity, Func<DateTime>? clock = null)
    {
        _connection = connection;
        _clock = clock ?? (() => DateTime.UtcNow);
        _peer = new Peer(connection, new WireFormat(), _clock);
        Identity = identity;
        _peer.Send(new Hello(WireFormat.ProtocolVersion, identity.Version, identity.Content, identity.Name, identity.Token));
    }

    /// <summary>Who this player is; the token is the seat's once joined.</summary>
    public ClientIdentity Identity { get; private set; }

    public int Seat { get; private set; } = -1;
    public LobbyState? State { get; private set; }
    public string? RejectedReason { get; private set; }
    public bool Started { get; private set; }
    public bool IsConnected => _peer.IsOpen;

    public event Action? Changed;
    public event Action<string>? Rejected;
    public event Action? GameStarting;
    public event Action? Disconnected;

    public void SubmitDeck(string name, string list) => _peer.Send(new SubmitDeck(name, list));

    public void Poll()
    {
        if (Started) return;
        foreach (var message in _peer.Receive())
        {
            switch (message)
            {
                case Joined j:
                    Seat = j.Seat;
                    Identity = Identity with { Token = j.Token };
                    Changed?.Invoke();
                    break;
                case LobbyState s:
                    State = s;
                    Changed?.Invoke();
                    break;
                case Protocol.Rejected r:
                    RejectedReason = r.Reason;
                    Rejected?.Invoke(r.Reason);
                    break;
                case Protocol.GameStarting:
                    Started = true;
                    GameStarting?.Invoke();
                    return; // the rest belongs to the game
            }
        }
        if (_wasOpen && !_peer.IsOpen)
        {
            _wasOpen = false;
            Disconnected?.Invoke();
        }
    }

    /// <summary>After <see cref="GameStarting"/>: the game client for this seat, on the same connection.</summary>
    public GameClient JoinGame() => new(_connection, Identity, _clock);

    public void Close() => _peer.Close();
}
