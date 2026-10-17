// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The playmat of a big tournament's table: one mat for both players, split by a band across the middle (white, black,
/// then red, the same on both sides) with the tournament's crest on it (the shield with the event's name in front).
/// </summary>
public partial class TournamentMat : Control
{
    public const string Shield = "res://ui-art/kit/career/tournament-shield.png";
    private const string FontPath = "res://fonts/bebas-neue/BebasNeue-Regular.ttf";

    /// <summary>The table's height the proportions below were drawn for (the table's picture's).</summary>
    private const float Reference = 1536f;
    private const int Radius = 30;
    private static readonly Color Cloth = new(52 / 255f, 52 / 255f, 54 / 255f), Stitch = new(70 / 255f, 70 / 255f, 72 / 255f);

    /// <summary>The band from its top edge down: thickness (at <see cref="Reference"/>) and color.</summary>
    private static readonly (float Size, Color Color)[] Bands =
    {
        (4, new Color("0a080a")), (6, new Color("e8e0e3")), (6, new Color("140a0e")), (4, new Color("78141a")), (32, new Color("f64b4e")),
        (4, new Color("78141a")), (6, new Color("140a0e")), (6, new Color("e8e0e3")), (4, new Color("0a080a")),
    };

    /// <summary>The band's thickness against the crest's.</summary>
    private const float BandScale = 0.42f;

    private readonly Panel _shadow = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Control _band = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _shield = new() { MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
    private readonly Label[] _name = { new(), new() };
    private float _line = 0.5f;

    public TournamentMat()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        var shadow = new StyleBoxFlat { BgColor = Colors.Black, ShadowColor = new Color(0, 0, 0, 0.6f), ShadowSize = 8, ShadowOffset = new Vector2(2, 4) };
        shadow.SetCornerRadiusAll(Radius);
        _shadow.AddThemeStyleboxOverride("panel", shadow);
        _shadow.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_shadow);
        // The cloth's rounded shape clips the band at the mat's edges.
        var cloth = new Panel { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.AndDraw };
        var style = new StyleBoxFlat { BgColor = Cloth, BorderColor = Stitch };
        style.SetCornerRadiusAll(Radius);
        cloth.AddThemeStyleboxOverride("panel", style);
        cloth.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(cloth);
        cloth.AddChild(_band);
        _band.Draw += DrawBand;
        var edge = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var stitched = new StyleBoxFlat { DrawCenter = false, BorderColor = Stitch };
        stitched.SetBorderWidthAll(3);
        stitched.SetCornerRadiusAll(Radius - 4);
        edge.AddThemeStyleboxOverride("panel", stitched);
        edge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        edge.OffsetLeft = edge.OffsetTop = 6;
        edge.OffsetRight = edge.OffsetBottom = -6;
        AddChild(edge);
        if (ResourceLoader.Exists(Shield)) _shield.Texture = GD.Load<Texture2D>(Shield);
        AddChild(_shield);
        var font = ResourceLoader.Exists(FontPath) ? GD.Load<Font>(FontPath) : null;
        foreach (var label in _name)
        {
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            if (font is not null) label.AddThemeFontOverride("font", font);
            label.AddThemeColorOverride("font_color", Colors.White);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            AddChild(label);
        }
        Resized += Layout;
    }

    /// <summary>Shows the event's name on the crest: its first word above, the rest below.</summary>
    public void SetName(string name)
    {
        var words = name.ToUpperInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        _name[0].Text = words.ElementAtOrDefault(0) ?? "";
        _name[1].Text = words.ElementAtOrDefault(1) ?? "";
        Layout();
    }

    /// <summary>Where the band crosses the mat, from its top (in pixels): the line between the two players' halves.</summary>
    public void SetLine(float y)
    {
        _line = y;
        Layout();
    }

    /// <summary>The crest and band keep their size against the table (the board), whatever the mat's.</summary>
    private float Scale => (GetParent() as Control is { Size.Y: > 0 } table ? table.Size.Y : Size.Y) / Reference;

    private void Layout()
    {
        float k = Scale;
        _band.Position = Vector2.Zero;
        _band.Size = Size;
        _band.QueueRedraw();
        // The crest: the shield centered on the band, the name's two lines centered on the shield.
        float height = 286 * k;
        float width = _shield.Texture is { } t ? height * t.GetWidth() / t.GetHeight() : height * 0.8f;
        _shield.Size = new Vector2(width, height);
        _shield.Position = new Vector2((Size.X - width) / 2, _line - height / 2);
        int size = Math.Max(8, (int)(height * 0.30f));
        float step = height * 0.24f;
        for (int i = 0; i < _name.Length; i++)
        {
            var label = _name[i];
            label.AddThemeFontSizeOverride("font_size", size);
            label.AddThemeConstantOverride("outline_size", Math.Max(3, (int)(height * 0.05f)));
            label.Size = new Vector2(Size.X, size * 1.2f);
            label.Position = new Vector2(0, _line + (i - 0.5f) * step - label.Size.Y / 2 + height * 0.02f);
        }
    }

    private void DrawBand()
    {
        float k = Scale * BandScale, total = Bands.Sum(b => b.Size) * k, y = _line - total / 2;
        foreach (var (size, color) in Bands)
        {
            _band.DrawRect(new Rect2(0, y, Size.X, size * k), color);
            y += size * k;
        }
    }
}
