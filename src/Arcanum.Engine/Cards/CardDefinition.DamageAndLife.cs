// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;

namespace Arcanum.Engine.Cards;

/// <summary>
/// "If a [matching] source you control would deal damage to a permanent or player, it deals that much damage plus
/// <paramref name="Amount"/> to that permanent or player instead" (a replacement effect, rule 614.1a).
/// <paramref name="Sources"/> is judged with the permanent that has this ability as the source ("another red source").
/// </summary>
public sealed record DamageBonus(ObjectFilter Sources, int Amount);

/// <summary>
/// "If a [matching] source would deal damage to you, prevent <paramref name="Amount"/> of that damage" (a prevention effect,
/// rule 615): applies once to each source's damage in each damage event.
/// </summary>
public sealed record DamageToYouReduction(ObjectFilter Sources, int Amount);

public sealed partial record CardDefinition
{
    /// <summary>Damage replacement effects of this permanent that add to damage its controller's sources deal.</summary>
    public IReadOnlyList<DamageBonus>? DamageBonuses { get; init; }

    /// <summary>Prevention effects of this permanent that reduce damage dealt to its controller.</summary>
    public IReadOnlyList<DamageToYouReduction>? DamageToYouReductions { get; init; }
}
