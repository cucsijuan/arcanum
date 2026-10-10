// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// The career's own look, apart from the rest of the game's menus: a pixel font, the store's website on the room's
/// computer (an old browser window) and paper notes over the store's picture.
/// </summary>
public static class CareerStyle
{
    public const string FontPath = "res://fonts/pixelify-sans/PixelifySans.ttf";

    public static readonly Color Ink = new("1d1b19");
    public static readonly Color InkDim = new("5c564d");
    public static readonly Color Link = new("1f4fbf");
    public static readonly Color Good = new("1e7a3c");
    public static readonly Color Bad = new("b3261e");
    public static readonly Color Page = new("f4f1ea");
    public static readonly Color TitleBar = new("24345e");
    public static readonly Color Chrome = new("c9c2b3");
    public static readonly Color Paper = new("f1e6cc");
    public static readonly Color PaperEdge = new("6b4a2b");
    public static readonly Color Gold = new(1f, 0.82f, 0.42f);

    private static Font? _font;
    private static Theme? _web, _paper;

    public static Font Font => _font ??= ResourceLoader.Exists(FontPath) ? GD.Load<Font>(FontPath) : ThemeDB.FallbackFont;

    public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 0, int radius = 0, float margin = 0) => new()
    {
        BgColor = bg,
        BorderColor = border ?? bg,
        BorderWidthLeft = borderWidth, BorderWidthTop = borderWidth, BorderWidthRight = borderWidth, BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin, ContentMarginBottom = margin,
    };

    /// <summary>An old raised button: light top-left edge, dark bottom-right edge.</summary>
    private static StyleBoxFlat Bevel(Color face, bool pressed) => new()
    {
        BgColor = face,
        BorderColor = pressed ? face.Darkened(0.45f) : face.Lightened(0.5f),
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        ShadowColor = new Color(0, 0, 0, pressed ? 0 : 0.35f), ShadowOffset = new Vector2(2, 2), ShadowSize = 0,
        ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    private static Theme Make(Color text, Color face, Color fieldBg)
    {
        var theme = new Theme { DefaultFont = Font, DefaultFontSize = 22 };
        theme.SetColor("font_color", "Label", text);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Bevel(face, false));
            theme.SetStylebox("hover", type, Bevel(face.Lightened(0.12f), false));
            theme.SetStylebox("pressed", type, Bevel(face.Darkened(0.1f), true));
            theme.SetStylebox("disabled", type, Bevel(face.Lerp(new Color(0.75f, 0.75f, 0.75f), 0.6f), false));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, text);
            theme.SetColor("font_hover_color", type, text);
            theme.SetColor("font_pressed_color", type, text);
            theme.SetColor("font_focus_color", type, text);
            theme.SetColor("font_disabled_color", type, text with { A = 0.45f });
        }
        theme.SetStylebox("normal", "LineEdit", Box(fieldBg, InkDim, 2, 0, 6));
        theme.SetStylebox("focus", "LineEdit", Box(fieldBg, Link, 2, 0, 6));
        theme.SetColor("font_color", "LineEdit", Ink);
        theme.SetColor("font_placeholder_color", "LineEdit", InkDim);
        theme.SetColor("font_color", "PopupMenu", Ink);
        theme.SetStylebox("panel", "PopupMenu", Box(fieldBg, InkDim, 2, 0, 6));
        theme.SetStylebox("hover", "PopupMenu", Box(Link with { A = 0.25f }));
        theme.SetColor("font_hover_color", "PopupMenu", Ink);
        theme.SetStylebox("panel", "PanelContainer", Box(Colors.Transparent));
        return theme;
    }

    /// <summary>The store's website: dark text on a light page, grey bevelled buttons.</summary>
    public static Theme Web => _web ??= Make(Ink, new Color("d6d0c4"), Colors.White);

    /// <summary>Notes over the store's picture: ink on paper, wooden buttons.</summary>
    public static Theme Notes => _paper ??= Make(Ink, new Color("e3c88f"), new Color("fbf5e6"));

    public static Label Text(string text, int size = 22, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    /// <summary>A link-looking button (the website's navigation).</summary>
    public static Button LinkButton(string text, bool current = false, int size = 22)
    {
        var button = new Button { Text = text, Flat = true, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", size);
        var color = current ? Ink : Link;
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(name, name == "font_hover_color" && !current ? Link.Lightened(0.25f) : color);
        var underline = current ? Box(Colors.Transparent, Ink, 0) : new StyleBoxFlat { BgColor = Colors.Transparent };
        if (current) { underline.BorderWidthBottom = 3; underline.BorderColor = Ink; }
        foreach (var name in new[] { "normal", "hover", "pressed", "focus" }) button.AddThemeStyleboxOverride(name, underline);
        return button;
    }

    public static Button Button(string text, Action pressed, int size = 22, bool primary = false)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", size);
        if (primary)
        {
            var face = new Color("3d7a4a");
            button.AddThemeStyleboxOverride("normal", Bevel(face, false));
            button.AddThemeStyleboxOverride("hover", Bevel(face.Lightened(0.12f), false));
            button.AddThemeStyleboxOverride("pressed", Bevel(face.Darkened(0.1f), true));
            foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) button.AddThemeColorOverride(name, Colors.White);
        }
        button.Pressed += pressed;
        return button;
    }

    /// <summary>A small dark label with a gold edge, over the art (money, the date, where Back goes).</summary>
    public static PanelContainer Tag(Control content)
    {
        var panel = new PanelContainer();
        var box = Box(new Color(0.08f, 0.06f, 0.05f, 0.82f), Gold, 2, 8);
        box.ContentMarginLeft = box.ContentMarginRight = 14;
        box.ContentMarginTop = box.ContentMarginBottom = 6;
        panel.AddThemeStyleboxOverride("panel", box);
        panel.AddChild(content);
        return panel;
    }
}
