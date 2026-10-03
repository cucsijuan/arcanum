// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>A game-state check an ability or effect depends on ("if you attacked this turn", ...).</summary>
public abstract record Condition;

/// <summary>"If you attacked this turn" (raid).</summary>
public sealed record AttackedThisTurn : Condition;

/// <summary>"If a creature died this turn" (morbid).</summary>
public sealed record CreatureDiedThisTurn : Condition;

/// <summary>"If you gained life this turn" / "if you gained N or more life this turn".</summary>
public sealed record GainedLifeThisTurn(int AtLeast = 1) : Condition;

/// <summary>"If there are N or more cards in your graveyard" (threshold: seven).</summary>
public sealed record CardsInGraveyard(int AtLeast, ObjectFilter? Filter = null) : Condition;

/// <summary>"If you control [N or more] [objects]" — e.g. ferocious is a creature with power 4 or greater.</summary>
public sealed record YouControl(ObjectFilter Filter, int AtLeast = 1) : Condition;

/// <summary>"If you have N or more life".</summary>
public sealed record LifeAtLeast(int Amount) : Condition;

/// <summary>"If [condition] isn't true" / "unless".</summary>
public sealed record Not(Condition Inner) : Condition;

/// <summary>"If it was kicked" / "if this spell was kicked".</summary>
public sealed record WasKicked : Condition;

/// <summary>"If an opponent lost life this turn".</summary>
public sealed record OpponentLostLifeThisTurn : Condition;

/// <summary>"If it's your turn" / "during your turn".</summary>
public sealed record YourTurn : Condition;

/// <summary>"If the source has N or more +1/+1 counters on it".</summary>
public sealed record SourceHasCounters(int AtLeast) : Condition;

/// <summary>"As long as it's attacking".</summary>
public sealed record SourceAttacking : Condition;

/// <summary>"If it's a Zombie card" / "if it was a creature card": the target at <paramref name="Index"/> matches.</summary>
public sealed record TargetMatches(int Index, ObjectFilter Filter) : Condition;

/// <summary>"As long as your life total is at least N greater than your starting life total".</summary>
public sealed record LifeAboveStarting(int AtLeast) : Condition;

/// <summary>"If [condition A] and [condition B]".</summary>
public sealed record All(IReadOnlyList<Condition> Conditions) : Condition;

/// <summary>"If creatures you control have total power N or greater".</summary>
public sealed record TotalPowerAtLeast(int Amount) : Condition;

/// <summary>"If you attacked with N or more creatures" (counts creatures you control that are attacking).</summary>
public sealed record AttackingCreatures(int AtLeast) : Condition;
