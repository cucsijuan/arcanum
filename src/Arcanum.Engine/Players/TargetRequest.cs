// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// Choose one target for each requirement of a spell or ability. <see cref="Legal"/> lists, per requirement, every
/// legal choice. Casting can be cancelled when <see cref="CanCancel"/> is true (not for triggered abilities).
/// </summary>
public sealed record TargetRequest(
    CardId Source,
    string Text,
    IReadOnlyList<TargetSpec> Specs,
    IReadOnlyList<IReadOnlyList<Target>> Legal,
    bool CanCancel);
