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

    public static Button MakeButton(string text, int size = 16)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", size);
        button.AddThemeColorOverride("font_color", Text);
        button.AddThemeStyleboxOverride("normal", Box(Panel, 6, PanelBorder, 1));
        button.AddThemeStyleboxOverride("hover", Box(new Color("26272c"), 6, TextDim, 1));
        button.AddThemeStyleboxOverride("pressed", Box(new Color("0f1012"), 6, TextDim, 1));
        button.AddThemeStyleboxOverride("disabled", Box(Panel, 6, PanelBorder, 1));
        return button;
    }

    public static Button MakePrimaryButton(string text, int size = 18)
    {
        var button = MakeButton(text, size);
        button.AddThemeColorOverride("font_color", new Color("16171a"));
        button.AddThemeColorOverride("font_hover_color", new Color("16171a"));
        button.AddThemeColorOverride("font_pressed_color", new Color("16171a"));
        button.AddThemeStyleboxOverride("normal", Box(ActiveBorder, 8));
        button.AddThemeStyleboxOverride("hover", Box(ActiveBorder.Lightened(0.15f), 8));
        button.AddThemeStyleboxOverride("pressed", Box(ActiveBorder.Darkened(0.15f), 8));
        return button;
    }

    public static readonly (string Id, string Label)[] CardBacks =
    {
        ("arcane", "Arcane (violet)"), ("tide", "Tide (blue)"), ("ember", "Ember (red)"), ("grove", "Grove (green)"), ("obsidian", "Obsidian (black)"),
    };

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
