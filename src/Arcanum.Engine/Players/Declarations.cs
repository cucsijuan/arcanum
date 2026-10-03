// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <param name="Planeswalker">Attack this planeswalker (controlled by <paramref name="Defender"/>) instead of the player.</param>
public sealed record AttackDeclaration(CardId Attacker, PlayerId Defender, CardId? Planeswalker = null);

public sealed record BlockDeclaration(CardId Blocker, CardId Attacker);
