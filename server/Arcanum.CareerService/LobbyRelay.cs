// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcanum.CareerService;

/// <summary>
/// A hosted game's public description, as the game's lobby browser shows it. <see cref="Address"/> is set here (the
/// host's public address, for a direct connection to <see cref="Port"/> before the relay); 0 means the host takes no
/// direct connections.
/// </summary>
public sealed record LobbyInfo(string LobbyId, string HostName, string Format, bool Commander, int Seats, int OpenSeats,
    string Version, string Content, string InviteCode, bool Listed = true, string? Event = null)
{
    public string? Address { get; init; }
    public int Port { get; init; }

    /// <summary>Whether the fields are within bounds (the host's text is shown to other players).</summary>
    public bool IsValid() =>
        Short(LobbyId, 64) && LobbyId.Length > 0 && Short(HostName, 40) && Short(Format, 80) && Short(Version, 32)
        && Short(Content, 80) && Short(InviteCode, 16) && (Event is null || Short(Event, 80))
        && Seats is >= 1 and <= 8 && OpenSeats >= 0 && OpenSeats <= Seats && Port is >= 0 and <= 65535;

    private static bool Short(string? text, int max) => text is not null && text.Length <= max;
}

public sealed class LobbyRelayOptions
{
    public int MaxLobbies { get; set; } = 200;
    public int MaxLobbiesPerPlayer { get; set; } = 2;
    public int MaxGuestsPerLobby { get; set; } = 8;
    public int MaxGuests { get; set; } = 1000;

    /// <summary>The largest relayed packet, in bytes (the game splits its messages to fit).</summary>
    public int MaxPacket { get; set; } = 16 * 1024;

    /// <summary>What a lobby may relay, all its players together: a steady rate and a burst, in bytes.</summary>
    public int BytesPerSecond { get; set; } = 256 * 1024;
    public int BurstBytes { get; set; } = 1024 * 1024;

    /// <summary>How long a host has to describe its lobby after connecting.</summary>
    public TimeSpan PublishWithin { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Internet lobbies for players whose system has no other lobby service (a console), and the relay that carries their
/// games. A lobby lives while its host's WebSocket is open:
/// <list type="bullet">
/// <item><c>/play/host</c>: the host's socket. Its first message (text) is <c>{"type":"publish","lobby":{…}}</c>, sent
/// again as the lobby changes. The service tells it <c>{"type":"open","peer":n}</c> when a player connects and
/// <c>{"type":"closed","peer":n}</c> when one leaves; the host may send <c>{"type":"close","peer":n}</c>. Binary
/// messages both ways are a player's packets: the peer number (4 bytes, big-endian), then the packet.</item>
/// <item><c>/play/join?lobby=id</c>: a player's socket; binary messages are packets to and from the host.</item>
/// <item><c>GET /lobbies?version=&amp;content=&amp;open=</c> lists the listed lobbies of that version and content;
/// <c>GET /lobbies/code/{code}</c> finds one by invite code.</item>
/// </list>
/// Every request carries the player's identity token. Nothing is stored: lobbies are gone when the service restarts.
/// </summary>
public sealed class LobbyRelay(LobbyRelayOptions options, ILogger<LobbyRelay> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new();
    private readonly object _gate = new();
    private int _guests;

    public LobbyRelayOptions Options => options;

    public IReadOnlyList<LobbyInfo> Search(string version, string content, bool onlyOpen) =>
        _lobbies.Values.Select(l => l.Info)
            .Where(i => i.Listed && i.Version == version && i.Content == content && (!onlyOpen || i.OpenSeats > 0))
            .OrderBy(i => i.HostName, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

    public LobbyInfo? FindByCode(string code) =>
        _lobbies.Values.Select(l => l.Info).FirstOrDefault(i => string.Equals(i.InviteCode, code, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ host

    public async Task HostAsync(HttpContext http, string player)
    {
        lock (_gate)
        {
            if (_lobbies.Count >= options.MaxLobbies || _lobbies.Values.Count(l => l.Owner == player) >= options.MaxLobbiesPerPlayer)
            {
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var cancel = http.RequestAborted;
        var host = new Endpoint(socket);
        var buffer = new byte[options.MaxPacket + 4];

        // The lobby's description comes first.
        Lobby? lobby = null;
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            first.CancelAfter(options.PublishWithin);
            try
            {
                var message = await ReceiveAsync(socket, buffer, first.Token);
                if (message is { Text: true } && Read(buffer, message.Value.Length) is { Type: "publish", Lobby: { } info } && info.IsValid())
                {
                    var stamped = Stamp(info, http);
                    var made = new Lobby(player, host, stamped, options);
                    lock (_gate)
                    {
                        if (_lobbies.TryAdd(info.LobbyId, made)) lobby = made;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        }
        if (lobby is null)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "publish the lobby first");
            return;
        }
        log.LogInformation("Lobby {Lobby} hosted by {Player}", lobby.Info.LobbyId, player);
        try
        {
            while (true)
            {
                var message = await ReceiveAsync(socket, buffer, cancel);
                if (message is not { } got) break;
                if (got.Text)
                {
                    switch (Read(buffer, got.Length))
                    {
                        case { Type: "publish", Lobby: { } info } when info.IsValid() && info.LobbyId == lobby.Info.LobbyId:
                            lobby.Info = Stamp(info, http);
                            break;
                        case { Type: "close", Peer: { } peer } when lobby.Guests.TryGetValue(peer, out var guest):
                            await CloseAsync(guest.Socket, WebSocketCloseStatus.NormalClosure, "closed by the host");
                            break;
                    }
                    continue;
                }
                if (got.Length < 4) continue;
                uint to = BinaryPrimitives.ReadUInt32BigEndian(buffer);
                if (!lobby.Guests.TryGetValue(to, out var target)) continue;
                await lobby.Budget.SpendAsync(got.Length, cancel);
                await target.SendAsync(buffer.AsMemory(4, got.Length - 4), WebSocketMessageType.Binary, cancel);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            lock (_gate) _lobbies.TryRemove(new KeyValuePair<string, Lobby>(lobby.Info.LobbyId, lobby));
            lobby.Leave();
            foreach (var guest in lobby.Guests.Values) await CloseAsync(guest.Socket, WebSocketCloseStatus.EndpointUnavailable, "the host left");
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "bye");
            log.LogInformation("Lobby {Lobby} closed", lobby.Info.LobbyId);
        }
    }

    /// <summary>The host's public address, seen from here (the web server in front passes it on).</summary>
    private static LobbyInfo Stamp(LobbyInfo info, HttpContext http) => info with
    {
        Address = http.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                  ?? http.Connection.RemoteIpAddress?.ToString(),
    };

    // ------------------------------------------------------------------ player

    public async Task JoinAsync(HttpContext http, string player, string lobbyId)
    {
        if (!_lobbies.TryGetValue(lobbyId, out var lobby))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        lock (_gate)
        {
            if (_guests >= options.MaxGuests || lobby.Guests.Count >= options.MaxGuestsPerLobby)
            {
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            _guests++;
        }
        try
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var cancel = http.RequestAborted;
            var guest = new Endpoint(socket);
            uint peer = lobby.Add(guest);
            var buffer = new byte[options.MaxPacket + 4];
            try
            {
                // The host left meanwhile (it closes the players it saw; this one came after).
                if (lobby.HasLeft) return;
                await lobby.Host.SendAsync(Write(new Message("open", Peer: peer)), WebSocketMessageType.Text, cancel);
                while (true)
                {
                    // Received after the peer number's room, so the packet goes to the host as it is.
                    var message = await ReceiveAsync(socket, buffer.AsMemory(4), cancel);
                    if (message is not { } got) break;
                    if (got.Text) continue;
                    BinaryPrimitives.WriteUInt32BigEndian(buffer, peer);
                    await lobby.Budget.SpendAsync(got.Length + 4, cancel);
                    await lobby.Host.SendAsync(buffer.AsMemory(0, got.Length + 4), WebSocketMessageType.Binary, cancel);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                lobby.Guests.TryRemove(peer, out _);
                try { await lobby.Host.SendAsync(Write(new Message("closed", Peer: peer)), WebSocketMessageType.Text, CancellationToken.None); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                await CloseAsync(socket, lobby.HasLeft ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure,
                    lobby.HasLeft ? "the host left" : "bye");
            }
        }
        finally
        {
            lock (_gate) _guests--;
        }
    }

    // ------------------------------------------------------------------ pieces

    private sealed record Message(string Type, LobbyInfo? Lobby = null, uint? Peer = null);

    private static Message? Read(byte[] buffer, int length)
    {
        try { return JsonSerializer.Deserialize<Message>(buffer.AsSpan(0, length), Json); }
        catch (JsonException) { return null; }
    }

    private static byte[] Write(Message message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    /// <summary>One whole message into the buffer; null when the socket closed. Too large a message closes it.</summary>
    private static async Task<(int Length, bool Text)?> ReceiveAsync(WebSocket socket, Memory<byte> buffer, CancellationToken cancel)
    {
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too large");
                return null;
            }
            var result = await socket.ReceiveAsync(buffer[length..], cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            length += result.Count;
            if (result.EndOfMessage) return (length, result.MessageType == WebSocketMessageType.Text);
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        // Also when a send is under way on it (the player's own task, about to fail).
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>A socket that several tasks send on, one message at a time.</summary>
    private sealed class Endpoint(WebSocket socket)
    {
        private readonly SemaphoreSlim _sending = new(1, 1);

        public WebSocket Socket => socket;

        public async Task SendAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken cancel)
        {
            await _sending.WaitAsync(cancel);
            try { await socket.SendAsync(message, type, true, cancel); }
            finally { _sending.Release(); }
        }
    }

    private sealed class Lobby(string owner, Endpoint host, LobbyInfo info, LobbyRelayOptions options)
    {
        private uint _nextPeer;
        private volatile bool _left;

        /// <summary>
        /// Set when the host leaves, before it closes the players it has: a player added after that sees it and closes
        /// itself. (Their reads aren't cancelled: cancelling a read aborts the socket without its closing message.)
        /// </summary>
        public bool HasLeft => _left;

        public void Leave() => _left = true;

        public string Owner => owner;
        public Endpoint Host => host;
        public volatile LobbyInfo Info = info;
        public ConcurrentDictionary<uint, Endpoint> Guests { get; } = new();
        public ByteBudget Budget { get; } = new(options.BytesPerSecond, options.BurstBytes);

        public uint Add(Endpoint guest)
        {
            uint peer = Interlocked.Increment(ref _nextPeer);
            Guests[peer] = guest;
            return peer;
        }
    }
}

/// <summary>A byte rate with a burst (a token bucket): spending more than there is waits until the rate refills it.</summary>
public sealed class ByteBudget(int perSecond, int burst, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private double _tokens = burst;
    private long _last = (clock ?? TimeProvider.System).GetTimestamp();

    public async Task SpendAsync(int bytes, CancellationToken cancel)
    {
        TimeSpan wait;
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            _tokens = Math.Min(burst, _tokens + _clock.GetElapsedTime(_last, now).TotalSeconds * perSecond);
            _last = now;
            _tokens -= bytes;
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / perSecond);
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, cancel);
    }
}

public static class LobbyRelayEndpoints
{
    /// <summary>
    /// The lobby and relay routes (see <see cref="LobbyRelay"/>); <paramref name="player"/> tells who is asking (null: no
    /// valid identity).
    /// </summary>
    public static void MapLobbyRelay(this IEndpointRouteBuilder app, Func<HttpContext, Task<string?>> player, string? searchLimit = null)
    {
        var search = app.MapGet("/lobbies", async (HttpContext http, string version, string content, bool? open, LobbyRelay relay) =>
            await player(http) is null ? Results.Unauthorized() : Results.Ok(relay.Search(version, content, open ?? true)));
        var code = app.MapGet("/lobbies/code/{code}", async (HttpContext http, string code, LobbyRelay relay) =>
            await player(http) is null ? Results.Unauthorized()
            : relay.FindByCode(code) is { } found ? Results.Ok(found) : Results.NotFound());
        if (searchLimit is not null)
        {
            search.RequireRateLimiting(searchLimit);
            code.RequireRateLimiting(searchLimit);
        }
        app.Map("/play/host", async (HttpContext http, LobbyRelay relay) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (await player(http) is not { } who) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            await relay.HostAsync(http, who);
        });
        app.Map("/play/join", async (HttpContext http, string lobby, LobbyRelay relay) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (await player(http) is not { } who) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            await relay.JoinAsync(http, who, lobby);
        });
    }
}
