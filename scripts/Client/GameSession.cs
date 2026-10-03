// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// Bridges the engine and the Godot UI: builds a game, runs it on the main thread (decisions are awaited,
/// so the engine never blocks a frame) and tells the board when to redraw.
/// </summary>
public sealed class GameSession
{
    public Game Game { get; }
    public DecisionHub Hub { get; }

    /// <summary>Show every hidden card (deck-test / hotseat mode).</summary>
    public bool RevealAll { get; set; } = true;

    /// <summary>Raised when the board should redraw: a decision is pending or the game ended.</summary>
    public event Action? Changed;

    public event Action<Exception>? Failed;

    private GameSession(Game game, DecisionHub hub)
    {
        Game = game;
        Hub = hub;
        hub.DecisionRequested += _ => Changed?.Invoke();
        game.EventRaised += e => { if (e is GameEnded) Changed?.Invoke(); };
    }

    public static GameSession CreateHotseatDemo(ulong seed)
    {
        static IReadOnlyList<CardDefinition> Deck(params (CardDefinition Card, int Count)[] entries) =>
            entries.SelectMany(e => Enumerable.Repeat(e.Card, e.Count)).ToList();

        var green = Deck((CoreCards.Forest, 17), (CoreCards.GrizzlyBears, 14), (CoreCards.CrawWurm, 4),
            (CoreCards.Mountain, 3), (CoreCards.GrayOgre, 2));
        var red = Deck((CoreCards.Mountain, 17), (CoreCards.GrayOgre, 10), (CoreCards.HillGiant, 8),
            (CoreCards.EarthElemental, 5));

        var hub = new DecisionHub();
        var game = new Game(new GameConfig { Seed = seed }, new[]
        {
            new PlayerSetup("Player 1", new UiPlayerController(new PlayerId(0), hub), green),
            new PlayerSetup("Player 2", new UiPlayerController(new PlayerId(1), hub), red),
        });
        return new GameSession(game, hub);
    }

    public Decision? CurrentDecision => Hub.Current is { IsAnswered: false } d ? d : null;

    public GameView ViewFor(PlayerId viewer) => Game.ViewFor(viewer, RevealAll);

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
