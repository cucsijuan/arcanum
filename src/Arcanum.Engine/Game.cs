// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Runs one game: owns the <see cref="GameState"/>, asks controllers for decisions and applies the rules.
/// Deterministic for a given seed and sequence of decisions.
/// </summary>
public sealed partial class Game
{
    private readonly IReadOnlyList<IPlayerController> _controllers;
    private readonly List<GameEvent> _log = new();
    private CancellationToken _ct;

    public GameConfig Config { get; }
    public GameState State { get; }
    public DeterministicRng Rng { get; }
    public IReadOnlyList<GameEvent> Log => _log;

    /// <summary>Raised synchronously for every event, in order.</summary>
    public event Action<GameEvent>? EventRaised;

    public Game(GameConfig config, IReadOnlyList<PlayerSetup> players)
    {
        if (players.Count < 2) throw new ArgumentException("A game needs at least two players.", nameof(players));
        Config = config;
        Rng = new DeterministicRng(config.Seed);
        _controllers = players.Select(p => p.Controller).ToList();

        var statePlayers = players.Select((p, i) => new Player(new PlayerId(i), p.Name, config.StartingLife)).ToList();
        State = new GameState(statePlayers);

        int nextId = 1;
        for (int i = 0; i < players.Count; i++)
        {
            foreach (var def in players[i].Deck)
            {
                var card = new Card(new CardId(nextId++), def, statePlayers[i].Id);
                State.Cards.Add(card.Id, card);
                statePlayers[i].Library.Add(card.Id);
            }
        }
    }

    private bool IsMultiplayer => State.Players.Count > 2;

    /// <summary>
    /// Puts a permanent onto the battlefield before the game starts (deck-test sandbox, rules scenarios).
    /// </summary>
    public CardId SetupPermanent(PlayerId owner, CardDefinition definition) => Setup(owner, definition, Zone.Battlefield);

    /// <summary>Puts a card into a player's hand before the game starts, on top of the normal opening hand.</summary>
    public CardId SetupInHand(PlayerId owner, CardDefinition definition) => Setup(owner, definition, Zone.Hand);

    private CardId Setup(PlayerId owner, CardDefinition definition, Zone zone)
    {
        if (State.TurnNumber > 0) throw new InvalidOperationException("Cards can only be set up before the game starts.");
        var nextId = State.Cards.Count == 0 ? 1 : State.Cards.Keys.Max(k => k.Value) + 1;
        var card = new Card(new CardId(nextId), definition, owner) { Zone = zone };
        State.Cards.Add(card.Id, card);
        if (zone == Zone.Battlefield) State.Battlefield.Add(card.Id);
        else State.GetPlayer(owner).Hand.Add(card.Id);
        return card.Id;
    }

    public GameView ViewFor(PlayerId player, bool revealAll = false) => ViewBuilder.Build(State, player, revealAll);

    private IPlayerController ControllerOf(PlayerId player) => _controllers[player.Value];

    private void Emit(GameEvent e)
    {
        _log.Add(e);
        CollectTriggers(e);
        EventRaised?.Invoke(e);
    }

    /// <summary>Plays the game to completion.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _ct = ct;
        var startingPlayer = Config.StartingPlayer ?? new PlayerId(Rng.Next(State.Players.Count));
        Emit(new GameStarted(startingPlayer, Config.Seed));

        foreach (var player in State.Players) Shuffle(player);
        State.ActivePlayer = startingPlayer;
        foreach (var playerId in State.ApnapOrder().ToList()) await ResolveMulliganAsync(playerId);

        bool firstTurn = true;
        while (!State.IsGameOver)
        {
            if (!firstTurn) State.ActivePlayer = State.NextLivingPlayer(State.ActivePlayer);
            await RunTurnAsync(skipDraw: firstTurn && (Config.StartingPlayerSkipsDraw ?? !IsMultiplayer));
            firstTurn = false;
        }
    }

    private async Task ResolveMulliganAsync(PlayerId playerId)
    {
        var player = State.GetPlayer(playerId);
        bool freeFirst = Config.FreeFirstMulligan ?? IsMultiplayer;
        int handSize = Config.StartingHandSize;
        int mulligans = 0;
        Draw(playerId, handSize);

        int ToBottom(int m) => Math.Max(0, m - (freeFirst ? 1 : 0));

        // A further mulligan is only offered while it would still leave at least one card.
        while (ToBottom(mulligans + 1) < handSize && !await ControllerOf(playerId).KeepHandAsync(ViewFor(playerId), mulligans))
        {
            mulligans++;
            Emit(new MulliganTaken(playerId, mulligans));
            foreach (var card in player.Hand.ToList()) MoveCard(card, Zone.Library);
            Shuffle(player);
            Draw(playerId, handSize);
        }

        int bottom = ToBottom(mulligans);
        if (bottom > 0)
        {
            var chosen = await ControllerOf(playerId).ChooseCardsToBottomAsync(ViewFor(playerId), bottom);
            Require(chosen.Count == bottom && chosen.Distinct().Count() == bottom && chosen.All(player.Hand.Contains),
                $"Must choose exactly {bottom} distinct cards from hand to put on the bottom.");
            foreach (var card in chosen) MoveCard(card, Zone.Library, toBottom: true);
        }
        Emit(new HandKept(playerId, player.Hand.Count));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDecisionException(message);
    }
}
