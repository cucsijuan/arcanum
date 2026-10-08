// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>Shared colors, sizes and style helpers for the game board.</summary>
public static class BoardStyle
{
    public static readonly Color Background = new("1e1f23");
    public static readonly Color Panel = new("16171a");
    public static readonly Color PanelBorder = new("2c2d33");
    public static readonly Color Text = new("e8e8ea");
    public static readonly Color TextDim = new("a0a1a8");
    public static readonly Color ActiveBorder = new("f5c518");
    public static readonly Color Playable = new("f2c14e");   // golden glow: can be played/used
    public static readonly Color SelectedTint = new("2ec4e6"); // selected: border + translucent overlay
    public static readonly Color Attacking = new("ff5a3c");
    public static readonly Color Blocking = new("4c8dff");
    public static readonly Color Selected = new("2ec4e6");
    public static readonly Color Targeting = new("3aa8ff"); // electric blue: arrows from spells and abilities to their targets

    private static readonly Color[] PlayerColors = { new("2ec4b6"), new("e0607e"), new("9b7bff"), new("f2a93b"), new("6fd08c"), new("d98ae0") };

    /// <summary>The color that stands for a seat (turn glow, banner, active-player name).</summary>
    public static Color PlayerColor(int seat) => PlayerColors[(seat % PlayerColors.Length + PlayerColors.Length) % PlayerColors.Length];

    /// <summary>
    /// Drawing order of board layers. Cards use 0–300 (hand, rows, attachments, dragged); everything that explains
    /// what is happening must draw above them.
    /// </summary>
    public static class Z
    {
        public const int Stack = 400;
        public const int Mulligan = 440;
        public const int PhaseBar = 410;
        public const int Arrows = 420;
        public const int ActionPanel = 450;
        public const int Log = 460;
        public const int Announcer = 480;
        public const int Picker = 485;
        public const int Floaters = 500;
        public const int Preview = 550;
        public const int GameOver = 600;
        public const int Menu = 650;
        public const int Loading = 700;
    }

    /// <summary>Multiplier for animation durations (from the animation speed setting).</summary>
    public static float AnimationScale { get; set; } = 1f;

    /// <summary>How strongly foil cards shine (from the foil shine setting), 0–1.</summary>
    public static float FoilShine { get; set; } = 0.45f;

    /// <summary>Selected card back design (see <see cref="CardBackColors"/>).</summary>
    public static string CardBack { get; set; } = "arcane";

    public static readonly Vector2 HandCardSize = new(84, 117);
    public static readonly Vector2 BattlefieldCardSize = new(86, 120);
    public static readonly Vector2 PileCardSize = new(84, 117);
    public static readonly Vector2 PreviewSize = new(375, 523);

    public static StyleBoxFlat Box(Color bg, int radius = 6, Color? border = null, int borderWidth = 0)
    {
        var box = new StyleBoxFlat { BgColor = bg };
        box.SetCornerRadiusAll(radius);
        if (border is { } b)
        {
            box.BorderColor = b;
            box.SetBorderWidthAll(borderWidth);
        }
        box.SetContentMarginAll(4);
        return box;
    }

    /// <summary>
    /// A frame for dialog-like panels: the art frame (tinted by <paramref name="fill"/>, with the accent on the frame when
    /// given) or, without the art, the plain bordered box.
    /// </summary>
    public static StyleBox DialogBox(PanelKind kind, Color plainBg, int radius, Color plainBorder, int borderWidth, Color? fill = null, Color? accent = null, float padding = -1, float artPadding = -1)
    {
        var art = UiArt.Panel(kind, fill, accent, artPadding >= 0 ? artPadding : padding);
        if (art is not null) return art;
        var box = Box(plainBg, radius, plainBorder, borderWidth);
        if (padding >= 0) box.SetContentMarginAll(padding);
        return box;
    }

    private static Font? _titleFont;
    private static bool _titleFontLoaded;

    /// <summary>Display font for titles and headers (null when missing, so labels keep the default font).</summary>
    public static Font? TitleFont
    {
        get
        {
            if (!_titleFontLoaded)
            {
                _titleFontLoaded = true;
                const string path = "res://fonts/lt-museum/LTMuseum-Bold.ttf";
                if (ResourceLoader.Exists(path)) _titleFont = GD.Load<Font>(path);
            }
            return _titleFont;
        }
    }

    /// <summary>A header label in the display font.</summary>
    public static Label MakeTitle(string text, int size = 20, Color? color = null)
    {
        var label = MakeLabel(text, size, color);
        if (TitleFont is { } font) label.AddThemeFontOverride("font", font);
        else
        {
            label.AddThemeConstantOverride("outline_size", 1);
            label.AddThemeColorOverride("font_outline_color", color ?? Text);
        }
        return label;
    }

    public static Label MakeLabel(string text, int size = 14, Color? color = null, bool bold = false)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Text);
        if (bold)
        {
            label.AddThemeConstantOverride("outline_size", 1);
            label.AddThemeColorOverride("font_outline_color", color ?? Text);
        }
        return label;
    }

    /// <param name="plain">The flat look even with the interface art (the phase bar keeps it).</param>
    public static Button MakeButton(string text, int size = 16, bool compact = false, bool plain = false)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", size);
        button.AddThemeColorOverride("font_color", Text);
        if (!plain && UiArt.StyleButton(button, false, compact)) return button;
        button.AddThemeStyleboxOverride("normal", Box(Panel, 6, PanelBorder, 1));
        button.AddThemeStyleboxOverride("hover", Box(new Color("26272c"), 6, TextDim, 1));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("0f1012"), 6, TextDim, 1));
        button.AddThemeStyleboxOverride("disabled", Box(Panel, 6, PanelBorder, 1));
        return button;
    }

    /// <summary>
    /// Raises a button's label by <paramref name="pixels"/>: symbol glyphs (≡, ↶) come from fallback fonts whose
    /// metrics leave them sitting low in the frame.
    /// </summary>
    public static void RaiseLabel(Button button, float pixels)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
        {
            if (!button.HasThemeStyleboxOverride(state)) continue;
            // The label is centered between the margins: moving it up by p takes p off the top and puts it at the bottom
            // (twice what the top can't give, when the top margin runs out).
            var box = button.GetThemeStylebox(state);
            float cut = Math.Min(pixels, Math.Max(0, box.ContentMarginTop));
            box.ContentMarginTop -= cut;
            box.ContentMarginBottom += 2 * pixels - cut;
        }
    }

    /// <summary>One of a row of exclusive choices (game mode, event kind): the chosen one is highlighted.</summary>
    public static void StyleChoice(Button button, bool chosen)
    {
        if (UiArt.StyleButton(button, chosen)) return;
        var style = chosen ? Box(ActiveBorder, 8) : Box(Panel, 6, PanelBorder, 1);
        button.AddThemeStyleboxOverride("normal", style);
        button.AddThemeStyleboxOverride("hover", style);
        button.AddThemeColorOverride("font_color", chosen ? new Color("16171a") : Text);
        button.AddThemeColorOverride("font_hover_color", chosen ? new Color("16171a") : Text);
    }

    /// <summary>An answer in a modal dialog: text without a frame; the default answer is set in gold.</summary>
    public static Button MakeModalButton(string text, bool primary = false, int size = 18)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", size);
        var color = primary ? UiArt.Gold : new Color(0.86f, 0.87f, 0.9f);
        button.AddThemeColorOverride("font_color", color);
        button.AddThemeColorOverride("font_hover_color", primary ? color.Lightened(0.3f) : Colors.White);
        button.AddThemeColorOverride("font_pressed_color", color.Darkened(0.2f));
        button.AddThemeColorOverride("font_hover_pressed_color", color.Darkened(0.2f));
        button.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.5f, 0.55f));
        StyleBox Back(float alpha)
        {
            var box = Box(new Color(1, 1, 1, alpha), 4);
            box.ContentMarginLeft = box.ContentMarginRight = 14;
            box.ContentMarginTop = box.ContentMarginBottom = 4;
            return box;
        }
        button.AddThemeStyleboxOverride("normal", Back(0));
        button.AddThemeStyleboxOverride("disabled", Back(0));
        button.AddThemeStyleboxOverride("hover", Back(0.06f));
        button.AddThemeStyleboxOverride("pressed", Back(0.03f));
        button.AddThemeStyleboxOverride("hover_pressed", Back(0.03f));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return button;
    }

    /// <summary>The thin gold line between a modal's answers.</summary>
    public static Control MakeModalSeparator(float height = 22) => new ColorRect
    {
        Color = UiArt.Gold with { A = 0.6f },
        CustomMinimumSize = new Vector2(1.5f, height),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    public static Button MakePrimaryButton(string text, int size = 18)
    {
        var button = MakeButton(text, size);
        button.AddThemeColorOverride("font_color", new Color("16171a"));
        button.AddThemeColorOverride("font_hover_color", new Color("16171a"));
        button.AddThemeColorOverride("font_pressed_color", new Color("16171a"));
        if (UiArt.StyleButton(button, true)) return button;
        button.AddThemeStyleboxOverride("normal", Box(ActiveBorder, 8));
        button.AddThemeStyleboxOverride("hover", Box(ActiveBorder.Lightened(0.15f), 8));
        button.AddThemeStyleboxOverride("pressed", Box(ActiveBorder.Darkened(0.15f), 8));
        return button;
    }

    private static readonly (string Id, string Label)[] BuiltInCardBacks =
    {
        ("arcane", "Arcane (violet)"), ("tide", "Tide (blue)"), ("ember", "Ember (red)"), ("grove", "Grove (green)"), ("obsidian", "Obsidian (black)"),
    };

    /// <summary>Marks a card back design offered by the content module ("module:id"), drawn from its downloaded picture.</summary>
    private const string ModuleCardBackPrefix = "module:";

    private static (string Id, string Label)[] _moduleCardBacks = Array.Empty<(string, string)>();

    /// <summary>Every card back design: the built-in ones, then those the content module offers.</summary>
    public static IReadOnlyList<(string Id, string Label)> CardBacks => BuiltInCardBacks.Concat(_moduleCardBacks).ToList();

    /// <summary>Adds the content module's card back designs to the choices.</summary>
    public static void SetModuleCardBacks(IEnumerable<Arcanum.Data.Modules.CardBackSource> backs) =>
        _moduleCardBacks = backs.Select(b => (ModuleCardBackPrefix + b.Id, b.Name)).ToArray();

    /// <summary>
    /// Image key of the picture of a card back design offered by the module (by its address, so a new picture replaces
    /// the cached one), or null for a built-in design (drawn by a shader) or one the module no longer offers. Until the
    /// picture arrives, or if it can't be downloaded, the card back shows the default built-in design.
    /// </summary>
    public static string? CardBackImageKey(string id) =>
        id.StartsWith(ModuleCardBackPrefix) && Arcanum.Client.App.Instance?.Module?.CardBackUrl(id[ModuleCardBackPrefix.Length..]) is { } url
            ? Arcanum.Client.CardImageCache.UrlKey(url)
            : null;

    /// <summary>Background, glow and accent colors of a card back design.</summary>
    public static (Color A, Color B, Color Accent) CardBackColors(string id) => id switch
    {
        "tide" => (new Color("0b1a33"), new Color("1d4e80"), new Color("9fd3ff")),
        "ember" => (new Color("2a0b0b"), new Color("7a2416"), new Color("ffbf6b")),
        "grove" => (new Color("0b2414"), new Color("2b6a3a"), new Color("d8e8a0")),
        "obsidian" => (new Color("0c0c0f"), new Color("2b2b33"), new Color("c8c8d0")),
        _ => (new Color(0.13f, 0.09f, 0.22f), new Color(0.30f, 0.18f, 0.45f), new Color(0.85f, 0.70f, 0.35f)),
    };

    public static readonly (string Id, string Label)[] Playmats =
    {
        ("grid", "Grid (default)"), ("slate", "Slate"), ("felt", "Green felt"), ("ocean", "Deep ocean"), ("crimson", "Crimson"), ("nebula", "Nebula"),
    };

    /// <summary>Grid playmat colors for a built-in playmat, or null for the nebula shader.</summary>
    public static (Color Base, Color Line)? PlaymatColors(string id) => id switch
    {
        "grid" => (new Color(0.125f, 0.129f, 0.145f), new Color(0.165f, 0.169f, 0.188f)),
        "slate" => (new Color("2a2e35"), new Color("2f343c")),
        "felt" => (new Color("143a26"), new Color("18432c")),
        "ocean" => (new Color("0e2236"), new Color("132b43")),
        "crimson" => (new Color("2e1215"), new Color("37171b")),
        _ => null,
    };

    /// <summary>Color for a mana symbol badge, as in the hand cost pips.</summary>
    public static (Color Bg, Color Fg) PipColors(char symbol) => symbol switch
    {
        'W' => (new Color("f8f3d8"), new Color("222")),
        'U' => (new Color("4ea4e6"), new Color("fff")),
        'B' => (new Color("8a8090"), new Color("fff")),
        'R' => (new Color("e8573e"), new Color("fff")),
        'G' => (new Color("2fa45a"), new Color("fff")),
        _ => (new Color("c9c6c0"), new Color("222")),
    };

    /// <summary>Round mana symbol badge ("2", "G", ...).</summary>
    public static Panel MakePip(string symbol, float diameter = 14, int fontSize = 9)
    {
        var (bg, fg) = PipColors(symbol.Length == 1 ? symbol[0] : '0');
        var pip = new Panel { CustomMinimumSize = new Vector2(diameter, diameter), MouseFilter = Control.MouseFilterEnum.Ignore };
        var box = Box(bg, (int)(diameter / 2), new Color("111"), 1);
        box.SetContentMarginAll(0);
        pip.AddThemeStyleboxOverride("panel", box);
        var label = MakeLabel(symbol, fontSize, fg);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        pip.AddChild(label);
        return pip;
    }

    /// <summary>A row of pips for a cost, e.g. "{1}{G}" → (1)(G).</summary>
    public static HBoxContainer MakeCostRow(string cost, float diameter = 22, int fontSize = 13)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 3);
        foreach (var symbol in ParseCostSymbols(cost)) row.AddChild(MakePip(symbol, diameter, fontSize));
        return row;
    }

    /// <summary>Splits "{2}{B}{B}" into ["2", "B", "B"].</summary>
    public static IEnumerable<string> ParseCostSymbols(string? cost)
    {
        if (string.IsNullOrEmpty(cost) || cost == "{0}") yield break;
        int i = 0;
        while ((i = cost.IndexOf('{', i)) >= 0)
        {
            int close = cost.IndexOf('}', i);
            if (close < 0) yield break;
            yield return cost.Substring(i + 1, close - i - 1);
            i = close + 1;
        }
    }
}
