// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Abilities;

/// <summary>One instruction carried out when a spell or ability resolves (rule 608.2).</summary>
public abstract record Effect;

public sealed record DealDamage(Quantity Amount, Subject To) : Effect
{
    /// <summary>"Excess damage is dealt to that creature's controller instead".</summary>
    public bool ExcessToController { get; init; }
}
public sealed record DrawCards(Quantity Count, Subject Who) : Effect;
public sealed record GainLife(Quantity Amount, Subject Who) : Effect;
public sealed record LoseLife(Quantity Amount, Subject Who) : Effect;
public sealed record Destroy(Subject What) : Effect
{
    /// <summary>"They can't be regenerated."</summary>
    public bool CantBeRegenerated { get; init; }
}
public sealed record ExileIt(Subject What) : Effect
{
    /// <summary>"Exile it with a [kind] counter on it."</summary>
    public CounterKind? WithCounter { get; init; }

    /// <summary>"All other ...": skip tokens this spell or ability created.</summary>
    public bool ExceptCreatedThisWay { get; init; }

    /// <summary>Linked to this object (rule 607): a later "return the exiled card" of the same object finds it.</summary>
    public bool Linked { get; init; }
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
    /// <summary>Keywords it loses until end of turn ("loses indestructible").</summary>
    public IReadOnlyList<Keyword>? LoseKeywords { get; init; }

    /// <summary>Lasts as long as the source stays on the battlefield instead of until end of turn.</summary>
    public bool WhileSourceRemains { get; init; }

    /// <summary>"For as long as you control this Saga": also ends once the ability's controller stops controlling the source.</summary>
    public bool WhileYouControlSource { get; init; }

    /// <summary>Lasts until the ability's controller's next turn instead of until end of turn.</summary>
    public bool UntilYourNextTurn { get; init; }
}

/// <summary>Put +1/+1 (or -1/-1) counters on a creature.</summary>
public sealed record AddCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <param name="Tapped">The tokens enter tapped.</param>
public sealed record CreateTokens(CardDefinition Token, Quantity Count, Subject Controller, bool Tapped = false) : Effect
{
    /// <summary>"Sacrifice that token at end of combat".</summary>
    public bool SacrificeAtEndOfCombat { get; init; }

    /// <summary>"Tapped and attacking": they attack the player (or planeswalker) the triggering creature attacks.</summary>
    public bool Attacking { get; init; }

    /// <summary>"They gain haste until end of turn."</summary>
    public bool HasteUntilEndOfTurn { get; init; }

    /// <summary>"An X/X token": its power and toughness worked out as it's created.</summary>
    public Quantity? PowerAndToughness { get; init; }
}

public enum CounterKind { PlusOnePlusOne, MinusOneMinusOne, Loyalty, Stun, Divinity, Revival, Page, Wish, Soul, Incubation, Fellowship, Bait, Stash, Lore, Hone, Quest, Trample, Indestructible, Lifelink, Shadow, Hope, Influence, Burden,
    FirstStrike, DoubleStrike, Deathtouch, Flying, Haste, Hexproof, Menace, Reach, Vigilance, Verse, Charge, Ribbon, Luck, Unity, Time }

/// <summary>Look at the top N cards of your library; put any number on the bottom, the rest back on top (rule 701.22).</summary>
public sealed record Scry(int Count) : Effect
{
    /// <summary>"Scry X": the number worked out as it happens.</summary>
    public Quantity? CountFrom { get; init; }
}

/// <summary>Look at the top N cards of your library; put any number into your graveyard, the rest back on top (rule 701.25).</summary>
public sealed record Surveil(int Count) : Effect;

/// <summary>Each of two creatures deals damage equal to its power to the other (rule 701.14).</summary>
public sealed record Fight(Subject First, Subject Second) : Effect;

/// <summary>"[Player] discards N cards" — the discarding player chooses.</summary>
public sealed record Discard(Quantity Count, Subject Who) : Effect;

/// <summary>Effects that happen only if a condition holds when the effect is reached ("If you control a Wizard, ...").</summary>
public sealed record IfThen(Condition Condition, IReadOnlyList<Effect> Then, IReadOnlyList<Effect>? Else = null) : Effect;

/// <summary>"You may [effects]": the controller chooses whether to carry them out as the ability resolves.</summary>
public sealed record MayDo(string Prompt, IReadOnlyList<Effect> Effects) : Effect
{
    /// <summary>"Do this only once each turn": not offered again this turn once done.</summary>
    public bool OncePerTurn { get; init; }

    /// <summary>"If you don't, …".</summary>
    public IReadOnlyList<Effect>? Else { get; init; }
}

/// <summary>Put cards (from a graveyard, usually) onto the battlefield; "under your control" unless <paramref name="UnderOwnersControl"/>.</summary>
public sealed record PutOntoBattlefield(Subject What, bool Tapped = false, bool UnderOwnersControl = false) : Effect
{
    /// <summary>"Tapped and attacking": it attacks a player or planeswalker its controller chooses (rule 508.4).</summary>
    public bool Attacking { get; init; }

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

    /// <summary>"With a vigilance counter and a lifelink counter on it": one counter of each kind.</summary>
    public IReadOnlyList<CounterKind>? CounterKinds { get; init; }
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

    /// <summary>"Basic land cards that share a land type".</summary>
    public bool ShareLandType { get; init; }

    /// <summary>"Put both cards onto the battlefield": the cards this effect exiled earlier enter together with what's found.</summary>
    public bool WithExiled { get; init; }
}

/// <summary>"[Players] sacrifice N [permanents] of their choice."</summary>
public sealed record Sacrifice(Quantity Count, ObjectFilter Filter, Subject Who) : Effect;

/// <summary>Put a target creature on the top or bottom of its owner's library (the owner chooses: "on their choice").</summary>
public sealed record PutIntoLibrary(Subject What, bool Bottom = false) : Effect
{
    /// <summary>Always on top (no choice).</summary>
    public bool Top { get; init; }

    /// <summary>"Second from the top" (2), "fifth from the top" (5): that position, or the bottom of a shorter library.</summary>
    public int Position { get; init; }
}

/// <summary>"Put your choice of a counter from among first strike, vigilance, … on it".</summary>
public sealed record AddChosenCounter(IReadOnlyList<CounterKind> Kinds, Subject What) : Effect;

/// <summary>"Put one of each of those kinds of counters on …": the kinds the trigger was about.</summary>
public sealed record AddCountersOfTriggeredKinds(Subject What) : Effect;

/// <summary>One of the choices of <see cref="ChooseOneEffect"/>.</summary>
public sealed record EffectChoice(string Text, IReadOnlyList<Effect> Effects);

/// <summary>"Gains your choice of lifelink or indestructible": the controller chooses one as it resolves (not a modal spell).</summary>
public sealed record ChooseOneEffect(IReadOnlyList<EffectChoice> Choices) : Effect;

/// <summary>The spell's controller gains control of the subject (until end of turn when <paramref name="UntilEndOfTurn"/>).</summary>
public sealed record GainControl(Subject What, bool UntilEndOfTurn = false, Subject? NewController = null) : Effect
{
    /// <summary>"For as long as you control this creature": ends once the source leaves or its controller changes (rule 611.2b).</summary>
    public bool WhileYouControlSource { get; init; }

    /// <summary>"Until the end of your next turn".</summary>
    public bool UntilEndOfYourNextTurn { get; init; }
}

/// <summary>
/// Effects of one chosen mode of a modal spell: their target indices start at <paramref name="TargetOffset"/> in the
/// stack object's targets. Built by the engine when modes are chosen.
/// </summary>
public sealed record ModeEffects(int TargetOffset, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Exile until the source leaves the battlefield ("until this enchantment leaves the battlefield").</summary>
public sealed record ExileUntilSourceLeaves(Subject What) : Effect
{
    /// <summary>"You may cast that card for as long as it remains exiled, and mana of any type can be spent to cast that spell."</summary>
    public bool Castable { get; init; }
}

/// <summary>Exile, then return to the battlefield at the beginning of the next end step (under its owner's control, or yours).</summary>
public sealed record ExileAndReturnAtEndStep(Subject What, bool UnderYourControl = false) : Effect;

/// <summary>Create token copies of a permanent, optionally with haste and "sacrifice it at the beginning of the next end step".</summary>
public sealed record CreateTokenCopy(Subject Of, Quantity Count, bool Haste = false, bool SacrificeAtEndStep = false) : Effect
{
    /// <summary>"Exile the token at end of combat."</summary>
    public bool ExileAtEndOfCombat { get; init; }

    /// <summary>"Except the tokens aren't legendary."</summary>
    public bool NotLegendary { get; init; }

    /// <summary>"Except it's a Nightmare in addition to its other types."</summary>
    public IReadOnlyList<string>? AddSubtypes { get; init; }

    /// <summary>"Except it's a 3/3 black Wraith with menace" / "except it's a Food artifact … and it loses all other card types".</summary>
    public int? SetPower { get; init; }
    public int? SetToughness { get; init; }
    public IReadOnlyList<string>? SetColors { get; init; }
    public CardType? SetTypes { get; init; }
    public IReadOnlyList<string>? SetSubtypes { get; init; }
    public IReadOnlyList<string>? AddKeywords { get; init; }
    public IReadOnlyList<AbilityDefinition>? AddAbilities { get; init; }

    public bool Tapped { get; init; }

    /// <summary>"Tapped and attacking".</summary>
    public bool Attacking { get; init; }

    /// <summary>"At the beginning of the next end step, [this effect] to that token" (unless the condition holds then).</summary>
    public IReadOnlyList<Effect>? AtNextEndStep { get; init; }
    public Condition? AtNextEndStepUnless { get; init; }
}

/// <summary>
/// "Look at the top N cards of your library, put them back in any order, then choose land or nonland. An opponent guesses
/// whether the top card of your library is the chosen kind. Reveal that card. If they guessed right, [right]. Otherwise, [wrong]."
/// </summary>
public sealed record GuessTopCard(int Look, IReadOnlyList<Effect> Right, IReadOnlyList<Effect> Wrong) : Effect;

/// <summary>"Sacrifice any number of [filter]" (the number is counted by "sacrificed this way").</summary>
public sealed record SacrificeAnyNumber(ObjectFilter Filter) : Effect;

/// <summary>
/// "Reveal the top X cards. Choose any number of [filter] cards revealed this way. Put all nonland cards chosen onto the
/// battlefield, then all land cards chosen onto the battlefield tapped, then the rest on the bottom in a random order."
/// </summary>
public sealed record RevealTopPutAny(Quantity Count, ObjectFilter Filter) : Effect;

/// <summary>"Note a creature type that hasn't been noted for [this]. When you next cast a creature spell of that type this turn, it enters with an additional +1/+1 counter."</summary>
public sealed record NoteCreatureType : Effect;

/// <summary>Sacrifice the subject (usually the source itself).</summary>
public sealed record SacrificeIt(Subject What) : Effect;

/// <summary>If the subject would die this turn, exile it instead.</summary>
public sealed record ExileIfDiesThisTurn(Subject What) : Effect;

/// <summary>Prevent all combat damage that would be dealt to the subject this turn.</summary>
public sealed record PreventCombatDamageTo(Subject What) : Effect;

/// <summary>How the cards left over from a look at the top of the library are ordered.</summary>
public enum RestOrder
{
    /// <summary>"In a random order" (bottom); on top they stay as they were.</summary>
    Random,

    /// <summary>"In any order": the player chooses.</summary>
    Chosen,
}

/// <summary>Look at the top cards of your library; take up to <paramref name="Take"/> matching ones, the rest go to the bottom (or graveyard).</summary>
public sealed record LookAtTopTake(int Count, ObjectFilter? Filter, int Take, State.Zone TakeTo, bool RestToGraveyard = false) : Effect
{
    /// <summary>The cards not taken stay on top of the library (a look at the top card).</summary>
    public bool RestOnTop { get; init; }

    /// <summary>How the rest are ordered when they go to the bottom (random by default) or back on top (as they were by default).</summary>
    public RestOrder RestOrder { get; init; }

    /// <summary>How many cards, worked out on resolution ("the top X cards, where X is that creature's mana value").</summary>
    public Quantity? CountFrom { get; init; }

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
public sealed record ExileGraveyard(Subject Who) : Effect
{
    /// <summary>Only the matching cards.</summary>
    public ObjectFilter? Filter { get; init; }

    /// <summary>The controller may cast spells from among them while they stay exiled, spending mana of any type.</summary>
    public bool Playable { get; init; }
}

/// <summary>"Double the number of [kind] counters on it"; with no kind, every kind of counter on it.</summary>
public sealed record DoubleCounters(Subject What, CounterKind? Kind = null) : Effect;

/// <summary>Remove counters from the subject.</summary>
public sealed record RemoveCounters(Quantity Count, Subject What, CounterKind Kind = CounterKind.PlusOnePlusOne) : Effect;

/// <summary>Shuffle the subject players' graveyards into their libraries.</summary>
public sealed record ShuffleGraveyardIntoLibrary(Subject Who) : Effect;

/// <summary>Add mana to the controller's pool.</summary>
public sealed record AddMana(IReadOnlyList<Mana.ManaType> Types) : Effect
{
    /// <summary>"Add {R} for each …": the mana is added this many times.</summary>
    public Quantity? Times { get; init; }
}

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
public sealed record MayPay(string Prompt, Mana.ManaCost? Mana, ExtraCost? Extra, IReadOnlyList<Effect> Effects) : Effect
{
    /// <summary>"You may sacrifice a Food or pay {2}{W}": the player pays one of these instead.</summary>
    public IReadOnlyList<CostOption>? Options { get; init; }

    /// <summary>"Otherwise, …": what happens when nothing is paid.</summary>
    public IReadOnlyList<Effect>? Else { get; init; }
}

/// <summary>"You may reveal the top card of your library. If it's a [filter] card, [effects]" (the card is "found").</summary>
public sealed record RevealTop(ObjectFilter Filter, bool Optional, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>Several effects that are one event ("put a +1/+1 counter and a lifelink counter on it"): "one or more" triggers see it once.</summary>
public sealed record Simultaneously(IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"Damage can't be prevented this turn."</summary>
public sealed record DamageCantBePreventedThisTurn : Effect;

/// <summary>"Goad target creature" (rule 701.15): it attacks each combat if able, and a player other than the goader if able, until the goader's next turn.</summary>
public sealed record Goad(Subject What) : Effect;

/// <summary>"Gains protection from the card type of your choice until end of turn".</summary>
public sealed record ProtectionFromChosenType(Subject What) : Effect;

/// <summary>"Gain protection from each of that creature's colors until end of turn".</summary>
public sealed record ProtectionFromColorsOf(Subject Of, Subject What) : Effect;

/// <summary>"If its controller has more than four cards in hand, they exile cards from their hand equal to the difference".</summary>
public sealed record ExileHandDownTo(int Keep, Subject Who) : Effect;

/// <summary>"You may pay {X}. When you do, [effects]" — the effects use the X paid.</summary>
public sealed record MayPayX(string Prompt, IReadOnlyList<Effect> Effects) : Effect
{
    /// <summary>"Where X is less than or equal to …".</summary>
    public Quantity? Max { get; init; }
}

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

    /// <summary>"Any number of cards with different mana values".</summary>
    public bool DifferentManaValues { get; init; }

    /// <summary>"From a graveyard" (any player's) instead of yours.</summary>
    public bool AnyGraveyard { get; init; }
}

/// <summary>"[Players] discard their hand."</summary>
public sealed record DiscardHand(Subject Who) : Effect;

/// <summary>"[Players] reveal cards from the top of their library until they reveal a [filter] card, then put those cards into their graveyard."</summary>
public sealed record MillUntil(Subject Who, ObjectFilter Until) : Effect;

/// <summary>Create an emblem for the controller: an object in the command zone with these abilities.</summary>
public sealed record CreateEmblem(string Name, IReadOnlyList<AbilityDefinition> Abilities) : Effect
{
    /// <summary>The emblem lasts only until end of turn (a delayed "whenever … this turn" ability).</summary>
    public bool UntilEndOfTurn { get; init; }
}

/// <summary>Exile the top N cards of your library; you choose one (or all with <paramref name="ChooseOne"/> false) and may play it this turn.</summary>
public sealed record ExileTopPlayable(int Count, bool ChooseOne = true, bool UntilEndOfNextTurn = false, bool WithoutPaying = false) : Effect
{
    /// <summary>"Mana of any type can be spent to cast those spells."</summary>
    public bool AnyManaType { get; init; }

    /// <summary>"When you play a card this way, …": a triggered ability of the source when one of these cards is played.</summary>
    public TriggeredAbility? WhenPlayed { get; init; }

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
    /// <summary>"Put that card onto the battlefield attached to [it]".</summary>
    public Subject? AttachTo { get; init; }

    /// <summary>Until this many matching cards are revealed ("until you reveal X land cards").</summary>
    public Quantity? CountFrom { get; init; }

    /// <summary>The cards found enter tapped.</summary>
    public bool Tapped { get; init; }

    /// <summary>The other revealed cards go to the graveyard (instead of the bottom in a random order).</summary>
    public bool RestToGraveyard { get; init; }

    /// <summary>Whose library ("that player exiles cards from the top of their library"): the controller's by default.</summary>
    public Subject? From { get; init; }

    /// <summary>The cards are exiled, and the one found may be cast without paying its mana cost; the others go to the bottom.</summary>
    public bool CastFree { get; init; }

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
public sealed record Piles(int Count) : Effect
{
    /// <summary>"Reveal the top N cards …": both piles are face up.</summary>
    public bool Revealed { get; init; }

    /// <summary>An opponent separates the piles and the controller chooses one ("they … separate them … Put one pile into your hand").</summary>
    public bool OpponentSeparates { get; init; }
}

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
public sealed record CopySpell(Subject What, Quantity Count) : Effect
{
    /// <summary>"Except the copy isn't legendary".</summary>
    public bool NotLegendary { get; init; }
}

/// <summary>Add one mana of any color (the controller chooses).</summary>
public sealed record AddManaOfAnyColor(int Count = 1) : Effect;

/// <summary>Put cards exiled by this permanent ("with it") into their owners' hands.</summary>
public sealed record ReturnExiledWithThis : Effect
{
    /// <summary>Only this many, chosen ("put a card exiled with this Saga into its owner's hand"); 0: all of them.</summary>
    public int Count { get; init; }
}

/// <summary>Search your library and exile the card, linked to this permanent (for a later "the exiled card").</summary>
public sealed record SearchAndExileWithThis(ObjectFilter Filter) : Effect
{
    /// <summary>Up to this many cards.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>The target card in your graveyard gains flashback (cost: its mana cost) until end of turn.</summary>
public sealed record GrantFlashback(Subject What) : Effect;

/// <summary>"You may cast the target card from your graveyard this turn."</summary>
public sealed record PlayableFromGraveyardThisTurn(Subject What) : Effect;

/// <summary>"You lose the game."</summary>
public sealed record LoseGame : Effect
{
    /// <summary>Who loses: the controller by default ("that player loses the game").</summary>
    public Subject? Who { get; init; }
}

/// <summary>"At the beginning of [that player]'s next end step, …" (a delayed triggered ability, rule 603.7).</summary>
public sealed record AtPlayersNextEndStep(Subject Whose, TriggeredAbility Ability) : Effect;

/// <summary>"Move a counter of each kind not on [to] from [from] onto [to]".</summary>
public sealed record MoveCounterOfEachMissingKind(Subject From, Subject To) : Effect;

/// <summary>"Move one or more counters from [from] onto [to]. If you do, [then]".</summary>
public sealed record MoveChosenCounters(Subject From, Subject To, IReadOnlyList<Effect> Then) : Effect;

/// <summary>"Remove it from combat" (rule 506.4).</summary>
public sealed record RemoveFromCombat(Subject What) : Effect;

/// <summary>"It loses all abilities until end of turn".</summary>
public sealed record LoseAllAbilitiesUntilEndOfTurn(Subject What) : Effect;

/// <summary>"Change the target of target spell or ability with a single target" (to another legal one, chosen by the controller).</summary>
public sealed record ChangeTarget(Subject What) : Effect;

/// <summary>"Counter it unless its controller pays [cost]" for the stack object <paramref name="StackObject"/> (ward).</summary>
public sealed record CounterUnlessPays(int StackObject, Mana.ManaCost Mana, int Life) : Effect
{
    /// <summary>A non-mana cost instead ("Ward—Discard an enchantment, instant, or sorcery card").</summary>
    public ExtraCost? Extra { get; init; }
}

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
public sealed record Blink(Subject What) : Effect
{
    /// <summary>They return tapped.</summary>
    public bool Tapped { get; init; }
}

/// <summary>Its owner shuffles the subject into their library.</summary>
public sealed record ShuffleIntoLibrary(Subject What) : Effect;

/// <summary>"You may play an additional land this turn."</summary>
public sealed record AdditionalLandThisTurn : Effect;

/// <summary>"Players can't cast spells this turn."</summary>
public sealed record NoSpellsThisTurn : Effect;

/// <summary>Exchange control of two permanents.</summary>
public sealed record ExchangeControl(Subject First, Subject Second) : Effect;

/// <summary>A delayed triggered ability: "at the beginning of the next upkeep, …", with an amount worked out now.</summary>
public sealed record AtNextUpkeep(TriggeredAbility Ability, Quantity? Amount = null) : Effect
{
    /// <summary>"At the beginning of your next upkeep" (not the next upkeep of any player).</summary>
    public bool Yours { get; init; }

    /// <summary>The player the delayed ability will call "that player" ("its controller may draw …"), worked out now.</summary>
    public Subject? Player { get; init; }
}

/// <summary>"Choose a creature type": the source remembers it (for "creatures of that type").</summary>
public sealed record ChooseCreatureType : Effect;

/// <summary>Reveal the top N cards, put a random matching one into <paramref name="To"/>, the rest on the bottom in a random order.</summary>
public sealed record RevealTopPutRandom(int Count, ObjectFilter Filter, State.Zone To) : Effect;

/// <summary>Search your hand and/or library for a matching card and put it into <paramref name="To"/>; shuffle.</summary>
public sealed record SearchHandOrLibrary(ObjectFilter Filter, State.Zone To) : Effect;

/// <summary>Add mana in any combination of colors, spendable only on spells matching <paramref name="OnlyFor"/>.</summary>
public sealed record AddManaInAnyCombination(Quantity Count, ObjectFilter? OnlyFor) : Effect;

/// <summary>"You may behold a [filter]" (choose one you control or reveal one from your hand); if you do, <paramref name="Effects"/>.</summary>
public sealed record Behold(ObjectFilter Filter, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"You may cast a [filter] spell from your graveyard"; an instant or sorcery cast this way is exiled instead of going to the graveyard.</summary>
public sealed record CastFromGraveyardNow(ObjectFilter Filter) : Effect
{
    /// <summary>Whose graveyard (default: yours); "that player's graveyard".</summary>
    public Subject? Of { get; init; }

    /// <summary>"Without paying its mana cost"; with <see cref="MaxManaValue"/>: "with mana value X or less".</summary>
    public bool Free { get; init; }
    public Quantity? MaxManaValue { get; init; }

    /// <summary>The card is the target at this index rather than a card chosen now.</summary>
    public Subject? Card { get; init; }

    /// <summary>Only among cards milled by this effect ("from among them").</summary>
    public bool FromMilled { get; init; }
}

/// <summary>Prevent all damage the subject would deal, for as long as the source remains on the battlefield.</summary>
public sealed record PreventDamageBy(Subject What) : Effect;

/// <summary>The subject permanents phase out (rule 702.26): treated as though they don't exist until their controller's next untap step.</summary>
public sealed record PhaseOut(Subject What) : Effect;

/// <summary>"The Ring tempts you" (rule 701.52): the Ring gains its next ability and you choose your Ring-bearer.</summary>
public sealed record RingTemptsYou : Effect;

/// <summary>"You gain protection from everything until your next turn."</summary>
public sealed record PlayerProtection : Effect;

/// <summary>Cascade (rule 702.85) for a spell of the given mana value (the trigger amount).</summary>
public sealed record CascadeEffect : Effect;

/// <summary>"You may cast a [filter] spell with mana value [max] or less from your hand without paying its mana cost."</summary>
public sealed record CastFromHandFree(ObjectFilter Filter, Quantity MaxManaValue) : Effect;

/// <summary>"Put any number of [filter] cards from your hand onto the battlefield."</summary>
public sealed record PutFromHand(ObjectFilter Filter) : Effect
{
    /// <summary>At most one card ("you may put a creature card … onto the battlefield").</summary>
    public bool One { get; init; }

    public bool Tapped { get; init; }

    /// <summary>"Tapped and attacking".</summary>
    public bool Attacking { get; init; }
}

/// <summary>"Choose up to N [filter], then destroy the rest."</summary>
public sealed record DestroyAllButChosen(ObjectFilter Filter, int Keep) : Effect;

/// <summary>"Put a card from your hand on the bottom of your library" (N cards).</summary>
public sealed record HandToBottom(int Count) : Effect;

/// <summary>"Tap any number of untapped [filter] you control" (counted as tapped this way).</summary>
public sealed record TapAnyNumber(ObjectFilter Filter) : Effect;

/// <summary>An opponent chooses: <paramref name="IfYes"/> when they agree to <paramref name="Prompt"/>, otherwise <paramref name="IfNo"/>.</summary>
public sealed record OpponentChooses(string Prompt, IReadOnlyList<Effect> IfYes, IReadOnlyList<Effect> IfNo) : Effect;

/// <summary>Exile the target card, copy it, and you may cast the copy without paying its mana cost.</summary>
public sealed record CastCopyOfExiled(Subject What) : Effect;

/// <summary>"Choose a player with the most life or tied for most life. [Subject] can't be blocked by creatures that player controls this turn."</summary>
public sealed record UnblockableByMostLifePlayer(Subject What) : Effect;

/// <summary>Return to the battlefield (under their owners' control) the cards exiled by this object's linked ability (rule 607).</summary>
public sealed record ReturnLinkedExiled : Effect;

/// <summary>"[Filter] creatures can't block this turn" (a rules effect: creatures that come later are affected too, rule 611.2c).</summary>
public sealed record CantBlockThisTurn(ObjectFilter Filter) : Effect;

/// <summary>"[Player] becomes the monarch" (rule 724): <paramref name="Who"/> is "you", a target player or the controller of the object a trigger was about.</summary>
public sealed record BecomeMonarch(Subject Who) : Effect;

/// <summary>"Exile [it] until an opponent becomes the monarch": the card returns under its owner's control when an opponent of this ability's controller does.</summary>
public sealed record ExileUntilOpponentIsMonarch(Subject What) : Effect;

/// <summary>"[Player] may pay {X}. If they don't, [effects]": X is worked out as the effect happens (the player's hand size …).</summary>
public sealed record PlayerMayPay(Subject Who, Quantity Generic, IReadOnlyList<Effect> IfNot) : Effect;

/// <summary>
/// "Prevent all [combat] damage that would be dealt this turn [by target creature / by matching sources / to you]".
/// </summary>
public sealed record PreventDamageThisTurn(bool CombatOnly, Subject? DealtBy = null, ObjectFilter? Sources = null, bool ToYou = false) : Effect;

/// <summary>"If a source you control would deal damage this turn to an opponent or a permanent an opponent controls, it deals triple that damage instead."</summary>
public sealed record TripleDamageThisTurn : Effect;

/// <summary>Fateful hour: "you can't lose life this turn, you can't lose the game this turn, and your opponents can't win the game this turn".</summary>
public sealed record CantLoseThisTurn : Effect;

/// <summary>"Each player gains control of all [filter] they own."</summary>
public sealed record OwnersGainControl(ObjectFilter Filter) : Effect;

/// <summary>What players vote for (rule 701.38).</summary>
public enum VoteFor { Options, Player, Creature }

/// <summary>
/// "Starting with you, each player votes for …" (rule 701.38); secret council votes are made secretly, then revealed. The votes
/// are kept for the effects after it (conditions and quantities about votes) and trigger "whenever players finish voting".
/// </summary>
public sealed record Vote(IReadOnlyList<string> Options, bool Secret = false, VoteFor For = VoteFor.Options, ObjectFilter? CreatureFilter = null) : Effect;

/// <summary>"Each player [who …] [does something]": the effects are carried out by each such player in turn order, as "you".</summary>
public sealed record ForEachPlayer(Subject Who, Condition? If, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"For each [thing], [do this]": the effects are carried out that many times, one after another.</summary>
public sealed record Repeat(Quantity Times, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"[It] becomes renowned" (renown, rule 702.112).</summary>
public sealed record BecomeRenowned(Subject What) : Effect;

/// <summary>"[It] gains protection from each of your opponents until end of turn".</summary>
public sealed record ProtectionFromOpponents(Subject What) : Effect;

/// <summary>"Look at the top card of each opponent's library and exile those cards face down" (linked to this permanent; its controller may look at them).</summary>
public sealed record ExileTopFaceDown(Subject Whose) : Effect;

/// <summary>"Until end of turn, you may play a card exiled with [this] without paying its mana cost" (one of them).</summary>
public sealed record PlayOneExiledWithThisFree : Effect;

/// <summary>"Put those counters on [this]": the counters the object a trigger is about had as it left the battlefield.</summary>
public sealed record TakeCountersOfTriggered : Effect;

/// <summary>"Move all counters from [this] onto [target]".</summary>
public sealed record MoveAllCounters(Subject From, Subject To) : Effect;

/// <summary>"You may return another permanent you control that shares a permanent type with it to its owner's hand."</summary>
public sealed record MayBounceAnotherSharingType : Effect;

/// <summary>"Copy that ability. You may choose new targets for the copy." (the ability a trigger is about, by stack object)</summary>
public sealed record CopyTriggeredAbility : Effect;

/// <summary>"Each player exiles all [filter] cards from their graveyard, then sacrifices all [filter] permanents they control, then puts all cards they exiled this way onto the battlefield."</summary>
public sealed record SwapGraveyardAndBattlefield(ObjectFilter Filter) : Effect;

/// <summary>Tempting offer (search): you search for a [filter] card onto the battlefield; each opponent may too; you search again for each who did.</summary>
public sealed record TemptingOfferSearch(ObjectFilter Filter) : Effect;

/// <summary>"For each [filter] you control, create a token that's a copy of that permanent."</summary>
public sealed record CopyEachYouControl(ObjectFilter Filter) : Effect;

/// <summary>"Each opponent exiles a creature with the greatest power among creatures that player controls"; then, if the condition holds, the source deals each opponent damage equal to the power of the creature they exiled.</summary>
public sealed record OpponentsExileGreatestPower(Condition? DamageIf) : Effect;

/// <summary>"Each opponent reveals the top card of their library. If any of those cards shares a card type with that spell, copy that spell … and each opponent draws a card. Otherwise, you draw a card."</summary>
public sealed record CopyIfOpponentsTopSharesType : Effect;

/// <summary>"Exile that card with N time counters on it instead of putting it into your graveyard as it resolves. Then if the exiled card doesn't have suspend, it gains suspend."</summary>
public sealed record SuspendWhenResolves(int TimeCounters) : Effect;

/// <summary>Suspend's upkeep ability: remove a time counter from this suspended card; when the last is removed, it may be played free.</summary>
public sealed record SuspendTick : Effect;

/// <summary>Suspend: "you may play it without paying its mana cost if able" (a creature spell cast this way gains haste).</summary>
public sealed record PlaySuspendedFree : Effect;

/// <summary>"Destroy all creatures with power greater than target creature's power."</summary>
public sealed record DestroyPowerAbove(Subject What) : Effect;

/// <summary>"Exile a [filter] card from your graveyard" (chosen as this happens): it is "the exiled card" for the effects after it.</summary>
public sealed record ExileChosenFromGraveyard(ObjectFilter Filter) : Effect;

/// <summary>"Return all [filter] cards from your graveyard to your hand."</summary>
public sealed record ReturnAllFromGraveyardToHand(ObjectFilter Filter) : Effect;

/// <summary>"Choose an opponent": the chosen player is "that player" for the effects after it.</summary>
public sealed record ChooseAnOpponent : Effect;

/// <summary>"[Chooser] chooses a [filter]": the chosen objects are "the chosen [objects]" for the effects after it.</summary>
public sealed record ChooseObjects(Subject Chooser, ObjectFilter Filter, bool Optional = false) : Effect;

/// <summary>"Return target nonland permanent and each other nonland permanent with the same mana value as that permanent to their owners' hands."</summary>
public sealed record BounceSameManaValue(Subject What) : Effect;

/// <summary>"At the beginning of the next end step, [effects]" about the object of <paramref name="About"/> ("return that card").</summary>
public sealed record AtNextEndStepAbout(Subject About, IReadOnlyList<Effect> Effects) : Effect;

/// <summary>"[Player] may draw up to N cards".</summary>
public sealed record DrawUpTo(int Max, Subject Who) : Effect;

/// <summary>Hideaway N (702.75): look at the top N cards, exile one face down (linked to this permanent), the rest on the bottom in a random order.</summary>
public sealed record Hideaway(int Count) : Effect;

/// <summary>"You may play the exiled card without paying its mana cost" (the card exiled with this permanent).</summary>
public sealed record PlayLinkedExiledFree : Effect;

/// <summary>Populate (701.30): create a token that's a copy of a creature token you control.</summary>
public sealed record Populate : Effect;

/// <summary>"Regenerate [it]": the next time it would be destroyed this turn, instead tap it, remove all damage from it and remove it from combat (701.19).</summary>
public sealed record Regenerate(Subject What) : Effect;

/// <summary>Miracle (702.94): "you may cast it by paying [its miracle cost]" (the card the trigger is about, still in hand).</summary>
public sealed record CastForMiracle : Effect;

/// <summary>"Put [this spell] on the bottom of its owner's library" as it resolves.</summary>
public sealed record ThisSpellToLibraryBottom : Effect;

/// <summary>"Then exile [this spell]": the resolving spell goes to exile instead of its owner's graveyard.</summary>
public sealed record ExileThisSpell : Effect;

/// <summary>"For each creature with one or more votes, put that many stun counters on it, then tap it."</summary>
public sealed record StunVotedCreatures : Effect;

/// <summary>"For each [option] vote, the voter chooses a creature they control. You gain control of each creature chosen this way, and they gain 'This creature can't attack its owner.'"</summary>
public sealed record VotersGiveCreatures(int Option) : Effect;

/// <summary>"You can't attack that player this turn".</summary>
public sealed record CantAttackPlayerThisTurn(Subject Player) : Effect;

/// <summary>"You can't sacrifice those creatures this turn".</summary>
public sealed record CantSacrificeThisTurn(Subject What) : Effect;

/// <summary>"[Player] can't attack you this combat".</summary>
public sealed record CantAttackYouThisCombat(Subject Who) : Effect;
