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

        // Options on the left, never wider than reads well; descriptions in the space to their right.
        var pageArea = new Control { AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 110, OffsetRight = 140 + 900, OffsetBottom = -30 };
        AddChild(pageArea);
        foreach (var content in new[] { GameplayPage(), AppearancePage(), AudioPage(), VideoPage() })
        {
            var page = Page(content);
            page.Visible = false;
            pageArea.AddChild(page);
            _pages.Add(page);
        }

        _description.AnchorLeft = 1; _description.AnchorRight = 1;
        _description.OffsetLeft = -800; _description.OffsetRight = -140; _description.OffsetTop = 150;
        _description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_description);
        SettingsKit.Row.Described += Describe;

        _tabs.Changed += index =>
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
            _description.Text = "";
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

    public override void _ExitTree() => SettingsKit.Row.Described -= Describe;

    private void Describe(string text) => _description.Text = text;

    /// <summary>Q and E (or Page Up/Down) switch tabs.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode is Key.Q or Key.Pageup) _tabs.Select(_tabs.Current - 1);
        else if (key.Keycode is Key.E or Key.Pagedown) _tabs.Select(_tabs.Current + 1);
        else return;
        GetViewport().SetInputAsHandled();
    }

    private static Control Page(Control content)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
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

        box.AddChild(SettingsKit.Section("Online"));
        var limits = new[] { (0, "No limit"), (30, "30 seconds"), (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes") };
        box.AddChild(Row("Time per decision", SettingsKit.Choice(limits.Select(l => l.Item2).ToList(), Math.Max(0, Array.FindIndex(limits, l => l.Item1 == S.DecisionSeconds)),
            i => { S.DecisionSeconds = limits[i].Item1; Settings.Save(); }),
            "For games you host: how long each player has for a decision before the computer makes it for them (they keep playing afterwards). " +
            "Passing priority with your stops never counts. Players who disconnect have their own wait."));
        return box;
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

    private static Control VideoPage()
    {
        var box = Column();
        box.AddChild(SettingsKit.Section("Display"));
        box.AddChild(Switch("Fullscreen", S.Fullscreen, v => S.Fullscreen = v, "Fill the whole screen, or play in a window."));
        box.AddChild(Switch("V-Sync", S.VSync, v => S.VSync = v, "Match the screen's refresh rate: no tearing, a little more input delay."));
        // Applied when the slider is released, so it doesn't jump around under the pointer while it rescales.
        box.AddChild(Row("Interface scale", SettingsKit.Slider(0.75, 1.5, 0.05, S.UiScale, v => $"{v:0.##}×", v => { S.UiScale = v; Settings.Save(); }, commitOnRelease: true),
            "The size of everything on screen: text, buttons and cards."));
        return box;
    }
}
