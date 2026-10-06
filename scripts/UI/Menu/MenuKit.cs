// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Shared building blocks for the menu screens so they look consistent.</summary>
public static class MenuKit
{
    /// <summary>Full-screen nebula backdrop with a dark gradient for readability.</summary>
    public static void AddBackdrop(Control screen)
    {
        var nebula = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        nebula.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/nebula_playmat.gdshader") };
        material.SetShaderParameter("rect_size", new Vector2(1920, 1080));
        nebula.Material = material;
        screen.AddChild(nebula);
        var shade = new ColorRect { Color = new Color(0.07f, 0.07f, 0.09f, 0.72f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(shade);
    }

    /// <summary>Top bar with a back button and a title; returns the bar so screens can add actions to it.</summary>
    public static HBoxContainer AddHeader(Control screen, string title, Action? onBack = null)
    {
        var bar = new HBoxContainer { AnchorRight = 1, OffsetLeft = 24, OffsetTop = 20, OffsetRight = -24, OffsetBottom = 76 };
        bar.AddThemeConstantOverride("separation", 16);
        var back = BoardStyle.MakeButton("←  Back", 16);
        back.CustomMinimumSize = new Vector2(120, 44);
        back.Pressed += onBack ?? (() => App.Instance.GoTo(App.MainMenuScene));
        bar.AddChild(back);
        var label = BoardStyle.MakeTitle(title, 32);
        label.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(label);
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        screen.AddChild(bar);
        return bar;
    }

    public static PanelContainer Card(Control content, int padding = 20)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Menu, new Color(0.09f, 0.09f, 0.11f, 0.92f), 12, BoardStyle.PanelBorder, 1, padding: padding));
        panel.AddChild(content);
        return panel;
    }

    /// <summary>A section heading in the display font with a divider under it (a plain line without the art).</summary>
    public static Label SectionTitle(string text)
    {
        var label = BoardStyle.MakeTitle(text, 20);
        const float dividerHeight = 20;
        label.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginBottom = dividerHeight + 4 });
        var divider = UiArt.Divider(dividerHeight);
        divider.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        divider.OffsetTop = -dividerHeight;
        divider.OffsetBottom = 0;
        label.AddChild(divider);
        return label;
    }

    public static Label Hint(string text)
    {
        var label = BoardStyle.MakeLabel(text, 13, BoardStyle.TextDim);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // A wrapping label has no minimum width; expanding keeps it from collapsing inside rows.
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    public static OptionButton Options(IEnumerable<string> items, int selected = 0)
    {
        var option = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 40) };
        option.AddThemeFontSizeOverride("font_size", 15);
        // Room for the drop-down arrow inside the frame's right corner.
        if (UiArt.StyleButton(option, false)) option.AddThemeConstantOverride("arrow_margin", 22);
        else
        {
            option.AddThemeStyleboxOverride("normal", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
            option.AddThemeStyleboxOverride("hover", BoardStyle.Box(new Color("26272c"), 6, BoardStyle.TextDim, 1));
        }
        foreach (var item in items) option.AddItem(item);
        if (option.ItemCount > 0) option.Selected = Math.Clamp(selected, 0, option.ItemCount - 1);
        return option;
    }

    public static LineEdit TextField(string text = "", string placeholder = "")
    {
        var field = new LineEdit { Text = text, PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 40) };
        field.AddThemeFontSizeOverride("font_size", 15);
        // A dark well with a faint gold edge (gold when typing in it).
        var well = UiArt.Available ? UiArt.Well : new Color("101114");
        var edge = UiArt.Available ? UiArt.Gold with { A = 0.3f } : BoardStyle.PanelBorder;
        var normal = BoardStyle.Box(well, 4, edge, 1);
        normal.ContentMarginLeft = normal.ContentMarginRight = 10;
        var focus = (StyleBoxFlat)normal.Duplicate();
        focus.BorderColor = UiArt.Available ? UiArt.Gold : BoardStyle.Playable;
        field.AddThemeStyleboxOverride("normal", normal);
        field.AddThemeStyleboxOverride("focus", focus);
        return field;
    }

    public static CheckButton Toggle(string text, bool on)
    {
        var toggle = new CheckButton { Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };
        toggle.AddThemeFontSizeOverride("font_size", 15);
        return toggle;
    }

    /// <summary>Compact on/off button that stays readable on dark backgrounds (unlike the default check box).</summary>
    public static Button Check(bool on, Color onColor, string tooltip = "")
    {
        var button = new Button { ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 30), TooltipText = tooltip };
        void Style()
        {
            bool pressed = button.ButtonPressed;
            button.Text = pressed ? "✓" : "";
            var box = UiArt.ButtonFill(pressed ? onColor : UiArt.Well, compact: true)
                ?? BoardStyle.Box(pressed ? onColor : new Color("1c1d21"), 6, pressed ? onColor : BoardStyle.TextDim with { A = 0.5f }, 1);
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" }) button.AddThemeStyleboxOverride(state, box);
            button.AddThemeColorOverride("font_color", new Color("16171a"));
            button.AddThemeColorOverride("font_pressed_color", new Color("16171a"));
            button.AddThemeColorOverride("font_hover_pressed_color", new Color("16171a"));
        }
        Style();
        button.Toggled += _ => Style();
        return button;
    }

    public static HSlider Slider(double min, double max, double step, double value)
    {
        return new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(260, 28), FocusMode = Control.FocusModeEnum.None };
    }

    /// <summary>A label + control row for forms.</summary>
    public static HBoxContainer Row(string label, Control control, float labelWidth = 220)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var l = BoardStyle.MakeLabel(label, 15);
        l.CustomMinimumSize = new Vector2(labelWidth, 0);
        l.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(l);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        return row;
    }

    /// <summary>Short message that fades out at the bottom of the screen.</summary>
    public static void Toast(Control screen, string text)
    {
        var label = BoardStyle.MakeLabel(text, 16);
        var panel = Card(label, 12);
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 1; panel.AnchorBottom = 1;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.OffsetBottom = -40;
        panel.ZIndex = 500;
        screen.AddChild(panel);
        var tween = panel.CreateTween();
        tween.TweenInterval(1.8);
        tween.TweenProperty(panel, "modulate:a", 0.0f, 0.5);
        tween.TweenCallback(Callable.From(panel.QueueFree));
    }

}
