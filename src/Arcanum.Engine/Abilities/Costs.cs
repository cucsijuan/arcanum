// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Abilities;

/// <summary>
/// Non-mana costs: "discard a card", "sacrifice a creature" / "sacrifice another creature", "pay 2 life".
/// </summary>
/// <param name="Sacrifice">What must be sacrificed (null: nothing); the filter's controller is ignored (you control it).</param>
public sealed record ExtraCost(int Discard = 0, ObjectFilter? Sacrifice = null, int SacrificeCount = 1, int PayLife = 0);

/// <summary>
/// "Costs {N} less to cast": a fixed amount when <see cref="Condition"/> holds, or that amount for each permanent
/// matching <see cref="PerPermanent"/> (or each card in your graveyard matching <see cref="PerGraveyardCard"/>).
/// Only generic mana is reduced (rule 601.2f).
/// </summary>
public sealed record CostReduction(int Amount, Condition? Condition = null, ObjectFilter? PerPermanent = null, ObjectFilter? PerGraveyardCard = null)
{
    /// <summary>"Costs {X} less, where X is the total power of creatures you control."</summary>
    public bool ByTotalPower { get; init; }
}
