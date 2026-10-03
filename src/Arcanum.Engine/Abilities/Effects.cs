// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Abilities;

/// <summary>One instruction carried out when a spell or ability resolves (rule 608.2).</summary>
public abstract record Effect;

public sealed record DealDamage(Quantity Amount, Subject To) : Effect;
public sealed record DrawCards(Quantity Count, Subject Who) : Effect;
public sealed record GainLife(Quantity Amount, Subject Who) : Effect;
public sealed record LoseLife(Quantity Amount, Subject Who) : Effect;
public sealed record Destroy(Subject What) : Effect;
public sealed record ExileIt(Subject What) : Effect;
public sealed record ReturnToHand(Subject What) : Effect;
public sealed record TapIt(Subject What) : Effect;
public sealed record UntapIt(Subject What) : Effect;
public sealed record Mill(Quantity Count, Subject Who) : Effect;
public sealed record CounterSpell(Subject What) : Effect;

/// <summary>"Target creature gets +N/+N (and gains a keyword) until end of turn."</summary>
public sealed record PumpUntilEndOfTurn(Quantity Power, Quantity Toughness, Subject What, IReadOnlyList<Keyword>? Keywords = null) : Effect;

/// <summary>Put +1/+1 (or -1/-1) counters on a creature.</summary>
public sealed record AddCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <param name="Tapped">The tokens enter tapped.</param>
public sealed record CreateTokens(CardDefinition Token, Quantity Count, Subject Controller, bool Tapped = false) : Effect;

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne }

/// <summary>Look at the top N cards of your library; put any number on the bottom, the rest back on top (rule 701.22).</summary>
public sealed record Scry(int Count) : Effect;

/// <summary>Look at the top N cards of your library; put any number into your graveyard, the rest back on top (rule 701.25).</summary>
public sealed record Surveil(int Count) : Effect;

/// <summary>Each of two creatures deals damage equal to its power to the other (rule 701.14).</summary>
public sealed record Fight(Subject First, Subject Second) : Effect;

/// <summary>"[Player] discards N cards" — the discarding player chooses.</summary>
public sealed record Discard(Quantity Count, Subject Who) : Effect;

/// <summary>Effects that happen only if a condition holds when the effect is reached ("If you control a Wizard, ...").</summary>
public sealed record IfThen(Condition Condition, IReadOnlyList<Effect> Then, IReadOnlyList<Effect>? Else = null) : Effect;

/// <summary>"You may [effects]": the controller chooses whether to carry them out as the ability resolves.</summary>
public sealed record MayDo(string Prompt, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Put cards (from a graveyard, usually) onto the battlefield; "under your control" unless <paramref name="UnderOwnersControl"/>.</summary>
public sealed record PutOntoBattlefield(Subject What, bool Tapped = false, bool UnderOwnersControl = false) : Effect;

/// <summary>
/// Search the controller's library for up to <paramref name="Count"/> cards matching <paramref name="Filter"/>,
/// put them into <paramref name="To"/> (hand, battlefield, graveyard or the top of the library), then shuffle.
/// </summary>
public sealed record SearchLibrary(ObjectFilter Filter, int Count, State.Zone To, bool Tapped = false) : Effect;

/// <summary>"[Players] sacrifice N [permanents] of their choice."</summary>
public sealed record Sacrifice(Quantity Count, ObjectFilter Filter, Subject Who) : Effect;

/// <summary>Put a target creature on the top or bottom of its owner's library (the owner chooses: "on their choice").</summary>
public sealed record PutIntoLibrary(Subject What, bool Bottom = false) : Effect;

/// <summary>The spell's controller gains control of the subject (until end of turn when <paramref name="UntilEndOfTurn"/>).</summary>
public sealed record GainControl(Subject What, bool UntilEndOfTurn = false) : Effect;

/// <summary>
/// Effects of one chosen mode of a modal spell: their target indices start at <paramref name="TargetOffset"/> in the
/// stack object's targets. Built by the engine when modes are chosen.
/// </summary>
public sealed record ModeEffects(int TargetOffset, IReadOnlyList<Effect> Effects) : Effect;
