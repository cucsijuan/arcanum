// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Protocol;

/// <summary>
/// One end of a game connection: encodes messages with its <see cref="WireFormat"/>, sends heartbeats and notices
/// a peer that went silent (a dropped network may never close the socket).
/// </summary>
public sealed class Peer
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(10);

    private readonly IConnection _connection;
    private readonly Func<DateTime> _clock;
    private DateTime _lastReceived;
    private DateTime _lastSent;
    private readonly List<NetMessage> _held = new();

    public WireFormat Format { get; }

    public Peer(IConnection connection, WireFormat format, Func<DateTime> clock)
    {
        _connection = connection;
        Format = format;
        _clock = clock;
        _lastReceived = _lastSent = clock();
    }

    public bool IsOpen => _connection.IsOpen;

    public IConnection Connection => _connection;

    public void Send(NetMessage message) => SendRaw(Format.Write(message));

    /// <summary>Sends an already encoded message (encoded with this peer's format).</summary>
    public void SendRaw(string json)
    {
        _connection.Send(json);
        _lastSent = _clock();
    }

    /// <summary>Puts messages back, to be returned first by the next <see cref="Receive"/> (read too early, e.g. right after a greeting).</summary>
    public void Hold(IEnumerable<NetMessage> messages) => _held.AddRange(messages);

    /// <summary>Messages put back with <see cref="Hold"/>, taken out (to hand them to whoever handles this connection next).</summary>
    public List<NetMessage> TakeHeld()
    {
        var held = _held.ToList();
        _held.Clear();
        return held;
    }

    /// <summary>Messages received since the last call. Closes the connection on silence.</summary>
    public List<NetMessage> Receive()
    {
        var received = TakeHeld();
        var now = _clock();
        while (_connection.TryReceive(out var json))
        {
            _lastReceived = now;
            try
            {
                var message = Format.Read(json);
                if (message is not Heartbeat) received.Add(message);
            }
            catch (JsonException)
            {
                received.Add(new Malformed(json));
            }
        }
        if (!_connection.IsOpen) return received;
        if (now - _lastReceived > SilenceTimeout) _connection.Close();
        else if (now - _lastSent >= HeartbeatInterval) Send(new Heartbeat());
        return received;
    }

    public void Close() => _connection.Close();

    /// <summary>A message that couldn't be read (stale card id, newer protocol...), kept so the receiver can react.</summary>
    public sealed record Malformed(string Json) : NetMessage;
}
