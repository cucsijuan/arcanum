// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>A part of a picture that does something: its outline as fractions of the picture (0..1).</summary>
public sealed record Hotspot(string Id, string Label, Vector2[] Points)
{
    public static Hotspot Rect(string id, string label, float x, float y, float w, float h) =>
        new(id, label, new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) });

    public Vector2 Center => Points.Aggregate(Vector2.Zero, (a, p) => a + p) / Points.Length;

    /// <summary>A rectangle ([x, y, w, h] fractions) named <paramref name="key"/> in a hotspot file, or <paramref name="fallback"/>.</summary>
    public static Rect2 LoadArea(string path, string key, Rect2 fallback)
    {
        if (!Godot.FileAccess.FileExists(path)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty(key, out var r) ? new Rect2(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle()) : fallback;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IndexOutOfRangeException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Hotspots from a JSON file next to the picture: {"spots": [{"id", "label", "points": [[x, y], ...]} or "rect": [x, y, w, h]]};
    /// <paramref name="fallback"/> when the file is missing or can't be read.
    /// </summary>
    public static IReadOnlyList<Hotspot> Load(string path, IReadOnlyList<Hotspot> fallback)
    {
        if (!Godot.FileAccess.FileExists(path)) return fallback;
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var spots = new List<Hotspot>();
            foreach (var s in doc.RootElement.GetProperty("spots").EnumerateArray())
            {
                var id = s.GetProperty("id").GetString()!;
                var label = s.TryGetProperty("label", out var l) ? l.GetString() ?? id : id;
                if (s.TryGetProperty("rect", out var r))
                    spots.Add(Rect(id, label, r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle()));
                else
                    spots.Add(new Hotspot(id, label, s.GetProperty("points").EnumerateArray().Select(p => new Vector2(p[0].GetSingle(), p[1].GetSingle())).ToArray()));
            }
            return spots;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            GD.PushWarning($"{path}: {e.Message}");
            return fallback;
        }
    }
}

/// <summary>
/// A picture filling the whole window (cropped to cover it) whose hotspots are what the player uses: the one under the
/// pointer, or chosen with the arrow keys, glows along its outline and shows its name; clicking it or Enter uses it,
/// Escape asks to go back. F3 shows every outline and the pointer's position in picture fractions (for placing hotspots;
/// Ctrl+click copies the point).
/// </summary>
public partial class ArtScene : Control
{
    private static readonly Color Glow = new(1f, 0.82f, 0.42f);

    private readonly Texture2D? _texture;
    private readonly Control _picture = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Control _overlay = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly PanelContainer _tag = new() { MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 10 };
    private readonly Label _tagText = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _debug = new() { MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 20 };
    private Vector2 _imageSize = new(16, 9);

    // What part of the picture is shown: its center, and how much closer than the whole picture (cropped to cover the window).
    private Vector2 _viewCenter = new(0.5f, 0.5f);
    private float _zoom = 1;
    private int _current = -1;
    private bool _showAll;
    private double _time;

    public IReadOnlyList<Hotspot> Spots { get; private set; } = Array.Empty<Hotspot>();

    /// <summary>A hotspot was used (its id).</summary>
    public event Action<string>? Activated;

    /// <summary>Escape: the screen should go back.</summary>
    public event Action? BackRequested;

    /// <summary>While false (a panel is open over the picture) the hotspots don't react.</summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            _interactive = value;
            if (!value) _current = -1;
            UpdateTag();
            _overlay.QueueRedraw();
        }
    }

    private bool _interactive = true;

    public ArtScene(Texture2D? picture, IReadOnlyList<Hotspot> spots)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        _picture.SetAnchorsPreset(LayoutPreset.FullRect);
        _overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        _texture = picture;
        if (picture is not null) _imageSize = picture.GetSize();
        _picture.Draw += () =>
        {
            if (_texture is not null) _picture.DrawTextureRect(_texture, new Rect2(ToScreen(Vector2.Zero), _imageSize * Scale()), false);
        };
        AddChild(_picture);
        AddChild(_overlay);
        _overlay.Draw += DrawOverlay;
        _tag.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.06f, 0.05f, 0.85f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            BorderColor = Glow, BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        _tagText.AddThemeFontSizeOverride("font_size", 22);
        _tagText.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f));
        _tag.AddChild(_tagText);
        AddChild(_tag);
        _debug.AddThemeFontSizeOverride("font_size", 16);
        _debug.AddThemeColorOverride("font_outline_color", Colors.Black);
        _debug.AddThemeConstantOverride("outline_size", 6);
        _debug.Position = new Vector2(16, 16);
        AddChild(_debug);
        SetSpots(spots);
    }

    public void SetSpots(IReadOnlyList<Hotspot> spots)
    {
        Spots = spots;
        _current = -1;
        UpdateTag();
    }

    private float CoverScale() => Math.Max(Size.X / _imageSize.X, Size.Y / _imageSize.Y);

    private float Scale() => CoverScale() * _zoom;

    /// <summary>A point of the picture (fractions) on screen.</summary>
    public Vector2 ToScreen(Vector2 picture) => Size / 2 + (picture - _viewCenter) * _imageSize * Scale();

    /// <summary>A hotspot's outline on screen.</summary>
    public Vector2[] ScreenPoints(Hotspot spot) => spot.Points.Select(ToScreen).ToArray();

    /// <summary>
    /// Moves the view (animated) so that <paramref name="area"/> of the picture (fractions) fills the window, or back to
    /// the whole picture when null; <paramref name="done"/> runs when it gets there.
    /// </summary>
    public void Zoom(Rect2? area, double seconds = 0.6, Action? done = null)
    {
        var center = area?.GetCenter() ?? new Vector2(0.5f, 0.5f);
        // The area fits the window whole.
        float zoom = area is { } a ? Math.Max(1, Math.Min(Size.X / (a.Size.X * _imageSize.X), Size.Y / (a.Size.Y * _imageSize.Y)) / CoverScale()) : 1;
        _tag.Visible = false;
        var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tween.TweenMethod(Callable.From<Vector2>(c => { _viewCenter = c; Changed(); }), _viewCenter, center, seconds);
        tween.TweenMethod(Callable.From<float>(z => { _zoom = z; Changed(); }), _zoom, zoom, seconds);
        if (done is not null) tween.Chain().TweenCallback(Callable.From(done));
    }

    private void Changed()
    {
        _picture.QueueRedraw();
        _overlay.QueueRedraw();
        ViewChanged?.Invoke();
    }

    /// <summary>The view moved (zooming) or the window changed size.</summary>
    public event Action? ViewChanged;

    /// <summary>The screen rectangle around a hotspot (where panels about it can go).</summary>
    public Rect2 ScreenRect(Hotspot spot)
    {
        var points = ScreenPoints(spot);
        var min = points.Aggregate((a, b) => a.Min(b));
        var max = points.Aggregate((a, b) => a.Max(b));
        return new Rect2(min, max - min);
    }

    public Hotspot? Spot(string id) => Spots.FirstOrDefault(s => s.Id == id);

    private Vector2 ToPicture(Vector2 screen) => _viewCenter + (screen - Size / 2) / (_imageSize * Scale());

    private int SpotAt(Vector2 point)
    {
        for (int i = Spots.Count - 1; i >= 0; i--)
            if (Geometry2D.IsPointInPolygon(point, ScreenPoints(Spots[i]))) return i;
        return -1;
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (_current >= 0 || _showAll) _overlay.QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            if (_showAll) _debug.Text = DebugText(motion.Position);
            if (!Interactive) return;
            int at = SpotAt(motion.Position);
            if (at != _current) Select(at);
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            if (_showAll && click.CtrlPressed)
            {
                var p = ToPicture(click.Position);
                DisplayServer.ClipboardSet($"[{p.X:0.###}, {p.Y:0.###}]");
                AcceptEvent();
                return;
            }
            if (!Interactive) return;
            int at = SpotAt(click.Position);
            if (at >= 0)
            {
                AcceptEvent();
                Activated?.Invoke(Spots[at].Id);
            }
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.F3 })
        {
            _showAll = !_showAll;
            _debug.Visible = _showAll;
            _overlay.QueueRedraw();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!Interactive) return;
        if (e.IsActionPressed("ui_cancel"))
        {
            BackRequested?.Invoke();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed("ui_accept") && _current >= 0)
        {
            Activated?.Invoke(Spots[_current].Id);
            GetViewport().SetInputAsHandled();
        }
        else if (Direction(e) is { } direction && Spots.Count > 0)
        {
            Select(Next(direction));
            GetViewport().SetInputAsHandled();
        }
    }

    private static Vector2? Direction(InputEvent e) =>
        e.IsActionPressed("ui_left") ? Vector2.Left : e.IsActionPressed("ui_right") ? Vector2.Right
        : e.IsActionPressed("ui_up") ? Vector2.Up : e.IsActionPressed("ui_down") ? Vector2.Down
        : e.IsActionPressed("ui_focus_next") ? Vector2.Right : null;

    /// <summary>The nearest hotspot in a direction from the selected one (the first one when none is).</summary>
    private int Next(Vector2 direction)
    {
        if (_current < 0) return 0;
        var from = Spots[_current].Center;
        int best = _current;
        float bestScore = float.MaxValue;
        for (int i = 0; i < Spots.Count; i++)
        {
            if (i == _current) continue;
            var to = Spots[i].Center - from;
            float along = to.Dot(direction);
            if (along <= 0.001f) continue;
            float score = along + 2 * Math.Abs(to.Cross(direction));
            if (score < bestScore) (best, bestScore) = (i, score);
        }
        return best;
    }

    private void Select(int index)
    {
        _current = index;
        UpdateTag();
        _overlay.QueueRedraw();
    }

    private void UpdateTag()
    {
        if (_current < 0 || _current >= Spots.Count || !Interactive)
        {
            _tag.Visible = false;
            return;
        }
        var spot = Spots[_current];
        _tagText.Text = spot.Label;
        _tag.Visible = true;
        _tag.ResetSize();
        var rect = ScreenRect(spot);
        var at = new Vector2(rect.GetCenter().X - _tag.Size.X / 2, rect.Position.Y - _tag.Size.Y - 10);
        if (at.Y < 8) at.Y = rect.End.Y + 10;
        _tag.Position = at.Clamp(new Vector2(8, 8), Size - _tag.Size - new Vector2(8, 8));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            UpdateTag();
            Changed();
        }
    }

    private void DrawOverlay()
    {
        if (_showAll)
            foreach (var spot in Spots)
            {
                var pts = ScreenPoints(spot);
                _overlay.DrawPolyline(pts.Append(pts[0]).ToArray(), new Color(0.3f, 1f, 0.6f, 0.9f), 2);
                _overlay.DrawString(ThemeDB.FallbackFont, pts[0] + new Vector2(4, 18), spot.Id, fontSize: 16, modulate: new Color(0.3f, 1f, 0.6f));
            }
        if (_current < 0 || _current >= Spots.Count || !Interactive) return;
        var outline = ScreenPoints(Spots[_current]);
        var closed = outline.Append(outline[0]).ToArray();
        float pulse = 0.75f + 0.25f * Mathf.Sin((float)_time * 4);
        _overlay.DrawColoredPolygon(outline, Glow with { A = 0.07f * pulse });
        foreach (var (width, alpha) in new[] { (18f, 0.06f), (12f, 0.1f), (7f, 0.18f), (4f, 0.35f), (2f, 0.9f) })
            _overlay.DrawPolyline(closed, Glow with { A = alpha * pulse }, width, antialiased: true);
    }

    private string DebugText(Vector2 at)
    {
        var p = ToPicture(at);
        return $"F3 hotspot view · pointer at [{p.X:0.###}, {p.Y:0.###}] · Ctrl+click copies it";
    }
}
