// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI;

/// <summary>Frame sizes: menus get the ornate frame, the board a thinner one that leaves the table readable.</summary>
public enum PanelKind { Menu, Board }

/// <summary>
/// Builds 9-slice styles from the interface art in <c>res://ui-art/</c> (a folder that is not part of the repository).
/// The art comes as a white background shape and a separate gold frame; they are composed once into one texture,
/// with the shape tinted by the fill color. Every method returns null/false when the art is missing, so callers keep
/// their plain procedural look.
/// </summary>
public static class UiArt
{
    private const string Root = "res://ui-art/";

    /// <summary>A background shape plus its frame, cropped to the frame's drawn area and scaled to the size it is used at.</summary>
    private sealed record Recipe(string Frame, string Shape, Rect2I Crop, float Scale, int Margin, float Content);

    private static readonly Recipe MenuPanel = new("frames/Frame_Box_Medium_03.png", "frames/Frame_Box_Medium_03_Lip.png", new Rect2I(9, 7, 494, 501), 0.5f, 34, 20);
    private static readonly Recipe BoardPanel = new("frames/Frame_Box_Small_02.png", "frames/Frame_Box_Small_01_Lip.png", new Rect2I(8, 9, 241, 241), 0.4f, 18, 10);
    private static readonly Recipe ButtonBox = new("buttons/Menu_Button_16_Frame.png", "buttons/Menu_Button_16_Background.png", new Rect2I(12, 9, 488, 113), 0.34f, 13, 8);
    private static readonly Recipe CompactBox = new("frames/Frame_Box_Small_02.png", "frames/Frame_Box_Small_01_Lip.png", new Rect2I(8, 9, 241, 241), 0.26f, 12, 5);
    private static readonly Rect2I DividerCrop = new(14, 39, 996, 182);
    private const string DividerFile = "dividers/Frame_Bar_06.png";

    private static bool? _available;
    private static readonly Dictionary<string, Image?> Sources = new();
    private static readonly Dictionary<string, ImageTexture> Composed = new();

    /// <summary>True when the art is installed and imported.</summary>
    public static bool Available => _available ??= new[] { MenuPanel, BoardPanel, ButtonBox }
        .SelectMany(r => new[] { r.Frame, r.Shape }).Append(DividerFile).All(f => ResourceLoader.Exists(Root + f));

    public static readonly Color MenuFill = new(0.30f, 0.22f, 0.50f, 0.95f);
    public static readonly Color BoardFill = new(0.26f, 0.19f, 0.44f, 0.94f);

    /// <summary>A panel background: the fill color shows through the shape, the frame is drawn over it.</summary>
    public static StyleBox? Panel(PanelKind kind, Color? fill = null, Color? frameTint = null, float padding = -1)
    {
        if (!Available) return null;
        var recipe = kind == PanelKind.Menu ? MenuPanel : BoardPanel;
        var color = fill ?? (kind == PanelKind.Menu ? MenuFill : BoardFill);
        float content = padding >= 0 ? padding + recipe.Content * 0.5f : recipe.Content;
        return Slice(recipe, color, frameTint ?? Colors.White, content, content);
    }

    public enum ButtonState { Normal, Hover, Pressed, Disabled, Focus }

    /// <summary>One state of a button: tinted background plus the gold frame; primary buttons are warmer and brighter.</summary>
    public static StyleBox? Button(ButtonState state, bool primary, bool compact = false)
    {
        if (!Available) return null;
        var (fill, frame) = (state, primary) switch
        {
            (ButtonState.Normal, false) => (new Color(0.30f, 0.22f, 0.50f), Colors.White),
            (ButtonState.Hover, false) => (new Color(0.44f, 0.33f, 0.70f), new Color(1.12f, 1.1f, 1.05f)),
            (ButtonState.Pressed, false) => (new Color(0.17f, 0.12f, 0.30f), new Color(0.85f, 0.8f, 0.75f)),
            (ButtonState.Disabled, false) => (new Color(0.2f, 0.19f, 0.24f, 0.8f), new Color(0.5f, 0.5f, 0.55f, 0.7f)),
            (ButtonState.Focus, false) => (new Color(0.38f, 0.28f, 0.62f), new Color(1.2f, 1.15f, 1.1f)),
            (ButtonState.Normal, true) => (new Color(1f, 0.78f, 0.30f), new Color(1.1f, 1.05f, 1f)),
            (ButtonState.Hover, true) => (new Color(1f, 0.9f, 0.5f), new Color(1.25f, 1.2f, 1.1f)),
            (ButtonState.Pressed, true) => (new Color(0.8f, 0.58f, 0.2f), new Color(0.9f, 0.85f, 0.8f)),
            (ButtonState.Disabled, true) => (new Color(0.4f, 0.33f, 0.2f, 0.8f), new Color(0.5f, 0.5f, 0.55f, 0.7f)),
            _ => (new Color(1f, 0.85f, 0.4f), new Color(1.25f, 1.2f, 1.1f)),
        };
        return compact ? Slice(CompactBox, fill, frame, 8, 3) : Slice(ButtonBox, fill, frame, 26, 4);
    }

    /// <summary>Gives a button the art for every state; false (button untouched) when the art is missing.</summary>
    /// <remarks>Compact buttons (icons, short labels) get a small square frame instead of the pointed one.</remarks>
    public static bool StyleButton(Button button, bool primary, bool compact = false)
    {
        if (!Available) return false;
        // The frame needs a minimum height to keep its shape.
        button.CustomMinimumSize = new Vector2(button.CustomMinimumSize.X, Math.Max(button.CustomMinimumSize.Y, compact ? 28 : 38));
        button.AddThemeStyleboxOverride("normal", Button(ButtonState.Normal, primary, compact)!);
        button.AddThemeStyleboxOverride("hover", Button(ButtonState.Hover, primary, compact)!);
        button.AddThemeStyleboxOverride("pressed", Button(ButtonState.Pressed, primary, compact)!);
        button.AddThemeStyleboxOverride("hover_pressed", Button(ButtonState.Pressed, primary, compact)!);
        button.AddThemeStyleboxOverride("disabled", Button(ButtonState.Disabled, primary, compact)!);
        button.AddThemeStyleboxOverride("focus", Button(ButtonState.Focus, primary, compact)!);
        return true;
    }

    /// <summary>A horizontal ornamental divider of the given height, or a thin line when the art is missing.</summary>
    public static Control Divider(float height = 22)
    {
        if (!Available)
        {
            var holder = new Control { CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
            var line = new ColorRect { Color = new Color(0.85f, 0.7f, 0.35f, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
            line.SetAnchorsPreset(Control.LayoutPreset.HcenterWide);
            line.AnchorTop = 0.5f; line.AnchorBottom = 0.5f;
            line.OffsetTop = 0; line.OffsetBottom = 1;
            holder.AddChild(line);
            return holder;
        }
        var image = Source(DividerFile)!.GetRegion(DividerCrop);
        float scale = height / DividerCrop.Size.Y;
        image.Resize(Math.Max(1, (int)(DividerCrop.Size.X * scale)), (int)height, Image.Interpolation.Lanczos);
        int end = (int)(image.GetWidth() * 0.36f);
        var box = new StyleBoxTexture
        {
            Texture = ImageTexture.CreateFromImage(image),
            TextureMarginLeft = end, TextureMarginRight = end,
        };
        var divider = new Panel { CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        divider.AddThemeStyleboxOverride("panel", box);
        return divider;
    }

    private static StyleBoxTexture Slice(Recipe recipe, Color fill, Color frame, float horizontal, float vertical)
    {
        var texture = Compose(recipe, fill, frame);
        int margin = recipe.Margin;
        // Wide buttons keep their pointed ends: the vertical margin is half the frame height, the horizontal one the end.
        bool wide = texture.GetWidth() > texture.GetHeight() * 2;
        return new StyleBoxTexture
        {
            Texture = texture,
            TextureMarginLeft = margin, TextureMarginRight = margin,
            TextureMarginTop = wide ? texture.GetHeight() / 2f - 1 : margin,
            TextureMarginBottom = wide ? texture.GetHeight() / 2f - 1 : margin,
            ContentMarginLeft = horizontal, ContentMarginRight = horizontal,
            ContentMarginTop = vertical, ContentMarginBottom = vertical,
        };
    }

    private static ImageTexture Compose(Recipe recipe, Color fill, Color frameTint)
    {
        string key = $"{recipe.Frame}|{fill.ToHtml()}|{frameTint.ToHtml()}";
        if (Composed.TryGetValue(key, out var cached)) return cached;
        var shape = Prepare(recipe, recipe.Shape);
        var result = Image.CreateEmpty(shape.GetWidth(), shape.GetHeight(), false, Image.Format.Rgba8);
        // The shape of a panel is only a rim with a soft inner shadow: the hollow inside it gets a solid, darker fill.
        var inside = Interior(shape);
        var core = fill.Darkened(0.45f);
        for (int y = 0; y < shape.GetHeight(); y++)
            for (int x = 0; x < shape.GetWidth(); x++)
                if (inside[y * shape.GetWidth() + x]) result.SetPixel(x, y, core);
        result.BlendRect(Tint(shape, fill), new Rect2I(Vector2I.Zero, shape.GetSize()), Vector2I.Zero);
        var frame = Tint(Prepare(recipe, recipe.Frame), frameTint);
        result.BlendRect(frame, new Rect2I(Vector2I.Zero, frame.GetSize()), Vector2I.Zero);
        var texture = ImageTexture.CreateFromImage(result);
        Composed[key] = texture;
        return texture;
    }

    /// <summary>Pixels reachable from the center without crossing the shape's solid rim (none when the center is solid).</summary>
    private static bool[] Interior(Image shape)
    {
        int w = shape.GetWidth(), h = shape.GetHeight();
        var inside = new bool[w * h];
        var data = shape.GetData();
        bool Open(int x, int y) => data[(y * w + x) * 4 + 3] < 200;
        if (!Open(w / 2, h / 2)) return inside;
        var pending = new Stack<(int X, int Y)>();
        pending.Push((w / 2, h / 2));
        inside[(h / 2) * w + w / 2] = true;
        while (pending.Count > 0)
        {
            var (x, y) = pending.Pop();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || inside[ny * w + nx] || !Open(nx, ny)) continue;
                inside[ny * w + nx] = true;
                pending.Push((nx, ny));
            }
        }
        return inside;
    }

    /// <summary>The cropped image scaled to the size the recipe is used at.</summary>
    private static Image Prepare(Recipe recipe, string file)
    {
        var image = Source(file)!.GetRegion(recipe.Crop);
        image.Resize(Math.Max(1, (int)(recipe.Crop.Size.X * recipe.Scale)), Math.Max(1, (int)(recipe.Crop.Size.Y * recipe.Scale)), Image.Interpolation.Lanczos);
        return image;
    }

    /// <summary>The image multiplied by a color (components above 1 brighten).</summary>
    private static Image Tint(Image image, Color tint)
    {
        var data = image.GetData();
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)Math.Min(255, data[i] * tint.R);
            data[i + 1] = (byte)Math.Min(255, data[i + 1] * tint.G);
            data[i + 2] = (byte)Math.Min(255, data[i + 2] * tint.B);
            data[i + 3] = (byte)Math.Min(255, data[i + 3] * tint.A);
        }
        return Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
    }

    private static Image? Source(string file)
    {
        if (Sources.TryGetValue(file, out var cached)) return cached;
        var image = GD.Load<Texture2D>(Root + file)?.GetImage();
        if (image is not null)
        {
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
        }
        Sources[file] = image;
        return image;
    }
}
