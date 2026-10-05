// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Soft glow along the inside edge of a player's area, drawn for as long as it is that player's turn. It breathes
/// slowly so it is noticed without competing with the cards.
/// </summary>
public partial class TurnGlow : Control
{
    private const int Layers = 14;
    private const float Peak = 0.26f;
    private Color _color = Colors.White;
    private Tween? _pulse;

    public TurnGlow()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
        Resized += QueueRedraw;
    }

    public void Glow(Color color)
    {
        if (color != _color)
        {
            _color = color;
            QueueRedraw();
        }
        if (Visible) return;
        Visible = true;
        Modulate = Colors.White;
        _pulse?.Kill();
        _pulse = CreateTween().SetLoops();
        _pulse.TweenProperty(this, "modulate:a", 0.6f, 1.6).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        _pulse.TweenProperty(this, "modulate:a", 1f, 1.6).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    public void Dim()
    {
        _pulse?.Kill();
        _pulse = null;
        Visible = false;
    }

    public override void _Draw()
    {
        for (int i = 0; i < Layers; i++)
        {
            float fade = 1f - i / (float)Layers;
            float inset = i * 2 + 1;
            var rect = new Rect2(inset, inset, Size.X - inset * 2, Size.Y - inset * 2);
            if (rect.Size.X <= 0 || rect.Size.Y <= 0) break;
            DrawRect(rect, new Color(_color, Peak * fade * fade), filled: false, width: 2);
        }
    }
}
