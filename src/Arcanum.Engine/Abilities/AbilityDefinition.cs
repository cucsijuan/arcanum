// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Abilities;

/// <summary>A spell's effect or one of a permanent's abilities: what it targets and what it does.</summary>
public abstract record AbilityDefinition
{
    public IReadOnlyList<TargetSpec> Targets { get; init; } = Array.Empty<TargetSpec>();
    public IReadOnlyList<Effect> Effects { get; init; } = Array.Empty<Effect>();

    /// <summary>Rules text shown to players (e.g. on the stack).</summary>
    public string Text { get; init; } = "";

    /// <summary>For an ability granted by another permanent ("Equipped creature has ..."): that permanent.</summary>
    public Core.CardId? GrantedBy { get; init; }

    /// <summary>Eminence: a triggered ability that works from the command zone too.</summary>
    public bool FromCommandZone { get; init; }

    /// <summary>"This ability can't be copied."</summary>
    public bool CantBeCopied { get; init; }

    /// <summary>
    /// A Class's level section (rule 716.2c): the ability exists only while the Class is at this level or higher. 0 or 1:
    /// always (the first section, and the "[cost]: Level N" abilities themselves).
    /// </summary>
    public int ClassLevel { get; init; }

    /// <summary>
    /// A Class's abilities with each level section after the lower ones (stable otherwise), so the abilities it has at any
    /// level are a prefix of its printed list and activation indices mean the same in both.
    /// </summary>
    public static IReadOnlyList<AbilityDefinition> InClassLevelOrder(IReadOnlyList<AbilityDefinition> abilities) =>
        abilities.Any(a => a.ClassLevel > 1) ? abilities.OrderBy(a => Math.Max(1, a.ClassLevel)).ToList() : abilities;

    /// <summary>
    /// For a triggered ability, an intervening "if" clause (rule 603.4): it triggers only if the condition holds,
    /// and does nothing on resolution unless it still holds. Null: unconditional.
    /// </summary>
    public Condition? Condition { get; init; }

    /// <summary>
    /// For a modal spell or ability ("Choose one —"): the modes. The controller picks <see cref="ModeCount"/>
    /// of them (or between 1 and that many when <see cref="UpToModes"/>) as it is cast, activated or put on the
    /// stack; <see cref="Targets"/> and <see cref="Effects"/> are then the chosen modes' ones, in order.
    /// </summary>
    public IReadOnlyList<Mode>? Modes { get; init; }

    public int ModeCount { get; init; } = 1;

    /// <summary>"Choose one or more" / "choose one or both": fewer modes than <see cref="ModeCount"/> may be picked.</summary>
    public bool UpToModes { get; init; }

    /// <summary>"You may choose two": exactly <see cref="ModeCount"/> modes or none, and with none the ability does nothing (603.3c).</summary>
    public bool MayChooseModes { get; init; }

    /// <summary>"Choose one that hasn't been chosen": each mode can be chosen only once for this object.</summary>
    public bool ModesOncePerObject { get; init; }

    /// <summary>"Choose one that hasn't been chosen this turn".</summary>
    public bool ModesOncePerTurn { get; init; }

    /// <summary>"You may choose the same mode more than once."</summary>
    public bool ModesMayRepeat { get; init; }

    /// <summary>"Choose up to five {P} worth of modes": the pawprints the chosen modes may add up to; 0 for other modal abilities.</summary>
    public int PawprintBudget { get; init; }

    /// <summary>"Choose one at random": the mode is picked at random among those that can be chosen (rule 700.2).</summary>
    public bool RandomMode { get; init; }

    /// <summary>One more mode may be chosen if this holds as it is cast ("if you control a Wizard, you may choose two instead").</summary>
    public Condition? ExtraModeIf { get; init; }

    /// <summary>A rule tying the targets together.</summary>
    public TargetRule TargetRule { get; init; }

    /// <summary>"Any number of target creature cards with total mana value 6 or less": the chosen targets' total mana value is at most this.</summary>
    public int? TargetsTotalManaValueAtMost { get; init; }

    /// <summary>"Any number of target creature cards with total power 10 or less".</summary>
    public int? TargetsTotalPowerAtMost { get; init; }

    /// <summary>What this spell does when kicked, when that changes its targets ("instead any number of target creatures").</summary>
    public AbilityDefinition? WhenKicked { get; init; }

    /// <summary>The spell as it is when its optional additional cost was paid ("If this spell's additional cost was paid, choose both instead").</summary>
    public AbilityDefinition? WhenAdditionalCostPaid { get; init; }
}

/// <summary>Constraints between the targets of one spell or ability.</summary>
public enum TargetRule
{
    None,
    /// <summary>Every target is a different object or player ("each of up to two other targets").</summary>
    AllDifferent,
    /// <summary>The targets are controlled by different players.</summary>
    DifferentControllers,
    /// <summary>Every target card is in the same graveyard ("from a single graveyard").</summary>
    SameGraveyard,
    /// <summary>The targets share a card type ("two target nonland permanents that share a card type").</summary>
    ShareCardType,
    /// <summary>Different objects controlled by the same player ("another target creature that player controls").</summary>
    SameController,
    /// <summary>Different objects that share a creature type ("two target creature cards that share a creature type").</summary>
    ShareCreatureType,
}

/// <summary>One mode of a modal spell or ability.</summary>
public sealed record Mode(string Text, IReadOnlyList<TargetSpec> Targets, IReadOnlyList<Effect> Effects)
{
    /// <summary>Tiered (rule 702.183): the additional cost paid for casting the spell with this mode chosen.</summary>
    public Mana.ManaCost? Cost { get; init; }

    /// <summary>The {P} (pawprint) symbols before this mode: what it uses of the spell's pawprint budget.</summary>
    public int Pawprints { get; init; }

    /// <summary>A rule between this mode's own targets ("two target creature cards that share a creature type").</summary>
    public TargetRule TargetRule { get; init; }
}

/// <summary>What an instant or sorcery does when it resolves.</summary>
public sealed record SpellAbility : AbilityDefinition
{
    /// <summary>"Exile [this spell]" as its last instruction: it goes to exile instead of the graveyard.</summary>
    public bool ExileAfterResolving { get; init; }
}

/// <summary>Costs of an activated ability, e.g. "{1}{R}, {T}, Sacrifice this".</summary>
public sealed record AbilityCost(ManaCost Mana, bool Tap = false, bool SacrificeSelf = false)
{
    /// <summary>Other costs: discard, sacrifice another permanent, pay life.</summary>
    public ExtraCost? Extra { get; init; }

    /// <summary>"Exile this card from your graveyard" / "Return this card from your graveyard": the ability works from the graveyard.</summary>
    public bool FromGraveyard { get; init; }

    /// <summary>Remove this many counters (of <see cref="RemoveCounterKind"/>) from the source.</summary>
    public int RemoveCounters { get; init; }

    public CounterKind RemoveCounterKind { get; init; } = CounterKind.PlusOnePlusOne;

    /// <summary>"Exile this [permanent]" as a cost.</summary>
    public bool ExileSelf { get; init; }

    /// <summary>"Discard X cards" as a cost: X is announced (rule 601.2b) and that many cards are discarded.</summary>
    public bool DiscardX { get; init; }

    /// <summary>"Pay X life": X as announced for the ability's other costs (rule 601.2b via 602.2b).</summary>
    public bool PayLifeX { get; init; }

    /// <summary>"Exile X cards from your graveyard": X as announced.</summary>
    public bool ExileGraveyardX { get; init; }

    /// <summary>"Sacrifice X Squirrels" as a cost: X is announced and that many matching permanents (<see cref="Extra"/>'s filter) are sacrificed.</summary>
    public bool SacrificeX { get; init; }

    /// <summary>"Tap X untapped tokens you control" as a cost: X is announced and that many matching permanents (<see cref="Extra"/>'s tap filter) are tapped.</summary>
    public bool TapX { get; init; }

    /// <summary>Ninjutsu (rule 702.49): activated from the hand (or, for commander ninjutsu, the command zone) by returning an unblocked attacker you control to its owner's hand.</summary>
    public bool Ninjutsu { get; init; }

    /// <summary>A loyalty ability's cost: +N / −N loyalty counters (null: not a loyalty ability, rule 606).</summary>
    public int? Loyalty { get; init; }

    /// <summary>A loyalty cost of −X ("−X: … X damage"): X is chosen as it's activated, at most the loyalty it has.</summary>
    public bool LoyaltyX { get; init; }

    /// <summary>"Put a [kind] counter on this" as a cost.</summary>
    public int AddCounters { get; init; }
    public CounterKind AddCounterKind { get; init; } = CounterKind.PlusOnePlusOne;

    /// <summary>"Return this [permanent] to its owner's hand" as a cost.</summary>
    public bool ReturnSelfToHand { get; init; }

    /// <summary>"Tap [the permanent that granted this ability]" as a cost ("Tap this equipment").</summary>
    public bool TapGranter { get; init; }

    /// <summary>Activated from the hand by discarding this card (cycling, rule 702.29).</summary>
    public bool FromHand { get; init; }

    /// <summary>With <see cref="FromHand"/>: exiling this card from the hand face down is part of the cost instead (foretell, rule 702.143a).</summary>
    public bool ExileFaceDown { get; init; }

    /// <summary>With <see cref="FromHand"/>: "Exile this card from your hand" is the cost (face up), instead of discarding it.</summary>
    public bool ExileFromHand { get; init; }

    /// <summary>{S} symbols: each paid with one mana from a snow source (rule 107.4h).</summary>
    public int SnowMana { get; init; }

    public static readonly AbilityCost TapOnly = new(ManaCost.Zero, Tap: true);
}

/// <summary>"[Cost]: [Effect]." (rule 602).</summary>
public sealed record ActivatedAbility : AbilityDefinition
{
    public required AbilityCost Cost { get; init; }

    /// <summary>"For each mana in this ability's activation cost, you may tap an untapped creature you control rather than pay that mana."</summary>
    public bool TapCreaturesForMana { get; init; }

    /// <summary>"X can't be 0": the smallest X that can be announced.</summary>
    public int MinX { get; init; }

    /// <summary>"Activate only as a sorcery."</summary>
    public bool SorcerySpeed { get; init; }

    /// <summary>A special action (rule 116), not an activated ability: done at once when the costs are paid, without using the stack.</summary>
    public bool SpecialAction { get; init; }

    /// <summary>Only when its card could begin to be cast from where it is (suspend, rule 702.62a): the card's own timing, flash included.</summary>
    public bool TimingAsCast { get; init; }

    /// <summary>"Activate only once each turn."</summary>
    public bool OncePerTurn { get; init; }

    /// <summary>"Activate only once" (for as long as the permanent stays on the battlefield).</summary>
    public bool OnlyOnce { get; init; }

    /// <summary>"Only your opponents may activate this ability": its controller's opponents may, its controller may not (rule 602.1b).</summary>
    public bool OnlyOpponents { get; init; }

    /// <summary>"Activate only if [condition]."</summary>
    public Condition? ActivationCondition { get; init; }

    /// <summary>An equip ability (rule 702.6): affected by "equip abilities you activate cost less" effects.</summary>
    public bool IsEquip { get; init; }

    /// <summary>"This ability costs {1} less to activate for each [filter] you control."</summary>
    public ObjectFilter? CostReductionPer { get; init; }

    /// <summary>"This ability costs {1} less to activate for each +1/+1 counter on the creature it targets."</summary>
    public bool CostReductionPerTargetCounter { get; init; }

    /// <summary>"This ability costs {1} less to activate for each color of the creature it targets" (before targets are chosen, the best legal one).</summary>
    public bool CostReductionPerTargetColor { get; init; }

    /// <summary>
    /// "Sacrifice a creature you control with mana value X: return target creature card with mana value X plus 1": X is announced first (rule 601.2b),
    /// then the sacrifice must have mana value X and the target mana value X plus <see cref="TargetManaValueOffset"/>.
    /// </summary>
    public bool XFromSacrificedManaValue { get; init; }
    public int TargetManaValueOffset { get; init; }

    /// <summary>"This ability costs {N} less to activate if [condition]."</summary>
    public Condition? CostReductionIf { get; init; }
    public int CostReductionAmount { get; init; }
}

/// <summary>"[Spells matching the filter] you cast cost {N} less to cast" while the source is on the battlefield.</summary>
/// <summary>"Activated abilities of Foods you control cost {1} less to activate" / "Equip abilities you activate cost {1} less" (generic mana only).</summary>
public sealed record AbilityCostReduction(ObjectFilter Sources, int Amount, bool EquipOnly) : AbilityDefinition
{
    /// <summary>"Equip abilities you activate that target [this] cost {2} less": only when the ability targets the reducer itself.</summary>
    public bool TargetingSelf { get; init; }

    /// <summary>
    /// "The first activated ability you activate during your turn that targets a creature you control costs {N} less": it must target a creature its
    /// activator controls, on their turn, and be the first such activation that turn.
    /// </summary>
    public bool FirstTargetingYourCreatureOnYourTurn { get; init; }
}

/// <summary>"[Spells matching the filter] cost {N} more to cast" (every player's, while the source is on the battlefield).</summary>
public sealed record SpellCostIncrease(ObjectFilter Spells, int Amount) : AbilityDefinition;

public sealed record SpellCostReduction(ObjectFilter Spells, int Amount) : AbilityDefinition
{
    /// <summary>The amount worked out from the source ("{X} less, where X is equipped creature's power").</summary>
    public Quantity? AmountFrom { get; init; }

    /// <summary>Only the first such spell each turn ("the first creature spell you cast each turn").</summary>
    public bool FirstOfTurn { get; init; }

    /// <summary>Only the Nth spell its controller casts each turn ("the second spell you cast each turn costs {2} less"), counting every spell.</summary>
    public int? NthOfTurn { get; init; }

    /// <summary>Those spells can also be cast as though they had flash.</summary>
    public bool GrantsFlash { get; init; }

    /// <summary>Only while this holds for its controller ("during your turn, spells you cast cost …").</summary>
    public Condition? While { get; init; }

    /// <summary>"Costs {U} less": the amount is of this color's mana, not generic (rule 118.7c).</summary>
    public Mana.ManaType? Color { get; init; }

    /// <summary>Eminence: it also works while its card is in the command zone.</summary>
    public bool FromCommandZone { get; init; }
}

public enum TriggerEvent
{
    /// <summary>"When this enters" (the source itself enters the battlefield).</summary>
    EntersBattlefield,
    /// <summary>A reflexive trigger ("When you do, …"): created by an effect, never by an event.</summary>
    Reflexive,
    /// <summary>"When this dies" (the source goes from the battlefield to a graveyard).</summary>
    Dies,
    /// <summary>"Whenever this attacks".</summary>
    Attacks,
    /// <summary>"At the beginning of your upkeep".</summary>
    YourUpkeep,
    /// <summary>"At the beginning of your end step".</summary>
    YourEndStep,
    /// <summary>"Whenever this deals combat damage to a player".</summary>
    DealsCombatDamageToPlayer,
    /// <summary>"Whenever this blocks".</summary>
    Blocks,
    /// <summary>"Whenever this attacks or blocks".</summary>
    AttacksOrBlocks,
    /// <summary>"Whenever [another] creature [you control] enters" — matched against <see cref="TriggeredAbility.Filter"/>.</summary>
    CreatureEnters,
    /// <summary>"Whenever a land you control enters" (landfall).</summary>
    LandEnters,
    /// <summary>"Whenever [another] creature [you control] dies" — matched against <see cref="TriggeredAbility.Filter"/>.</summary>
    CreatureDies,
    /// <summary>"Whenever you gain life".</summary>
    YouGainLife,
    /// <summary>"Whenever you cast a [noncreature / instant or sorcery / ...] spell" — matched against the filter.</summary>
    YouCastSpell,
    /// <summary>"Whenever you play a land [from exile]" (rule 305.1: playing it, not putting it onto the battlefield) — matched against the filter.</summary>
    YouPlayLand,
    /// <summary>A delayed ability: "at the beginning of that player's next declare attackers step".</summary>
    NextDeclareAttackers,
    /// <summary>"Whenever you cast or copy an instant or sorcery spell" (magecraft): your spells as they're cast and the copies you make.</summary>
    YouCastOrCopySpell,
    /// <summary>"Whenever you win a coin flip".</summary>
    YouWinCoinFlip,
    /// <summary>
    /// "Whenever one or more creatures an opponent controls attack you and aren't blocked": once blockers are declared, once for each
    /// opponent with an unblocked creature attacking you (not your planeswalkers); <c>"triggeredPlayer"</c> is that opponent.
    /// </summary>
    AttackedUnblockedByOpponent,
    /// <summary>"At the beginning of combat on your turn".</summary>
    YourBeginCombat,
    /// <summary>"Whenever you attack" (one or more creatures you control attack).</summary>
    YouAttack,
    /// <summary>"Whenever one or more [filter] you control attack": once per declaration with any matching attacker (amount: how many).</summary>
    YouAttackWithMatching,
    /// <summary>"Whenever an opponent casts a [filter] spell" (the filter's controller is ignored).</summary>
    OpponentCastsSpell,
    /// <summary>"Whenever a [filter] creature attacks" (another creature, usually one you control).</summary>
    CreatureAttacks,
    /// <summary>"Whenever an opponent loses life".</summary>
    OpponentLosesLife,
    /// <summary>"Whenever an opponent becomes the monarch" (rule 724): <c>"triggeredPlayer"</c> is that opponent. A player who already is the monarch doesn't become it again.</summary>
    OpponentBecomesMonarch,
    /// <summary>"Whenever one or more creatures you control become the target of an activated ability": once for each such ability put on the stack; <c>"triggeredPlayer"</c> is its controller.</summary>
    CreaturesYouControlTargetedByActivatedAbility,
    /// <summary>"Whenever you draw a card" (with <see cref="TriggeredAbility.NthOfTurn"/>: "your second card each turn").</summary>
    YouDrawCard,
    /// <summary>"Whenever a [filter] creature deals combat damage to a player".</summary>
    CreatureDealsCombatDamageToPlayer,
    /// <summary>"Whenever a [filter] creature deals combat damage to a player or planeswalker".</summary>
    CreatureDealsCombatDamageToPlayerOrPlaneswalker,
    /// <summary>"At the beginning of each end step" / "the end step".</summary>
    EachEndStep,
    /// <summary>"At the beginning of each combat".</summary>
    EachBeginCombat,
    /// <summary>"At the beginning of each upkeep".</summary>
    EachUpkeep,
    /// <summary>"Whenever one or more +1/+1 counters are put on [filter]" (this creature: filter "self").</summary>
    CountersPlaced,
    /// <summary>"Whenever this creature becomes tapped".</summary>
    BecomesTapped,
    /// <summary>"Whenever a player casts a spell" (any player, including you).</summary>
    AnyPlayerCastsSpell,
    /// <summary>"When you sacrifice this [permanent]".</summary>
    SelfSacrificed,
    /// <summary>"Whenever a source you control deals noncombat damage to an opponent".</summary>
    YourSourceDealsNoncombatDamageToOpponent,
    /// <summary>"Whenever a [filter] creature deals combat damage" (to anything).</summary>
    CreatureDealsCombatDamage,
    /// <summary>"Whenever this creature deals combat damage" (to anything).</summary>
    DealsCombatDamage,
    /// <summary>"Whenever an opponent discards a card".</summary>
    OpponentDiscards,
    /// <summary>"Whenever this permanent becomes untapped" / "Whenever [filter] becomes untapped".</summary>
    BecomesUntapped,
    /// <summary>"Whenever an opponent draws a card".</summary>
    OpponentDrawsCard,
    /// <summary>"At the beginning of each player's draw step".</summary>
    EachDrawStep,
    /// <summary>A Saga's chapter ability (rule 714.2): triggers when lore counters reach one of <see cref="TriggeredAbility.Chapters"/>.</summary>
    Chapter,
    /// <summary>"Whenever [another] [permanent matching the filter] enters" (any permanent type; tokens, artifacts, ...).</summary>
    PermanentEnters,
    /// <summary>"Whenever a [filter] card leaves your graveyard".</summary>
    LeavesGraveyard,
    /// <summary>"At the beginning of your first main phase" (precombat main).</summary>
    YourPrecombatMain,
    /// <summary>"When this is put into a graveyard from the battlefield" (any permanent, not only creatures).</summary>
    PutIntoGraveyard,
    /// <summary>"Whenever this becomes the target of a spell or ability an opponent controls".</summary>
    BecomesTargetOfOpponent,
    /// <summary>"Whenever you activate an ability of a [filter]".</summary>
    YouActivateAbility,
    /// <summary>"Whenever a player loses life".</summary>
    PlayerLosesLife,
    /// <summary>"Whenever you sacrifice a [filter]".</summary>
    YouSacrifice,
    /// <summary>"Whenever you forage" (rule 701.61).</summary>
    YouForage,
    /// <summary>"Whenever you give a gift" (rule 702.174).</summary>
    YouGiveGift,
    /// <summary>"Whenever you gain or lose life".</summary>
    YouGainOrLoseLife,
    /// <summary>"When this leaves the battlefield without dying" (to any zone but a graveyard).</summary>
    LeavesWithoutDying,
    /// <summary>"Whenever you expend N": as you spend your Nth total mana to cast spells this turn.</summary>
    YouExpend,
    /// <summary>"When this Class becomes level N" (rule 716.2d): N is the ability's own <see cref="AbilityDefinition.ClassLevel"/>.</summary>
    BecomesClassLevel,
    /// <summary>A creature an opponent controls would have died and was exiled instead by this permanent ("When you do, …").</summary>
    CreatureExiledInstead,
    /// <summary>A delayed ability: "at the beginning of the next upkeep".</summary>
    NextUpkeep,
    /// <summary>"Whenever this is dealt noncombat damage" (amount: the damage).</summary>
    DealtNoncombatDamage,
    /// <summary>"Whenever this creature becomes blocked".</summary>
    BecomesBlocked,
    /// <summary>"Whenever you scry" (amount: cards looked at).</summary>
    YouScry,
    /// <summary>"Whenever you surveil."</summary>
    YouSurveil,
    /// <summary>"Whenever you scry or surveil": one event for either.</summary>
    YouScryOrSurveil,
    /// <summary>"Whenever you activate a loyalty ability" (amount: loyalty counters removed to activate it).</summary>
    YouActivateLoyaltyAbility,
    /// <summary>"Whenever an opponent activates a loyalty ability" (the triggered player is that opponent).</summary>
    OpponentActivatesLoyaltyAbility,
    /// <summary>"Whenever an opponent is dealt noncombat damage" (by any source; the opponent is the triggered player, the damage the amount).</summary>
    OpponentDealtNoncombatDamage,
    /// <summary>"When enchanted creature is dealt damage": once for each source dealing damage to the permanent this is attached to.</summary>
    AttachedDealtDamage,
    /// <summary>"Whenever an opponent gains control of a permanent from you": the permanent is "triggered", that opponent "triggeredPlayer".</summary>
    OpponentGainsControlFromYou,
    /// <summary>"Whenever this deals damage": once for each damage event it deals damage in, with the total dealt ("that much").</summary>
    DealsDamage,
    /// <summary>"When the chosen creature leaves the battlefield" (the creature this permanent remembers, <c>rememberChosen</c>).</summary>
    ChosenCreatureLeaves,
    /// <summary>"When the chosen player loses the game."</summary>
    ChosenPlayerLoses,
    /// <summary>"At the beginning of each of your postcombat main phases."</summary>
    YourPostcombatMain,
    /// <summary>"At the beginning of each of your main phases" (precombat and postcombat).</summary>
    YourMainPhase,
    /// <summary>"Whenever a [filter] card is put into a graveyard from anywhere other than the battlefield."</summary>
    CardPutIntoGraveyardNotFromBattlefield,
    /// <summary>A card matching the filter is put into your graveyard from anywhere (with "batched": "one or more").</summary>
    CardPutIntoYourGraveyard,
    /// <summary>"Whenever equipped creature attacks" (an Equipment's own ability): the creature is "triggered".</summary>
    EquippedAttacks,
    /// <summary>"Whenever equipped creature deals combat damage to a player" (an Equipment's own ability): amount, that player.</summary>
    EquippedCombatDamageToPlayer,
    /// <summary>"Whenever one or more creatures deal combat damage to you".</summary>
    CombatDamageToYou,
    /// <summary>"When one or more of your opponents are dealt combat damage during your turn": once per combat damage step.</summary>
    OpponentsDealtCombatDamageDuringYourTurn,
    /// <summary>"Whenever your opponents are dealt combat damage" (any turn): once per combat damage step; batch: the creatures that dealt it.</summary>
    OpponentsDealtCombatDamage,
    /// <summary>"Whenever the final chapter ability of a Saga you control resolves".</summary>
    FinalChapterResolved,
    /// <summary>"Whenever the Ring tempts you".</summary>
    RingTemptsYou,
    /// <summary>"When this leaves the battlefield" (to any zone).</summary>
    LeavesBattlefield,
    /// <summary>"Whenever you choose a creature as your Ring-bearer".</summary>
    RingBearerChosen,
    /// <summary>"Whenever you create a token" (once per token).</summary>
    TokenCreated,
    /// <summary>"Whenever you create one or more [filter] tokens": once for each effect that creates them.</summary>
    TokensCreated,
    /// <summary>"Whenever one or more [filter] you control attack a player": once per player attacked (amount: how many).</summary>
    YouAttackPlayer,
    /// <summary>"When you attack enchanted opponent or a planeswalker they control or when they attack you or a planeswalker you control."</summary>
    AttackBetweenYouAndEnchanted,
    /// <summary>"Whenever enchanted player is attacked" (the player, not their planeswalkers; triggered player: the attacking player).</summary>
    EnchantedPlayerAttacked,
    /// <summary>"Whenever this creature deals damage to a [filter]" (subject: the creature dealt damage).</summary>
    DealsDamageToCreature,
    /// <summary>"Whenever a [filter] is dealt excess noncombat damage" (amount: the excess).</summary>
    ExcessNoncombatDamage,
    /// <summary>"Whenever [filter] becomes the target of a spell" (subject: that permanent).</summary>
    BecomesTargetOfSpell,
    /// <summary>"Whenever [filter] phases in".</summary>
    PhasesIn,
    /// <summary>"Whenever equipped creature blocks or becomes blocked by a creature" (once per such creature; subject: that creature).</summary>
    EquippedBlocksOrBecomesBlocked,
    /// <summary>"Whenever a [filter] becomes the target of a spell or ability an opponent controls" (subject: that permanent).</summary>
    PermanentBecomesTargetOfOpponent,
    /// <summary>"At the beginning of the monarch's end step" (the trigger is about the monarch: "that player").</summary>
    MonarchEndStep,
    /// <summary>"Whenever damage that would be dealt to you is prevented" (amount: the damage prevented).</summary>
    DamageToYouPrevented,
    /// <summary>"Whenever players finish voting".</summary>
    PlayersFinishVoting,
    /// <summary>"Whenever an opponent sacrifices a [filter]" (subject: the sacrificed card; player: who sacrificed it).</summary>
    OpponentSacrifices,
    /// <summary>"When you exert this creature" / "whenever you exert a creature" (with a filter: any creature you exert).</summary>
    Exerted,
    /// <summary>
    /// "Whenever this permanent transforms" (rule 701.27); with a filter, "transforms into [a permanent matching it]", judged
    /// right after it transforms (701.27e). Without <c>OnSelf</c>, a filter watches other permanents ("whenever a permanent you control transforms").
    /// </summary>
    Transforms,
    /// <summary>"When you cycle this card" (works from the graveyard it was discarded to; amount: X paid).</summary>
    Cycled,
    /// <summary>"When you cast this spell" (the spell's own ability, from the stack).</summary>
    CastThis,
    /// <summary>"Whenever an opponent activates an ability of a [filter] on the battlefield" (not a mana ability).</summary>
    OpponentActivatesAbility,
    /// <summary>"Whenever an opponent taps an artifact for mana" (subject: that artifact).</summary>
    OpponentTapsArtifactForMana,
    /// <summary>"Whenever a [filter] creature you control leaves the battlefield" (subject: that creature as it last existed).</summary>
    CreatureLeaves,
    /// <summary>"Whenever one or more creatures attack one of your opponents or a planeswalker they control" (player: that opponent).</summary>
    CreaturesAttackOpponent,
    /// <summary>"Whenever a player attacks you" / "whenever a player attacks" (player: the attacking player).</summary>
    PlayerAttacks,
    /// <summary>"Whenever you activate an ability that isn't a mana ability" (amount: the ability's stack object).</summary>
    YouActivateNonManaAbility,
    /// <summary>"At the beginning of the upkeep of enchanted creature's controller" (that player is the triggered player).</summary>
    EnchantedControllersUpkeep,
    /// <summary>"Whenever this creature deals damage to an opponent" (combat or noncombat; player: that opponent, amount: the damage).</summary>
    DealsDamageToOpponent,
    /// <summary>"When this becomes the target of a spell or ability" (anyone's; player: that spell or ability's controller).</summary>
    BecomesTarget,
    /// <summary>"When enchanted/equipped creature becomes the target of a spell or ability" (subject: that creature).</summary>
    AttachedBecomesTarget,
    /// <summary>"Whenever a [kind] counter is removed from this": once for each counter removed.</summary>
    CounterRemoved,
    /// <summary>"Whenever a player taps a land for mana" (player: that player, subject: the land).</summary>
    PlayerTapsLandForMana,
    /// <summary>"Whenever this creature blocks or becomes blocked by a creature": once per such creature (subject: that creature).</summary>
    BlocksOrBecomesBlockedByCreature,
    /// <summary>"Whenever this creature blocks a creature": once per creature it blocks (subject: that creature, rule 509.3d).</summary>
    BlocksCreature,
    /// <summary>"Whenever you become the target of a spell or ability an opponent controls" (player: that opponent, amount: the stack object).</summary>
    YouBecomeTargetOfOpponent,
    /// <summary>"When a spell or ability an opponent controls causes you to discard this card" (works from the graveyard it went to).</summary>
    DiscardedByOpponent,
    /// <summary>"When you discard this card" (whatever caused it; the card's own ability, from wherever it went).</summary>
    DiscardedThis,
    /// <summary>"Whenever a creature [matching filter] blocks": once per blocking creature (the triggered player is its controller).</summary>
    CreatureBlocks,
    /// <summary>"Whenever a player discards one or more cards" (the triggered player is the discarder; use batchedPerPlayer).</summary>
    PlayerDiscards,
    /// <summary>"Whenever this creature is dealt damage" (combat or not; amount: the damage, one trigger per source).</summary>
    DealtDamage,
    /// <summary>"When enchanted/equipped creature blocks" (once however many creatures it blocks; subject: that creature).</summary>
    AttachedBlocks,
    /// <summary>"Whenever a [filter] permanent you control dies" (any permanent put into a graveyard from the battlefield; subject: that permanent as it last existed).</summary>
    PermanentDies,
    /// <summary>A state trigger (rule 603.8): "When you control no [permanents]…": triggers once its condition (<see cref="TriggeredAbility.TriggerCondition"/>) is true, and not again until it has left the stack.</summary>
    StateTrigger,
    /// <summary>"At end of combat": at the beginning of each end of combat step (rule 511.2).</summary>
    EndOfCombat,
    /// <summary>"When this creature becomes renowned" (rule 702.112b: it goes from not renowned to renowned on the battlefield, whatever made it).</summary>
    BecomesRenowned,
    /// <summary>"When this creature becomes monstrous" (rule 701.37b: its monstrosity ability resolved and made it monstrous).</summary>
    BecomesMonstrous,
    /// <summary>"Whenever a [filter] creature becomes renowned" (subject: that creature; the observer sees itself too).</summary>
    CreatureBecomesRenowned,
    /// <summary>"Whenever a [filter] creature becomes blocked": once per attacker as it becomes blocked (rule 509.3c; subject: that attacker).</summary>
    CreatureBecomesBlocked,
    /// <summary>"Whenever a [filter] permanent becomes the target of a spell or ability you control" (subject: that permanent), also when a target is changed to it.</summary>
    BecomesTargetOfYours,
    /// <summary>"Whenever this deals combat damage to a player or planeswalker": once for each player or planeswalker dealt damage (amount: that damage).</summary>
    DealsCombatDamageToPlayerOrPlaneswalker,
    /// <summary>"Whenever another permanent you control leaves the battlefield" — matched against <see cref="TriggeredAbility.Filter"/> as it last existed.</summary>
    PermanentLeaves,
    /// <summary>"When this dies or is put into exile from the battlefield."</summary>
    DiesOrExiled,
    /// <summary>"When a [type] is championed with this creature" (rule 702.72): the trigger is about the exiled card.</summary>
    Championed,
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
    /// <summary>For cast triggers: the spell must target something matching ("a spell that targets a creature you don't control").</summary>
    public ObjectFilter? SpellTargets { get; init; }

    /// <summary>For cast triggers: the spell may instead target an opponent ("a spell that targets an opponent or a creature an opponent controls").</summary>
    public bool SpellTargetsOpponent { get; init; }

    /// <summary>For "creatures attack one of your opponents": only attacks on the player, not on their planeswalkers.</summary>
    public bool PlayersOnly { get; init; }

    /// <summary>For cast triggers: the spell matches the filter OR targets what <see cref="SpellTargets"/> describes ("an Equipment spell or a spell that targets …").</summary>
    public bool FilterOrSpellTargets { get; init; }

    public required TriggerEvent Trigger { get; init; }

    /// <summary>Which objects the event must involve, for events about other objects (creature enters, spell cast...).</summary>
    public ObjectFilter? Filter { get; init; }

    /// <summary>
    /// A condition of the trigger event itself ("whenever you attack with three or more creatures", "attacks while you
    /// control …"): checked only when the event happens, unlike an intervening "if" (<see cref="Condition"/>), which is
    /// checked again on resolution (rule 603.4).
    /// </summary>
    public Condition? TriggerCondition { get; init; }

    /// <summary>Only the Nth such event of the turn counts ("your second card each turn", "for the first time each turn").</summary>
    public int? NthOfTurn { get; init; }

    /// <summary>For <see cref="TriggerEvent.YouExpend"/>: N of "whenever you expend N".</summary>
    public int Expend { get; init; }

    /// <summary>For <see cref="TriggerEvent.CountersPlaced"/>: only counters put on the source itself.</summary>
    public bool OnSelf { get; init; }

    /// <summary>For <see cref="TriggerEvent.CountersPlaced"/>: once for each counter ("whenever a +1/+1 counter is put on …"), not once per event.</summary>
    public bool PerCounter { get; init; }

    /// <summary>"If it's the first time counters have been put on that creature this turn" (counters-placed triggers).</summary>
    public bool FirstCountersThisTurn { get; init; }

    /// <summary>Becomes-the-target triggers: "a creature you control or a creature spell you control" (spells on the stack too).</summary>
    public bool SpellsToo { get; init; }

    /// <summary>Put-into-your-graveyard triggers: only cards that came from this zone ("from your library").</summary>
    public State.Zone? FromZone { get; init; }

    /// <summary>A batched trigger whose amount is the total of the batch ("that much damage" from one or more creatures).</summary>
    public bool SumBatchAmounts { get; init; }

    /// <summary>"This ability triggers only once each turn."</summary>
    public bool OncePerTurn { get; init; }

    /// <summary>For spell-cast triggers: only spells that target the source ("a spell that targets this creature").</summary>
    public bool TargetsSource { get; init; }

    /// <summary>The ability works while its card is in its owner's graveyard (and not on the battlefield).</summary>
    public bool FromGraveyard { get; init; }

    /// <summary>For counter triggers: only counters put by you ("Whenever you put one or more counters on").</summary>
    public bool PlacedByYou { get; init; }

    /// <summary>For counter triggers: the kind of counters (default +1/+1).</summary>
    public CounterKind CounterKind { get; init; } = CounterKind.PlusOnePlusOne;

    /// <summary>For a chapter ability: its chapter numbers ("III, IV —").</summary>
    public IReadOnlyList<int> Chapters { get; init; } = Array.Empty<int>();

    /// <summary>"Whenever one or more …": triggers once for events that happen at the same time.</summary>
    public bool Batched { get; init; }

    /// <summary>With <see cref="Batched"/>: once per player the simultaneous events are about ("whenever one or more creatures you control deal combat damage to a player").</summary>
    public bool BatchedPerPlayer { get; init; }

    /// <summary>For counter triggers: counters of any kind ("one or more counters").</summary>
    public bool AnyCounterKind { get; init; }

    /// <summary>For draw triggers: not the first card a player draws in their own draw step.</summary>
    public bool ExceptFirstInDrawStep { get; init; }
}

/// <summary>
/// Describes a set of objects relative to an ability's controller and source: "another Elf you control",
/// "a noncreature spell", "an instant or sorcery spell", "a creature with power 4 or greater".
/// </summary>
/// <param name="Types">The object must have at least one of these types (no restriction when 0).</param>
/// <param name="ExcludedTypes">The object must have none of these types ("noncreature").</param>
/// <param name="Other">Excludes the source itself ("another").</param>
/// <param name="Colors">The object must have at least one of these colors (W, U, B, R, G).</param>
/// <param name="Keyword">The object must have this keyword ability ("creature with flying").</param>
/// <param name="WithoutKeyword">The object must not have this keyword ability ("creature without flying").</param>
/// <param name="InCombat">Attacking or blocking.</param>
public sealed record ObjectFilter(
    Cards.CardType Types = 0,
    Cards.CardType ExcludedTypes = 0,
    string? Subtype = null,
    ControllerFilter Controller = ControllerFilter.You,
    bool Other = false,
    int? MinPower = null,
    bool? Token = null,
    int? MaxPower = null,
    int? MinToughness = null,
    int? MinManaValue = null,
    int? MaxManaValue = null,
    IReadOnlyList<string>? Colors = null,
    Cards.Keyword? Keyword = null,
    Cards.Keyword? WithoutKeyword = null,
    bool? Tapped = null,
    bool? InCombat = null,
    bool? Attacking = null,
    Cards.Supertype Supertype = 0,
    string? ExcludedSubtype = null,
    string? Name = null,
    bool? HasCounters = null,
    IReadOnlyList<ObjectFilter>? AnyOf = null,
    bool AttachedToSource = false,
    bool ChosenColor = false,
    bool ChosenType = false,
    bool DamagedBySource = false,
    bool MaxManaValueSourcePower = false,
    bool? Attached = null,
    Cards.Supertype ExcludedSupertype = 0,
    bool MaxManaValueLandCount = false,
    bool OwnedByYou = false,
    bool PutIntoZoneThisTurn = false,
    bool? Blocking = null,
    bool? Multicolored = null,
    bool? Colorless = null,
    bool? Enchanted = null,
    bool? Equipped = null,
    bool? Commander = null,
    bool? InHand = null,
    bool FromBattlefieldThisTurn = false,
    bool PaidWithTreasure = false,
    bool ChosenParity = false,
    bool SharesNameWithYourLegendary = false,
    int? MaxToughness = null,
    bool NotChosenType = false,
    bool LeastPower = false,
    bool DamagedThisTurn = false,
    bool BlockingSource = false,
    bool DealtCombatDamageToYou = false,
    bool MaxManaValueTriggerAmount = false)
{
    /// <summary>"With mana value less than or equal to the amount of life you lost this turn" (the ability's controller).</summary>
    public bool MaxManaValueLifeLostThisTurn { get; init; }

    /// <summary>"A goaded creature": goaded by anyone (rule 701.15).</summary>
    public bool Goaded { get; init; }

    /// <summary>Power at most that of the object the ability is about ("with power less than or equal to the amassed Army's power").</summary>
    public bool MaxPowerTriggered { get; init; }

    /// <summary>Dealt damage this turn by a Spider its controller controlled.</summary>
    public bool DamagedThisTurnByYourSpider { get; init; }

    /// <summary>Historic: an artifact, a legendary or a Saga (rule 700.6).</summary>
    public bool Historic { get; init; }

    /// <summary>"Creatures with toxic".</summary>
    public bool HasToxic { get; init; }

    /// <summary>"With mana value less than this creature's power".</summary>
    public bool LessManaValueThanSourcePower { get; init; }

    /// <summary>Power exactly X ("target creature with power X").</summary>
    public bool PowerIsX { get; init; }

    /// <summary>Toughness less than the source's power ("with toughness less than this creature's power").</summary>
    public bool ToughnessLessThanSourcePower { get; init; }

    /// <summary>Power less than the source's ("with lesser power").</summary>
    public bool LesserPowerThanSource { get; init; }

    /// <summary>Power greater than the source's ("with greater power").</summary>
    public bool GreaterPowerThanSource { get; init; }

    /// <summary>Blocked, or was blocked by, a legendary creature this turn.</summary>
    public bool BlockedOrBlockedByLegendaryThisTurn { get; init; }

    /// <summary>Shares a color with a legendary creature the ability's controller controls.</summary>
    public bool SharesColorWithYourLegendaryCreature { get; init; }

    /// <summary>Attacking the filter's controller or a planeswalker they control ("a creature attacks you or a planeswalker you control").</summary>
    public bool AttackingYou { get; init; }

    /// <summary>Attacking the filter's controller, not a planeswalker ("creatures attacking you").</summary>
    public bool AttackingYouDirectly { get; init; }

    /// <summary>"That's attacking one of your opponents": attacking an opponent of the source's controller (not a planeswalker or battle).</summary>
    public bool AttackingYourOpponent { get; init; }

    /// <summary>A spell cast from a graveyard / a permanent that entered from a graveyard.</summary>
    public bool FromGraveyard { get; init; }

    /// <summary>A permanent that entered from a graveyard or was cast from a graveyard.</summary>
    public bool EnteredOrCastFromGraveyard { get; init; }

    /// <summary>A spell cast from its owner's hand.</summary>
    public bool CastFromHand { get; init; }

    /// <summary>"With base power 1" (base power and toughness after copy and setting effects, before modifications).</summary>
    public int? BasePower { get; init; }

    /// <summary>"With base power or toughness 1".</summary>
    public int? BasePowerOrToughness { get; init; }

    /// <summary>"If it wasn't cast" (false) / "if it was cast" (true): a permanent that entered as a cast spell resolved.</summary>
    public bool? WasCast { get; init; }

    /// <summary>A spell cast from exile / a land played (or permanent put) onto the battlefield from exile.</summary>
    public bool FromExile { get; init; }

    /// <summary>A spell with {X} in its mana cost.</summary>
    public bool HasXInCost { get; init; }

    /// <summary>Exiled with the source ("a creature card exiled with this permanent").</summary>
    public bool ExiledWithSource { get; init; }

    /// <summary>Mana value exactly X ("target creature card with mana value X"; X is announced first).</summary>
    public bool ManaValueIsX { get; init; }

    /// <summary>Shares a creature type with the object a trigger is about.</summary>
    public bool SharesCreatureTypeWithTriggered { get; init; }

    /// <summary>Renowned ("a renowned creature").</summary>
    public bool Renowned { get; init; }

    /// <summary>A copy of a prepare spell cast from exile ("a prepared spell").</summary>
    public bool PreparedSpell { get; init; }

    /// <summary>The object an emblem (or other source) remembers ("that creature").</summary>
    public bool IsRemembered { get; init; }

    /// <summary>Toughness greater than its own power.</summary>
    public bool ToughnessAbovePower { get; init; }

    /// <summary>"With total power and toughness 5 or less".</summary>
    public int? MaxPowerPlusToughness { get; init; }

    /// <summary>"A transformed permanent" (true: back face up, rule 701.27g) or a double-faced permanent front face up (false).</summary>
    public bool? Transformed { get; init; }

    /// <summary>Has at least one counter of this kind on it ("a permanent with a phylactery counter on it").</summary>
    public CounterKind? WithCounterKind { get; init; }

    /// <summary>Doesn't share a creature type with a creature the ability's controller controls.</summary>
    public bool NoSharedCreatureTypeWithYours { get; init; }

    /// <summary>Has none of these colors ("nonblack": ["B"]).</summary>
    public IReadOnlyList<string>? NotColors { get; init; }

    /// <summary>Has the card name the source chose as it entered ("lands with the chosen name").</summary>
    public bool ChosenName { get; init; }

    /// <summary>Mana value exactly the amount the trigger is about ("with mana value X" once X was paid for a reflexive ability).</summary>
    public bool ManaValueIsTriggerAmount { get; init; }

    /// <summary>Only the source itself ("sacrifice this enchantment").</summary>
    public bool IsSource { get; init; }

    /// <summary>Has none of these keywords ("except by creatures with flying or reach": blockers without both).</summary>
    public IReadOnlyList<Cards.Keyword>? WithoutKeywords { get; init; }

    /// <summary>"Whose power and toughness aren't equal".</summary>
    public bool PowerNotEqualToughness { get; init; }

    /// <summary>Owned by a player other than the ability's controller ("a permanent owned by another player").</summary>
    public bool NotOwnedByYou { get; init; }

    /// <summary>Controlled by an opponent of this player (a filter fixed when an effect is created: "creatures your opponents control").</summary>
    public Core.PlayerId? OpponentOf { get; init; }

    /// <summary>One of the cards a reflexive ability carries ("target creature card exiled this way").</summary>
    public bool AmongFound { get; init; }

    /// <summary>A permanent that entered the battlefield this turn ("target commander that entered this turn").</summary>
    public bool EnteredThisTurn { get; init; }

    /// <summary>Attacked this turn ("a creature … that attacked this turn").</summary>
    public bool AttackedThisTurn { get; init; }

    /// <summary>Modified (rule 700.9): it has a counter on it, or is equipped, or is enchanted by an Aura its controller controls.</summary>
    public bool Modified { get; init; }

    /// <summary>A blocked creature: an attacking creature a creature blocked this combat (it stays blocked if the blockers leave combat, rule 509.1h).</summary>
    public bool Blocked { get; init; }

    /// <summary>Not the object the trigger is about ("for each other creature you control" than the attacking one).</summary>
    public bool NotTriggered { get; init; }

    /// <summary>With lesser mana value than the object the trigger is about, as it is or last existed ("target creature you control with lesser mana value").</summary>
    public bool LesserManaValueThanTriggered { get; init; }

    /// <summary>"With equal or lesser mana value" than the object the trigger is about.</summary>
    public bool AtMostManaValueOfTriggered { get; init; }

    /// <summary>"With mana value less than or equal to the number of counters on that creature" (the object the trigger is about).</summary>
    public bool MaxManaValueTriggeredCounters { get; init; }

    /// <summary>Not the card the trigger is about, as whatever object it is now ("another target permanent card" after it died).</summary>
    public bool NotTriggeredCard { get; init; }

    /// <summary>"A spell with a single target": a spell on the stack whose targets are one object or player.</summary>
    public bool SingleTargetSpell { get; init; }

    /// <summary>"If it wasn't put onto the battlefield with this ability": not put there by the source's own effect.</summary>
    public bool NotPutByThis { get; init; }

    /// <summary>Not named this ("an Otter spell other than [this card's name]").</summary>
    public string? NotName { get; init; }

    /// <summary>Not in this zone now ("leave the battlefield without dying": not in a graveyard).</summary>
    public State.Zone? NotInZone { get; init; }

    /// <summary>Mana value at most the mana spent to cast the source ("with mana value less than or equal to the amount of mana spent to cast this creature").</summary>
    public bool MaxManaValueSpentOnSource { get; init; }

    /// <summary>Mana value X or less, X being the resolving spell's ("with mana value X or less").</summary>
    public bool MaxManaValueX { get; init; }

    /// <summary>A spell or ability on the stack with the ability's controller among its targets ("spell that targets you").</summary>
    public bool TargetsYou { get; init; }
    /// <summary>Dealt damage this turn by the object the source remembers ("a creature dealt damage by that creature this turn"), by object (rule 400.7).</summary>
    public bool DamagedThisTurnByRemembered { get; init; }

    /// <summary>Card types it must all have ("artifact creature"), where <see cref="Types"/> asks for any one of them.</summary>
    public Cards.CardType AllTypes { get; init; }

    /// <summary>A creature with the greatest power among creatures its controller controls ("a creature with the greatest power among creatures they control").</summary>
    public bool GreatestPowerAmongItsController { get; init; }

    /// <summary>"Its power is greater than each other creature's power": a creature whose power is above every other creature's on the battlefield.</summary>
    public bool PowerGreaterThanEachOtherCreature { get; init; }

    /// <summary>A creature with the greatest mana value among creatures its controller controls.</summary>
    public bool GreatestManaValueAmongItsController { get; init; }

    /// <summary>Is in this zone ("from your graveyard", "exiled card").</summary>
    public State.Zone? InZone { get; init; }

    /// <summary>Has flashback (printed or given by an effect).</summary>
    public bool HasFlashback { get; init; }

    /// <summary>Has none of these subtypes ("each creature that isn't a Kraken, Leviathan, Merfolk, Octopus, or Serpent").</summary>
    public IReadOnlyList<string>? NotSubtypes { get; init; }

    public static readonly ObjectFilter Anything = new(Controller: ControllerFilter.Any);

    public static readonly ObjectFilter YourCreatures = new(Cards.CardType.Creature);
}
