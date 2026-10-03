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
}

/// <summary>What an instant or sorcery does when it resolves.</summary>
public sealed record SpellAbility : AbilityDefinition;

/// <summary>Costs of an activated ability, e.g. "{1}{R}, {T}, Sacrifice this".</summary>
public sealed record AbilityCost(ManaCost Mana, bool Tap = false, bool SacrificeSelf = false)
{
    public static readonly AbilityCost TapOnly = new(ManaCost.Zero, Tap: true);
}

/// <summary>"[Cost]: [Effect]." (rule 602).</summary>
public sealed record ActivatedAbility : AbilityDefinition
{
    public required AbilityCost Cost { get; init; }

    /// <summary>"Activate only as a sorcery."</summary>
    public bool SorcerySpeed { get; init; }
}

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
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
    public required TriggerEvent Trigger { get; init; }

    /// <summary>Which objects the event must involve, for events about other objects (creature enters, spell cast...).</summary>
    public ObjectFilter? Filter { get; init; }
}

/// <summary>
/// Describes a set of objects relative to an ability's controller and source: "another Elf you control",
/// "a noncreature spell", "an instant or sorcery spell", "a creature with power 4 or greater".
/// </summary>
/// <param name="Types">The object must have at least one of these types (no restriction when 0).</param>
/// <param name="ExcludedTypes">The object must have none of these types ("noncreature").</param>
/// <param name="Other">Excludes the source itself ("another").</param>
public sealed record ObjectFilter(
    Cards.CardType Types = 0,
    Cards.CardType ExcludedTypes = 0,
    string? Subtype = null,
    ControllerFilter Controller = ControllerFilter.You,
    bool Other = false,
    int? MinPower = null,
    bool? Token = null)
{
    public static readonly ObjectFilter YourCreatures = new(Cards.CardType.Creature);
}
