// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Players;

/// <summary>A yes/no choice, e.g. "Put your commander into the command zone?".</summary>
public sealed record YesNoRequest(string Prompt, CardId? Card = null)
{
    /// <summary>The answer the asker suggests to the computer (a game mode's question); null: the computer decides.</summary>
    public bool? Suggested { get; init; }
}
