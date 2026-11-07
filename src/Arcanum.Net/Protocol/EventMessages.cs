// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;

namespace Arcanum.Net.Protocol;

/// <summary>An event (draft, sealed or constructed) as one player may see it: their own boosters, pool and deck only.</summary>
public sealed record EventUpdate(EventInfo Event) : NetMessage;

/// <summary>The player takes the card at <paramref name="Index"/> of the booster in front of them at that pick.</summary>
public sealed record DraftPick(int Round, int Pick, int Index) : NetMessage;

/// <summary>Deck done (after building), or ready for the next game of the match (after sideboarding).</summary>
public sealed record EventReady : NetMessage;

/// <param name="Ready">Picked already (draft), deck done (building) or ready for the next game.</param>
public sealed record EventSeatInfo(string Name, bool IsComputer, bool Connected, bool Ready);

/// <param name="B">Null for a bye.</param>
/// <param name="Playing">A game of this match is being played now.</param>
/// <param name="Started">When its first game began (UTC): a timed round's clock runs from then.</param>
/// <param name="Waiting">Waiting for a late player since <paramref name="Due"/>; <paramref name="Late"/> got a game loss for it.</param>
/// <param name="BothLost">Neither player showed up: a match loss for each.</param>
public sealed record MatchInfo(int A, int? B, int WinsA, int WinsB, int Draws, bool Done, bool Playing, DateTime? Started = null,
    bool Waiting = false, DateTime? Due = null, IReadOnlyList<int>? Late = null, bool BothLost = false);

/// <param name="Picked">This player already took a card from this booster; waiting for the others.</param>
public sealed record DraftInfo(int Round, int Rounds, int Pick, bool PassesLeft, IReadOnlyList<PoolCard> Pack, IReadOnlyList<PoolCard> Picks, bool Picked);

/// <param name="You">This player's seat.</param>
/// <param name="Pool">This player's cards (after the draft, or opened in sealed).</param>
/// <param name="Deck">This player's deck list, once they sent one.</param>
/// <param name="DeckProblem">Why that deck can't be played, or null.</param>
/// <param name="SecondsLeft">Time left for what this player must do now (pick, build, sideboard); -1 when nothing is timed.</param>
/// <param name="GameToken">A game of this player's match is running: join it with this token.</param>
/// <param name="TopCut">How many of the Swiss standings play the playoff after the Swiss rounds (0: none).</param>
/// <param name="CutAfter">The cut by record after this many Swiss rounds (0: none), at <paramref name="CutPoints"/> match points.</param>
/// <param name="RoundMinutes">The Swiss rounds' time limit (0: untimed).</param>
/// <param name="Dropped">Seats no longer paired (dropped, or out at the cut).</param>
/// <param name="Registered">A constructed event: this player's deck as registered, the cards it may change with between games.</param>
/// <param name="NextDay">The day's rounds are over: the event goes on then (UTC).</param>
public sealed record EventInfo(
    int You, string Title, LimitedMode Mode, string Source, int BestOf, int RoundsTotal, EventStage Stage,
    IReadOnlyList<EventSeatInfo> Seats, IReadOnlyList<IReadOnlyList<MatchInfo>> Rounds,
    DraftInfo? Draft, IReadOnlyList<PoolCard> Pool, string? Deck, string? DeckProblem, bool Ready, int SecondsLeft, string? GameToken,
    int TopCut = 0, int CutAfter = 0, int CutPoints = 0, int RoundMinutes = 0, IReadOnlyList<int>? Dropped = null, string? Registered = null, DateTime? NextDay = null);
