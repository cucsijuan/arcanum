// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>The one path for tapping and untapping permanents (rule 701.26), so "becomes tapped" triggers and conditions always see it.</summary>
public sealed partial class Game
{
    /// <summary>Taps a permanent that is untapped, announcing it. Nothing happens if it is already tapped.</summary>
    private void Tap(Card card)
    {
        if (card.Tapped) return;
        card.Tapped = true;
        Emit(new PermanentTapped(card.Id));
    }

    /// <summary>Untaps a permanent that is tapped, announcing it. Nothing happens if it is already untapped.</summary>
    private void Untap(Card card)
    {
        if (!card.Tapped) return;
        card.Tapped = false;
        Emit(new PermanentUntapped(card.Id));
    }

    /// <summary>
    /// A permanent that is still choosing "as it enters" (pay life or enter tapped, reveal or enter tapped) enters tapped:
    /// it never becomes tapped, so there is no event (rule 614.1c).
    /// </summary>
    private static void EnterTapped(Card card) => card.Tapped = true;
}
