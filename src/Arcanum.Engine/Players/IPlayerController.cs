// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Players;

/// <summary>
/// Source of decisions for one seat: the local UI, a computer opponent or a remote client. The engine validates
/// every answer, so a controller can never put the game into an illegal state.
/// Decisions are async so a UI can await player input without blocking.
/// </summary>
public interface IPlayerController
{
    Task<bool> KeepHandAsync(GameView view, int mulligansTaken);

    /// <summary>London mulligan: choose <paramref name="count"/> cards from hand to put on the bottom.</summary>
    Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count);

    /// <summary>Choose one of <paramref name="legalActions"/> (always contains <see cref="PassPriority"/>).</summary>
    Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions);

    /// <summary>
    /// Choose which sources to tap to pay for a spell. Must pay <see cref="ManaPaymentRequest.RemainingAfterPool"/>
    /// exactly (no surplus taps). Return null to cancel casting; the card stays in hand.
    /// </summary>
    Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request);

    Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders);

    Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(
        GameView view, IReadOnlyList<CardId> possibleBlockers, IReadOnlyList<CardId> attackers);

    /// <summary>Divide an attacker's combat damage among its blockers. Amounts must add up to the attacker's power.</summary>
    Task<IReadOnlyDictionary<CardId, int>> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request);

    Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count);
}

public sealed class InvalidDecisionException(string message) : Exception(message);
