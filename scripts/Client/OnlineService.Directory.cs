// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Client;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;
using Godot;

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

    private Task? _signIn;

    /// <summary>
    /// Where lobbies are listed and how players reach hosts, in order of preference: the local network, the internet
    /// lobby service when this build has it, and the career service's relay (for systems without that service, a
    /// console; a hosted lobby is listed there too, so they find it, and only their games go through it).
    /// ARCANUM_ONLINE_INTERNET_ONLY=1 keeps only the internet lobby service and ARCANUM_ONLINE_RELAY_ONLY=1 only the
    /// relay, for testing them on one network.
    /// </summary>
    public IOnlineServices Services => _services ??= MakeServices();

    private IOnlineServices MakeServices()
    {
        if (OS.GetEnvironment("ARCANUM_ONLINE_RELAY_ONLY") == "1") return Relay;
        if (Internet is { } internet && OS.GetEnvironment("ARCANUM_ONLINE_INTERNET_ONLY") == "1") return internet;
        var lan = new LanOnlineServices(hosts: AcceptsConnections);
        return Internet is { } both ? new CombinedOnlineServices(lan, both, Relay) : new CombinedOnlineServices(lan, Relay);
    }

    /// <summary>
    /// Whether this system can take players' connections. A console can't (the connections it accepts fail): its
    /// lobbies are reached through the relay only.
    /// </summary>
    public static bool AcceptsConnections => OS.GetName() != "Switch";

    private ServiceOnlineServices? _relay;

    /// <summary>The career service's lobbies and relay.</summary>
    private ServiceOnlineServices Relay => _relay ??= new ServiceOnlineServices(Account.ServiceUrl, RelayIdentityAsync);

    /// <summary>
    /// Who the player is for the relay: the same identity token the career service takes (from the internet lobby
    /// service when signed in, else from its web interface for the Discord sign-in). ARCANUM_RELAY_PLAYER=name names the
    /// player instead, for a local service started with Lobbies:TrustPlayerHeader (testing).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> RelayIdentityAsync(CancellationToken cancel)
    {
        if (OS.GetEnvironment("ARCANUM_RELAY_PLAYER") is { Length: > 0 } tester) return new Dictionary<string, string> { ["X-Player"] = tester };
        string? token = null;
        try
        {
            if (Internet is null) token = Account.HasDiscord ? await Eos.EosWebIdentity.IdTokenAsync(cancel) : null;
            else if (Internet is IIdentityTokens tokens && Internet.IsAvailable) token = await tokens.IdTokenAsync(cancel);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException) { }
        return token is null ? null : new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
    }

    private IOnlineServices? _internet;
    private bool _internetMade;

    private IOnlineServices? Internet
    {
        get
        {
            if (!_internetMade)
            {
                _internetMade = true;
                _internet = Eos.InternetServices.Create(Version, message => GD.Print(message));
            }
            return _internet;
        }
    }

    /// <summary>Playing on the internet needs the player to sign in with Discord first (this build has internet lobbies).</summary>
    public bool NeedsAccount => !(Internet ?? (IOnlineServices)Relay).IsAvailable && !Account.HasDiscord && !Account.UsesDeviceAccount
                                && OS.GetEnvironment("ARCANUM_RELAY_PLAYER").Length == 0;

    private string? _internetError;
    private string? _relayError;
    private bool _publishWaiting;

    /// <summary>Whether this build has internet lobbies, and whether they're ready.</summary>
    public string InternetStatus => Internet is null ? RelayStatus
        : Internet.IsAvailable ? "Internet lobbies are on."
        : _internetError is { } error ? $"Internet lobbies are off: {error}"
        : "Connecting to internet lobbies…";

    /// <summary>The relay's state, where it's the only way onto the internet (a console).</summary>
    private string RelayStatus => Relay.IsAvailable ? "Internet lobbies are on."
        : _relayError is { } error ? $"Internet lobbies are off: {error}"
        : _signIn is { IsCompleted: false } ? "Connecting to internet lobbies…"
        : "Sign in with Discord (Settings, Account) to play on the internet.";

    /// <summary>Signs in to the internet service, then the relay (once each; again after a failure).</summary>
    public async void SignIn(string name)
    {
        bool internet = Internet is { IsAvailable: false }, relay = !Relay.IsAvailable;
        if ((!internet && !relay) || _signIn is { IsCompleted: false }) return;
        var signIn = SignInServicesAsync(name, internet, relay);
        _signIn = signIn;
        await signIn;
        try
        {
            // A lobby hosted before signing in is listed on the internet too now.
            if (_lobby is not null && _published is { } published) await Services.Lobbies.PublishAsync(published);
            else if (_lobby is not null && _publishWaiting) PublishHostedLobby();
        }
        catch (Exception e) { _internetError ??= e.GetBaseException().Message; }
        Changed?.Invoke();
    }

    /// <summary>The relay's identity token comes from the internet service where there is one, so it signs in after it.</summary>
    private async Task SignInServicesAsync(string name, bool internet, bool relay)
    {
        if (internet)
        {
            _internetError = null;
            try { await Internet!.SignInAsync(name); }
            catch (Exception e) { _internetError = e.GetBaseException().Message; }
        }
        if (relay)
        {
            _relayError = null;
            try { await Relay.SignInAsync(name); }
            catch (Exception e) { _relayError = e.GetBaseException().Message; }
        }
    }

    /// <summary>
    /// The player's cloud files (kept by the career service; what the online services kept before is moved there), or
    /// null when the player hasn't signed in or the services can't be reached.
    /// </summary>
    public async Task<ICloudStorage?> CloudAsync()
    {
        if (await IdTokenAsync() is null) return null;
        return new ServiceCloud(IdTokenAsync, Internet is ICloudStorage { IsReady: true } earlier ? earlier : null);
    }

    /// <summary>
    /// The player's ID token for the career service, signing in to the online services first when the player has an
    /// account (null: the player hasn't signed in, or the services can't be reached). Where the services' library isn't
    /// part of the build (a console), the token comes from their web interface for the Discord sign-in.
    /// </summary>
    public async Task<string?> IdTokenAsync()
    {
        if (Internet is null) return Account.HasDiscord ? await Eos.EosWebIdentity.IdTokenAsync() : null;
        if (Internet is not IIdentityTokens tokens || !(Account.HasDiscord || Account.UsesDeviceAccount)) return null;
        if (!Internet.IsAvailable)
        {
            SignIn(Settings.PlayerName);
            try { if (_signIn is { } signIn) await signIn; }
            catch (Exception) { /* the status says why */ }
        }
        if (!Internet.IsAvailable) return null;
        try { return await tokens.IdTokenAsync(); }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException) { return null; }
    }

    /// <summary>The hosted lobby as published: its invite code and what the browser shows.</summary>
    public LobbyListing? PublishedLobby => _published;

    /// <summary>Lists the hosted lobby in the browser and gives it an invite code (players connect through the service).</summary>
    private async void PublishHostedLobby()
    {
        if (_lobby is not { } lobby) return;
        _publishWaiting = !Services.IsAvailable; // published once the service signs in
        if (_publishWaiting) return;
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
        new(id, lobby.State.Seats.FirstOrDefault()?.Name is { Length: > 0 } host ? host : Settings.PlayerName, lobby.Settings.Format, lobby.Settings.Commander, lobby.Settings.Seats,
            lobby.State.Seats.Count(s => s.Kind == LobbySeatKind.Open), Version, ContentId, code, Listed: !_unlisted, lobby.Settings.Event)
        {
            // Players on the internet try the host directly first when its router lets them in (the relay otherwise;
            // the local network fills in its own port).
            Port = _upnp is not null ? _port : 0,
        };

    private async void UpdatePublishedLobby()
    {
        if (_lobby is not { } lobby || _published is not { } published) return;
        var now = ListingOf(lobby, published.LobbyId, published.InviteCode);
        if (lobby.Game is not null || lobby.Event is not null) now = now with { OpenSeats = 0 }; // started: no longer joinable
        if (now == published) return;
        _published = now;
        try { await Services.Lobbies.UpdateAsync(now); }
        catch (Exception) { /* the next change tries again */ }
    }

    /// <summary>Removing the listing in progress (waited for, briefly, when the game quits).</summary>
    private Task _unpublishing = Task.CompletedTask;

    private void UnpublishHostedLobby()
    {
        if (_published is not { } published) return;
        _published = null;
        _unpublishing = RemoveListingAsync(published.LobbyId);
    }

    private async Task RemoveListingAsync(string lobbyId)
    {
        try { await Services.Lobbies.RemoveAsync(lobbyId).ConfigureAwait(false); }
        catch (Exception) { /* the listing expires on its own */ }
    }

    /// <summary>Quitting: stops hosting and removes the listing, then closes the services, waiting a short while so a lobby on the internet is destroyed.</summary>
    private void ShutDownServices()
    {
        Leave();
        try { _unpublishing.Wait(ShutdownWait); }
        catch (Exception) { /* quitting anyway */ }
        var services = _services;
        _services = null;
        if (services is null) return;
        try { Task.Run(services.Dispose).Wait(ShutdownWait); }
        catch (Exception) { /* quitting anyway */ }
    }

    /// <summary>The longest quitting waits for the services, so closing the game never hangs.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    /// <summary>Lobbies the browser shows: same version and content, with free seats.</summary>
    public async Task<IReadOnlyList<LobbyListing>> SearchLobbiesAsync()
    {
        await SignedInAsync();
        return await Services.Lobbies.SearchAsync(new LobbyQuery(Version, ContentId));
    }

    /// <summary>Waits for a sign-in in progress, so a search right after opening the screen includes the internet.</summary>
    private async Task SignedInAsync()
    {
        if (_signIn is not { IsCompleted: false } signIn) return;
        try { await signIn; }
        catch (Exception) { /* searched without it */ }
    }

    /// <summary>Joins a lobby found by its invite code.</summary>
    public async void JoinByCode(string name, string code, DeckInfo? deck = null, bool seatFirst = true)
    {
        if (!InviteCode.TryParse(code, out var parsed))
        {
            Status?.Invoke($"An invite code has {InviteCode.Length} letters and digits, like {InviteCode.Display("ABCDEF")}.");
            return;
        }
        // The lobby of the game this device was in (no longer listed once it started): back into the seat.
        if (seatFirst && SavedRoute is { } saved && InviteCode.TryParse(saved.InviteCode, out var savedCode) && savedCode == parsed)
        {
            Rejoin(name, () => JoinByCode(name, code, deck, seatFirst: false));
            return;
        }
        Status?.Invoke($"Looking for {InviteCode.Display(parsed)}…");
        await SignedInAsync();
        var listing = await Services.Lobbies.FindByCodeAsync(parsed);
        if (listing is null)
        {
            Status?.Invoke($"No open lobby has the code {InviteCode.Display(parsed)} ({Services.Name}).");
            return;
        }
        JoinListing(name, listing, deck);
    }

    /// <summary>Joins a lobby from the browser (or found by its code), reaching the host through the service's network.</summary>
    public async void JoinListing(string name, LobbyListing listing, DeckInfo? deck = null, bool seatFirst = true)
    {
        Leave();
        if (seatFirst && SavedRoute is { } saved && saved.LobbyId == listing.LobbyId)
        {
            Rejoin(name, () => JoinListing(name, listing, deck, seatFirst: false));
            return;
        }
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
            JoinLobby(connection, new ClientIdentity(name, "", Version, ContentId, OwnPlaymat));
            if (deck is not null) SubmitDeck(deck);
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
