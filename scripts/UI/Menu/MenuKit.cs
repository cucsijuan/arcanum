// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Shared building blocks for the menu screens so they look consistent.</summary>
public static class MenuKit
{
    /// <summary>A card as the menus show it (outside a game), with its other face when it is double-faced.</summary>
    public static Arcanum.Engine.Views.CardView CardViewOf(Arcanum.Engine.Cards.CardDefinition d, int id = -1) => new()
    {
        Id = new Arcanum.Engine.Core.CardId(id), Owner = new Arcanum.Engine.Core.PlayerId(0), Controller = new Arcanum.Engine.Core.PlayerId(0),
        Zone = Arcanum.Engine.State.Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey, Foil = d.Foil, Surge = d.Surge, Fracture = d.Fracture,
        OtherFace = d.BackFace is { } back ? new()
        {
            Id = new Arcanum.Engine.Core.CardId(id), Owner = new Arcanum.Engine.Core.PlayerId(0), Controller = new Arcanum.Engine.Core.PlayerId(0),
            Zone = Arcanum.Engine.State.Zone.Hand, IsHidden = false,
            Name = back.Name, ManaCost = back.ManaCost.ToString(), Types = back.Types, Power = back.Power, Toughness = back.Toughness,
            Keywords = back.Keywords, BasePower = back.Power, BaseToughness = back.Toughness, ImageKey = back.ImageKey ?? d.ImageKey,
            Foil = d.Foil, Surge = d.Surge, Fracture = d.Fracture, Transformed = true, IsBackFace = true,
        } : null,
    };

    /// <summary>The menus' backdrop: the animated arcane sky, darker behind screens full of panels and text.</summary>
    public static void AddBackdrop(Control screen) => AddArcaneBackdrop(screen, shade: 0.5f);

    /// <summary>The nebula is drawn this many times smaller than the screen.</summary>
    private const int NebulaScale = 4;

    /// <summary>
    /// Animated backdrop: a drifting nebula, twinkling stars and a faint arcane circle of turning rune rings behind the
    /// right half of the screen.
    /// </summary>
    /// <returns>The backdrop's material (for its parameters, e.g. where the circle stands).</returns>
    public static ShaderMaterial AddArcaneBackdrop(Control screen, float shade = 0.25f)
    {
        var sky = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        sky.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/arcane_backdrop.gdshader") };
        sky.Material = material;
        // The nebula, the costly part, has no fine detail: drawn at a fraction of the size and stretched.
        var nebula = new SubViewport { Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = new Vector2I(480, 270) };
        var nebulaMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/arcane_nebula.gdshader") };
        var clouds = new ColorRect { Material = nebulaMaterial };
        clouds.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        nebula.AddChild(clouds);
        sky.AddChild(nebula);
        material.SetShaderParameter("nebula", nebula.GetTexture());
        sky.Resized += () =>
        {
            material.SetShaderParameter("rect_size", sky.Size);
            nebulaMaterial.SetShaderParameter("rect_size", sky.Size);
            nebula.Size = new Vector2I(Math.Max(16, (int)sky.Size.X / NebulaScale), Math.Max(16, (int)sky.Size.Y / NebulaScale));
        };
        screen.AddChild(sky);
        if (shade <= 0) return material;
        var dim = new ColorRect { Color = new Color(0.03f, 0.02f, 0.05f, shade), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(dim);
        return material;
    }

    /// <summary>Top bar with a back button and a title; returns the bar so screens can add actions to it.</summary>
    public static HBoxContainer AddHeader(Control screen, string title, Action? onBack = null)
    {
        var bar = new HBoxContainer { AnchorRight = 1, OffsetLeft = 24, OffsetTop = 20, OffsetRight = -24, OffsetBottom = 76 };
        bar.AddThemeConstantOverride("separation", 16);
        var back = BoardStyle.MakeButton("←  Back", 16);
        back.CustomMinimumSize = new Vector2(120, 44);
        back.Pressed += onBack ?? (() => App.Instance.GoTo(App.MainMenuScene));
        PadFocus.BackButton(back);
        bar.AddChild(back);
        var label = BoardStyle.MakeTitle(title, 32);
        label.VerticalAlignment = VerticalAlignment.Center;
        bar.AddChild(label);
        bar.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        screen.AddChild(bar);
        return bar;
    }

    public static PanelContainer Card(Control content, int padding = 20)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Menu, new Color(0.09f, 0.09f, 0.11f, 0.92f), 12, BoardStyle.PanelBorder, 1, padding: padding));
        panel.AddChild(content);
        return panel;
    }

    /// <summary>A section heading in the display font with a divider under it (a plain line without the art).</summary>
    public static Label SectionTitle(string text)
    {
        var label = BoardStyle.MakeTitle(text, 20);
        const float dividerHeight = 20;
        label.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginBottom = dividerHeight + 4 });
        var divider = UiArt.Divider(dividerHeight);
        divider.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        divider.OffsetTop = -dividerHeight;
        divider.OffsetBottom = 0;
        label.AddChild(divider);
        return label;
    }

    public static Label Hint(string text)
    {
        var label = BoardStyle.MakeLabel(text, 13, BoardStyle.TextDim);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // A wrapping label has no minimum width; expanding keeps it from collapsing inside rows.
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    public static OptionButton Options(IEnumerable<string> items, int selected = 0)
    {
        // Not as wide as the longest item (a list of every set would push the screen out of the window); long texts are cut.
        var option = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(190, 40), FitToLongestItem = false, ClipText = true };
        option.AddThemeFontSizeOverride("font_size", 15);
        // Room for the drop-down arrow inside the frame's right corner.
        if (UiArt.StyleButton(option, false)) option.AddThemeConstantOverride("arrow_margin", 22);
        else
        {
            option.AddThemeStyleboxOverride("normal", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
            option.AddThemeStyleboxOverride("hover", BoardStyle.Box(new Color("26272c"), 6, BoardStyle.TextDim, 1));
        }
        foreach (var item in items) option.AddItem(item);
        if (option.ItemCount > 0) option.Selected = Math.Clamp(selected, 0, option.ItemCount - 1);
        return option;
    }

    public static LineEdit TextField(string text = "", string placeholder = "")
    {
        var field = new LineEdit { Text = text, PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 40) };
        field.AddThemeFontSizeOverride("font_size", 15);
        // A dark well with a faint gold edge (gold when typing in it).
        var well = UiArt.Available ? UiArt.Well : new Color("101114");
        var edge = UiArt.Available ? UiArt.Gold with { A = 0.3f } : BoardStyle.PanelBorder;
        var normal = BoardStyle.Box(well, 4, edge, 1);
        normal.ContentMarginLeft = normal.ContentMarginRight = 10;
        var focus = (StyleBoxFlat)normal.Duplicate();
        focus.BorderColor = UiArt.Available ? UiArt.Gold : BoardStyle.Playable;
        field.AddThemeStyleboxOverride("normal", normal);
        field.AddThemeStyleboxOverride("focus", focus);
        return field;
    }

    public static CheckButton Toggle(string text, bool on)
    {
        var toggle = new CheckButton { Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };
        toggle.AddThemeFontSizeOverride("font_size", 15);
        return toggle;
    }

    /// <summary>Compact on/off button that stays readable on dark backgrounds (unlike the default check box).</summary>
    public static Button Check(bool on, Color onColor, string tooltip = "")
    {
        var button = new Button { ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 30), TooltipText = tooltip };
        void Style()
        {
            bool pressed = button.ButtonPressed;
            button.Text = pressed ? "✓" : "";
            var box = UiArt.ButtonFill(pressed ? onColor : UiArt.Well, compact: true)
                ?? BoardStyle.Box(pressed ? onColor : new Color("1c1d21"), 6, pressed ? onColor : BoardStyle.TextDim with { A = 0.5f }, 1);
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" }) button.AddThemeStyleboxOverride(state, box);
            button.AddThemeColorOverride("font_color", new Color("16171a"));
            button.AddThemeColorOverride("font_pressed_color", new Color("16171a"));
            button.AddThemeColorOverride("font_hover_pressed_color", new Color("16171a"));
        }
        Style();
        button.Toggled += _ => Style();
        return button;
    }

    public static HSlider Slider(double min, double max, double step, double value)
    {
        return new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(260, 28), FocusMode = Control.FocusModeEnum.None };
    }

    /// <summary>A label + control row for forms.</summary>
    public static HBoxContainer Row(string label, Control control, float labelWidth = 220)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        var l = BoardStyle.MakeLabel(label, 15);
        l.CustomMinimumSize = new Vector2(labelWidth, 0);
        l.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(l);
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(control);
        return row;
    }

    /// <summary>Short message that fades out at the bottom of the screen.</summary>
    public static void Toast(Control screen, string text)
    {
        var label = BoardStyle.MakeLabel(text, 16);
        var panel = Card(label, 12);
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 1; panel.AnchorBottom = 1;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.OffsetBottom = -40;
        panel.ZIndex = 500;
        screen.AddChild(panel);
        var tween = panel.CreateTween();
        tween.TweenInterval(1.8);
        tween.TweenProperty(panel, "modulate:a", 0.0f, 0.5);
        tween.TweenCallback(Callable.From(panel.QueueFree));
    }

    /// <summary>
    /// The cards of a booster in rows filling the space given to the list, as large as they all fit in (up to
    /// <paramref name="full"/>, shrinking on a small screen); <paramref name="count"/> places are kept, so the size
    /// stays the same as the cards are taken. Each child of <paramref name="cards"/> holds a <see cref="CardNode"/>.
    /// Too small to read, they scroll.
    /// </summary>
    public static ScrollContainer FittedCards(Control cards, int count, Vector2 full, float gap)
    {
        const float edge = 6, smallest = 90; // the edge is room for the focus ring around the outer cards
        var scroll = new ScrollContainer
        {
            FollowFocus = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(cards);
        var placed = Vector2.Zero;
        scroll.Resized += () =>
        {
            if (scroll.Size == placed || !GodotObject.IsInstanceValid(cards)) return;
            placed = scroll.Size;
            var area = placed - new Vector2(edge, edge) * 2;
            float width = full.X;
            int Columns() => Math.Max(1, (int)((area.X + gap) / (width + gap)));
            float Height() => Mathf.Floor(width * full.Y / full.X);
            float Total(int n) => (n + Columns() - 1) / Columns() * (Height() + gap) - gap;
            while (width > smallest && Total(count) > area.Y) width--;
            if (Total(count) > area.Y) area.X -= 24; // the scroll bar's
            var size = new Vector2(width, Height());
            int columns = Columns(), i = 0;
            for (int child = 0; child < cards.GetChildCount(); child++)
            {
                if (cards.GetChild(child) is not Control holder) continue;
                holder.CustomMinimumSize = size;
                holder.Size = size;
                holder.Position = new Vector2(edge + i % columns * (width + gap), edge + i / columns * (size.Y + gap));
                i++;
                for (int c = 0; c < holder.GetChildCount(); c++)
                    if (holder.GetChild(c) is CardNode card) card.Size = size;
            }
            cards.CustomMinimumSize = new Vector2(0, Total(i) + 2 * edge);
        };
        return scroll;
    }

    /// <summary>
    /// A modal dialog over <paramref name="screen"/>: a title, a text, optional content and its answers in a row (the
    /// last one primary); choosing an answer runs it and closes the dialog.
    /// </summary>
    public static Control Modal(Control screen, string title, string text, Control? content, params (string Text, Action Action)[] answers)
    {
        var overlay = new Control { ZIndex = 200, MouseFilter = Control.MouseFilterEnum.Stop };
        overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(shade);
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Modal, BoardStyle.Panel, 12, BoardStyle.PanelBorder, 1, artPadding: 24));
        overlay.AddChild(panel);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        var heading = BoardStyle.MakeTitle(title, 26);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(heading);
        if (text.Length > 0)
        {
            var body = BoardStyle.MakeLabel(text, 16);
            body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            body.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(body);
        }
        if (content is not null) box.AddChild(content);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        for (int i = 0; i < answers.Length; i++)
        {
            if (i > 0) buttons.AddChild(BoardStyle.MakeModalSeparator());
            var (label, action) = answers[i];
            var button = BoardStyle.MakeModalButton(label, i == answers.Length - 1, 18);
            button.Pressed += () =>
            {
                overlay.QueueFree();
                action();
            };
            buttons.AddChild(button);
        }
        screen.AddChild(overlay);
        PadFocus.Cover(overlay);
        PadFocus.OfferModal(buttons, buttons.GetChildren().OfType<Button>().LastOrDefault());
        return overlay;
    }
}
