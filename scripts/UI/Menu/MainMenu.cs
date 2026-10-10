// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Title screen: play, decks, settings, extras. The options are large text; a selector (an arrow with a glow under the
/// text, trailing sparks) glides to the option under the pointer or chosen with the arrow keys.
/// </summary>
public partial class MainMenu : Control
{
    private readonly Label _status = BoardStyle.MakeLabel("", 14, BoardStyle.TextDim);
    private readonly List<Button> _items = new();
    private readonly Control _selector = new() { MouseFilter = MouseFilterEnum.Ignore };
    private Control _list = null!;
    private int _selected = -1;
    private Tween? _move;

    private static bool _opened;
    private static bool _checkedUpdates;

    private static readonly Color ItemColor = new(0.86f, 0.88f, 0.92f);
    private static readonly Color GlowColor = new(0.62f, 0.38f, 1f, 0.55f);

    public override void _Ready()
    {
        // ARCANUM_OPEN=online opens the online screen at start (testing exported builds, which can't be given a scene).
        if (OS.GetEnvironment("ARCANUM_OPEN") == "online" && !_opened)
        {
            _opened = true;
            Callable.From(() => App.Instance.GoTo(App.OnlineScene)).CallDeferred();
        }
        SetAnchorsPreset(LayoutPreset.FullRect);
        var backdrop = MenuKit.AddArcaneBackdrop(this);
        var fan = AddFannedCardBacks();
        // The arcane circle stands behind the fan of cards, centered on it at any window size.
        void CenterCircle()
        {
            if (Size.X > 0 && Size.Y > 0) backdrop.SetShaderParameter("circle_center", (fan.Position + FanCenter) / Size);
        }
        Resized += CenterCircle;
        fan.Resized += CenterCircle;
        Callable.From(CenterCircle).CallDeferred();

        var column = new VBoxContainer { AnchorTop = 0.5f, AnchorBottom = 0.5f, OffsetLeft = 140, GrowVertical = GrowDirection.Both };
        column.AddThemeConstantOverride("separation", 14);
        var title = BoardStyle.MakeTitle("ARCANUM", 84);
        title.AddThemeConstantOverride("outline_size", 2);
        column.AddChild(title);
        column.AddChild(BoardStyle.MakeLabel("A tabletop card game engine", 20, BoardStyle.TextDim));
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 30) });

        // The selector is drawn first, so its glow lies under the text.
        var holder = new Control { CustomMinimumSize = new Vector2(420, 0) };
        BuildSelector();
        holder.AddChild(_selector);
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        holder.AddChild(list);
        _list = list;
        list.Resized += () => holder.CustomMinimumSize = new Vector2(420, list.Size.Y);
        column.AddChild(holder);

        void Item(string text, string hint, Action action)
        {
            int index = _items.Count;
            var button = new Button { Text = text, TooltipText = hint, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(420, 54) };
            button.AddThemeFontSizeOverride("font_size", 30);
            var empty = new StyleBoxEmpty { ContentMarginLeft = 0 };
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus" }) button.AddThemeStyleboxOverride(state, empty);
            button.AddThemeColorOverride("font_color", ItemColor);
            button.AddThemeColorOverride("font_hover_color", Colors.White);
            button.AddThemeColorOverride("font_pressed_color", UiArt.Gold);
            button.AddThemeColorOverride("font_hover_pressed_color", UiArt.Gold);
            button.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
            button.AddThemeConstantOverride("shadow_offset_y", 2);
            button.MouseEntered += () => Select(index);
            button.Pressed += action;
            _items.Add(button);
            list.AddChild(button);
        }
        Item("Play", "Start a game: constructed formats, commander, draft or sealed", () => App.Instance.GoTo(App.PlaySetupScene));
        Item("Online", "Host a game, browse open lobbies or join with an invite code", () => App.Instance.GoTo(App.OnlineScene));
        Item("Decks", "Build, import and export decks", () => App.Instance.GoTo(App.DeckBuilderScene));
        Item("Settings", "Gameplay, appearance, audio and video", () => App.Instance.GoTo(App.SettingsScene));
        Item("Extras", "Sandbox, card data and about", () => App.Instance.GoTo(App.ExtrasScene));
        if (!OS.HasFeature("mobile")) Item("Quit", "Close the game", () => GetTree().Quit());
        AddChild(column);
        CheckForUpdates();
        // Start on the first option, and keep the selector on its option whenever the list is laid out again.
        list.SortChildren += () => Select(Math.Max(0, _selected), animate: false);

        _status.AnchorTop = 1; _status.AnchorBottom = 1;
        _status.OffsetLeft = 24; _status.OffsetTop = -40;
        _status.Text = App.Instance.ContentStatus;
        AddChild(_status);
        App.Instance.ContentProgress += OnProgress;

        var version = BoardStyle.MakeLabel($"v{ProjectSettings.GetSetting("application/config/version", "0.5")} · AGPL-3.0", 13, BoardStyle.TextDim);
        version.AnchorLeft = 1; version.AnchorRight = 1; version.AnchorTop = 1; version.AnchorBottom = 1;
        version.GrowHorizontal = GrowDirection.Begin;
        version.OffsetRight = -24; version.OffsetTop = -40;
        AddChild(version);
    }

    public override void _ExitTree() => App.Instance.ContentProgress -= OnProgress;

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Once per run: offers a newer game release (exported desktop builds), or else a newer release of the installed content
    /// module. Skipped versions aren't offered again.
    /// </summary>
    private async void CheckForUpdates()
    {
        if (_checkedUpdates || !Settings.Current.CheckForUpdates) return;
        _checkedUpdates = true;
        if (Updater.ChecksGame && await Updater.NewerGameAsync() is { } game && game.Tag != Settings.Current.SkippedGameVersion)
        {
            if (IsInsideTree()) ShowUpdate(game, isGame: true);
            return;
        }
        await App.Instance.ContentReady;
        // Only a module installed by the game is updated (not a copy used while developing).
        if (App.Instance.Module is not { } module
            || !Path.GetFullPath(module.Directory).StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("user://modules")), StringComparison.OrdinalIgnoreCase))
            return;
        if (await Updater.NewerModuleAsync(module.Manifest.Version) is { } content && content.Tag != Settings.Current.SkippedModuleVersion && IsInsideTree())
            ShowUpdate(content, isGame: false);
    }

    /// <summary>The update dialog: what's new, then Update (or the download page when it can't update itself), Later, or Skip.</summary>
    private void ShowUpdate(ReleaseInfo release, bool isGame)
    {
        var overlay = new Control { ZIndex = 200 };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(shade);
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        panel.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Modal, BoardStyle.Panel, 12, BoardStyle.PanelBorder, 1, artPadding: 24));
        overlay.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);
        string what = isGame ? $"Arcanum {release.Tag}" : $"Content module {release.Tag}";
        var title = BoardStyle.MakeTitle($"{what} is available", 24);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(title);
        box.AddChild(BoardStyle.MakeLabel(isGame ? $"You have {Updater.GameVersion}." : $"You have {App.Instance.Module?.Manifest.Version}.", 15, BoardStyle.TextDim));
        var notes = new RichTextLabel { Text = PlainNotes(release.Notes), FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(560, 0) };
        notes.AddThemeFontSizeOverride("normal_font_size", 14);
        notes.AddThemeColorOverride("default_color", BoardStyle.Text);
        var notesScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(580, 260) };
        notesScroll.AddChild(notes);
        box.AddChild(notesScroll);
        var status = BoardStyle.MakeLabel("", 15, UiArt.Gold);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(status);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 8);
        box.AddChild(buttons);
        void Close() => overlay.QueueFree();
        Button Answer(string text, bool primary, Action action)
        {
            if (buttons.GetChildCount() > 0) buttons.AddChild(BoardStyle.MakeModalSeparator());
            var button = BoardStyle.MakeModalButton(text, primary, 18);
            button.Pressed += action;
            buttons.AddChild(button);
            return button;
        }
        Answer("Skip this version", false, () =>
        {
            if (isGame) Settings.Current.SkippedGameVersion = release.Tag;
            else Settings.Current.SkippedModuleVersion = release.Tag;
            Settings.Save();
            Close();
        });
        Answer("Later", false, Close);
        bool canInstall = release.ZipUrl is not null && (!isGame || Updater.CanUpdateGame && Updater.InstallFolderWritable());
        if (!canInstall)
        {
            status.Text = isGame ? "This copy of the game can't update itself here: download it from the release page." : "";
            Answer("Open download page", true, () => { OS.ShellOpen(release.PageUrl); Close(); });
        }
        else
        {
            Answer("Update", true, async () =>
            {
                foreach (var b in buttons.GetChildren().OfType<Button>()) b.Disabled = true;
                try
                {
                    var zip = await Updater.DownloadAsync(release, p => Callable.From(() => status.Text = $"Downloading… {p:P0}").CallDeferred());
                    if (isGame)
                    {
                        status.Text = "Installing… the game restarts by itself.";
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        Updater.InstallGameAndRestart(zip);
                        GetTree().Quit();
                        return;
                    }
                    var manifest = Updater.InstallModule(zip);
                    status.Text = $"{manifest.Name} {manifest.Version} is installed.";
                    foreach (var child in buttons.GetChildren()) child.QueueFree();
                    Answer("Restart now", true, () => { OS.SetRestartOnExit(true); GetTree().Quit(); });
                    Answer("Later", false, Close);
                }
                catch (Exception e)
                {
                    GD.PushWarning($"Update failed: {e}");
                    status.Text = $"The update failed: {e.Message}";
                    foreach (var child in buttons.GetChildren()) child.QueueFree();
                    Answer("Open download page", true, () => { OS.ShellOpen(release.PageUrl); Close(); });
                    Answer("Close", false, Close);
                }
            });
        }
        AddChild(overlay);
    }

    /// <summary>Release notes without Markdown marks (headings, bold, code), up to their install steps (the update does those).</summary>
    private static string PlainNotes(string markdown) =>
        string.Join("\n", markdown.Replace("\r", "").Split('\n').TakeWhile(line => !line.StartsWith("## Install", StringComparison.OrdinalIgnoreCase)).Select(line => line.TrimStart('#', ' ') is var t && line.StartsWith('#') ? t.ToUpperInvariant() : line))
            .Replace("**", "").Replace("`", "");

    private void OnProgress(string message) => _status.Text = message;

    /// <summary>Up/down move the selector, Enter or Space opens the option.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_items.Count == 0) return;
        if (@event.IsActionPressed("ui_down")) Select((_selected + 1) % _items.Count);
        else if (@event.IsActionPressed("ui_up")) Select((_selected - 1 + _items.Count) % _items.Count);
        else if (@event.IsActionPressed("ui_accept") && _selected >= 0) _items[_selected].EmitSignal(BaseButton.SignalName.Pressed);
        else return;
        GetViewport().SetInputAsHandled();
    }

    private void Select(int index, bool animate = true)
    {
        if (index == _selected && animate) return;
        if (_selected >= 0) _items[_selected].AddThemeColorOverride("font_color", ItemColor);
        _selected = index;
        var item = _items[index];
        item.AddThemeColorOverride("font_color", Colors.White);
        var target = new Vector2(0, _list.Position.Y + item.Position.Y + item.Size.Y / 2);
        _move?.Kill();
        if (!animate)
        {
            _selector.Position = target;
            return;
        }
        _move = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _move.TweenProperty(_selector, "position", target, 0.22);
    }

    /// <summary>The selector, centered on its origin's line: glow under the text, arrow before it, sparks around it.</summary>
    private void BuildSelector()
    {
        var glowTexture = UiArt.Texture("selector_glow");
        var glow = new TextureRect
        {
            Texture = glowTexture ?? FallbackGlow(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            SelfModulate = GlowColor,
            // The stars are drawn only inside the glow's shape.
            ClipChildren = ClipChildrenMode.AndDraw,
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(-30, -34),
            Size = new Vector2(380, 68),
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _selector.AddChild(glow);
        // A slow breath, so the selector feels alive.
        var breath = glow.CreateTween().SetLoops();
        breath.TweenProperty(glow, "self_modulate:a", 0.35f, 1.4).SetTrans(Tween.TransitionType.Sine);
        breath.TweenProperty(glow, "self_modulate:a", GlowColor.A, 1.4).SetTrans(Tween.TransitionType.Sine);

        foreach (var stars in MagicStars())
        {
            // Emitted at the arrow, in the glow's coordinates.
            stars.Position -= glow.Position;
            glow.AddChild(stars);
        }

        if (UiArt.Texture("selector") is { } arrowTexture)
        {
            var size = arrowTexture.GetSize();
            _selector.AddChild(new TextureRect { Texture = arrowTexture, MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(-size.X - 4, -size.Y / 2), Size = size });
        }
        else
        {
            var arrow = BoardStyle.MakeLabel("◆", 22, UiArt.Gold);
            arrow.Position = new Vector2(-34, -16);
            _selector.AddChild(arrow);
        }
    }

    /// <summary>
    /// Stars drifting out of the selector to the right, like magic leaving it: the four-pointed star of the card backs
    /// in several sizes, eight-pointed ones and small glints, in the nebula's violets with a little gold, faint enough
    /// to stay behind the text. In world space, so they trail behind when the selector moves.
    /// </summary>
    private static IEnumerable<CpuParticles2D> MagicStars()
    {
        var colors = new Gradient();
        colors.SetColor(0, new Color(0.62f, 0.42f, 1f));
        colors.SetColor(1, new Color(1f, 0.86f, 0.62f));
        colors.AddPoint(0.75f, new Color(0.86f, 0.74f, 1f));
        CpuParticles2D Stream(Texture2D texture, int amount, float minScale, float maxScale, float minSpeed, float maxSpeed, double lifetime) => new()
        {
            Amount = amount,
            Lifetime = lifetime,
            LifetimeRandomness = 0.4,
            LocalCoords = false,
            Texture = texture,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            // The glow's whole height, so the stars fill it.
            EmissionRectExtents = new Vector2(10, 28),
            Position = new Vector2(-8, 0),
            Direction = Vector2.Right,
            Spread = 4,
            Gravity = Vector2.Zero,
            InitialVelocityMin = minSpeed,
            InitialVelocityMax = maxSpeed,
            AngleMin = -15,
            AngleMax = 15,
            AngularVelocityMin = -30,
            AngularVelocityMax = 30,
            ScaleAmountMin = minScale,
            ScaleAmountMax = maxScale,
            ScaleAmountCurve = Twinkle(),
            ColorInitialRamp = colors,
            ColorRamp = Fade(),
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        yield return Stream(StarTexture(48, eightPoints: false), 14, 0.35f, 0.7f, 45, 85, 3.6);
        yield return Stream(StarTexture(48, eightPoints: true), 10, 0.25f, 0.5f, 55, 95, 3.2);
        yield return Stream(StarTexture(24, eightPoints: false), 30, 0.25f, 0.6f, 70, 120, 2.6);
    }

    /// <summary>
    /// A white star on a soft glow: four long points (the star of the card backs), or with four short points between
    /// them.
    /// </summary>
    private static Texture2D StarTexture(int size, bool eightPoints)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float half = size / 2f;
        // An astroid (|x|^½ + |y|^½ <= r^½): four concave sides meeting in sharp points on the axes.
        static float Points(float x, float y, float reach) =>
            Mathf.Clamp((1 - (Mathf.Sqrt(Mathf.Abs(x)) + Mathf.Sqrt(Mathf.Abs(y))) / Mathf.Sqrt(reach)) * 5, 0, 1);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float star = Points(dx, dy, 1f);
                if (eightPoints)
                {
                    const float c = 0.7071f;
                    star = Mathf.Max(star, Points((dx - dy) * c, (dx + dy) * c, 0.55f));
                }
                float glow = Mathf.Max(0, 1 - Mathf.Sqrt(dx * dx + dy * dy) * 2.4f) * 0.55f;
                image.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp(star + glow, 0, 1)));
            }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Stars appear, swell a little and shrink away.</summary>
    private static Curve Twinkle()
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0, 0.2f));
        curve.AddPoint(new Vector2(0.2f, 1));
        curve.AddPoint(new Vector2(1, 0.1f));
        return curve;
    }

    /// <summary>Fades in quickly, out slowly (the glow they are clipped to fades them too).</summary>
    private static Gradient Fade()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, new Color(1, 1, 1, 0));
        gradient.SetColor(1, new Color(1, 1, 1, 0));
        gradient.AddPoint(0.15f, new Color(1, 1, 1, 0.9f));
        return gradient;
    }

    /// <summary>A glow fading to the right when the kit's glow is missing.</summary>
    private static Texture2D FallbackGlow()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, Colors.White);
        gradient.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture2D { Gradient = gradient, Width = 256, Height = 32 };
    }

    /// <summary>The middle of the fan of cards, from its anchor point (the cards are 230×320, fanned out by ±2 steps).</summary>
    private static readonly Vector2 FanCenter = new(115, 30);

    /// <summary>Decorative fan of card backs on the right side; returns its anchor point's holder.</summary>
    private Control AddFannedCardBacks()
    {
        var holder = new Control { AnchorLeft = 0.62f, AnchorTop = 0.5f, AnchorRight = 0.62f, AnchorBottom = 0.5f, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(holder);
        for (int i = 0; i < 5; i++)
        {
            var card = new CardNode { Size = new Vector2(230, 320), MouseFilter = MouseFilterEnum.Ignore };
            holder.AddChild(card);
            card.Setup(new CardView { Id = new CardId(-100 - i), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Library, IsHidden = true }, false);
            float t = i - 2;
            card.Position = new Vector2(t * 90, -160 + Math.Abs(t) * 18);
            card.RotationDegrees = t * 8;
            card.Modulate = new Color(1, 1, 1, 0.9f);
        }
        return holder;
    }
}
