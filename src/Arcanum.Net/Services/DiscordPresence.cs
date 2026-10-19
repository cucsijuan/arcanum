// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arcanum.Net.Services;

/// <summary>What the player's Discord profile shows they are doing.</summary>
/// <param name="Details">The first line ("Commander game").</param>
/// <param name="State">The second line ("Turn 7").</param>
/// <param name="Start">When the shown activity began (Discord counts the time from it); null shows no time.</param>
public sealed record DiscordActivity(string Details, string? State = null, DateTimeOffset? Start = null);

/// <summary>
/// The player's activity on Discord ("rich presence"): talks to the Discord app running on the same computer through its
/// local socket (a Unix socket, or a named pipe on Windows). Needs no sign-in; when Discord isn't running nothing is shown,
/// and the activity is sent as soon as it starts. All the work happens on a background task; <see cref="Set"/> never waits.
/// </summary>
/// <param name="environment">Reads environment variables (where the socket is); tests pass their own.</param>
public sealed class DiscordPresence(string clientId, string? largeImage = null, string? largeText = null,
    Func<string, string?>? environment = null) : IDisposable
{
    private const int Handshake = 0, Frame = 1, Close = 2;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private DiscordActivity? _wanted;
    private bool _changed;
    private Task? _loop;

    /// <summary>Shows <paramref name="activity"/> (null clears it). Repeating the shown activity does nothing.</summary>
    public void Set(DiscordActivity? activity)
    {
        lock (_gate)
        {
            if (Equals(_wanted, activity)) return;
            _wanted = activity;
            _changed = true;
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
        _wake.Release();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var stream = await ConnectAsync(cancel);
                if (stream is not null)
                {
                    await WriteAsync(stream, Handshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, cancel);
                    await ReadAsync(stream, cancel); // READY (or an error frame, after which Discord closes)
                    lock (_gate) _changed = true;
                    while (true)
                    {
                        DiscordActivity? activity;
                        bool send;
                        lock (_gate)
                        {
                            (activity, send) = (_wanted, _changed);
                            _changed = false;
                        }
                        if (!send)
                        {
                            await _wake.WaitAsync(cancel);
                            continue;
                        }
                        await WriteAsync(stream, Frame, Command(activity, Environment.ProcessId), cancel);
                        if ((await ReadAsync(stream, cancel)).Op == Close) break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception) { } // Discord closed or isn't there: try again later
            try { await Task.Delay(Retry, cancel); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The SET_ACTIVITY command for <paramref name="activity"/> (null clears it).</summary>
    public JsonObject Command(DiscordActivity? activity, int processId)
    {
        JsonObject? body = null;
        if (activity is not null)
        {
            body = new JsonObject { ["details"] = Clip(activity.Details) };
            if (activity.State is { Length: > 0 } state) body["state"] = Clip(state);
            if (activity.Start is { } start) body["timestamps"] = new JsonObject { ["start"] = start.ToUnixTimeSeconds() };
            if (largeImage is not null)
            {
                var assets = new JsonObject { ["large_image"] = largeImage };
                if (largeText is not null) assets["large_text"] = largeText;
                body["assets"] = assets;
            }
        }
        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = processId, ["activity"] = body },
            ["nonce"] = Guid.NewGuid().ToString(),
        };
    }

    /// <summary>Discord takes 2 to 128 characters per line.</summary>
    private static string Clip(string text) => text.Length > 128 ? text[..127] + "…" : text.PadRight(2);

    /// <summary>A frame: opcode and length (little-endian 32-bit each), then the JSON.</summary>
    public static byte[] Encode(int op, JsonNode payload)
    {
        byte[] json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        return frame;
    }

    private static async Task WriteAsync(Stream stream, int op, JsonNode payload, CancellationToken cancel)
    {
        await stream.WriteAsync(Encode(op, payload), cancel);
        await stream.FlushAsync(cancel);
    }

    private static async Task<(int Op, JsonNode? Body)> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancel);
        int op = BinaryPrimitives.ReadInt32LittleEndian(header), length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20) throw new InvalidDataException("Bad frame");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancel);
        try { return (op, JsonNode.Parse(body)); }
        catch (JsonException) { return (op, null); }
    }

    /// <summary>
    /// Where the Discord app listens on this system: discord-ipc-0..9 in the runtime or temporary folder, also inside the
    /// folders that Flatpak and Snap installs use.
    /// </summary>
    public static IEnumerable<string> SocketPaths(Func<string, string?> environment)
    {
        var roots = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }.Select(environment).OfType<string>().Where(r => r.Length > 0)
            .Append("/tmp").Distinct().ToList();
        var folders = roots.SelectMany(r => new[] { r, Path.Combine(r, "app", "com.discordapp.Discord"), Path.Combine(r, "snap.discord") });
        foreach (var folder in folders)
            for (int i = 0; i < 10; i++)
                yield return Path.Combine(folder, $"discord-ipc-{i}");
    }

    private async Task<Stream?> ConnectAsync(CancellationToken cancel)
    {
        if (OperatingSystem.IsWindows())
        {
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(500, cancel);
                    return pipe;
                }
                catch (Exception e) when (e is TimeoutException or IOException) { await pipe.DisposeAsync(); }
            }
            return null;
        }
        foreach (var path in SocketPaths(environment ?? Environment.GetEnvironmentVariable).Where(File.Exists))
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
        }
        return null;
    }
}
