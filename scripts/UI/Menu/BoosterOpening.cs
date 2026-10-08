// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// A booster pack in 3D, opened by swiping across its tear line: the cut glows where the pointer has passed and once
/// it runs most of the way across, the sealed top tears off and flies away and the pack lets its cards out. Left alone
/// it opens by itself; it can also open by itself straight away, or be skipped.
/// The pack model comes from the interface art (<see cref="ModelPath"/>, not part of the repository); without it there
/// is no animation. Its picture is the set's booster as the module gives it, downloaded when needed.
/// </summary>
public partial class BoosterOpening : Control
{
    public const string ModelPath = "res://ui-art/kit/booster/booster.glb";

    // The model's measures (tools/bake_booster.py in the art repository): centered on the origin, front facing +Z.
    private const float PackWidth = 0.7823f, PackHeight = 1.3969f, TearY = 0.5097f, FrontZ = 0.04f;
    // Its sealed ends, as fractions of its height.
    private const float ModelSealTop = 0.123f, ModelSealBottom = 0.103f;

    /// <summary>Seconds a pack waits for a swipe before opening by itself.</summary>
    private const double OpensAfter = 8;
    /// <summary>How much of the tear line a swipe must cover to tear the pack.</summary>
    private const float TearsAt = 0.8f;

    public static bool Available => ResourceLoader.Exists(ModelPath);

    /// <summary>The pack has torn open: the opening's position on screen, where its cards come out.</summary>
    public event Action<Vector2>? Opened;

    /// <summary>Controls of the screen underneath that stay clickable through the dimmed overlay (drawn above it).</summary>
    public List<Control> ClickThrough { get; } = new();

    public override bool _HasPoint(Vector2 point)
    {
        var global = GetGlobalTransform() * point;
        return !ClickThrough.Any(c => IsInstanceValid(c) && c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(global));
    }

    private readonly string? _imageUrl;
    private readonly (double Top, double Bottom) _seals;
    private readonly bool _automatic;
    private readonly string _label;

    private readonly ColorRect _dim = new() { Color = new Color(0, 0, 0, 0) };
    private readonly SubViewport _viewport = new() { TransparentBg = true, OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X };
    private readonly Camera3D _camera = new() { Position = new Vector3(0, 0.02f, 3.0f), Fov = 34 };
    private readonly Node3D _pack = new();
    private Node3D _strip = null!;
    private readonly MeshInstance3D _line = new();
    private readonly ShaderMaterial _lineMaterial = new() { Shader = GD.Load<Shader>("res://shaders/booster_tear.gdshader") };
    private readonly StandardMaterial3D _material = new();
    private StandardMaterial3D _stripMaterial = null!;
    private readonly OmniLight3D _burst = new() { LightEnergy = 0, OmniRange = 2.5f, LightColor = new Color(0.75f, 0.9f, 1f) };
    private readonly Label3D _name = new() { FontSize = 64, OutlineSize = 12, PixelSize = 0.0022f, Width = 360, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _hint = BoardStyle.MakeLabel("Swipe across the line to open your booster", 20, BoardStyle.Text);

    private double _elapsed;
    private bool _dragging, _autoCutting, _torn;
    private float _cutFrom = -1, _cutTo = -1;
    private int _direction = 1;

    /// <param name="imageUrl">The set's booster picture, or null for a plain pack labelled <paramref name="label"/>.</param>
    /// <param name="automatic">Tear by itself straight away instead of waiting for a swipe.</param>
    public BoosterOpening(string? imageUrl, (double Top, double Bottom) seals, string label, bool automatic)
    {
        _imageUrl = imageUrl;
        _seals = seals;
        _label = label;
        _automatic = automatic;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 400;

        _dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dim.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_dim);

        var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        container.AddChild(_viewport);
        AddChild(container);
        BuildScene();

        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _hint.GrowHorizontal = GrowDirection.Both;
        _hint.OffsetTop = -96;
        _hint.OffsetBottom = -64;
        _hint.Visible = !_automatic;
        AddChild(_hint);

        var skip = BoardStyle.MakeButton("Skip", 16);
        skip.SetAnchorsPreset(LayoutPreset.BottomRight);
        skip.GrowHorizontal = GrowDirection.Begin;
        skip.GrowVertical = GrowDirection.Begin;
        skip.OffsetLeft = -140; skip.OffsetTop = -70; skip.OffsetRight = -24; skip.OffsetBottom = -24;
        skip.Pressed += () => Tear(1, quick: true);
        AddChild(skip);

        // The pack rises into view while the table dims.
        _pack.Position = new Vector3(0, -2.6f, 0);
        var enter = CreateTween().SetParallel();
        enter.TweenProperty(_dim, "color:a", 0.7f, 0.35);
        enter.TweenProperty(_pack, "position:y", 0f, 0.55).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private void BuildScene()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.85f, 0.88f, 1f),
            AmbientLightEnergy = 0.55f,
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        _viewport.AddChild(_camera);
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-28, -32, 0), LightEnergy = 1.25f });
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-10, 40, 0), LightEnergy = 0.35f, LightColor = new Color(0.8f, 0.85f, 1f) });
        _viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(15, 170, 0), LightEnergy = 0.6f }); // rim light from behind
        _viewport.AddChild(_pack);

        var model = GD.Load<PackedScene>(ModelPath).Instantiate<Node3D>();
        _pack.AddChild(model);
        _material.Roughness = 0.32f;
        _material.Metallic = 0.35f;
        _material.MetallicSpecular = 0.65f;
        _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        _stripMaterial = (StandardMaterial3D)_material.Duplicate();
        _stripMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        ((MeshInstance3D)model.FindChild("Body", true, false)!).MaterialOverride = _material;
        _strip = (MeshInstance3D)model.FindChild("Strip", true, false)!;
        ((MeshInstance3D)_strip).MaterialOverride = _stripMaterial;
        SetPicture(null);
        if (_imageUrl is not null) CardImageCache.Request(CardImageCache.UrlKey(_imageUrl), texture => { if (IsInstanceValid(this)) SetPicture(texture.GetImage()); });

        // A plain pack carries the name of what it holds.
        _name.Text = _label;
        _name.Position = new Vector3(0, -0.05f, FrontZ + 0.02f);
        _name.Modulate = new Color(1, 0.93f, 0.75f);
        _name.OutlineModulate = new Color(0, 0, 0, 0.7f);
        _pack.AddChild(_name);

        _line.Mesh = new QuadMesh { Size = new Vector2(PackWidth, 0.09f) };
        _line.MaterialOverride = _lineMaterial;
        _line.Position = new Vector3(0, TearY, FrontZ);
        _pack.AddChild(_line);
        _burst.Position = new Vector3(0, TearY + 0.05f, 0.35f);
        _pack.AddChild(_burst);
    }

    private void SetPicture(Image? photo)
    {
        var texture = PackTexture(photo, _seals, BoardStyle.CardBackColors(BoardStyle.CardBack));
        _material.AlbedoTexture = texture;
        _stripMaterial.AlbedoTexture = texture;
        _name.Visible = photo is null;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (!_torn)
        {
            // The pack leans toward the pointer and breathes a little.
            var mouse = GetLocalMousePosition() / Size - new Vector2(0.5f, 0.5f);
            var target = new Vector3(Mathf.Clamp(mouse.Y, -0.5f, 0.5f) * 0.25f + 0.03f * Mathf.Sin((float)_elapsed * 1.3f),
                Mathf.Clamp(mouse.X, -0.5f, 0.5f) * 0.4f, 0);
            _pack.Rotation = _pack.Rotation.Lerp(target, (float)Math.Min(1, delta * 5));

            if (!_dragging && !_autoCutting && _elapsed > (_automatic ? 0.9 : OpensAfter)) AutoCut();
        }
        _lineMaterial.SetShaderParameter("cut_from", _cutFrom);
        _lineMaterial.SetShaderParameter("cut_to", _cutTo);
    }

    /// <summary>Cuts along the whole line by itself, then tears.</summary>
    private void AutoCut()
    {
        _autoCutting = true;
        _hint.Visible = false;
        if (_cutFrom < 0) _cutFrom = _cutTo = 0;
        var cut = CreateTween();
        cut.TweenMethod(Callable.From<float>(v => { _cutFrom = Math.Min(_cutFrom, v); _cutTo = Math.Max(_cutTo, v); }), _cutTo, 1f, 0.5 * (1 - _cutTo) + 0.1)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        cut.TweenCallback(Callable.From(() => Tear(1, quick: false)));
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_torn || _autoCutting) return;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                var (along, distance) = OnLine(press.Position);
                if (distance > 48 || along < -0.12f || along > 1.12f) return;
                _dragging = true;
                _hint.Visible = false;
                along = Mathf.Clamp(along, 0, 1);
                // A swipe that starts away from the cut so far starts a new cut: two short swipes far apart don't
                // count as cutting what lies between them.
                if (_cutFrom < 0 || along < _cutFrom - 0.06f || along > _cutTo + 0.06f) _cutFrom = _cutTo = along;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _dragging = false;
                break;
            case InputEventMouseMotion motion when _dragging:
                var (at, _) = OnLine(motion.Position);
                at = Mathf.Clamp(at, 0, 1);
                if (at > _cutTo || at < _cutFrom) _direction = Math.Sign(motion.Relative.X) is var s and not 0 ? s : _direction;
                _cutFrom = Math.Min(_cutFrom, at);
                _cutTo = Math.Max(_cutTo, at);
                if (_cutTo - _cutFrom >= TearsAt) Tear(_direction, quick: false);
                AcceptEvent();
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel") && !_torn) { Tear(1, quick: true); GetViewport().SetInputAsHandled(); }
    }

    /// <summary>Where a point falls along the tear line on screen (0 at its left end, 1 at its right) and how far from it.</summary>
    private (float Along, float Distance) OnLine(Vector2 point)
    {
        var a = _camera.UnprojectPosition(_pack.ToGlobal(new Vector3(-PackWidth / 2, TearY, FrontZ)));
        var b = _camera.UnprojectPosition(_pack.ToGlobal(new Vector3(PackWidth / 2, TearY, FrontZ)));
        var ab = b - a;
        float along = (point - a).Dot(ab) / ab.LengthSquared();
        return (along, point.DistanceTo(a + ab * Mathf.Clamp(along, 0, 1)));
    }

    /// <summary>The sealed top tears off toward <paramref name="direction"/> (±1) and flies away; the cards come out.</summary>
    private void Tear(int direction, bool quick)
    {
        if (_torn) return;
        _torn = true;
        _dragging = false;
        _hint.Visible = false;
        float speed = quick ? 0.5f : 1f;
        _cutFrom = Math.Max(0, _cutFrom);

        var cut = CreateTween();
        cut.TweenMethod(Callable.From<float>(v => { _cutFrom = Math.Min(_cutFrom, 1 - v); _cutTo = Math.Max(_cutTo, v); }), 0f, 1f, 0.08 * speed);
        cut.TweenMethod(Callable.From<float>(v => _lineMaterial.SetShaderParameter("strength", v)), 1f, 0f, 0.35 * speed).SetDelay(0.15 * speed);

        var fly = CreateTween().SetParallel();
        fly.TweenProperty(_strip, "position", _strip.Position + new Vector3(direction * 1.3f, 1.1f, 0.5f), 0.9 * speed)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        fly.TweenProperty(_strip, "rotation", _strip.Rotation + new Vector3(-0.7f, direction * 0.5f, -direction * 1.1f), 0.9 * speed)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        fly.TweenProperty(_stripMaterial, "albedo_color:a", 0f, 0.45 * speed).SetDelay(0.4 * speed);
        // The pack gives a little as the top comes off, and light spills out of the opening.
        fly.TweenProperty(_pack, "position:y", -0.06f, 0.1 * speed);
        fly.TweenProperty(_pack, "rotation", Vector3.Zero, 0.3 * speed);
        fly.TweenProperty(_burst, "light_energy", 5f, 0.18 * speed).SetDelay(0.05 * speed);

        var after = CreateTween();
        after.TweenInterval(0.4 * speed);
        after.TweenCallback(Callable.From(() =>
        {
            var opening = _camera.UnprojectPosition(_pack.ToGlobal(new Vector3(0, TearY, 0)));
            Opened?.Invoke(GetGlobalTransform() * opening);
        }));
        after.SetParallel();
        after.TweenProperty(_burst, "light_energy", 0f, 0.4 * speed);
        after.TweenProperty(_pack, "position:y", -2.8f, 0.5 * speed).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        after.TweenProperty(_dim, "color:a", 0f, 0.5 * speed);
        after.Chain().TweenCallback(Callable.From(QueueFree));
    }

    // ---------------------------------------------------------------- the pack's picture

    /// <summary>
    /// The pack's texture: the front, from the booster's picture, on the left half; the back, plain in the picture's
    /// colors, on the right. The picture's sealed ends are stretched over the model's (taller) ones and the white around
    /// the photographed pack is painted over. Without a picture, a gradient in the card back's colors.
    /// Pixels are worked on as raw bytes (RGBA, one byte each), which is far quicker than one engine call per pixel.
    /// </summary>
    private static ImageTexture PackTexture(Image? photo, (double Top, double Bottom) seals, (Color A, Color B, Color Accent) plain)
    {
        const int size = 1024, half = size / 2;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        int sealTop = (int)(size * ModelSealTop), sealBottom = (int)(size * ModelSealBottom);
        if (photo is not null)
        {
            photo = (Image)photo.Duplicate();
            photo.ClearMipmaps();
            photo.Convert(Image.Format.Rgba8);
            var pack = photo.GetRegion(PackBounds(photo.GetData(), photo.GetWidth(), photo.GetHeight()));
            int h = pack.GetHeight();
            int top = (int)(h * seals.Top), bottom = (int)(h * seals.Bottom);
            Band(pack, 0, top, image, 0, sealTop);
            Band(pack, top, h - top - bottom, image, sealTop, size - sealTop - sealBottom);
            Band(pack, h - bottom, bottom, image, size - sealBottom, sealBottom);
        }
        else
        {
            for (int y = 0; y < size; y++)
                image.FillRect(new Rect2I(0, y, half, 1), plain.A.Lerp(plain.B, Mathf.Sin(y / (float)size * Mathf.Pi)));
        }

        var data = image.GetData();
        if (photo is not null)
        {
            PaintOverWhite(data, size, new Rect2I(0, 0, half, sealTop));
            PaintOverWhite(data, size, new Rect2I(0, size - sealBottom, half, sealBottom));
            for (int y = 0; y < size; y += 16)
            {
                PaintOverWhite(data, size, new Rect2I(0, y, 14, 16));
                PaintOverWhite(data, size, new Rect2I(half - 14, y, 14, 16));
            }
        }
        // The back: each row in the front row's average color, a little darker.
        for (int y = 0; y < size; y++)
        {
            int r = 0, g = 0, b = 0, n = 0;
            for (int x = 0; x < half; x += 8, n++)
            {
                int i = (y * size + x) * 4;
                r += data[i]; g += data[i + 1]; b += data[i + 2];
            }
            byte br = (byte)(r * 0.8f / n), bg = (byte)(g * 0.8f / n), bb = (byte)(b * 0.8f / n);
            for (int x = half; x < size; x++)
            {
                int i = (y * size + x) * 4;
                data[i] = br; data[i + 1] = bg; data[i + 2] = bb; data[i + 3] = 255;
            }
        }
        image.SetData(size, size, false, Image.Format.Rgba8, data);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The photographed pack inside its white background (an RGBA8 picture's bytes).</summary>
    private static Rect2I PackBounds(byte[] data, int w, int h)
    {
        bool Ink(int x, int y) { int i = (y * w + x) * 4; return Math.Min(data[i], Math.Min(data[i + 1], data[i + 2])) < 230; }
        bool Row(int y) { int n = 0; for (int x = 0; x < w; x += 2) if (Ink(x, y)) n++; return n > w / 20; }
        bool Column(int x) { int n = 0; for (int y = 0; y < h; y += 2) if (Ink(x, y)) n++; return n > h / 20; }
        int top = 0, bottom = h - 1, left = 0, right = w - 1;
        while (top < bottom && !Row(top)) top++;
        while (bottom > top && !Row(bottom)) bottom--;
        while (left < right && !Column(left)) left++;
        while (right > left && !Column(right)) right--;
        return new Rect2I(left, top, right - left + 1, bottom - top + 1);
    }

    private static void Band(Image source, int sourceY, int sourceHeight, Image target, int targetY, int targetHeight)
    {
        if (sourceHeight <= 0 || targetHeight <= 0) return;
        var band = source.GetRegion(new Rect2I(0, sourceY, source.GetWidth(), sourceHeight));
        band.Resize(target.GetWidth() / 2, targetHeight, Image.Interpolation.Lanczos);
        target.BlitRect(band, new Rect2I(0, 0, band.GetWidth(), band.GetHeight()), new Vector2I(0, targetY));
    }

    /// <summary>Near-white pixels in <paramref name="area"/> take the average color of the rest of it (RGBA8 bytes, <paramref name="width"/> wide).</summary>
    private static void PaintOverWhite(byte[] data, int width, Rect2I area)
    {
        bool White(int i) => Math.Min(data[i], Math.Min(data[i + 1], data[i + 2])) > 209;
        long r = 0, g = 0, b = 0;
        int count = 0;
        for (int y = area.Position.Y; y < area.End.Y; y++)
            for (int x = area.Position.X; x < area.End.X; x++)
            {
                int i = (y * width + x) * 4;
                if (White(i)) continue;
                r += data[i]; g += data[i + 1]; b += data[i + 2];
                count++;
            }
        if (count == 0) return;
        byte fr = (byte)(r / count), fg = (byte)(g / count), fb = (byte)(b / count);
        for (int y = area.Position.Y; y < area.End.Y; y++)
            for (int x = area.Position.X; x < area.End.X; x++)
            {
                int i = (y * width + x) * 4;
                if (!White(i)) continue;
                data[i] = fr; data[i + 1] = fg; data[i + 2] = fb; data[i + 3] = 255;
            }
    }
}
