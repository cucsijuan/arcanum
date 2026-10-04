// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Arcanum.Net.Transport;

/// <summary>
/// A reliable, ordered message channel to one peer. Receiving is polled from the game's thread, so the host and the
/// players never handle messages concurrently with the game.
/// </summary>
public interface IConnection : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Queues a message; silently dropped once the connection is closed.</summary>
    void Send(string message);

    bool TryReceive([NotNullWhen(true)] out string? message);

    void Close();
}

/// <summary>Where a host accepts players' connections.</summary>
public interface IConnectionListener : IDisposable
{
    bool TryAccept([NotNullWhen(true)] out IConnection? connection);
}

/// <summary>Both ends of a connection inside one process: the host's own player, and tests.</summary>
public sealed class InMemoryConnection : IConnection
{
    private readonly ConcurrentQueue<string> _inbox = new();
    private InMemoryConnection _other = null!;
    private volatile bool _open = true;

    private InMemoryConnection() { }

    public static (InMemoryConnection A, InMemoryConnection B) CreatePair()
    {
        var a = new InMemoryConnection();
        var b = new InMemoryConnection();
        a._other = b;
        b._other = a;
        return (a, b);
    }

    public bool IsOpen => _open;

    public void Send(string message)
    {
        if (_open) _other._inbox.Enqueue(message);
    }

    public bool TryReceive([NotNullWhen(true)] out string? message) => _inbox.TryDequeue(out message);

    /// <summary>Closes both ends, like a dropped network connection (messages already sent can still be read).</summary>
    public void Close()
    {
        _open = false;
        _other._open = false;
    }

    public void Dispose() => Close();
}

/// <summary>Hands out in-memory connections to a host.</summary>
public sealed class InMemoryListener : IConnectionListener
{
    private readonly ConcurrentQueue<IConnection> _pending = new();

    /// <summary>A new connection to the host; returns the player's end.</summary>
    public IConnection Connect()
    {
        var (player, host) = InMemoryConnection.CreatePair();
        _pending.Enqueue(host);
        return player;
    }

    public bool TryAccept([NotNullWhen(true)] out IConnection? connection) => _pending.TryDequeue(out connection);

    public void Dispose() { }
}
