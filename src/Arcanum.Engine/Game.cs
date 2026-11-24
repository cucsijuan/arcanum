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
    private IReadOnlyList<IPlayerController> _controllers;
    private readonly List<GameEvent> _log = new();
    private CancellationToken _ct;

    public GameConfig Config { get; }
    public GameState State { get; }
    public DeterministicRng Rng { get; }
    public IReadOnlyList<GameEvent> Log => _log;

    /// <summary>Raised synchronously for every event, in order.</summary>
    public event Action<GameEvent>? EventRaised;

    /// <summary>
    /// Players stopped knowing some library cards without an event saying so (cards put back in an order they didn't
    /// see); a shuffle or a card moving is announced by its own event.
    /// </summary>
    public event Action? KnowledgeLost;

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
            // Commanders begin the game in the command zone (rule 903.6).
            foreach (var def in players[i].Commanders ?? Array.Empty<CardDefinition>())
            {
                var card = new Card(new CardId(nextId++), def, statePlayers[i].Id) { Zone = Zone.Command, IsCommander = true };
                State.Cards.Add(card.Id, card);
                statePlayers[i].Command.Add(card.Id);
            }
            // The sideboard: cards the player owns outside the game (rule 400.11b).
            foreach (var def in players[i].Sideboard ?? Array.Empty<CardDefinition>())
            {
                var card = new Card(new CardId(nextId++), def, statePlayers[i].Id) { Zone = Zone.Outside };
                State.Cards.Add(card.Id, card);
                statePlayers[i].Outside.Add(card.Id);
            }
        }
    }

    private bool IsMultiplayer => State.Players.Count > 2;

    /// <summary>
    /// Puts a permanent onto the battlefield before the game starts (deck-test sandbox, rules scenarios).
    /// </summary>
    public CardId SetupPermanent(PlayerId owner, CardDefinition definition, CardId? attachTo = null)
    {
        var id = Setup(owner, definition, Zone.Battlefield);
        State.GetCard(id).AttachedTo = attachTo;
        State.GetCard(id).ClassLevel = 1; // as on any entering (rule 716.3)
        return id;
    }

    /// <summary>Puts a card into a player's hand before the game starts, on top of the normal opening hand.</summary>
    public CardId SetupInHand(PlayerId owner, CardDefinition definition) => Setup(owner, definition, Zone.Hand);

    /// <summary>Puts a card at the bottom of a player's library before the game starts (rules scenarios).</summary>
    public CardId SetupInLibrary(PlayerId owner, CardDefinition definition) => Setup(owner, definition, Zone.Library);

    /// <summary>
    /// Puts a card into exile before the game starts, held there until <paramref name="source"/> leaves the battlefield
    /// (deck-test sandbox).
    /// </summary>
    public CardId SetupExiledUntilLeaves(PlayerId owner, CardDefinition definition, CardId source)
    {
        var id = Setup(owner, definition, Zone.Exile);
        State.LinkedExiles.Add(new LinkedExile(source, State.GetCard(source).Version, id, State.GetCard(id).Version));
        return id;
    }

    private CardId Setup(PlayerId owner, CardDefinition definition, Zone zone)
    {
        if (State.TurnNumber > 0) throw new InvalidOperationException("Cards can only be set up before the game starts.");
        var nextId = State.Cards.Count == 0 ? 1 : State.Cards.Keys.Max(k => k.Value) + 1;
        var card = new Card(new CardId(nextId), definition, owner) { Zone = zone };
        State.Cards.Add(card.Id, card);
        if (zone == Zone.Battlefield) State.Battlefield.Add(card.Id);
        else if (zone == Zone.Library) State.GetPlayer(owner).Library.Add(card.Id);
        else if (zone == Zone.Exile) State.GetPlayer(owner).Exile.Add(card.Id);
        else State.GetPlayer(owner).Hand.Add(card.Id);
        return card.Id;
    }

    /// <summary>
    /// For a bot's rebuilt game only: the card with this id, in a hand or a library, becomes a new card with another
    /// definition (what the bot imagines a hidden card to be). Its place in the zone stays the same.
    /// </summary>
    public void ReplaceHiddenCard(CardId id, CardDefinition definition)
    {
        var old = State.GetCard(id);
        if (old.Zone is not (Zone.Hand or Zone.Library)) throw new InvalidOperationException("Only cards in a hand or a library can be replaced.");
        State.Cards[id] = new Card(id, definition, old.Owner) { Zone = old.Zone };
    }

    /// <summary>
    /// What a player sees. A seat whose controller is replaying known answers (<see cref="IReplayingController"/>) is
    /// given the first view built instead (it doesn't read it), which keeps rebuilding a recorded game quick.
    /// </summary>
    public GameView ViewFor(PlayerId player, bool revealAll = false)
    {
        if (!revealAll && _controllers.Count > 0 && ControllerOf(player) is IReplayingController { Replaying: true })
            return _replayView ??= BuildView(player, revealAll);
        return BuildView(player, revealAll);
    }

    private GameView? _replayView;

    private GameView BuildView(PlayerId player, bool revealAll) =>
        ViewBuilder.Build(State, player, revealAll, Config.Commander?.TaxPerCast ?? 0) with
        {
            AttackTaxes = AttackTaxesFor(player),
            AttackerTaxes = AttackerTaxesFor(player),
            AttackTaxBudget = State.ActivePlayer == player && AttackerTaxesFor(player).Count > 0 ? GenericBudget(player) : 0,
            AttackRestrictions = State.PermanentsControlledBy(player).Where(c => c.IsCreature)
                .SelectMany(c => State.OpponentsOf(player).Where(d => AttackForbidden(c, d) || AttackForbiddenWithPlaneswalkers(c, d)).Select(d => new AttackRestrictionView(c.Id, d))).ToList(),
            Monarch = State.Monarch,
            AttackRequest = player == State.ActivePlayer ? _attackRequest : null,
            TimeCalledAt = TimeCalledAt,
            ActivePlayerControlledBy = State.ActivePlayerControlledBy is { } by && State.PlayerControlledBy(by) is not null ? by : null,
            ControlledView = State.PlayerControlledBy(player) is { } controlled ? ViewFor(controlled, revealAll) : null,
        };

    /// <summary>Who makes <paramref name="player"/>'s decisions: themselves, or the player controlling them this turn (rule 722.4).</summary>
    private IPlayerController ControllerOf(PlayerId player) => _controllers[DeciderFor(player).Value];

    /// <summary>The player making <paramref name="player"/>'s choices and decisions now (rule 722.4).</summary>
    public PlayerId DeciderFor(PlayerId player) =>
        player == State.ActivePlayer && State.ActivePlayerControlledBy is { } by && State.PlayerControlledBy(by) == player ? by : player;

    /// <summary>A new timestamp, later than every earlier one (rule 613.7).</summary>
    private long NewTimestamp() => ++State.LastTimestamp;

    private void Emit(GameEvent e)
    {
        _log.Add(e);
        TrackTurnHistory(e);
        // Continuous effects are always current (rule 611.3a): conditions like "as long as it's untapped" or
        // "as long as you have 30 or more life" change with these events.
        if (e is PermanentTapped or PermanentUntapped or CountersPlaced or LifeChanged or ControlChanged or AttacksDeclared or AttackerDeclared or BlockerDeclared or SpellCast)
            RecomputeContinuousEffects();
        CollectTriggers(e);
        NoteKnowledge(e);
        if (_mode is not null) _modeEvents.Enqueue(e);
        EventRaised?.Invoke(e);
    }

    /// <summary>Remembers what happened this turn for conditions like raid and morbid.</summary>
    private void TrackTurnHistory(GameEvent e)
    {
        // Every damage event, whatever it was dealt to (the switch below takes only the first matching case).
        if (e is CardMoved { To: Zone.Exile } exiled && !State.GetCard(exiled.Card).Definition.IsToken) State.CardsPutIntoExileThisTurn++;
        if (e is CardMoved { To: Zone.Battlefield } arrived) State.GetCard(arrived.Card).ControlStamp = NewTimestamp();
        if (e is ControlChanged newControl) State.GetCard(newControl.Card).ControlStamp = NewTimestamp();
        if (e is StepBegan { Step: Step.Upkeep } upkeep)
        {
            var upkeeper = State.GetPlayer(upkeep.ActivePlayer);
            upkeeper.PreviousUpkeepStamp = upkeeper.UpkeepStamp;
            upkeeper.UpkeepStamp = NewTimestamp();
        }
        if (e is CardMoved { From: Zone.Battlefield, To: Zone.Graveyard }) State.PermanentsPutIntoGraveyardThisTurn++;
        if (e is DamageDealt damage)
        {
            LogDamage(damage);
            if (damage.IsCombat && damage.TargetPlayer is { } player) State.GetCard(damage.Source).CombatDamagedPlayers.Add(player);
        }
        switch (e)
        {
            case CardMoved { From: Zone.Battlefield, To: Zone.Graveyard } m when WasCreature(State.GetCard(m.Card)):
                State.CreaturesDiedThisTurn++;
                State.CreatureDeathsThisTurn.Add(m.Card);
                if (!State.GetCard(m.Card).Definition.IsToken) State.NontokenCreaturesDiedThisTurn++;
                State.GetPlayer(m.LastController).CreaturesDiedThisTurn++;
                State.GetPlayer(m.LastController).PermanentLeftThisTurn = true;
                break;
            case CardMoved { From: Zone.Hand, To: Zone.Graveyard } discardedOrMilled:
                State.GetPlayer(discardedOrMilled.Owner).CardsToGraveyardFromHandOrLibraryThisTurn++;
                break;
            case DamageDealt { TargetPlayer: { } hurtPlayer } dealt:
                State.GetPlayer(hurtPlayer).DamageTakenThisTurn += dealt.Amount;
                if (dealt.IsCombat)
                {
                    State.GetPlayer(hurtPlayer).CombatDamageTakenThisTurn += dealt.Amount;
                    if (State.GetCard(dealt.Source) is { IsCreature: true } hitter && (hitter.Supertypes & Cards.Supertype.Legendary) != 0)
                        State.GetPlayer(hurtPlayer).CombatDamagedByLegendaryThisTurn = true;
                    var damager = State.GetCard(dealt.Source);
                    State.GetPlayer(hurtPlayer).CombatDamageSourcesThisTurn.Add((damager.Id, damager.Version));
                    if (damager.HasSubtype("Dragon")) State.GetPlayer(hurtPlayer).CombatDamagedByDragonThisTurn = true;
                }
                if (dealt.IsCombat && State.GetCard(dealt.Source).IsCreature) State.GetPlayer(hurtPlayer).CombatDamagedByNames.Add(State.GetCard(dealt.Source).Name);
                break;
            case CardMoved { To: Zone.Battlefield } entered:
                State.GetPlayer(State.GetCard(entered.Card).Controller).EnteredThisTurn.Add((entered.Card, State.GetCard(entered.Card).Version));
                break;
            case CardMoved { From: Zone.Battlefield } left:
                State.GetPlayer(left.LastController).PermanentLeftThisTurn = true;
                if (State.GetCard(left.Card).LastKnownInfo is { } leftLki && (leftLki.Types & CardType.Land) == 0) State.NonlandPermanentLeftThisTurn = true;
                if (left.To == Zone.Exile && State.GetCard(left.Card).LastKnownInfo is { } exiledLki && (exiledLki.Types & CardType.Creature) != 0)
                    State.GetPlayer(left.LastController).CreaturesExiledThisTurn++;
                break;
            case CardMoved { From: Zone.Library, To: Zone.Graveyard } milled:
                State.GetPlayer(milled.Owner).MilledThisTurn++;
                State.GetPlayer(milled.Owner).CardsToGraveyardFromHandOrLibraryThisTurn++;
                break;
            case CardMoved { From: Zone.Graveyard } fromGraveyard:
                State.GetPlayer(fromGraveyard.Owner).CardLeftGraveyardThisTurn = true;
                State.GetPlayer(fromGraveyard.Owner).CardsLeftGraveyardThisTurn++;
                break;
            case CountersPlaced { Count: > 0 } placed:
                var counted = State.GetCard(placed.Card);
                if (counted.CounterPutTurn != State.TurnNumber) { counted.CounterEventsThisTurn = 0; counted.CounterPuttersThisTurn.Clear(); }
                if (placed.By is { } putter) counted.CounterPuttersThisTurn.Add(putter);
                // The kinds put in one placement are one event ("one or more counters"): counted once.
                if (_lastCounterPlacement != (placed.Card, State.TurnNumber, _counterPlacements)) counted.CounterEventsThisTurn++;
                _lastCounterPlacement = (placed.Card, State.TurnNumber, _counterPlacements);
                counted.CounterPutTurn = State.TurnNumber;
                if (placed.By is { } placer && counted.IsCreature) State.GetPlayer(placer).PutCounterOnCreatureThisTurn = true;
                break;
            case PermanentSacrificed ps:
                State.PermanentsSacrificedThisTurn++;
                State.GetPlayer(State.GetCard(ps.Card).LastKnownInfo?.Controller ?? State.GetCard(ps.Card).Owner).SacrificedThisTurn.Add(ps.Card);
                break;
            case LifeChanged l when l.NewLife > l.OldLife:
                State.GetPlayer(l.Player).LifeGainedThisTurn += l.NewLife - l.OldLife;
                State.GetPlayer(l.Player).LifeGainsThisTurn++;
                break;
            case CardDrawn d:
                State.GetPlayer(d.Player).CardsDrawnThisTurn++;
                State.GetPlayer(d.Player).DrawnThisTurn.Add(d.Card);
                break;
            case LifeChanged l when l.NewLife < l.OldLife:
                State.GetPlayer(l.Player).LifeLostThisTurn += l.OldLife - l.NewLife;
                break;
            case AttacksDeclared a:
                State.GetPlayer(a.Player).AttackedThisTurn = true;
                break;
            case AttackerDeclared ad:
                State.GetCard(ad.Attacker).AttacksThisTurn++;
                State.AttackersThisTurn.Add((ad.Attacker, State.GetCard(ad.Attacker).Version, State.TurnNumber));
                if (State.Combat?.FindAttack(ad.Attacker) is { Planeswalker: null } attacked)
                    State.GetPlayer(State.GetCard(ad.Attacker).Controller).PlayersAttackedThisTurn.Add(attacked.Defender);
                break;
        }
    }

    /// <summary>Plays the game to completion.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _ct = ct;
        if (_resume == ResumePoint.None) await StartAsync();
        // Makes the next turn's player active: the most recent extra turn, else the next player in turn order. True for an extra turn.
        bool NextTurn()
        {
            State.ExtraTurns.RemoveAll(p => State.GetPlayer(p).HasLost);
            if (State.ExtraTurns.Count > 0)
            {
                State.ActivePlayer = State.ExtraTurns[^1];
                State.ExtraTurns.RemoveAt(State.ExtraTurns.Count - 1);
                return true;
            }
            _regularPlayer = State.NextLivingPlayer(_regularPlayer);
            State.ActivePlayer = _regularPlayer;
            return false;
        }
        while (!State.IsGameOver)
        {
            // A copied game resumes inside the turn it was copied in (see Copy).
            if (_resume == ResumePoint.None)
            {
                if (!_firstTurn)
                {
                    // Extra turns come right after the current one, the most recently created first (rule 500.7).
                    bool extra = NextTurn();
                    // "Skips their next turn" / "if an opponent would begin an extra turn, that player skips that turn instead": that turn
                    // doesn't happen, and the next one is picked the same way (rule 614.10). With both, the player chooses which applies (616.1).
                    while (!State.IsGameOver && await SkipsTurnAsync(State.ActivePlayer, extra)) extra = NextTurn();
                }
                _skipDraw = _firstTurn && (Config.StartingPlayerSkipsDraw ?? !IsMultiplayer);
            }
            await RunTurnAsync();
            _firstTurn = false;
            // The end-of-match procedure: after the additional turns, a game nobody has won is a draw.
            if (!State.IsGameOver && TimeCalledAt is { } called && State.TurnNumber >= called + AdditionalTurns)
            {
                State.IsGameOver = true;
                State.Winner = null;
                Emit(new GameEnded(null));
            }
        }
    }

    /// <summary>The game's start: starting player, mulligans, cards that begin the game on the battlefield, the game mode's start.</summary>
    private async Task StartAsync()
    {
        var startingPlayer = Config.StartingPlayer ?? new PlayerId(Rng.Next(State.Players.Count));
        Emit(new GameStarted(startingPlayer, Config.Seed));
        State.ActivePlayer = startingPlayer;
        BeginMode();
        await RevealCompanionsAsync();

        foreach (var player in State.Players) Shuffle(player);
        RecomputeContinuousEffects(); // permanents set up before the game may have static abilities
        State.ActivePlayer = startingPlayer;
        await ResolveMulliganAsync(State.ApnapOrder().ToList());
        // "If this card is in your opening hand, you may begin the game with it on the battlefield."
        var starting = State.ActivePlayer;
        foreach (var playerId in State.ApnapOrder().ToList())
            foreach (var id in State.GetPlayer(playerId).Hand.Where(c => State.GetCard(c).Definition.StartsOnBattlefieldFromOpeningHand).ToList())
            {
                var card = State.GetCard(id);
                if (card.Zone != Zone.Hand || (card.Definition.StartsOnlyIfNotStartingPlayer && playerId == starting)) continue;
                if (!await ControllerOf(playerId).ChooseYesNoAsync(ViewFor(playerId), new YesNoRequest($"Begin the game with {card.Name} on the battlefield?", id))) continue;
                await MoveCardAsync(id, Zone.Battlefield);
                if (card.Definition.StartsWithCounter is { } kind) PutCounters(card, kind, 1, playerId);
                // "If you do, exile a card from your hand."
                var hand = State.GetPlayer(playerId).Hand.ToList();
                int exile = Math.Min(card.Definition.StartsExilingFromHand, hand.Count);
                if (exile > 0)
                {
                    var chosen = await ControllerOf(playerId).ChooseCardsAsync(ViewFor(playerId), new CardChoiceRequest($"{card.Name}: exile {exile} card(s) from your hand", id,
                        hand.Select(h => ViewBuilder.Card(State, h, playerId)).ToList(), exile, exile, CardChoicePurpose.Discard));
                    Require(chosen.Count == exile && chosen.Distinct().Count() == exile && chosen.All(hand.Contains), "Exile cards from your hand.");
                    foreach (var h in chosen) await MoveCardAsync(h, Zone.Exile);
                }
            }
        await DeliverModeEventsAsync();
        await RunModeAsync((m, h) => m.StartAsync(h));
        await DeliverModeEventsAsync();

        _firstTurn = true;
        _regularPlayer = State.ActivePlayer; // whose turn it is in the normal turn order
    }

    /// <summary>The additional turns of the end-of-match procedure after the turn in progress when time is called (two-player games).</summary>
    public const int AdditionalTurns = 5;

    /// <summary>The turn in progress when the round's time ran out (see <see cref="CallTime"/>), or null.</summary>
    public int? TimeCalledAt { get; private set; }

    /// <summary>
    /// The tournament round's time is up: the turn in progress is turn 0, then <see cref="AdditionalTurns"/> more turns
    /// (extra turns count among them); a game not over after them is a draw. Called once; <paramref name="turn"/> replays
    /// a call made at that turn (an undone game played again).
    /// </summary>
    public void CallTime(int? turn = null)
    {
        if (TimeCalledAt is not null || State.IsGameOver) return;
        TimeCalledAt = turn ?? Math.Max(1, State.TurnNumber);
        Emit(new TimeCalled(TimeCalledAt.Value, AdditionalTurns));
    }

    /// <summary>
    /// Rule 103.5: everyone draws a hand; in turn order each player who may still mulligan declares keep or mulligan, then everyone
    /// who chose to mulligan shuffles their hand into their library and draws a new hand at the same time; this repeats until
    /// everyone keeps, and then each player who mulliganed puts cards on the bottom (a free first mulligan in multiplayer, rule
    /// 103.5c, puts one fewer). A player may mulligan down to no cards.
    /// </summary>
    private async Task ResolveMulliganAsync(IReadOnlyList<PlayerId> order)
    {
        bool freeFirst = Config.FreeFirstMulligan ?? IsMultiplayer;
        int handSize = Config.StartingHandSize;
        var mulligans = order.ToDictionary(p => p, _ => 0);
        foreach (var playerId in order) await DrawAsync(playerId, handSize);

        int ToBottom(int m) => Math.Max(0, m - (freeFirst ? 1 : 0));

        var deciding = order.ToList();
        while (deciding.Count > 0)
        {
            var taking = new List<PlayerId>();
            foreach (var playerId in deciding)
            {
                // Another mulligan is pointless once keeping would already leave no cards.
                if (ToBottom(mulligans[playerId]) >= handSize) continue;
                if (!await ControllerOf(playerId).KeepHandAsync(ViewFor(playerId), mulligans[playerId])) taking.Add(playerId);
            }
            foreach (var playerId in taking)
            {
                var player = State.GetPlayer(playerId);
                mulligans[playerId]++;
                Emit(new MulliganTaken(playerId, mulligans[playerId]));
                foreach (var card in player.Hand.ToList()) await MoveCardAsync(card, Zone.Library);
                Shuffle(player);
            }
            foreach (var playerId in taking) await DrawAsync(playerId, handSize);
            deciding = taking;
        }

        foreach (var playerId in order)
        {
            var player = State.GetPlayer(playerId);
            int bottom = Math.Min(ToBottom(mulligans[playerId]), player.Hand.Count);
            if (bottom > 0)
            {
                var chosen = await ControllerOf(playerId).ChooseCardsToBottomAsync(ViewFor(playerId), bottom);
                Require(chosen.Count == bottom && chosen.Distinct().Count() == bottom && chosen.All(player.Hand.Contains),
                    $"Must choose exactly {bottom} distinct cards from hand to put on the bottom.");
                foreach (var card in chosen) await MoveCardAsync(card, Zone.Library, toBottom: true);
            }
            Emit(new HandKept(playerId, player.Hand.Count));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDecisionException(message);
    }
}
