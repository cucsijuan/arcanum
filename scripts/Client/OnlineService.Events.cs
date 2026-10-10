// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Net.Client;
using Arcanum.Net.Events;
using Arcanum.Net.Host;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;
using Godot;

namespace Arcanum.Client;

/// <summary>Online limited events: hosting one (draft or sealed), taking part, and the games of its rounds.</summary>
public partial class OnlineService
{
    private const string EventSavePath = "user://online/event.json";

    private sealed record EventSetup(LimitedMode Mode, LimitedSource Source, int BestOf);

    private EventSetup? _eventSetup;
    private EventHost? _resumedEvent;
    private EventClient? _eventClient;
    private ClientIdentity? _eventIdentity;
    private string? _gameToken;
    private ulong _lastEventSaveMs;

    /// <summary>The online event this device takes part in (the Limited screen shows it).</summary>
    public NetEventSession? EventSession { get; private set; }

    private EventHost? HostedEvent => _lobby?.Event ?? _resumedEvent;

    // ------------------------------------------------------------------ hosting

    /// <summary>Opens a lobby for a draft or sealed event; this device's player takes seat 0.</summary>
    public void HostEvent(string name, LimitedMode mode, LimitedSource source, int bestOf, int players, int port)
    {
        Leave();
        string what = $"{(mode == LimitedMode.Draft ? "Draft" : "Sealed")} · {source.Name}" + (bestOf > 1 ? " · best of three" : "");
        var settings = new LobbySettings("Limited", false, 20, players, Version, ContentId, Event: what);
        _lobby = new LobbyHost(settings, _ => new DeckCheck(Array.Empty<Arcanum.Engine.Cards.CardDefinition>(), null, null));
        _lobby.Changed += () => Changed?.Invoke();
        if (!Listen(port))
        {
            _lobby = null;
            return;
        }
        _lobby.AddListener(_tcp!);
        _lobby.AddListener(_loopback!);
        _eventSetup = new EventSetup(mode, source, bestOf);
        ListAddresses();
        OpenRouterPort();
        JoinLobby(_loopback!.Connect(), new ClientIdentity(name, _lobby.HostToken, Version, ContentId, OwnPlaymat));
        PublishHostedLobby();
    }

    private bool Listen(int port)
    {
        try
        {
            _tcp = new TcpConnectionListener(port);
        }
        catch (Exception e)
        {
            Status?.Invoke($"Can't listen on port {port}: {e.Message}");
            return false;
        }
        _port = _tcp.Port;
        _loopback = new InMemoryListener();
        return true;
    }

    private void StartEvent()
    {
        var setup = _eventSetup!;
        var seats = _lobby!.Setup;
        int seed = System.Environment.TickCount;
        var ev = LimitedService.NewEvent(setup.Mode, setup.Source,
            seats.Select(s => new EventSeat { Name = s.Name, IsHuman = !s.IsComputer }).ToList(), setup.BestOf, seed, out var error);
        if (ev is null)
        {
            Status?.Invoke(error ?? "The event can't start.");
            return;
        }
        var title = _lobby.State.Event ?? "Event";
        _lobby.StartEvent(_ => new EventHost(ev, seats.Select(s => s.IsComputer ? null : s.Token).ToList(), Rules(ev), EventOptions()) { Title = title });
        UpdatePublishedLobby(); // started: the listing has no free seats any more
    }

    private static EventRules Rules(LimitedEvent ev)
    {
        var cards = App.Instance.Cards!;
        return new EventRules
        {
            Boosters = opened => LimitedService.BoosterSource(ev, opened, new Random(ev.Seed ^ 0x5eed)),
            Option = LimitedService.Option,
            AutoBuild = pool => LimitedService.AutoBuild(pool, ev.Source),
            CheckDeck = (deck, pool) => DeckValidator.ValidateLimited(deck, LimitedService.Format, pool, cards)
                .FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message,
            Cards = deck => deck.Resolve(cards).Item1,
            Strength = LimitedService.DeckStrength,
        };
    }

    private EventOptions EventOptions() => new()
    {
        Games = new HostOptions { ComputerPace = EventPaceAsync, Version = Version, Content = ContentId, DecisionTime = DecisionTime,
            CardNames = App.Instance?.Cards?.Names, NonbasicLandNames = App.Instance?.Cards?.NonbasicLandNames,
            CreatureCardNames = App.Instance?.Cards?.CreatureCardNames },
    };

    /// <summary>Several games run at once: the computer pauses briefly, without waiting for this screen's animations.</summary>
    private static async Task EventPaceAsync()
    {
        if (Godot.Engine.GetMainLoop() is not SceneTree tree) return;
        await tree.ToSignal(tree.CreateTimer(0.55 * UI.Board.BoardStyle.AnimationScale), SceneTreeTimer.SignalName.Timeout);
    }

    /// <summary>An event this device hosted was interrupted and can be resumed.</summary>
    public static bool HasSavedEvent => Godot.FileAccess.FileExists(EventSavePath);

    private void SaveHostedEvent()
    {
        if (HostedEvent is not { } host || Time.GetTicksMsec() - _lastEventSaveMs < 2000) return;
        _lastEventSaveMs = Time.GetTicksMsec();
        var path = ProjectSettings.GlobalizePath(EventSavePath);
        if (host.Event.Stage == EventStage.Finished)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var root = new JsonObject { ["port"] = _port, ["host"] = JsonNode.Parse(host.Save()) };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", root.ToJsonString());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Hosts the saved event again; players get back in with their seats.</summary>
    public bool ResumeSavedEvent(string name)
    {
        Leave();
        EventHost host;
        int port;
        string token;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(EventSavePath)))!;
            port = root["port"]?.GetValue<int>() ?? DefaultPort;
            var saved = root["host"]!;
            var ev = LimitedEvent.FromJson(saved["event"]!.ToJsonString());
            token = saved["tokens"]![0]!.GetValue<string>();
            host = EventHost.Restore(saved.ToJsonString(), Rules(ev), EventOptions());
        }
        catch (Exception e)
        {
            Status?.Invoke($"The saved event can't be resumed: {e.Message}");
            return false;
        }
        if (!Listen(port) && !Listen(0)) return false;
        host.AddListener(_tcp!);
        host.AddListener(_loopback!);
        _resumedEvent = host;
        ListAddresses();
        OpenRouterPort();
        StartEventSession(new EventClient(_loopback!.Connect(), new ClientIdentity(name, token, Version, ContentId, OwnPlaymat), greet: true));
        return true;
    }

    // ------------------------------------------------------------------ taking part

    private void StartEventSession(EventClient client)
    {
        _eventClient = client;
        _eventIdentity = client.Identity;
        if (!IsHosting)
        {
            Settings.Current.LastSeatToken = client.Identity.Token;
            Settings.Current.LastSeatIsEvent = true;
            RememberRoute();
            Settings.Save();
        }
        EventSession = new NetEventSession(client);
        Wire(client);
        Changed?.Invoke();
        App.Instance.GoTo(App.LimitedScene);
    }

    private void Wire(EventClient client)
    {
        client.Changed += OnEventInfo;
        client.Rejected += reason => Status?.Invoke(reason);
        client.Disconnected += () => _nextRetry = DateTime.UtcNow + RetryEvery;
    }

    private void OnEventInfo(EventInfo info)
    {
        if (info.Stage == EventStage.Finished && !IsHosting) ForgetSeat();
        if (info.GameToken is not { } token || token == _gameToken) return;
        // A game of this player's match begins: join it on its own connection.
        _gameToken = token;
        Session = null;
        _gameClient?.Close();
        _gameClient = null;
        OpenEventGame(token);
    }

    private async void OpenEventGame(string token)
    {
        var identity = new ClientIdentity(_eventIdentity!.Name, token, Version, ContentId, OwnPlaymat);
        try
        {
            var connection = _loopback is not null && IsHosting
                ? _loopback.Connect()
                : await ConnectToHostAsync(TimeSpan.FromSeconds(8));
            StartGameClient(connection, identity, eventGame: true);
        }
        catch (Exception e)
        {
            _gameToken = null; // tried again with the next update
            Status?.Invoke($"Couldn't join your game: {e.Message}");
        }
    }

    /// <summary>Back to the game of this player's match that is being played.</summary>
    public void ReturnToGame()
    {
        if (Session is not null) App.Instance.GoTo(App.GameBoardScene);
        else if (EventSession?.Info?.GameToken is { } token)
        {
            _gameToken = token;
            OpenEventGame(token);
        }
    }

    /// <summary>
    /// The game screen closed. In an event, the game goes on without this screen (the player can return to it) and
    /// the event stays; otherwise this leaves the online game.
    /// </summary>
    public void LeaveGame()
    {
        if (EventSession is null)
        {
            Leave();
            return;
        }
        if (Session?.Client.View is { IsGameOver: true })
        {
            Session = null;
            _gameClient?.Close();
            _gameClient = null;
        }
    }

    /// <summary>The game is over: back to the event.</summary>
    public void BackToEvent()
    {
        Session = null;
        _gameClient?.Close();
        _gameClient = null;
        App.Instance.GoTo(App.LimitedScene);
    }

    /// <summary>A joined player lost the event's connection: try again with the seat's token.</summary>
    private async void ReconnectEvent()
    {
        if (_connecting || _eventIdentity is null || IsHosting || EventSession is null) return;
        _connecting = true;
        try
        {
            var connection = await ConnectToHostAsync(TimeSpan.FromSeconds(5));
            if (EventSession is null) { connection.Close(); return; }
            var client = new EventClient(connection, _eventIdentity, greet: true);
            _eventClient = client;
            Wire(client);
            EventSession.Attach(client);
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

    /// <summary>Gets back into the event this device had joined.</summary>
    private async Task RejoinEvent(string name)
    {
        if (await ReconnectAsync(name) is not { } connection) return;
        StartEventSession(new EventClient(connection, new ClientIdentity(name, Settings.Current.LastSeatToken, Version, ContentId, OwnPlaymat), greet: true));
    }
}
