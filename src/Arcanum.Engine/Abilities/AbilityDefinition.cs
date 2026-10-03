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
}

/// <summary>One mode of a modal spell or ability.</summary>
public sealed record Mode(string Text, IReadOnlyList<TargetSpec> Targets, IReadOnlyList<Effect> Effects);

/// <summary>What an instant or sorcery does when it resolves.</summary>
public sealed record SpellAbility : AbilityDefinition;

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

    /// <summary>"Activate only if [condition]."</summary>
    public Condition? ActivationCondition { get; init; }
}

/// <summary>"[Spells matching the filter] you cast cost {N} less to cast" while the source is on the battlefield.</summary>
public sealed record SpellCostReduction(ObjectFilter Spells, int Amount) : AbilityDefinition;

public enum TriggerEvent
{
    /// <summary>"When this enters" (the source itself enters the battlefield).</summary>
    EntersBattlefield,
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
    /// <summary>"Whenever an opponent draws a card".</summary>
    OpponentDrawsCard,
    /// <summary>"At the beginning of each player's draw step".</summary>
    EachDrawStep,
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
    public required TriggerEvent Trigger { get; init; }

    /// <summary>Which objects the event must involve, for events about other objects (creature enters, spell cast...).</summary>
    public ObjectFilter? Filter { get; init; }

    /// <summary>Only the Nth such event of the turn counts ("your second card each turn", "for the first time each turn").</summary>
    public int? NthOfTurn { get; init; }

    /// <summary>For <see cref="TriggerEvent.CountersPlaced"/>: only counters put on the source itself.</summary>
    public bool OnSelf { get; init; }

    /// <summary>"This ability triggers only once each turn."</summary>
    public bool OncePerTurn { get; init; }
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
    bool AttachedToSource = false)
{
    public static readonly ObjectFilter Anything = new(Controller: ControllerFilter.Any);

    public static readonly ObjectFilter YourCreatures = new(Cards.CardType.Creature);
}
