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
public sealed class GameSession
{
    public sealed record Seat(string Name, IReadOnlyList<CardDefinition> Deck);

    private readonly GameConfig _config;
    private readonly IReadOnlyList<Seat> _seats;

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

    private GameSession(GameConfig config, IReadOnlyList<Seat> seats, AutoPassPolicy policy, IEnumerable<DecisionLog.Entry>? replay)
    {
        _config = config;
        _seats = seats;
        Policy = policy;
        Log = new DecisionLog(replay);
        Game = new Game(config, seats.Select((s, i) =>
            new PlayerSetup(s.Name, new UiPlayerController(new PlayerId(i), Hub, Log, policy), s.Deck)).ToList());
        Hub.DecisionRequested += _ => Changed?.Invoke();
        Game.EventRaised += e => { if (e is GameEnded) Changed?.Invoke(); };
    }

    /// <summary>Two local seats sharing one screen, with the given decks.</summary>
    public static GameSession CreateHotseat(ulong seed, Seat first, Seat second) =>
        new(new GameConfig { Seed = seed }, new[] { first, second }, new AutoPassPolicy(), replay: null);

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
            new[] { new Seat("Player 1", green), new Seat("Player 2", red) }, new AutoPassPolicy(), replay: null);
    }

    public Decision? CurrentDecision => Hub.Current is { IsAnswered: false } d ? d : null;

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
        return new GameSession(_config, _seats, Policy, Log.Entries.Take(last).ToList()) { RevealAll = RevealAll };
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
