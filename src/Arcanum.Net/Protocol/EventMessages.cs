// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;

namespace Arcanum.Net.Protocol;

/// <summary>A limited event (draft or sealed) as one player may see it: their own boosters, pool and deck only.</summary>
public sealed record EventUpdate(EventInfo Event) : NetMessage;

/// <summary>The player takes the card at <paramref name="Index"/> of the booster in front of them at that pick.</summary>
public sealed record DraftPick(int Round, int Pick, int Index) : NetMessage;

/// <summary>Deck done (after building), or ready for the next game of the match (after sideboarding).</summary>
public sealed record EventReady : NetMessage;

/// <param name="Ready">Picked already (draft), deck done (building) or ready for the next game.</param>
public sealed record EventSeatInfo(string Name, bool IsComputer, bool Connected, bool Ready);

/// <param name="B">Null for a bye.</param>
/// <param name="Playing">A game of this match is being played now.</param>
public sealed record MatchInfo(int A, int? B, int WinsA, int WinsB, int Draws, bool Done, bool Playing);

/// <param name="Picked">This player already took a card from this booster; waiting for the others.</param>
public sealed record DraftInfo(int Round, int Rounds, int Pick, bool PassesLeft, IReadOnlyList<PoolCard> Pack, IReadOnlyList<PoolCard> Picks, bool Picked);

/// <param name="You">This player's seat.</param>
/// <param name="Pool">This player's cards (after the draft, or opened in sealed).</param>
/// <param name="Deck">This player's deck list, once they sent one.</param>
/// <param name="DeckProblem">Why that deck can't be played, or null.</param>
/// <param name="SecondsLeft">Time left for what this player must do now (pick, build, sideboard); -1 when nothing is timed.</param>
/// <param name="GameToken">A game of this player's match is running: join it with this token.</param>
public sealed record EventInfo(
    int You, string Title, LimitedMode Mode, string Source, int BestOf, int RoundsTotal, EventStage Stage,
    IReadOnlyList<EventSeatInfo> Seats, IReadOnlyList<IReadOnlyList<MatchInfo>> Rounds,
    DraftInfo? Draft, IReadOnlyList<PoolCard> Pool, string? Deck, string? DeckProblem, bool Ready, int SecondsLeft, string? GameToken);
