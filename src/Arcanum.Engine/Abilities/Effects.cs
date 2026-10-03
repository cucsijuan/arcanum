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
public sealed record ExileIt(Subject What) : Effect
{
    /// <summary>"Exile it with a [kind] counter on it."</summary>
    public CounterKind? WithCounter { get; init; }
}
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
public sealed record CreateTokens(CardDefinition Token, Quantity Count, Subject Controller, bool Tapped = false) : Effect
{
    /// <summary>"They gain haste until end of turn."</summary>
    public bool HasteUntilEndOfTurn { get; init; }
}

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne, Loyalty, Stun, Divinity, Revival, Page, Wish, Soul, Incubation, Fellowship, Bait, Stash }

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
public sealed record PutOntoBattlefield(Subject What, bool Tapped = false, bool UnderOwnersControl = false) : Effect
{
    /// <summary>It enters with these +1/+1 counters, and keeps these extra creature types and keywords.</summary>
    public int Counters { get; init; }
    public CounterKind CounterKind { get; init; } = CounterKind.PlusOnePlusOne;
    public IReadOnlyList<string>? AddSubtypes { get; init; }
    public IReadOnlyList<Cards.Keyword>? AddKeywords { get; init; }
}

/// <summary>
/// Search the controller's library for up to <paramref name="Count"/> cards matching <paramref name="Filter"/>,
/// put them into <paramref name="To"/> (hand, battlefield, graveyard or the top of the library), then shuffle.
/// </summary>
public sealed record SearchLibrary(ObjectFilter Filter, int Count, State.Zone To, bool Tapped = false) : Effect
{
    /// <summary>Whose library (and who searches): the controller by default.</summary>
    public Subject? Who { get; init; }

    /// <summary>"May search": the searching player is asked first.</summary>
    public bool Optional { get; init; }
}

/// <summary>"[Players] sacrifice N [permanents] of their choice."</summary>
public sealed record Sacrifice(Quantity Count, ObjectFilter Filter, Subject Who) : Effect;

/// <summary>Put a target creature on the top or bottom of its owner's library (the owner chooses: "on their choice").</summary>
public sealed record PutIntoLibrary(Subject What, bool Bottom = false) : Effect
{
    /// <summary>Always on top (no choice).</summary>
    public bool Top { get; init; }
}

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
public sealed record LookAtTopTake(int Count, ObjectFilter? Filter, int Take, State.Zone TakeTo, bool RestToGraveyard = false) : Effect
{
    /// <summary>Look at X cards (X of the spell) instead of <see cref="Count"/>.</summary>
    public bool CountIsX { get; init; }

    /// <summary>Only cards with mana value X or less qualify.</summary>
    public bool MaxManaValueX { get; init; }
}

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

/// <summary>"You may pay {X}. When you do, [effects]" — the effects use the X paid.</summary>
public sealed record MayPayX(string Prompt, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"You have no maximum hand size for the rest of the game."</summary>
public sealed record NoMaximumHandSizeForever : Effect;

/// <summary>"When you next cast an instant or sorcery spell this turn, copy that spell."</summary>
public sealed record CopyNextInstantOrSorcery : Effect;

/// <summary>Exile the top card of each player's library; you may cast any number of spells from among them without paying their mana costs.</summary>
public sealed record CastFromEachLibraryTopFree : Effect;

/// <summary>Return this card from the graveyard to the battlefield at the beginning of the next end step, with one fewer <paramref name="Kind"/> counter than it had.</summary>
public sealed record ReturnAtNextEndStepWithOneFewer(CounterKind Kind) : Effect;

/// <summary>Destroy each nonland permanent with mana value X whose controller was dealt combat damage by the source this turn.</summary>
public sealed record DestroyManaValueXOfDamagedPlayers : Effect;

/// <summary>Add mana that stays until end of turn.</summary>
public sealed record AddManaUntilEndOfTurn(IReadOnlyList<Mana.ManaType> Types) : Effect;

/// <summary>Choose cards (not targeted) from your graveyard and put them into <paramref name="To"/> ("return an instant or sorcery card from your graveyard to your hand").</summary>
public sealed record ReturnFromGraveyard(ObjectFilter? Filter, int Count, State.Zone To, bool UpTo = false) : Effect
{
    /// <summary>"Another" card: not one sacrificed by this effect.</summary>
    public bool ExcludeSacrificed { get; init; }
}

/// <summary>"[Players] discard their hand."</summary>
public sealed record DiscardHand(Subject Who) : Effect;

/// <summary>"[Players] reveal cards from the top of their library until they reveal a [filter] card, then put those cards into their graveyard."</summary>
public sealed record MillUntil(Subject Who, ObjectFilter Until) : Effect;

/// <summary>Create an emblem for the controller: an object in the command zone with these abilities.</summary>
public sealed record CreateEmblem(string Name, IReadOnlyList<AbilityDefinition> Abilities) : Effect;

/// <summary>Exile the top N cards of your library; you choose one (or all with <paramref name="ChooseOne"/> false) and may play it this turn.</summary>
public sealed record ExileTopPlayable(int Count, bool ChooseOne = true, bool UntilEndOfNextTurn = false, bool WithoutPaying = false) : Effect;

/// <summary>Divide damage as you choose among the chosen targets (at least 1 to each, rule 601.2d).</summary>
public sealed record DealDamageDivided(int Total) : Effect;

/// <summary>"Each [player] chooses a permanent they control of each permanent type and sacrifices the rest."</summary>
public sealed record KeepOneOfEachType(Subject Who) : Effect;

/// <summary>
/// The subject becomes a creature (and/or gains types, subtypes, keywords, abilities) until end of turn, or for as long
/// as it stays on the battlefield when <paramref name="Permanent"/>.
/// </summary>
public sealed record Become(Subject What, int? Power = null, int? Toughness = null, Cards.CardType AddTypes = 0,
    IReadOnlyList<string>? AddSubtypes = null, IReadOnlyList<Cards.Keyword>? Keywords = null, IReadOnlyList<AbilityDefinition>? Abilities = null,
    bool Permanent = false) : Effect
{
    /// <summary>Replaces its creature types ("becomes a Human Faerie Detective").</summary>
    public IReadOnlyList<string>? SetSubtypes { get; init; }
}

/// <summary>Destroy the target and every other permanent with the same name.</summary>
public sealed record DestroySameName(Subject What) : Effect;

/// <summary>Distribute N +1/+1 counters among the chosen targets (at least one each).</summary>
public sealed record DistributeCounters(int Total) : Effect;

/// <summary>Reveal cards from the top of your library until a matching card; put it into <paramref name="To"/>, the rest on the bottom in a random order.</summary>
public sealed record RevealUntil(ObjectFilter Filter, State.Zone To) : Effect;

/// <summary>
/// Each subject player may pay one of the options (discard a card, sacrifice a permanent, pay life...);
/// a player who doesn't suffers <paramref name="Otherwise"/> ("loses 3 life unless they ...").
/// </summary>
public sealed record Unless(Subject Who, IReadOnlyList<ExtraCost> Options, IReadOnlyList<Effect> Otherwise) : Effect;

/// <summary>"Any opponent may sacrifice a [filter]. If a player does, [effects]."</summary>
public sealed record OpponentMaySacrifice(ObjectFilter Filter, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Look at the top N, split them into a face-down and a face-up pile; an opponent picks the pile you put into your hand, the other goes to your graveyard.</summary>
public sealed record Piles(int Count) : Effect;

/// <summary>"You win the game" (when the condition holds).</summary>
public sealed record WinGame : Effect;

/// <summary>"Untap up to N lands."</summary>
public sealed record UntapUpTo(int Count, ObjectFilter Filter) : Effect;

/// <summary>Give players poison counters.</summary>
public sealed record AddPoison(int Count, Subject Who) : Effect;

/// <summary>"End the turn" (rule 724).</summary>
public sealed record EndTheTurn : Effect;

/// <summary>An additional combat phase after this one; <paramref name="UntapCreatures"/> untaps your creatures first.</summary>
public sealed record AdditionalCombat(bool UntapCreatures) : Effect;

/// <summary>Copy the target spell (or the spell the trigger was about); the copy's controller may choose new targets.</summary>
public sealed record CopySpell(Subject What, Quantity Count) : Effect;

/// <summary>Add one mana of any color (the controller chooses).</summary>
public sealed record AddManaOfAnyColor(int Count = 1) : Effect;

/// <summary>Put cards exiled by this permanent ("with it") into their owners' hands.</summary>
public sealed record ReturnExiledWithThis : Effect;

/// <summary>Search your library and exile the card, linked to this permanent (for a later "the exiled card").</summary>
public sealed record SearchAndExileWithThis(ObjectFilter Filter) : Effect;

/// <summary>The target card in your graveyard gains flashback (cost: its mana cost) until end of turn.</summary>
public sealed record GrantFlashback(Subject What) : Effect;

/// <summary>"You may cast the target card from your graveyard this turn."</summary>
public sealed record PlayableFromGraveyardThisTurn(Subject What) : Effect;

/// <summary>"You lose the game."</summary>
public sealed record LoseGame : Effect;
