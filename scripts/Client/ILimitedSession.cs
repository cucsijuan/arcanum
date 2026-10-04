// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Mana;

namespace Arcanum.Client;

/// <summary>The booster in front of the player during a draft, and what they took so far.</summary>
/// <param name="Picked">Already took a card from this booster; waiting for the other players.</param>
public sealed record DraftView(int Round, int Rounds, int PickInRound, bool PassesLeft, IReadOnlyList<PoolCard> Pack, IReadOnlyList<PoolCard> Picks, bool Picked);

/// <summary>
/// The limited event the Limited screen shows: one against the computer on this device (<see cref="LimitedService"/>)
/// or an online event run by a host (<see cref="NetEventSession"/>). <see cref="Current"/> holds only what this player
/// may know: in an online event, the other players' pools and decks are empty.
/// </summary>
public interface ILimitedSession
{
    LimitedEvent? Current { get; }

    /// <summary>The player's seat in <see cref="Current"/>.</summary>
    int Seat { get; }

    DraftView? DraftState { get; }

    bool IsOnline { get; }

    /// <summary>Seconds left for what the player must do now (pick, build, sideboard), or -1 when nothing is timed.</summary>
    int SecondsLeft { get; }

    /// <summary>A note from the host (a deck it refused, what everyone is waiting for), or null.</summary>
    string? Status { get; }

    /// <summary>One of the player's games is being played now (online): it can be returned to.</summary>
    bool GameInProgress { get; }

    /// <summary>The player said they're ready for the next game and waits for their opponent (online).</summary>
    bool WaitingForOpponent { get; }

    /// <summary>The event changed (online updates); the screen redraws.</summary>
    event Action? Changed;

    void Pick(int index);

    /// <summary>The player changed their deck (in place, or by replacing it on their seat).</summary>
    void DeckEdited();

    /// <summary>Deck done: on to the rounds (when everyone is ready, online).</summary>
    void FinishBuilding();

    /// <summary>Plays (or, online, says ready for) the next game of the player's match.</summary>
    void PlayNext();

    /// <summary>Back to the game being played (online).</summary>
    void ReturnToGame();

    /// <summary>Leaves the event (online) or deletes it (this device).</summary>
    void Abandon();

    DeckList AutoBuild(IReadOnlyList<PoolCard> pool);

    DeckEntry BasicLand(ManaType color);

    List<DeckIssue> Validate(EventSeat seat);
}
