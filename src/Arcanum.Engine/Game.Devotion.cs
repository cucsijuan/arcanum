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

    /// <summary>The last creature card exiled with this permanent (as the object it is now) that is still in exile ("imprint").</summary>
    private State.Card? LastCreatureExiledWith(State.Card source) =>
        State.Cards.Values.Where(c => c.Zone == Arcanum.Engine.State.Zone.Exile && c.ExiledWith is { } with && with.Source == source.Id && with.Version == source.Version
                                      && c.Definition.IsCreature())
            .OrderByDescending(c => c.ExiledWithOrder).FirstOrDefault();
}
