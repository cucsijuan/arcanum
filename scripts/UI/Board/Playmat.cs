// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Draws a playmat setting onto a shader surface plus an optional picture: built-in styles are procedural
/// (grid colors or nebula) or a picture from the interface art (<see cref="BoardStyle.PicturePlaymats"/>);
/// "custom:&lt;path&gt;" shows a user image from this device; <see cref="Clear"/> shows nothing (a career table under the area).
/// </summary>
public static class Playmat
{
    private static Shader? _grid, _nebula;

    /// <summary>No playmat: what is under the area shows through (a career table).</summary>
    public const string Clear = "clear";

    /// <summary>
    /// Where a picture playmat's picture is cut when its area is wider than it (0 keeps the top, 0.5 the middle), unless
    /// kit/mats/mats.json gives the playmat its own: a little above the middle, so the figures' heads stay and the
    /// ground goes under the hand.
    /// </summary>
    public const float PictureFocus = 0.3f;

    private static Dictionary<string, float>? _focus;

    /// <summary>Where a picture playmat's picture is cut: its "focus" in kit/mats/mats.json, else <see cref="PictureFocus"/>.</summary>
    public static float FocusOf(string id)
    {
        if (_focus is null)
        {
            _focus = new Dictionary<string, float>();
            const string path = "res://ui-art/kit/mats/mats.json";
            if (Godot.FileAccess.FileExists(path))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path),
                        new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.TryGetProperty("focus", out var focus))
                        foreach (var mat in focus.EnumerateObject()) _focus[mat.Name] = Math.Clamp(mat.Value.GetSingle(), 0f, 1f);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
                {
                    GD.PushWarning($"{path}: {e.Message}");
                }
            }
        }
        return _focus.GetValueOrDefault(id, PictureFocus);
    }

    /// <summary>
    /// Shows a texture filling a picture without stretching it, like <see cref="TextureRect.StretchModeEnum.KeepAspectCovered"/>,
    /// but cut at <paramref name="focusY"/> (0 keeps the top, 0.5 the middle) when the picture's area is wider than the texture;
    /// a narrower area is cut equally on both sides. Kept when the picture is resized.
    /// </summary>
    public static void Cover(TextureRect picture, Texture2D texture, float focusY)
    {
        picture.SetMeta(CoverSource, texture);
        picture.SetMeta(CoverFocus, focusY);
        picture.StretchMode = TextureRect.StretchModeEnum.Scale;
        if (!picture.HasMeta(CoverHooked))
        {
            picture.SetMeta(CoverHooked, true);
            picture.Resized += () => Fit(picture);
        }
        Fit(picture);
    }

    /// <summary>Takes a picture's texture away (and what <see cref="Cover"/> kept for it).</summary>
    public static void Uncover(TextureRect picture)
    {
        picture.RemoveMeta(CoverSource);
        picture.Texture = null;
    }

    private const string CoverSource = "playmat_cover", CoverFocus = "playmat_cover_focus", CoverHooked = "playmat_cover_hooked";

    private static void Fit(TextureRect picture)
    {
        if (!picture.HasMeta(CoverSource) || picture.GetMeta(CoverSource).As<Texture2D>() is not { } texture) return;
        var size = picture.Size;
        var full = texture.GetSize();
        if (size.X <= 0 || size.Y <= 0 || full.X <= 0 || full.Y <= 0)
        {
            picture.Texture = texture;
            return;
        }
        Rect2 region;
        if (size.X / size.Y > full.X / full.Y)
        {
            float height = full.X * size.Y / size.X;
            region = new Rect2(0, (full.Y - height) * picture.GetMeta(CoverFocus).AsSingle(), full.X, height);
        }
        else
        {
            float width = full.Y * size.X / size.Y;
            region = new Rect2((full.X - width) / 2, 0, width, full.Y);
        }
        if (picture.Texture is AtlasTexture atlas && atlas.Atlas == texture) atlas.Region = region;
        else picture.Texture = new AtlasTexture { Atlas = texture, Region = region };
    }

    /// <summary>The picture of a picture playmat, or null (another playmat, or the interface art is missing).</summary>
    public static string? Picture(string id)
    {
        if (!BoardStyle.PicturePlaymats.Contains(id)) return null;
        var path = $"res://ui-art/kit/mats/{id}.jpg";
        return ResourceLoader.Exists(path) ? path : null;
    }

    public static void Apply(ColorRect surface, TextureRect picture, string id, int seed)
    {
        surface.Visible = id != Clear;
        _grid ??= GD.Load<Shader>("res://shaders/grid_playmat.gdshader");
        _nebula ??= GD.Load<Shader>("res://shaders/nebula_playmat.gdshader");
        Uncover(picture);
        picture.Visible = false;
        picture.Modulate = new Color(1, 1, 1, 0.55f); // keep cards readable over busy art
        if (id.StartsWith("custom:"))
        {
            var image = Image.LoadFromFile(id[7..]);
            if (image is not null && !image.IsEmpty())
            {
                picture.Texture = ImageTexture.CreateFromImage(image);
                picture.Visible = true;
            }
            id = "grid";
        }
        else if (BoardStyle.PicturePlaymats.Contains(id))
        {
            if (Picture(id) is { } path)
            {
                Cover(picture, GD.Load<Texture2D>(path), FocusOf(id));
                picture.Visible = true;
            }
            id = "grid";
        }
        if (surface.Material is not ShaderMaterial material) surface.Material = material = new ShaderMaterial();
        if (BoardStyle.PlaymatColors(id) is { } colors)
        {
            material.Shader = _grid;
            material.SetShaderParameter("base_color", colors.Base);
            material.SetShaderParameter("line_color", colors.Line);
        }
        else
        {
            material.Shader = _nebula;
            material.SetShaderParameter("seed", (float)(seed * 7 + 3));
        }
        material.SetShaderParameter("rect_size", surface.Size == Vector2.Zero ? new Vector2(1920, 540) : surface.Size);
    }
}

/// <summary>Small preview of a playmat setting (settings screen).</summary>
public partial class PlaymatSwatch : Control
{
    private readonly ColorRect _surface = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _picture = new()
    {
        MouseFilter = MouseFilterEnum.Ignore,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
    };
    private string _id = "grid";
    public int Seed { get; set; }

    public PlaymatSwatch()
    {
        ClipContents = true;
        _surface.SetAnchorsPreset(LayoutPreset.FullRect);
        _picture.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_surface);
        AddChild(_picture);
        Resized += () => Playmat.Apply(_surface, _picture, _id, Seed);
    }

    public void SetStyle(string id)
    {
        _id = id;
        Playmat.Apply(_surface, _picture, id, Seed);
    }
}

/// <summary>
/// A playmat lying on a table picture (career tables): rounded corners, a shadow and an edge like the deck desk's mat,
/// a built-in playmat's color (or the nebula) as its print. A picture playmat's picture, or one in the career kit's
/// mats/&lt;id&gt;.jpg or .png, replaces the print.
/// </summary>
public partial class TableMat : Control
{
    private const string Kit = "res://ui-art/kit/career/mats/";
    private const int Radius = 16;
    // The shadow is drawn apart from the clipped body: a clipping parent's shadow shows as a pale box around it.
    private readonly Panel _shadow = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Panel _body = new() { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.AndDraw };
    private readonly ColorRect _print = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _picture = new()
    {
        MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
    };
    private readonly Panel _edge = new() { MouseFilter = MouseFilterEnum.Ignore };
    private string? _id;

    public TableMat()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        var shadow = new StyleBoxFlat { BgColor = Colors.Black, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 3, ShadowOffset = new Vector2(0, 1) };
        shadow.SetCornerRadiusAll(Radius);
        _shadow.AddThemeStyleboxOverride("panel", shadow);
        foreach (var layer in new Control[] { _shadow, _body, _edge })
        {
            layer.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(layer);
        }
        foreach (var layer in new Control[] { _print, _picture })
        {
            layer.SetAnchorsPreset(LayoutPreset.FullRect);
            _body.AddChild(layer);
        }
        Resized += () => (_print.Material as ShaderMaterial)?.SetShaderParameter("rect_size", Size);
    }

    /// <summary>Whether the mat lies turned round (the far seat's, as across a table).</summary>
    public bool Turned
    {
        get => _picture.FlipV;
        set => _picture.FlipH = _picture.FlipV = value;
    }

    /// <summary>Shows a built-in playmat (by id).</summary>
    public void SetStyle(string id)
    {
        if (id == _id) return;
        _id = id;
        var colors = BoardStyle.PlaymatColors(BoardStyle.PicturePlaymats.Contains(id) ? "grid" : id);
        var cloth = new StyleBoxFlat { BgColor = colors?.Base ?? new Color(0.04f, 0.03f, 0.08f) };
        cloth.SetCornerRadiusAll(Radius);
        _body.AddThemeStyleboxOverride("panel", cloth);
        // A stitched edge a little lighter than the cloth.
        var edge = new StyleBoxFlat { DrawCenter = false, BorderColor = (colors?.Line ?? new Color(0.18f, 0.08f, 0.28f)).Lightened(0.08f) };
        edge.SetBorderWidthAll(3);
        edge.SetCornerRadiusAll(Radius);
        _edge.AddThemeStyleboxOverride("panel", edge);

        var picture = Playmat.Picture(id) ?? new[] { ".jpg", ".png" }.Select(ext => Kit + id + ext).FirstOrDefault(path => ResourceLoader.Exists(path));
        if (picture is null) _picture.Texture = null;
        else Playmat.Cover(_picture, GD.Load<Texture2D>(picture), Playmat.FocusOf(id));
        _picture.Visible = picture is not null;
        _print.Visible = picture is null && colors is null;
        if (_print.Visible)
        {
            _print.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/nebula_playmat.gdshader") };
            ((ShaderMaterial)_print.Material).SetShaderParameter("rect_size", Size);
        }
    }
}
