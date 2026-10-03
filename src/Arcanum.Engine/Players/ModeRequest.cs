// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>
/// Choose between <see cref="Min"/> and <see cref="Max"/> modes of a modal spell or ability ("Choose one —"),
/// among <see cref="Possible"/> (indices into <see cref="Modes"/>; modes without legal targets are left out).
/// </summary>
public sealed record ModeRequest(CardId Source, string Text, IReadOnlyList<string> Modes, IReadOnlyList<int> Possible, int Min, int Max, bool CanCancel);
