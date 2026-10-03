// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

public sealed record AttackDeclaration(CardId Attacker, PlayerId Defender);

public sealed record BlockDeclaration(CardId Blocker, CardId Attacker);
