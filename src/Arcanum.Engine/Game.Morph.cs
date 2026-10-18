// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// Morph (rule 702.37) and face-down spells and permanents (rule 708): cast face down as a 2/2 creature for {3}, turned face up
/// for its morph cost as a special action any time its controller has priority.
/// </summary>
public sealed partial class Game
{
    /// <summary>What casting a card face down costs (702.37c).</summary>
    private static readonly ManaCost FaceDownCastCost = ManaCost.Parse("{3}");

    /// <summary>Works something out as if the card were face down (its costs, the way a face-down spell is paid for).</summary>
    private T AsFaceDown<T>(Card card, Func<T> work)
    {
        bool before = card.TurnedFaceDown;
        card.TurnedFaceDown = true;
        try { return work(); }
        finally { card.TurnedFaceDown = before; }
    }

    /// <summary>Face-down permanents the player controls that they can turn face up now, paying their morph costs (702.37e, 116.2b).</summary>
    private IEnumerable<PlayerAction> TurnFaceUpActions(PlayerId playerId) =>
        State.PermanentsControlledBy(playerId)
            .Where(c => c.TurnedFaceDown && c.PrintedDefinition.Morph is { } morph
                        && morph.Variants().Any(v => ManaPayment.FindPlan(State, playerId, v) is not null))
            .Select(c => new TurnFaceUp(c.Id));

    /// <summary>
    /// Turning a face-down permanent face up: pay its morph cost, then it has its own characteristics (702.37e). This doesn't use the
    /// stack; the permanent stays the same object, with its counters, damage and status.
    /// </summary>
    private async Task<bool> TurnFaceUpAsync(Player player, TurnFaceUp action)
    {
        var card = State.GetCard(action.Card);
        Require(card.Zone == Zone.Battlefield && card.TurnedFaceDown && card.Controller == player.Id && card.PrintedDefinition.Morph is not null,
            "Only a face-down permanent you control with morph can be turned face up.");
        if (!await PayManaAsync(player, card.Id, card.PrintedDefinition.Morph!, null)) return false;
        card.TurnedFaceDown = false;
        RecomputeContinuousEffects();
        Emit(new TurnedFaceUp(card.Id, player.Id));
        return true;
    }
}
