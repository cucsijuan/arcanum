// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Client;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;

namespace Arcanum.Client;

/// <summary>
/// The lobby browser and invite codes: a hosted lobby is published to the online services (the local network for now), and
/// players find it in the browser or by its code, then reach the host through the service's network instead of an address.
/// </summary>
public partial class OnlineService
{
    private IOnlineServices? _services;
    private LobbyListing? _published;

    /// <summary>The lobby a joined player reached through the services (null: joined by address).</summary>
    private LobbyListing? _route;

    /// <summary>Where lobbies are listed and how players reach hosts.</summary>
    public IOnlineServices Services => _services ??= new LanOnlineServices();

    /// <summary>The hosted lobby as published: its invite code and what the browser shows.</summary>
    public LobbyListing? PublishedLobby => _published;

    /// <summary>Lists the hosted lobby in the browser and gives it an invite code (players connect through the service).</summary>
    private async void PublishHostedLobby()
    {
        if (_lobby is not { } lobby) return;
        try
        {
            var listing = ListingOf(lobby, LobbyIds.New(), InviteCode.New());
            lobby.AddListener(Services.Network.Listen(listing.LobbyId));
            _published = await Services.Lobbies.PublishAsync(listing);
            lobby.Changed += UpdatePublishedLobby;
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Status?.Invoke($"The lobby couldn't be listed ({Services.Name}): {e.Message}");
        }
    }

    private LobbyListing ListingOf(LobbyHost lobby, string id, string code) =>
        new(id, lobby.State.Seats.FirstOrDefault()?.Name ?? "Host", lobby.Settings.Format, lobby.Settings.Commander, lobby.Settings.Seats,
            lobby.State.Seats.Count(s => s.Kind == LobbySeatKind.Open), Version, ContentId, code, Listed: true, lobby.Settings.Event);

    private async void UpdatePublishedLobby()
    {
        if (_lobby is not { } lobby || _published is not { } published) return;
        var now = ListingOf(lobby, published.LobbyId, published.InviteCode) with { Port = published.Port };
        if (lobby.Game is not null || lobby.Event is not null) now = now with { OpenSeats = 0 }; // started: no longer joinable
        if (now == published) return;
        _published = now;
        try { await Services.Lobbies.UpdateAsync(now); }
        catch (Exception) { /* the next change tries again */ }
    }

    private async void UnpublishHostedLobby()
    {
        if (_published is not { } published) return;
        _published = null;
        try { await Services.Lobbies.RemoveAsync(published.LobbyId); }
        catch (Exception) { /* the listing expires on its own */ }
    }

    /// <summary>Lobbies the browser shows: same version and content, with free seats.</summary>
    public Task<IReadOnlyList<LobbyListing>> SearchLobbiesAsync() => Services.Lobbies.SearchAsync(new LobbyQuery(Version, ContentId));

    /// <summary>Joins a lobby found by its invite code.</summary>
    public async void JoinByCode(string name, string code, DeckInfo deck)
    {
        if (!InviteCode.TryParse(code, out var parsed))
        {
            Status?.Invoke($"An invite code has {InviteCode.Length} letters and digits, like {InviteCode.Display("ABCDEF")}.");
            return;
        }
        Status?.Invoke($"Looking for {InviteCode.Display(parsed)}…");
        var listing = await Services.Lobbies.FindByCodeAsync(parsed);
        if (listing is null)
        {
            Status?.Invoke($"No open lobby has the code {InviteCode.Display(parsed)} ({Services.Name}).");
            return;
        }
        JoinListing(name, listing, deck);
    }

    /// <summary>Joins a lobby from the browser (or found by its code), reaching the host through the service's network.</summary>
    public async void JoinListing(string name, LobbyListing listing, DeckInfo deck)
    {
        Leave();
        if (listing.Version != Version || listing.Content != ContentId)
        {
            Status?.Invoke("That lobby uses another version of the game or other card content.");
            return;
        }
        _route = listing;
        _address = listing.Address ?? "";
        _port = listing.Port;
        Status?.Invoke($"Joining {listing.HostName}'s lobby…");
        try
        {
            var connection = await Services.Network.ConnectAsync(listing);
            JoinLobby(connection, new ClientIdentity(name, "", Version, ContentId));
            SubmitDeck(deck);
        }
        catch (Exception e)
        {
            _route = null;
            Status?.Invoke($"Couldn't join: {e.Message}");
        }
    }

    /// <summary>A new connection to the host: through the services when the lobby was found there, otherwise to its address.</summary>
    private async Task<IConnection> ConnectToHostAsync(TimeSpan timeout)
    {
        if (_route is { } route)
        {
            using var cancel = new CancellationTokenSource(timeout);
            return await Services.Network.ConnectAsync(route, cancel.Token);
        }
        return await TcpConnection.ConnectAsync(_address, _port, timeout);
    }
}
