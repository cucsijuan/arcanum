// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>
/// "[Creature] attacks [planeswalker] during its controller's next turn if able": the creature and the planeswalker as the
/// objects they were, and the player whose next turn it is. <see cref="ActiveTurn"/> is that turn once it has begun.
/// </summary>
public sealed record AttackPlaneswalkerRequirement(CardId Card, int Version, CardId Planeswalker, int PlaneswalkerVersion, PlayerId Player)
{
    public int? ActiveTurn { get; set; }
}

public sealed partial class GameState
{
    /// <summary>Requirements to attack a given planeswalker during a player's next turn.</summary>
    public List<AttackPlaneswalkerRequirement> AttackPlaneswalkerRequirements { get; } = new();
}
