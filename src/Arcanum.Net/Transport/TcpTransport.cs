// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Arcanum.Net.Transport;

/// <summary>
/// A connection over TCP. Each message is a frame: a 4-byte big-endian length (top bit set when the payload is
/// deflate-compressed) followed by UTF-8 text. Reading and writing run on background threads; the game polls.
/// </summary>
public sealed class TcpConnection : IConnection
{
    /// <summary>Larger frames are refused (a peer sending them is broken or hostile).</summary>
    public const int MaxFrame = 16 * 1024 * 1024;

    private const uint CompressedFlag = 0x8000_0000;
    private const int CompressAbove = 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ConcurrentQueue<string> _inbox = new();
    private readonly BlockingCollection<byte[]> _outbox = new();
    private volatile bool _open = true;

    public TcpConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        new Thread(ReadLoop) { IsBackground = true, Name = "net-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "net-write" }.Start();
    }

    /// <summary>Connects to a host; throws <see cref="SocketException"/> or <see cref="TimeoutException"/> on failure.</summary>
    public static async Task<TcpConnection> ConnectAsync(string host, int port, TimeSpan timeout)
    {
        var client = new TcpClient(AddressFamily.InterNetworkV6) { Client = { DualMode = true } };
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw new TimeoutException($"No answer from {host}:{port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new TcpConnection(client);
    }

    public bool IsOpen => _open;

    public void Send(string message)
    {
        if (!_open) return;
        try { _outbox.Add(Encode(message)); }
        catch (InvalidOperationException) { } // closed meanwhile
    }

    public bool TryReceive([NotNullWhen(true)] out string? message) => _inbox.TryDequeue(out message);

    public void Close()
    {
        if (!_open) return;
        _open = false;
        _outbox.CompleteAdding();
        try { _client.Client.Shutdown(SocketShutdown.Both); } catch { }
        _client.Close();
    }

    public void Dispose() => Close();

    internal static byte[] Encode(string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        uint flag = 0;
        if (payload.Length > CompressAbove)
        {
            using var buffer = new MemoryStream();
            using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(payload);
            payload = buffer.ToArray();
            flag = CompressedFlag;
        }
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length | flag);
        payload.CopyTo(frame, 4);
        return frame;
    }

    internal static string Decode(byte[] payload, bool compressed)
    {
        if (!compressed) return Encoding.UTF8.GetString(payload);
        using var input = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = input.Read(chunk)) > 0)
        {
            output.Write(chunk, 0, read);
            if (output.Length > MaxFrame) throw new InvalidDataException("Message too large.");
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private void ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (_open)
            {
                _stream.ReadExactly(header);
                uint word = BinaryPrimitives.ReadUInt32BigEndian(header);
                int length = (int)(word & ~CompressedFlag);
                if (length > MaxFrame) break;
                var payload = new byte[length];
                _stream.ReadExactly(payload);
                _inbox.Enqueue(Decode(payload, (word & CompressedFlag) != 0));
            }
        }
        catch (Exception) { } // closed by either side, or a broken frame
        Close();
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var frame in _outbox.GetConsumingEnumerable()) _stream.Write(frame);
        }
        catch (Exception) { }
        Close();
    }
}

/// <summary>Accepts players' TCP connections on a port (IPv4 and IPv6).</summary>
public sealed class TcpConnectionListener : IConnectionListener
{
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<IConnection> _pending = new();
    private volatile bool _open = true;

    public int Port { get; }

    /// <param name="port">0 picks a free port (see <see cref="Port"/>).</param>
    public TcpConnectionListener(int port)
    {
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        _listener.Server.DualMode = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" }.Start();
    }

    private void AcceptLoop()
    {
        try
        {
            while (_open) _pending.Enqueue(new TcpConnection(_listener.AcceptTcpClient()));
        }
        catch (Exception) { } // stopped
    }

    public bool TryAccept([NotNullWhen(true)] out IConnection? connection) => _pending.TryDequeue(out connection);

    public void Dispose()
    {
        _open = false;
        _listener.Stop();
        while (_pending.TryDequeue(out var c)) c.Close();
    }
}
