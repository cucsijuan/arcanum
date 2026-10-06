// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Settings: gameplay, appearance, audio and video. Changes are saved immediately.</summary>
public partial class SettingsScreen : Control
{
    private static readonly (Step Step, string Name)[] StopSteps =
    {
        (Step.Upkeep, "Upkeep"), (Step.Draw, "Draw"), (Step.PrecombatMain, "Main 1"), (Step.BeginCombat, "Beginning of combat"),
        (Step.DeclareAttackers, "Declare attackers"), (Step.DeclareBlockers, "Declare blockers"), (Step.CombatDamage, "Combat damage"),
        (Step.EndCombat, "End of combat"), (Step.PostcombatMain, "Main 2"), (Step.End, "End step"),
    };

    private readonly FileDialog _fileDialog = new();
    private int _customPlaymatSeat;
    private readonly PlaymatSwatch[] _playmatPreviews = new PlaymatSwatch[2];
    private CardNode _backPreview = null!;

    private static SettingsData S => Settings.Current;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Settings");

        var tabs = new TabContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 100, OffsetRight = -140, OffsetBottom = -40 };
        tabs.AddThemeFontSizeOverride("font_size", 16);
        tabs.AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Menu, new Color(0.09f, 0.09f, 0.11f, 0.92f), 12, BoardStyle.PanelBorder, 1, artPadding: 12));
        AddChild(tabs);

        tabs.AddChild(Page("Gameplay", GameplayPage()));
        tabs.AddChild(Page("Appearance", AppearancePage()));
        tabs.AddChild(Page("Audio", AudioPage()));
        tabs.AddChild(Page("Video", VideoPage()));

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

    private static Control Page(string name, Control content)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 24);
        margin.AddChild(content);
        scroll.AddChild(margin);
        return scroll;
    }

    private static VBoxContainer Column()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 16);
        return box;
    }

    private Control GameplayPage()
    {
        var box = Column();
        var confirm = MenuKit.Toggle("Confirm mana payment before casting (choose which lands to tap)", S.ConfirmManaPayment);
        confirm.Toggled += on => { S.ConfirmManaPayment = on; Settings.Save(); };
        box.AddChild(confirm);
        var reveal = MenuKit.Toggle("Show both hands in hotseat games", S.RevealHandsInHotseat);
        reveal.Toggled += on => { S.RevealHandsInHotseat = on; Settings.Save(); };
        box.AddChild(reveal);

        var speedLabel = BoardStyle.MakeLabel(SpeedText(S.AnimationSpeed), 15, BoardStyle.TextDim);
        var speed = MenuKit.Slider(0.5, 2.5, 0.25, S.AnimationSpeed);
        speed.ValueChanged += v => { S.AnimationSpeed = v; speedLabel.Text = SpeedText(v); Settings.Save(); };
        var speedRow = MenuKit.Row("Animation speed", speed);
        speedRow.AddChild(speedLabel);
        box.AddChild(speedRow);

        box.AddChild(MenuKit.SectionTitle("Default stops"));
        box.AddChild(MenuKit.Hint("Where the game stops to let you act when you could play something. A stop gives you priority after that step's action " +
            "(at Declare blockers, once blocks are known). Combat damage happens as soon as its step begins, so tricks before damage go at Declare blockers; " +
            "when your creatures are in combat the game stops there automatically if you can play something. You can also change stops during a game on the phase bar."));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 32);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.AddChild(BoardStyle.MakeLabel("Step", 14, BoardStyle.TextDim));
        grid.AddChild(BoardStyle.MakeLabel("Your turn", 14, BoardStyle.Playable));
        grid.AddChild(BoardStyle.MakeLabel("Opponents' turns", 14, BoardStyle.Blocking));
        foreach (var (step, name) in StopSteps)
        {
            grid.AddChild(BoardStyle.MakeLabel(name, 15));
            grid.AddChild(StopToggle(step, own: true));
            grid.AddChild(StopToggle(step, own: false));
        }
        box.AddChild(grid);
        var full = MenuKit.Toggle("Full control (stop at every priority, even with nothing to do)", S.FullControl);
        full.Toggled += on => { S.FullControl = on; Settings.Save(); };
        box.AddChild(full);

        box.AddChild(MenuKit.SectionTitle("Online"));
        box.AddChild(MenuKit.Hint("For games you host: how long each player has for a decision before the computer makes it for them " +
            "(they keep playing afterwards). Passing priority with your stops never counts. Players who disconnect have their own wait."));
        var limits = new[] { (0, "No limit"), (30, "30 seconds"), (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes") };
        var limit = MenuKit.Options(limits.Select(l => l.Item2), Math.Max(0, Array.FindIndex(limits, l => l.Item1 == S.DecisionSeconds)));
        limit.ItemSelected += i => { S.DecisionSeconds = limits[i].Item1; Settings.Save(); };
        box.AddChild(MenuKit.Row("Time per decision", limit));
        return box;
    }

    private static string SpeedText(double v) => v switch { < 0.9 => "slower", > 1.1 => "faster", _ => "normal" } + $" ({v:0.##}×)";

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
        box.AddChild(MenuKit.SectionTitle("Playmats"));
        for (int i = 0; i < 2; i++)
        {
            int seat = i;
            var current = S.Playmats[seat];
            int index = Array.FindIndex(BoardStyle.Playmats, p => p.Id == current);
            var options = BoardStyle.Playmats.Select(p => p.Label).Append("Custom image…").ToList();
            var picker = MenuKit.Options(options, index >= 0 ? index : current.StartsWith("custom:") ? options.Count - 1 : 0);
            var preview = _playmatPreviews[seat] = new PlaymatSwatch { Seed = seat, CustomMinimumSize = new Vector2(480, 110) };
            picker.ItemSelected += selected =>
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
            };
            var row = MenuKit.Row($"Player {seat + 1}", picker, 120);
            box.AddChild(row);
            box.AddChild(preview);
            preview.SetStyle(S.Playmats[seat]);
        }
        box.AddChild(MenuKit.Hint("Custom images are used only on this device and stretched to fill each player's half of the table."));

        box.AddChild(MenuKit.SectionTitle("Card back"));
        int backIndex = Math.Max(0, Array.FindIndex(BoardStyle.CardBacks, b => b.Id == S.CardBack));
        var backPicker = MenuKit.Options(BoardStyle.CardBacks.Select(b => b.Label), backIndex);
        var backRow = MenuKit.Row("Design", backPicker, 120);
        box.AddChild(backRow);
        var holder = new Control { CustomMinimumSize = new Vector2(150, 200) };
        box.AddChild(holder);
        RebuildBackPreview(holder);
        backPicker.ItemSelected += selected =>
        {
            S.CardBack = BoardStyle.CardBacks[selected].Id;
            Settings.Save();
            RebuildBackPreview(holder);
        };
        return box;
    }

    private void RebuildBackPreview(Control holder)
    {
        _backPreview?.QueueFree();
        _backPreview = new CardNode { Size = new Vector2(140, 196), MouseFilter = MouseFilterEnum.Ignore };
        holder.AddChild(_backPreview);
        _backPreview.Setup(new CardView { Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Library, IsHidden = true }, false);
    }

    private static Control AudioPage()
    {
        var box = Column();
        box.AddChild(MenuKit.Hint("Sound effects and music arrive in a later version; these levels are kept for then."));
        void Volume(string label, double value, Action<double> set)
        {
            var slider = MenuKit.Slider(0, 1, 0.05, value);
            slider.ValueChanged += v => { set(v); Settings.Save(); };
            box.AddChild(MenuKit.Row(label, slider));
        }
        Volume("Master volume", S.MasterVolume, v => S.MasterVolume = v);
        Volume("Music", S.MusicVolume, v => S.MusicVolume = v);
        Volume("Effects", S.EffectsVolume, v => S.EffectsVolume = v);
        return box;
    }

    private static Control VideoPage()
    {
        var box = Column();
        var fullscreen = MenuKit.Toggle("Fullscreen", S.Fullscreen);
        fullscreen.Toggled += on => { S.Fullscreen = on; Settings.Save(); };
        box.AddChild(fullscreen);
        var vsync = MenuKit.Toggle("V-Sync", S.VSync);
        vsync.Toggled += on => { S.VSync = on; Settings.Save(); };
        box.AddChild(vsync);
        var scaleLabel = BoardStyle.MakeLabel($"{S.UiScale:0.##}×", 15, BoardStyle.TextDim);
        var scale = MenuKit.Slider(0.75, 1.5, 0.05, S.UiScale);
        // Apply on release so the slider doesn't jump around under the pointer while it rescales.
        scale.ValueChanged += v => scaleLabel.Text = $"{v:0.##}×";
        scale.DragEnded += changed => { if (changed) { S.UiScale = scale.Value; Settings.Save(); } };
        var row = MenuKit.Row("Interface scale", scale);
        row.AddChild(scaleLabel);
        box.AddChild(row);
        return box;
    }
}
