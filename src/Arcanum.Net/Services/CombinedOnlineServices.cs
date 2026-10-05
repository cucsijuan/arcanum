// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// Several online services used together (the local network and an internet service): a hosted lobby is published on every
/// available one and players reach the host through any of them; the browser lists what all of them find, each lobby once
/// (from the first service in order that has it).
/// </summary>
public sealed class CombinedOnlineServices : IOnlineServices
{
    private readonly IReadOnlyList<IOnlineServices> _services;

    public CombinedOnlineServices(params IOnlineServices[] services)
    {
        _services = services;
        Lobbies = new Directory(this);
        Network = new Networks(this);
    }

    private IEnumerable<IOnlineServices> Available => _services.Where(s => s.IsAvailable);

    public string Name => string.Join(" and ", Available.Select(s => s.Name).DefaultIfEmpty("No service"));
    public bool IsAvailable => Available.Any();
    public ILobbyDirectory Lobbies { get; }
    public IRelayNetwork Network { get; }

    /// <summary>The services combined, in order of preference.</summary>
    public IReadOnlyList<IOnlineServices> Services => _services;

    public async Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default)
    {
        OnlineIdentity? first = null;
        foreach (var service in _services)
        {
            try { first ??= await service.SignInAsync(displayName, cancel); }
            catch (Exception) when (!cancel.IsCancellationRequested) { /* that service stays unavailable */ }
        }
        return first ?? throw new InvalidOperationException("No online service could sign in.");
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
    }

    private sealed class Directory(CombinedOnlineServices combined) : ILobbyDirectory
    {
        private readonly Dictionary<string, List<IOnlineServices>> _publishedOn = new();

        public async Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            LobbyListing? result = null;
            var on = new List<IOnlineServices>();
            foreach (var service in combined.Available.ToList())
            {
                try
                {
                    var published = await service.Lobbies.PublishAsync(listing, cancel);
                    result ??= published;
                    on.Add(service);
                }
                catch (Exception) when (!cancel.IsCancellationRequested && on.Count + 1 < combined._services.Count) { /* listed elsewhere */ }
            }
            if (result is null) throw new InvalidOperationException("No online service is available.");
            lock (_publishedOn) _publishedOn[listing.LobbyId] = on;
            return result;
        }

        private List<IOnlineServices> PublishedOn(string lobbyId)
        {
            lock (_publishedOn) return _publishedOn.TryGetValue(lobbyId, out var on) ? on.ToList() : new();
        }

        public async Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default)
        {
            foreach (var service in PublishedOn(listing.LobbyId))
            {
                try { await service.Lobbies.UpdateAsync(listing, cancel); }
                catch (Exception) when (!cancel.IsCancellationRequested) { /* the next update tries again */ }
            }
        }

        public async Task RemoveAsync(string lobbyId, CancellationToken cancel = default)
        {
            foreach (var service in PublishedOn(lobbyId))
            {
                try { await service.Lobbies.RemoveAsync(lobbyId, cancel); }
                catch (Exception) when (!cancel.IsCancellationRequested) { /* it expires on its own */ }
            }
            lock (_publishedOn) _publishedOn.Remove(lobbyId);
        }

        public async Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default)
        {
            var services = combined.Available.ToList();
            var results = await Task.WhenAll(services.Select(async s =>
            {
                try { return (await s.Lobbies.SearchAsync(query, cancel)).Select(l => l with { Provider = s.Name }).ToList(); }
                catch (Exception) when (!cancel.IsCancellationRequested) { return new List<LobbyListing>(); }
            }));
            var seen = new HashSet<string>();
            return results.SelectMany(r => r).Where(l => seen.Add(l.LobbyId)).ToList();
        }

        public async Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default)
        {
            var services = combined.Available.ToList();
            var results = await Task.WhenAll(services.Select(async s =>
            {
                try { return await s.Lobbies.FindByCodeAsync(inviteCode, cancel) is { } l ? l with { Provider = s.Name } : null; }
                catch (Exception) when (!cancel.IsCancellationRequested) { return null; }
            }));
            return results.FirstOrDefault(l => l is not null);
        }
    }

    private sealed class Networks(CombinedOnlineServices combined) : IRelayNetwork
    {
        /// <summary>Listens on every service, including ones that become available later (signed in after hosting).</summary>
        public IConnectionListener Listen(string lobbyId)
        {
            var listener = new AnyListener(combined, lobbyId);
            listener.ListenOnAvailable();
            return listener;
        }

        public async Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default)
        {
            var services = combined.Available.Where(s => lobby.Provider is null || s.Name == lobby.Provider).ToList();
            Exception? last = null;
            foreach (var service in services)
            {
                try { return await service.Network.ConnectAsync(lobby, cancel); }
                catch (Exception e) when (!cancel.IsCancellationRequested) { last = e; }
            }
            throw last ?? new InvalidOperationException("No online service can reach that lobby.");
        }
    }

    private sealed class AnyListener(CombinedOnlineServices combined, string lobbyId) : IConnectionListener
    {
        private readonly Dictionary<IOnlineServices, IConnectionListener> _listeners = new();

        public void ListenOnAvailable()
        {
            lock (_listeners)
                foreach (var service in combined.Available)
                    if (!_listeners.ContainsKey(service)) _listeners[service] = service.Network.Listen(lobbyId);
        }

        public bool TryAccept([NotNullWhen(true)] out IConnection? connection)
        {
            ListenOnAvailable();
            lock (_listeners)
            {
                foreach (var listener in _listeners.Values)
                    if (listener.TryAccept(out connection)) return true;
            }
            connection = null;
            return false;
        }

        public void Dispose()
        {
            lock (_listeners)
            {
                foreach (var listener in _listeners.Values) listener.Dispose();
                _listeners.Clear();
            }
        }
    }
}
