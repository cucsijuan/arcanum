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
