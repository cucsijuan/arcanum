// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

public enum CardHighlight { None, Playable, Selected, Attacking, Blocking }

/// <summary>
/// Visual for one card: Scryfall image when cached, a text frame while it loads (or offline),
/// or Arcanum's card back when the card is hidden.
/// </summary>
public partial class CardNode : Control
{
    private static Shader? _roundedShader;
    private static Shader? _backShader;

    private readonly TextureRect _face = new();
    private readonly Panel _fallback = new();
    private readonly Label _fallbackName = BoardStyle.MakeLabel("", 11);
    private readonly Label _fallbackType = BoardStyle.MakeLabel("", 9, BoardStyle.TextDim);
    private readonly Label _fallbackPt = BoardStyle.MakeLabel("", 13);
    private readonly ColorRect _back = new();
    private static Shader? _glowShader;
    private const float GlowPad = 12;
    private readonly ColorRect _glow = new();
    private readonly Panel _selectedOverlay = new();
    private readonly HBoxContainer _pips = new();
    private readonly Label _damage = BoardStyle.MakeLabel("", 13, Colors.White);
    private readonly Panel _assigned = new();
    private readonly Label _assignedLabel = BoardStyle.MakeLabel("", 22, new Color("16171a"), bold: true);
    private string? _requestedImage;

    public CardId Id { get; private set; }
    public CardView? View { get; private set; }

    public event Action<CardNode>? Clicked;
    public event Action<CardNode>? HoverStarted;
    public event Action<CardNode>? HoverEnded;

    public bool IsHovered { get; private set; }

    /// <summary>Running move/rotate animation, killed when a new layout starts.</summary>
    public Tween? LayoutTween { get; set; }

    /// <summary>End state of the current layout animation (local to the parent).</summary>
    public Vector2 TargetPosition { get; set; }
    public Vector2 TargetSize { get; set; }

    /// <summary>Being dragged by the player; layout leaves it alone until released.</summary>
    public bool IsDragging { get; set; }

    public CardNode()
    {
        _roundedShader ??= GD.Load<Shader>("res://shaders/rounded_card.gdshader");
        _backShader ??= GD.Load<Shader>("res://shaders/card_back.gdshader");
        _glowShader ??= GD.Load<Shader>("res://shaders/glow_border.gdshader");

        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;

        _fallback.MouseFilter = MouseFilterEnum.Ignore;
        _fallback.SetAnchorsPreset(LayoutPreset.FullRect);
        var fallbackBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        fallbackBox.SetAnchorsPreset(LayoutPreset.FullRect);
        fallbackBox.OffsetLeft = 5; fallbackBox.OffsetTop = 4; fallbackBox.OffsetRight = -5; fallbackBox.OffsetBottom = -4;
        _fallbackName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _fallbackType.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var spacer = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _fallbackPt.HorizontalAlignment = HorizontalAlignment.Right;
        fallbackBox.AddChild(_fallbackName);
        fallbackBox.AddChild(_fallbackType);
        fallbackBox.AddChild(spacer);
        fallbackBox.AddChild(_fallbackPt);
        _fallback.AddChild(fallbackBox);
        AddChild(_fallback);

        _face.MouseFilter = MouseFilterEnum.Ignore;
        _face.SetAnchorsPreset(LayoutPreset.FullRect);
        _face.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _face.StretchMode = TextureRect.StretchModeEnum.Scale;
        _face.TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        _face.Material = new ShaderMaterial { Shader = _roundedShader };
        AddChild(_face);

        _back.MouseFilter = MouseFilterEnum.Ignore;
        _back.SetAnchorsPreset(LayoutPreset.FullRect);
        _back.Material = new ShaderMaterial { Shader = _backShader };
        AddChild(_back);

        _selectedOverlay.MouseFilter = MouseFilterEnum.Ignore;
        _selectedOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        _selectedOverlay.Visible = false;
        AddChild(_selectedOverlay);

        _glow.MouseFilter = MouseFilterEnum.Ignore;
        _glow.SetAnchorsPreset(LayoutPreset.FullRect);
        _glow.OffsetLeft = -GlowPad; _glow.OffsetTop = -GlowPad; _glow.OffsetRight = GlowPad; _glow.OffsetBottom = GlowPad;
        _glow.Material = new ShaderMaterial { Shader = _glowShader };
        _glow.Visible = false;
        AddChild(_glow);

        _pips.MouseFilter = MouseFilterEnum.Ignore;
        _pips.AddThemeConstantOverride("separation", 1);
        _pips.Position = new Vector2(2, -15);
        AddChild(_pips);

        var damageBg = new Panel { MouseFilter = MouseFilterEnum.Ignore, Name = "DamageBadge", Visible = false };
        damageBg.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("c0392b"), 10));
        damageBg.Size = new Vector2(24, 20);
        _damage.HorizontalAlignment = HorizontalAlignment.Center;
        _damage.Size = damageBg.Size;
        damageBg.AddChild(_damage);
        AddChild(damageBg);

        // Big badge for damage being assigned to this card (damage assignment UI).
        _assigned.MouseFilter = MouseFilterEnum.Ignore;
        _assigned.Size = new Vector2(40, 40);
        _assigned.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("ffb347"), 20, new Color("16171a"), 2));
        _assignedLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _assignedLabel.VerticalAlignment = VerticalAlignment.Center;
        _assignedLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        _assigned.AddChild(_assignedLabel);
        _assigned.Visible = false;
        AddChild(_assigned);

        MouseEntered += () => { IsHovered = true; HoverStarted?.Invoke(this); };
        MouseExited += () => { IsHovered = false; HoverEnded?.Invoke(this); };
        Resized += ApplySize;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            Clicked?.Invoke(this);
            AcceptEvent();
        }
    }

    /// <summary>Use a Lanczos-resampled texture at this node's exact size (for the large hover preview).</summary>
    public bool SharpImage { get; set; }

    public void Setup(CardView view, bool showCostPips)
    {
        Id = view.Id;
        View = view;

        _back.Visible = view.IsHidden;
        _face.Visible = false;
        _fallback.Visible = !view.IsHidden;
        _pips.Visible = showCostPips && !view.IsHidden;

        if (!view.IsHidden)
        {
            _fallbackName.Text = view.Name;
            _fallbackType.Text = TypeLine(view);
            _fallbackPt.Text = view.Power is { } p && view.Toughness is { } t ? $"{p}/{t}" : "";
            _fallback.AddThemeStyleboxOverride("panel", BoardStyle.Box(FrameColor(view), 6, new Color("0b0b0d"), 2));
            BuildPips(view.ManaCost);
            if (_requestedImage != view.Name)
            {
                _requestedImage = view.Name;
                var name = view.Name!;
                void Apply(Texture2D texture)
                {
                    if (!IsInstanceValid(this) || View?.Name != name) return;
                    _face.Texture = texture;
                    _face.Visible = !View.IsHidden;
                }
                if (SharpImage)
                {
                    _face.TextureFilter = TextureFilterEnum.Linear;
                    CardImageCache.RequestSharp(name, new Vector2I((int)Size.X, (int)Size.Y), Apply);
                }
                else
                {
                    CardImageCache.Request(name, Apply);
                }
            }
            else if (_face.Texture is not null)
            {
                _face.Visible = true;
            }
        }

        var damageBadge = GetNode<Panel>("DamageBadge");
        damageBadge.Visible = view.Damage > 0;
        _damage.Text = view.Damage.ToString();
        ApplySize();
    }

    public void SetHighlight(CardHighlight highlight)
    {
        _glow.Visible = highlight != CardHighlight.None;
        // Only a *selected* card gets a tinted overlay; everything else is just a glowing border.
        _selectedOverlay.Visible = highlight == CardHighlight.Selected;
        if (highlight == CardHighlight.None) return;

        var color = highlight switch
        {
            CardHighlight.Playable => BoardStyle.Playable,
            CardHighlight.Attacking => BoardStyle.Attacking,
            CardHighlight.Blocking => BoardStyle.Blocking,
            _ => BoardStyle.Selected,
        };
        ((ShaderMaterial)_glow.Material).SetShaderParameter("glow_color", color);
        if (_selectedOverlay.Visible)
            _selectedOverlay.AddThemeStyleboxOverride("panel", BoardStyle.Box(color with { A = 0.28f }, (int)(Size.X * 0.055f)));
    }

    /// <summary>Shows (or hides with null) the damage currently assigned to this card.</summary>
    public void SetAssignedDamage(int? amount)
    {
        _assigned.Visible = amount is not null;
        _assignedLabel.Text = amount?.ToString() ?? "";
    }

    private void ApplySize()
    {
        _assigned.Position = Size / 2 - _assigned.Size / 2;
        _assigned.PivotOffset = _assigned.Size / 2;
        _assigned.Rotation = -Rotation; // stays upright on tapped cards
        float radius = Size.X * 0.055f;
        ((ShaderMaterial)_face.Material).SetShaderParameter("rect_size", Size);
        ((ShaderMaterial)_face.Material).SetShaderParameter("radius", radius);
        ((ShaderMaterial)_back.Material).SetShaderParameter("rect_size", Size);
        ((ShaderMaterial)_back.Material).SetShaderParameter("radius", radius);
        var glow = (ShaderMaterial)_glow.Material;
        glow.SetShaderParameter("rect_size", Size + new Vector2(GlowPad * 2, GlowPad * 2));
        glow.SetShaderParameter("pad", GlowPad);
        glow.SetShaderParameter("radius", radius);
        PivotOffset = Size / 2;
        var badge = GetNodeOrNull<Panel>("DamageBadge");
        if (badge is not null) badge.Position = new Vector2(Size.X - 26, Size.Y * 0.55f);
    }

    private void BuildPips(string? cost)
    {
        foreach (var child in _pips.GetChildren()) child.QueueFree();
        foreach (var symbol in BoardStyle.ParseCostSymbols(cost)) _pips.AddChild(BoardStyle.MakePip(symbol));
    }

    private static string TypeLine(CardView view)
    {
        var types = Enum.GetValues<CardType>().Where(t => t != CardType.None && (view.Types & t) != 0);
        return string.Join(" ", types);
    }

    private static Color FrameColor(CardView view)
    {
        if ((view.Types & CardType.Land) != 0) return new Color("5b4a3a");
        var symbols = BoardStyle.ParseCostSymbols(view.ManaCost).Where(s => s.Length == 1 && "WUBRG".Contains(s[0])).Distinct().ToList();
        if (symbols.Count == 0) return new Color("6d6f75");
        if (symbols.Count > 1) return new Color("b8973a");
        return BoardStyle.PipColors(symbols[0][0]).Bg.Darkened(0.35f);
    }
}
