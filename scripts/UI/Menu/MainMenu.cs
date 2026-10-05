// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Title screen: play, decks, settings, extras.</summary>
public partial class MainMenu : Control
{
    private readonly Label _status = BoardStyle.MakeLabel("", 14, BoardStyle.TextDim);

    private static bool _opened;

    public override void _Ready()
    {
        // ARCANUM_OPEN=online opens the online screen at start (testing exported builds, which can't be given a scene).
        if (OS.GetEnvironment("ARCANUM_OPEN") == "online" && !_opened)
        {
            _opened = true;
            Callable.From(() => App.Instance.GoTo(App.OnlineScene)).CallDeferred();
        }
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        AddFannedCardBacks();

        var column = new VBoxContainer { AnchorTop = 0.5f, AnchorBottom = 0.5f, OffsetLeft = 140, GrowVertical = GrowDirection.Both };
        column.AddThemeConstantOverride("separation", 14);
        var title = BoardStyle.MakeLabel("ARCANUM", 84, bold: true);
        title.AddThemeConstantOverride("outline_size", 2);
        column.AddChild(title);
        column.AddChild(BoardStyle.MakeLabel("A tabletop card game engine", 20, BoardStyle.TextDim));
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 36) });

        void Item(string text, string hint, Action action, bool primary = false)
        {
            var button = primary ? BoardStyle.MakePrimaryButton(text, 22) : BoardStyle.MakeButton(text, 20);
            button.CustomMinimumSize = new Vector2(340, 58);
            button.Alignment = HorizontalAlignment.Left;
            button.TooltipText = hint;
            button.Pressed += action;
            column.AddChild(button);
        }
        Item("Play", "Start a game", () => App.Instance.GoTo(App.PlaySetupScene), primary: true);
        Item("Online", "Host a game, browse open lobbies or join with an invite code", () => App.Instance.GoTo(App.OnlineScene));
        Item("Limited", "Draft and sealed: open boosters, build a deck, play rounds", () => App.Instance.GoTo(App.LimitedScene));
        Item("Decks", "Build, import and export decks", () => App.Instance.GoTo(App.DeckBuilderScene));
        Item("Settings", "Gameplay, appearance, audio and video", () => App.Instance.GoTo(App.SettingsScene));
        Item("Extras", "Sandbox, card data and about", () => App.Instance.GoTo(App.ExtrasScene));
        if (!OS.HasFeature("mobile")) Item("Quit", "Close the game", () => GetTree().Quit());
        AddChild(column);

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

    private void OnProgress(string message) => _status.Text = message;

    /// <summary>Decorative fan of card backs on the right side.</summary>
    private void AddFannedCardBacks()
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
    }
}
