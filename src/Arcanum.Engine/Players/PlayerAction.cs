// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>Something a player can do while holding priority.</summary>
public abstract record PlayerAction;

public sealed record PassPriority : PlayerAction
{
    public static readonly PassPriority Instance = new();
}

public sealed record PlayLand(CardId Card) : PlayerAction;

/// <summary>Cast a spell. Mana is paid through a payment decision (auto-pay suggested).</summary>
public sealed record CastSpell(CardId Card) : PlayerAction;

/// <summary>
/// Activate a mana ability, e.g. tap a land for mana (rule 605). Mana abilities don't use the stack;
/// the mana floats in the pool until spent or until the step ends.
/// </summary>
public sealed record ActivateManaAbility(CardId Source, Mana.ManaType Type) : PlayerAction;
