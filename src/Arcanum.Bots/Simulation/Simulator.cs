// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Bots.Simulation;

/// <summary>
/// Everything needed to rebuild a game in progress: its configuration, its seats' decks, any setup applied to it, and
/// each seat's recorded answers. The host that runs the game makes it (wrapping every seat in a <see cref="Recorder"/>).
/// </summary>
public sealed class GameRecording
{
    public required GameConfig Config { get; init; }
    public required IReadOnlyList<PlayerSetup> Seats { get; init; }
    public required IReadOnlyList<Recorder> Recorders { get; init; }

    /// <summary>Applied to the new game before it runs (prepared positions), exactly as it was to the real one.</summary>
    public Action<Game>? Setup { get; init; }

    /// <summary>The cards an opponent could have (see <see cref="CardCandidates"/>); without it the bot imagines more of what it saw.</summary>
    public CardCandidates? Candidates { get; init; }

    /// <summary>
    /// The game being played, when the host has it at hand: at a decision where it can be copied the bot copies it instead
    /// of rebuilding it from the recording (much quicker).
    /// </summary>
    public Func<Game?>? Live { get; set; }

    /// <summary>Wraps every seat's controller in a recorder and returns the setups to build the real game with.</summary>
    public static (GameRecording Recording, PlayerSetup[] Seats) Record(GameConfig config, IReadOnlyList<PlayerSetup> seats, Action<Game>? setup = null,
        CardCandidates? candidates = null)
    {
        var recorders = seats.Select(s => new Recorder(s.Controller)).ToList();
        var recorded = seats.Select((s, i) => s with { Controller = recorders[i] }).ToArray();
        return (new GameRecording { Config = config, Seats = seats, Recorders = recorders, Setup = setup, Candidates = candidates }, recorded);
    }
}

/// <summary>
/// Tries a decision of one seat by playing the game on from it many times: each time the game is rebuilt from the
/// recording, the cards that seat can't see are dealt anew (so it learns nothing hidden), the decision is made, and
/// plain bots play every seat for a few turns. The position reached is then scored for the seat.
/// </summary>
public sealed class Simulator
{
    private readonly GameRecording _recording;
    private readonly PlayerId _me;

    public Simulator(GameRecording recording, PlayerId me)
    {
        _recording = recording;
        _me = me;
    }

    /// <summary>Games played on from each option.</summary>
    public int Rollouts { get; init; } = 4;

    /// <summary>How many turns each game is played on before the position is scored.</summary>
    public int Horizon { get; init; } = 4;

    /// <summary>The longest a single game played on may take.</summary>
    public TimeSpan RolloutTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Limits how many games are played on at once, across every simulator (shared by all bots).</summary>
    public static SemaphoreSlim Workers { get; set; } = new(Math.Max(1, Environment.ProcessorCount - 1));

    /// <summary>
    /// The most time one decision may take (null: no limit). Rollouts run in rounds (one game per option each round);
    /// when the time is up, games still waiting aren't started, those being played are stopped, and only the rounds every
    /// option finished count. A slow device thus plays fewer rounds instead of making the player wait.
    /// </summary>
    public TimeSpan? Budget { get; set; }

    /// <summary>Copy the game at a decision where it can be (see <see cref="GameRecording.Live"/>) instead of rebuilding it.</summary>
    public bool UseCopies { get; set; } = true;

    /// <summary>Rollouts played on a copy, and on a rebuilt game, across all simulators (for measuring).</summary>
    public static long CopiedRollouts, ReplayedRollouts;

    /// <summary>Games copied (templates and rollouts) and the time copying took, in stopwatch ticks, across all simulators (for measuring).</summary>
    public static long Copies, CopyTicks;

    private static Game TimedCopy(Game game, IReadOnlyList<IPlayerController> controllers)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var copy = game.Copy(controllers);
        Interlocked.Add(ref CopyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref Copies);
        return copy;
    }

    /// <summary>Rounds that counted, and decisions where the time ran out before one round, across all simulators (for measuring).</summary>
    public static long RoundsPlayed;
    public static int OutOfTime;

    /// <summary>
    /// The average score (0 = lost, 1 = won) of each option for the seat's pending decision, or null when the time ran out
    /// before one round was finished. <paramref name="answer"/> gives that decision's answer for an option, from the view
    /// and the decision the rebuilt game asks.
    /// </summary>
    public async Task<double[]?> ScoreAsync<T>(IReadOnlyList<T> options, Func<T, IPlayerController, IPlayerController> answer, int seed)
    {
        var answers = _recording.Recorders.Select(r => r.Answers).ToList();
        // At a copy point the game is copied once here (on the game's thread, while it waits for this decision), and each
        // rollout copies that copy; otherwise each rollout rebuilds the game from the recording.
        Game? template = null;
        if (UseCopies && _recording.Live?.Invoke() is { CanCopy: true } live)
            try { template = TimedCopy(live, _recording.Seats.Select(s => s.Controller).ToList()); }
            catch (NotSupportedException) { }
        using var deadline = Budget is { } budget ? new CancellationTokenSource(budget) : new CancellationTokenSource();
        var jobs = new List<Task<double?>>();
        for (int r = 0; r < Rollouts; r++)
            for (int o = 0; o < options.Count; o++)
            {
                var option = options[o];
                // The same deals for every option: only the decision differs between them.
                int deal = seed * 7919 + r;
                jobs.Add(Task.Run(async () =>
                {
                    try { await Workers.WaitAsync(deadline.Token); }
                    catch (OperationCanceledException) { return (double?)null; }
                    try
                    {
                        return template is not null
                            ? await PlayOnCopyAsync(template, rollout => answer(option, rollout), deal, deadline.Token)
                            : await PlayOnAsync(answers, rollout => answer(option, rollout), deal, deadline.Token);
                    }
                    finally { Workers.Release(); }
                }));
            }
        var scores = await Task.WhenAll(jobs);
        var rounds = Enumerable.Range(0, Rollouts).Where(r => Enumerable.Range(0, options.Count).All(o => scores[r * options.Count + o] is not null)).ToList();
        Interlocked.Add(ref RoundsPlayed, rounds.Count);
        if (rounds.Count == 0) Interlocked.Increment(ref OutOfTime);
        if (rounds.Count == 0) return null;
        return Enumerable.Range(0, options.Count).Select(o => rounds.Average(r => scores[r * options.Count + o]!.Value)).ToArray();
    }

    private async Task<double?> PlayOnAsync(IReadOnlyList<IReadOnlyList<object?>> answers, Func<IPlayerController, IPlayerController> first, int deal,
        CancellationToken deadline)
    {
        Game? game = null;
        Func<CardId, CardDefinition?> rules = id => game!.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
        var bots = _recording.Seats.Select((s, i) =>
        {
            var bot = new BotController(new PlayerId(i)) { AlwaysKeep = true };
            bot.UseCardRules(rules);
            // Only the searching seat knows its deck list: the others are played without a plan (their lists are hidden from it).
            if (i == _me.Value) bot.KnowDeck(s.Deck, s.Commanders);
            return bot;
        }).ToList();
        var replayers = _recording.Seats.Select((_, i) => new Replayer(answers[i], i == _me.Value ? first(bots[i]) : bots[i])).ToList();
        int startTurn = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        cts.CancelAfter(RolloutTimeout);
        replayers[_me.Value].ReachedEnd = _ =>
        {
            Deal(game!, new Random(deal));
            game.Rng.Reseed((ulong)deal * 2654435761UL + 1);
            startTurn = game.State.TurnNumber;
            return Task.CompletedTask;
        };
        game = new Game(_recording.Config, _recording.Seats.Select((s, i) => s with { Controller = replayers[i] }).ToList());
        _recording.Setup?.Invoke(game);
        Interlocked.Increment(ref ReplayedRollouts);
        game.EventRaised += e =>
        {
            if (e is TurnBegan t && startTurn > 0 && t.TurnNumber > startTurn + Horizon) cts.Cancel();
        };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { return 0.5; } // a rebuilt game that fails tells nothing either way
        if (deadline.IsCancellationRequested && !game.State.IsGameOver) return null; // stopped by the time limit: doesn't count
        return Score(game.State);
    }

    /// <summary>A rollout on a copy of the game at the decision: the hidden cards dealt anew, then plain bots play on.</summary>
    private async Task<double?> PlayOnCopyAsync(Game template, Func<IPlayerController, IPlayerController> first, int deal, CancellationToken deadline)
    {
        Game? game = null;
        Func<CardId, CardDefinition?> rules = id => game!.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
        var controllers = _recording.Seats.Select((s, i) =>
        {
            var bot = new BotController(new PlayerId(i)) { AlwaysKeep = true };
            bot.UseCardRules(rules);
            // Only the searching seat knows its deck list: the others are played without a plan (their lists are hidden from it).
            if (i == _me.Value) bot.KnowDeck(s.Deck, s.Commanders);
            return i == _me.Value ? first(bot) : bot;
        }).ToList();
        game = TimedCopy(template, controllers);
        Deal(game, new Random(deal));
        game.Rng.Reseed((ulong)deal * 2654435761UL + 1);
        int startTurn = game.State.TurnNumber;
        Interlocked.Increment(ref CopiedRollouts);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        cts.CancelAfter(RolloutTimeout);
        game.EventRaised += e =>
        {
            if (e is TurnBegan t && t.TurnNumber > startTurn + Horizon) cts.Cancel();
        };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { return 0.5; } // a game that fails tells nothing either way
        if (deadline.IsCancellationRequested && !game.State.IsGameOver) return null; // stopped by the time limit: doesn't count
        return Score(game.State);
    }

    /// <summary>
    /// Deals the cards the seat can't see anew, without looking at them: its own library in a new order, and each
    /// opponent's hidden hand and library replaced by cards imagined from what the seat has seen (<see cref="OpponentModel"/>).
    /// </summary>
    private void Deal(Game game, Random random)
    {
        var state = game.State;
        bool Hidden(CardId id) => !state.GetCard(id).IsVisibleTo(_me);
        foreach (var player in state.Players)
        {
            if (player.Id == _me)
            {
                var slots = Enumerable.Range(0, player.Library.Count).Where(i => Hidden(player.Library[i])).ToList();
                var order = slots.Select(i => player.Library[i]).OrderBy(_ => random.Next()).ToList();
                for (int k = 0; k < slots.Count; k++) player.Library[slots[k]] = order[k];
                continue;
            }
            var owned = state.Cards.Values.Where(c => c.Owner == player.Id && !c.PrintedDefinition.IsToken).ToList();
            var hiddenHand = player.Hand.Where(Hidden).ToList();
            var hiddenLibrary = player.Library.Where(Hidden).ToList();
            var seen = owned.Where(c => c.IsVisibleTo(_me)).Select(c => c.PrintedDefinition).ToList();
            var known = new OpponentKnowledge(seen, owned.Where(c => c.IsCommander).Select(c => c.PrintedDefinition).ToList(),
                owned.Count, hiddenHand.Count, hiddenLibrary.Count);
            var (hand, library) = OpponentModel.ImagineFrom(known, WeighingFor(player.Id, known), random);
            for (int k = 0; k < hiddenHand.Count && k < hand.Count; k++) game.ReplaceHiddenCard(hiddenHand[k], hand[k]);
            for (int k = 0; k < hiddenLibrary.Count && k < library.Count; k++) game.ReplaceHiddenCard(hiddenLibrary[k], library[k]);
        }
    }

    private readonly Dictionary<(PlayerId, int), OpponentModel.Weighing> _weighings = new();

    /// <summary>The host's candidates for an opponent, weighed once for each set of seen cards.</summary>
    private OpponentModel.Weighing WeighingFor(PlayerId player, OpponentKnowledge known)
    {
        int key = known.Seen.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).Aggregate(17, (h, n) => h * 31 + n.GetHashCode());
        lock (_weighings)
        {
            if (!_weighings.TryGetValue((player, key), out var weighing))
                _weighings[(player, key)] = weighing = OpponentModel.Weigh(known, _recording.Candidates?.Invoke(known.Seen));
            return weighing;
        }
    }

    /// <summary>
    /// The seat's score for a position: 1 won, 0 lost; otherwise from its life, board and cards against the strongest
    /// opponent still in the game, squeezed into (0, 1).
    /// </summary>
    private double Score(GameState state)
    {
        if (state.Winner is { } winner) return winner == _me ? 1 : 0;
        var me = state.GetPlayer(_me);
        if (me.HasLost) return 0;
        var opponents = state.LivingPlayers.Where(p => p.Id != _me).ToList();
        if (opponents.Count == 0) return 1;
        double Strength(Player p)
        {
            double board = state.Battlefield.Select(state.GetCard).Where(c => c.Controller == p.Id).Sum(c =>
                c.Definition.IsCreature() ? 1 + 0.6 * Math.Max(0, c.Definition.Power ?? 0) + 0.4 * Math.Max(0, c.Definition.Toughness ?? 0)
                : c.Definition.Is(CardType.Land) ? 0.6 : 1.2);
            return 0.12 * Math.Min(p.Life, 40) + 0.35 * board + 0.3 * p.Hand.Count + (p.Life <= 0 ? -10 : 0);
        }
        double diff = Strength(me) - opponents.Max(Strength);
        return 1 / (1 + Math.Exp(-diff / 3));
    }
}

/// <summary>Answers one kind of decision once with a fixed answer, then leaves everything to another controller.</summary>
public sealed class FirstAnswer : IPlayerController
{
    private readonly IPlayerController _then;
    private PlayerAction? _action;
    private IReadOnlyList<AttackDeclaration>? _attacks;
    private IReadOnlyList<BlockDeclaration>? _blocks;

    private FirstAnswer(IPlayerController then) => _then = then;

    public static FirstAnswer Action(PlayerAction action, IPlayerController then) => new(then) { _action = action };
    public static FirstAnswer Attacks(IReadOnlyList<AttackDeclaration> attacks, IPlayerController then) => new(then) { _attacks = attacks };
    public static FirstAnswer Blocks(IReadOnlyList<BlockDeclaration> blocks, IPlayerController then) => new(then) { _blocks = blocks };

    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions)
    {
        if (_action is { } action)
        {
            _action = null;
            if (legalActions.Contains(action)) return Task.FromResult(action);
        }
        return _then.ChooseActionAsync(view, legalActions);
    }

    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders)
    {
        if (_attacks is { } attacks)
        {
            _attacks = null;
            return Task.FromResult(attacks);
        }
        return _then.DeclareAttackersAsync(view, possibleAttackers, defenders);
    }

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) => _then.KeepHandAsync(view, mulligansTaken);
    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) => _then.ChooseCardsToBottomAsync(view, count);
    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) => _then.ChooseManaPaymentAsync(view, request);
    public Task<IReadOnlyList<Arcanum.Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) => _then.ChooseTargetsAsync(view, request);
    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request)
    {
        if (_blocks is { } blocks)
        {
            _blocks = null;
            if (request.IsLegal(blocks, out _)) return Task.FromResult(blocks);
        }
        return _then.DeclareBlockersAsync(view, request);
    }

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) => _then.AssignCombatDamageAsync(view, request);
    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) => _then.ChooseYesNoAsync(view, request);
    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) => _then.ChooseDiscardAsync(view, count);
    public Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request) => _then.ChooseCardsAsync(view, request);
    public Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request) => _then.ChooseModesAsync(view, request);
    public Task<int> ChooseNumberAsync(GameView view, NumberRequest request) => _then.ChooseNumberAsync(view, request);
    public Task<int> ChooseOptionAsync(GameView view, OptionRequest request) => _then.ChooseOptionAsync(view, request);
}
