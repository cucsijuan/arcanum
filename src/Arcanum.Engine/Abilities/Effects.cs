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

    /// <summary>"All other ...": skip tokens this spell or ability created.</summary>
    public bool ExceptCreatedThisWay { get; init; }
}
public sealed record ReturnToHand(Subject What) : Effect;
public sealed record TapIt(Subject What) : Effect;
public sealed record UntapIt(Subject What) : Effect;
public sealed record Mill(Quantity Count, Subject Who) : Effect
{
    /// <summary>"If two nonland cards that share a color were milled this way, repeat this process."</summary>
    public bool RepeatWhileNonlandShareColor { get; init; }
}
public sealed record CounterSpell(Subject What) : Effect
{
    /// <summary>"Unless its controller pays {N}".</summary>
    public Mana.ManaCost? UnlessPays { get; init; }

    /// <summary>A permanent spell countered this way is exiled, and its countering player may cast it for free while it stays exiled.</summary>
    public bool ExilePermanentPlayable { get; init; }
}

/// <summary>"Target creature gets +N/+N (and gains a keyword) until end of turn."</summary>
public sealed record PumpUntilEndOfTurn(Quantity Power, Quantity Toughness, Subject What, IReadOnlyList<Keyword>? Keywords = null) : Effect
{
    /// <summary>Lasts as long as the source stays on the battlefield instead of until end of turn.</summary>
    public bool WhileSourceRemains { get; init; }
}

/// <summary>Put +1/+1 (or -1/-1) counters on a creature.</summary>
public sealed record AddCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <param name="Tapped">The tokens enter tapped.</param>
public sealed record CreateTokens(CardDefinition Token, Quantity Count, Subject Controller, bool Tapped = false) : Effect
{
    /// <summary>"They gain haste until end of turn."</summary>
    public bool HasteUntilEndOfTurn { get; init; }
}

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne, Loyalty, Stun, Divinity, Revival, Page, Wish, Soul, Incubation, Fellowship, Bait, Stash, Lore, Hone, Quest, Trample }

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
    /// <summary>An Aura returned "attached to" this (a target creature).</summary>
    public Subject? AttachTo { get; init; }

    /// <summary>"They're an artifact" / "They are Food artifacts": its card types (and subtypes) from now on.</summary>
    public Cards.CardType? SetTypes { get; init; }
    public IReadOnlyList<string>? SetSubtypes { get; init; }

    /// <summary>Abilities it has from now on, besides its own.</summary>
    public IReadOnlyList<AbilityDefinition>? AddAbilities { get; init; }

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
    /// <summary>Up to this many instead of <see cref="Count"/> ("that many basic land cards").</summary>
    public Quantity? CountFrom { get; init; }

    /// <summary>"Put one onto the battlefield tapped and the other into your hand."</summary>
    public bool OneToBattlefieldRestToHand { get; init; }

    /// <summary>Whose library (and who searches): the controller by default.</summary>
    public Subject? Who { get; init; }

    /// <summary>"May search": the searching player is asked first.</summary>
    public bool Optional { get; init; }

    /// <summary>"Reveal it": the card found is shown to every player.</summary>
    public bool Reveal { get; init; }

    /// <summary>"With mana value X or less" (the X of the spell or ability).</summary>
    public bool MaxManaValueX { get; init; }
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
public sealed record CreateTokenCopy(Subject Of, Quantity Count, bool Haste = false, bool SacrificeAtEndStep = false) : Effect
{
    /// <summary>"Except the tokens aren't legendary."</summary>
    public bool NotLegendary { get; init; }

    /// <summary>"Except it's a Nightmare in addition to its other types."</summary>
    public IReadOnlyList<string>? AddSubtypes { get; init; }
}

/// <summary>Sacrifice the subject (usually the source itself).</summary>
public sealed record SacrificeIt(Subject What) : Effect;

/// <summary>If the subject would die this turn, exile it instead.</summary>
public sealed record ExileIfDiesThisTurn(Subject What) : Effect;

/// <summary>Prevent all combat damage that would be dealt to the subject this turn.</summary>
public sealed record PreventCombatDamageTo(Subject What) : Effect;

/// <summary>Look at the top cards of your library; take up to <paramref name="Take"/> matching ones, the rest go to the bottom (or graveyard).</summary>
public sealed record LookAtTopTake(int Count, ObjectFilter? Filter, int Take, State.Zone TakeTo, bool RestToGraveyard = false) : Effect
{
    /// <summary>The cards taken onto the battlefield enter tapped.</summary>
    public bool Tapped { get; init; }

    /// <summary>"Then shuffle": the rest stay and the library is shuffled.</summary>
    public bool RestShuffled { get; init; }

    /// <summary>Look at X cards (X of the spell) instead of <see cref="Count"/>.</summary>
    public bool CountIsX { get; init; }

    /// <summary>Only cards with mana value X or less qualify.</summary>
    public bool MaxManaValueX { get; init; }

    /// <summary>"Reveal it": the card taken is shown to every player.</summary>
    public bool Reveal { get; init; }

    /// <summary>"Reveal the top X cards": every card looked at is shown to every player.</summary>
    public bool RevealAll { get; init; }
}

/// <summary>"[Player] reveals their hand; you choose a [filter] card from it; they discard it."</summary>
public sealed record DiscardChosenByYou(Subject Who, ObjectFilter? Filter, int Count = 1) : Effect;

/// <summary>Exile every card in the subject players' graveyards.</summary>
public sealed record ExileGraveyard(Subject Who) : Effect;

/// <summary>"Double the number of [kind] counters on it"; with no kind, every kind of counter on it.</summary>
public sealed record DoubleCounters(Subject What, CounterKind? Kind = null) : Effect;

/// <summary>Remove counters from the subject.</summary>
public sealed record RemoveCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <summary>Shuffle the subject players' graveyards into their libraries.</summary>
public sealed record ShuffleGraveyardIntoLibrary(Subject Who) : Effect;

/// <summary>Add mana to the controller's pool.</summary>
public sealed record AddMana(IReadOnlyList<Mana.ManaType> Types) : Effect;

/// <summary>The subject (a creature) deals damage equal to its power to the other subject ("bite").</summary>
public sealed record DealsDamageEqualToPower(Subject Source, Subject To) : Effect;

/// <summary>Put cards from the subject players' graveyards matching the filter onto the battlefield under your control.</summary>
public sealed record ReanimateAll(Subject Who, ObjectFilter Filter) : Effect
{
    /// <summary>What they become as they enter (types, subtypes, abilities), as for <see cref="PutOntoBattlefield"/>.</summary>
    public PutOntoBattlefield? As { get; init; }
}

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
public sealed record ExileTopPlayable(int Count, bool ChooseOne = true, bool UntilEndOfNextTurn = false, bool WithoutPaying = false) : Effect
{
    /// <summary>Exile this many instead of <see cref="Count"/> (X).</summary>
    public Quantity? CountFrom { get; init; }

    /// <summary>Whose library (the controller's by default): "target opponent's library".</summary>
    public Subject? From { get; init; }

    /// <summary>"Pay life equal to its mana value rather than pay its mana cost."</summary>
    public bool PayLife { get; init; }

    /// <summary>"For as long as they remain exiled" instead of this turn.</summary>
    public bool Forever { get; init; }

    /// <summary>Exiled face down: only the controller may look at them.</summary>
    public bool FaceDown { get; init; }

    /// <summary>They may be played only while this holds ("if you control a Wizard").</summary>
    public Condition? While { get; init; }
}

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
    /// <summary>Base power and toughness worked out: once as the effect starts ("equal to Galion's power"), or continuously when <see cref="Continuous"/>.</summary>
    public Quantity? PowerFrom { get; init; }
    public Quantity? ToughnessFrom { get; init; }
    public bool Continuous { get; init; }

    /// <summary>Lasts as long as the source stays on the battlefield ("for as long as this Saga remains on the battlefield").</summary>
    public bool WhileSourceRemains { get; init; }

    /// <summary>Replaces its creature types ("becomes a Human Faerie Detective").</summary>
    public IReadOnlyList<string>? SetSubtypes { get; init; }
}

/// <summary>Destroy the target and every other permanent with the same name.</summary>
public sealed record DestroySameName(Subject What) : Effect;

/// <summary>Distribute N +1/+1 counters among the chosen targets (at least one each).</summary>
public sealed record DistributeCounters(int Total) : Effect;

/// <summary>Reveal cards from the top of your library until a matching card; put it into <paramref name="To"/>, the rest on the bottom in a random order.</summary>
public sealed record RevealUntil(ObjectFilter Filter, State.Zone To) : Effect
{
    /// <summary>The card found goes onto the battlefield instead when it also matches this ("if its mana value is less than or equal to …").</summary>
    public ObjectFilter? BattlefieldIf { get; init; }
}

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

/// <summary>"Take an extra turn after this one" for the subject players.</summary>
public sealed record ExtraTurn(Subject Who) : Effect;

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

/// <summary>"Change the target of target spell or ability with a single target" (to another legal one, chosen by the controller).</summary>
public sealed record ChangeTarget(Subject What) : Effect;

/// <summary>"Counter it unless its controller pays [cost]" for the stack object <paramref name="StackObject"/> (ward).</summary>
public sealed record CounterUnlessPays(int StackObject, Mana.ManaCost Mana, int Life) : Effect;

/// <summary>"When you do, …": creates a reflexive triggered ability (with its own targets) if <see cref="If"/> shows the action before it happened.</summary>
public sealed record ReflexiveTrigger(TriggeredAbility Ability, Condition? If = null) : Effect
{
    /// <summary>What the reflexive ability is about ("that creature"), and an amount worked out now ("that creature's power").</summary>
    public Subject? About { get; init; }
    public Quantity? Amount { get; init; }
}

/// <summary>
/// Amass [type] N (rule 701.47): if the player controls no Army, they create <paramref name="Token"/> (a 0/0 black
/// [type] Army); then they put N +1/+1 counters on an Army they control, which becomes a [type] in addition.
/// </summary>
public sealed record Amass(Quantity Count, string Subtype, CardDefinition Token, Subject Who) : Effect;

/// <summary>Recruit: the player draws a card, then discards a card; if a nonland card was discarded, they create <paramref name="Token"/> (a 1/1 white Human Soldier).</summary>
public sealed record Recruit(CardDefinition Token, Subject Who) : Effect;

/// <summary>Attach Auras or Equipment (<paramref name="What"/>) to a permanent (<paramref name="To"/>).</summary>
public sealed record Attach(Subject What, Subject To) : Effect;

/// <summary>Put cards milled by this effect into their owner's hand: all matching ones (<paramref name="Count"/> &lt; 0) or up to <paramref name="Count"/> chosen.</summary>
public sealed record TakeMilled(ObjectFilter? Filter, int Count) : Effect;

/// <summary>Remove every counter from the subject.</summary>
public sealed record RemoveAllCounters(Subject What) : Effect;

/// <summary>Exile the subject, then return it to the battlefield at once under its owner's control.</summary>
public sealed record Blink(Subject What) : Effect;

/// <summary>Its owner shuffles the subject into their library.</summary>
public sealed record ShuffleIntoLibrary(Subject What) : Effect;

/// <summary>"You may play an additional land this turn."</summary>
public sealed record AdditionalLandThisTurn : Effect;

/// <summary>"Players can't cast spells this turn."</summary>
public sealed record NoSpellsThisTurn : Effect;

/// <summary>Exchange control of two permanents.</summary>
public sealed record ExchangeControl(Subject First, Subject Second) : Effect;

/// <summary>A delayed triggered ability: "at the beginning of the next upkeep, …", with an amount worked out now.</summary>
public sealed record AtNextUpkeep(TriggeredAbility Ability, Quantity? Amount = null) : Effect;

/// <summary>"Choose a creature type": the source remembers it (for "creatures of that type").</summary>
public sealed record ChooseCreatureType : Effect;

/// <summary>Reveal the top N cards, put a random matching one into <paramref name="To"/>, the rest on the bottom in a random order.</summary>
public sealed record RevealTopPutRandom(int Count, ObjectFilter Filter, State.Zone To) : Effect;

/// <summary>Search your hand and/or library for a matching card and put it into <paramref name="To"/>; shuffle.</summary>
public sealed record SearchHandOrLibrary(ObjectFilter Filter, State.Zone To) : Effect;

/// <summary>Add mana in any combination of colors, spendable only on spells matching <paramref name="OnlyFor"/>.</summary>
public sealed record AddManaInAnyCombination(int Count, ObjectFilter? OnlyFor) : Effect;

/// <summary>"You may behold a [filter]" (choose one you control or reveal one from your hand); if you do, <paramref name="Effects"/>.</summary>
public sealed record Behold(ObjectFilter Filter, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"You may cast a [filter] spell from your graveyard"; an instant or sorcery cast this way is exiled instead of going to the graveyard.</summary>
public sealed record CastFromGraveyardNow(ObjectFilter Filter) : Effect;

/// <summary>Prevent all damage the subject would deal, for as long as the source remains on the battlefield.</summary>
public sealed record PreventDamageBy(Subject What) : Effect;
