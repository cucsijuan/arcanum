// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;

namespace Arcanum.Engine;

/// <summary>Rule parameters for a game. Game-mode plugins (M13) will produce these too.</summary>
public sealed record GameConfig
{
    public ulong Seed { get; init; }
    public int StartingLife { get; init; } = 20;
    public int StartingHandSize { get; init; } = 7;
    public int MaxHandSize { get; init; } = 7;
    public int LandsPerTurn { get; init; } = 1;

    /// <summary>Commander variant rules; null for other games.</summary>
    public CommanderRules? Commander { get; init; }

    /// <summary>Who takes the first turn. Null = chosen at random from the seed.</summary>
    public PlayerId? StartingPlayer { get; init; }

    /// <summary>Multiplayer games give one free mulligan (rule 103.5c). Null = decide by player count.</summary>
    public bool? FreeFirstMulligan { get; init; }

    /// <summary>Starting player skips their first draw (rule 103.8). Null = only in two-player games.</summary>
    public bool? StartingPlayerSkipsDraw { get; init; }

    /// <summary>
    /// Every card name players may choose when an effect says "choose a card name" (the names in the card database). Null:
    /// the names of the cards the choosing player knows of in this game.
    /// </summary>
    public IReadOnlyList<string>? CardNames { get; init; }

    /// <summary>The names among <see cref="CardNames"/> of nonbasic land cards ("choose a nonbasic land card name"). Null: those the chooser knows of.</summary>
    public IReadOnlyList<string>? NonbasicLandNames { get; init; }

    /// <summary>The names among <see cref="CardNames"/> of creature cards ("choose a creature card name"). Null: those the chooser knows of.</summary>
    public IReadOnlyList<string>? CreatureCardNames { get; init; }
}

/// <param name="Commanders">The player's commander(s) in a commander game; they start in the command zone.</param>
public sealed record PlayerSetup(string Name, IPlayerController Controller, IReadOnlyList<CardDefinition> Deck,
    IReadOnlyList<CardDefinition>? Commanders = null);

/// <summary>
/// Rules of the commander variant (rule 903): commanders start in the command zone, can be cast from there for
/// an additional {2} per previous cast, may return there instead of going to a graveyard, exile, hand or library,
/// and 21 combat damage from a single commander makes a player lose.
/// </summary>
public sealed record CommanderRules
{
    public int CommanderDamageToLose { get; init; } = 21;
    public int TaxPerCast { get; init; } = 2;
}
