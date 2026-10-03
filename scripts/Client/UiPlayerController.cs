// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;

namespace Arcanum.Client;

/// <summary>Seat controlled by a person on this device. Every decision is routed to the board UI.</summary>
public sealed class UiPlayerController(PlayerId player, DecisionHub hub) : IPlayerController
{
    /// <summary>
    /// Minimal auto-pass: skip priority when passing is the only legal action.
    /// Replaced by the configurable stop system (AutoPassPolicy) in M3.
    /// </summary>
    public bool AutoPassWhenNothingToDo { get; set; } = true;

    /// <summary>
    /// Ask the player to confirm (or change) which sources pay for each spell. When false, the engine's
    /// auto-pay suggestion is used directly. Will become a user setting (M5).
    /// </summary>
    public bool ConfirmManaPayment { get; set; } = true;

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) =>
        hub.Ask(new MulliganDecision { Player = player, MulligansTaken = mulligansTaken });

    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
        hub.Ask(new SelectCardsDecision { Player = player, Count = count, Reason = SelectCardsReason.MulliganBottom });

    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions)
    {
        // Tapping lands for floating mana is always possible, but on its own it is no reason to stop.
        if (AutoPassWhenNothingToDo && legalActions.All(a => a is PassPriority or ActivateManaAbility))
            return Task.FromResult<PlayerAction>(PassPriority.Instance);
        return hub.Ask(new PriorityDecision { Player = player, Legal = legalActions });
    }

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request)
    {
        if (!ConfirmManaPayment) return Task.FromResult<IReadOnlyList<ManaTap>?>(request.SuggestedTaps);
        return hub.Ask(new ManaPaymentDecision { Player = player, Request = request });
    }

    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        hub.Ask(new AttackDecision { Player = player, PossibleAttackers = possibleAttackers, Defenders = defenders });

    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(
        GameView view, IReadOnlyList<CardId> possibleBlockers, IReadOnlyList<CardId> attackers) =>
        hub.Ask(new BlockDecision { Player = player, PossibleBlockers = possibleBlockers, Attackers = attackers });

    public Task<IReadOnlyDictionary<CardId, int>> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        hub.Ask(new DamageAssignmentDecision { Player = player, Request = request });

    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) =>
        hub.Ask(new SelectCardsDecision { Player = player, Count = count, Reason = SelectCardsReason.Discard });
}
