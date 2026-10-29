// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.GameModes;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// The game mode of <see cref="GameConfig.Mode"/>: each player's mode emblem, and the mode's hooks at their points of the
/// game (see <see cref="IGameMode"/>). A mode that fails is dropped with an announcement; its emblems stay.
/// </summary>
public sealed partial class Game
{
    /// <summary>Events a mode may react to in a row before it is stopped as endless.</summary>
    private const int MaxModeEvents = 10_000;

    /// <summary>Times in a row a mode may change the game while state-based actions are checked before it is stopped as endless.</summary>
    private const int MaxModeRepeats = 1_000;

    private IGameMode? _mode;
    private string _modeName = "";
    private ModeHost? _modeHost;
    private readonly Queue<GameEvent> _modeEvents = new();
    private readonly Dictionary<PlayerId, CardId> _modeEmblems = new();

    /// <summary>As the game begins: every player gets the mode's emblem.</summary>
    private void BeginMode()
    {
        if (Config.Mode is not { } make) return;
        IGameMode mode;
        try
        {
            mode = make();
        }
        catch (GameModeException e)
        {
            Emit(new ModeAnnounced("Game mode", $"The game mode couldn't start, and the game goes on without it: {e.Message}"));
            return;
        }
        _mode = mode;
        _modeName = mode.Name;
        _modeHost = new ModeHost(this);
        foreach (var player in State.Players)
        {
            var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
            var emblem = new Card(id, mode.Emblem with { IsEmblem = true, IsToken = true }, player.Id) { Zone = Zone.Command };
            State.Cards.Add(id, emblem);
            State.Emblems.Add(id);
            _modeEmblems[player.Id] = id;
            Emit(new EmblemCreated(id, player.Id));
        }
    }

    /// <summary>Runs one of the mode's hooks; true when anything happened meanwhile.</summary>
    private async Task<bool> RunModeAsync(Func<IGameMode, ModeHost, Task> hook)
    {
        if (_mode is not { } mode || State.IsGameOver) return false;
        int before = _log.Count;
        try
        {
            await hook(mode, _modeHost!);
        }
        catch (GameModeException e)
        {
            StopMode(e.Message);
        }
        return _log.Count > before;
    }

    private void StopMode(string why)
    {
        if (_mode is not { } mode) return;
        _mode = null;
        _modeEvents.Clear();
        Emit(new ModeAnnounced(mode.Name, $"The game mode stopped working, and the game goes on without it: {why}"));
    }

    private Task<bool> ModeStepAsync(Step step) => RunModeAsync((m, h) => m.StepAsync(h, step));

    private Task<bool> ModeCheckAsync() => RunModeAsync((m, h) => m.CheckAsync(h));

    /// <summary>Gives the mode every event so far, those its own reactions cause too; true when it changed anything.</summary>
    private async Task<bool> DeliverModeEventsAsync()
    {
        bool changed = false;
        for (int delivered = 0; _mode is not null && _modeEvents.Count > 0; delivered++)
        {
            if (delivered == MaxModeEvents)
            {
                StopMode($"it reacted to {MaxModeEvents} events in a row");
                return true;
            }
            var e = _modeEvents.Dequeue();
            changed |= await RunModeAsync((m, h) => m.EventAsync(h, e));
        }
        return changed;
    }

    /// <summary>
    /// What a game mode may do to the game. Its actions happen right away, not on the stack; effects use the card script
    /// vocabulary with the player's mode emblem as their source.
    /// </summary>
    public sealed class ModeHost
    {
        private readonly Game _game;

        internal ModeHost(Game game) => _game = game;

        public GameState State => _game.State;
        public GameConfig Config => _game.Config;

        /// <summary>The game's random numbers (the same for a replayed game).</summary>
        public DeterministicRng Rng => _game.Rng;

        /// <summary>The player's mode emblem: the source of what the mode does for that player.</summary>
        public CardId EmblemOf(PlayerId player) => _game._modeEmblems[player];

        /// <summary>
        /// Applies <paramref name="effects"/> (without targets) as if <paramref name="controller"/>'s mode emblem did:
        /// "you" is that player. Nothing happens for a player who has left the game.
        /// </summary>
        public async Task ApplyAsync(PlayerId controller, IReadOnlyList<Effect> effects)
        {
            if (State.IsGameOver || State.GetPlayer(controller).HasLost) return;
            var source = State.GetCard(EmblemOf(controller));
            await _game.ApplyAllAsync(effects, new EffectContext(controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>()));
        }

        /// <summary>The player loses the game (unless an effect says they can't).</summary>
        public void Lose(PlayerId player, string reason)
        {
            if (State.IsGameOver || State.GetPlayer(player).HasLost || _game.CantLose(player)) return;
            _game.Lose(player, reason);
        }

        /// <summary>The player wins the game: all their opponents lose at once (unless an effect says they can't win, or those can't lose).</summary>
        public void Win(PlayerId player)
        {
            if (State.IsGameOver || State.GetPlayer(player).HasLost || _game.CantWin(player)) return;
            var losers = State.OpponentsOf(player).Where(p => !_game.CantLose(p))
                .Select(p => (p, $"{State.GetPlayer(player).Name} won the game")).ToList();
            if (losers.Count > 0) _game.LoseAll(losers);
        }

        /// <summary>The player takes an extra turn after this one (rule 500.7).</summary>
        public void ExtraTurn(PlayerId player)
        {
            if (!State.GetPlayer(player).HasLost) State.ExtraTurns.Add(player);
        }

        /// <summary>Tells every player something (shown on the board and in the game log).</summary>
        public void Announce(string text) => _game.Emit(new ModeAnnounced(_game._modeName, text));

        /// <summary>Asks the player a yes/no question; <paramref name="suggested"/> is the computer's answer.</summary>
        public Task<bool> AskAsync(PlayerId player, string question, bool? suggested = null) =>
            _game.ControllerOf(player).ChooseYesNoAsync(_game.ViewFor(player), new YesNoRequest(question, EmblemOf(player)) { Suggested = suggested });

        /// <summary>The player chooses one of <paramref name="options"/> (its index); <paramref name="suggested"/> is the computer's choice.</summary>
        public async Task<int> ChooseAsync(PlayerId player, string prompt, IReadOnlyList<string> options, int? suggested = null)
        {
            if (options.Count == 0) throw new GameModeException("a choice needs at least one option");
            int chosen = await _game.ControllerOf(player).ChooseOptionAsync(_game.ViewFor(player),
                new OptionRequest(prompt, EmblemOf(player), options, OptionKind.Other) { Suggested = suggested });
            Require(chosen >= 0 && chosen < options.Count, "Choose one of the options.");
            return chosen;
        }
    }
}
