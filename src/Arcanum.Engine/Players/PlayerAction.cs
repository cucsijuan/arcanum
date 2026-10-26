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

/// <summary>Cast a spell (as the card's Adventure when <paramref name="Adventure"/>). Mana is paid through a payment decision (auto-pay suggested).</summary>
public sealed record CastSpell(CardId Card, bool Adventure = false, int? Half = null) : PlayerAction;

/// <summary>
/// Activate a mana ability, e.g. tap a land for mana (rule 605). Mana abilities don't use the stack;
/// the mana floats in the pool until spent or until the step ends.
/// </summary>
/// <summary>Activate the <paramref name="Index"/>-th activated ability of a permanent (rule 602).</summary>
public sealed record ActivateAbility(CardId Source, int Index) : PlayerAction;

/// <summary>
/// Turn a face-down permanent face up, a special action: paying its morph cost (rule 702.37e) or, with <paramref name="ForManaCost"/>,
/// a manifested creature card's mana cost (rule 701.40b).
/// </summary>
public sealed record TurnFaceUp(CardId Card, bool ForManaCost = false) : PlayerAction;

/// <summary>Pay {3} and put your companion from outside the game into your hand (a special action, rules 702.139a, 116.2g).</summary>
public sealed record PutCompanionIntoHand(CardId Card) : PlayerAction;

public sealed record ActivateManaAbility(CardId Source, Mana.ManaType Type, int Option = 0) : PlayerAction;
