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
}

/// <summary>Attach the source Aura/Equipment to a permanent ("Equip {2}").</summary>
public sealed record AttachSelf(Subject To) : Effect;
