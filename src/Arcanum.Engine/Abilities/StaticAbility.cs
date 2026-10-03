// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Abilities;

/// <summary>Which permanents a static ability affects, relative to its source.</summary>
public enum AffectedScope
{
    /// <summary>The source itself.</summary>
    Self,
    /// <summary>Creatures the source's controller controls.</summary>
    YourCreatures,
    /// <summary>Creatures controlled by the source controller's opponents.</summary>
    OpponentsCreatures,
    /// <summary>Every creature.</summary>
    AllCreatures,
    /// <summary>The permanent this Aura is attached to.</summary>
    Enchanted,
    /// <summary>The creature this Equipment is attached to.</summary>
    Equipped,
    /// <summary>Permanents the source's controller controls.</summary>
    YourPermanents,
}

/// <summary>Filter for a static ability: scope, "other" (excludes the source) and an optional subtype.</summary>
public sealed record AffectedFilter(AffectedScope Scope, bool Other = false, string? Subtype = null);

/// <summary>
/// A static ability that changes other objects' characteristics while its source is on the battlefield
/// (rule 604): "Other Goblins you control get +1/+1", "Enchanted creature gets +2/+0 and has trample", ...
/// Applied in layer 6 (keywords) and layer 7c (P/T changes) — rule 613.
/// </summary>
public sealed record StaticAbility(AffectedFilter Affects, int Power = 0, int Toughness = 0, IReadOnlyList<Keyword>? Keywords = null)
    : AbilityDefinition
{
    public IReadOnlyList<Keyword> GrantedKeywords => Keywords ?? Array.Empty<Keyword>();

    /// <summary>Further requirements on the affected objects ("with flying", "attacking", "green", "with +1/+1 counters").</summary>
    public ObjectFilter? Filter { get; init; }

    /// <summary>The ability applies only while this holds ("as long as you have 25 or more life", "during your turn").</summary>
    public Condition? While { get; init; }

    /// <summary>Extra P/T worked out continuously ("+1/+1 for each Forest you control").</summary>
    public Quantity? PowerBonus { get; init; }
    public Quantity? ToughnessBonus { get; init; }

    /// <summary>Creature types added ("is an Angel in addition to its other types").</summary>
    public IReadOnlyList<string>? AddSubtypes { get; init; }

    /// <summary>Abilities granted ("Enchanted creature has '{T}: ...'", "Equipped creature has '...'").</summary>
    public IReadOnlyList<AbilityDefinition>? GrantsAbilities { get; init; }

    /// <summary>"Loses all abilities" (layer 6).</summary>
    public bool LosesAllAbilities { get; init; }

    /// <summary>Sets base power/toughness ("with base power and toughness 1/1", layer 7b).</summary>
    public int? SetPower { get; init; }
    public int? SetToughness { get; init; }

    /// <summary>Replaces card types / creature types / colors / name (layers 4, 5, 1-ish).</summary>
    public Cards.CardType? SetTypes { get; init; }
    public IReadOnlyList<string>? SetSubtypes { get; init; }
    public IReadOnlyList<string>? SetColors { get; init; }
    public string? SetName { get; init; }

    /// <summary>Card types added ("is an artifact in addition").</summary>
    public Cards.CardType AddTypes { get; init; }

    /// <summary>Grants a mana ability: "{T}: Add [types]" (amount mana of one of them), in addition to its own.</summary>
    public IReadOnlyList<Mana.ManaType>? GrantsMana { get; init; }
    public int GrantsManaAmount { get; init; } = 1;

    /// <summary>"You control enchanted permanent" (layer 2).</summary>
    public bool GivesControl { get; init; }

    /// <summary>Adds the creature type chosen as the source entered ("is the chosen type in addition to its other types").</summary>
    public bool AddChosenType { get; init; }
}

/// <summary>Attach the source Aura/Equipment to a permanent ("Equip {2}").</summary>
public sealed record AttachSelf(Subject To) : Effect;
