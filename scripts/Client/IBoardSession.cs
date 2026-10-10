// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;

namespace Arcanum.Client;

/// <summary>
/// What the game board plays: a game run on this device (<see cref="GameSession"/>) or a seat in a game run by a host
/// (<see cref="NetSession"/>). The board only sees views, events filtered for this screen and pending decisions.
/// </summary>
public interface IBoardSession
{
    int PlayerCount { get; }

    /// <summary>The seat shown at the bottom: the person in front of the screen.</summary>
    PlayerId LocalSeat { get; }

    /// <summary>
    /// The game as <paramref name="viewer"/> sees it. A seat in a hosted game only has its own view, whatever the
    /// viewer asked for.
    /// </summary>
    GameView ViewFor(PlayerId viewer);

    /// <summary>The built-in playmat <paramref name="player"/> chose for their side of the table, when this screen knows it.</summary>
    string? PlaymatOf(PlayerId player) => null;

    /// <summary>Show every hidden card (deck-test / hotseat mode).</summary>
    bool RevealAll { get; }

    Decision? CurrentDecision { get; }

    /// <summary>Stops of the people at this screen.</summary>
    AutoPassPolicy Policy { get; }

    /// <summary>Whether this player's actions are announced on screen (they aren't playing on this screen).</summary>
    bool Announces(PlayerId player);

    /// <summary>Some players act without a decision on this screen, so the board follows events to redraw.</summary>
    bool FollowsOthers { get; }

    /// <summary>Rebuilding an earlier position (undo): events aren't animated.</summary>
    bool IsReplaying { get; }

    bool CanUndo { get; }

    /// <summary>Waits until the board has finished presenting recent events (set by the board).</summary>
    Func<Task>? Presentation { get; set; }

    /// <summary>The board should redraw: a decision is pending, the view changed or the game ended.</summary>
    event Action? Changed;

    event Action<Exception>? Failed;

    /// <summary>Something happened, as the person at this screen may see it.</summary>
    event Action<EventView>? EventRaised;

    void Start();

    /// <summary>Called every frame (network sessions handle their messages here).</summary>
    void Poll();

    /// <summary>The screen is closing: leave the game.</summary>
    void Leave();
}
