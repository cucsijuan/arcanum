// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;

namespace Arcanum.Engine;

/// <summary>
/// "Up to N target …" (rule 601.2c, 115.1): the player may choose any number of those targets from zero to N, for spells,
/// activated (including loyalty) and triggered abilities alike. Written as optional targets, or as one target with
/// <c>"upTo": N</c> (N may be X), which becomes N optional, different targets once N is known.
/// </summary>
public sealed partial class Game
{
    /// <summary>A target the player may leave unchosen: optional, or an "up to N" requirement not yet expanded.</summary>
    private static bool IsUpTo(TargetSpec spec) => spec.Optional || spec.RepeatFrom is not null;
}
