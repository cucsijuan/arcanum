// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// Bridges the engine and the Godot UI: builds a game, runs it on the main thread (decisions are awaited,
/// so the engine never blocks a frame), tells the board when to redraw and supports undo by replay.
/// </summary>
public sealed class GameSession : IBoardSession
{
    /// <param name="IsBot">Played by the computer instead of a person on this device.</param>
    public sealed record Seat(string Name, IReadOnlyList<CardDefinition> Deck, bool IsBot = false, IReadOnlyList<CardDefinition>? Commanders = null);

    private readonly GameConfig _config;
    private readonly IReadOnlyList<Seat> _seats;
    private readonly Action<Game>? _setup;

    public Game Game { get; }
    public DecisionHub Hub { get; } = new();
    public DecisionLog Log { get; }

    /// <summary>Stop settings; shared by both seats in hotseat, since one person controls both.</summary>
    public AutoPassPolicy Policy { get; }

    /// <summary>Show every hidden card (deck-test / hotseat mode).</summary>
    public bool RevealAll { get; set; } = true;

    /// <summary>Raised when the board should redraw: a decision is pending or the game ended.</summary>
    public event Action? Changed;

    public event Action<Exception>? Failed;

    public event Action<EventView>? EventRaised;

    /// <summary>Seat 0 is shown at the bottom (in hotseat, everyone shares that screen).</summary>
    public PlayerId LocalSeat => new(0);

    public bool Announces(PlayerId player) => IsBot(player);

    public bool FollowsOthers => HasBot;

    public bool IsReplaying => Log.IsReplaying;

    public void Poll() { }

    public void Leave() { }

    private GameSession(GameConfig config, IReadOnlyList<Seat> seats, AutoPassPolicy policy, IEnumerable<DecisionLog.Entry>? replay,
        Action<Game>? setup)
    {
        _config = config;
        _seats = seats;
        _setup = setup;
        Policy = policy;
        Log = new DecisionLog(replay);
        var bots = new List<Arcanum.Bots.BotController>();
        Game = new Game(config, seats.Select((s, i) =>
        {
            if (s.IsBot)
            {
                var bot = new Arcanum.Bots.BotController(new PlayerId(i)) { Pace = BotPaceAsync, AlwaysKeep = setup is not null };
                bots.Add(bot);
                return new PlayerSetup(s.Name, bot, s.Deck, s.Commanders);
            }
            var controller = new UiPlayerController(new PlayerId(i), Hub, Log, policy, PresentAsync);
            _controllers.Add(controller);
            return new PlayerSetup(s.Name, controller, s.Deck, s.Commanders);
        }).ToList());
        // Bots read printed rules only for cards they can see (the controller enforces that).
        foreach (var bot in bots) bot.UseCardRules(id => Game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        HasBot = bots.Count > 0;
        setup?.Invoke(Game); // sandbox: pre-placed permanents and cards, re-applied identically on undo
        Hub.DecisionRequested += _ => Changed?.Invoke();
        Game.EventRaised += e =>
        {
            EventRaised?.Invoke(EventViews.Build(Game.State, e, LocalSeat, RevealAll, config.Commander?.TaxPerCast ?? 0));
            if (e is GameEnded) Changed?.Invoke();
        };
    }

    /// <summary>Two local seats sharing one screen, with the given decks.</summary>
    /// <param name="setup">Optional sandbox setup run on the new game before it starts.</param>
    public static GameSession CreateHotseat(ulong seed, Seat first, Seat second, Action<Game>? setup = null,
        int startingLife = 20, AutoPassPolicy? policy = null, bool confirmManaPayment = true) =>
        CreateMatch(seed, new[] { first, second }, setup, startingLife, policy, confirmManaPayment, commander: false);

    /// <summary>A game for any number of seats (people on this device and/or the computer).</summary>
    /// <param name="commander">Use commander rules (command zone, tax, commander damage).</param>
    public static GameSession CreateMatch(ulong seed, IReadOnlyList<Seat> seats, Action<Game>? setup, int startingLife,
        AutoPassPolicy? policy, bool confirmManaPayment, bool commander) =>
        new(new GameConfig
            {
                Seed = seed,
                StartingLife = startingLife,
                StartingPlayer = setup is null ? null : new PlayerId(0),
                Commander = commander ? new CommanderRules() : null,
                CardNames = App.Instance?.Cards?.Names,
                NonbasicLandNames = App.Instance?.Cards?.NonbasicLandNames,
            },
            seats, policy ?? new AutoPassPolicy(), replay: null, setup) { ConfirmManaPayment = confirmManaPayment };

    public int PlayerCount => _seats.Count;

    /// <summary>Ask players to confirm each mana payment (applies to every local seat).</summary>
    public bool ConfirmManaPayment
    {
        get => _confirmManaPayment;
        init
        {
            _confirmManaPayment = value;
            foreach (var controller in _controllers) controller.ConfirmManaPayment = value;
        }
    }

    private readonly bool _confirmManaPayment = true;
    private readonly List<UiPlayerController> _controllers = new();

    /// <summary>Offline demo with Arcanum's generic cards, used when no content module is available.</summary>
    public static GameSession CreateHotseatDemo(ulong seed)
    {
        static IReadOnlyList<CardDefinition> Deck(params (CardDefinition Card, int Count)[] entries) =>
            entries.SelectMany(e => Enumerable.Repeat(e.Card, e.Count)).ToList();

        var green = Deck((GenericCards.Forest, 17), (GenericCards.GladeCub, 14), (GenericCards.GreatWurm, 4),
            (GenericCards.Mountain, 3), (GenericCards.OgreBrute, 2));
        var red = Deck((GenericCards.Mountain, 17), (GenericCards.OgreBrute, 10), (GenericCards.HillBrute, 8),
            (GenericCards.StoneElemental, 5));

        return new GameSession(new GameConfig { Seed = seed },
            new[] { new Seat("Player 1", green), new Seat("Player 2", red) }, new AutoPassPolicy(), replay: null, setup: null);
    }

    public Decision? CurrentDecision => Hub.Current is { IsAnswered: false } d ? d : null;

    public bool HasBot { get; }

    /// <summary>Waits until the board has finished presenting recent events (set by the board).</summary>
    public Func<Task>? Presentation { get; set; }

    public bool IsBot(PlayerId player) => _seats[player.Value].IsBot;

    private Task PresentAsync() => Log.IsReplaying || Presentation is null ? Task.CompletedTask : Presentation();

    /// <summary>Short pause before each visible bot action (skipped while replaying for undo).</summary>
    private async Task BotPaceAsync()
    {
        if (Log.IsReplaying || Godot.Engine.GetMainLoop() is not SceneTree tree) return;
        await PresentAsync(); // let the previous action finish being shown first
        var timer = tree.CreateTimer(0.55 * UI.Board.BoardStyle.AnimationScale);
        await tree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    public GameView ViewFor(PlayerId viewer) => Game.ViewFor(viewer, RevealAll);

    public bool CanUndo => Log.Entries.Exists(e => e.Manual);

    /// <summary>
    /// A new session at the moment the last decision made by a person was asked: same seed, every earlier answer
    /// replayed. Returns null if nothing can be undone.
    /// </summary>
    public GameSession? CreateUndo()
    {
        int last = Log.Entries.FindLastIndex(e => e.Manual);
        if (last < 0) return null;
        Policy.CancelPassTurn();
        return new GameSession(_config, _seats, Policy, Log.Entries.Take(last).ToList(), _setup)
        {
            RevealAll = RevealAll,
            ConfirmManaPayment = ConfirmManaPayment,
        };
    }

    public async void Start()
    {
        try
        {
            await Game.RunAsync();
        }
        catch (Exception e)
        {
            GD.PushError($"Game crashed: {e}");
            Failed?.Invoke(e);
        }
    }
}
