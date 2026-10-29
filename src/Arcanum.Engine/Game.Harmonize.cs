// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Harmonize (rule 702.180): cast from the graveyard for its harmonize cost, tapping a creature to pay less, then exiled.</summary>
public sealed partial class Game
{
    /// <summary>
    /// The creature the caster taps for harmonize (702.180b), or none: up to one untapped creature they control, among those that
    /// leave the spell payable; one has to be tapped when the spell can't be paid for without it.
    /// </summary>
    private async Task<CardId?> ChooseHarmonizeTapAsync(PlayerId player, Card card, CastingChoices choices)
    {
        var untapped = choices with { HarmonizeTapped = null };
        var candidates = HarmonizeTaps(card, player, untapped);
        if (candidates.Count == 0) return null;
        int min = CanPayToCast(card, player, untapped) ? 0 : 1;
        var picked = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
            $"Harmonize: you may tap an untapped creature you control ({card.Name} costs {{X}} less, X its power)", card.Id,
            candidates.Select(id => ViewBuilder.Card(State, id, player)).ToList(), min, 1, CardChoicePurpose.Sacrifice));
        Require(picked.Count >= min && picked.Count <= 1 && picked.All(candidates.Contains), "Choose an untapped creature you control.");
        return picked.Count == 0 ? null : picked[0];
    }
}
