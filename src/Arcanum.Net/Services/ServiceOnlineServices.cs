// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// Internet lobbies kept by the game's own service (its lobby relay), for systems without another internet lobby service
/// (a console). A hosted lobby keeps one WebSocket to the service: the lobby is listed while it is open, and players who
/// can't reach the host directly are relayed through it. Joining tries a direct TCP connection to the host's public
/// address first (when the host takes them), then the relay. Every request carries the player's identity headers.
/// </summary>
public sealed class ServiceOnlineServices : IOnlineServices
{
    /// <summary>The largest relayed packet the service takes.</summary>
    public const int MaxPacket = 16 * 1024;

    public static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _service;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>?>> _identity;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();
    private volatile bool _signedIn;

    /// <param name="service">The service's address (https or http).</param>
    /// <param name="identity">The headers that say who the player is (an identity token), or null when not signed in.</param>
    public ServiceOnlineServices(Uri service, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>?>> identity, HttpClient? http = null)
    {
        _service = service;
        _identity = identity;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Lobbies = new Directory(this);
        Network = new Relay(this);
    }

    public string Name => "Internet relay";
    public bool IsAvailable => _signedIn;
    public ILobbyDirectory Lobbies { get; }
    public IRelayNetwork Network { get; }

    public async Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default)
    {
        var headers = await _identity(cancel) ?? throw new InvalidOperationException("Sign in to play on the internet.");
        _signedIn = true;
        return new OnlineIdentity(UserOf(headers), displayName);
    }

    /// <summary>Who the headers name: an identity token's subject, else the header's own value.</summary>
    private static string UserOf(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || !value.StartsWith("Bearer ", StringComparison.Ordinal)) continue;
            var parts = value[7..].Split('.');
            if (parts.Length != 3) break;
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var token = JsonDocument.Parse(Convert.FromBase64String(payload));
                if (token.RootElement.TryGetProperty("sub", out var sub) && sub.GetString() is { Length: > 0 } user) return user;
            }
            catch (Exception e) when (e is FormatException or JsonException) { }
            break;
        }
        return headers.Values.FirstOrDefault() ?? "player";
    }

    private async Task<IReadOnlyDictionary<string, string>> HeadersAsync(CancellationToken cancel) =>
        await _identity(cancel) ?? throw new InvalidOperationException("Sign in to play on the internet.");

    private Uri Socket(string path)
    {
        var builder = new UriBuilder(new Uri(_service, path)) { Scheme = _service.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_service, path));
        foreach (var (name, value) in await HeadersAsync(cancel)) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancel);
    }

    private HostSession Session(string lobbyId) => _sessions.GetOrAdd(lobbyId, id => new HostSession(this, id));

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Close();
        _sessions.Clear();
    }

    // ------------------------------------------------------------------ lobbies

    private sealed class Directory(ServiceOnlineServices service) : ILobbyDirectory
    {
        public async Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            var session = service.Session(listing.LobbyId);
            await session.OpenAsync(cancel);
            session.Publish(listing);
            return listing;
        }

        public async Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            if (!service._sessions.TryGetValue(listing.LobbyId, out var session)) return;
            await session.OpenAsync(cancel); // again, if the connection to the service was lost
            session.Publish(listing);
        }

        public Task RemoveAsync(string lobbyId, CancellationToken cancel = default)
        {
            if (service._sessions.TryRemove(lobbyId, out var session)) session.Close();
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default) =>
            await service.GetAsync<List<LobbyListing>>(
                $"lobbies?version={Uri.EscapeDataString(query.Version)}&content={Uri.EscapeDataString(query.Content)}" +
                $"&open={(query.OnlyWithOpenSeats ? "true" : "false")}", cancel) ?? new List<LobbyListing>();

        public Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default) =>
            service.GetAsync<LobbyListing>($"lobbies/code/{Uri.EscapeDataString(inviteCode)}", cancel);
    }

    /// <summary>A hosted lobby's connection to the service, and the relay of its players' connections.</summary>
    private sealed class HostSession(ServiceOnlineServices service, string lobbyId)
    {
        private readonly SemaphoreSlim _opening = new(1, 1);
        private WebSocketClient? _socket;
        private volatile IConnectionListener? _listener;
        private LobbyListing? _listing;
        private bool _closed;

        public async Task OpenAsync(CancellationToken cancel)
        {
            await _opening.WaitAsync(cancel);
            try
            {
                if (_closed || _socket is { IsOpen: true }) return;
                var socket = await WebSocketClient.ConnectAsync(service.Socket("play/host"), await service.HeadersAsync(cancel), ConnectTimeout, cancel);
                _socket = socket;
                _listener = new PacketRelay(new HostLink(socket)).Listen();
                if (_listing is { } listing) Publish(listing);
            }
            finally { _opening.Release(); }
        }

        public void Publish(LobbyListing listing)
        {
            _listing = listing;
            var lobby = listing with { Address = null, Provider = null };
            _socket?.SendText(JsonSerializer.Serialize(new { type = "publish", lobby }, Json));
        }

        public IConnectionListener Listener => new SessionListener(this);

        public void Close()
        {
            _closed = true;
            _socket?.Close();
        }

        private sealed class SessionListener(HostSession session) : IConnectionListener
        {
            public bool TryAccept([NotNullWhen(true)] out IConnection? connection)
            {
                connection = null;
                return session._listener is { } listener && listener.TryAccept(out connection);
            }

            public void Dispose() => session.Close();
        }

        public override string ToString() => lobbyId;
    }

    // ------------------------------------------------------------------ connections

    private sealed class Relay(ServiceOnlineServices service) : IRelayNetwork
    {
        public IConnectionListener Listen(string lobbyId) => service.Session(lobbyId).Listener;

        public async Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default)
        {
            // Straight to the host when it takes connections from the internet (its router lets them in).
            if (!string.IsNullOrEmpty(lobby.Address) && lobby.Port > 0)
            {
                try { return await TcpConnection.ConnectAsync(lobby.Address, lobby.Port, DirectTimeout); }
                catch (Exception e) when (e is SocketException or TimeoutException or IOException) { }
            }
            var socket = await WebSocketClient.ConnectAsync(service.Socket($"play/join?lobby={Uri.EscapeDataString(lobby.LobbyId)}"),
                await service.HeadersAsync(cancel), ConnectTimeout, cancel);
            try
            {
                var connection = await new PacketRelay(new PlayerLink(socket)).ConnectAsync(PlayerLink.Host, ConnectTimeout, cancel);
                return new SocketConnection(connection, socket);
            }
            catch
            {
                socket.Close();
                throw;
            }
        }
    }

    /// <summary>A relayed connection that also closes its socket to the service.</summary>
    private sealed class SocketConnection(IConnection connection, WebSocketClient socket) : IConnection
    {
        public bool IsOpen => connection.IsOpen;
        public void Send(string message) => connection.Send(message);
        public bool TryReceive([NotNullWhen(true)] out string? message) => connection.TryReceive(out message);

        public void Close()
        {
            connection.Close();
            socket.Close();
        }

        public void Dispose() => Close();
    }

    /// <summary>
    /// The host's side of the relay: packets carry a player's number (4 bytes, big-endian) first; the service tells when
    /// a player leaves.
    /// </summary>
    internal sealed class HostLink(WebSocketClient socket) : IPacketLink
    {
        private readonly ConcurrentQueue<string> _closed = new();
        private readonly HashSet<string> _known = new();
        private bool _lost;

        public int MaxPacketSize => MaxPacket;

        public void Send(string peer, byte[] packet)
        {
            if (!uint.TryParse(peer, out uint number)) return;
            var data = new byte[4 + packet.Length];
            BinaryPrimitives.WriteUInt32BigEndian(data, number);
            packet.CopyTo(data, 4);
            socket.SendBinary(data);
        }

        public bool TryReceive([NotNullWhen(true)] out string? peer, [NotNullWhen(true)] out byte[]? packet)
        {
            while (socket.TryReceive(out var message))
            {
                var got = message.Value;
                if (got.Text)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(got.Data);
                        if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "closed"
                            && json.RootElement.TryGetProperty("peer", out var number))
                        {
                            var who = number.GetUInt32().ToString();
                            lock (_known) _known.Remove(who);
                            _closed.Enqueue(who);
                        }
                    }
                    catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { }
                    continue;
                }
                if (got.Data.Length < 4) continue;
                peer = BinaryPrimitives.ReadUInt32BigEndian(got.Data).ToString();
                lock (_known) _known.Add(peer);
                packet = got.Data[4..];
                return true;
            }
            peer = null;
            packet = null;
            return false;
        }

        public bool TryTakeClosedPeer([NotNullWhen(true)] out string? peer)
        {
            // The service connection lost: every player relayed through it is gone.
            if (!socket.IsOpen && !socket.HasPending && !_lost)
            {
                _lost = true;
                lock (_known)
                {
                    foreach (var known in _known) _closed.Enqueue(known);
                    _known.Clear();
                }
            }
            return _closed.TryDequeue(out peer);
        }
    }

    /// <summary>A player's side of the relay: every packet is to or from the host.</summary>
    internal sealed class PlayerLink(WebSocketClient socket) : IPacketLink
    {
        public const string Host = "host";
        private bool _lost;

        public int MaxPacketSize => MaxPacket;

        public void Send(string peer, byte[] packet) => socket.SendBinary(packet);

        public bool TryReceive([NotNullWhen(true)] out string? peer, [NotNullWhen(true)] out byte[]? packet)
        {
            while (socket.TryReceive(out var message))
            {
                if (message.Value.Text) continue;
                peer = Host;
                packet = message.Value.Data;
                return true;
            }
            peer = null;
            packet = null;
            return false;
        }

        public bool TryTakeClosedPeer([NotNullWhen(true)] out string? peer)
        {
            peer = Host;
            if (socket.IsOpen || socket.HasPending || _lost) return false; // what came before the end is read first
            _lost = true;
            return true;
        }
    }
}
