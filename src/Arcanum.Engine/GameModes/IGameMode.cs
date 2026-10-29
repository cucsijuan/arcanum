// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine.GameModes;

/// <summary>
/// A game mode: rules a plugin adds to a game. As the game begins each player gets an emblem made from
/// <see cref="Emblem"/> (rule 114: it can have triggered, static and activated abilities, and is the source of what the
/// mode does for that player). The hooks run on the game's host, at fixed points of the game, and change it through the
/// <see cref="Game.ModeHost"/> they are given; what they do is not on the stack, like a turn-based action. A mode object
/// belongs to one game (it may keep state) and must be deterministic: same game, same decisions, same result.
/// </summary>
public interface IGameMode
{
    string Name { get; }

    /// <summary>Each player's mode emblem (its name, rules text and abilities); the engine makes it an emblem.</summary>
    CardDefinition Emblem { get; }

    /// <summary>After every player has kept an opening hand, before the first turn.</summary>
    Task StartAsync(Game.ModeHost game);

    /// <summary>
    /// A step has begun and its turn-based actions are done, before anyone gets priority (the cleanup step: before its
    /// turn-based actions).
    /// </summary>
    Task StepAsync(Game.ModeHost game, Step step);

    /// <summary>Something happened. Events are given in order, the next time state-based actions are checked.</summary>
    Task EventAsync(Game.ModeHost game, GameEvent e);

    /// <summary>The mode's own checks, each time right after state-based actions (its own ways to win or lose the game).</summary>
    Task CheckAsync(Game.ModeHost game);
}

/// <summary>A game mode failed (an error in its script, or it ran too long); the game goes on without the mode.</summary>
public sealed class GameModeException(string message, Exception? inner = null) : Exception(message, inner);
