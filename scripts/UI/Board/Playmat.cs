// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Draws a playmat setting onto a shader surface plus an optional picture: built-in styles are procedural
/// (grid colors or nebula); "custom:&lt;path&gt;" shows a user image from this device; <see cref="Clear"/> shows nothing
/// (a career table under the area).
/// </summary>
public static class Playmat
{
    private static Shader? _grid, _nebula;

    /// <summary>No playmat: what is under the area shows through (a career table).</summary>
    public const string Clear = "clear";

    public static void Apply(ColorRect surface, TextureRect picture, string id, int seed)
    {
        surface.Visible = id != Clear;
        _grid ??= GD.Load<Shader>("res://shaders/grid_playmat.gdshader");
        _nebula ??= GD.Load<Shader>("res://shaders/nebula_playmat.gdshader");
        picture.Texture = null;
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
/// a built-in playmat's color (or the nebula) as its print. A picture in mats/&lt;id&gt;.jpg or .png replaces the print.
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

    /// <summary>Shows a built-in playmat (by id).</summary>
    public void SetStyle(string id)
    {
        if (id == _id) return;
        _id = id;
        var colors = BoardStyle.PlaymatColors(id);
        var cloth = new StyleBoxFlat { BgColor = colors?.Base ?? new Color(0.04f, 0.03f, 0.08f) };
        cloth.SetCornerRadiusAll(Radius);
        _body.AddThemeStyleboxOverride("panel", cloth);
        // A stitched edge a little lighter than the cloth.
        var edge = new StyleBoxFlat { DrawCenter = false, BorderColor = (colors?.Line ?? new Color(0.18f, 0.08f, 0.28f)).Lightened(0.08f) };
        edge.SetBorderWidthAll(3);
        edge.SetCornerRadiusAll(Radius);
        _edge.AddThemeStyleboxOverride("panel", edge);

        var picture = new[] { ".jpg", ".png" }.Select(ext => Kit + id + ext).FirstOrDefault(path => ResourceLoader.Exists(path));
        _picture.Texture = picture is null ? null : GD.Load<Texture2D>(picture);
        _picture.Visible = picture is not null;
        _print.Visible = picture is null && colors is null;
        if (_print.Visible)
        {
            _print.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/nebula_playmat.gdshader") };
            ((ShaderMaterial)_print.Material).SetShaderParameter("rect_size", Size);
        }
    }
}
