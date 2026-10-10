// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// The winner's celebration: the cards of their deck leave one after the other from places along the top of the screen
/// (as if laid there side by side), fall and bounce along the bottom, each bounce lower, until they leave by a side,
/// leaving a trail of themselves behind: a copy every <see cref="TrailSpacing"/> pixels of their path, however fast
/// they go, stamped on a canvas that is never cleared. A click or a key ends it.
/// </summary>
public partial class Celebration : Control
{
    private const float Gravity = 2200, Bounce = 0.78f;

    /// <summary>How far a card travels between the copies it leaves behind, so each copy still shows some of itself.</summary>
    private const float TrailSpacing = 30;
    private static readonly Vector2 CardSize = new(172, 240);

    private readonly SubViewport _canvas = new()
    {
        TransparentBg = true,
        RenderTargetClearMode = SubViewport.ClearMode.Once,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
    };
    private readonly Sprite2D _card = Sprite();

    /// <summary>The copies left this frame (shown on the canvas for this frame only), as many as the path since the last one holds.</summary>
    private readonly List<Sprite2D> _stamps = new();
    private float _travelled; // since the last copy

    private static Sprite2D Sprite() => new()
    {
        Centered = false, Visible = false,
        Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rounded_card.gdshader") }, // rounded corners, as on the table
    };
    private readonly Queue<Texture2D> _ready = new();
    private readonly RandomNumberGenerator _random = new();
    private Vector2 _velocity;
    private int _waiting;

    /// <summary>Closed by the player, or every card has gone.</summary>
    public event Action? Finished;

    /// <param name="cards">The cards to throw, in order (pictures still loading join as they arrive).</param>
    public Celebration(IEnumerable<CardView> cards, string title)
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 900;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        AddChild(_canvas);
        var trail = new TextureRect { Texture = _canvas.GetTexture(), MouseFilter = MouseFilterEnum.Ignore };
        trail.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(trail);
        AddChild(_card); // the card in flight, over its trail

        // The title stands on a dark translucent band, its letters outlined, so it reads over the cards going by.
        var banner = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var heading = BoardStyle.MakeTitle(title, 44);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        var hint = BoardStyle.MakeLabel("Click or press a key to continue", 15, BoardStyle.Text);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var (label, outline) in new[] { (heading, 10), (hint, 5) })
        {
            label.AddThemeColorOverride("font_outline_color", new Color(0.04f, 0.02f, 0.08f));
            label.AddThemeConstantOverride("outline_size", outline);
            banner.AddChild(label);
        }
        var band = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetTop = 34, GrowHorizontal = GrowDirection.Both };
        band.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.03f, 0.1f, 0.78f),
            CornerRadiusTopLeft = 18, CornerRadiusTopRight = 18, CornerRadiusBottomLeft = 18, CornerRadiusBottomRight = 18,
            BorderColor = new Color(0.96f, 0.85f, 0.56f, 0.45f), BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginLeft = 48, ContentMarginRight = 48, ContentMarginTop = 10, ContentMarginBottom = 12,
            ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 16,
        });
        band.AddChild(banner);
        AddChild(band);

        foreach (var view in cards)
        {
            if (CardImageCache.KeyFor(view) is not { } key) continue;
            _waiting++;
            CardImageCache.Request(key, texture =>
            {
                _waiting--;
                if (IsInstanceValid(this)) _ready.Enqueue(texture);
            });
        }
        var fanfare = new AudioStreamPlayer { Bus = "Master" };
        // C5 E5 G5 C6, rising.
        fanfare.Stream = TurnChime.Render(1.3, (0.0, 523.25, 0.35, 0.7), (0.14, 659.25, 0.35, 0.75), (0.28, 783.99, 0.4, 0.8), (0.42, 1046.5, 0.8, 0.9));
        AddChild(fanfare);
        double effects = Math.Clamp(Settings.Current.EffectsVolume, 0, 1);
        if (effects > 0)
        {
            fanfare.VolumeDb = Mathf.LinearToDb((float)effects);
            fanfare.Ready += () => fanfare.Play();
        }
    }

    public override void _Ready()
    {
        _canvas.Size = (Vector2I)GetViewportRect().Size;
        _random.Randomize();
    }

    public override void _Process(double delta)
    {
        var screen = GetViewportRect().Size;
        foreach (var stamp in _stamps) stamp.Visible = false;
        if (!_card.Visible)
        {
            if (_ready.Count > 0) Launch(_ready.Dequeue(), screen);
            else if (_waiting == 0) Close();
            return;
        }
        float dt = (float)Math.Min(delta, 1 / 30.0);
        _velocity.Y += Gravity * dt;
        var position = _card.Position + _velocity * dt;
        float floor = screen.Y - CardSize.Y;
        if (position.Y > floor)
        {
            position.Y = floor;
            _velocity.Y = -_velocity.Y * Bounce;
        }
        // Copies every TrailSpacing pixels of the path, placed back along this frame's step when it covers more than one.
        var previous = _card.Position;
        float step = previous.DistanceTo(position);
        _travelled += step;
        int used = 0;
        while (_travelled >= TrailSpacing && step > 0)
        {
            _travelled -= TrailSpacing;
            Stamp(used++, position + (previous - position) / step * _travelled);
        }
        _card.Position = position;
        if (position.X < -CardSize.X || position.X > screen.X) _card.Visible = false; // gone by a side: the next one goes
    }

    private void Stamp(int index, Vector2 at)
    {
        if (index == _stamps.Count)
        {
            var stamp = Sprite();
            _canvas.AddChild(stamp);
            _stamps.Add(stamp);
            Dress(stamp, (Texture2D)_card.Texture);
        }
        _stamps[index].Position = at;
        _stamps[index].Visible = true;
    }

    private static void Dress(Sprite2D sprite, Texture2D texture)
    {
        sprite.Texture = texture;
        sprite.Scale = CardSize / texture.GetSize();
        var material = (ShaderMaterial)sprite.Material;
        material.SetShaderParameter("rect_size", texture.GetSize());
        material.SetShaderParameter("radius", texture.GetSize().X * 0.055f);
    }

    /// <summary>
    /// A card leaves from one of the places along the top of the screen where cards laid side by side would be,
    /// towards either side.
    /// </summary>
    private void Launch(Texture2D texture, Vector2 screen)
    {
        Dress(_card, texture);
        foreach (var stamp in _stamps) Dress(stamp, texture);
        int places = Math.Max(1, (int)(screen.X / CardSize.X));
        float left = (screen.X - places * CardSize.X) / 2;
        _card.Position = new Vector2(left + _random.RandiRange(0, places - 1) * CardSize.X, 24);
        _travelled = 0;
        float speed = _random.RandfRange(360, 870);
        _velocity = new Vector2(_random.Randf() < 0.5f ? -speed : speed, _random.RandfRange(-700, 0));
        _card.Visible = true;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) Close();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true }) Close();
    }

    private void Close()
    {
        if (IsQueuedForDeletion()) return;
        Finished?.Invoke();
        QueueFree();
    }
}
