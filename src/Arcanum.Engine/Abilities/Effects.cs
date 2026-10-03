// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Abilities;

/// <summary>One instruction carried out when a spell or ability resolves (rule 608.2).</summary>
public abstract record Effect;

public sealed record DealDamage(int Amount, Subject To) : Effect;
public sealed record DrawCards(int Count, Subject Who) : Effect;
public sealed record GainLife(int Amount, Subject Who) : Effect;
public sealed record LoseLife(int Amount, Subject Who) : Effect;
public sealed record Destroy(Subject What) : Effect;
public sealed record ExileIt(Subject What) : Effect;
public sealed record ReturnToHand(Subject What) : Effect;
public sealed record TapIt(Subject What) : Effect;
public sealed record UntapIt(Subject What) : Effect;
public sealed record Mill(int Count, Subject Who) : Effect;
public sealed record CounterSpell(Subject What) : Effect;

/// <summary>"Target creature gets +N/+N (and gains a keyword) until end of turn."</summary>
public sealed record PumpUntilEndOfTurn(int Power, int Toughness, Subject What, IReadOnlyList<Keyword>? Keywords = null) : Effect;

/// <summary>Put +1/+1 (or -1/-1) counters on a creature.</summary>
public sealed record AddCounters(int Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

public sealed record CreateTokens(CardDefinition Token, int Count, Subject Controller) : Effect;

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne }

/// <summary>Look at the top N cards of your library; put any number on the bottom, the rest back on top (rule 701.22).</summary>
public sealed record Scry(int Count) : Effect;

/// <summary>Look at the top N cards of your library; put any number into your graveyard, the rest back on top (rule 701.25).</summary>
public sealed record Surveil(int Count) : Effect;

/// <summary>Each of two creatures deals damage equal to its power to the other (rule 701.14).</summary>
public sealed record Fight(Subject First, Subject Second) : Effect;

/// <summary>"[Player] discards N cards" — the discarding player chooses.</summary>
public sealed record Discard(int Count, Subject Who) : Effect;

/// <summary>Effects that happen only if a condition holds when the effect is reached ("If you control a Wizard, ...").</summary>
public sealed record IfThen(Condition Condition, IReadOnlyList<Effect> Then, IReadOnlyList<Effect>? Else = null) : Effect;

/// <summary>"You may [effects]": the controller chooses whether to carry them out as the ability resolves.</summary>
public sealed record MayDo(string Prompt, IReadOnlyList<Effect> Effects) : Effect;
