// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Events;

/// <summary>
/// Something that happened in the game. The UI animates from events, the log prints them and
/// the network layer forwards them (filtered per player for hidden information).
/// </summary>
public abstract record GameEvent;

public sealed record GameStarted(PlayerId StartingPlayer, ulong Seed) : GameEvent;
public sealed record MulliganTaken(PlayerId Player, int Count) : GameEvent;
public sealed record HandKept(PlayerId Player, int HandSize) : GameEvent;
public sealed record TurnBegan(int TurnNumber, PlayerId ActivePlayer) : GameEvent;
public sealed record StepBegan(Step Step, PlayerId ActivePlayer) : GameEvent;
public sealed record PriorityGiven(PlayerId Player) : GameEvent;

/// <param name="LastController">Who controlled the card just before it moved (for "dies" triggers).</param>
public sealed record CardMoved(CardId Card, PlayerId Owner, Zone From, Zone To, PlayerId LastController) : GameEvent;
public sealed record CardDrawn(PlayerId Player, CardId Card) : GameEvent;
public sealed record LibraryShuffled(PlayerId Player) : GameEvent;
public sealed record LandPlayed(PlayerId Player, CardId Card) : GameEvent;
public sealed record SpellCast(PlayerId Player, CardId Card) : GameEvent;
public sealed record SpellResolved(CardId Card) : GameEvent;
public sealed record PermanentTapped(CardId Card) : GameEvent;
public sealed record PermanentUntapped(CardId Card) : GameEvent;
public sealed record ManaAdded(PlayerId Player, ManaType Type, CardId? Source) : GameEvent;

public sealed record AttackerDeclared(CardId Attacker, PlayerId Defender) : GameEvent;
/// <summary>All attackers have been declared; <paramref name="Count"/> creatures attack.</summary>
public sealed record AttacksDeclared(PlayerId Player, int Count) : GameEvent;
public sealed record BlockerDeclared(CardId Blocker, CardId Attacker) : GameEvent;
public sealed record DamageDealt(CardId Source, CardId? TargetCard, PlayerId? TargetPlayer, int Amount, bool IsCombat = false) : GameEvent;
public sealed record AbilityActivated(PlayerId Player, CardId Source, string Text) : GameEvent;
/// <summary>A permanent was tapped to activate a mana ability ("whenever a player taps an artifact for mana").</summary>
public sealed record TappedForMana(PlayerId Player, CardId Source) : GameEvent;
public sealed record AbilityTriggered(PlayerId Controller, CardId Source, string Text) : GameEvent;
public sealed record AbilityResolved(CardId Source, string Text) : GameEvent;
/// <summary>A spell or ability whose targets all became illegal does nothing (rule 608.2b).</summary>
public sealed record FizzledOnResolution(CardId Source) : GameEvent;
/// <summary>A triggered ability with no legal targets is removed from the stack instead of being put on it (rule 603.3d).</summary>
public sealed record TriggerRemovedNoTargets(PlayerId Controller, CardId Source, string Text) : GameEvent;
public sealed record SpellCountered(CardId Card) : GameEvent;
/// <param name="By">The player whose spell, ability or cost put them (null: not known).</param>
/// <summary>Counters were removed from a permanent.</summary>
public sealed record CountersRemoved(CardId Card, Abilities.CounterKind Kind, int Count) : GameEvent;
public sealed record CountersPlaced(CardId Card, Abilities.CounterKind Kind, int Count, PlayerId? By = null) : GameEvent;
/// <summary>Cards shown to every player ("reveal it").</summary>
public sealed record CardsRevealed(PlayerId Player, IReadOnlyList<CardId> Cards) : GameEvent;
public sealed record TokenCreated(CardId Card, PlayerId Controller) : GameEvent;
/// <summary>A face-down permanent was turned face up (rule 702.37e).</summary>
public sealed record TurnedFaceUp(CardId Card, PlayerId Player) : GameEvent;
/// <summary>The tokens one effect created together, after each <see cref="TokenCreated"/>.</summary>
public sealed record TokensCreated(IReadOnlyList<CardId> Cards, PlayerId Controller) : GameEvent;
public sealed record PermanentDestroyed(CardId Card) : GameEvent;
public sealed record CommanderReturned(CardId Card, PlayerId Owner) : GameEvent;
public sealed record LifeChanged(PlayerId Player, int OldLife, int NewLife) : GameEvent;
public sealed record CreatureDied(CardId Card) : GameEvent;
public sealed record AbilityCountered(CardId Source) : GameEvent;
public sealed record SpellCopied(CardId Copy, CardId Original, PlayerId Controller) : GameEvent;
public sealed record PoisonGiven(PlayerId Player, int Count) : GameEvent;
public sealed record EmblemCreated(CardId Emblem, PlayerId Owner) : GameEvent;
public sealed record ChoiceMade(CardId Card, string Choice) : GameEvent;
/// <summary>A creature became prepared (a copy of its prepare spell waits in exile), or was unprepared by an effect.</summary>
public sealed record PreparedChanged(CardId Card, bool Prepared) : GameEvent;
/// <summary>A Class became level <see cref="Level"/> (rule 716.2a).</summary>
public sealed record ClassLevelChanged(CardId Card, int Level) : GameEvent;
/// <summary>A player got an enduring story (storied) for the rest of the game.</summary>
public sealed record EnduringStoryGained(PlayerId Player) : GameEvent;
/// <summary>A player got the city's blessing (ascend).</summary>
public sealed record CitysBlessingGained(PlayerId Player) : GameEvent;
/// <summary>The Ring tempted a player; <paramref name="Bearer"/> is their Ring-bearer now (if any).</summary>
public sealed record RingTempted(PlayerId Player, int Level, CardId? Bearer) : GameEvent;
/// <summary>Permanents phased out or in.</summary>
public sealed record PhasedOut(CardId Card) : GameEvent;
public sealed record PhasedIn(CardId Card) : GameEvent;
/// <summary>A player reveals their hand; <paramref name="Chooser"/>, when set, looks through it to choose cards from it.</summary>
public sealed record HandRevealed(PlayerId Player, IReadOnlyList<CardId> Cards, PlayerId? Chooser = null) : GameEvent;
/// <summary><paramref name="Looker"/> looks at <paramref name="Player"/>'s hand: only they (and its owner) see those cards.</summary>
public sealed record HandLookedAt(PlayerId Looker, PlayerId Player, IReadOnlyList<CardId> Cards) : GameEvent;
/// <summary><paramref name="Chooser"/> chose these cards from <paramref name="Player"/>'s hand (for that player to discard).</summary>
public sealed record ChosenFromHand(PlayerId Chooser, PlayerId Player, IReadOnlyList<CardId> Cards) : GameEvent;
public sealed record PermanentSacrificed(CardId Card) : GameEvent;
/// <summary>A player foraged (rule 701.61a: exiled three cards from their graveyard or sacrificed a Food).</summary>
public sealed record Foraged(PlayerId Player) : GameEvent;
/// <summary>A player blighted: put that many -1/-1 counters on a creature they control.</summary>
public sealed record Blighted(PlayerId Player, CardId Card, int Count) : GameEvent;
/// <summary>A player manifested a card: it entered the battlefield face down as a 2/2 creature.</summary>
public sealed record Manifested(PlayerId Player, CardId Card) : GameEvent;
/// <summary>A player revealed their companion from outside the game before the game began (rule 103.2b).</summary>
public sealed record CompanionRevealed(PlayerId Player, CardId Card) : GameEvent;
/// <summary>A player paid {3} and put their companion into their hand.</summary>
public sealed record CompanionPutIntoHand(PlayerId Player, CardId Card) : GameEvent;
/// <summary>A clash: the cards each player revealed (null: an empty library) and who won (null: nobody).</summary>
public sealed record Clashed(PlayerId Player, PlayerId Opponent, CardId? PlayerCard, CardId? OpponentCard, PlayerId? Winner) : GameEvent;
/// <summary>A player gave a gift they promised (rule 702.174): the giftee drew or got the gift token.</summary>
public sealed record GiftGiven(PlayerId Giver, PlayerId Giftee, CardId Source) : GameEvent;
/// <summary>A player spent mana to cast a spell: their total for the turn went from <see cref="Before"/> to <see cref="After"/> (expend).</summary>
public sealed record ManaSpentOnSpell(PlayerId Player, int Before, int After) : GameEvent;
public sealed record ControlChanged(CardId Card, PlayerId NewController) : GameEvent
{
    /// <summary>Who controlled it before.</summary>
    public PlayerId? Previous { get; init; }
}
public sealed record CardDiscarded(PlayerId Player, CardId Card) : GameEvent;
/// <summary>Scry (<paramref name="Scry"/> true) or surveil: <paramref name="Moved"/> of the <paramref name="Looked"/> cards left the top.</summary>
public sealed record LookedAtTop(PlayerId Player, int Looked, int Moved, bool Scry) : GameEvent;

/// <summary>A double-faced permanent transformed (rule 701.27); <paramref name="BackFaceUp"/>: it is transformed now.</summary>
public sealed record PermanentTransformed(CardId Card, bool BackFaceUp) : GameEvent;

/// <summary>A player became the monarch (rule 724).</summary>
public sealed record MonarchChanged(PlayerId Player) : GameEvent;

public sealed record PlayerLost(PlayerId Player, string Reason) : GameEvent;
public sealed record GameEnded(PlayerId? Winner) : GameEvent;

/// <summary>
/// A tournament round's time ran out during the game (the end-of-match procedure): the turn in progress (turn
/// <see cref="TurnNumber"/>, "turn 0") is finished, then <see cref="AdditionalTurns"/> more turns are played; a game
/// not over by then is a draw.
/// </summary>
public sealed record TimeCalled(int TurnNumber, int AdditionalTurns) : GameEvent;
