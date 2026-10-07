// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI;

/// <summary>Frame sizes: menu windows, thinner board panels, and the modal dialogs that ask for a choice.</summary>
public enum PanelKind { Menu, Board, Modal }

/// <summary>
/// The interface kit in <c>res://ui-art/kit/</c> (baked from a private art pack, not part of the repository): frames,
/// fills and ornaments at the size they are drawn, each with the 9-slice margins that keep its corners whole.
/// Every method returns null/false when the kit is missing, so callers keep their plain procedural look.
/// </summary>
public static class UiArt
{
    private const string Root = "res://ui-art/kit/";

    /// <summary>A kit image and its slice margins; a center ornament (CenterFrom..CenterTo) stays fixed in the middle.</summary>
    public sealed record Piece(Texture2D Texture, int Left, int Top, int Right, int Bottom, int CenterFrom, int CenterTo)
    {
        public Vector2 Size => Texture.GetSize();
    }

    private static Dictionary<string, Piece>? _pieces;

    private static Dictionary<string, Piece> Pieces => _pieces ??= Load();

    /// <summary>True when the kit is installed and imported.</summary>
    public static bool Available => Pieces.Count > 0;

    public static Piece? Get(string name) => Pieces.GetValueOrDefault(name);

    public static Texture2D? Texture(string name) => Get(name)?.Texture;

    private static Dictionary<string, Piece> Load()
    {
        var pieces = new Dictionary<string, Piece>();
        // Loaded as a JSON resource, so exported builds include it.
        if (!ResourceLoader.Exists(Root + "kit.json") || GD.Load<Json>(Root + "kit.json")?.Data.AsGodotDictionary() is not { } manifest) return pieces;
        static int[] Ints(Godot.Collections.Dictionary entry, string key, int count) =>
            entry.TryGetValue(key, out var value) ? value.AsGodotArray().Select(v => (int)v.AsDouble()).ToArray() : new int[count];
        foreach (var (key, value) in manifest)
        {
            string name = key.AsString();
            if (!ResourceLoader.Exists(Root + name + ".png")) return new();
            var entry = value.AsGodotDictionary();
            int[] m = Ints(entry, "margins", 4), c = Ints(entry, "center", 2);
            pieces[name] = new Piece(GD.Load<Texture2D>(Root + name + ".png"), m[0], m[1], m[2], m[3], c[0], c[1]);
        }
        return pieces;
    }

    // ---------------------------------------------------------------- palette

    // The nebula's colors: near-black violet depths, violet clouds.
    public static readonly Color MenuFill = new(0.075f, 0.05f, 0.12f, 0.94f);
    public static readonly Color BoardFill = new(0.065f, 0.045f, 0.105f, 0.93f);
    public static readonly Color ModalFill = new(0.05f, 0.035f, 0.08f, 0.97f);
    /// <summary>The fill of buttons and of the unchosen half of a switch.</summary>
    public static readonly Color ButtonBase = new(0.13f, 0.08f, 0.21f);
    /// <summary>The dark fill of value boxes, check boxes and text fields.</summary>
    public static readonly Color Well = new(0.04f, 0.03f, 0.075f, 0.95f);
    /// <summary>The fill that marks the choice to make (default button, selected switch half, lit row).</summary>
    public static readonly Color Highlight = new(0.36f, 0.18f, 0.53f);
    /// <summary>Text color for descriptions and highlighted labels.</summary>
    public static readonly Color Gold = new(0.96f, 0.85f, 0.56f);

    // ---------------------------------------------------------------- styles

    /// <summary>A panel background: the frame of its kind over its fill (null without the kit).</summary>
    public static StyleBox? Panel(PanelKind kind, Color? fill = null, Color? frameTint = null, float padding = -1)
    {
        if (!Available) return null;
        var frame = frameTint ?? Colors.White;
        if (kind == PanelKind.Modal) return Split("modal", fill ?? ModalFill, frame, padding >= 0 ? padding : 14, 4);
        string name = kind == PanelKind.Menu ? "window" : "panel";
        var box = new KitBox();
        box.Add(Get(name + "_fill")!, fill ?? (kind == PanelKind.Menu ? MenuFill : BoardFill));
        box.Add(Get(name + "_frame")!, frame);
        // Content starts inside the inner line (the corner bars may overlap it a little).
        float edge = kind == PanelKind.Menu ? 26 : 18;
        box.SetContent(edge + Math.Max(0, padding), edge + Math.Max(0, padding) * 0.6f);
        return box;
    }

    /// <summary>The frame of the selected tab (a small modal frame).</summary>
    public static StyleBox? Tab(Color fill) => Available ? Split("tab", fill, Colors.White, 14, -3) : null;

    /// <summary>A frame in two halves (a diamond where they meet); content sits inside the corners' margins.</summary>
    private static KitBox Split(string name, Color fill, Color frame, float padding, float vertical)
    {
        var box = new KitBox();
        box.Add(Get(name + "_fill_left")!, fill, 0, 0.5f);
        box.Add(Get(name + "_fill_right")!, fill, 0.5f, 1);
        box.Add(Get(name + "_left")!, frame, 0, 0.5f);
        box.Add(Get(name + "_right")!, frame, 0.5f, 1);
        var left = Get(name + "_left")!;
        box.ContentMarginLeft = box.ContentMarginRight = left.Left + padding;
        box.ContentMarginTop = left.Top + vertical;
        box.ContentMarginBottom = left.Bottom + vertical;
        return box;
    }

    public enum ButtonState { Normal, Hover, Pressed, Disabled, Focus }

    /// <summary>One state of a button: the button frame over its fill; the primary (default) choice is highlighted.</summary>
    public static StyleBox? Button(ButtonState state, bool primary, bool compact = false)
    {
        if (!Available) return null;
        var baseFill = primary ? Highlight : ButtonBase;
        var (fill, frame) = state switch
        {
            ButtonState.Hover => (baseFill.Lightened(0.18f), new Color(1.15f, 1.12f, 1.05f)),
            ButtonState.Pressed => (baseFill.Darkened(0.35f), new Color(0.85f, 0.8f, 0.75f)),
            ButtonState.Disabled => (new Color(0.11f, 0.12f, 0.14f, 0.85f), new Color(0.55f, 0.55f, 0.6f, 0.75f)),
            ButtonState.Focus => (baseFill.Lightened(0.1f), new Color(1.2f, 1.15f, 1.1f)),
            _ => (baseFill, Colors.White),
        };
        return ButtonFill(fill, compact, frame);
    }

    /// <summary>The button frame over a fill of any color (toggles that light up in their own color).</summary>
    public static StyleBox? ButtonFill(Color fill, bool compact = false, Color? frameTint = null)
    {
        if (!Available) return null;
        string name = compact ? "button_small" : "button";
        var box = new KitBox();
        box.Add(Get(name + "_fill")!, fill);
        box.Add(Get(name + "_frame")!, frameTint ?? Colors.White);
        // Tall enough content margins that even a button given no minimum height keeps room for the frame's corners.
        box.SetContent(compact ? 10 : 20, compact ? 6 : 8);
        return box;
    }

    /// <summary>Gives a button the kit look for every state; false (button untouched) without the kit.</summary>
    /// <remarks>Compact buttons (icons, short labels) get the smaller frame.</remarks>
    public static bool StyleButton(Button button, bool primary, bool compact = false)
    {
        if (!Available) return false;
        // The frame's corners need this much room.
        var piece = Get(compact ? "button_small_frame" : "button_frame")!;
        button.CustomMinimumSize = new Vector2(Math.Max(button.CustomMinimumSize.X, piece.Left + piece.Right + 4),
            Math.Max(button.CustomMinimumSize.Y, piece.Top + piece.Bottom));
        // Text small for the frame looks lost in it.
        if (!compact && button.GetThemeFontSize("font_size") < 20) button.AddThemeFontSizeOverride("font_size", 20);
        button.AddThemeStyleboxOverride("normal", Button(ButtonState.Normal, primary, compact)!);
        button.AddThemeStyleboxOverride("hover", Button(ButtonState.Hover, primary, compact)!);
        button.AddThemeStyleboxOverride("pressed", Button(ButtonState.Pressed, primary, compact)!);
        button.AddThemeStyleboxOverride("hover_pressed", Button(ButtonState.Pressed, primary, compact)!);
        button.AddThemeStyleboxOverride("disabled", Button(ButtonState.Disabled, primary, compact)!);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        foreach (var color in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(color, primary ? Colors.White : new Color(0.93f, 0.93f, 0.95f));
        button.AddThemeColorOverride("font_disabled_color", new Color(0.6f, 0.6f, 0.65f));
        return true;
    }

    /// <summary>A horizontal ornamental divider (fixed V in the middle), or a thin line without the kit.</summary>
    public static Control Divider(float height = 18)
    {
        var holder = new Control { CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (Get("divider") is { } piece)
        {
            var box = new KitBox();
            box.Add(piece, Colors.White, fixedHeight: true);
            var panel = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            panel.AddThemeStyleboxOverride("panel", box);
            holder.AddChild(panel);
            return holder;
        }
        var line = new ColorRect { Color = new Color(0.85f, 0.7f, 0.35f, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
        line.SetAnchorsPreset(Control.LayoutPreset.HcenterWide);
        line.AnchorTop = 0.5f; line.AnchorBottom = 0.5f;
        line.OffsetTop = 0; line.OffsetBottom = 1;
        holder.AddChild(line);
        return holder;
    }

    /// <summary>
    /// The theme every screen inherits: kit scroll bars (thin dark track, gold grabber). Null without the kit.
    /// </summary>
    public static Theme? Theme()
    {
        if (!Available) return null;
        var theme = new Theme();
        foreach (var (type, suffix, vertical) in new[] { ("VScrollBar", "", true), ("HScrollBar", "_h", false) })
        {
            KitBox Bar(string name, Color tint)
            {
                var box = new KitBox();
                box.Add(Get(name + suffix)!, tint);
                if (vertical) { box.ContentMarginLeft = box.ContentMarginRight = 5; }
                else { box.ContentMarginTop = box.ContentMarginBottom = 5; }
                return box;
            }
            theme.SetStylebox("scroll", type, Bar("scroll_track", Colors.White));
            theme.SetStylebox("scroll_focus", type, Bar("scroll_track", Colors.White));
            theme.SetStylebox("grabber", type, Bar("scroll_grabber", new Color(0.9f, 0.9f, 0.9f)));
            theme.SetStylebox("grabber_highlight", type, Bar("scroll_grabber", new Color(1.15f, 1.1f, 1.05f)));
            theme.SetStylebox("grabber_pressed", type, Bar("scroll_grabber", new Color(1.25f, 1.2f, 1.1f)));
        }
        // Drop-down lists and context menus: a dark list edged by a thin gold line, no frame.
        var list = new StyleBoxFlat { BgColor = ModalFill, BorderColor = Gold with { A = 0.85f } };
        list.SetBorderWidthAll(1);
        list.SetContentMarginAll(6);
        theme.SetStylebox("panel", "PopupMenu", list);
        theme.SetStylebox("hover", "PopupMenu", new StyleBoxFlat { BgColor = Highlight with { A = 0.7f } });
        return theme;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Draws a piece into a rectangle: 9-slice, or ends + stretched middle around its fixed center ornament.</summary>
    public static void Draw(Rid canvasItem, Piece piece, Rect2 rect, Color modulate)
    {
        var size = piece.Size;
        if (piece.CenterTo > piece.CenterFrom)
        {
            float width = piece.CenterTo - piece.CenterFrom;
            float middle = Mathf.Round(rect.Position.X + rect.Size.X / 2 - width / 2);
            Patch(canvasItem, piece, new Rect2(0, 0, piece.CenterFrom, size.Y), new Rect2(rect.Position.X, rect.Position.Y, middle - rect.Position.X, rect.Size.Y), new Vector2(piece.Left, 0), Vector2.Zero, modulate);
            RenderingServer.CanvasItemAddTextureRectRegion(canvasItem, new Rect2(middle, rect.Position.Y, width, rect.Size.Y), piece.Texture.GetRid(), new Rect2(piece.CenterFrom, 0, width, size.Y), modulate);
            Patch(canvasItem, piece, new Rect2(piece.CenterTo, 0, size.X - piece.CenterTo, size.Y), new Rect2(middle + width, rect.Position.Y, rect.End.X - middle - width, rect.Size.Y), Vector2.Zero, new Vector2(piece.Right, 0), modulate);
            return;
        }
        Patch(canvasItem, piece, new Rect2(Vector2.Zero, size), rect, new Vector2(piece.Left, piece.Top), new Vector2(piece.Right, piece.Bottom), modulate);
    }

    private static void Patch(Rid canvasItem, Piece piece, Rect2 source, Rect2 rect, Vector2 topLeft, Vector2 bottomRight, Color modulate)
    {
        if (rect.Size.X <= 0 || rect.Size.Y <= 0) return;
        RenderingServer.CanvasItemAddNinePatch(canvasItem, rect, source, piece.Texture.GetRid(), topLeft, bottomRight,
            RenderingServer.NinePatchAxisMode.Stretch, RenderingServer.NinePatchAxisMode.Stretch, true, modulate);
    }
}

/// <summary>
/// A style box drawn from kit pieces in layers (a tinted fill, then its frame). A layer can cover part of the width
/// (the halves of a split frame) or keep its piece's own height, centered (bars, dividers).
/// </summary>
public partial class KitBox : StyleBox
{
    private readonly record struct Layer(UiArt.Piece Piece, Color Modulate, float From, float To, bool FixedHeight);

    private readonly List<Layer> _layers = new();

    public void Add(UiArt.Piece piece, Color modulate, float from = 0, float to = 1, bool fixedHeight = false) =>
        _layers.Add(new Layer(piece, modulate, from, to, fixedHeight));

    public void SetContent(float horizontal, float vertical)
    {
        ContentMarginLeft = ContentMarginRight = horizontal;
        ContentMarginTop = ContentMarginBottom = vertical;
    }

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
    {
        foreach (var layer in _layers)
        {
            float x0 = Mathf.Floor(rect.Position.X + rect.Size.X * layer.From);
            float x1 = layer.To >= 1 ? rect.End.X : Mathf.Floor(rect.Position.X + rect.Size.X * layer.To);
            var part = new Rect2(x0, rect.Position.Y, x1 - x0, rect.Size.Y);
            if (layer.FixedHeight)
            {
                float h = layer.Piece.Size.Y;
                part = new Rect2(part.Position.X, Mathf.Round(rect.Position.Y + (rect.Size.Y - h) / 2), part.Size.X, h);
            }
            UiArt.Draw(toCanvasItem, layer.Piece, part, layer.Modulate);
        }
    }
}
