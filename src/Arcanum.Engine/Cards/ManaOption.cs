// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>
/// One mana ability of a permanent: "{T}: Add [Amount] mana of one of [Types]". Mana from it may be restricted to
/// spells matching <see cref="OnlyFor"/> (and, with <see cref="AbilitiesToo"/>, abilities of sources matching it).
/// </summary>
public sealed record ManaOption(IReadOnlyList<ManaType> Types, int Amount = 1, ObjectFilter? OnlyFor = null, bool AbilitiesToo = false)
{
    /// <summary>Adds one mana of each of <see cref="Types"/> at once instead of <see cref="Amount"/> mana of one type.</summary>
    public bool OneOfEach { get; init; }

    /// <summary>"Add two mana in any combination of {U}, {B}, and/or {R}": each of the <see cref="Amount"/> mana is chosen among <see cref="Types"/>.</summary>
    public bool Combination { get; init; }

    /// <summary>Every combination it can add (for a <see cref="Combination"/> ability), as sorted lists of types.</summary>
    public IEnumerable<IReadOnlyList<ManaType>> Combinations()
    {
        var types = Types.Distinct().OrderBy(t => t).ToList();
        IEnumerable<List<ManaType>> From(int start, int left) =>
            left == 0 ? new[] { new List<ManaType>() }
            : Enumerable.Range(start, types.Count - start).SelectMany(i => From(i, left - 1).Select(rest => rest.Prepend(types[i]).ToList()));
        return From(0, Amount);
    }

    /// <summary>Life paid to activate it ("{T}, Pay 1 life: Add {B} or {R}").</summary>
    public int LifeCost { get; init; }

    /// <summary>What happens when its mana is spent (this ability only; otherwise the card's own rider).</summary>
    public Abilities.ManaRider Rider { get; init; }

    /// <summary>Its types are the colors among permanents its controller controls ("for each color among permanents you control").</summary>
    public bool ColorsAmongYourPermanents { get; init; }

    /// <summary>Its types are the colors among legendary creature cards in its controller's graveyard.</summary>
    public bool ColorsAmongLegendaryCreatureCardsInGraveyard { get; init; }

    /// <summary>How many mana one activation adds.</summary>
    public int Produces => OneOfEach ? Types.Count : Amount;
}
