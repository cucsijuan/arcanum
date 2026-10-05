// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// Online services on the local network, without accounts: hosts announce their lobbies with UDP broadcasts, the lobby browser
/// listens for them, and players connect straight to the host over TCP. Invite codes work for lobbies on the same network.
/// </summary>
public sealed class LanOnlineServices : IOnlineServices
{
    public const int DefaultAnnouncePort = 47014;

    /// <summary>How often a host announces its lobbies, and how long a search listens.</summary>
    public static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SearchFor = TimeSpan.FromSeconds(2.5);

    private readonly int _announcePort;
    private readonly ConcurrentDictionary<string, LobbyListing> _published = new();
    private readonly ConcurrentDictionary<string, TcpConnectionListener> _listeners = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _announcer;

    /// <param name="announcePort">The UDP port lobbies are announced on (the same for everyone on the network).</param>
    public LanOnlineServices(int announcePort = DefaultAnnouncePort)
    {
        _announcePort = announcePort;
        Lobbies = new Directory(this);
        Network = new Direct(this);
    }

    public string Name => "Local network";
    public bool IsAvailable => true;
    public ILobbyDirectory Lobbies { get; }
    public IRelayNetwork Network { get; }

    public Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default) =>
        Task.FromResult(new OnlineIdentity($"lan-{Environment.MachineName}-{Environment.ProcessId}", displayName));

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var listener in _listeners.Values) listener.Dispose();
        _listeners.Clear();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The datagram announcing a lobby: its listing and the TCP port players connect to.</summary>
    private sealed record Announcement(string Signature, LobbyListing Lobby);

    private const string Signature = "arcanum-lobby-1";

    private void StartAnnouncing()
    {
        if (_announcer is not null) return;
        _announcer = Task.Run(async () =>
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            while (!_stop.IsCancellationRequested)
            {
                foreach (var lobby in _published.Values)
                {
                    var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Announcement(Signature, lobby with { Address = null }), Json));
                    foreach (var target in new[] { IPAddress.Broadcast, IPAddress.Loopback })
                    {
                        try { await udp.SendAsync(data, data.Length, new IPEndPoint(target, _announcePort)); }
                        catch (SocketException) { } // no network: nothing to announce to
                    }
                }
                try { await Task.Delay(AnnounceEvery, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    /// <summary>Listens for announcements for a while; each lobby once, with the address it was announced from.</summary>
    private async Task<IReadOnlyList<LobbyListing>> ListenAsync(TimeSpan duration, Func<LobbyListing, bool> wanted, bool stopAtFirst, CancellationToken cancel)
    {
        var found = new Dictionary<string, LobbyListing>();
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _announcePort));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(duration);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                Announcement? announcement;
                try { announcement = JsonSerializer.Deserialize<Announcement>(result.Buffer, Json); }
                catch (JsonException) { continue; }
                if (announcement is not { Signature: Signature, Lobby: { } lobby } || !wanted(lobby)) continue;
                found[lobby.LobbyId] = lobby with { Address = result.RemoteEndPoint.Address.ToString() };
                if (stopAtFirst) break;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        return found.Values.OrderBy(l => l.HostName).ToList();
    }

    private sealed class Directory(LanOnlineServices lan) : ILobbyDirectory
    {
        public Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            if (lan._listeners.TryGetValue(listing.LobbyId, out var listener)) listing = listing with { Port = listener.Port };
            lan._published[listing.LobbyId] = listing;
            lan.StartAnnouncing();
            return Task.FromResult(listing);
        }

        public Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            if (lan._published.TryGetValue(listing.LobbyId, out var old)) lan._published[listing.LobbyId] = listing with { Port = old.Port };
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string lobbyId, CancellationToken cancel = default)
        {
            lan._published.TryRemove(lobbyId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default) =>
            lan.ListenAsync(SearchFor, l => l.Listed && l.Version == query.Version && l.Content == query.Content && (!query.OnlyWithOpenSeats || l.OpenSeats > 0),
                stopAtFirst: false, cancel);

        public async Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default)
        {
            if (!InviteCode.TryParse(inviteCode, out var code)) return null;
            var found = await lan.ListenAsync(SearchFor, l => l.InviteCode == code, stopAtFirst: true, cancel);
            return found.FirstOrDefault();
        }
    }

    private sealed class Direct(LanOnlineServices lan) : IRelayNetwork
    {
        public IConnectionListener Listen(string lobbyId) => lan._listeners.GetOrAdd(lobbyId, _ => new TcpConnectionListener(0));

        public async Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default)
        {
            if (lobby.Address is not { } address || lobby.Port <= 0) throw new InvalidOperationException("That lobby can't be reached directly.");
            return await TcpConnection.ConnectAsync(address, lobby.Port, TimeSpan.FromSeconds(8));
        }
    }
}
