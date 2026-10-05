// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// A packet service between peers with reliable, ordered delivery and a small maximum packet size: what relay and NAT
/// traversal services offer. Implementations are thread-safe.
/// </summary>
public interface IPacketLink
{
    /// <summary>The largest packet <see cref="Send"/> accepts, in bytes.</summary>
    int MaxPacketSize { get; }

    void Send(string peer, byte[] packet);

    bool TryReceive([NotNullWhen(true)] out string? peer, [NotNullWhen(true)] out byte[]? packet);

    /// <summary>A peer the service lost (it closed, timed out or was unreachable): its connections are closed.</summary>
    bool TryTakeClosedPeer([NotNullWhen(true)] out string? peer);
}

/// <summary>
/// Message connections carried over an <see cref="IPacketLink"/>: any number of connections between two peers, messages
/// split into packets and joined again, an opening handshake and closing. A host listens; players connect to the host's
/// peer id.
/// </summary>
public sealed class PacketRelay
{
    private enum Kind : byte { Open = 1, Accept = 2, Data = 3, DataMore = 4, Close = 5 }

    private const int HeaderSize = 6;

    private readonly IPacketLink _link;
    private readonly object _gate = new();
    private readonly Dictionary<(string Peer, int Id, bool Mine), RelayConnection> _connections = new();
    private Listener? _listener;

    public PacketRelay(IPacketLink link)
    {
        if (link.MaxPacketSize <= HeaderSize) throw new ArgumentException("Packets are too small.", nameof(link));
        _link = link;
    }

    /// <summary>Accepts connections from any peer (one listener; listening again returns it).</summary>
    public IConnectionListener Listen()
    {
        lock (_gate) return _listener ??= new Listener(this);
    }

    /// <summary>Opens a connection to a listening peer.</summary>
    public async Task<IConnection> ConnectAsync(string peer, TimeSpan timeout, CancellationToken cancel = default)
    {
        RelayConnection connection;
        lock (_gate)
        {
            int id;
            do id = Random.Shared.Next(1, int.MaxValue); while (_connections.ContainsKey((peer, id, true)));
            connection = new RelayConnection(this, peer, id, mine: true);
            _connections[(peer, id, true)] = connection;
        }
        SendControl(Kind.Open, connection);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Pump();
            if (connection.Accepted) return connection;
            if (!connection.IsOpen) throw new IOException("The host refused the connection.");
            if (DateTime.UtcNow > deadline)
            {
                connection.Close();
                throw new TimeoutException("The host didn't answer.");
            }
            await Task.Delay(20, cancel);
        }
    }

    /// <summary>Routes every packet received so far to its connection.</summary>
    public void Pump()
    {
        lock (_gate)
        {
            while (_link.TryTakeClosedPeer(out var lost))
                foreach (var connection in _connections.Values.Where(c => c.Peer == lost).ToList()) connection.Drop();
            while (_link.TryReceive(out var peer, out var packet))
            {
                if (packet.Length < HeaderSize) continue;
                var kind = (Kind)packet[0];
                bool fromInitiator = packet[1] == 0;
                int id = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(2));
                // A connection is "mine" when this side opened it, i.e. the packet comes from the other end (not the initiator).
                var key = (peer, id, !fromInitiator);
                _connections.TryGetValue(key, out var connection);
                switch (kind)
                {
                    case Kind.Open when fromInitiator && connection is null:
                        if (_listener is null)
                        {
                            Send(peer, Kind.Close, fromInitiator: false, id, ReadOnlySpan<byte>.Empty);
                            break;
                        }
                        connection = new RelayConnection(this, peer, id, mine: false) { Accepted = true };
                        _connections[key] = connection;
                        _listener.Pending.Enqueue(connection);
                        SendControl(Kind.Accept, connection);
                        break;
                    case Kind.Accept when connection is not null:
                        connection.Accepted = true;
                        break;
                    case Kind.Data or Kind.DataMore when connection is not null:
                        connection.Receive(packet.AsSpan(HeaderSize), more: kind == Kind.DataMore);
                        break;
                    case Kind.Close when connection is not null:
                        connection.Drop();
                        break;
                }
            }
        }
    }

    private void SendControl(Kind kind, RelayConnection connection) =>
        Send(connection.Peer, kind, fromInitiator: connection.Mine, connection.Id, ReadOnlySpan<byte>.Empty);

    private void Send(string peer, Kind kind, bool fromInitiator, int id, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = (byte)kind;
        packet[1] = fromInitiator ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(2), id);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        _link.Send(peer, packet);
    }

    private void SendMessage(RelayConnection connection, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        int chunk = _link.MaxPacketSize - HeaderSize;
        int offset = 0;
        do
        {
            int length = Math.Min(chunk, bytes.Length - offset);
            bool more = offset + length < bytes.Length;
            Send(connection.Peer, more ? Kind.DataMore : Kind.Data, connection.Mine, connection.Id, bytes.AsSpan(offset, length));
            offset += length;
        } while (offset < bytes.Length);
    }

    private void Forget(RelayConnection connection)
    {
        lock (_gate) _connections.Remove((connection.Peer, connection.Id, connection.Mine));
    }

    private sealed class Listener(PacketRelay relay) : IConnectionListener
    {
        public readonly ConcurrentQueue<RelayConnection> Pending = new();

        public bool TryAccept([NotNullWhen(true)] out IConnection? connection)
        {
            relay.Pump();
            if (Pending.TryDequeue(out var next))
            {
                connection = next;
                return true;
            }
            connection = null;
            return false;
        }

        public void Dispose()
        {
            lock (relay._gate) relay._listener = null;
        }
    }

    private sealed class RelayConnection(PacketRelay relay, string peer, int id, bool mine) : IConnection
    {
        private readonly ConcurrentQueue<string> _inbox = new();
        private readonly MemoryStream _partial = new();
        private volatile bool _open = true;

        public string Peer { get; } = peer;
        public int Id { get; } = id;
        public bool Mine { get; } = mine;
        public volatile bool Accepted;

        public bool IsOpen
        {
            get
            {
                if (_open) relay.Pump();
                return _open;
            }
        }

        public void Send(string message)
        {
            if (_open) relay.SendMessage(this, message);
        }

        public bool TryReceive([NotNullWhen(true)] out string? message)
        {
            relay.Pump();
            return _inbox.TryDequeue(out message);
        }

        /// <summary>Called with the relay's lock held.</summary>
        public void Receive(ReadOnlySpan<byte> chunk, bool more)
        {
            _partial.Write(chunk);
            if (more) return;
            _inbox.Enqueue(Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length));
            _partial.SetLength(0);
        }

        /// <summary>The other end closed or was lost: messages already received can still be read.</summary>
        public void Drop()
        {
            _open = false;
            relay.Forget(this);
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            relay.SendControl(Kind.Close, this);
            relay.Forget(this);
        }

        public void Dispose() => Close();
    }
}
