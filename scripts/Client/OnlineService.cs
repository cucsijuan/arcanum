// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Engine;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Net.Client;
using Arcanum.Net.Host;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// Online play with a direct connection: hosting (a lobby, then the game, for players who connect to this device's
/// address) or joining someone else's game. Lives as long as the app, so the game keeps running between screens;
/// a joined player whose connection drops keeps trying to get back in.
/// </summary>
public partial class OnlineService : Node
{
    public const int DefaultPort = 47013;
    private const string SavePath = "user://online/hosted.json";
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(3);

    // Hosting
    private LobbyHost? _lobby;
    private TcpConnectionListener? _tcp;
    private InMemoryListener? _loopback;
    private HostedSave? _save;
    private int _savedAnswers = -1;
    private ulong _lastSaveMs;
    private Upnp? _upnp;

    // This device's player (also when hosting)
    private LobbyClient? _lobbyClient;
    private GameClient? _gameClient;
    private string _address = "";
    private int _port;
    private DateTime _nextRetry;
    private bool _connecting;

    public NetSession? Session { get; private set; }
    public LobbyClient? Lobby => _lobbyClient;
    public LobbyHost? HostedLobby => _lobby;
    public bool IsHosting => _lobby is not null || _resumed is not null || _resumedEvent is not null;

    /// <summary>This device runs the game (it can hand dropped players' seats to the computer).</summary>
    public bool IsHostingGame => HostedGame is not null;
    public bool IsActive => _lobbyClient is not null || _gameClient is not null || _lobby is not null || _eventClient is not null;

    /// <summary>Addresses to give the other players (local network, and the public one when the router allows it).</summary>
    public List<string> ShareAddresses { get; } = new();

    /// <summary>Something to tell the player (connection refused, router setup...).</summary>
    public event Action<string>? Status;

    /// <summary>The lobby changed, or the game started (see <see cref="Session"/>).</summary>
    public event Action? Changed;

    public static string Version => (string)ProjectSettings.GetSetting("application/config/version");

    /// <summary>The playmat this player shows the others: their own built-in one (a custom image stays on this device).</summary>
    private static string? OwnPlaymat => Settings.Current.Playmats.ElementAtOrDefault(0) is { } id && !id.StartsWith("custom:") ? id : null;

    public static string ContentId => App.Instance.Module?.Manifest.Id ?? "generic";

    // ------------------------------------------------------------------ hosting

    /// <summary>Opens a lobby on <paramref name="port"/>; this device's player takes seat 0 (their deck is chosen in the lobby, or given here).</summary>
    public void Host(string name, FormatRules format, int players, int port, DeckInfo? deck = null)
    {
        Leave();
        var settings = new LobbySettings(format.Name, format.Commander, format.StartingLife, players, Version, ContentId);
        _lobby = new LobbyHost(settings, list => CheckDeck(list, format));
        _hostFormat = format;
        _lobby.Changed += () => Changed?.Invoke();
        try
        {
            _tcp = new TcpConnectionListener(port);
        }
        catch (Exception e)
        {
            _lobby = null;
            Status?.Invoke($"Can't listen on port {port}: {e.Message}");
            return;
        }
        _port = _tcp.Port;
        _loopback = new InMemoryListener();
        _lobby.AddListener(_tcp);
        _lobby.AddListener(_loopback);
        _save = new HostedSave { Format = format.Id, Port = _port };
        ListAddresses();
        OpenRouterPort();
        JoinLobby(_loopback.Connect(), new ClientIdentity(name, _lobby.HostToken, Version, ContentId, OwnPlaymat));
        if (deck is not null) SubmitDeck(deck);
        PublishHostedLobby();
    }

    private FormatRules? _hostFormat;

    /// <summary>
    /// The computer takes every free seat of the lobby: in an event it opens or drafts its own cards; in a game it
    /// brings a playable deck of the game's format (a different one for each seat when there are enough).
    /// </summary>
    public void FillWithComputer()
    {
        if (_lobby is not { Game: null, Event: null } lobby) return;
        var free = Enumerable.Range(1, lobby.Settings.Seats - 1).Where(i => lobby.State.Seats[i].Kind == LobbySeatKind.Open).ToList();
        if (free.Count == 0) return;
        string Name(int seat) => lobby.Settings.Seats == 2 ? "Computer" : $"Computer {seat}";
        if (lobby.Settings.Event is not null)
        {
            foreach (var seat in free) lobby.SetComputer(seat, Name(seat), "", "");
            return;
        }
        var format = _hostFormat ?? FormatRules.Casual;
        var decks = App.Instance.Decks.List(App.Instance.Module)
            .Select(d => (Info: d, List: App.Instance.Decks.Load(d).Deck.Export()))
            .Where(d => App.Instance.FormatById(d.Info.FormatId).Commander == format.Commander && CheckDeck(d.List, format).Problem is null)
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        if (decks.Count == 0)
        {
            Status?.Invoke($"No deck is playable in {format.Name} for the computer.");
            return;
        }
        for (int i = 0; i < free.Count; i++)
        {
            var (info, list) = decks[i % decks.Count];
            lobby.SetComputer(free[i], Name(free[i]), info.Name, list);
        }
    }

    /// <summary>Starts the hosted game with the lobby as it is.</summary>
    public void StartGame()
    {
        if (_lobby is null || _lobby.StartProblem is not null || _lobby.Game is not null || _lobby.Event is not null) return;
        if (_eventSetup is not null)
        {
            StartEvent();
            return;
        }
        ulong seed = (ulong)Random.Shared.NextInt64();
        _save!.Seed = seed;
        _save.Seats = _lobby.Setup.ToList();
        _lobby.Start(seed, HostOptions());
        UpdatePublishedLobby(); // started: the listing has no free seats any more
    }

    private HostOptions HostOptions() => new()
    {
        ComputerPace = ComputerPaceAsync,
        DecisionTime = DecisionTime,
        CardNames = App.Instance?.Cards?.Names,
        NonbasicLandNames = App.Instance?.Cards?.NonbasicLandNames,
        CreatureCardNames = App.Instance?.Cards?.CreatureCardNames,
    };

    /// <summary>The decision time limit chosen in the settings (null: no limit).</summary>
    private static TimeSpan? DecisionTime => Settings.Current.DecisionSeconds > 0 ? TimeSpan.FromSeconds(Settings.Current.DecisionSeconds) : null;

    private async Task ComputerPaceAsync()
    {
        if (Godot.Engine.GetMainLoop() is not SceneTree tree) return;
        if (Session?.Presentation is { } presenting) await presenting();
        await tree.ToSignal(tree.CreateTimer(0.55 * UI.Board.BoardStyle.AnimationScale), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>A hosted game that was interrupted (this device closed or crashed) and can be resumed.</summary>
    public static bool HasSavedGame => Godot.FileAccess.FileExists(SavePath);

    /// <summary>Hosts the saved game again: players get back in with their seats, and the game goes on where it stopped.</summary>
    public bool ResumeSavedGame()
    {
        Leave();
        HostedSave? save;
        try
        {
            save = JsonSerializer.Deserialize<HostedSave>(File.ReadAllText(ProjectSettings.GlobalizePath(SavePath)));
        }
        catch (Exception e)
        {
            Status?.Invoke($"The saved game can't be read: {e.Message}");
            return false;
        }
        if (save is null || save.Seats.Count < 2) return false;
        var format = App.Instance.FormatById(save.Format);
        var seats = new List<HostSeat>();
        foreach (var seat in save.Seats)
        {
            var check = CheckDeck(seat.DeckList, format);
            if (check.Problem is not null && check.Deck.Count == 0)
            {
                Status?.Invoke($"The saved game can't be resumed: {seat.Name}'s deck can't be read.");
                return false;
            }
            seats.Add(new HostSeat(seat.Name, check.Deck, check.Commanders, seat.IsComputer, seat.IsComputer ? null : seat.Token));
        }
        var options = HostOptions() with { Version = Version, Content = ContentId, Replay = save.Answers };
        // The same config as when the game started (LobbyHost.Start), so the recorded answers replay the same game.
        var config = new GameConfig
        {
            Seed = save.Seed, StartingLife = format.StartingLife, Commander = format.Commander ? new CommanderRules() : null, CardNames = options.CardNames, NonbasicLandNames = options.NonbasicLandNames,
            CreatureCardNames = options.CreatureCardNames,
        };
        var game = new GameHost(config, seats, options);
        try
        {
            _tcp = new TcpConnectionListener(save.Port);
        }
        catch
        {
            _tcp = new TcpConnectionListener(0);
            Status?.Invoke($"Port {save.Port} is busy: players must join on port {_tcp.Port}.");
        }
        _port = _tcp.Port;
        _loopback = new InMemoryListener();
        game.AddListener(_tcp);
        game.AddListener(_loopback);
        _save = save;
        _savedAnswers = save.Answers.Count;
        _resumed = game;
        ListAddresses();
        OpenRouterPort();
        game.Start();
        StartGameClient(_loopback.Connect(), new ClientIdentity(save.Seats[0].Name, save.Seats[0].Token, Version, ContentId, OwnPlaymat));
        return true;
    }

    private GameHost? _resumed;

    private GameHost? HostedGame => _lobby?.Game ?? _resumed;

    /// <summary>Gives a disconnected player's seat to the computer now.</summary>
    public void ReplaceWithComputer(PlayerId seat) => HostedGame?.ReplaceWithComputer(seat);

    private DeckCheck CheckDeck(string list, FormatRules format)
    {
        if (App.Instance.Cards is not { } cards) return new DeckCheck(Array.Empty<Arcanum.Engine.Cards.CardDefinition>(), null, "The host has no card data.");
        DeckList deck;
        try
        {
            deck = DeckList.Parse(list);
        }
        catch (Exception e)
        {
            return new DeckCheck(Array.Empty<Arcanum.Engine.Cards.CardDefinition>(), null, $"Unreadable deck list: {e.Message}");
        }
        var (definitions, unknown) = deck.Resolve(cards);
        var commanders = format.Commander ? deck.ResolveCommanders(cards) : null;
        if (!format.Commander) definitions.AddRange(deck.ResolveCommanders(cards));
        string? problem = unknown.Count > 0 ? $"Cards the host doesn't know: {string.Join(", ", unknown.Take(3))}" : null;
        problem ??= DeckValidator.Validate(deck, format, cards).FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
        return new DeckCheck(definitions, commanders, problem);
    }

    private void ListAddresses()
    {
        ShareAddresses.Clear();
        foreach (var ip in IP.GetLocalAddresses().Where(a => !a.Contains(':') && !a.StartsWith("127.") && !a.StartsWith("169.254.")))
            ShareAddresses.Add($"{ip}:{_port} (local network)");
    }

    /// <summary>Asks the router to forward the port (UPnP), so players outside the local network can join.</summary>
    private void OpenRouterPort()
    {
        int port = _port;
        Task.Run(() =>
        {
            var upnp = new Upnp();
            string message;
            if (upnp.Discover(2000, 2, "") == (int)Upnp.UpnpResult.Success && upnp.GetGateway() is { } gateway && gateway.IsValidGateway()
                && upnp.AddPortMapping(port, port, "Arcanum", "TCP", 0) == (int)Upnp.UpnpResult.Success)
            {
                _upnp = upnp;
                var external = upnp.QueryExternalAddress();
                message = $"{external}:{port} (internet)";
                Callable.From(() =>
                {
                    ShareAddresses.Add(message);
                    Changed?.Invoke();
                }).CallDeferred();
            }
            else
            {
                Callable.From(() => Status?.Invoke(
                    $"The router didn't open port {port} automatically. Players outside your network need you to forward TCP port {port} to this device.")).CallDeferred();
            }
        });
    }

    // ------------------------------------------------------------------ joining

    /// <summary>
    /// Joins the game hosted at <paramref name="address"/> ("host" or "host:port"); the deck is chosen in the lobby (or
    /// given here). The place of an unfinished game this device was playing gets it back into its seat instead.
    /// </summary>
    public async void Join(string name, string address, DeckInfo? deck = null, bool seatFirst = true)
    {
        Leave();
        if (!TryParseAddress(address, out var host, out int port))
        {
            Status?.Invoke("Enter the host's address, like 192.168.1.20:47013.");
            return;
        }
        if (seatFirst && CanRejoin && TryParseAddress(Settings.Current.LastHostAddress, out var savedHost, out int savedPort)
            && savedHost.Equals(host, StringComparison.OrdinalIgnoreCase) && savedPort == port)
        {
            Rejoin(name, () => Join(name, address, deck, seatFirst: false));
            return;
        }
        _address = host;
        _port = port;
        _route = null;
        Status?.Invoke($"Connecting to {host}:{port}…");
        try
        {
            var connection = await TcpConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(8));
            JoinLobby(connection, new ClientIdentity(name, "", Version, ContentId, OwnPlaymat));
            if (deck is not null) SubmitDeck(deck);
        }
        catch (Exception e)
        {
            Status?.Invoke($"Couldn't connect: {e.Message}");
        }
    }

    /// <summary>The unfinished game this device was in is offered to be got back into (unless the player said no to it).</summary>
    public static bool OffersRejoin => CanRejoin && Settings.Current.RejoinDeclined != Settings.Current.LastSeatToken;

    /// <summary>The player doesn't want to get back into that game: it isn't offered again (joining its lobby again still gets the seat back).</summary>
    public static void DeclineRejoin()
    {
        Settings.Current.RejoinDeclined = Settings.Current.LastSeatToken;
        Settings.Save();
    }

    /// <summary>The lobby of the unfinished game this device was in, when it was reached through the services.</summary>
    private static LobbyListing? SavedRoute => CanRejoin && LobbyRoute.TryLoad(Settings.Current.LastLobbyRoute, out var route) ? route : null;

    /// <summary>A game this device joined is still in progress (as far as it knows): it can get back in.</summary>
    public static bool CanRejoin => Settings.Current.LastSeatToken.Length > 0
        && (Settings.Current.LastHostAddress.Length > 0 || Settings.Current.LastLobbyRoute.Length > 0);

    /// <summary>The place the last joined game is at, for the button: the host's address, or the service it was found on.</summary>
    public static string RejoinPlace => LobbyRoute.TryLoad(Settings.Current.LastLobbyRoute, out var route)
        ? $"{route.HostName}'s lobby" + (route.Provider is { } provider ? $" ({provider})" : "")
        : Settings.Current.LastHostAddress;

    /// <summary>Remembers how the lobby just joined was reached: through the services, or by address.</summary>
    private void RememberRoute()
    {
        Settings.Current.LastLobbyRoute = _route is { } route ? LobbyRoute.Save(route) : "";
        Settings.Current.LastHostAddress = _route is not null ? "" : _address.Contains(':') ? $"[{_address}]:{_port}" : $"{_address}:{_port}";
    }

    /// <summary>A connection back to the lobby of the saved route: through the same service (the internet one signed in first), otherwise to the address.</summary>
    private async Task<IConnection?> ReconnectAsync(string name)
    {
        if (LobbyRoute.TryLoad(Settings.Current.LastLobbyRoute, out var route))
        {
            _route = route;
            _address = route.Address ?? "";
            _port = route.Port;
            SignIn(name);
            await SignedInAsync();
            return await ConnectToHostAsync(TimeSpan.FromSeconds(8));
        }
        _route = null;
        if (!TryParseAddress(Settings.Current.LastHostAddress, out var host, out int port)) return null;
        _address = host;
        _port = port;
        return await TcpConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Gets back into the game in progress this device had joined, with its seat's token. When the host doesn't know
    /// the seat any more (that game is over, another one is set up there), the seat is forgotten and
    /// <paramref name="otherwise"/> runs: joining that place as a new player.
    /// </summary>
    public async void Rejoin(string name, Action? otherwise = null)
    {
        Leave();
        if (!CanRejoin) return;
        Status?.Invoke($"Reconnecting to {RejoinPlace}…");
        try
        {
            if (Settings.Current.LastSeatIsEvent) { await RejoinEvent(name, otherwise); return; }
            var connection = await ReconnectAsync(name);
            if (connection is null) return;
            StartGameClient(connection, new ClientIdentity(name, Settings.Current.LastSeatToken, Version, ContentId, OwnPlaymat));
            if (otherwise is not null) _gameClient!.Rejected += _ => JoinAsNew(otherwise);
        }
        catch (Exception e)
        {
            _route = null;
            Status?.Invoke($"Couldn't connect: {e.Message}");
        }
    }

    /// <summary>The seat wasn't there any more: join the same place as a new player (once this connection is done with).</summary>
    private void JoinAsNew(Action join)
    {
        ForgetSeat();
        Status?.Invoke("That game is over: joining as a new player…");
        Callable.From(join).CallDeferred();
    }

    /// <summary>The game this device joined is over: nothing to get back into.</summary>
    public static void ForgetSeat()
    {
        if (Settings.Current.LastSeatToken.Length == 0) return;
        Settings.Current.LastSeatToken = "";
        Settings.Save();
    }

    public static bool TryParseAddress(string text, out string host, out int port)
    {
        text = text.Trim();
        host = text;
        port = DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon) // host:port (IPv6 addresses go in brackets: [::1]:47013)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        }
        else if (text.StartsWith('[') && text.Contains("]:"))
        {
            int close = text.IndexOf("]:", StringComparison.Ordinal);
            host = text[1..close];
            if (!int.TryParse(text[(close + 2)..], out port)) return false;
        }
        return host.Length > 0;
    }

    private void JoinLobby(IConnection connection, ClientIdentity identity)
    {
        _lobbyClient = new LobbyClient(connection, identity);
        _lobbyClient.Changed += () => Changed?.Invoke();
        _lobbyClient.Rejected += reason => Status?.Invoke(reason);
        _lobbyClient.Disconnected += () =>
        {
            if (!_lobbyClient!.Started) Status?.Invoke("Disconnected from the host.");
        };
        _lobbyClient.GameStarting += () =>
        {
            if (_lobbyClient.IsEvent) StartEventSession(_lobbyClient.JoinEvent());
            else StartGameClient(null, _lobbyClient.Identity);
        };
    }

    /// <summary>Sends the chosen deck to the host (again whenever it changes).</summary>
    public void SubmitDeck(DeckInfo deck)
    {
        if (_lobbyClient is null) return;
        var (list, _) = App.Instance.Decks.Load(deck);
        _lobbyClient.SubmitDeck(deck.Name, list.Export());
    }

    private ClientIdentity? _identity;

    /// <param name="eventGame">A game of an event: the event, not this game, is what a rejoin goes back to.</param>
    private void StartGameClient(IConnection? connection, ClientIdentity identity, bool eventGame = false)
    {
        _identity = identity;
        if (!IsHosting && !eventGame)
        {
            // Remembered so this player can get back in even if this device closes.
            Settings.Current.LastSeatToken = identity.Token;
            Settings.Current.LastSeatIsEvent = false;
            RememberRoute();
            Settings.Save();
        }
        var client = connection is null ? _lobbyClient!.JoinGame() : new GameClient(connection, identity);
        _gameClient = client;
        client.Rejected += reason =>
        {
            if (!eventGame) ForgetSeat(); // the seat is gone (game over or another game)
            Status?.Invoke(reason);
        };
        if (!eventGame) client.ViewChanged += view => { if (view.IsGameOver) ForgetSeat(); };
        void Ready(Arcanum.Engine.Views.GameView _)
        {
            client.ViewChanged -= Ready;
            if (Session is not null) return;
            Session = new NetSession(client, PolicyFromSettings(), Settings.Current.ConfirmManaPayment);
            Session.ConnectionChanged += connected => { if (!connected) _nextRetry = DateTime.UtcNow + RetryEvery; };
            Changed?.Invoke();
            App.Instance.GoTo(App.GameBoardScene);
        }
        client.ViewChanged += Ready;
    }

    private static AutoPassPolicy PolicyFromSettings()
    {
        var policy = new AutoPassPolicy();
        policy.OwnTurnStops.Clear();
        policy.OwnTurnStops.UnionWith(Settings.ParseSteps(Settings.Current.OwnTurnStops));
        policy.OpponentTurnStops.UnionWith(Settings.ParseSteps(Settings.Current.OpponentTurnStops));
        policy.FullControl = Settings.Current.FullControl;
        return policy;
    }

    /// <summary>A joined player lost the host: try the same address again with the seat's token.</summary>
    private async void Reconnect()
    {
        if (_connecting || _identity is null || IsHosting || Session?.Client.View is { IsGameOver: true }) return;
        _connecting = true;
        try
        {
            var connection = await ConnectToHostAsync(TimeSpan.FromSeconds(5));
            if (Session is null) { connection.Close(); return; }
            var client = new GameClient(connection, _identity);
            _gameClient = client;
            Session.Attach(client);
        }
        catch (Exception)
        {
            _nextRetry = DateTime.UtcNow + RetryEvery;
        }
        finally
        {
            _connecting = false;
        }
    }

    // ------------------------------------------------------------------ every frame

    public override void _Process(double delta)
    {
        _lobby?.Poll();
        _resumed?.Poll();
        _resumedEvent?.Poll();
        _lobbyClient?.Poll();
        _eventClient?.Poll();
        _gameClient?.Poll();
        if (Session is { IsConnected: false } && !_connecting && DateTime.UtcNow >= _nextRetry) Reconnect();
        if (EventSession is not null && _eventClient is { IsConnected: false } && !_connecting && DateTime.UtcNow >= _nextRetry) ReconnectEvent();
        SaveHostedGame();
        SaveHostedEvent();
    }

    /// <summary>Keeps the hosted game's answers on disk (a couple of times a second at most) so it can be resumed.</summary>
    private void SaveHostedGame()
    {
        if (HostedGame is not { } game || _save is null || Time.GetTicksMsec() - _lastSaveMs < 500) return;
        if (game.Answers.Count == _savedAnswers) return;
        _lastSaveMs = Time.GetTicksMsec();
        _savedAnswers = game.Answers.Count;
        _save.Answers = game.Answers.ToList();
        if (game.Game.State.IsGameOver)
        {
            DeleteSave();
            return;
        }
        var path = ProjectSettings.GlobalizePath(SavePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_save));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void DeleteSave()
    {
        var path = ProjectSettings.GlobalizePath(SavePath);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Leaves (or stops hosting) the current online game.</summary>
    public void Leave()
    {
        UnpublishHostedLobby();
        _eventClient?.Close();
        _eventClient = null;
        EventSession = null;
        _resumedEvent = null;
        _eventSetup = null;
        _gameToken = null;
        _lobbyClient?.Close();
        _gameClient?.Close();
        _tcp?.Dispose();
        _lobbyClient = null;
        _gameClient = null;
        _tcp = null;
        _loopback = null;
        _lobby = null;
        _route = null;
        _resumed = null;
        _save = null;
        _savedAnswers = -1;
        _identity = null;
        Session = null;
        ShareAddresses.Clear();
        if (_upnp is { } upnp)
        {
            int port = _port;
            _upnp = null;
            Task.Run(() => upnp.DeletePortMapping(port, "TCP"));
        }
    }

    public override void _ExitTree() => ShutDownServices();

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) ShutDownServices();
    }

    /// <summary>What a hosted game needs to be resumed.</summary>
    public sealed class HostedSave
    {
        public ulong Seed { get; set; }
        public string Format { get; set; } = "casual";
        public int Port { get; set; } = DefaultPort;
        public List<SeatSetup> Seats { get; set; } = new();
        public List<RecordedAnswer> Answers { get; set; } = new();
    }
}
