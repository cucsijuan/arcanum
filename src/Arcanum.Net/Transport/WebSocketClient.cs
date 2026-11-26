// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Arcanum.Net.Transport;

/// <summary>A whole WebSocket message: text (UTF-8) or binary.</summary>
public readonly record struct WebSocketMessage(bool Text, byte[] Data)
{
    public string AsText() => Encoding.UTF8.GetString(Data);
}

/// <summary>The server refused the upgrade to a WebSocket (its HTTP status: 401 no identity, 404 not found, 429 full…).</summary>
public sealed class WebSocketRefusedException(int status, string line) : IOException($"The server refused: {line}")
{
    public int Status { get; } = status;
}

/// <summary>
/// The client side of a WebSocket (RFC 6455) over a TCP connection, with TLS for <c>wss</c>. Written here because some
/// systems (a console) have no WebSocket in their runtime, while TCP and TLS work. Like <see cref="TcpConnection"/>,
/// reading and writing run on background threads with blocking calls and the game polls. Pings are answered; a
/// connection silent for longer than <see cref="SilenceLimit"/> (the server pings every 30 seconds) is closed.
/// </summary>
public sealed class WebSocketClient : IDisposable
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const byte Continuation = 0, TextFrame = 1, BinaryFrame = 2, CloseFrame = 8, PingFrame = 9, PongFrame = 10;

    /// <summary>Larger messages from the server close the connection.</summary>
    public const int MaxMessage = 4 * 1024 * 1024;

    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(75);

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly ConcurrentQueue<WebSocketMessage> _inbox = new();
    private readonly BlockingCollection<byte[]> _outbox = new();
    private volatile bool _open = true;
    private bool _closeSent;
    private long _lastHeard = Environment.TickCount64;

    private WebSocketClient(TcpClient client, Stream stream)
    {
        _client = client;
        _stream = stream;
        new Thread(ReadLoop) { IsBackground = true, Name = "ws-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "ws-write" }.Start();
    }

    public bool IsOpen => _open;

    /// <summary>Whether received messages wait to be read (they can be read after the connection closed).</summary>
    public bool HasPending => !_inbox.IsEmpty;

    /// <summary>The server's closing status and reason, once it closed.</summary>
    public (int Status, string Reason)? Closed { get; private set; }

    /// <summary>Opens a WebSocket to a <c>ws</c> or <c>wss</c> address, sending the given headers with the upgrade.</summary>
    public static async Task<WebSocketClient> ConnectAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout,
        CancellationToken cancel = default)
    {
        bool secure = uri.Scheme switch
        {
            "wss" => true,
            "ws" => false,
            _ => throw new ArgumentException("Not a WebSocket address.", nameof(uri)),
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        var client = TcpConnection.NewClient();
        try
        {
            try { await client.ConnectAsync(uri.Host, uri.Port, limit.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TimeoutException($"No answer from {uri.Host}.");
            }
            if (TcpConnection.UseNoDelay) client.NoDelay = true;
            Stream stream = client.GetStream();
            if (secure)
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, limit.Token);
                stream = ssl;
            }
            await HandshakeAsync(stream, uri, headers, limit.Token);
            return new WebSocketClient(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task HandshakeAsync(Stream stream, Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancel)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = new StringBuilder()
            .Append($"GET {uri.PathAndQuery} HTTP/1.1\r\n")
            .Append($"Host: {(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")}\r\n")
            .Append("Upgrade: websocket\r\nConnection: Upgrade\r\n")
            .Append($"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (name.AsSpan().IndexOfAny("\r\n:") >= 0 || value.AsSpan().IndexOfAny("\r\n") >= 0)
                throw new ArgumentException($"Bad header {name}.", nameof(headers));
            request.Append($"{name}: {value}\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancel);
        await stream.FlushAsync(cancel);

        // The answer's head, up to the empty line; the frames after it are read by the read loop.
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            if (head.Count > 16 * 1024) throw new IOException("The server's answer is too long.");
            if (await stream.ReadAsync(one, cancel) == 0) throw new IOException("The server closed the connection.");
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var status = lines[0].Split(' ', 3);
        if (status.Length < 2 || !int.TryParse(status[1], out int code)) throw new IOException("Not an HTTP answer.");
        if (code != 101) throw new WebSocketRefusedException(code, lines[0]);
        string expected = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
        bool accepted = lines.Skip(1).Select(l => l.Split(':', 2)).Any(p =>
            p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase) && p[1].Trim() == expected);
        if (!accepted) throw new IOException("The server's answer isn't a WebSocket upgrade.");
    }

    public void SendText(string text) => Queue(TextFrame, Encoding.UTF8.GetBytes(text));

    public void SendBinary(ReadOnlySpan<byte> data) => Queue(BinaryFrame, data);

    public bool TryReceive([NotNullWhen(true)] out WebSocketMessage? message)
    {
        if (_inbox.TryDequeue(out var next))
        {
            message = next;
            return true;
        }
        message = null;
        return false;
    }

    /// <summary>Closes with a closing message (normal closure); messages already received can still be read.</summary>
    public void Close()
    {
        if (!_open) return;
        SendClose(1000);
        _outbox.CompleteAdding();
        _open = false;
        // The writer sends the closing message, then ends the connection.
    }

    public void Dispose() => Close();

    private void SendClose(int status)
    {
        lock (_outbox)
        {
            if (_closeSent) return;
            _closeSent = true;
        }
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
        Queue(CloseFrame, payload);
    }

    private void Queue(byte opcode, ReadOnlySpan<byte> payload)
    {
        if (_outbox.IsAddingCompleted) return;
        try { _outbox.Add(Frame(opcode, payload)); }
        catch (InvalidOperationException) { } // closed meanwhile
    }

    /// <summary>One final frame, masked as a client's frames must be.</summary>
    internal static byte[] Frame(byte opcode, ReadOnlySpan<byte> payload)
    {
        int lengthBytes = payload.Length < 126 ? 0 : payload.Length <= ushort.MaxValue ? 2 : 8;
        var frame = new byte[2 + lengthBytes + 4 + payload.Length];
        frame[0] = (byte)(0x80 | opcode);
        if (lengthBytes == 0) frame[1] = (byte)(0x80 | payload.Length);
        else if (lengthBytes == 2)
        {
            frame[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
        }
        var mask = frame.AsSpan(2 + lengthBytes, 4);
        RandomNumberGenerator.Fill(mask);
        var body = frame.AsSpan(6 + lengthBytes);
        for (int i = 0; i < payload.Length; i++) body[i] = (byte)(payload[i] ^ mask[i & 3]);
        return frame;
    }

    private void ReadLoop()
    {
        var header = new byte[8];
        var message = new MemoryStream();
        bool messageText = false;
        try
        {
            while (true)
            {
                _stream.ReadExactly(header.AsSpan(0, 2));
                bool fin = (header[0] & 0x80) != 0;
                byte opcode = (byte)(header[0] & 0x0F);
                if ((header[1] & 0x80) != 0) throw new IOException("The server masked a frame.");
                long length = header[1] & 0x7F;
                if (length == 126)
                {
                    _stream.ReadExactly(header.AsSpan(0, 2));
                    length = BinaryPrimitives.ReadUInt16BigEndian(header);
                }
                else if (length == 127)
                {
                    _stream.ReadExactly(header.AsSpan(0, 8));
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(header);
                }
                if (length < 0 || length + message.Length > MaxMessage) throw new IOException("The server's message is too large.");
                var payload = new byte[length];
                _stream.ReadExactly(payload);
                Interlocked.Exchange(ref _lastHeard, Environment.TickCount64);
                switch (opcode)
                {
                    case PingFrame:
                        Queue(PongFrame, payload);
                        break;
                    case PongFrame:
                        break;
                    case CloseFrame:
                        Closed = payload.Length >= 2
                            ? (BinaryPrimitives.ReadUInt16BigEndian(payload), Encoding.UTF8.GetString(payload, 2, payload.Length - 2))
                            : (1005, "");
                        SendClose(1000);
                        _outbox.CompleteAdding();
                        _open = false;
                        return;
                    case TextFrame or BinaryFrame or Continuation:
                        if (opcode != Continuation) messageText = opcode == TextFrame;
                        message.Write(payload);
                        if (!fin) break;
                        _inbox.Enqueue(new WebSocketMessage(messageText, message.ToArray()));
                        message.SetLength(0);
                        break;
                    default:
                        throw new IOException("Unknown frame.");
                }
            }
        }
        catch (Exception) { } // closed by either side, or a broken frame
        _open = false;
        _outbox.CompleteAdding();
        _client.Close();
    }

    private void WriteLoop()
    {
        try
        {
            while (!_outbox.IsCompleted)
            {
                if (_outbox.TryTake(out var frame, TimeSpan.FromSeconds(5)))
                {
                    _stream.Write(frame);
                    _stream.Flush();
                }
                else if (Environment.TickCount64 - Interlocked.Read(ref _lastHeard) > SilenceLimit.TotalMilliseconds)
                {
                    break; // the server went quiet: the network is gone
                }
            }
        }
        catch (Exception) { }
        _open = false;
        _outbox.CompleteAdding();
        _client.Close(); // ends the read loop too
    }
}
