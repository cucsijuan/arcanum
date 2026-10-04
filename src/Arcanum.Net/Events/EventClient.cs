// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Client;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Events;

/// <summary>
/// A player's end of a limited event: the latest <see cref="EventInfo"/>, and the player's actions (picks, deck,
/// ready). The games themselves are played on separate connections with <see cref="EventInfo.GameToken"/>.
/// </summary>
public sealed class EventClient
{
    private readonly Peer _peer;
    private bool _wasOpen = true;

    /// <param name="greet">Say hello with the seat's token (reconnecting); false when the lobby handed the connection over.</param>
    /// <param name="received">Messages already read from the connection (by the lobby).</param>
    public EventClient(IConnection connection, ClientIdentity identity, bool greet, Func<DateTime>? clock = null, IEnumerable<NetMessage>? received = null)
    {
        _peer = new Peer(connection, new WireFormat(), clock ?? (() => DateTime.UtcNow));
        if (received is not null) _peer.Hold(received);
        Identity = identity;
        if (greet) _peer.Send(new Hello(WireFormat.ProtocolVersion, identity.Version, identity.Content, identity.Name, identity.Token));
    }

    public ClientIdentity Identity { get; }
    public EventInfo? Event { get; private set; }
    public string? RejectedReason { get; private set; }
    public bool IsConnected => _peer.IsOpen;

    public event Action<EventInfo>? Changed;
    public event Action<string>? Rejected;
    public event Action? Disconnected;

    /// <summary>Takes the card at <paramref name="index"/> of the current booster.</summary>
    public void Pick(int index)
    {
        if (Event?.Draft is { } d) _peer.Send(new DraftPick(d.Round, d.Pick, index));
    }

    public void SubmitDeck(string name, string list) => _peer.Send(new SubmitDeck(name, list));

    /// <summary>Deck done, or ready for the next game.</summary>
    public void Ready() => _peer.Send(new EventReady());

    public void Poll()
    {
        foreach (var message in _peer.Receive())
        {
            switch (message)
            {
                case EventUpdate update:
                    Event = update.Event;
                    Changed?.Invoke(update.Event);
                    break;
                case Protocol.Rejected r:
                    RejectedReason = r.Reason;
                    Rejected?.Invoke(r.Reason);
                    break;
            }
        }
        if (_wasOpen && !_peer.IsOpen)
        {
            _wasOpen = false;
            Disconnected?.Invoke();
        }
    }

    public void Close() => _peer.Close();
}
