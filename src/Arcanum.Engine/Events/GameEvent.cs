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

public sealed record CardMoved(CardId Card, PlayerId Owner, Zone From, Zone To) : GameEvent;
public sealed record CardDrawn(PlayerId Player, CardId Card) : GameEvent;
public sealed record LibraryShuffled(PlayerId Player) : GameEvent;
public sealed record LandPlayed(PlayerId Player, CardId Card) : GameEvent;
public sealed record SpellCast(PlayerId Player, CardId Card) : GameEvent;
public sealed record SpellResolved(CardId Card) : GameEvent;
public sealed record PermanentTapped(CardId Card) : GameEvent;
public sealed record PermanentUntapped(CardId Card) : GameEvent;
public sealed record ManaAdded(PlayerId Player, ManaType Type, CardId? Source) : GameEvent;

public sealed record AttackerDeclared(CardId Attacker, PlayerId Defender) : GameEvent;
public sealed record BlockerDeclared(CardId Blocker, CardId Attacker) : GameEvent;
public sealed record DamageDealt(CardId Source, CardId? TargetCard, PlayerId? TargetPlayer, int Amount) : GameEvent;
public sealed record LifeChanged(PlayerId Player, int OldLife, int NewLife) : GameEvent;
public sealed record CreatureDied(CardId Card) : GameEvent;

public sealed record PlayerLost(PlayerId Player, string Reason) : GameEvent;
public sealed record GameEnded(PlayerId? Winner) : GameEvent;
