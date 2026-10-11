// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>
    /// A player's devotion to one or more colors (rule 700.5): the mana symbols among the mana costs of the permanents they control
    /// that are of those colors; a hybrid symbol counts once if either of its halves is.
    /// </summary>
    private int DevotionTo(PlayerId player, IReadOnlyCollection<string> colors)
    {
        var types = colors.Select(c => ManaTypeExtensions.TryParse(c[0], out var t) ? t : (ManaType?)null).OfType<ManaType>().ToHashSet();
        return State.PermanentsControlledBy(player).Sum(card =>
        {
            var cost = card.Definition.ManaCost;
            return cost.Pips.Count(types.Contains) + cost.Hybrid.Count(h => types.Contains(h.First) || types.Contains(h.Second));
        });
    }
}
