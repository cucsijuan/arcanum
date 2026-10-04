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

    /// <summary>"Choose one that hasn't been chosen": each mode can be chosen only once for this object.</summary>
    public bool ModesOncePerObject { get; init; }

    /// <summary>"Choose one that hasn't been chosen this turn".</summary>
    public bool ModesOncePerTurn { get; init; }

    /// <summary>One more mode may be chosen if this holds as it is cast ("if you control a Wizard, you may choose two instead").</summary>
    public Condition? ExtraModeIf { get; init; }

    /// <summary>A rule tying the targets together.</summary>
    public TargetRule TargetRule { get; init; }

    /// <summary>What this spell does when kicked, when that changes its targets ("instead any number of target creatures").</summary>
    public AbilityDefinition? WhenKicked { get; init; }
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
}

/// <summary>One mode of a modal spell or ability.</summary>
public sealed record Mode(string Text, IReadOnlyList<TargetSpec> Targets, IReadOnlyList<Effect> Effects);

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

    /// <summary>A loyalty ability's cost: +N / −N loyalty counters (null: not a loyalty ability, rule 606).</summary>
    public int? Loyalty { get; init; }

    /// <summary>"Put a [kind] counter on this" as a cost.</summary>
    public int AddCounters { get; init; }
    public CounterKind AddCounterKind { get; init; } = CounterKind.PlusOnePlusOne;

    /// <summary>"Return this [permanent] to its owner's hand" as a cost.</summary>
    public bool ReturnSelfToHand { get; init; }

    /// <summary>"Tap [the permanent that granted this ability]" as a cost ("Tap Fishing Pole").</summary>
    public bool TapGranter { get; init; }

    /// <summary>Activated from the hand by discarding this card (cycling, rule 702.29).</summary>
    public bool FromHand { get; init; }

    public static readonly AbilityCost TapOnly = new(ManaCost.Zero, Tap: true);
}

/// <summary>"[Cost]: [Effect]." (rule 602).</summary>
public sealed record ActivatedAbility : AbilityDefinition
{
    public required AbilityCost Cost { get; init; }

    /// <summary>"Activate only as a sorcery."</summary>
    public bool SorcerySpeed { get; init; }

    /// <summary>"Activate only once each turn."</summary>
    public bool OncePerTurn { get; init; }

    /// <summary>"Activate only once" (for as long as the permanent stays on the battlefield).</summary>
    public bool OnlyOnce { get; init; }

    /// <summary>"Activate only if [condition]."</summary>
    public Condition? ActivationCondition { get; init; }

    /// <summary>An equip ability (rule 702.6): affected by "equip abilities you activate cost less" effects.</summary>
    public bool IsEquip { get; init; }

    /// <summary>"This ability costs {1} less to activate for each [filter] you control."</summary>
    public ObjectFilter? CostReductionPer { get; init; }

    /// <summary>"This ability costs {N} less to activate if [condition]."</summary>
    public Condition? CostReductionIf { get; init; }
    public int CostReductionAmount { get; init; }
}

/// <summary>"[Spells matching the filter] you cast cost {N} less to cast" while the source is on the battlefield.</summary>
public sealed record SpellCostReduction(ObjectFilter Spells, int Amount) : AbilityDefinition
{
    /// <summary>The amount worked out from the source ("{X} less, where X is equipped creature's power").</summary>
    public Quantity? AmountFrom { get; init; }

    /// <summary>Only the first such spell each turn ("the first creature spell you cast each turn").</summary>
    public bool FirstOfTurn { get; init; }

    /// <summary>Those spells can also be cast as though they had flash.</summary>
    public bool GrantsFlash { get; init; }
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
    /// <summary>"At the beginning of combat on your turn".</summary>
    YourBeginCombat,
    /// <summary>"Whenever you attack" (one or more creatures you control attack).</summary>
    YouAttack,
    /// <summary>"Whenever an opponent casts a [filter] spell" (the filter's controller is ignored).</summary>
    OpponentCastsSpell,
    /// <summary>"Whenever a [filter] creature attacks" (another creature, usually one you control).</summary>
    CreatureAttacks,
    /// <summary>"Whenever an opponent loses life".</summary>
    OpponentLosesLife,
    /// <summary>"Whenever you draw a card" (with <see cref="TriggeredAbility.NthOfTurn"/>: "your second card each turn").</summary>
    YouDrawCard,
    /// <summary>"Whenever a [filter] creature deals combat damage to a player".</summary>
    CreatureDealsCombatDamageToPlayer,
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
    /// <summary>"Whenever one or more creatures deal combat damage to you".</summary>
    CombatDamageToYou,
    /// <summary>"Whenever the final chapter ability of a Saga you control resolves".</summary>
    FinalChapterResolved,
    /// <summary>"Whenever the Ring tempts you".</summary>
    RingTemptsYou,
    /// <summary>"When this leaves the battlefield" (to any zone).</summary>
    LeavesBattlefield,
    /// <summary>"Whenever you choose a creature as your Ring-bearer".</summary>
    RingBearerChosen,
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
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

    /// <summary>For <see cref="TriggerEvent.CountersPlaced"/>: only counters put on the source itself.</summary>
    public bool OnSelf { get; init; }

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
    /// <summary>Power at most that of the object the ability is about ("with power less than or equal to the amassed Army's power").</summary>
    public bool MaxPowerTriggered { get; init; }

    /// <summary>Historic: an artifact, a legendary or a Saga (rule 700.6).</summary>
    public bool Historic { get; init; }

    /// <summary>Power exactly X ("target creature with power X").</summary>
    public bool PowerIsX { get; init; }

    public static readonly ObjectFilter Anything = new(Controller: ControllerFilter.Any);

    public static readonly ObjectFilter YourCreatures = new(Cards.CardType.Creature);
}
