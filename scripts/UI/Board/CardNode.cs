// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

public enum CardHighlight { None, Playable, Selected, Attacking, Blocking }

/// <summary>
/// Visual for one card: its image when available, a text frame while it loads (or offline),
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

    // Number tags, one per corner so they never overlap: bottom-right power/toughness (damage shown in it) or loyalty,
    // with the Saga chapter beside it; top-left the size of a stack of identical tokens; top-right counters;
    // bottom-left status marks such as summoning sickness. All of them hide on the cards behind the top of a stack.
    private readonly Control _badgeLayer = new();
    private readonly PanelContainer _ptBadge = new();
    private readonly Label _ptLabel = BoardStyle.MakeLabel("", 13, Colors.White, bold: true);
    private readonly Label _toughnessLabel = BoardStyle.MakeLabel("", 13, Colors.White, bold: true);
    private readonly PanelContainer _stackBadge = new();
    private readonly Label _stackLabel = BoardStyle.MakeLabel("", 14, Colors.White, bold: true);
    private readonly PanelContainer _marksBadge = new();
    private readonly Label _marksLabel = BoardStyle.MakeLabel("", 12, Colors.White, bold: true);
    private readonly PanelContainer _loreBadge = new();
    private readonly Label _loreLabel = BoardStyle.MakeLabel("", 13, Colors.White, bold: true);
    private readonly PanelContainer _counterBadge = new();
    private readonly Label _counterLabel = BoardStyle.MakeLabel("", 11, Colors.White);
    private readonly PanelContainer _caption = new();
    private readonly Label _captionLabel = BoardStyle.MakeLabel("", 12, Colors.White, bold: true);
    private readonly Panel _assigned = new();
    private readonly Label _assignedLabel = BoardStyle.MakeLabel("", 22, new Color("16171a"), bold: true);
    private string? _requestedImage;

    public CardId Id { get; private set; }
    public CardView? View { get; private set; }

    public event Action<CardNode>? Clicked;
    public event Action<CardNode>? RightClicked;
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
        _fallback.ClipContents = true; // small cards: text never spills outside the card
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

        _badgeLayer.MouseFilter = MouseFilterEnum.Ignore;
        _badgeLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_badgeLayer);

        // Current power/toughness (with the damage marked on it) in the bottom-right corner.
        _ptBadge.MouseFilter = MouseFilterEnum.Ignore;
        var ptRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        ptRow.AddThemeConstantOverride("separation", 0);
        ptRow.AddChild(_ptLabel);
        ptRow.AddChild(_toughnessLabel);
        _ptBadge.AddChild(ptRow);
        _ptBadge.Visible = false;
        _badgeLayer.AddChild(_ptBadge);
        // A Saga's chapter reached / final chapter, in the same corner (left of the power/toughness if it has both).
        _loreBadge.MouseFilter = MouseFilterEnum.Ignore;
        _loreBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("4a3a6b"), 6, new Color("0b0b0d"), 1));
        _loreBadge.AddChild(_loreLabel);
        _loreBadge.Visible = false;
        _badgeLayer.AddChild(_loreBadge);
        // Counters in the top-right corner, under the title.
        _counterBadge.MouseFilter = MouseFilterEnum.Ignore;
        _counterBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.1f, 0.1f, 0.12f, 0.9f), 8, BoardStyle.Playable, 1));
        _counterLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _counterBadge.AddChild(_counterLabel);
        _counterBadge.Visible = false;
        _badgeLayer.AddChild(_counterBadge);
        // How many identical tokens this card stands for, top-left under the title.
        _stackBadge.MouseFilter = MouseFilterEnum.Ignore;
        _stackBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.06f, 0.06f, 0.08f, 0.94f), 9, Colors.White, 1));
        _stackBadge.AddChild(_stackLabel);
        _stackBadge.Visible = false;
        _badgeLayer.AddChild(_stackBadge);
        // Status marks (summoning sickness...), bottom-left.
        _marksBadge.MouseFilter = MouseFilterEnum.Ignore;
        _marksBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.1f, 0.1f, 0.12f, 0.9f), 6, new Color("8c8f99"), 1));
        _marksBadge.AddChild(_marksLabel);
        _marksBadge.Visible = false;
        _badgeLayer.AddChild(_marksBadge);

        // Short caption over the card, e.g. which player an attacker goes after.
        _caption.MouseFilter = MouseFilterEnum.Ignore;
        _caption.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.05f, 0.05f, 0.06f, 0.9f), 6, BoardStyle.Attacking, 1));
        _caption.AddChild(_captionLabel);
        _caption.Visible = false;
        AddChild(_caption);

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

        MouseEntered += () => { IsHovered = true; ShineFoil(true); HoverStarted?.Invoke(this); };
        MouseExited += () => { IsHovered = false; ShineFoil(false); HoverEnded?.Invoke(this); };
        Resized += ApplySize;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && IsFoil && Size.X > 0 && Size.Y > 0)
            FoilAt = (motion.Position / Size).Clamp(Vector2.Zero, Vector2.One);
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            Clicked?.Invoke(this);
            AcceptEvent();
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            RightClicked?.Invoke(this);
            AcceptEvent();
        }
    }

    // ---------------------------------------------------------------- foil

    private float _foilShine;
    private Vector2 _foilAt = new(0.5f, 0.5f);
    private Tween? _foilTween;
    private CardNode? _foilSource;

    /// <summary>A foil copy shown face up: its picture shines under the pointer.</summary>
    public bool IsFoil => View is { Foil: true, IsHidden: false };

    /// <summary>Where the pointer is on the card (0..1 across and down): the center of the foil shine.</summary>
    public Vector2 FoilAt
    {
        get => _foilAt;
        set { _foilAt = value; ((ShaderMaterial)_face.Material).SetShaderParameter("foil_at", value); }
    }

    /// <summary>How much the foil shines (0–1): it fades in while the pointer is on the card.</summary>
    public float FoilShine
    {
        get => _foilShine;
        set { _foilShine = value; ((ShaderMaterial)_face.Material).SetShaderParameter("foil", IsFoil ? value * BoardStyle.FoilShine : 0f); }
    }

    /// <summary>Fades the foil shine in or out.</summary>
    public void ShineFoil(bool on)
    {
        if (_foilSource is not null) return;
        _foilTween?.Kill();
        if (!IsFoil) { FoilShine = 0; return; }
        _foilTween = CreateTween();
        _foilTween.TweenMethod(Callable.From<float>(v => FoilShine = v), FoilShine, on ? 1f : 0f, 0.25);
    }

    /// <summary>
    /// Shines like <paramref name="source"/> (the large preview of a hovered card follows the pointer on the small one);
    /// null to stop.
    /// </summary>
    public void MirrorFoil(CardNode? source)
    {
        _foilSource = source;
        _foilTween?.Kill();
        SetProcess(source is not null);
        if (source is null) FoilShine = 0;
    }

    // Only a mirroring preview needs to run every frame (overriding _Process turns processing on at ready).
    public override void _Ready() => SetProcess(_foilSource is not null);

    public override void _Process(double delta)
    {
        if (_foilSource is null) return;
        if (!IsInstanceValid(_foilSource)) { MirrorFoil(null); return; }
        FoilAt = _foilSource.FoilAt;
        FoilShine = _foilSource.FoilShine;
    }

    /// <summary>Use a Lanczos-resampled texture at this node's exact size (for the large hover preview).</summary>
    public bool SharpImage { get; set; }

    /// <summary>Shows the counters tag (off for the large preview, which lists counters beside the card).</summary>
    public bool ShowCounterBadge { get; set; } = true;

    public void Setup(CardView view, bool showCostPips)
    {
        Id = view.Id;
        View = view;

        _back.Visible = view.IsHidden;
        FoilShine = _foilSource is not null ? FoilShine : IsHovered ? 1 : 0;
        if (view.IsHidden)
        {
            // Applied on every setup so the current card back setting is used even by nodes created earlier.
            var (backA, backB, accent) = BoardStyle.CardBackColors(BoardStyle.CardBack);
            var back = (ShaderMaterial)_back.Material;
            back.SetShaderParameter("color_a", backA);
            back.SetShaderParameter("color_b", backB);
            back.SetShaderParameter("accent", accent);
        }
        _face.Visible = false;
        _fallback.Visible = !view.IsHidden;
        _pips.Visible = showCostPips && !view.IsHidden;

        if (!view.IsHidden)
        {
            _fallbackName.Text = view.Name;
            _fallbackType.Text = view.Keywords.Count > 0 ? $"{TypeLine(view)}\n{string.Join(", ", view.Keywords)}" : TypeLine(view);
            _fallbackPt.Text = view.Power is { } p && view.Toughness is { } t ? $"{p}/{t}" : "";
            _fallback.AddThemeStyleboxOverride("panel", BoardStyle.Box(FrameColor(view), 6, new Color("0b0b0d"), 2));
            BuildPips(view.ManaCost);
            var imageKey = CardImageCache.KeyFor(view);
            if (imageKey is null)
            {
                _requestedImage = null;
                _face.Texture = null;
                _face.Visible = false; // e.g. a token without an exact picture: keep the text frame
            }
            else if (_requestedImage != imageKey)
            {
                _requestedImage = imageKey;
                var name = view.Name!;
                void Apply(Texture2D texture)
                {
                    if (!IsInstanceValid(this) || View?.Name != name) return;
                    _face.Texture = texture;
                    _face.Visible = !View.IsHidden;
                    _fallback.Visible = !_face.Visible; // the picture replaces the text frame entirely
                }
                if (SharpImage)
                {
                    _face.TextureFilter = TextureFilterEnum.Linear;
                    CardImageCache.RequestSharp(imageKey, new Vector2I((int)Size.X, (int)Size.Y), Apply);
                }
                else
                {
                    CardImageCache.Request(imageKey, Apply);
                }
            }
            else if (_face.Texture is not null)
            {
                _face.Visible = true;
            }
            _fallback.Visible = !_face.Visible;
            _fallbackPt.Visible = view.Zone != Arcanum.Engine.State.Zone.Battlefield; // on the battlefield the corner badge shows P/T
        }

        bool onBattlefield = !view.IsHidden && view.Zone == Arcanum.Engine.State.Zone.Battlefield;

        // Creatures on the battlefield always show P/T in their bottom-right corner (it rotates with the card):
        // gray as printed, green when raised, red when lowered. Damage marked on it is shown in the same tag:
        // the toughness left, in red.
        bool showPt = onBattlefield && view.Power is not null && view.Toughness is not null;
        bool showLoyalty = !showPt && onBattlefield && (view.Types & Arcanum.Engine.Cards.CardType.Planeswalker) != 0;
        _ptBadge.Visible = showPt || showLoyalty;
        _toughnessLabel.Text = "";
        if (showLoyalty)
        {
            // Planeswalkers show their loyalty in the same corner.
            _ptBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("5a4a1e"), 6, new Color("0b0b0d"), 1));
            _ptLabel.Text = $"\u25c6 {view.Loyalty}";
        }
        if (showPt)
        {
            int now = view.Power!.Value + view.Toughness!.Value, printed = (view.BasePower ?? 0) + (view.BaseToughness ?? 0);
            bool changed = view.Power != view.BasePower || view.Toughness != view.BaseToughness;
            bool damaged = view.Damage > 0;
            var color = damaged ? new Color("1a1214") : !changed ? new Color("3a3b42") : now >= printed ? new Color("1f7a43") : new Color("a3302a");
            _ptBadge.AddThemeStyleboxOverride("panel", damaged
                ? BoardStyle.Box(color, 6, new Color("e0453a"), 2)
                : BoardStyle.Box(color, 6, new Color("0b0b0d"), 1));
            _ptLabel.Text = $"{view.Power}/";
            _toughnessLabel.Text = (view.Toughness!.Value - view.Damage).ToString();
            var toughnessColor = damaged ? new Color("ff6b5e") : Colors.White;
            _toughnessLabel.AddThemeColorOverride("font_color", toughnessColor);
            _toughnessLabel.AddThemeColorOverride("font_outline_color", toughnessColor);
        }
        else _ptLabel.AddThemeColorOverride("font_color", Colors.White);
        bool showLore = onBattlefield && view.FinalChapter > 0;
        _loreBadge.Visible = showLore;
        if (showLore) _loreLabel.Text = $"{Roman(view.LoreCounters)}/{Roman(view.FinalChapter)}";

        // Counters, top-right: one line each, only for kinds nothing else shows (+1/+1 and -1/-1 are in the power/toughness,
        // lore in the chapter tag, and the large preview lists them all beside the card).
        var counters = new List<string>();
        foreach (var (kind, count) in view.OtherCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (count > 0 && !(kind == nameof(Arcanum.Engine.Abilities.CounterKind.Lore) && view.FinalChapter > 0)) counters.Add($"{kind} \u00d7{count}");
        _counterBadge.Visible = counters.Count > 0 && onBattlefield && ShowCounterBadge;
        _counterLabel.Text = string.Join("\n", counters);

        // Status marks, bottom-left.
        var marks = new List<string>();
        if (onBattlefield && view.SummoningSick && (view.Types & Arcanum.Engine.Cards.CardType.Creature) != 0) marks.Add("zz");
        if (onBattlefield && view.AttacksEachCombat) marks.Add("\u2694");
        _marksBadge.Visible = marks.Count > 0;
        _marksLabel.Text = string.Join(" ", marks);
        ApplySize();
    }

    /// <summary>
    /// This card stands for a stack of <paramref name="count"/> identical tokens (the "\u00d7N" tag shows when more than one).
    /// A card behind the top of a stack shows no tags and ignores the mouse.
    /// </summary>
    public void SetStack(int count, bool behind)
    {
        _stackBadge.Visible = count > 1 && !behind;
        _stackLabel.Text = $"\u00d7{count}";
        _badgeLayer.Visible = !behind;
        MouseFilter = behind ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
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

    /// <summary>Shows (or hides with null) a short caption above the card.</summary>
    public void SetCaption(string? text)
    {
        _caption.Visible = text is not null;
        _captionLabel.Text = text ?? "";
        _caption.ResetSize();
        _caption.Position = new Vector2((Size.X - _caption.Size.X) / 2, -_caption.Size.Y - 4);
    }

    /// <summary>Shows (or hides with null) the damage currently assigned to this card.</summary>
    public void SetAssignedDamage(int? amount)
    {
        _assigned.Visible = amount is not null;
        _assignedLabel.Text = amount?.ToString() ?? "";
    }

    private void ApplySize()
    {
        const float Inset = 2;
        _ptBadge.ResetSize();
        _ptBadge.Position = new Vector2(Size.X - _ptBadge.Size.X - Inset, Size.Y - _ptBadge.Size.Y - Inset);
        _loreBadge.ResetSize();
        float loreRight = _ptBadge.Visible ? _ptBadge.Position.X - 2 : Size.X - Inset;
        _loreBadge.Position = new Vector2(loreRight - _loreBadge.Size.X, Size.Y - _loreBadge.Size.Y - Inset);
        // Top tags start below the title and type lines, so the card's name stays readable.
        float top = Mathf.Round(Size.Y * 0.28f);
        _stackBadge.ResetSize();
        _stackBadge.Position = new Vector2(Inset, top);
        _counterBadge.ResetSize();
        float counterTop = top;
        // On a narrow card the two tags would meet: counters go below the count.
        if (_stackBadge.Visible && _counterBadge.Visible && _stackBadge.Size.X + _counterBadge.Size.X + 3 * Inset > Size.X)
            counterTop += _stackBadge.Size.Y + 2;
        _counterBadge.Position = new Vector2(Size.X - _counterBadge.Size.X - Inset, counterTop);
        _marksBadge.ResetSize();
        _marksBadge.Position = new Vector2(Inset, Size.Y - _marksBadge.Size.Y - Inset);
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
    }

    private void BuildPips(string? cost)
    {
        foreach (var child in _pips.GetChildren()) child.QueueFree();
        foreach (var symbol in BoardStyle.ParseCostSymbols(cost)) _pips.AddChild(BoardStyle.MakePip(symbol));
    }

    /// <summary>Chapter numbers are written in Roman numerals ("0" before the first lore counter, Arabic past 10).</summary>
    private static string Roman(int n) => n switch
    {
        <= 0 or > 10 => n.ToString(),
        _ => new[] { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" }[n - 1],
    };

    private static string TypeLine(CardView view)
    {
        var types = Enum.GetValues<CardType>().Where(t => t != CardType.None && (view.Types & t) != 0);
        return string.Join(" ", types);
    }

    private static Color FrameColor(CardView view)
    {
        if ((view.Types & CardType.Land) != 0) return new Color("5b4a3a");
        var symbols = BoardStyle.ParseCostSymbols(view.ManaCost).Where(s => s.Length == 1 && "WUBRG".Contains(s[0])).Distinct().ToList();
        if (symbols.Count == 0) symbols = view.Colors.ToList(); // tokens have colors but no mana cost
        if (symbols.Count == 0) return new Color("6d6f75");
        if (symbols.Count > 1) return new Color("b8973a");
        return BoardStyle.PipColors(symbols[0][0]).Bg.Darkened(0.35f);
    }
}
