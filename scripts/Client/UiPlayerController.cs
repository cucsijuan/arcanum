// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;

namespace Arcanum.Client;

/// <summary>
/// Seat controlled by a person on this device. Decisions go to the board UI unless the stop policy passes
/// priority automatically; every answer is recorded for undo.
/// </summary>
/// <param name="presentation">Waits until the board has finished showing what just happened (opponents' actions).</param>
public sealed class UiPlayerController(PlayerId player, DecisionHub hub, DecisionLog log, AutoPassPolicy policy, Func<Task> presentation) : IPlayerController
{
    /// <summary>
    /// Ask the player to confirm (or change) which sources pay for each spell. When false, the engine's
    /// auto-pay suggestion is used directly (the "Confirm mana payment" setting).
    /// </summary>
    public bool ConfirmManaPayment { get; set; } = true;

    private Task<T> Ask<T>(Decision<T> decision) =>
        log.Record(async () => (await hub.Ask(decision), true));

    public Task<bool> KeepHandAsync(GameView view, int mulligansTaken) =>
        Ask(new MulliganDecision { Player = player, MulligansTaken = mulligansTaken });

    public Task<IReadOnlyList<CardId>> ChooseCardsToBottomAsync(GameView view, int count) =>
        Ask(new SelectCardsDecision { Player = player, Count = count, Reason = SelectCardsReason.MulliganBottom });

    public Task<PlayerAction> ChooseActionAsync(GameView view, IReadOnlyList<PlayerAction> legalActions) =>
        log.Record(async () =>
        {
            if (policy.ShouldAutoPass(view, legalActions))
            {
                // Passing automatically still waits for announcements, so an opponent's play can't rush by unseen.
                await presentation();
                return ((PlayerAction)PassPriority.Instance, false);
            }
            return (await hub.Ask(new PriorityDecision { Player = player, Legal = legalActions }), true);
        });

    public Task<IReadOnlyList<ManaTap>?> ChooseManaPaymentAsync(GameView view, ManaPaymentRequest request) =>
        log.Record(async () =>
        {
            if (!ConfirmManaPayment) return ((IReadOnlyList<ManaTap>?)request.SuggestedTaps, false);
            return (await hub.Ask(new ManaPaymentDecision { Player = player, Request = request }), true);
        });

    public Task<IReadOnlyList<Arcanum.Engine.Abilities.Target>?> ChooseTargetsAsync(GameView view, TargetRequest request) =>
        Ask(new TargetDecision { Player = player, Request = request });

    public Task<IReadOnlyList<AttackDeclaration>> DeclareAttackersAsync(
        GameView view, IReadOnlyList<CardId> possibleAttackers, IReadOnlyList<PlayerId> defenders) =>
        Ask(new AttackDecision { Player = player, PossibleAttackers = possibleAttackers, Defenders = defenders });

    public Task<IReadOnlyList<BlockDeclaration>> DeclareBlockersAsync(GameView view, BlockRequest request) =>
        Ask(new BlockDecision { Player = player, Request = request });

    public Task<DamageAssignment> AssignCombatDamageAsync(GameView view, DamageAssignmentRequest request) =>
        Ask(new DamageAssignmentDecision { Player = player, Request = request });

    public Task<bool> ChooseYesNoAsync(GameView view, YesNoRequest request) =>
        Ask(new YesNoDecision { Player = player, Request = request });

    public Task<IReadOnlyList<CardId>> ChooseDiscardAsync(GameView view, int count) =>
        Ask(new SelectCardsDecision { Player = player, Count = count, Reason = SelectCardsReason.Discard });
}
