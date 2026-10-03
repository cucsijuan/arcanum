// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Players;

namespace Arcanum.Engine;

/// <summary>Rule parameters for a game. Game modes (IGameMode, M7) will produce these.</summary>
public sealed record GameConfig
{
    public ulong Seed { get; init; }
    public int StartingLife { get; init; } = 20;
    public int StartingHandSize { get; init; } = 7;
    public int MaxHandSize { get; init; } = 7;
    public int LandsPerTurn { get; init; } = 1;

    /// <summary>Multiplayer games give one free mulligan (rule 103.5c). Null = decide by player count.</summary>
    public bool? FreeFirstMulligan { get; init; }

    /// <summary>Starting player skips their first draw (rule 103.8). Null = only in two-player games.</summary>
    public bool? StartingPlayerSkipsDraw { get; init; }
}

public sealed record PlayerSetup(string Name, IPlayerController Controller, IReadOnlyList<CardDefinition> Deck);
