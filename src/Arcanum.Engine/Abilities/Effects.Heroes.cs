// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Abilities;

/// <summary>
/// Effects carried out in <c>Game.Heroes.cs</c>, first needed by a set of hero, summon and job cards: searching a library and/or
/// graveyard, handing a permanent's counters and Equipment over as it leaves, and more.
/// </summary>
public abstract record HeroesEffect : Effect;

/// <summary>
/// "Search your library and/or graveyard for a [filter] card, reveal it, and put it into your hand. If you search your library this
/// way, shuffle." The player chooses whether to search the library; a matching card in the graveyard (a public zone) must be found.
/// </summary>
public sealed record SearchLibraryOrGraveyard(ObjectFilter Filter) : HeroesEffect;

/// <summary>
/// "Flip a coin. If you win the flip, [effects]" (rule 705): the flipping player wins half the time, or always for their first flip
/// each turn while they control a permanent saying so (<see cref="Cards.Replacements.FirstCoinFlipEachTurnWins"/>).
/// </summary>
public sealed record FlipCoin(IReadOnlyList<Effect> Won, IReadOnlyList<Effect> Lost) : HeroesEffect;

/// <summary>
/// "Copy target activated or triggered ability you control X times. You may choose new targets for the copies" (rule 707.10): each copy
/// has the original's X, modes and what its trigger was about. An ability that "can't be copied" isn't.
/// </summary>
public sealed record CopyAbility(Subject What, Quantity Count) : HeroesEffect;

/// <summary>"There is an additional end step after this step" (rule 500.8), with <see cref="OnlyIfFirst"/>: "if it's the first end step of the turn".</summary>
public sealed record AdditionalEndStep(bool OnlyIfFirst) : HeroesEffect;

/// <summary>
/// "When you lose control of [it] this turn, if it's attached to a creature you control, unattach it": a delayed triggered ability
/// (rule 603.7) watching that object.
/// </summary>
public sealed record UnattachWhenControlLost(Subject What) : HeroesEffect;

/// <summary>Unattach it if it's attached to a creature its ability's controller controls (the delayed ability above, on resolution).</summary>
public sealed record UnattachIfAttachedToYours(Subject What) : HeroesEffect;

/// <summary>
/// "Reveal the top N cards of your library. Put one of them into your hand. Then choose an opponent. They put one on the bottom of your
/// library. Then you put one into your hand. … Put the other into your hand": you and the chosen opponent alternate, you first; the
/// last card goes to your hand.
/// </summary>
public sealed record RevealAndAlternate(int Count) : HeroesEffect;

/// <summary>The source remembers the creature and/or player chosen earlier in the effect ("choose a creature an opponent controls", "choose an opponent").</summary>
public sealed record RememberChosen : HeroesEffect;

/// <summary>"[Player] exiles the top N cards of their library" (not playable; the cards are "exiled this way").</summary>
public sealed record ExileTop(Quantity Count, Subject Who) : HeroesEffect
{
    /// <summary>"During any turn you put a lore counter on this Saga, you may play that card."</summary>
    public bool PlayableDuringLoreTurns { get; init; }
}

/// <summary>
/// "When you next cast a creature spell this turn, it gains haste until end of turn" / "that creature enters with an additional +1/+1
/// counter on it": applied to the next creature spell its controller casts this turn.
/// </summary>
public sealed record NextCreatureSpell(bool Haste, int Counters) : HeroesEffect
{
    /// <summary>Only the next creature spell of this creature type ("when you next cast a Bird creature spell this turn"): one additional +1/+1 counter.</summary>
    public string? Type { get; init; }
}

/// <summary>
/// "Each player exiles the top card of their library. Until end of turn, you may play the card you own exiled this way and each other card
/// exiled this way with lesser mana value than it without paying their mana costs."
/// </summary>
public sealed record EachExilesTopPlayLesser : HeroesEffect;

/// <summary>"Unattach it at the beginning of the next end step" (for the permanents this effect attached).</summary>
public sealed record UnattachAtNextEndStep : HeroesEffect;

/// <summary>"Until your next turn, if a source would deal damage to that player or a permanent that player controls, it deals double that damage instead."</summary>
public sealed record DoubleDamageToPlayerUntilYourNextTurn(Subject Who) : HeroesEffect;

/// <summary>"Each other Bird you control deals damage equal to its power to [it]": all at once, one damage event.</summary>
public sealed record EachDealsDamageEqualToPower(Subject Sources, Subject To) : HeroesEffect;

/// <summary>"Double [its] power until end of turn" / "triple [its] power and toughness": +X/+0 or +2X/+2Y (rule 701.10d-e).</summary>
public sealed record MultiplyPower(Subject What, int Factor, bool Toughness) : HeroesEffect;

/// <summary>"That creature enters with an additional +1/+1 counter on it for each …": the spell the trigger is about.</summary>
public sealed record EntersWithAdditionalCounters(Subject What, Quantity Count) : HeroesEffect;

/// <summary>"Exile [this spell], then put it onto the battlefield transformed under its owner's control [with a finality counter on it]."</summary>
public sealed record ThisSpellOntoBattlefieldTransformed(CounterKind? WithCounter) : HeroesEffect;

/// <summary>Unattach it (the delayed part of <see cref="UnattachAtNextEndStep"/>).</summary>
public sealed record Unattach(Subject What) : HeroesEffect;

/// <summary>"You may cast it [the exiled card]. If you don't, [else]": cast now while this resolves, paying its costs (rule 608.2g).</summary>
public sealed record CastExiledNow(Subject What, IReadOnlyList<Effect> Else) : HeroesEffect;

/// <summary>"You may play [the exiled card] this turn[, and mana of any type can be spent to cast it]."</summary>
public sealed record PlayableThisTurn(Subject What, bool AnyMana) : HeroesEffect;

/// <summary>"Remove a [kind] counter from each of any number of [filter] you control": the counters removed are counted ("removed this way").</summary>
public sealed record RemoveCounterFromAnyNumber(ObjectFilter Filter, CounterKind Kind) : HeroesEffect;

/// <summary>
/// "Exile a permanent card from your graveyard at random, then create a tapped token that's a copy of that card. If the exiled card is a land
/// card, repeat this process."
/// </summary>
public sealed record ExileRandomPermanentCardCopyTapped : HeroesEffect;

/// <summary>"Put up to N [kind] counters on it": the controller chooses how many.</summary>
public sealed record CountersUpTo(int Max, CounterKind Kind, Subject What) : HeroesEffect;

/// <summary>"Attach any number of [filter] you control to [it]".</summary>
public sealed record AttachAnyNumber(ObjectFilter Filter, Subject To) : HeroesEffect;

/// <summary>"Shuffle all [filter] cards from your graveyard into your library" (the cards shuffled are "returned this way").</summary>
public sealed record ShuffleFromGraveyard(ObjectFilter Filter) : HeroesEffect;

/// <summary>
/// "Look at that many cards from the top of your library. You may put one of them into your hand. Then put any number of land cards from
/// among them onto the battlefield tapped and the rest into your graveyard."
/// </summary>
public sealed record LookHandLandsGraveyard(Quantity Count) : HeroesEffect;

/// <summary>
/// "If you both own and control [this] and a creature named [partner], … exile them, then meld them into [result]" (rule 701.42): both are
/// exiled; if both are, they return as one melded permanent with the combined back face (rule 712.4).
/// </summary>
public sealed record Meld : HeroesEffect;

/// <summary>Ninjutsu's effect: put this card onto the battlefield from your hand (or the command zone) tapped and attacking what the returned creature attacked.</summary>
public sealed record NinjutsuEnter : HeroesEffect;

/// <summary>"Draw … then put N cards from your hand on top of your library in any order."</summary>
public sealed record HandToTop(int Count) : HeroesEffect;

/// <summary>"Put your commander into your hand from the command zone."</summary>
public sealed record CommanderToHand : HeroesEffect;

/// <summary>
/// "Choose an opponent at random. [This] deals damage equal to [amount] to that player or a planeswalker that player controls" (the source's
/// controller chooses which).
/// </summary>
public sealed record DamageRandomOpponent(Quantity Amount) : HeroesEffect;

/// <summary>"You may add X mana of any one color."</summary>
public sealed record AddManaOfOneColor(Quantity Count, bool Optional) : HeroesEffect;

/// <summary>"Roll a d20" (rule 706): the result is "rolled".</summary>
public sealed record RollDie(int Sides) : HeroesEffect;

/// <summary>
/// "Exile cards from the top of your library until you exile a [filter] card with lesser mana value [than the triggering spell]. You may cast that
/// card without paying its mana cost. Put the rest on the bottom of your library in a random order."
/// </summary>
public sealed record ExileUntilLesserCastFree(ObjectFilter Filter) : HeroesEffect;

/// <summary>"Reveal the top N cards. For each card type, you may put a card of that type from among them into your hand. The rest on the bottom in a random order."</summary>
public sealed record RevealOnePerCardType(int Count) : HeroesEffect;

/// <summary>"Put a counter on it / remove a counter" bookkeeping for a mana ability: see <see cref="Cards.ManaOption"/>.</summary>
public sealed record MarkDoneThisTurn : HeroesEffect;

/// <summary>"Put [this permanent]'s counters on [it]": every counter the source had as it last existed on the battlefield (rule 608.2h).</summary>
public sealed record PutSourceCountersOn(Subject To) : HeroesEffect;

/// <summary>"Attach an Equipment that was attached to [this permanent] to [it]": one that became unattached as the source left the battlefield.</summary>
public sealed record AttachFormerlyAttached(Subject To, ObjectFilter? Filter) : HeroesEffect;
