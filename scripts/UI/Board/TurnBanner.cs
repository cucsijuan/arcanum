// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Large banner announcing whose turn begins ("Your turn" / "Ana's turn" over "Turn 5"). It slides in, holds and
/// fades out within the time it is given; the <see cref="Announcer"/> owns the timing, so nothing else is shown
/// until the banner is gone.
/// </summary>
public partial class TurnBanner : Control
{
    private static readonly Vector2 BannerSize = new(720, 136);
    private const float SlideDistance = 90;

    private readonly PanelContainer _panel = new();
    private readonly Label _title = BoardStyle.MakeTitle("", 52, BoardStyle.Text);
    private readonly Label _subtitle = BoardStyle.MakeLabel("", 22, BoardStyle.TextDim);
    private Tween? _tween;

    public TurnBanner()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _panel.MouseFilter = MouseFilterEnum.Ignore;
        _panel.CustomMinimumSize = BannerSize;
        _panel.Size = BannerSize;
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 0);
        foreach (var label in new[] { _title, _subtitle })
        {
            label.HorizontalAlignment = HorizontalAlignment.Center;
            column.AddChild(label);
        }
        _title.AddThemeConstantOverride("outline_size", 0);
        _panel.AddChild(column);
        AddChild(_panel);
    }

    /// <summary>Shows the banner for <paramref name="seconds"/> (already scaled by the animation speed).</summary>
    public void Play(string title, string subtitle, Color accent, double seconds)
    {
        _tween?.Kill();
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.AddThemeColorOverride("font_color", accent.Lerp(Colors.White, 0.35f));
        var art = UiArt.Panel(PanelKind.Board, UiArt.BoardFill.Lerp(accent, 0.4f) with { A = 0.92f }, Colors.White.Lerp(accent, 0.3f), 12);
        if (art is not null) _panel.AddThemeStyleboxOverride("panel", art);
        else
        {
            var box = BoardStyle.Box(new Color(0.05f, 0.05f, 0.07f, 0.88f), 16, accent, 3);
            box.SetContentMarginAll(12);
            box.ShadowColor = new Color(accent, 0.35f);
            box.ShadowSize = 22;
            _panel.AddThemeStyleboxOverride("panel", box);
        }

        double fadeIn = seconds * 0.26, fadeOut = seconds * 0.3;
        var home = -BannerSize / 2;
        _panel.Position = home + new Vector2(-SlideDistance, 0);
        _panel.Modulate = new Color(1, 1, 1, 0);
        Visible = true;
        _tween = CreateTween();
        _tween.SetParallel();
        _tween.TweenProperty(_panel, "position", home, fadeIn).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_panel, "modulate:a", 1f, fadeIn).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        double outAt = seconds - fadeOut;
        _tween.TweenProperty(_panel, "position", home + new Vector2(SlideDistance, 0), fadeOut).SetDelay(outAt)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _tween.TweenProperty(_panel, "modulate:a", 0f, fadeOut).SetDelay(outAt).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        _tween.Chain().TweenCallback(Callable.From(Stop));
    }

    public void Stop()
    {
        _tween?.Kill();
        _tween = null;
        Visible = false;
    }
}
