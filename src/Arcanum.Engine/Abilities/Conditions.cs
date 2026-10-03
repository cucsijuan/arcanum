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
public sealed record CardsInGraveyard(int AtLeast) : Condition;

/// <summary>"If you control [N or more] [objects]" — e.g. ferocious is a creature with power 4 or greater.</summary>
public sealed record YouControl(ObjectFilter Filter, int AtLeast = 1) : Condition;

/// <summary>"If you have N or more life".</summary>
public sealed record LifeAtLeast(int Amount) : Condition;

/// <summary>"If [condition] isn't true" / "unless".</summary>
public sealed record Not(Condition Inner) : Condition;
