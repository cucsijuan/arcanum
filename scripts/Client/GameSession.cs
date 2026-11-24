// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Godot;

using Sim = Arcanum.Bots.Simulation.Simulator;

namespace Arcanum.Client;

/// <summary>
/// Bridges the engine and the Godot UI: builds a game, runs it on the main thread (decisions are awaited,
/// so the engine never blocks a frame), tells the board when to redraw and supports undo by replay.
/// </summary>
public sealed class GameSession : IBoardSession
{
    /// <param name="IsBot">Played by the computer instead of a person on this device.</param>
    public sealed record Seat(string Name, IReadOnlyList<CardDefinition> Deck, bool IsBot = false, IReadOnlyList<CardDefinition>? Commanders = null,
        IReadOnlyList<CardDefinition>? Sideboard = null);

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
        var setups = seats.Select((s, i) =>
        {
            if (s.IsBot)
            {
                var bot = new Arcanum.Bots.BotController(new PlayerId(i)) { Pace = BotPaceAsync, AlwaysKeep = setup is not null };
                bots.Add(bot);
                bot.KnowDeck(s.Deck, s.Commanders);
                return new PlayerSetup(s.Name, bot, s.Deck, s.Commanders, s.Sideboard);
            }
            var controller = new UiPlayerController(new PlayerId(i), Hub, Log, policy, PresentAsync);
            _controllers.Add(controller);
            return new PlayerSetup(s.Name, controller, s.Deck, s.Commanders, s.Sideboard);
        }).ToList();
        // Bots that think ahead copy or rebuild this game from every seat's recorded answers to try their options.
        var search = SearchSettings(() => LimitedSet);
        bool searching = bots.Count > 0 && search is not null;
        Arcanum.Bots.Simulation.GameRecording? recording = null;
        if (search is not null && searching)
        {
            var (made, recorded) = Arcanum.Bots.Simulation.GameRecording.Record(config, setups, setup, search.Candidates);
            recording = made;
            setups = recorded.ToList();
            foreach (var bot in bots)
                bot.Search = new Arcanum.Bots.Simulation.Simulator(made, bot.Player) { Budget = search.Budget, Rollouts = search.Rollouts, Horizon = search.Horizon };
        }
        Game = new Game(config, setups);
        if (recording is not null) recording.Live = () => Game; // copied at the bots' decisions where it can be
        // Bots read printed rules only for cards they can see (the controller enforces that).
        foreach (var bot in bots) bot.UseCardRules(id => Game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        HasBot = bots.Count > 0;
        setup?.Invoke(Game); // sandbox: pre-placed permanents and cards, re-applied identically on undo
        Hub.DecisionRequested += _ => Changed?.Invoke();
        Game.EventRaised += e =>
        {
            EventRaised?.Invoke(EventViews.Build(Game.State, e, LocalSeat, RevealAll, config.Commander?.TaxPerCast ?? 0));
            if (e is GameEnded)
            {
                if (searching) LogSearch();
                Changed?.Invoke();
            }
        };
    }

    /// <summary>Two local seats sharing one screen, with the given decks.</summary>
    /// <param name="setup">Optional sandbox setup run on the new game before it starts.</param>
    public static GameSession CreateHotseat(ulong seed, Seat first, Seat second, Action<Game>? setup = null,
        int startingLife = 20, AutoPassPolicy? policy = null, bool confirmManaPayment = true) =>
        CreateMatch(seed, new[] { first, second }, setup, startingLife, policy, confirmManaPayment, commander: false);

    /// <summary>A game for any number of seats (people on this device and/or the computer).</summary>
    /// <param name="commander">Use commander rules (command zone, tax, commander damage).</param>
    /// <param name="mode">A game mode (plugin) adding its rules and settings.</param>
    public static GameSession CreateMatch(ulong seed, IReadOnlyList<Seat> seats, Action<Game>? setup, int startingLife,
        AutoPassPolicy? policy, bool confirmManaPayment, bool commander, Arcanum.Data.GameModes.GameModeScript? mode = null,
        string? limitedSet = null) =>
        new(WithMode(mode, new GameConfig
            {
                Seed = seed,
                StartingLife = startingLife,
                StartingPlayer = setup is null ? null : new PlayerId(0),
                Commander = commander ? new CommanderRules() : null,
                CardNames = App.Instance?.Cards?.Names,
                NonbasicLandNames = App.Instance?.Cards?.NonbasicLandNames,
                CreatureCardNames = App.Instance?.Cards?.CreatureCardNames,
            }),
            seats, policy ?? new AutoPassPolicy(), replay: null, setup) { ConfirmManaPayment = confirmManaPayment, LimitedSet = limitedSet };

    /// <summary>
    /// The most time a computer player thinking ahead spends on one decision: longer on devices with few processors (a
    /// handheld console needs about 4.5 s for a full search, measured), so the search finishes there too.
    /// </summary>
    public static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(System.Environment.ProcessorCount >= 6 ? 2 : 5);

    /// <summary>How long the computer's thinking ahead took so far (all games of this run), for measuring on slow devices.</summary>
    private void LogSearch()
    {
        int n = Arcanum.Bots.BotController.SearchedDecisions;
        if (n == 0) return;
        double avg = System.Diagnostics.Stopwatch.GetElapsedTime(0, Arcanum.Bots.BotController.SearchTicks / n).TotalMilliseconds;
        double longest = System.Diagnostics.Stopwatch.GetElapsedTime(0, Arcanum.Bots.BotController.LongestSearchTicks).TotalMilliseconds;
        GD.Print($"bot search: {n} decisions, {avg:0} ms each, longest {longest:0} ms, {Sim.RoundsPlayed / (double)n:0.0} rounds each, " +
                 $"{Sim.OutOfTime} out of time, {Arcanum.Bots.BotController.GaveUp} gave up; {Sim.CopiedRollouts} rollouts copied/{Sim.ReplayedRollouts} rebuilt, " +
                 $"{Sim.Copies} copies {System.Diagnostics.Stopwatch.GetElapsedTime(0, Sim.CopyTicks / Math.Max(1, Sim.Copies)).TotalMilliseconds:0.0} ms each; {System.Environment.ProcessorCount} processors, turn {Game.State.TurnNumber}");
    }

    /// <summary>The set of a draft or sealed game: the computer imagines its opponents' hidden cards from it.</summary>
    public string? LimitedSet { get; init; }

    /// <summary>
    /// The cards the computer may imagine in an opponent's hidden hand and library: from the set being played in limited,
    /// otherwise from the sets of the cards it has seen (never from the opponent's deck).
    /// </summary>
    private static Arcanum.Bots.Simulation.CardCandidates? Candidates(Func<string?> limitedSet)
    {
        if (App.Instance?.Cards is not { } db) return null;
        var module = App.Instance.Module;
        // Read when first asked (during the game), once the limited set is known.
        var guess = new Lazy<Arcanum.Data.CardData.CardPoolGuess>(() => new Arcanum.Data.CardData.CardPoolGuess(db, limitedSet(), set => module?.LoadDraftRatings(set)));
        return seen => guess.Value.For(seen).Select(x => new Arcanum.Bots.Simulation.CardGuess(x.Card, x.Weight)).ToList();
    }

    /// <summary>
    /// How the computer thinks ahead on this device, or null when it doesn't (the <b>Computer thinks ahead</b> setting), for
    /// the games played here and those this device hosts online. ARCANUM_BOT_SEARCH: "off", "on", or the most milliseconds
    /// one decision may take; ARCANUM_BOT_SEARCH_SHAPE=&lt;rollouts&gt;x&lt;turns&gt;: how many games each option is played on, and for
    /// how many turns (for testing on a device).
    /// </summary>
    public static Arcanum.Net.Host.ComputerSearch? SearchSettings(Func<string?> limitedSet)
    {
        var forced = OS.GetEnvironment("ARCANUM_BOT_SEARCH");
        bool thinks = forced switch { "off" => false, "" => Settings.Current.BotThinksAhead, _ => true };
        if (!thinks) return null;
        var budget = int.TryParse(forced, out var ms) ? TimeSpan.FromMilliseconds(ms) : SearchBudget;
        var shape = OS.GetEnvironment("ARCANUM_BOT_SEARCH_SHAPE").Split('x');
        // Devices with few processors (a handheld console) play 2 games for 2 turns: most of the strength (measured), in time.
        int fallback = System.Environment.ProcessorCount >= 6 ? 4 : 2;
        int rollouts = int.TryParse(shape[0], out var r) ? r : fallback, horizon = shape.Length > 1 && int.TryParse(shape[1], out var h) ? h : fallback;
        return new Arcanum.Net.Host.ComputerSearch(budget, rollouts, horizon, Candidates(limitedSet));
    }

    private static GameConfig WithMode(Arcanum.Data.GameModes.GameModeScript? mode, GameConfig config) => mode?.ApplyTo(config) ?? config;

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
            LimitedSet = LimitedSet,
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
