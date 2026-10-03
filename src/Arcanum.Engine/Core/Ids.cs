// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Core;

/// <summary>Stable identifier of a card object inside one game.</summary>
public readonly record struct CardId(int Value)
{
    public override string ToString() => $"#{Value}";
}

/// <summary>Identifier of a player inside one game (seat index).</summary>
public readonly record struct PlayerId(int Value)
{
    public override string ToString() => $"P{Value}";
}
