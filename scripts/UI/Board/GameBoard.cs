// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Rules;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Arcanum.Net.Protocol;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The game screen: two player areas split horizontally, turn/menu controls top-right,
/// card preview on hover, an action panel for the pending decision and a toggleable game log.
/// </summary>
public partial class GameBoard : Control
{
    /// <summary>The seat shown at the bottom: the person in front of the screen.</summary>
    private PlayerId Bottom => _session.LocalSeat;

    private IBoardSession _session = null!;
    /// <summary>Players who lost so far, and why (shown when the game ends).</summary>
    private readonly List<PlayerLost> _lost = new();
    /// <summary>One area per player: seat 0 across the bottom half, opponents side by side across the top half.</summary>
    private readonly List<PlayerArea> _areas = new();
    private readonly CardNode _preview = new();
    private readonly CardStatusPanel _status = new() { Visible = false };
    private readonly Label _turnNumber = BoardStyle.MakeLabel("1", 22, bold: true);
    private readonly Label _stepLabel = BoardStyle.MakeTitle("", 20);
    /// <summary>Online: time left for the pending decision before the computer makes it.</summary>
    private readonly Label _decisionTimer = BoardStyle.MakeLabel("", 14, BoardStyle.Attacking, bold: true);
    private readonly PanelContainer _actionPanel = new();
    private readonly Label _prompt = BoardStyle.MakeLabel("", 15);
    private readonly HBoxContainer _actionButtons = new();
    private readonly PanelContainer _logPanel = new();
    private readonly RichTextLabel _log = new();
    private readonly Label _logBadge = BoardStyle.MakeLabel("", 10, Colors.White);
    private readonly Control _gameOver = new();
    private readonly Label _gameOverText = BoardStyle.MakeTitle("", 42);
    private readonly Label _gameOverReasons = BoardStyle.MakeLabel("", 18, BoardStyle.TextDim);

    // A game that belongs to an event (limited): the result is reported once and the game ends with a way back.
    private MatchSetup? _match;
    private bool _resultReported;
    private Button? _newGameButton, _backToEventButton;
    private int _unreadLog;

    // In-progress choices for the pending decision.
    private readonly HashSet<CardId> _selected = new();
    private readonly List<int> _chosenModes = new();
    private int _number;
    private readonly Dictionary<CardId, PlayerId> _attackTargets = new(); // attacker -> player it attacks
    private PlayerId? _attackDefender;                                    // where newly picked attackers go
    private readonly Dictionary<CardId, CardId> _attackWalkers = new();   // attacker -> planeswalker it attacks
    private CardId? _attackWalker;                                        // planeswalker newly picked attackers go after
    private readonly HashSet<CardId> _attackGroup = new();                // picked since the last change of target
    private readonly Dictionary<CardId, CardId> _blocks = new(); // blocker -> attacker
    private CardId? _pendingBlocker;
    private readonly List<CardId> _pendingGroup = new();  // more tokens of the pending blocker's stack that block along with it
    private StackPicker? _picker;
    private readonly List<ManaTap> _staged = new();  // sources picked for an unconfirmed payment
    private ManaSourceOption? _comboSource;          // a source whose mana combination is being chosen
    private CardId? _abilityChoiceSource;              // permanent with several abilities waiting for a choice
    private readonly List<Arcanum.Engine.Abilities.Target> _chosenTargets = new(); // targets picked so far
    private readonly VBoxContainer _actionExtra = new();
    private readonly Dictionary<CardId, int> _damageSplit = new(); // blocker -> damage, for an unconfirmed assignment
    private int _damageToPlayer;                                   // trample damage to the defending player
    private readonly ArrowLayer _arrows = new();
    private readonly Announcer _announcer = new();
    private readonly TurnChime _turnChime = new();
    private readonly MulliganView _mulligan = new();
    private ulong _holdUntilMs;
    private readonly List<EventView> _pendingAttacks = new();
    private readonly List<EventView> _pendingBlocks = new();
    private bool _combatFlushQueued;
    private PlayerId? _handLookedAt;                    // whose hand another player is looking at right now
    private readonly PhaseBar _phaseBar = new();
    private readonly StackView _stackView = new();
    private Button _undoButton = null!;
    private DragState? _drag;

    /// <summary>A hand card held with the mouse: played on release unless dropped back onto the hand.</summary>
    private sealed class DragState
    {
        public required CardNode Node { get; init; }
        public required PlayerArea Area { get; init; }
        public required Vector2 Start { get; init; }
        public required PriorityDecision Decision { get; init; }
        public required PlayerAction Action { get; init; }
        public bool Dragging { get; set; }
    }

    private readonly Label _loading = BoardStyle.MakeLabel("", 22);
    private Button? _newGameItem;
    private readonly Control _menu = new();
    private Func<ulong, IBoardSession> _newSession = GameSession.CreateHotseatDemo;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildLayout();
        BuildMenu();

        // The board can open before card data is ready (e.g. launched directly); wait for it.
        _loading.SetAnchorsPreset(LayoutPreset.Center);
        _loading.GrowHorizontal = GrowDirection.Both;
        _loading.GrowVertical = GrowDirection.Both;
        _loading.ZIndex = BoardStyle.Z.Loading;
        _loading.Text = App.Instance.ContentStatus;
        AddChild(_loading);
        void OnProgress(string message) => _loading.Text = message;
        App.Instance.ContentProgress += OnProgress;
        bool loaded = await App.Instance.ContentReady;
        App.Instance.ContentProgress -= OnProgress;
        _loading.Visible = false;

        if (App.Instance.Online.Session is { } online)
        {
            StartOnline(online);
            return;
        }

        var match = App.Instance.PendingMatch;
        if (match is not null) UseMatch(match);
        else if (loaded) UseModuleDecks();

        // ARCANUM_SEED makes the first game reproducible (debugging, screenshots).
        StartNewGame(ulong.TryParse(OS.GetEnvironment("ARCANUM_SEED"), out var seed) ? seed : (ulong)Time.GetTicksUsec());
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

    private Func<ulong, GameSession> MatchFactory(IReadOnlyList<GameSession.Seat> seats, int life, bool commander, Action<Arcanum.Engine.Game>? setup) =>
        seed =>
        {
            var session = GameSession.CreateMatch(seed, seats, setup, life, PolicyFromSettings(), Settings.Current.ConfirmManaPayment, commander);
            // The computer's hand stays hidden; with only people at the table it's the players' choice.
            session.RevealAll = !seats.Any(s => s.IsBot) && Settings.Current.RevealHandsInHotseat;
            return session;
        };

    private void UseMatch(MatchSetup match)
    {
        _match = match;
        var setup = match.Sandbox ? SandboxSetup() : null;
        _newSession = MatchFactory(match.Seats, match.StartingLife, match.Commander, setup);
    }

    /// <summary>Hotseat with the module's sample decks; falls back to generic cards if they can't be built.</summary>
    private void UseModuleDecks()
    {
        var module = App.Instance.Module!;
        var cards = App.Instance.Cards!;
        if (OS.GetEnvironment("ARCANUM_COMMANDER") == "1" && UseCommanderDecks(module, cards)) return;
        // ARCANUM_DECKS=first,second picks the module decks to play (quick testing without the menus).
        var chosen = OS.GetEnvironment("ARCANUM_DECKS");
        var names = chosen.Length > 0 ? chosen.Split(',').Select(n => n.Trim()).ToList() : module.DeckNames().Take(2).ToList();
        if (names.Count < 2) return;

        var seats = new List<GameSession.Seat>();
        foreach (var (name, index) in names.Select((n, i) => (n, i)))
        {
            var (deck, unknown) = Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(name)).Resolve(cards);
            if (unknown.Count > 0) GD.PushWarning($"Deck '{name}': unknown cards {string.Join(", ", unknown)}");
            if (deck.Count == 0) return;
            // ARCANUM_VS_BOT=1: the second seat is the computer (quick testing without the menus).
            bool bot = index == 1 && OS.GetEnvironment("ARCANUM_VS_BOT") == "1";
            seats.Add(new GameSession.Seat(bot ? "Computer" : Settings.Current.PlayerNames.ElementAtOrDefault(index) ?? $"Player {index + 1}", deck, bot));
        }
        var setup = OS.GetEnvironment("ARCANUM_SANDBOX") == "1" ? SandboxSetup() : null;
        _newSession = MatchFactory(seats, 20, commander: false, setup);
    }

    /// <summary>
    /// ARCANUM_COMMANDER=1 (with ARCANUM_PLAYERS=2..4, default 4): a commander game with the module's commander
    /// decks (ARCANUM_COMMANDER_DECKS=prefix picks other decks by name prefix); with ARCANUM_VS_BOT=1 every seat but the
    /// first is the computer. For quick testing without the menus.
    /// </summary>
    private bool UseCommanderDecks(Arcanum.Data.Modules.ContentModule module, Arcanum.Data.CardData.CardDatabase cards)
    {
        var prefix = OS.GetEnvironment("ARCANUM_COMMANDER_DECKS") is { Length: > 0 } p ? p : "commander";
        var names = module.DeckNames().Where(n => n.StartsWith(prefix)).ToList();
        int players = int.TryParse(OS.GetEnvironment("ARCANUM_PLAYERS"), out int n) ? Math.Clamp(n, 2, 4) : 4;
        if (names.Count == 0) return false;
        bool vsBot = OS.GetEnvironment("ARCANUM_VS_BOT") == "1";
        var seats = new List<GameSession.Seat>();
        for (int i = 0; i < players; i++)
        {
            var list = Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(names[i % names.Count]));
            var (deck, _) = list.Resolve(cards);
            var commanders = Arcanum.Data.Decks.DeckList.Definitions(cards, list.Commander);
            bool bot = vsBot && i > 0;
            seats.Add(new GameSession.Seat(bot ? $"Computer {i}" : $"Player {i + 1}", deck, bot, commanders));
        }
        _newSession = MatchFactory(seats, 40, commander: true, setup: null);
        return true;
    }

    /// <summary>
    /// ARCANUM_SANDBOX=1: start from a prepared board of the generic cards for manual testing of attachments, static
    /// abilities, targeted spells and activated abilities: player one has a creature carrying an aura and an equipment,
    /// a spare equipment, a creature with a tap ability and burn, pump and destroy in hand; player two has creatures, a
    /// creature-boosting enchantment and burn and bounce in hand; both have stacks of identical tokens. It needs no card module.
    /// </summary>
    private static Action<Arcanum.Engine.Game> SandboxSetup()
    {
        var p1 = new PlayerId(0);
        var p2 = new PlayerId(1);
        return game =>
        {
            void Put(PlayerId owner, Arcanum.Engine.Cards.CardDefinition d, int count = 1)
            {
                for (int i = 0; i < count; i++) game.SetupPermanent(owner, d);
            }

            Put(p1, Arcanum.Cards.GenericCards.Forest, 3);
            Put(p1, Arcanum.Cards.GenericCards.Mountain, 2);
            Put(p1, Arcanum.Cards.GenericCards.Swamp);
            var cub = game.SetupPermanent(p1, Arcanum.Cards.GenericCards.GladeCub);
            game.SetupPermanent(p1, Arcanum.Cards.GenericCards.StoneSkin, attachTo: cub);
            game.SetupPermanent(p1, Arcanum.Cards.GenericCards.IronBlade, attachTo: cub);
            Put(p1, Arcanum.Cards.GenericCards.IronBlade);
            Put(p1, Arcanum.Cards.GenericCards.SparkMage);
            Put(p1, Arcanum.Cards.GenericCards.HillBrute);
            // Identical tokens show as one stack with a count.
            var sprite = Arcanum.Cards.GenericCards.RiverScout with { Name = "Sprite", ManaCost = Arcanum.Engine.Mana.ManaCost.Zero, IsToken = true };
            var gem = new Arcanum.Engine.Cards.CardDefinition { Name = "Gem", Types = Arcanum.Engine.Cards.CardType.Artifact, IsToken = true };
            Put(p1, sprite, 4);
            Put(p1, gem, 3);
            game.SetupInHand(p1, Arcanum.Cards.GenericCards.EmberBolt);
            game.SetupInHand(p1, Arcanum.Cards.GenericCards.MightySurge);
            game.SetupInHand(p1, Arcanum.Cards.GenericCards.Rend);

            Put(p2, Arcanum.Cards.GenericCards.Mountain, 3);
            Put(p2, Arcanum.Cards.GenericCards.Island, 2);
            Put(p2, Arcanum.Cards.GenericCards.OgreBrute, 2);
            Put(p2, Arcanum.Cards.GenericCards.StoneElemental);
            Put(p2, sprite, 3);
            Put(p2, Arcanum.Cards.GenericCards.RallyBanner);
            game.SetupInHand(p2, Arcanum.Cards.GenericCards.EmberBolt);
            game.SetupInHand(p2, Arcanum.Cards.GenericCards.GustAway);
        };
    }

    private void StartNewGame(ulong seed) => StartSession(_newSession(seed));

    // ---------------------------------------------------------------- online

    private NetSession? _online;
    private bool _leavingToEvent;
    private readonly Dictionary<PlayerId, (SeatState State, DateTime? Deadline)> _seatStates = new();
    private readonly VBoxContainer _seatPanel = new();
    private readonly Label _connectionLost = BoardStyle.MakeLabel("Connection to the host lost — reconnecting…", 18, BoardStyle.Attacking);
    private ulong _nextSeatTick;

    /// <summary>A seat in a game run by a host: no new game or undo; who plays each seat is shown.</summary>
    private void StartOnline(NetSession online)
    {
        _online = online;
        if (_newGameItem is not null) _newGameItem.Visible = false;
        online.SeatChanged += OnSeatChanged;
        online.Notice += text => Arcanum.UI.Menu.MenuKit.Toast(this, text);
        online.ConnectionChanged += connected => _connectionLost.Visible = !connected;

        _connectionLost.Visible = false;
        _connectionLost.AnchorLeft = 0.5f; _connectionLost.AnchorRight = 0.5f;
        _connectionLost.GrowHorizontal = GrowDirection.Both;
        _connectionLost.OffsetTop = 14;
        _connectionLost.ZIndex = BoardStyle.Z.Menu - 1;
        AddChild(_connectionLost);
        _seatPanel.AnchorLeft = 0.5f; _seatPanel.AnchorRight = 0.5f;
        _seatPanel.GrowHorizontal = GrowDirection.Both;
        _seatPanel.OffsetTop = 44;
        _seatPanel.ZIndex = BoardStyle.Z.Menu - 1;
        AddChild(_seatPanel);
        StartSession(online);
        foreach (var status in online.Seats.Values) OnSeatChanged(status);
    }

    private void OnSeatChanged(SeatStatus status)
    {
        DateTime? deadline = status is { State: SeatState.Disconnected, SecondsLeft: >= 0 } ? DateTime.UtcNow.AddSeconds(status.SecondsLeft) : null;
        _seatStates[status.Seat] = (status.State, deadline);
        UpdateSeatNotes();
    }

    /// <summary>Shows who plays each seat beside its name, and lets the host give a dropped player's seat to the computer.</summary>
    private void UpdateSeatNotes()
    {
        if (_online is null) return;
        foreach (var child in _seatPanel.GetChildren()) child.QueueFree();
        foreach (var (seat, (state, deadline)) in _seatStates)
        {
            if (seat.Value >= _areas.Count) continue;
            string? note = state switch
            {
                SeatState.Computer => "\U0001F916 computer",
                SeatState.Disconnected when deadline is { } d => $"disconnected · computer in {Math.Max(0, (int)(d - DateTime.UtcNow).TotalSeconds) / 60}:{Math.Max(0, (int)(d - DateTime.UtcNow).TotalSeconds) % 60:00}",
                SeatState.Disconnected => "disconnected",
                _ => null,
            };
            _areas[seat.Value].SeatNote = note;
            if (state == SeatState.Disconnected && App.Instance.Online.IsHostingGame)
            {
                var name = _session.ViewFor(Bottom).Players[seat.Value].Name;
                var button = BoardStyle.MakeButton($"\U0001F916 Let the computer play for {name} now", 14);
                var target = seat;
                button.Pressed += () => App.Instance.Online.ReplaceWithComputer(target);
                _seatPanel.AddChild(button);
            }
        }
        if (_online.Client.View is not null) Refresh();
    }

    private void StartSession(IBoardSession session)
    {
        _session?.Leave();
        _session = session;
        _lost.Clear();
        _announcer.Clear();
        _holdUntilMs = 0;
        session.Presentation = PresentationAsync;
        ConfigureAreas(session.PlayerCount);
        _session.Changed += Refresh;
        _session.Failed += e => ShowGameOver($"Engine error:\n{e.Message}");
        _session.EventRaised += OnGameEvent;
        _phaseBar.Bind(session.Policy);
        _log.Clear();
        _unreadLog = 0;
        UpdateLogBadge();
        _gameOver.Visible = false;
        _lastDecision = null;
        _session.Start();
    }

    /// <summary>Test-mode undo: rebuild the game up to the last decision a person made and ask it again.</summary>
    private void Undo()
    {
        if (_match?.Event is not null) return; // no take-backs in event games
        if (_session is GameSession local && local.CreateUndo() is { } previous) StartSession(previous);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        AddChild(new ColorRect { Color = BoardStyle.Background, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });


        _arrows.ZIndex = BoardStyle.Z.Arrows;
        AddChild(_arrows);
        _mulligan.ZIndex = BoardStyle.Z.Mulligan;
        _mulligan.CardClicked += OnCardClicked;
        _mulligan.CardHoverStarted += ShowPreview;
        _mulligan.CardHoverEnded += HidePreview;
        AddChild(_mulligan);
        _announcer.ZIndex = BoardStyle.Z.Announcer;
        _announcer.ArrowsChanged += arrows => _arrows.SetOverlayArrows(arrows);
        AddChild(_announcer);
        AddChild(_turnChime);

        // Top-right: menu, turn counter, log toggle.
        var corner = new VBoxContainer { AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -62, OffsetTop = 14, OffsetRight = -14 };
        corner.AddThemeConstantOverride("separation", 8);
        var menu = BoardStyle.MakeButton("≡", 22, compact: true);
        BoardStyle.RaiseLabel(menu, 3);
        menu.CustomMinimumSize = new Vector2(48, 40);
        menu.Pressed += () => _menu.Visible = true;
        menu.TooltipText = "Menu";
        corner.AddChild(menu);

        var turnBox = new PanelContainer { CustomMinimumSize = new Vector2(48, 48) };
        turnBox.AddThemeStyleboxOverride("panel", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
        var turnStack = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        turnStack.AddThemeConstantOverride("separation", -4);
        var turnTitle = BoardStyle.MakeLabel("TURN", 9, BoardStyle.TextDim);
        turnTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _turnNumber.HorizontalAlignment = HorizontalAlignment.Center;
        turnStack.AddChild(turnTitle);
        turnStack.AddChild(_turnNumber);
        turnBox.AddChild(turnStack);
        corner.AddChild(turnBox);

        _undoButton = BoardStyle.MakeButton("↶", 22, compact: true);
        BoardStyle.RaiseLabel(_undoButton, 4);
        _undoButton.CustomMinimumSize = new Vector2(48, 40);
        _undoButton.TooltipText = "Undo (Ctrl+Z)";
        _undoButton.Pressed += Undo;
        corner.AddChild(_undoButton);

        var logToggle = BoardStyle.MakeButton("▤", 18, compact: true);
        BoardStyle.RaiseLabel(logToggle, 1);
        logToggle.CustomMinimumSize = new Vector2(48, 40);
        logToggle.TooltipText = "Game log";
        logToggle.Pressed += ToggleLog;
        corner.AddChild(logToggle);
        AddChild(corner);

        // Phase bar on the divider, left of the top player's hand.
        // Just below the divider, so it never covers an opponent's hand (they sit along the divider's top side).
        _phaseBar.AnchorTop = 0.5f; _phaseBar.AnchorBottom = 0.5f;
        _phaseBar.OffsetLeft = 110;
        _phaseBar.OffsetTop = 8;
        _phaseBar.GrowVertical = GrowDirection.End;
        _phaseBar.ZIndex = BoardStyle.Z.PhaseBar;
        AddChild(_phaseBar);

        // Stack: right of center, clear of the zone piles.
        // In the local player's half (left, under the phase bar): same place for any number of players.
        _stackView.AnchorTop = 0.5f; _stackView.AnchorBottom = 0.5f;
        _stackView.OffsetLeft = 120; _stackView.OffsetTop = 120;
        _stackView.ZIndex = BoardStyle.Z.Stack;
        _stackView.CardHoverStarted += ShowPreview;
        _stackView.CardHoverEnded += HidePreview;
        _stackView.ItemClicked += OnStackItemClicked;
        AddChild(_stackView);

        // Action panel: right side of the bottom half, clear of both players' zone piles.
        _actionPanel.AnchorLeft = 1; _actionPanel.AnchorRight = 1; _actionPanel.AnchorTop = 0.75f; _actionPanel.AnchorBottom = 0.75f;
        _actionPanel.GrowHorizontal = GrowDirection.Begin;
        _actionPanel.ZIndex = BoardStyle.Z.ActionPanel;
        _actionPanel.GrowVertical = GrowDirection.Both;
        _actionPanel.OffsetRight = -16;
        _actionPanel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Modal, new Color(0.06f, 0.06f, 0.07f, 0.92f), 10, BoardStyle.PanelBorder, 1, artPadding: 14));
        var actionBox = new VBoxContainer();
        actionBox.AddThemeConstantOverride("separation", 6);
        _stepLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        _actionButtons.Alignment = BoxContainer.AlignmentMode.Center;
        _actionButtons.AddThemeConstantOverride("separation", 8);
        _decisionTimer.HorizontalAlignment = HorizontalAlignment.Center;
        _decisionTimer.Visible = false;
        actionBox.AddChild(_decisionTimer);
        actionBox.AddChild(_stepLabel);
        actionBox.AddChild(_prompt);
        _actionExtra.AddThemeConstantOverride("separation", 6);
        actionBox.AddChild(_actionExtra);
        actionBox.AddChild(_actionButtons);
        _actionPanel.AddChild(actionBox);
        AddChild(_actionPanel);

        // Bottom-left log button with unread badge.
        var logButton = BoardStyle.MakeButton("Log", 13, compact: true);
        logButton.AnchorTop = 1; logButton.AnchorBottom = 1;
        logButton.OffsetLeft = 14; logButton.OffsetTop = -54; logButton.OffsetRight = 54; logButton.OffsetBottom = -14;
        logButton.Pressed += ToggleLog;
        var badge = new Panel { Position = new Vector2(26, -8), Size = new Vector2(20, 18), MouseFilter = MouseFilterEnum.Ignore, Name = "Badge" };
        badge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("b3261e"), 9));
        _logBadge.HorizontalAlignment = HorizontalAlignment.Center;
        _logBadge.Size = badge.Size;
        badge.AddChild(_logBadge);
        logButton.AddChild(badge);
        AddChild(logButton);

        _logPanel.AnchorTop = 1; _logPanel.AnchorBottom = 1;
        _logPanel.OffsetLeft = 14; _logPanel.OffsetTop = -420; _logPanel.OffsetRight = 384; _logPanel.OffsetBottom = -64;
        _logPanel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Board, new Color(0.06f, 0.06f, 0.07f, 0.95f), 8, BoardStyle.PanelBorder, 1, artPadding: 8));
        _log.ScrollFollowing = true;
        _log.BbcodeEnabled = true;
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        _log.AddThemeColorOverride("default_color", BoardStyle.Text);
        _logPanel.AddChild(_log);
        _logPanel.Visible = false;
        _logPanel.ZIndex = BoardStyle.Z.Log;
        AddChild(_logPanel);

        // Hover preview on the left edge.
        _preview.MouseFilter = MouseFilterEnum.Ignore;
        _preview.SharpImage = true;
        _preview.ShowCounterBadge = false;
        _preview.Size = BoardStyle.PreviewSize;
        _preview.Visible = false;
        _preview.ZIndex = BoardStyle.Z.Preview;
        AddChild(_preview);
        _status.ZIndex = BoardStyle.Z.Preview;
        AddChild(_status);

        // Game over overlay.
        _gameOver.SetAnchorsPreset(LayoutPreset.FullRect);
        _gameOver.Visible = false;
        _gameOver.ZIndex = BoardStyle.Z.GameOver;
        _gameOver.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.7f), AnchorRight = 1, AnchorBottom = 1 });
        var overBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, AnchorRight = 1, AnchorBottom = 1 };
        overBox.AddThemeConstantOverride("separation", 24);
        _gameOverText.HorizontalAlignment = HorizontalAlignment.Center;
        overBox.AddChild(_gameOverText);
        _gameOverReasons.HorizontalAlignment = HorizontalAlignment.Center;
        overBox.AddChild(_gameOverReasons);
        var again = BoardStyle.MakePrimaryButton("New game", 20);
        again.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        again.CustomMinimumSize = new Vector2(220, 52);
        again.Pressed += () => StartNewGame((ulong)Time.GetTicksUsec());
        overBox.AddChild(again);
        _newGameButton = again;
        var back = BoardStyle.MakePrimaryButton("Back to event", 20);
        back.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        back.CustomMinimumSize = new Vector2(220, 52);
        back.Visible = false;
        back.Pressed += () =>
        {
            if (_match?.Event is { } hook) App.Instance.GoTo(hook.ReturnScene);
            else if (App.Instance.Online.EventSession is not null) App.Instance.Online.BackToEvent();
        };
        overBox.AddChild(back);
        _backToEventButton = back;
        var toMenu = BoardStyle.MakeButton("Main menu", 18);
        toMenu.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        toMenu.CustomMinimumSize = new Vector2(220, 46);
        toMenu.Pressed += () => App.Instance.GoTo(App.MainMenuScene);
        overBox.AddChild(toMenu);
        _gameOver.AddChild(overBox);
        AddChild(_gameOver);

        UpdateLogBadge();
    }

    /// <summary>In-game menu opened from the ≡ button (or Escape).</summary>
    private void BuildMenu()
    {
        _menu.SetAnchorsPreset(LayoutPreset.FullRect);
        _menu.ZIndex = BoardStyle.Z.Menu;
        _menu.Visible = false;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), AnchorRight = 1, AnchorBottom = 1 };
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) _menu.Visible = false; };
        _menu.AddChild(shade);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        panel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Modal, BoardStyle.Panel, 12, BoardStyle.PanelBorder, 1, artPadding: 24));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var title = BoardStyle.MakeTitle("Menu", 24);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(title);
        box.AddChild(UiArt.Divider());
        Button Item(string text, Action action, bool primary = false)
        {
            var button = BoardStyle.MakeModalButton(text, primary, 20);
            button.CustomMinimumSize = new Vector2(280, 40);
            button.Pressed += () => { _menu.Visible = false; action(); };
            box.AddChild(button);
            return button;
        }
        Item("Resume", () => { }, primary: true);
        _newGameItem = Item("New game (same decks)", () => StartNewGame((ulong)Time.GetTicksUsec()));
        Item("Main menu", () => App.Instance.GoTo(App.MainMenuScene));
        panel.AddChild(box);
        _menu.AddChild(panel);
        AddChild(_menu);
    }

    // ---------------------------------------------------------------- refresh

    private PlayerArea AreaOf(PlayerId player) => _areas[player.Value];

    private CardNode? FindCard(CardId id) => _areas.Select(a => a.FindCard(id)).FirstOrDefault(n => n is not null);

    private PlayerId _areasBottom;

    /// <summary>
    /// (Re)creates the player areas for <paramref name="count"/> players: this screen's seat across the bottom half,
    /// the others in seat order (starting after it) across the top half.
    /// </summary>
    private void ConfigureAreas(int count)
    {
        if (_areas.Count == count && _areasBottom == Bottom) return;
        foreach (var old in _areas) old.QueueFree();
        _areas.Clear();
        _areasBottom = Bottom;
        int opponents = count - 1;
        for (int i = 0; i < count; i++)
        {
            int place = (i - Bottom.Value + count) % count; // 0: bottom, then left to right on top
            // The menu buttons stand at the right edge of the screen, over the top half's piles.
            var area = new PlayerArea { Player = new PlayerId(i), Compact = count > 2, RightInset = place == opponents ? 64 : 0 };
            if (place == 0)
            {
                area.AnchorTop = 0.5f; area.AnchorRight = 1; area.AnchorBottom = 1;
            }
            else
            {
                area.AnchorLeft = (place - 1) / (float)opponents; area.AnchorRight = place / (float)opponents; area.AnchorBottom = 0.5f;
                area.FacesDown = true;
            }
            area.CardClicked += OnCardClicked;
            area.StackMark = StackMark;
            area.ZoneClicked += OpenZone;
            area.PlayerClicked += OnPlayerClicked;
            area.CardHoverStarted += ShowPreview;
            area.CardHoverEnded += HidePreview;
            AddChild(area);
            MoveChild(area, 1 + i); // above the background, below arrows and overlays
            area.SetPlaymatStyle(Settings.Current.Playmats.ElementAtOrDefault(place == 0 ? 0 : 1) ?? "grid");
            _areas.Add(area);
        }
    }

    private void Refresh()
    {
        var decision = _session.CurrentDecision;
        if (decision is not null && !ReferenceEquals(decision, _lastDecision))
        {
            _selected.Clear();
            _attackTargets.Clear();
            _attackWalkers.Clear();
            _attackWalker = null;
            _attackGroup.Clear();
            _attackDefender = decision is AttackDecision ad ? ad.Defenders[0] : null;
            // Creatures that attack each combat if able start selected (their target can still be changed).
            if (decision is AttackDecision forced)
                foreach (var id in forced.PossibleAttackers.Where(id => _session.ViewFor(forced.Player).FindCard(id)?.AttacksEachCombat == true))
                {
                    _attackTargets[id] = AllowedDefender(forced, id, forced.Defenders[0]);
                    _attackGroup.Add(id);
                }
            _blocks.Clear();
            _pendingBlocker = null;
            _pendingGroup.Clear();
            ClosePicker();
            _abilityChoiceSource = null;
            _chosenTargets.Clear();
            _chosenModes.Clear();
            _number = decision is ChooseNumberDecision nd ? nd.Request.Max : 0;
            _staged.Clear();
            _comboSource = null;
            if (decision is ManaPaymentDecision pay) _staged.AddRange(pay.Request.SuggestedTaps);
            if (decision is DamageAssignmentDecision dmg) ResetDamage(dmg);
            _lastDecision = decision;
        }

        var view = _session.ViewFor(Bottom);
        var staged = _staged.Select(t => t.Source).ToHashSet();
        var attacking = view.Attacks.Select(a => a.Attacker).ToHashSet();
        if (decision is AttackDecision) attacking.UnionWith(_attackTargets.Keys);
        // The active player's side is laid out first so blockers can line up with where attackers will be.
        var activeArea = AreaOf(view.ActivePlayer);
        activeArea.Refresh(view, true, staged, attacking);
        var blockerAlign = new Dictionary<CardId, float>();
        void Align(CardId blocker, CardId attacker)
        {
            if (activeArea.TargetGlobalCenter(attacker) is { } center) blockerAlign[blocker] = center.X;
        }
        foreach (var attack in view.Attacks)
            foreach (var blocker in attack.Blockers) Align(blocker, attack.Attacker);
        if (decision is BlockDecision)
            foreach (var (blocker, attacker) in _blocks) Align(blocker, attacker);
        foreach (var area in _areas.Where(a => a != activeArea)) area.Refresh(view, false, staged, attacking, blockerAlign);
        _turnNumber.Text = Math.Max(1, view.TurnNumber).ToString();
        _stepLabel.Text = view.TurnNumber == 0 ? "Mulligan" : EventLogFormatter.StepName(view.Step);
        _phaseBar.SetCurrentStep(view.TurnNumber == 0 ? null : view.Step);
        _phaseBar.SetActivePlayer(view.TurnNumber == 0 ? null : view.Players[view.ActivePlayer.Value].Name, view.ActivePlayer == Bottom,
            BoardStyle.PlayerColor(view.ActivePlayer.Value));
        _stackView.Refresh(view.Stack);
        _undoButton.Disabled = !_session.CanUndo || _match?.Event is not null; // no take-backs in event games

        if (view.IsGameOver)
        {
            // Say why everyone else lost, so a sudden defeat is never a mystery.
            var winner = view.Winner is { } w ? view.Players[w.Value].Name + " wins!" : "Draw";
            var reasons = _lost.Select(l => $"{view.Players[l.Player.Value].Name} lost: {l.Reason}");
            if (_match?.Event is { } hook && !_resultReported)
            {
                _resultReported = true;
                hook.RecordWinner(view.Winner?.Value);
            }
            ShowGameOver(winner + "\n" + string.Join("\n", reasons));
            // Automatic play of a whole event (smoke tests): go back to the event on its own.
            if (_autoplay && _match?.Event is { } back)
                GetTree().CreateTimer(0.5).Timeout += () => App.Instance.GoTo(back.ReturnScene);
            else if (_autoplay && _online is not null && App.Instance.Online.EventSession is not null && !_leavingToEvent)
            {
                _leavingToEvent = true;
                GetTree().CreateTimer(0.5).Timeout += App.Instance.Online.BackToEvent;
            }
        }

        ApplyHighlights(view, decision);
        _zoneViewer.Refresh(view, id => UsableNow(decision, id));
        UpdateArrows(view, decision);
        BuildActionPanel(view, decision);
        UpdateMulliganView(decision);
        if (_autoplay && decision is not null)
        {
            // ARCANUM_AUTOPLAY=showcase[:DecisionType] freezes on the first decision of that type (default:
            // ManaPaymentDecision) so screenshots can capture it.
            if (_showcase is { } freezeOn && decision.GetType().Name == freezeOn)
            {
                GD.Print($"SHOWCASE frozen on {freezeOn}");
                if (decision is ManaPaymentDecision pay && FindCard(pay.Request.Source) is { } spellNode) ShowPreview(spellNode);
                return;
            }
            // ARCANUM_TEST_UNDO=1: every 7th manual decision, undo once first (exercises replay end to end).
            int manual = _session is GameSession { Log: var log } ? log.Entries.Count(e => e.Manual) : 0;
            if (_testUndo && manual > 0 && manual % 7 == 0 && _undoneAt.Add(manual))
            {
                GD.Print($"TEST_UNDO at {manual} manual decisions");
                GetTree().CreateTimer(0.2).Timeout += Undo;
                return;
            }
            GetTree().CreateTimer(0.35).Timeout += () => { if (!TestStackPick(decision)) AutoAnswer(decision, view); };
        }
    }

    /// <summary>Debug autopilot (ARCANUM_AUTOPLAY=1): plays both seats greedily for smoke tests and screenshots.</summary>
    private readonly bool _testUndo = OS.GetEnvironment("ARCANUM_TEST_UNDO") == "1";
    private readonly HashSet<int> _undoneAt = new();

    private static readonly string AutoplayEnv = OS.GetEnvironment("ARCANUM_AUTOPLAY");
    private readonly bool _autoplay = AutoplayEnv == "1" || AutoplayEnv.StartsWith("showcase");
    private readonly string? _showcase = AutoplayEnv.StartsWith("showcase")
        ? (AutoplayEnv.Contains(':') ? AutoplayEnv[(AutoplayEnv.IndexOf(':') + 1)..] : nameof(ManaPaymentDecision))
        : null;

    /// <summary>
    /// ARCANUM_TEST_STACKS=1 (with autoplay): attacks and blocks go through the same code as the stack picker, choosing
    /// half of every group of identical creatures, so smoke tests exercise stacks. Returns whether it handled the decision.
    /// ARCANUM_TEST_STACKS=picker stops on the first attack with identical creatures, with the picker open (screenshots).
    /// </summary>
    private static readonly string TestStacksEnv = OS.GetEnvironment("ARCANUM_TEST_STACKS");
    private readonly bool _testStacks = TestStacksEnv is "1" or "picker";

    private bool TestStackPick(Decision decision)
    {
        if (!_testStacks || decision.IsAnswered) return false;
        var view = _session.ViewFor(decision.Player);
        CardNode? NodeOf(CardId id) => view.FindCard(id) is { } card ? AreaOf(card.Controller).FindCard(id) : null;
        switch (decision)
        {
            case AttackDecision a:
            {
                var done = new HashSet<CardId>();
                foreach (var id in a.PossibleAttackers)
                {
                    if (!done.Add(id) || NodeOf(id) is not { } node) continue;
                    var same = SameKindAmong(node, a.PossibleAttackers);
                    if (same.Count > 1 && TestStacksEnv == "picker")
                    {
                        OpenAttackPicker(a, same, node.View?.Name);
                        return true;
                    }
                    if (same.Count > 1)
                    {
                        done.UnionWith(same);
                        ApplyAttackCount(a, same, (same.Count + 1) / 2);
                        GD.Print($"STACKTEST attack with {same.Count - same.Count / 2} of {same.Count} {node.View?.Name}");
                    }
                    else _attackTargets[id] = AllowedDefender(a, id, a.Defenders[0]);
                }
                a.Answer(_attackTargets.Select(kv => new AttackDeclaration(kv.Key, kv.Value, null)).ToList());
                return true;
            }
            case BlockDecision b when b.Request.MinimumBlockers.Count == 0 && b.Request.MustBeBlocked.Count == 0:
            {
                var target = b.Attackers[0];
                var done = new HashSet<CardId>();
                foreach (var id in b.PossibleBlockers.Where(bl => b.Request.CanBlock[bl].Contains(target)))
                {
                    if (!done.Add(id) || NodeOf(id) is not { } node) continue;
                    var same = SameKindAmong(node, b.PossibleBlockers);
                    done.UnionWith(same);
                    if (same.Count > 1)
                    {
                        ChoosePendingBlockers(same, (same.Count + 1) / 2);
                        GD.Print($"STACKTEST block with {(same.Count + 1) / 2} of {same.Count} {node.View?.Name}");
                    }
                    else _pendingBlocker = id;
                    foreach (var picked in PendingBlockers().Where(p => b.Request.CanBlock[p].Contains(target))) _blocks[picked] = target;
                    ClearPendingBlockers();
                    ApplyBlockCount(same, same.Count);
                }
                var blocks = _blocks.Select(kv => new BlockDeclaration(kv.Key, kv.Value)).ToList();
                if (b.Request.IsLegal(blocks, out _)) { b.Answer(blocks); return true; }
                _blocks.Clear();
                return false;
            }
        }
        return false;
    }

    private static void AutoAnswer(Decision decision, GameView view)
    {
        if (decision.IsAnswered) return;
        switch (decision)
        {
            case MulliganDecision m: m.Answer(true); break;
            case PriorityDecision p: p.Answer(p.Legal.FirstOrDefault(a => a is PlayLand or CastSpell) ?? PassPriority.Instance); break;
            case TargetDecision t:
            {
                var picks = new List<Arcanum.Engine.Abilities.Target>();
                for (int i = 0; i < t.Request.Specs.Count; i++)
                {
                    var options = t.Request.LegalAt(i).Where(o => t.Request.IsAllowed(i, o, picks)).ToList();
                    if (options.Count == 0) break; // no legal combination of targets: cancel below
                    picks.Add(options.FirstOrDefault(o => !o.IsNone) is { } o && !o.IsNone ? o : options[0]);
                }
                t.Answer(picks.Count < t.Request.Specs.Count && t.Request.CanCancel ? null : picks);
                break;
            }
            case ManaPaymentDecision pay: pay.Answer(pay.Request.SuggestedTaps); break;
            case YesNoDecision yn: yn.Answer(true); break;
            case DamageAssignmentDecision dmg: dmg.Answer(dmg.Request.Suggested); break;
            case BlockDecision b when b.Request.MinimumBlockers.Count > 0 || b.Request.MustBeBlocked.Count > 0:
                b.Answer(b.Request.WithRequirements(Array.Empty<BlockDeclaration>())); // keep autoplay simple around menace
                break;
            case AttackDecision a:
                a.Answer(a.PossibleAttackers.Take(view.AttackTaxes.FirstOrDefault(t => t.Defender == a.Defenders[0])?.Affordable ?? int.MaxValue)
                    .Select(id => new AttackDeclaration(id, view.MayAttack(id, a.Defenders[0]) ? a.Defenders[0] : a.Defenders.FirstOrDefault(d => view.MayAttack(id, d), a.Defenders[0]))).ToList());
                break;
            // Double-block the first attacker when possible so damage assignment gets exercised too.
            case BlockDecision b:
            {
                var target = b.Attackers[0];
                b.Answer(b.PossibleBlockers.Where(bl => b.Request.CanBlock[bl].Contains(target)).Take(2)
                    .Select(bl => new BlockDeclaration(bl, target)).ToList());
                break;
            }
            case SelectCardsDecision s: s.Answer(view.Players[s.Player.Value].Hand.Take(s.Count).Select(c => c.Id).ToList()); break;
            case ChooseModesDecision md: md.Answer(md.Request.Possible.Take(Math.Max(1, md.Request.Min)).ToList()); break;
            case ChooseNumberDecision nd: nd.Answer(nd.Request.Max); break;
            case ChooseOptionDecision od: od.Answer(0); break;
            case ChooseCardsDecision c: c.Answer(c.Request.Options.Take(Math.Max(c.Request.Min, 1)).Take(c.Request.Max).Select(o => o.Id).ToList()); break;
        }
    }

    private Decision? _lastDecision;

    private void ApplyHighlights(GameView view, Decision? decision)
    {
        foreach (var area in _areas) area.SetPlayerTargetable(false);
        foreach (var attack in view.Attacks)
        {
            FindCard(attack.Attacker)?.SetHighlight(CardHighlight.Attacking);
            foreach (var blocker in attack.Blockers) FindCard(blocker)?.SetHighlight(CardHighlight.Blocking);
        }

        switch (decision)
        {
            case PriorityDecision p:
                foreach (var action in p.Legal)
                {
                    var id = action switch { PlayLand l => l.Card, CastSpell c => c.Card, ActivateAbility a => a.Source, _ => (CardId?)null };
                    if (id is { } cardId) FindCard(cardId)?.SetHighlight(CardHighlight.Playable);
                }
                break;
            case TargetDecision t:
            {
                FindCard(t.Request.Source)?.SetHighlight(CardHighlight.Selected);
                foreach (var chosen in _chosenTargets)
                    if (chosen.Card is { } c) FindCard(c)?.SetHighlight(CardHighlight.Selected);
                if (_chosenTargets.Count < t.Request.Legal.Count || t.Request.LastIsAnyNumber)
                {
                    foreach (var option in t.Request.LegalAt(_chosenTargets.Count).Where(o => t.Request.IsAllowed(_chosenTargets.Count, o, _chosenTargets)))
                    {
                        if (option.Card is { } c) FindCard(c)?.SetHighlight(CardHighlight.Playable);
                        if (option.Player is { } pl) AreaOf(pl).SetPlayerTargetable(true);
                    }
                    // Spells and abilities on the stack that can be targeted.
                    for (int i = 0; i < view.Stack.Count; i++)
                        if (t.Request.IsAllowed(_chosenTargets.Count, StackTarget(view.Stack[i]), _chosenTargets))
                            _stackView.NodeAt(i)?.SetHighlight(CardHighlight.Playable);
                }
                break;
            }
            case ManaPaymentDecision pay:
            {
                FindCard(pay.Request.Source)?.SetHighlight(CardHighlight.Selected);
                var remaining = RemainingCost(pay);
                foreach (var source in pay.Request.Sources)
                {
                    if (_staged.Any(t => t.Source == source.Source)) FindCard(source.Source)?.SetHighlight(CardHighlight.Selected);
                    else if (UsefulType(source, remaining) is not null) FindCard(source.Source)?.SetHighlight(CardHighlight.Playable);
                }
                break;
            }
            case DamageAssignmentDecision dmg:
                FindCard(dmg.Request.Attacker)?.SetHighlight(CardHighlight.Attacking);
                foreach (var id in dmg.Request.Blockers)
                {
                    FindCard(id)?.SetHighlight(CardHighlight.Blocking);
                    FindCard(id)?.SetAssignedDamage(_damageSplit.GetValueOrDefault(id));
                }
                break;
            case AttackDecision a:
                foreach (var id in a.PossibleAttackers)
                {
                    bool attacking = _attackTargets.TryGetValue(id, out var target);
                    FindCard(id)?.SetHighlight(attacking ? CardHighlight.Attacking : CardHighlight.Playable);
                    if (attacking && _attackWalkers.TryGetValue(id, out var walker)) FindCard(id)?.SetCaption("\u2192 " + (view.FindCard(walker)?.Name ?? "planeswalker"));
                    else if (attacking && a.Defenders.Count > 1) FindCard(id)?.SetCaption("\u2192 " + view.Players[target.Value].Name);
                }
                // Opponents' planeswalkers can be attacked too: click one to send the selected attackers at it.
                foreach (var walker in view.Battlefield.Where(c => (c.Types & Arcanum.Engine.Cards.CardType.Planeswalker) != 0 && a.Defenders.Contains(c.Controller)))
                    FindCard(walker.Id)?.SetHighlight(_attackWalker == walker.Id ? CardHighlight.Selected : CardHighlight.Playable);
                foreach (var defender in a.Defenders) AreaOf(defender).SetPlayerTargetable(defender == _attackDefender);
                break;
            case BlockDecision b:
                foreach (var id in b.Attackers)
                {
                    // With a blocker picked, only the attackers it may legally block light up.
                    bool target = _pendingBlocker is { } picked && b.Request.CanBlock[picked].Contains(id);
                    FindCard(id)?.SetHighlight(target ? CardHighlight.Playable : CardHighlight.Attacking);
                }
                foreach (var id in b.PossibleBlockers)
                {
                    var h = IsPendingBlocker(id) ? CardHighlight.Selected
                        : _blocks.ContainsKey(id) ? CardHighlight.Blocking
                        : CardHighlight.Playable;
                    FindCard(id)?.SetHighlight(h);
                }
                break;
            case SelectCardsDecision s:
                foreach (var card in view.Players[s.Player.Value].Hand)
                    FindCard(card.Id)?.SetHighlight(_selected.Contains(card.Id) ? CardHighlight.Selected : CardHighlight.Playable);
                break;
        }
    }

    /// <summary>Blocker → attacker arrows for declared and in-progress blocks, plus one following the mouse.</summary>
    private void UpdateArrows(GameView view, Decision? decision)
    {
        var arrows = new List<ArrowLayer.Arrow>();
        if (view.IsGameOver) { _arrows.SetArrows(arrows); return; }
        void Add(CardId from, CardId? to, Color color)
        {
            var fromNode = FindCard(from);
            CardNode? toNode = to is { } t ? FindCard(t) : null;
            if (fromNode is not null && (to is null || toNode is not null)) arrows.Add(new(fromNode, toNode, color));
        }

        foreach (var attack in view.Attacks)
            foreach (var blocker in attack.Blockers) Add(blocker, attack.Attacker, BoardStyle.Blocking);

        // With several opponents, show who each attacker goes after.
        if (_areas.Count > 2)
        {
            var targets = decision is AttackDecision
                ? _attackTargets.Select(kv => (kv.Key, kv.Value))
                : view.Attacks.Where(at => !at.IsBlocked).Select(at => (at.Attacker, at.Defender));
            foreach (var (attacker, defender) in targets)
                if (FindCard(attacker) is { } node && !_attackWalkers.ContainsKey(attacker)) arrows.Add(new(node, AreaOf(defender).LifeBox, BoardStyle.Attacking));
        }
        // Attacks on planeswalkers point at the planeswalker.
        {
            var walkerTargets = decision is AttackDecision
                ? _attackWalkers.Select(kv => (kv.Key, kv.Value))
                : view.Attacks.Where(at => at.Planeswalker is not null && !at.IsBlocked).Select(at => (at.Attacker, at.Planeswalker!.Value));
            foreach (var (attacker, walker) in walkerTargets)
                if (FindCard(attacker) is { } from && FindCard(walker) is { } to) arrows.Add(new(from, to, BoardStyle.Attacking));
        }

        // Spells and abilities on the stack point at their targets.
        for (int i = 0; i < view.Stack.Count; i++)
        {
            if (_stackView.NodeAt(i) is not { } stackNode) continue;
            foreach (var target in view.Stack[i].Targets)
                if (TargetControl(target) is { } to) arrows.Add(new(stackNode, to, BoardStyle.Selected));
        }

        // While choosing targets: arrows to the ones picked so far and one following the mouse.
        if (decision is TargetDecision td && FindCard(td.Request.Source) is { } sourceNode)
        {
            foreach (var chosen in _chosenTargets)
                if (TargetControl(chosen) is { } to) arrows.Add(new(sourceNode, to, BoardStyle.Selected));
            if (_chosenTargets.Count < td.Request.Legal.Count || td.Request.LastIsAnyNumber) arrows.Add(new(sourceNode, null, BoardStyle.Playable));
        }

        if (decision is BlockDecision)
        {
            foreach (var (blocker, attacker) in _blocks) Add(blocker, attacker, BoardStyle.Blocking);
            if (_pendingBlocker is { } pending) Add(pending, null, BoardStyle.Selected);
        }
        _arrows.SetArrows(arrows);
    }

    private Control? TargetControl(Arcanum.Engine.Abilities.Target target) =>
        target.Card is { } c ? FindCard(c) : target.Player is { } p ? AreaOf(p).LifeBox : null;

    private void BuildActionPanel(GameView view, Decision? decision)
    {
        foreach (var child in _actionButtons.GetChildren()) child.QueueFree();
        foreach (var child in _actionExtra.GetChildren()) child.QueueFree();
        _actionExtra.Visible = false;
        _actionPanel.Visible = decision is not null && !view.IsGameOver;
        if (decision is null) return;

        string who = view.Players[decision.Player.Value].Name;
        switch (decision)
        {
            case MulliganDecision m:
                _prompt.Text = m.MulligansTaken == 0 ? $"{who}: keep this hand?" : $"{who}: keep? ({m.MulligansTaken} mulligan{(m.MulligansTaken > 1 ? "s" : "")})";
                AddButton("Mulligan", () => m.Answer(false));
                AddButton("Keep", () => m.Answer(true), primary: true);
                break;

            case PriorityDecision p when _abilityChoiceSource is { } source:
            {
                var card = view.FindCard(source);
                _prompt.Text = $"{who}: use {card?.Name ?? "permanent"}";
                AddButton("Cancel", () => { _abilityChoiceSource = null; Refresh(); });
                foreach (var option in SourceActions(p, source))
                {
                    var label = option switch
                    {
                        PlayLand => $"Play {card?.Name}",
                        CastSpell { Adventure: true } => $"Adventure: {card?.AdventureName} {card?.AdventureCost}",
                        CastSpell { Half: { } half } when card?.SplitHalves is { } halves && half < halves.Count => $"Cast {halves[half]}",
                        CastSpell => $"Cast {card?.Name}",
                        ActivateManaAbility m => $"Add {{{m.Type.ToSymbol()}}}",
                        ActivateAbility a when card is not null && a.Index < card.AbilityTexts.Count => Shorten(card.AbilityTexts[a.Index]),
                        _ => "Activate",
                    };
                    AddButton(label, () => p.Answer(option));
                }
                break;
            }

            case YesNoDecision yn:
                _prompt.Text = $"{who}: {yn.Request.Prompt}";
                AddButton("No", () => yn.Answer(false));
                AddButton("Yes", () => yn.Answer(true), primary: true);
                break;

            case TargetDecision t:
            {
                var source = view.FindCard(t.Request.Source)?.Name ?? "spell";
                var spec = t.Request.SpecAt(_chosenTargets.Count);
                bool more = spec.AnyNumber && _chosenTargets.Count >= t.Request.Specs.Count - 1;
                _prompt.Text = more
                    ? $"{who}: choose any number of {spec.Describe()} for {source} ({_chosenTargets.Count - (t.Request.Specs.Count - 1)} chosen)"
                    : $"{who}: choose {(spec.Optional ? "up to one " : "")}{spec.Describe()} for {source}";
                if (t.Request.CanCancel) AddButton("Cancel", () => t.Answer(null));
                if (_chosenTargets.Count > 0) AddButton("Back", () => { _chosenTargets.RemoveAt(_chosenTargets.Count - 1); Refresh(); });
                if (more)
                {
                    var done = AddButton("Done", () => t.Answer(_chosenTargets.ToList()), primary: true);
                    done.Disabled = !t.Request.IsComplete(_chosenTargets.Count);
                }
                else if (spec.Optional) AddButton("None", () => PickTarget(Arcanum.Engine.Abilities.Target.None), primary: true);
                break;
            }

            case ChooseModesDecision md:
            {
                var source = view.FindCard(md.Request.Source)?.Name ?? "spell";
                var count = md.Request.Min == md.Request.Max ? $"{md.Request.Max}" : $"{md.Request.Min}–{md.Request.Max}";
                _prompt.Text = $"{who}: {source} — choose {count}";
                _actionExtra.Visible = true;
                foreach (var i in md.Request.Possible)
                {
                    int mode = i;
                    var text = (md.Request.Max > 1 && _chosenModes.Contains(mode) ? "\u2713 " : "") + Shorten(md.Request.Modes[mode]);
                    AddChoiceButton(text, () =>
                    {
                        if (md.Request.Max == 1) { md.Answer(new[] { mode }); return; }
                        if (!_chosenModes.Remove(mode) && _chosenModes.Count < md.Request.Max) _chosenModes.Add(mode);
                        Refresh();
                    });
                }
                if (md.Request.CanCancel) AddButton("Cancel", () => md.Answer(null));
                if (md.Request.Max > 1)
                {
                    var done = AddButton($"Confirm ({_chosenModes.Count})", () => md.Answer(_chosenModes.OrderBy(m => m).ToList()), primary: true);
                    done.Disabled = _chosenModes.Count < md.Request.Min || _chosenModes.Count > md.Request.Max;
                }
                break;
            }

            case ChooseOptionDecision od:
            {
                _prompt.Text = $"{who}: {od.Request.Prompt}";
                var grid = new GridContainer { Columns = od.Request.Options.Count > 6 ? 3 : 1 };
                grid.AddThemeConstantOverride("h_separation", 6);
                grid.AddThemeConstantOverride("v_separation", 6);
                _actionExtra.Visible = true;
                _actionExtra.AddChild(grid);
                for (int i = 0; i < od.Request.Options.Count; i++)
                {
                    int option = i;
                    var button = BoardStyle.MakeButton(od.Request.Options[i], 14);
                    button.CustomMinimumSize = new Vector2(od.Request.Options.Count > 6 ? 110 : 220, 34);
                    button.Pressed += () =>
                    {
                        if (_session.CurrentDecision is null) return;
                        od.Answer(option);
                        _actionPanel.Visible = false;
                    };
                    grid.AddChild(button);
                }
                break;
            }

            case ChooseNumberDecision nd:
            {
                _prompt.Text = $"{who}: {nd.Request.Prompt} ({nd.Request.Min}–{nd.Request.Max})";
                var less = AddButton("\u2212", () => { _number = Math.Max(nd.Request.Min, _number - 1); Refresh(); });
                less.CustomMinimumSize = new Vector2(52, 44);
                less.Disabled = _number <= nd.Request.Min;
                var more = AddButton("+", () => { _number = Math.Min(nd.Request.Max, _number + 1); Refresh(); });
                more.CustomMinimumSize = new Vector2(52, 44);
                more.Disabled = _number >= nd.Request.Max;
                AddButton($"X = {_number}", () => nd.Answer(_number), primary: true);
                break;
            }

            case ManaPaymentDecision pay:
            {
                var spell = view.FindCard(pay.Request.Source)?.Name ?? "spell";
                var remaining = RemainingCost(pay);
                _prompt.Text = $"{who}: cast {spell}";
                _actionExtra.Visible = true;
                _actionExtra.AddChild(CostLine("Cost", pay.Request.Cost.ToString()));
                if (pay.Request.FromPool.Count > 0)
                    _actionExtra.AddChild(CostLine("Floating mana used", string.Concat(pay.Request.FromPool.Select(t => $"{{{t.ToSymbol()}}}"))));
                if (remaining.ManaValue > 0)
                    _actionExtra.AddChild(CostLine("Still needed", remaining.ToString(), BoardStyle.Attacking));
                else
                    _actionExtra.AddChild(BoardStyle.MakeLabel("Fully paid \u2713", 14, new Color("6fd08c")));
                if (_comboSource is { } combo)
                {
                    // "Add two mana in any combination of …": choose the mana this source adds.
                    var name = view.FindCard(combo.Source)?.Name ?? "source";
                    _actionExtra.AddChild(BoardStyle.MakeLabel($"{name}: choose the mana to add", 14, BoardStyle.Text, bold: true));
                    foreach (var mana in Combinations(combo))
                    {
                        var (_, excess) = ManaPayment.Apply(remaining, mana);
                        var button = AddChoiceButton(string.Concat(mana.Select(t => $"{{{t.ToSymbol()}}}")) + (excess > 0 ? "  (some of it floats)" : ""), () =>
                        {
                            _staged.Add(new ManaTap(combo.Source, mana[0], combo.Option, mana));
                            _comboSource = null;
                            Refresh();
                        });
                    }
                    AddButton("Back", () => { _comboSource = null; Refresh(); });
                    break;
                }
                AddButton("Cancel", () => pay.Answer(null));
                AddButton("Auto", () => { _staged.Clear(); _staged.AddRange(pay.Request.SuggestedTaps); Refresh(); });
                var confirmPay = AddButton("Confirm", () => pay.Answer(_staged.ToList()), primary: true);
                confirmPay.Disabled = remaining.ManaValue > 0;
                break;
            }

            case PriorityDecision p:
            {
                _prompt.Text = $"{who}: your move";
                string resolve = "Next";
                if (view.Stack.Count > 0)
                {
                    // Say exactly what passing lets resolve: name, rules text and targets of the top of the stack.
                    var top = view.Stack[^1];
                    var name = top.Card.Name ?? "spell";
                    resolve = $"Resolve {Shorten(name)}";
                    _prompt.Text = $"{who}: respond, or let it resolve";
                    _actionExtra.Visible = true;
                    var caster = view.Players[top.Controller.Value].Name;
                    _actionExtra.AddChild(BoardStyle.MakeLabel($"{caster}: {name}{(top.AbilityText is null ? "" : " (ability)")}", 15, BoardStyle.Text, bold: true));
                    var rules = top.AbilityText ?? top.Card.OracleText;
                    if (rules.Length > 0)
                    {
                        var text = BoardStyle.MakeLabel(rules.Length > 170 ? rules[..167] + "\u2026" : rules, 13, BoardStyle.TextDim);
                        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                        text.CustomMinimumSize = new Vector2(360, 0);
                        _actionExtra.AddChild(text);
                    }
                    if (top.Targets.Count > 0)
                    {
                        var names = top.Targets.Select(t => t.Player is { } pl ? view.Players[pl.Value].Name : view.FindCard(t.Card!.Value)?.Name ?? "?");
                        _actionExtra.AddChild(BoardStyle.MakeLabel("\u2192 " + string.Join(", ", names), 14, BoardStyle.Playable));
                    }
                }
                // Cards in the graveyard aren't on the board: flashback and graveyard abilities get their own buttons.
                foreach (var action in p.Legal)
                {
                    var (id, label) = action switch
                    {
                        CastSpell { Half: { } half } c when view.FindCard(c.Card) is { Zone: Arcanum.Engine.State.Zone.Graveyard, SplitHalves: { } halves } g && half < halves.Count
                            => (c.Card, $"Cast {halves[half]} (graveyard)"),
                        CastSpell c when view.FindCard(c.Card) is { Zone: Arcanum.Engine.State.Zone.Graveyard } g => (c.Card, $"Flashback {g.Name}"),
                        CastSpell { Adventure: true } c when view.FindCard(c.Card) is { Zone: Arcanum.Engine.State.Zone.Exile } x => (c.Card, $"Adventure: {x.AdventureName} (exile)"),
                        CastSpell c when view.FindCard(c.Card) is { Zone: Arcanum.Engine.State.Zone.Exile } x => (c.Card, $"Cast {x.Name} (exile)"),
                        PlayLand l when view.FindCard(l.Card) is { Zone: Arcanum.Engine.State.Zone.Exile } x => (l.Card, $"Play {x.Name} (exile)"),
                        ActivateAbility a when view.FindCard(a.Source) is { Zone: Arcanum.Engine.State.Zone.Graveyard } g
                            => (a.Source, $"{g.Name}: {Shorten(a.Index < g.AbilityTexts.Count ? g.AbilityTexts[a.Index] : "activate")}"),
                        _ => (default(CardId?), ""),
                    };
                    if (id is not null) AddChoiceButton(label, () => p.Answer(action));
                }
                // Only the active player ends the turn; during someone else's turn the player can only pass priority.
                if (view.ActivePlayer == decision.Player)
                    AddButton("End turn", () =>
                    {
                        _session.Policy.PassTurn(view.TurnNumber); // skip the rest of the turn unless an opponent acts
                        p.Answer(PassPriority.Instance);
                    });
                AddButton(resolve, () => p.Answer(PassPriority.Instance), primary: true);
                break;
            }

            case DamageAssignmentDecision dmg:
            {
                var attackerName = view.FindCard(dmg.Request.Attacker)?.Name ?? "Attacker";
                int remaining = dmg.Request.Power - _damageSplit.Values.Sum() - _damageToPlayer;
                _prompt.Text = $"{who}: assign {dmg.Request.Power} damage from {attackerName}";
                _actionExtra.Visible = true;
                foreach (var blocker in dmg.Request.Blockers) _actionExtra.AddChild(DamageRow(view, dmg, blocker, remaining));
                bool lethalToAll = dmg.Request.Lethal.All(kv => _damageSplit.GetValueOrDefault(kv.Key) >= kv.Value);
                if (dmg.Request.Trample) _actionExtra.AddChild(PlayerDamageRow(view, dmg, remaining, lethalToAll));
                _actionExtra.AddChild(BoardStyle.MakeLabel(remaining == 0 ? "All damage assigned \u2713" : $"Left to assign: {remaining}", 14,
                    remaining == 0 ? new Color("6fd08c") : BoardStyle.Attacking));
                AddButton("Auto", () => { ResetDamage(dmg); Refresh(); });
                var confirmDamage = AddButton("Confirm", () =>
                    dmg.Answer(new DamageAssignment(new Dictionary<CardId, int>(_damageSplit), _damageToPlayer)), primary: true);
                confirmDamage.Disabled = remaining != 0 || (_damageToPlayer > 0 && !lethalToAll);
                break;
            }

            case AttackDecision a:
                _prompt.Text = $"{who}: choose attackers";
                foreach (var tax in view.AttackTaxes)
                {
                    _actionExtra.Visible = true;
                    _actionExtra.AddChild(BoardStyle.MakeLabel($"Attacking {view.Players[tax.Defender.Value].Name} costs {tax.CostPerCreature} per creature (you can pay for {tax.Affordable})", 14, BoardStyle.Attacking));
                }
                if (a.Defenders.Count > 1)
                {
                    // Several opponents: pick who the next attackers go after.
                    _actionExtra.Visible = true;
                    var targets = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
                    targets.AddThemeConstantOverride("separation", 6);
                    targets.AddChild(BoardStyle.MakeLabel("Attack:", 13, BoardStyle.TextDim));
                    foreach (var defender in a.Defenders)
                    {
                        bool current = defender == _attackDefender;
                        var pick = current ? BoardStyle.MakePrimaryButton(view.Players[defender.Value].Name, 14) : BoardStyle.MakeButton(view.Players[defender.Value].Name, 14);
                        pick.CustomMinimumSize = new Vector2(0, 34);
                        pick.Pressed += () => ChooseAttackTarget(defender);
                        targets.AddChild(pick);
                    }
                    _actionExtra.AddChild(targets);
                }
                if (_attackTargets.Count < a.PossibleAttackers.Count)
                    AddButton("All", () =>
                    {
                        foreach (var id in a.PossibleAttackers) _attackTargets.TryAdd(id, _attackDefender ?? a.Defenders[0]);
                        Refresh();
                    });
                AddButton(_attackTargets.Count == 0 ? "No attacks" : $"Attack ({_attackTargets.Count})", () =>
                    a.Answer(_attackTargets.Select(kv => new AttackDeclaration(kv.Key, kv.Value, _attackWalkers.TryGetValue(kv.Key, out var w) ? w : null)).ToList()),
                    primary: true);
                break;

            case BlockDecision b:
            {
                var blocks = _blocks.Select(kv => new BlockDeclaration(kv.Key, kv.Value)).ToList();
                bool legal = b.Request.IsLegal(blocks, out var reason);
                _prompt.Text = !legal ? $"{who}: {reason}"
                    : _pendingBlocker is null ? $"{who}: choose a blocker" : $"{who}: choose what it blocks";
                var confirmBlocks = AddButton(_blocks.Count == 0 ? "No blocks" : $"Confirm blocks ({_blocks.Count})", () => b.Answer(blocks), primary: true);
                confirmBlocks.Disabled = !legal;
                break;
            }

            case SelectCardsDecision s:
                string what = s.Reason == SelectCardsReason.MulliganBottom ? "put on the bottom" : "discard";
                _prompt.Text = $"{who}: choose {s.Count} card{(s.Count > 1 ? "s" : "")} to {what}";
                var confirm = AddButton($"Confirm ({_selected.Count}/{s.Count})", () => s.Answer(_selected.ToList()), primary: true);
                confirm.Disabled = _selected.Count != s.Count;
                break;

            case ChooseCardsDecision c:
            {
                _prompt.Text = $"{who}: {c.Request.Prompt}";
                var range = c.Request.Min == c.Request.Max ? $"{c.Request.Max}" : $"{c.Request.Min}–{c.Request.Max}";
                var done = AddButton($"Confirm ({_selected.Count} of {range})", () => c.Answer(_selected.ToList()), primary: true);
                done.Disabled = _selected.Count < c.Request.Min || _selected.Count > c.Request.Max;
                break;
            }
        }
    }

    /// <summary>One blocker in the damage assignment panel: name, lethal hint and −/+ controls.</summary>
    private Control DamageRow(GameView view, DamageAssignmentDecision dmg, CardId blocker, int remaining)
    {
        var card = view.FindCard(blocker);
        int lethal = dmg.Request.Lethal[blocker];
        int amount = _damageSplit.GetValueOrDefault(blocker);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);
        // Hovering a row points out which card it is (names can repeat).
        row.MouseEntered += () => FindCard(blocker)?.SetHighlight(CardHighlight.Selected);
        row.MouseExited += () => FindCard(blocker)?.SetHighlight(CardHighlight.Blocking);
        row.AddChild(BoardStyle.MakeLabel(card?.Name ?? "?", 14));
        row.AddChild(BoardStyle.MakeLabel($"lethal {lethal}", 12, amount >= lethal ? new Color("6fd08c") : BoardStyle.TextDim));

        var minus = BoardStyle.MakeButton("\u2212", 16, compact: true);
        minus.CustomMinimumSize = new Vector2(34, 32);
        minus.Disabled = amount == 0;
        minus.Pressed += () => { _damageSplit[blocker] = amount - 1; Refresh(); };
        var value = BoardStyle.MakeLabel(amount.ToString(), 18, bold: true);
        value.CustomMinimumSize = new Vector2(28, 0);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var plus = BoardStyle.MakeButton("+", 16, compact: true);
        plus.CustomMinimumSize = new Vector2(34, 32);
        plus.Disabled = remaining == 0;
        plus.Pressed += () => { _damageSplit[blocker] = amount + 1; Refresh(); };

        row.AddChild(minus);
        row.AddChild(value);
        row.AddChild(plus);
        return row;
    }

    private void ResetDamage(DamageAssignmentDecision dmg)
    {
        _damageSplit.Clear();
        foreach (var (blocker, amount) in dmg.Request.Suggested.ToBlockers) _damageSplit[blocker] = amount;
        _damageToPlayer = dmg.Request.Suggested.ToPlayer;
    }

    /// <summary>Trample row: damage to the defending player, only allowed once every blocker has lethal damage.</summary>
    private Control PlayerDamageRow(GameView view, DamageAssignmentDecision dmg, int remaining, bool lethalToAll)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(BoardStyle.MakeLabel(view.Players[dmg.Request.Defender.Value].Name, 14));
        row.AddChild(BoardStyle.MakeLabel("trample", 12, lethalToAll ? new Color("6fd08c") : BoardStyle.TextDim));
        var minus = BoardStyle.MakeButton("\u2212", 16, compact: true);
        minus.CustomMinimumSize = new Vector2(34, 32);
        minus.Disabled = _damageToPlayer == 0;
        minus.Pressed += () => { _damageToPlayer--; Refresh(); };
        var value = BoardStyle.MakeLabel(_damageToPlayer.ToString(), 18, bold: true);
        value.CustomMinimumSize = new Vector2(28, 0);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var plus = BoardStyle.MakeButton("+", 16, compact: true);
        plus.CustomMinimumSize = new Vector2(34, 32);
        plus.Disabled = remaining == 0 || !lethalToAll;
        plus.Pressed += () => { _damageToPlayer++; Refresh(); };
        row.AddChild(minus);
        row.AddChild(value);
        row.AddChild(plus);
        return row;
    }

    private static Control CostLine(string title, string cost, Color? titleColor = null)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(BoardStyle.MakeLabel(title, 13, titleColor ?? BoardStyle.TextDim));
        row.AddChild(BoardStyle.MakeCostRow(cost));
        return row;
    }

    /// <summary>What the staged taps still leave unpaid.</summary>
    private ManaCost RemainingCost(ManaPaymentDecision pay) =>
        ManaPayment.Apply(pay.Request.RemainingAfterPool, _staged.SelectMany(t => t.Combination
            ?? Enumerable.Repeat(t.Type, pay.Request.Sources.FirstOrDefault(s => s.Source == t.Source && s.Option == t.Option)?.Amount ?? 1))).Remaining;

    /// <summary>Every combination of mana a "in any combination" source can add.</summary>
    private static List<IReadOnlyList<ManaType>> Combinations(ManaSourceOption source) =>
        new Arcanum.Engine.Cards.ManaOption(source.Types, source.Amount) { Combination = true }.Combinations().ToList();

    /// <summary>A mana type this source can add that still helps pay <paramref name="remaining"/>, preferring colored pips.</summary>
    private static ManaType? UsefulType(ManaSourceOption source, ManaCost remaining)
    {
        foreach (var type in source.Types) if (remaining.Pips.Contains(type)) return type;
        return remaining.Generic > 0 ? source.Types[0] : null;
    }

    /// <summary>A full-width option button in the panel's extra area (modes, long choices).</summary>
    private Button AddChoiceButton(string text, Action onPressed)
    {
        var button = BoardStyle.MakeModalButton(text, size: 15);
        button.CustomMinimumSize = new Vector2(320, 34);
        button.Alignment = HorizontalAlignment.Left;
        button.Pressed += () =>
        {
            if (_session.CurrentDecision is null) return;
            onPressed();
            if (_session.CurrentDecision is null) _actionPanel.Visible = false;
        };
        _actionExtra.Visible = true;
        _actionExtra.AddChild(button);
        return button;
    }

    private Button AddButton(string text, Action onPressed, bool primary = false)
    {
        // Answers are plain text with a gold line between them; the default one is gold.
        if (_actionButtons.GetChildren().Any(c => !c.IsQueuedForDeletion())) _actionButtons.AddChild(BoardStyle.MakeModalSeparator());
        var button = BoardStyle.MakeModalButton(text, primary);
        button.CustomMinimumSize = new Vector2(80, 36);
        button.Pressed += () =>
        {
            if (_session.CurrentDecision is null) return;
            onPressed();
            if (_session.CurrentDecision is null) _actionPanel.Visible = false; // wait for the engine's next question
        };
        _actionButtons.AddChild(button);
        return button;
    }

    // ---------------------------------------------------------------- input

    public override void _Process(double delta)
    {
        _session?.Poll();
        if (_online is not null)
        {
            int left = _online.DecisionSecondsLeft;
            _decisionTimer.Visible = left >= 0;
            if (left >= 0) _decisionTimer.Text = $"⏱ {left / 60}:{left % 60:00}";
        }
        // Count down the seats waiting for a disconnected player.
        if (_online is not null && Time.GetTicksMsec() >= _nextSeatTick && _seatStates.Values.Any(s => s.Deadline is not null))
        {
            _nextSeatTick = Time.GetTicksMsec() + 1000;
            UpdateSeatNotes();
        }
    }

    public override void _ExitTree() => _session?.Leave();

    public override void _Input(InputEvent @event)
    {
        if (_drag is not { } drag) return;
        switch (@event)
        {
            case InputEventMouseMotion motion:
                if (!drag.Dragging && motion.GlobalPosition.DistanceTo(drag.Start) > 10)
                {
                    drag.Dragging = drag.Node.IsDragging = true;
                    drag.Node.LayoutTween?.Kill();
                    drag.Node.ZIndex = 300;
                    drag.Node.RotationDegrees = 0;
                }
                if (drag.Dragging) drag.Node.GlobalPosition = motion.GlobalPosition - drag.Node.Size / 2;
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release:
                _drag = null;
                drag.Node.IsDragging = false;
                bool cancelled = drag.Dragging && drag.Area.IsOverHand(release.GlobalPosition);
                if (!cancelled && !drag.Decision.IsAnswered) drag.Decision.Answer(drag.Action);
                else Refresh(); // dropped back onto the hand: snap into place
                break;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            _menu.Visible = !_menu.Visible;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z, CtrlPressed: true })
        {
            Undo();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Space confirms the primary action.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space } && _actionPanel.Visible)
        {
            var primary = _actionButtons.GetChildren().OfType<Button>().LastOrDefault();
            if (primary is { Disabled: false }) primary.EmitSignal(BaseButton.SignalName.Pressed);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Playing or casting <paramref name="source"/> and its activated and mana abilities: what a click on it could mean.</summary>
    /// <summary>Graveyard or exile shown in full (clicking a pile).</summary>
    private readonly ZoneViewer _zoneViewer = new();

    private void OpenZone(PlayerId player, Arcanum.Engine.State.Zone zone)
    {
        if (_zoneViewer.Visible && _zoneViewer.Player == player && _zoneViewer.Zone == zone) { _zoneViewer.Close(); return; }
        if (_zoneViewer.GetParent() is null)
        {
            _zoneViewer.CardClicked += OnZoneCardClicked;
            _zoneViewer.CardHoverStarted += ShowPreview;
            _zoneViewer.CardHoverEnded += HidePreview;
            AddChild(_zoneViewer);
        }
        _zoneViewer.Open(player, zone);
        Refresh();
    }

    /// <summary>A card in the zone viewer: used like a card on the board; the viewer closes once it starts something.</summary>
    private void OnZoneCardClicked(CardNode node)
    {
        var before = _session.CurrentDecision;
        bool acts = UsableNow(before, node.Id);
        OnCardClicked(node);
        if (acts && (before is PriorityDecision || before is TargetDecision)) _zoneViewer.Close();
        HidePreview(node);
    }

    /// <summary>Whether clicking this card does something in the current decision (cast, play, activate, target, choose).</summary>
    private bool UsableNow(Decision? decision, CardId id) => decision switch
    {
        PriorityDecision p => SourceActions(p, id).Count > 0,
        TargetDecision t => _chosenTargets.Count < t.Request.Specs.Count || t.Request.LastIsAnyNumber
            ? t.Request.LegalAt(_chosenTargets.Count).Any(o => o.Card == id && t.Request.IsAllowed(_chosenTargets.Count, o, _chosenTargets))
            : false,
        ChooseCardsDecision c => c.Request.Options.Any(o => o.Id == id),
        _ => false,
    };

    private static List<PlayerAction> SourceActions(PriorityDecision p, CardId source) =>
        p.Legal.Where(a => a is ActivateAbility aa && aa.Source == source || a is ActivateManaAbility m && m.Source == source
                           || a is CastSpell c && c.Card == source || a is PlayLand l && l.Card == source).ToList();

    private static string Shorten(string text) => text.Length <= 28 ? text : text[..27] + "\u2026";

    private void OnPlayerClicked(PlayerId player)
    {
        if (_session.CurrentDecision is AttackDecision a && a.Defenders.Contains(player)) ChooseAttackTarget(player);
        else PickTarget(Arcanum.Engine.Abilities.Target.Of(player));
    }

    /// <summary>
    /// New attack target: attackers picked since the last change follow it, later picks go to it too. So "pick A
    /// and B, then choose Computer 3" sends both there, and "pick A, choose P2, pick B, choose P3" splits them.
    /// </summary>
    /// <summary>The player a creature attacks: the one wanted, or the first it may attack when it can't attack that one.</summary>
    private PlayerId AllowedDefender(AttackDecision decision, CardId attacker, PlayerId wanted)
    {
        var view = _session.ViewFor(decision.Player);
        return view.MayAttack(attacker, wanted) ? wanted : decision.Defenders.FirstOrDefault(d => view.MayAttack(attacker, d), wanted);
    }

    private void ChooseAttackTarget(PlayerId defender, CardId? walker = null)
    {
        foreach (var id in _attackGroup)
        {
            if (_session.CurrentDecision is AttackDecision decision && AllowedDefender(decision, id, defender) != defender) continue; // it can't attack that player
            _attackTargets[id] = defender;
            if (walker is { } w) _attackWalkers[id] = w;
            else _attackWalkers.Remove(id);
        }
        _attackWalker = walker;
        _attackGroup.Clear();
        _attackDefender = defender;
        Refresh();
    }

    /// <summary>How a stack item is targeted: a spell by its card, an ability by its stack object.</summary>
    private static Arcanum.Engine.Abilities.Target StackTarget(StackItemView item) =>
        item.AbilityText is null ? Arcanum.Engine.Abilities.Target.Of(item.Card.Id) : Arcanum.Engine.Abilities.Target.OfStack(item.Id);

    private void OnStackItemClicked(int index)
    {
        var view = _session.ViewFor(_session.CurrentDecision?.Player ?? Bottom);
        if (index < view.Stack.Count && _session.CurrentDecision is TargetDecision) PickTarget(StackTarget(view.Stack[index]));
    }

    /// <summary>Adds a target if it is legal for the next requirement; answers once every target is chosen.</summary>
    private void PickTarget(Arcanum.Engine.Abilities.Target target)
    {
        if (_session.CurrentDecision is not TargetDecision t || (_chosenTargets.Count >= t.Request.Legal.Count && !t.Request.LastIsAnyNumber)) return;
        if (!t.Request.IsAllowed(_chosenTargets.Count, target, _chosenTargets)) return;
        _chosenTargets.Add(target);
        if (!t.Request.LastIsAnyNumber && _chosenTargets.Count == t.Request.Legal.Count) t.Answer(_chosenTargets.ToList());
        else Refresh();
    }

    // ---------------------------------------------------------------- stacks of identical tokens

    private PlayerArea? AreaHolding(CardNode node) => _areas.FirstOrDefault(a => a.FindCard(node.Id) == node);

    /// <summary>Every card that looks like this one (a stack's tokens, attacking or not) and is among <paramref name="eligible"/>.</summary>
    private List<CardId> SameKindAmong(CardNode node, IEnumerable<CardId> eligible)
    {
        var set = eligible.ToHashSet();
        return (AreaHolding(node)?.SameKind(node.Id) ?? new[] { node.Id }).Where(set.Contains).ToList();
    }

    private IEnumerable<CardId> PendingBlockers() => _pendingBlocker is { } first ? _pendingGroup.Prepend(first) : Enumerable.Empty<CardId>();

    private bool IsPendingBlocker(CardId id) => _pendingBlocker == id || _pendingGroup.Contains(id);

    private void ClearPendingBlockers()
    {
        _pendingBlocker = null;
        _pendingGroup.Clear();
    }

    /// <summary>What sets a token apart from identical ones right now, so a stack splits when some of its tokens are picked for something.</summary>
    private string? StackMark(CardId id)
    {
        var decision = _session.CurrentDecision;
        var mark = new System.Text.StringBuilder();
        if (_selected.Contains(id)) mark.Append('s');
        if (_chosenTargets.Any(t => t.Card == id)) mark.Append('t');
        if (_attackTargets.TryGetValue(id, out var defender)) mark.Append($"a{defender.Value}/{(_attackWalkers.TryGetValue(id, out var w) ? w.Value : -1)}");
        if (_attackGroup.Contains(id)) mark.Append('g');
        if (_blocks.TryGetValue(id, out var blocked)) mark.Append($"b{blocked.Value}");
        if (IsPendingBlocker(id)) mark.Append('p');
        if (_damageSplit.TryGetValue(id, out var dmg)) mark.Append($"d{dmg}");
        mark.Append(decision switch
        {
            ManaPaymentDecision pay => pay.Request.Sources.Any(src => src.Source == id) ? "m" : "",
            AttackDecision a => a.PossibleAttackers.Contains(id) ? "k" : "",
            BlockDecision b => (b.PossibleBlockers.Contains(id) ? "k" : "") + (b.Attackers.Contains(id) ? "a" : ""),
            DamageAssignmentDecision damage => damage.Request.Blockers.Contains(id) ? "k" : "",
            _ => UsableNow(decision, id) ? "u" : "",
        });
        return mark.ToString();
    }

    private void OpenPicker(string title, int min, int max, int initial, Action<int> chosen, Decision decision)
    {
        ClosePicker();
        _picker = new StackPicker();
        _picker.Chosen += count =>
        {
            // Ignore the answer if the decision moved on while the picker was open.
            if (ReferenceEquals(_session.CurrentDecision, decision) && !decision.IsAnswered) chosen(count);
        };
        AddChild(_picker);
        _picker.Open(title, min, max, initial);
    }

    private void ClosePicker()
    {
        if (_picker is { } picker && IsInstanceValid(picker)) picker.Close();
        _picker = null;
    }

    /// <summary>"Attack with how many?": exactly that many of the identical creatures attack (the ones already attacking first).</summary>
    private void OpenAttackPicker(AttackDecision decision, IReadOnlyList<CardId> same, string? name)
    {
        int attacking = same.Count(_attackTargets.ContainsKey);
        OpenPicker($"Attack with how many {name}?", 0, same.Count, attacking > 0 ? attacking : same.Count,
            count => ApplyAttackCount(decision, same, count), decision);
    }

    private void ApplyAttackCount(AttackDecision decision, IReadOnlyList<CardId> same, int count)
    {
        var (add, remove) = BattlefieldLayout.Pick(same, _attackTargets.ContainsKey, count);
        foreach (var id in remove)
        {
            _attackTargets.Remove(id);
            _attackGroup.Remove(id);
            _attackWalkers.Remove(id);
        }
        foreach (var id in add)
        {
            _attackTargets[id] = AllowedDefender(decision, id, _attackDefender ?? decision.Defenders[0]);
            if (_attackWalker is { } walker) _attackWalkers[id] = walker;
            _attackGroup.Add(id);
        }
        Refresh();
    }

    /// <summary>
    /// Blocking with part of a stack. For tokens not blocking yet: how many block, then click the attacker they block.
    /// For a stack already blocking: how many of them keep blocking.
    /// </summary>
    private void OpenBlockPicker(BlockDecision decision, string? name, IReadOnlyList<CardId> members, bool blocking)
    {
        if (blocking)
            OpenPicker($"Block with how many {name}?", 0, members.Count, members.Count,
                count => ApplyBlockCount(members, count), decision);
        else
            OpenPicker($"Block with how many {name}?", 0, members.Count, members.Count,
                count => ChoosePendingBlockers(members, count), decision);
    }

    private void ApplyBlockCount(IReadOnlyList<CardId> members, int count)
    {
        var (_, remove) = BattlefieldLayout.Pick(members, _blocks.ContainsKey, count);
        foreach (var id in remove) _blocks.Remove(id);
        Refresh();
    }

    private void ChoosePendingBlockers(IReadOnlyList<CardId> members, int count)
    {
        ClearPendingBlockers();
        if (count > 0)
        {
            _pendingBlocker = members[0];
            _pendingGroup.AddRange(members.Skip(1).Take(count - 1));
        }
        Refresh();
    }

    private void OnCardClicked(CardNode node)
    {
        var id = node.Id;
        switch (_session.CurrentDecision)
        {
            case TargetDecision:
                PickTarget(Arcanum.Engine.Abilities.Target.Of(id));
                return;

            case PriorityDecision p:
            {
                // Everything a click on this card could mean: play or cast it (or its Adventure), or use one of its abilities.
                var options = SourceActions(p, id);
                if (options.Count == 1 && options[0] is PlayLand or CastSpell && node.View?.Zone == Arcanum.Engine.State.Zone.Hand)
                {
                    // Click plays it on release; dragging it out of the hand plays it too.
                    var area = AreaOf(node.View.Owner);
                    _drag = new DragState { Node = node, Area = area, Start = GetGlobalMousePosition(), Decision = p, Action = options[0] };
                    return;
                }
                if (options.Count == 1) { p.Answer(options[0]); return; }
                if (options.Count > 1) _abilityChoiceSource = id; // several options (a card and its Adventure, cycling, abilities) open a chooser
                break;
            }

            case ManaPaymentDecision pay:
            {
                int staged = _staged.FindIndex(t => t.Source == id);
                if (staged >= 0) _staged.RemoveAt(staged); // untap: pick something else instead
                else if (pay.Request.Sources.FirstOrDefault(s => s.Source == id && s.Combination && UsefulType(s, RemainingCost(pay)) is not null) is { } comboSource)
                    _comboSource = comboSource; // choose the combination in the action panel
                else if (pay.Request.Sources.Where(s => s.Source == id).Select(s => (Source: s, Type: UsefulType(s, RemainingCost(pay))))
                             .FirstOrDefault(x => x.Type is not null) is { Type: { } type } useful)
                    _staged.Add(new ManaTap(id, type, useful.Source.Option)); // can never tap more than what is still needed
                break;
            }

            case DamageAssignmentDecision dmg when dmg.Request.Blockers.Contains(id):
                // Clicking a blocker adds one damage to it.
                if (_damageSplit.Values.Sum() + _damageToPlayer < dmg.Request.Power) _damageSplit[id] = _damageSplit.GetValueOrDefault(id) + 1;
                break;

            case AttackDecision a when a.PossibleAttackers.Contains(id):
                // Identical creatures (a stack of tokens): choose how many of them attack.
                if (SameKindAmong(node, a.PossibleAttackers) is { Count: > 1 } sameAttackers)
                {
                    OpenAttackPicker(a, sameAttackers, node.View?.Name);
                    return;
                }
                if (_attackTargets.Remove(id))
                {
                    _attackGroup.Remove(id);
                    _attackWalkers.Remove(id);
                }
                else
                {
                    _attackTargets[id] = AllowedDefender(a, id, _attackDefender ?? a.Defenders[0]);
                    if (_attackWalker is { } w) _attackWalkers[id] = w;
                    _attackGroup.Add(id);
                }
                break;

            case AttackDecision a when node.View is { } pw && (pw.Types & Arcanum.Engine.Cards.CardType.Planeswalker) != 0 && a.Defenders.Contains(pw.Controller):
                ChooseAttackTarget(pw.Controller, pw.Id);
                return;

            case BlockDecision b:
                if (b.PossibleBlockers.Contains(id))
                {
                    var stack = AreaHolding(node)?.StackMembers(id) ?? new[] { id };
                    var same = SameKindAmong(node, b.PossibleBlockers);
                    if (IsPendingBlocker(id)) ClearPendingBlockers(); // picked again: not blocking after all
                    else if (_blocks.ContainsKey(id) && stack.Count > 1 && stack.All(_blocks.ContainsKey))
                    {
                        OpenBlockPicker(b, node.View?.Name, stack, blocking: true);
                        return;
                    }
                    else if (_blocks.Remove(id)) ClearPendingBlockers();
                    else if (same is { Count: > 1 } && same.Where(m => !_blocks.ContainsKey(m)).ToList() is { Count: > 1 } idle)
                    {
                        OpenBlockPicker(b, node.View?.Name, idle, blocking: false);
                        return;
                    }
                    else
                    {
                        ClearPendingBlockers();
                        _pendingBlocker = id;
                    }
                }
                else if (b.Attackers.Contains(id) && _pendingBlocker is { } blocker && b.Request.CanBlock[blocker].Contains(id))
                {
                    foreach (var picked in PendingBlockers().Where(p => b.Request.CanBlock[p].Contains(id)))
                        _blocks[picked] = id;
                    ClearPendingBlockers();
                }
                break;

            case SelectCardsDecision s when node.View?.Zone == Arcanum.Engine.State.Zone.Hand && node.View.Owner == s.Player:
                if (!_selected.Remove(id) && _selected.Count < s.Count) _selected.Add(id);
                break;

            case ChooseCardsDecision c when c.Request.Options.Any(o => o.Id == id):
                if (!_selected.Remove(id) && _selected.Count < c.Request.Max) _selected.Add(id);
                break;

            default:
                return;
        }
        Refresh();
    }

    // ---------------------------------------------------------------- preview, log, game over

    private void ShowPreview(CardNode node)
    {
        if (node.View is not { IsHidden: false } view) return;
        _preview.MirrorFoil(node); // a foil card shines on the preview as it does under the pointer
        _preview.Setup(view, showCostPips: false);
        _preview.Position = new Vector2(16, (Size.Y - _preview.Size.Y) / 2);
        _preview.Visible = true;
        // Beside it: what effects changed about the card and its state now.
        var game = _session.ViewFor(Bottom);
        if (_status.ShowFor(game.FindCard(view.Id) ?? view, game))
            _status.Position = _preview.Position + new Vector2(_preview.Size.X + 12, 0);
    }

    private void HidePreview(CardNode node)
    {
        _preview.MirrorFoil(null);
        _preview.Visible = false;
        _status.Visible = false;
    }

    private bool _refreshQueued;

    private void OnGameEvent(EventView ev)
    {
        var e = ev.Event;
        if (e is PlayerLost lost) _lost.Add(lost);
        if (!_session.IsReplaying)
        {
            PlayEffect(ev);
            // The board also follows actions taken without a local decision (the computer's turn): redraw once per frame.
            if (_session.FollowsOthers && !_refreshQueued)
            {
                _refreshQueued = true;
                Callable.From(() => { _refreshQueued = false; Refresh(); }).CallDeferred();
            }
        }
        var line = EventLogFormatter.Format(ev, PlayerName, _session.RevealAll);
        if (line is null) return;
        if (_autoplay && e is TurnBegan or PlayerLost or GameEnded) GD.Print(line); // smoke tests follow the game in the console
        _log.AppendText((e is TurnBegan ? "\n[b]" + line + "[/b]" : line) + "\n");
        if (!_logPanel.Visible) _unreadLog++;
        UpdateLogBadge();
    }

    /// <summary>
    /// Visual feedback for an event. Runs while the engine is mid-resolution, before the board redraws, so card
    /// nodes are still where the player last saw them.
    /// </summary>
    /// <summary>
    /// The opening hand, large and centered on the local half, while a mulligan is being decided; the same view shows
    /// the cards of any other card choice (scry, surveil...).
    /// </summary>
    private void UpdateMulliganView(Decision? decision)
    {
        if (decision is TargetDecision td && _chosenTargets.Count < td.Request.Specs.Count
            && td.Request.Specs[_chosenTargets.Count].Kind == Arcanum.Engine.Abilities.TargetKind.GraveyardCard)
        {
            // Cards in graveyards aren't on the board: show the legal ones large to pick from.
            var options = td.Request.Legal[_chosenTargets.Count].Where(t => t.Card is not null)
                .Select(t => ViewBuilderCard(t.Card!.Value, decision.Player)).ToList();
            _mulligan.ShowHand(options, $"Choose {td.Request.Specs[_chosenTargets.Count].Describe()}", new HashSet<CardId>(), selectable: true);
            return;
        }
        if (decision is ChooseCardsDecision choice)
        {
            var who = PlayerName(choice.Player);
            _mulligan.ShowHand(choice.Request.Options, $"{who}: {choice.Request.Prompt}", _selected, selectable: true);
            return;
        }
        bool bottom = decision is SelectCardsDecision { Reason: SelectCardsReason.MulliganBottom };
        if (decision is not (MulliganDecision or SelectCardsDecision { Reason: SelectCardsReason.MulliganBottom }))
        {
            _mulligan.HideHand();
            return;
        }
        // The deciding player's own view: in hotseat that's their hand even when hands are hidden from each other.
        var view = _session.ViewFor(decision.Player);
        var name = view.Players[decision.Player.Value].Name;
        var title = decision switch
        {
            MulliganDecision { MulligansTaken: 0 } => $"{name}: opening hand",
            MulliganDecision m => $"{name}: new hand ({m.MulligansTaken} mulligan{(m.MulligansTaken > 1 ? "s" : "")})",
            SelectCardsDecision s => $"{name}: choose {s.Count} card{(s.Count > 1 ? "s" : "")} to put on the bottom",
            _ => name,
        };
        _mulligan.ShowHand(view.Players[decision.Player.Value].Hand, title, _selected, selectable: bottom);
    }

    private CardView ViewBuilderCard(CardId id, PlayerId viewer) =>
        _session.ViewFor(viewer).FindCard(id) ?? new CardView { Id = id, Owner = viewer, Controller = viewer, Zone = Arcanum.Engine.State.Zone.Graveyard, IsHidden = true };

    private string PlayerName(PlayerId player) => _session.ViewFor(Bottom).Players[player.Value].Name;

    /// <summary>Completes once queued announcements and short holds (e.g. after combat damage) are over.</summary>
    private async Task PresentationAsync()
    {
        while (IsInstanceValid(this) && (_announcer.Busy || Time.GetTicksMsec() < _holdUntilMs || _combatFlushQueued))
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
    }

    private void Hold(double seconds) =>
        _holdUntilMs = Math.Max(_holdUntilMs, Time.GetTicksMsec() + (ulong)(seconds * BoardStyle.AnimationScale * 1000));

    /// <summary>Turns what other players (the computer, people elsewhere) do into announcements, and holds the game on key moments.</summary>
    private void Announce(EventView ev)
    {
        // A look at a hand lasts until a card is chosen from it or the spell or ability is done.
        if (_handLookedAt is { } looked && ev.Event is ChosenFromHand or SpellResolved or AbilityResolved or PriorityGiven or StepBegan)
        {
            AreaOf(looked).SetHandNote(null);
            _handLookedAt = null;
        }
        switch (ev.Event)
        {
            case TurnBegan t:
                AnnounceTurn(t);
                break;
            case HandLookedAt l when _session.Announces(l.Looker):
                AnnounceHandLook(l.Looker, l.Player);
                break;
            case HandRevealed { Chooser: { } chooser } h when _session.Announces(chooser):
                AnnounceHandLook(chooser, h.Player);
                break;
            case ChosenFromHand c when _session.Announces(c.Chooser):
                foreach (var card in c.Cards)
                    _announcer.Enqueue(new($"{PlayerName(c.Chooser)} chooses {ev.Name(card)} from {Whose(c.Player)} hand", ViewOf(ev, card), 2.5));
                break;
            case SpellCast c when _session.Announces(c.Player):
                AnnounceStackObject(ev, c.Player, c.Card, "casts", null);
                break;
            case AbilityActivated a when _session.Announces(a.Player):
                AnnounceStackObject(ev, a.Player, a.Source, "activates", a.Text);
                break;
            case AbilityTriggered t when _session.Announces(t.Controller):
                AnnounceStackObject(ev, t.Controller, t.Source, "\u2014 triggered:", t.Text);
                break;
            case AttackerDeclared a when ev.Card(a.Attacker) is { } attacker && _session.Announces(attacker.Controller):
                _pendingAttacks.Add(ev);
                QueueCombatFlush();
                break;
            case BlockerDeclared b when ev.Card(b.Blocker) is { } blocker && _session.Announces(blocker.Controller):
                _pendingBlocks.Add(ev);
                QueueCombatFlush();
                break;
            case DamageDealt { IsCombat: true }:
                Hold(1.2); // let the damage numbers be seen before the game moves on
                break;
            case PermanentDestroyed d:
                _announcer.Enqueue(new($"{ev.Name(d.Card)} is destroyed", ViewOf(ev, d.Card), 1.5));
                break;
            case CardMoved { From: Arcanum.Engine.State.Zone.Battlefield, To: Arcanum.Engine.State.Zone.Exile } m when !_session.ViewFor(Bottom).Players[m.Owner.Value].HasLost:
                _announcer.Enqueue(new($"{ev.Name(m.Card)} is exiled", ViewOf(ev, m.Card), 1.5));
                break;
            case CardMoved { From: Arcanum.Engine.State.Zone.Battlefield, To: Arcanum.Engine.State.Zone.Hand } m:
                _announcer.Enqueue(new($"{ev.Name(m.Card)} returns to {PlayerName(m.Owner)}'s hand", ViewOf(ev, m.Card), 1.5));
                break;
            case PlayerLost l:
                _announcer.Enqueue(new($"{PlayerName(l.Player)} loses: {l.Reason}", null, 2.5));
                break;
        }
    }

    /// <summary>The large "whose turn" banner (queued like any announcement), with a soft chime when the turn is yours.</summary>
    private void AnnounceTurn(TurnBegan turn)
    {
        bool own = turn.ActivePlayer == Bottom;
        _announcer.Enqueue(new(own ? "Your turn" : $"{PlayerName(turn.ActivePlayer)}'s turn", null, 1.1,
            Turn: new($"Turn {turn.TurnNumber}", BoardStyle.PlayerColor(turn.ActivePlayer.Value)),
            OnShown: own ? _turnChime.Chime : null));
    }

    private string Whose(PlayerId player) => player == Bottom ? "your" : $"{PlayerName(player)}'s";

    /// <summary>Another player looks at a hand (to choose from it, or just to see it): said, and noted beside that hand while it lasts.</summary>
    private void AnnounceHandLook(PlayerId looker, PlayerId owner)
    {
        var text = $"{PlayerName(looker)} is looking at {Whose(owner)} hand";
        _announcer.Enqueue(new(text, null, 2));
        if (_handLookedAt is { } earlier) AreaOf(earlier).SetHandNote(null);
        AreaOf(owner).SetHandNote(text);
        _handLookedAt = owner;
    }

    /// <summary>The card as it was when the event happened (falls back to the current view).</summary>
    private CardView? ViewOf(EventView ev, CardId id) =>
        ev.Card(id) is { IsHidden: false } v ? v : _session.ViewFor(Bottom).FindCard(id) is { IsHidden: false } now ? now : null;

    /// <summary>A spell or ability just put on the stack: its card, what it does, and arrows to its targets.</summary>
    private void AnnounceStackObject(EventView ev, PlayerId player, CardId source, string verb, string? abilityText)
    {
        var targets = ev.Stack?.Targets ?? (IReadOnlyList<Arcanum.Engine.Abilities.Target>)Array.Empty<Arcanum.Engine.Abilities.Target>();
        string TargetName(Arcanum.Engine.Abilities.Target t) =>
            t.Player is { } p ? (p == Bottom ? "you" : PlayerName(p)) : t.Card is { } c ? ev.Name(c) : "an ability";
        var text = $"{PlayerName(player)} {verb} {ev.Name(source)}";
        if (abilityText is { Length: > 0 }) text += $"\n{abilityText}";
        if (targets.Count > 0) text += $"\n\u2192 {string.Join(", ", targets.Select(TargetName))}";
        _announcer.Enqueue(new(text, ViewOf(ev, source), targets.Count > 0 ? 3 : 1.5,
            card => targets.Select(TargetControl).OfType<Control>().Select(to => new ArrowLayer.Arrow(card, to, BoardStyle.Attacking))));
    }

    private void QueueCombatFlush()
    {
        if (_combatFlushQueued) return;
        _combatFlushQueued = true;
        Callable.From(FlushCombat).CallDeferred(); // all declarations of a step arrive in the same frame
    }

    /// <summary>One announcement per attacking (or blocking) player, with arrows, so attacks are seen before damage.</summary>
    private void FlushCombat()
    {
        _combatFlushQueued = false;
        foreach (var group in _pendingAttacks.GroupBy(ev => ev.Card(((AttackerDeclared)ev.Event).Attacker)!.Controller))
        {
            var attacks = group.Select(ev => (Ev: ev, Attack: (AttackerDeclared)ev.Event)).ToList();
            var defenders = attacks.Select(a => a.Attack.Defender).Distinct().Select(d => d == Bottom ? "you" : PlayerName(d));
            var text = $"{PlayerName(group.Key)} attacks {string.Join(" and ", defenders)} with " +
                       string.Join(", ", attacks.Select(a => a.Ev.Name(a.Attack.Attacker)));
            _announcer.Enqueue(new(text, null, 2, _ => attacks
                .Select(a => (From: FindCard(a.Attack.Attacker), To: (Control)AreaOf(a.Attack.Defender).LifeBox))
                .Where(x => x.From is not null)
                .Select(x => new ArrowLayer.Arrow(x.From!, x.To, BoardStyle.Attacking))));
        }
        foreach (var group in _pendingBlocks.GroupBy(ev => ev.Card(((BlockerDeclared)ev.Event).Blocker)!.Controller))
        {
            var blocks = group.Select(ev => (Ev: ev, Block: (BlockerDeclared)ev.Event)).ToList();
            var text = $"{PlayerName(group.Key)} blocks: " +
                       string.Join(", ", blocks.Select(b => $"{b.Ev.Name(b.Block.Blocker)} \u2192 {b.Ev.Name(b.Block.Attacker)}"));
            _announcer.Enqueue(new(text, null, 1.5, _ => blocks
                .Select(b => (From: FindCard(b.Block.Blocker), To: FindCard(b.Block.Attacker)))
                .Where(x => x.From is not null && x.To is not null)
                .Select(x => new ArrowLayer.Arrow(x.From!, x.To!, BoardStyle.Blocking))));
        }
        _pendingAttacks.Clear();
        _pendingBlocks.Clear();
    }

    private void PlayEffect(EventView ev)
    {
        Announce(ev);
        switch (ev.Event)
        {
            case DamageDealt { TargetCard: { } card } d when FindCard(card) is { } node:
                SpawnFloatingText($"-{d.Amount}", node.GetGlobalTransform() * (node.Size / 2), BoardStyle.Attacking);
                break;
            case LifeChanged { NewLife: var now, OldLife: var before } l when now > before:
                SpawnFloatingText($"+{now - before}", AreaOf(l.Player).LifeGlobalCenter + new Vector2(0, 62), new Color("6fd08c"), rise: 30);
                break;
            case DamageDealt { TargetPlayer: { } player } d:
                var area = AreaOf(player);
                // Beside the counter and rising only a little, so it stays on screen for the top player too.
                SpawnFloatingText($"-{d.Amount}", area.LifeGlobalCenter + new Vector2(0, 62), BoardStyle.Attacking, rise: 30);
                area.FlashLife(BoardStyle.Attacking);
                break;
        }
    }

    private void SpawnFloatingText(string text, Vector2 globalCenter, Color color, float rise = 60)
    {
        var label = BoardStyle.MakeLabel(text, 34, color);
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        label.ZIndex = BoardStyle.Z.Floaters;
        AddChild(label);
        label.ResetSize();
        label.GlobalPosition = globalCenter - label.Size / 2;
        var tween = label.CreateTween().SetParallel();
        float k = BoardStyle.AnimationScale;
        tween.TweenProperty(label, "position:y", label.Position.Y - rise, 1.1 * k).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(label, "modulate:a", 0.0f, 0.5 * k).SetDelay(0.6 * k);
        tween.Chain().TweenCallback(Callable.From(label.QueueFree));
    }

    private void ToggleLog()
    {
        _logPanel.Visible = !_logPanel.Visible;
        if (_logPanel.Visible) _unreadLog = 0;
        UpdateLogBadge();
    }

    private void UpdateLogBadge()
    {
        _logBadge.Text = _unreadLog > 99 ? "99+" : _unreadLog.ToString();
        _logBadge.GetParent<Control>().Visible = _unreadLog > 0;
    }

    private void ShowGameOver(string text)
    {
        var lines = text.Split('\n', 2);
        _gameOverText.Text = lines[0];
        _gameOverReasons.Text = lines.Length > 1 ? lines[1] : "";
        _gameOver.Visible = true;
        _actionPanel.Visible = false;
        bool inEvent = _match?.Event is not null || (_online is not null && App.Instance.Online.EventSession is not null);
        if (_newGameButton is not null) _newGameButton.Visible = !inEvent && _online is null;
        if (_backToEventButton is not null) _backToEventButton.Visible = inEvent;
    }
}
