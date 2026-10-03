// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>
/// One mana ability of a permanent: "{T}: Add [Amount] mana of one of [Types]". Mana from it may be restricted to
/// spells matching <see cref="OnlyFor"/> (and, with <see cref="AbilitiesToo"/>, abilities of sources matching it).
/// </summary>
public sealed record ManaOption(IReadOnlyList<ManaType> Types, int Amount = 1, ObjectFilter? OnlyFor = null, bool AbilitiesToo = false);
