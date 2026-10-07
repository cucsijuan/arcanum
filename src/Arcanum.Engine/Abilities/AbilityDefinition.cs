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

    /// <summary>"You may choose the same mode more than once."</summary>
    public bool ModesMayRepeat { get; init; }

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
    /// <summary>Different objects controlled by the same player ("another target creature that player controls").</summary>
    SameController,
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

    /// <summary>{S} symbols: each paid with one mana from a snow source (rule 107.4h).</summary>
    public int SnowMana { get; init; }

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
/// <summary>"Activated abilities of Foods you control cost {1} less to activate" / "Equip abilities you activate cost {1} less" (generic mana only).</summary>
public sealed record AbilityCostReduction(ObjectFilter Sources, int Amount, bool EquipOnly) : AbilityDefinition;

/// <summary>"[Spells matching the filter] cost {N} more to cast" (every player's, while the source is on the battlefield).</summary>
public sealed record SpellCostIncrease(ObjectFilter Spells, int Amount) : AbilityDefinition;

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
    /// <summary>"Whenever you create a token" (once per token).</summary>
    TokenCreated,
    /// <summary>"Whenever one or more [filter] you control attack a player": once per player attacked (amount: how many).</summary>
    YouAttackPlayer,
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
    /// <summary>A state trigger (rule 603.8): "When you control no [permanents]…": triggers once its condition (<see cref="TriggeredAbility.TriggerCondition"/>) is true, and not again until it has left the stack.</summary>
    StateTrigger,
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
    /// <summary>For cast triggers: the spell must target something matching ("a spell that targets a creature you don't control").</summary>
    public ObjectFilter? SpellTargets { get; init; }

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

    /// <summary>Dealt damage this turn by a Spider its controller controlled.</summary>
    public bool DamagedThisTurnByYourSpider { get; init; }

    /// <summary>Historic: an artifact, a legendary or a Saga (rule 700.6).</summary>
    public bool Historic { get; init; }

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

    /// <summary>A spell cast from a graveyard / a permanent that entered from a graveyard.</summary>
    public bool FromGraveyard { get; init; }

    /// <summary>A spell cast from its owner's hand.</summary>
    public bool CastFromHand { get; init; }

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

    /// <summary>"A transformed permanent" (true: back face up, rule 701.27g) or a double-faced permanent front face up (false).</summary>
    public bool? Transformed { get; init; }

    /// <summary>Has at least one counter of this kind on it ("a permanent with a phylactery counter on it").</summary>
    public CounterKind? WithCounterKind { get; init; }

    /// <summary>Doesn't share a creature type with a creature the ability's controller controls.</summary>
    public bool NoSharedCreatureTypeWithYours { get; init; }

    /// <summary>Has none of these colors ("nonblack": ["B"]).</summary>
    public IReadOnlyList<string>? NotColors { get; init; }

    public static readonly ObjectFilter Anything = new(Controller: ControllerFilter.Any);

    public static readonly ObjectFilter YourCreatures = new(Cards.CardType.Creature);
}
