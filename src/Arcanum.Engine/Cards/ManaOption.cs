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

    /// <summary>Its types are the colors among permanents its controller controls ("for each color among permanents you control").</summary>
    public bool ColorsAmongYourPermanents { get; init; }

    /// <summary>How many mana one activation adds.</summary>
    public int Produces => OneOfEach ? Types.Count : Amount;
}
