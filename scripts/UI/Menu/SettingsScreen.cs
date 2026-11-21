// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Settings: gameplay, appearance, audio and video on tabs. Options sit in a column on the left; the one under the
/// pointer lights up and its description shows on the right. Changes are saved immediately.
/// </summary>
public partial class SettingsScreen : Control
{
    private static readonly (Step Step, string Name)[] StopSteps =
    {
        (Step.Upkeep, "Upkeep"), (Step.Draw, "Draw"), (Step.PrecombatMain, "Main 1"), (Step.BeginCombat, "Beginning of combat"),
        (Step.DeclareAttackers, "Declare attackers"), (Step.DeclareBlockers, "Declare blockers"), (Step.CombatDamage, "Combat damage"),
        (Step.EndCombat, "End of combat"), (Step.PostcombatMain, "Main 2"), (Step.End, "End step"),
    };

    private const string StopsHelp = "Where the game stops to let you act when you could play something. A stop gives you priority after that " +
        "step's action (at Declare blockers, once blocks are known). Combat damage happens as soon as its step begins, so tricks before damage go " +
        "at Declare blockers; when your creatures are in combat the game stops there automatically if you can play something. You can also " +
        "change stops during a game on the phase bar.";

    private readonly FileDialog _fileDialog = new();
    private int _customPlaymatSeat;
    private readonly PlaymatSwatch[] _playmatPreviews = new PlaymatSwatch[2];
    private CardNode _backPreview = null!;
    private readonly Label _description = BoardStyle.MakeLabel("", 22, UiArt.Gold);
    private readonly List<Control> _pages = new();
    private SettingsKit.Tabs _tabs = null!;

    private static SettingsData S => Settings.Current;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Settings");

        var names = new[] { "Gameplay", "Appearance", "Audio", "Video" };
        _tabs = new SettingsKit.Tabs(names) { AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetTop = 22, GrowHorizontal = GrowDirection.Both };
        AddChild(_tabs);

        // Options on the left, never wider than reads well; descriptions in a column of their own to the right, or on a
        // screen too narrow for both, in a strip of their own under the options. Neither ever covers the other.
        _body.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_body);
        _body.AddChild(_split);
        var pageArea = _pageArea;
        _split.AddChild(pageArea);
        foreach (var content in new[] { GameplayPage(), AppearancePage(), AudioPage(), VideoPage() })
        {
            var page = Page(content);
            page.Visible = false;
            pageArea.AddChild(page);
            _pages.Add(page);
        }

        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _description.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _split.AddChild(_description);
        PlaceDescription();
        Resized += PlaceDescription;
        SettingsKit.Row.Described += Describe;

        _tabs.Changed += index =>
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
            _description.Text = "";
            // With a controller, the page's first option takes the focus.
            PadFocus.Prefer(FirstButton(_pages[index]));
        };
        _tabs.Select(0);

        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Access = FileDialog.AccessEnum.Filesystem;
        _fileDialog.Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; Images" };
        _fileDialog.Size = new Vector2I(900, 600);
        _fileDialog.FileSelected += path =>
        {
            S.Playmats[_customPlaymatSeat] = "custom:" + path;
            Settings.Save();
            _playmatPreviews[_customPlaymatSeat].SetStyle(S.Playmats[_customPlaymatSeat]);
        };
        AddChild(_fileDialog);
    }

    // They cover the whole screen, the header and tabs included: clicks go through to what's under them.
    private readonly MarginContainer _body = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly BoxContainer _split = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Control _pageArea = new() { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };

    /// <summary>The widest the options get, and the narrowest a description column beside them may be.</summary>
    private const float OptionsWidth = 900, DescriptionWidth = 360;

    /// <summary>Lays out the options and their descriptions for the screen's width.</summary>
    private void PlaceDescription()
    {
        float side = Size.X >= OptionsWidth + DescriptionWidth + 2 * 140 + 60 ? 140 : Math.Clamp((Size.X - OptionsWidth) / 2, 16, 140);
        bool beside = Size.X >= OptionsWidth + DescriptionWidth + 2 * side + 60;
        _body.AddThemeConstantOverride("margin_left", (int)side);
        _body.AddThemeConstantOverride("margin_right", (int)side);
        _body.AddThemeConstantOverride("margin_top", 110);
        _body.AddThemeConstantOverride("margin_bottom", beside ? 30 : 12);
        _split.Vertical = !beside;
        _split.AddThemeConstantOverride("separation", beside ? 60 : 8);
        // Beside: the options keep their width and the description takes the rest, from the top. Under: the options take
        // the height left by the description strip, which keeps room for three lines so the list doesn't jump.
        _pageArea.CustomMinimumSize = new Vector2(beside ? OptionsWidth : 0, 0);
        _pageArea.SizeFlagsHorizontal = beside ? SizeFlags.Fill : SizeFlags.ExpandFill;
        _description.SizeFlagsVertical = beside ? SizeFlags.ShrinkBegin : SizeFlags.ShrinkEnd;
        int font = beside ? 22 : 17;
        _description.AddThemeFontSizeOverride("font_size", font);
        _description.AddThemeConstantOverride("line_spacing", beside ? 6 : 0);
        _description.CustomMinimumSize = new Vector2(0, beside ? 0 : 3 * (font + 6));
        _description.VerticalAlignment = VerticalAlignment.Top;
    }

    public override void _ExitTree() => SettingsKit.Row.Described -= Describe;

    private void Describe(string text) => _description.Text = text;

    private static BaseButton? FirstButton(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is CanvasItem { Visible: false }) continue;
            if (child is BaseButton { Disabled: false } button) return button;
            if (FirstButton(child) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>Q and E (or Page Up/Down, or a controller's shoulder buttons) switch tabs.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        int step = @event switch
        {
            InputEventKey { Pressed: true, Echo: false, Keycode: Key.Q or Key.Pageup } => -1,
            InputEventKey { Pressed: true, Echo: false, Keycode: Key.E or Key.Pagedown } => 1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.RightShoulder } => 1,
            _ => 0,
        };
        if (step == 0) return;
        _tabs.Select(_tabs.Current + step);
        GetViewport().SetInputAsHandled();
    }

    private static Control Page(Control content)
    {
        // The page scrolls to the option the controller's focus moves to.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        margin.AddChild(content);
        scroll.AddChild(margin);
        return scroll;
    }

    private static VBoxContainer Column()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);
        return box;
    }

    private static SettingsKit.Row Row(string name, Control control, string description) => new(name, control, description);

    private static Control Switch(string name, bool on, Action<bool> set, string description) =>
        Row(name, SettingsKit.OnOff(on, v => { set(v); Settings.Save(); }), description);

    private Control GameplayPage()
    {
        var box = Column();
        box.AddChild(SettingsKit.Section("Play"));
        var playerName = _playerName = MenuKit.TextField(Settings.PlayerName, "Your name");
        playerName.MaxLength = 32;
        playerName.TextChanged += text =>
        {
            S.PlayerNames[0] = text.Trim() is { Length: > 0 } chosen ? chosen : "Player 1";
            S.NameChosen = true;
            Settings.Save();
        };
        box.AddChild(Row("Your name", playerName, "What other players see in online games, and the name of the first seat on this device."));
        box.AddChild(Switch("Confirm mana payment", S.ConfirmManaPayment, v => S.ConfirmManaPayment = v,
            "Before a spell is cast, choose which lands to tap instead of letting the game pay for you."));
        box.AddChild(Switch("Show both hands in hotseat", S.RevealHandsInHotseat, v => S.RevealHandsInHotseat = v,
            "In hotseat games (several players on this device), every hand stays face up."));
        box.AddChild(Row("Animation speed", SettingsKit.Slider(0.5, 2.5, 0.25, S.AnimationSpeed, v => $"{v:0.##}×", v => { S.AnimationSpeed = v; Settings.Save(); }),
            "How fast cards move, turn and fly across the table. Above 1× is faster."));
        box.AddChild(Switch("Full control", S.FullControl, v => S.FullControl = v,
            "Stop at every priority, even with nothing to do."));
        var openings = new[] { ("tear", "Tear them open"), ("auto", "Open automatically"), ("skip", "No animation") };
        box.AddChild(Row("Opening boosters", SettingsKit.Choice(openings.Select(o => o.Item2).ToList(),
                Math.Max(0, Array.FindIndex(openings, o => o.Item1 == S.BoosterOpening)), selected => { S.BoosterOpening = openings[selected].Item1; Settings.Save(); }),
            "How your booster opens at the start of each draft round: swipe across its tear line yourself (it opens by itself if you wait), let it tear open, or skip straight to the cards."));
        var views = new[] { ("auto", "Automatic"), ("compact", "One table at a time"), ("full", "Every table") };
        box.AddChild(Row("Board view", SettingsKit.Choice(views.Select(v => v.Item2).ToList(),
                Math.Max(0, Array.FindIndex(views, v => v.Item1 == S.BoardView)), selected => { S.BoardView = views[selected].Item1; Settings.Save(); }),
            "Every table: all players' tables at once. One table at a time: the table of the player whose turn it is fills the screen, with your own cards along the bottom, and the list of players beside it opens anyone's table. Automatic picks one table at a time on small screens (handheld consoles, phones)."));

        box.AddChild(SettingsKit.Section("Default stops"));
        var captions = new HBoxContainer();
        foreach (var (caption, color) in new[] { ("Your turn", BoardStyle.Playable), ("Opponents' turns", BoardStyle.Blocking) })
        {
            var label = BoardStyle.MakeLabel(caption, 15, color);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            captions.AddChild(label);
        }
        box.AddChild(Row("Step", captions, StopsHelp));
        foreach (var (step, name) in StopSteps)
        {
            var pair = new HBoxContainer();
            pair.AddThemeConstantOverride("separation", 0);
            foreach (bool own in new[] { true, false })
            {
                var holder = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                holder.AddChild(StopToggle(step, own));
                pair.AddChild(holder);
            }
            box.AddChild(Row(name, pair, StopsHelp));
        }

        box.AddChild(SettingsKit.Section("Updates"));
        box.AddChild(Switch("Check for updates at start", S.CheckForUpdates, v => S.CheckForUpdates = v,
            "When the game starts, look for a newer version of the game or of the card content and offer to install it."));

        box.AddChild(SettingsKit.Section("Card pictures"));
        box.AddChild(Switch("Download every card picture", S.DownloadEveryPicture, v =>
            {
                S.DownloadEveryPicture = v;
                CardImageCache.DownloadEvery(v ? App.Instance.Cards : null);
            },
            "Card pictures download when a card is first seen and stay on this device. On, every card's picture also downloads in " +
            "the background (after the ones you are looking at), so cards show at once everywhere. It takes about 5 GB " +
            "and goes on where it stopped each time the game starts."));
        var everyState = BoardStyle.MakeLabel("", 16);
        everyState.CustomMinimumSize = new Vector2(SettingsKit.ControlWidth, 0);
        box.AddChild(Row("Downloaded", everyState, "How many card pictures are on this device, while every picture is downloading."));
        var everyTimer = new Godot.Timer { WaitTime = 0.5, Autostart = true };
        void ShowEvery() => everyState.Text = !S.DownloadEveryPicture ? "Only the pictures seen"
            : CardImageCache.EveryProgress is not { } p ? "Looking at the pictures on this device…"
            : $"{p.Have:N0} of {p.Total:N0}" + (p.Failed > 0 ? $", {p.Failed:N0} not available" : "")
              + (p.Stopped is { } why ? $". Stopped: {why}" : p.Have + p.Failed >= p.Total ? ". All done." : "");
        everyTimer.Timeout += ShowEvery;
        everyState.AddChild(everyTimer);
        ShowEvery();

        box.AddChild(SettingsKit.Section("Online"));
        box.AddChild(Row("Discord account", DiscordAccount(),
            "Sign in with Discord to play on the internet: your online identity (and later your career) follows your Discord account to any computer. " +
            "The game only learns your Discord user id and name."));
        box.AddChild(Switch("Show activity on Discord", S.DiscordActivity, v => S.DiscordActivity = v,
            "While the Discord app is open on this computer, your Discord profile shows what you are doing: the screen you are on, " +
            "or the kind of game, its turn and how many players are at the table. Works without signing in."));
        var limits = new[] { (0, "No limit"), (30, "30 seconds"), (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes") };
        box.AddChild(Row("Time per decision", SettingsKit.Choice(limits.Select(l => l.Item2).ToList(), Math.Max(0, Array.FindIndex(limits, l => l.Item1 == S.DecisionSeconds)),
            i => { S.DecisionSeconds = limits[i].Item1; Settings.Save(); }),
            "For games you host: how long each player has for a decision before the computer makes it for them (they keep playing afterwards). " +
            "Passing priority with your stops never counts. Players who disconnect have their own wait."));
        return box;
    }

    private CancellationTokenSource? _discordSignIn;
    private LineEdit _playerName = null!;

    /// <summary>Signed in or not, and the button to sign in (it waits for the browser; pressed again, it stops waiting) or out.</summary>
    private Control DiscordAccount()
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(SettingsKit.ControlWidth, 0) };
        row.AddThemeConstantOverride("separation", 12);
        var state = BoardStyle.MakeLabel("", 16);
        state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        state.VerticalAlignment = VerticalAlignment.Center;
        var button = BoardStyle.MakeButton("", 15, compact: true);
        void Show(string? note = null)
        {
            bool waiting = _discordSignIn is not null;
            state.Text = note ?? (waiting ? (Account.HasBrowser ? "Waiting for the browser…" : "Waiting for the code…") : Account.HasDiscord ? "Signed in" : "Not signed in");
            button.Text = waiting ? "Cancel" : Account.HasDiscord ? "Sign out" : "Sign in";
        }
        button.Pressed += async () =>
        {
            if (_discordSignIn is { } waiting) { waiting.Cancel(); return; }
            if (Account.HasDiscord) { Account.SignOut(); Show(); return; }
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(Account.HasBrowser ? 5 : 10));
            _discordSignIn = cancel;
            Show();
            string? note = null;
            Control? codeDialog = null;
            try
            {
                var name = await Account.SignInWithDiscordAsync(cancel.Token, code => codeDialog = AccountPrompt.ShowCode(this, code, cancel));
                AccountPrompt.OfferName(this, name, () => { if (IsInstanceValid(_playerName)) _playerName.Text = Settings.PlayerName; });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { note = e is System.Net.Http.HttpRequestException ? "The sign-in service can't be reached." : e.Message; }
            if (IsInstanceValid(codeDialog)) codeDialog!.QueueFree();
            _discordSignIn = null;
            if (IsInstanceValid(button)) Show(note);
        };
        Show();
        row.AddChild(state);
        row.AddChild(button);
        return row;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete) _discordSignIn?.Cancel();
    }

    private static Button StopToggle(Step step, bool own)
    {
        var list = own ? S.OwnTurnStops : S.OpponentTurnStops;
        var box = MenuKit.Check(list.Contains(step.ToString()), own ? BoardStyle.Playable : BoardStyle.Blocking);
        box.Toggled += on =>
        {
            var set = (own ? S.OwnTurnStops : S.OpponentTurnStops).ToHashSet();
            if (on) set.Add(step.ToString()); else set.Remove(step.ToString());
            if (own) S.OwnTurnStops = set.ToArray(); else S.OpponentTurnStops = set.ToArray();
            Settings.Save();
        };
        return box;
    }

    private Control AppearancePage()
    {
        var box = Column();
        box.AddChild(SettingsKit.Section("Playmats"));
        for (int i = 0; i < 2; i++)
        {
            int seat = i;
            var current = S.Playmats[seat];
            int index = Array.FindIndex(BoardStyle.Playmats, p => p.Id == current);
            var options = BoardStyle.Playmats.Select(p => p.Label).Append("Custom image…").ToList();
            var preview = _playmatPreviews[seat] = new PlaymatSwatch { Seed = seat, CustomMinimumSize = new Vector2(0, 90) };
            var picker = SettingsKit.Choice(options, index >= 0 ? index : current.StartsWith("custom:") ? options.Count - 1 : 0, selected =>
            {
                if (selected == BoardStyle.Playmats.Length)
                {
                    _customPlaymatSeat = seat;
                    _fileDialog.PopupCentered();
                    return;
                }
                S.Playmats[seat] = BoardStyle.Playmats[selected].Id;
                Settings.Save();
                preview.SetStyle(S.Playmats[seat]);
            });
            box.AddChild(Row(seat == 0 ? "Your playmat" : "Opponents' playmat", picker, seat == 0
                ? "The table under your cards. Online, your opponents see it too, unless it is a custom image: those are used only on this device."
                : "The table under the computer's cards, and under an online opponent's when they use a custom image (which stays on their device)."));
            var previewMargin = new MarginContainer();
            previewMargin.AddThemeConstantOverride("margin_left", 34);
            previewMargin.AddThemeConstantOverride("margin_bottom", 8);
            previewMargin.AddChild(preview);
            box.AddChild(previewMargin);
            preview.SetStyle(S.Playmats[seat]);
        }

        box.AddChild(SettingsKit.Section("Card back"));
        var backs = BoardStyle.CardBacks;
        int backIndex = Math.Max(0, backs.ToList().FindIndex(b => b.Id == S.CardBack));
        var holder = new Control { CustomMinimumSize = new Vector2(150, 206) };
        box.AddChild(Row("Design", SettingsKit.Choice(backs.Select(b => b.Label).ToList(), backIndex, selected =>
        {
            S.CardBack = backs[selected].Id;
            Settings.Save();
            RebuildBackPreview(holder);
        }), "The back of every hidden card: libraries, opponents' hands and face-down cards. Designs from the card module are downloaded the first time they are shown."));
        box.AddChild(holder);
        RebuildBackPreview(holder);

        box.AddChild(SettingsKit.Section("Foil"));
        box.AddChild(Row("Foil shine", SettingsKit.Slider(0, 1, 0.05, S.FoilShine, v => $"{Math.Round(v * 100)}%", v => { S.FoilShine = v; Settings.Save(); }),
            "How strongly foil cards shine under the pointer. Lower it if the shine makes rules text hard to read; 0 turns it off."));
        return box;
    }

    private void RebuildBackPreview(Control holder)
    {
        _backPreview?.QueueFree();
        _backPreview = new CardNode { Size = new Vector2(140, 196), Position = new Vector2(34, 6), MouseFilter = MouseFilterEnum.Ignore };
        holder.AddChild(_backPreview);
        _backPreview.Setup(new CardView { Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Library, IsHidden = true }, false);
    }

    private static Control AudioPage()
    {
        var box = Column();
        box.AddChild(SettingsKit.Section("Volume"));
        void Volume(string label, double value, Action<double> set, string description)
        {
            box.AddChild(Row(label, SettingsKit.Slider(0, 1, 0.05, value, v => $"{Math.Round(v * 100)}", v => { set(v); Settings.Save(); }),
                description + " Sound effects and music arrive in a later version; these levels are kept for then."));
        }
        Volume("Master volume", S.MasterVolume, v => S.MasterVolume = v, "The level of everything the game plays.");
        Volume("Music", S.MusicVolume, v => S.MusicVolume = v, "The level of the music.");
        Volume("Effects", S.EffectsVolume, v => S.EffectsVolume = v, "The level of sound effects such as the chime that starts your turn.");
        return box;
    }

    private static Control WindowSizeChoice()
    {
        var usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen()).Size;
        var sizes = Settings.WindowSizes.Where(t => Settings.ParseSize(t) is { } v && v.X <= usable.X && v.Y <= usable.Y).ToList();
        if (sizes.Count == 0) sizes.Add(Settings.WindowSizes[0]);
        if (S.WindowSize.Length > 0 && !sizes.Contains(S.WindowSize)) sizes.Add(S.WindowSize);
        var current = S.WindowSize.Length > 0 ? S.WindowSize : $"{DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}";
        int index = sizes.IndexOf(current);
        if (index < 0) { sizes.Insert(0, current); index = 0; }
        return SettingsKit.Choice(sizes.Select(t => t.Replace("x", " × ")).ToList(), index, i => { S.WindowSize = sizes[i]; Settings.Save(); });
    }

    private static Control VideoPage()
    {
        var box = Column();
        box.AddChild(SettingsKit.Section("Display"));
        box.AddChild(Switch("Fullscreen", S.Fullscreen, v => S.Fullscreen = v, "Fill the whole screen, or play in a window."));
        box.AddChild(Row("Window size", WindowSizeChoice(), "The window's size when not fullscreen (only sizes that fit this screen). Fullscreen always uses the screen's own resolution; the game scales to any size."));
        box.AddChild(Switch("V-Sync", S.VSync, v => S.VSync = v, "Match the screen's refresh rate: no tearing, a little more input delay."));
        // Applied when the slider is released, so it doesn't jump around under the pointer while it rescales.
        box.AddChild(Row("Interface scale", SettingsKit.Slider(0.75, 1.5, 0.05, S.UiScale, v => $"{v:0.##}×", v => { S.UiScale = v; Settings.Save(); }, commitOnRelease: true),
            "The size of everything on screen: text, buttons and cards."));
        return box;
    }
}
