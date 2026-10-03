// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
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

    /// <summary>Pick one target per requirement from the legal choices; null cancels (only if allowed).</summary>
    Task<IReadOnlyList<Target>?> ChooseTargetsAsync(GameView view, TargetRequest request);

    Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders);

    Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request);

    /// <summary>Divide an attacker's combat damage among its blockers (and the player, with trample).</summary>
    Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request);

    Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request);

    Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count);

    /// <summary>Pick cards among the request's options (scry, surveil, search...); the engine checks the count.</summary>
    Task<IReadOnlyList<CardId>> ChooseCardsAsync(GameView view, CardChoiceRequest request);

    /// <summary>Pick modes of a modal spell or ability; null cancels (only if allowed).</summary>
    Task<IReadOnlyList<int>?> ChooseModesAsync(GameView view, ModeRequest request);

    /// <summary>Choose a number between the request's bounds (the value of X, for example).</summary>
    Task<int> ChooseNumberAsync(GameView view, NumberRequest request);

    /// <summary>Pick one of several named options (a color, a creature type...); returns its index.</summary>
    Task<int> ChooseOptionAsync(GameView view, OptionRequest request);
}

public enum OptionKind { Color, CreatureType, Other }

/// <summary>Choose one of <see cref="Options"/>.</summary>
public sealed record OptionRequest(string Prompt, CardId? Source, IReadOnlyList<string> Options, OptionKind Kind);

/// <summary>Choose a number from <see cref="Min"/> to <see cref="Max"/>.</summary>
public sealed record NumberRequest(string Prompt, CardId? Source, int Min, int Max);

public sealed class InvalidDecisionException(string message) : Exception(message);
