// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>Effects of the academy set, applied by <c>ApplyAcademyAsync</c> (<c>Game.Academy.cs</c>).</summary>
public abstract record AcademyEffect : Effect;

/// <summary>
/// "At the beginning of your next main phase, [effects]": a delayed ability that triggers as the controller's next main phase begins
/// (precombat or postcombat, whichever comes first). <paramref name="Amount"/> is worked out now and read as "triggerAmount".
/// </summary>
public sealed record AtYourNextMainPhase(TriggeredAbility Ability, Quantity? Amount = null) : AcademyEffect;

/// <summary>
/// "Return enchanted creature card to the battlefield under your control and attach this Aura to it" (the Aura then enchants only that creature);
/// with <paramref name="Sacrifice"/>: "when this Aura leaves the battlefield, that creature's controller sacrifices it".
/// </summary>
public sealed record AnimateEnchantedCard(bool Sacrifice = false) : AcademyEffect;

/// <summary>
/// "You and target opponent each reveal the top card of your library. You each lose life equal to the mana value of the card revealed by the
/// other player. You each put the card you revealed into your hand."
/// </summary>
public sealed record RevealTopDuel(Subject Opponent) : AcademyEffect;

/// <summary>"When this token leaves the battlefield, return the exiled card to its owner's graveyard" (the card this token copied).</summary>
public sealed record ReturnCopiedCardToGraveyard : AcademyEffect;

/// <summary>
/// "You may cast that card this turn without paying its mana cost. If that spell would be put into a graveyard, put it on the bottom of its
/// owner's library instead" (an exiled card).
/// </summary>
public sealed record CastExiledFreeThisTurnBottomInstead(Subject What) : AcademyEffect;

/// <summary>
/// "That player copies that spell for each other creature they control that the spell could target. Each copy targets a different one of
/// those creatures" (the spell the trigger is about, which targets only this creature).
/// </summary>
public sealed record CopyForEachOtherCreatureItCouldTarget : AcademyEffect;

/// <summary>
/// "Exile the top N cards of your library in a face-down pile, then exile the top N cards of your library in a face-up pile. An opponent
/// chooses one of those piles. Put that pile into your graveyard. Look at the cards in the other pile. You may cast a spell from among them
/// without paying its mana cost. Put the rest into your hand."
/// </summary>
public sealed record ExiledPilesOpponentChooses(int Count) : AcademyEffect;

/// <summary>
/// "As many times as you choose, you may exile the top card of your library. If the total mana value of the cards exiled this way is N or
/// less, you may cast any number of spells from among those cards without paying their mana costs."
/// </summary>
public sealed record ExileTopAsYouChooseCastIfTotalAtMost(int MaxTotal) : AcademyEffect;

/// <summary>
/// "Each player exiles cards from the top of their library until they exile a nonland card. An opponent chooses a nonland card exiled this
/// way. You may cast up to N spells from among the other cards exiled this way without paying their mana costs."
/// </summary>
public sealed record EachExilesUntilNonlandOpponentPicks(int Casts) : AcademyEffect;

/// <summary>"Double the value of X" of the spell the trigger is about, while it is on the stack.</summary>
public sealed record DoubleTriggeredSpellX : AcademyEffect;

/// <summary>"Whenever that creature deals combat damage to a player this combat, [effects]": a delayed ability about each creature of <paramref name="About"/>.</summary>
public sealed record WhenDealsCombatDamageThisCombat(Subject About, TriggeredAbility Ability) : AcademyEffect;

/// <summary>"Whenever a creature you control deals combat damage to a player this turn, [effects]": the ability is about the first object of <paramref name="About"/> ("copy the exiled card").</summary>
public sealed record WhenYourCreatureDealsCombatDamageThisTurn(Subject About, TriggeredAbility Ability) : AcademyEffect;

/// <summary>"Choose a land card name" (as an effect, e.g. of an enters trigger): the source remembers it, as with "as this enters, choose".</summary>
public sealed record ChooseLandCardName : AcademyEffect;

/// <summary>"Flip five coins. Target opponent skips their next X turns, where X is the number of coins that came up heads."</summary>
public sealed record FlipCoinsSkipTurns(int Coins, Subject Who) : AcademyEffect;

/// <summary>
/// "Move X +1/+1 counters from this creature onto another target creature": up to <paramref name="Count"/> counters of
/// <paramref name="Kind"/> (as many as <paramref name="From"/> has), if both are still on the battlefield.
/// </summary>
public sealed record MoveCountersOfKind(Subject From, Subject To, Quantity Count, CounterKind Kind = CounterKind.PlusOnePlusOne) : AcademyEffect;

/// <summary>"You may cast that card without paying its mana cost": up to <paramref name="Most"/> of these exiled cards, cast while this resolves.</summary>
public sealed record CastFreeNow(Subject What, int Most = 1) : AcademyEffect;

/// <summary>
/// "Reveal the top card of your library and put that card into your hand. You lose life equal to its mana value. You may repeat this
/// process any number of times."
/// </summary>
public sealed record RevealToHandLoseLifeRepeat : AcademyEffect;

/// <summary>"You can't lose the game this turn and your opponents can't win the game this turn."</summary>
public sealed record CantLoseGameThisTurn : AcademyEffect;

/// <summary>"Until end of turn, damage that would reduce your life total to less than 1 reduces it to 1 instead."</summary>
public sealed record DamageCantReduceLifeBelowOneThisTurn : AcademyEffect;

/// <summary>"Spells you control can't be countered this turn."</summary>
public sealed record YourSpellsCantBeCounteredThisTurn : AcademyEffect;

/// <summary>"You and permanents you control gain hexproof from [colors] until end of turn" (the permanents controlled now).</summary>
public sealed record HexproofFromColorsThisTurn(IReadOnlyList<string> Colors) : AcademyEffect;

/// <summary>
/// "For each creature exiled this way, its controller searches their library for a basic land card. Those players put those cards onto the
/// battlefield tapped, then shuffle."
/// </summary>
public sealed record ControllersSearchBasicPerExiled : AcademyEffect;

/// <summary>"When you next cast a [matching] spell this turn, [ability]": a delayed ability (the spell's mana value is its trigger amount).</summary>
public sealed record WhenYouNextCastThisTurn(ObjectFilter Spells, TriggeredAbility Ability) : AcademyEffect;

/// <summary>
/// "You may draw two additional cards. If you do, choose two cards in your hand drawn this turn. For each of those cards, pay 4 life or put
/// the card on top of your library."
/// </summary>
public sealed record DrawTwoPayOrReturnDrawn : AcademyEffect;

/// <summary>"Look at target opponent's hand. You may exile a [filter] card from it until this creature leaves the battlefield."</summary>
public sealed record ExileFromHandUntilLeaves(Subject Whose, ObjectFilter Filter) : AcademyEffect;

/// <summary>Warp's delayed exile (702.185a): exile the warped permanent; its owner may cast the card from exile on a later turn.</summary>
public sealed record WarpExile : AcademyEffect;

/// <summary>"Solve this Case" (rule 719.3b): it becomes solved.</summary>
public sealed record SolveCase : AcademyEffect;
