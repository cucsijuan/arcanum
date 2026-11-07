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

    private readonly StyleBoxFlat _shadow, _cloth;
    private readonly Texture2D? _shieldArt = ResourceLoader.Exists(Shield) ? GD.Load<Texture2D>(Shield) : null;
    private readonly Font _font = ResourceLoader.Exists(FontPath) ? GD.Load<Font>(FontPath) : ThemeDB.FallbackFont;
    private readonly string[] _name = { "", "" };
    private float _line = 0.5f;

    public TournamentMat()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _shadow = new StyleBoxFlat { BgColor = Colors.Black, ShadowColor = new Color(0, 0, 0, 0.6f), ShadowSize = 8, ShadowOffset = new Vector2(2, 4) };
        _shadow.SetCornerRadiusAll(Radius);
        _cloth = new StyleBoxFlat { BgColor = Cloth, BorderColor = Stitch };
        _cloth.SetCornerRadiusAll(Radius);
        var edge = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var stitched = new StyleBoxFlat { DrawCenter = false, BorderColor = Stitch };
        stitched.SetBorderWidthAll(3);
        stitched.SetCornerRadiusAll(Radius - 4);
        edge.AddThemeStyleboxOverride("panel", stitched);
        edge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        edge.OffsetLeft = edge.OffsetTop = 6;
        edge.OffsetRight = edge.OffsetBottom = -6;
        AddChild(edge);
        Resized += QueueRedraw;
    }

    /// <summary>Shows the event's name on the crest: its first word above, the rest below.</summary>
    public void SetName(string name)
    {
        var words = name.ToUpperInvariant().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        _name[0] = words.ElementAtOrDefault(0) ?? "";
        _name[1] = words.ElementAtOrDefault(1) ?? "";
        QueueRedraw();
    }

    /// <summary>Where the band crosses the mat, from its top (in pixels): the line between the two players' halves.</summary>
    public void SetLine(float y)
    {
        _line = y;
        QueueRedraw();
    }

    /// <summary>The crest and band keep their size against the table (the board), whatever the mat's.</summary>
    private float Scale => (GetParent() as Control is { Size.Y: > 0 } table ? table.Size.Y : Size.Y) / Reference;

    /// <summary>
    /// The cloth, the band and the crest, in that order, in one drawing (a cloth that clipped the band as a child drew
    /// it in a pass of its own, over the crest). The band crosses the mat's middle, clear of its rounded corners.
    /// </summary>
    public override void _Draw()
    {
        var all = new Rect2(Vector2.Zero, Size);
        DrawStyleBox(_shadow, all);
        DrawStyleBox(_cloth, all);
        float k = Scale, band = k * BandScale, y = _line - Bands.Sum(b => b.Size) * band / 2;
        foreach (var (size, color) in Bands)
        {
            DrawRect(new Rect2(0, y, Size.X, size * band), color);
            y += size * band;
        }
        // The crest: the shield centered on the band, the name's two lines centered on the shield.
        float height = 286 * k;
        if (_shieldArt is { } art)
        {
            float width = height * art.GetWidth() / art.GetHeight();
            DrawTextureRect(art, new Rect2((Size.X - width) / 2, _line - height / 2, width, height), false);
        }
        int fontSize = Math.Max(8, (int)(height * 0.30f)), outline = Math.Max(3, (int)(height * 0.05f));
        float step = height * 0.24f;
        for (int i = 0; i < _name.Length; i++)
        {
            if (_name[i].Length == 0) continue;
            float middle = _line + (i - 0.5f) * step + height * 0.02f;
            var baseline = new Vector2(0, middle + (_font.GetAscent(fontSize) - _font.GetDescent(fontSize)) / 2);
            DrawStringOutline(_font, baseline, _name[i], HorizontalAlignment.Center, Size.X, fontSize, outline, Colors.Black);
            DrawString(_font, baseline, _name[i], HorizontalAlignment.Center, Size.X, fontSize, Colors.White);
        }
    }
}
