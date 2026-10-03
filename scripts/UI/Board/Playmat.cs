// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Draws a playmat setting onto a shader surface plus an optional picture: built-in styles are procedural
/// (grid colors or nebula); "custom:&lt;path&gt;" shows a user image from this device.
/// </summary>
public static class Playmat
{
    private static Shader? _grid, _nebula;

    public static void Apply(ColorRect surface, TextureRect picture, string id, int seed)
    {
        _grid ??= GD.Load<Shader>("res://shaders/grid_playmat.gdshader");
        _nebula ??= GD.Load<Shader>("res://shaders/nebula_playmat.gdshader");
        picture.Texture = null;
        picture.Visible = false;
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
