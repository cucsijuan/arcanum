// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

public partial class PlayerArea
{
    private TurnGlow? _turnGlow;
    private Color? _borderColor;

    /// <summary>Marks the area of the player whose turn it is: border and glow in that player's color.</summary>
    private void ApplyTurnGlow(bool isActive)
    {
        if (!isActive)
        {
            _turnGlow?.Dim();
            return;
        }
        var color = BoardStyle.PlayerColor(Player.Value);
        if (_turnGlow is null)
        {
            _turnGlow = new TurnGlow();
            AddChild(_turnGlow);
            MoveChild(_turnGlow, _activeBorder.GetIndex());
        }
        _turnGlow.Glow(color);
        if (_borderColor != color)
        {
            _borderColor = color;
            _activeBorder.AddThemeStyleboxOverride("panel", BoardStyle.Box(Colors.Transparent, 4, color, 2));
        }
    }
}
