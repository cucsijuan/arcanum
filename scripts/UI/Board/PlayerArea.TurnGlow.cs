// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

public partial class PlayerArea
{
    private TurnGlow? _turnGlow;
    private Color? _borderColor;
    private bool _openEdge;

    /// <summary>
    /// The border leaves out its edge along the line between the players (a tournament mat's band marks that line, and
    /// its crest sits on it).
    /// </summary>
    public bool OpenEdge
    {
        get => _openEdge;
        set
        {
            if (_openEdge == value) return;
            _openEdge = value;
            _activeBorder.AddThemeStyleboxOverride("panel", Border(_borderColor ?? BoardStyle.ActiveBorder));
            if (_turnGlow is not null) _turnGlow.Open = MiddleSide;
        }
    }

    /// <summary>The turn border in a color, without its edge on the line between the players when <see cref="OpenEdge"/>.</summary>
    private StyleBoxFlat Border(Color color)
    {
        var box = BoardStyle.Box(Colors.Transparent, 4, color, 2);
        if (!_openEdge) return box;
        if (MiddleSide == Side.Bottom) box.BorderWidthBottom = 0;
        else box.BorderWidthTop = 0;
        return box;
    }

    /// <summary>With <see cref="OpenEdge"/>, the side along the line between the players: the top half's bottom, the bottom half's top.</summary>
    private Side? MiddleSide => _openEdge ? FacesDown ? Side.Bottom : Side.Top : null;

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
            _turnGlow = new TurnGlow { Open = MiddleSide };
            AddChild(_turnGlow);
            MoveChild(_turnGlow, _activeBorder.GetIndex());
        }
        _turnGlow.Glow(color);
        if (_borderColor != color)
        {
            _borderColor = color;
            _activeBorder.AddThemeStyleboxOverride("panel", Border(color));
        }
    }
}
