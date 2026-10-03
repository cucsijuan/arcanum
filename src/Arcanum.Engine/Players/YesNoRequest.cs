// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>A yes/no choice, e.g. "Put your commander into the command zone?".</summary>
public sealed record YesNoRequest(string Prompt, CardId? Card = null);
