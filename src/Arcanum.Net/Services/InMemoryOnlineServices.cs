// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// Online services inside one process: a shared lobby directory and in-memory connections. For tests and for developing the
/// online screens without accounts or a network; every <see cref="InMemoryOnlineServices"/> made from the same backend sees the
/// same lobbies.
/// </summary>
public sealed class InMemoryOnlineServices : IOnlineServices
{
    private readonly Backend _backend;
    private readonly string _device;

    /// <summary>The shared "server" of a set of in-memory services.</summary>
    public sealed class Backend
    {
        internal readonly ConcurrentDictionary<string, LobbyListing> Lobbies = new();
        internal readonly ConcurrentDictionary<string, InMemoryListener> Listeners = new();
        internal int NextUser;
    }

    public InMemoryOnlineServices(Backend backend, string device = "device")
    {
        _backend = backend;
        _device = device;
        Lobbies = new Directory(backend);
        Network = new Relay(backend);
    }

    public string Name => "In-process";
    public bool IsAvailable => true;
    public ILobbyDirectory Lobbies { get; }
    public IRelayNetwork Network { get; }

    public Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default) =>
        Task.FromResult(new OnlineIdentity($"{_device}-{Interlocked.Increment(ref _backend.NextUser)}", displayName));

    public void Dispose() { }

    private sealed class Directory(Backend backend) : ILobbyDirectory
    {
        public Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            // Invite codes are unique among open lobbies.
            while (backend.Lobbies.Values.Any(l => l.InviteCode == listing.InviteCode && l.LobbyId != listing.LobbyId))
                listing = listing with { InviteCode = InviteCode.New() };
            backend.Lobbies[listing.LobbyId] = listing;
            return Task.FromResult(listing);
        }

        public Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            if (backend.Lobbies.ContainsKey(listing.LobbyId)) backend.Lobbies[listing.LobbyId] = listing;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string lobbyId, CancellationToken cancel = default)
        {
            backend.Lobbies.TryRemove(lobbyId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<LobbyListing>>(backend.Lobbies.Values
                .Where(l => l.Listed && l.Version == query.Version && l.Content == query.Content && (!query.OnlyWithOpenSeats || l.OpenSeats > 0))
                .OrderBy(l => l.HostName).ToList());

        public Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default) =>
            Task.FromResult(InviteCode.TryParse(inviteCode, out var code) ? backend.Lobbies.Values.FirstOrDefault(l => l.InviteCode == code) : null);
    }

    private sealed class Relay(Backend backend) : IRelayNetwork
    {
        public IConnectionListener Listen(string lobbyId) => backend.Listeners.GetOrAdd(lobbyId, _ => new InMemoryListener());

        public Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default) =>
            backend.Listeners.TryGetValue(lobby.LobbyId, out var listener)
                ? Task.FromResult(listener.Connect())
                : Task.FromException<IConnection>(new InvalidOperationException("That lobby isn't open any more."));
    }
}
