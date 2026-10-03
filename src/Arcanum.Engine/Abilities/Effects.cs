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

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne, Loyalty, Stun, Divinity, Revival, Page, Wish, Soul, Incubation, Fellowship, Bait }

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
public sealed record GainControl(Subject What, bool UntilEndOfTurn = false, Subject? NewController = null) : Effect;

/// <summary>
/// Effects of one chosen mode of a modal spell: their target indices start at <paramref name="TargetOffset"/> in the
/// stack object's targets. Built by the engine when modes are chosen.
/// </summary>
public sealed record ModeEffects(int TargetOffset, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Exile until the source leaves the battlefield ("until this enchantment leaves the battlefield").</summary>
public sealed record ExileUntilSourceLeaves(Subject What) : Effect;

/// <summary>Exile, then return to the battlefield at the beginning of the next end step (under its owner's control, or yours).</summary>
public sealed record ExileAndReturnAtEndStep(Subject What, bool UnderYourControl = false) : Effect;

/// <summary>Create token copies of a permanent, optionally with haste and "sacrifice it at the beginning of the next end step".</summary>
public sealed record CreateTokenCopy(Subject Of, Quantity Count, bool Haste = false, bool SacrificeAtEndStep = false) : Effect;

/// <summary>Sacrifice the subject (usually the source itself).</summary>
public sealed record SacrificeIt(Subject What) : Effect;

/// <summary>If the subject would die this turn, exile it instead.</summary>
public sealed record ExileIfDiesThisTurn(Subject What) : Effect;

/// <summary>Prevent all combat damage that would be dealt to the subject this turn.</summary>
public sealed record PreventCombatDamageTo(Subject What) : Effect;

/// <summary>Look at the top cards of your library; take up to <paramref name="Take"/> matching ones, the rest go to the bottom (or graveyard).</summary>
public sealed record LookAtTopTake(int Count, ObjectFilter? Filter, int Take, State.Zone TakeTo, bool RestToGraveyard = false) : Effect;

/// <summary>"[Player] reveals their hand; you choose a [filter] card from it; they discard it."</summary>
public sealed record DiscardChosenByYou(Subject Who, ObjectFilter? Filter, int Count = 1) : Effect;

/// <summary>Exile every card in the subject players' graveyards.</summary>
public sealed record ExileGraveyard(Subject Who) : Effect;

/// <summary>Double the number of +1/+1 counters on the subject.</summary>
public sealed record DoubleCounters(Subject What) : Effect;

/// <summary>Remove counters from the subject.</summary>
public sealed record RemoveCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <summary>Shuffle the subject players' graveyards into their libraries.</summary>
public sealed record ShuffleGraveyardIntoLibrary(Subject Who) : Effect;

/// <summary>Add mana to the controller's pool.</summary>
public sealed record AddMana(IReadOnlyList<Mana.ManaType> Types) : Effect;

/// <summary>The subject (a creature) deals damage equal to its power to the other subject ("bite").</summary>
public sealed record DealsDamageEqualToPower(Subject Source, Subject To) : Effect;

/// <summary>Put cards from the subject players' graveyards matching the filter onto the battlefield under your control.</summary>
public sealed record ReanimateAll(Subject Who, ObjectFilter Filter) : Effect;

/// <summary>Return every permanent matching the filter to its owner's hand (filter controller relative to <paramref name="RelativeTo"/>).</summary>
public sealed record BounceAll(ObjectFilter Filter, Subject? RelativeTo = null) : Effect;

/// <summary>"You may [pay a cost]. If you do, [effects]."</summary>
public sealed record MayPay(string Prompt, Mana.ManaCost? Mana, ExtraCost? Extra, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Choose cards (not targeted) from your graveyard and put them into <paramref name="To"/> ("return an instant or sorcery card from your graveyard to your hand").</summary>
public sealed record ReturnFromGraveyard(ObjectFilter? Filter, int Count, State.Zone To, bool UpTo = false) : Effect;

/// <summary>"[Players] discard their hand."</summary>
public sealed record DiscardHand(Subject Who) : Effect;
