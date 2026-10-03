// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Abilities;

/// <summary>A spell's effect or one of a permanent's abilities: what it targets and what it does.</summary>
public abstract record AbilityDefinition
{
    public IReadOnlyList<TargetSpec> Targets { get; init; } = Array.Empty<TargetSpec>();
    public required IReadOnlyList<Effect> Effects { get; init; }

    /// <summary>Rules text shown to players (e.g. on the stack).</summary>
    public string Text { get; init; } = "";
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
}

/// <summary>"When/Whenever/At [event], [effect]." (rule 603).</summary>
public sealed record TriggeredAbility : AbilityDefinition
{
    public required TriggerEvent Trigger { get; init; }
}
