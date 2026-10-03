// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Choose game mode, players and decks, then start the match.</summary>
public partial class PlaySetup : Control
{
    private readonly List<DeckInfo> _decks = new();
    private readonly LineEdit[] _names = new LineEdit[2];
    private readonly OptionButton[] _deckPickers = new OptionButton[2];
    private readonly Label[] _summaries = new Label[2];
    private readonly CheckButton _sandbox = MenuKit.Toggle("Start from the sandbox board (test mode)", false);
    private Button _start = null!;
    private Button _hotseatMode = null!, _botMode = null!;
    private bool _vsBot = true;
    private readonly Label[] _seatTitles = new Label[2];

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Play");

        var root = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 110, OffsetRight = -140, OffsetBottom = -40 };
        root.AddThemeConstantOverride("separation", 22);
        AddChild(root);

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 12);
        _botMode = BoardStyle.MakePrimaryButton("Vs computer", 18);
        _botMode.CustomMinimumSize = new Vector2(200, 48);
        _botMode.TooltipText = "Play against the computer";
        _botMode.Pressed += () => SetMode(vsBot: true);
        _hotseatMode = BoardStyle.MakeButton("Hotseat", 18);
        _hotseatMode.CustomMinimumSize = new Vector2(200, 48);
        _hotseatMode.TooltipText = "Two players on this device";
        _hotseatMode.Pressed += () => SetMode(vsBot: false);
        modes.AddChild(_botMode);
        modes.AddChild(_hotseatMode);
        modes.AddChild(MenuKit.Hint("Online play arrives in a later version."));
        root.AddChild(modes);

        var seats = new HBoxContainer();
        seats.AddThemeConstantOverride("separation", 24);
        root.AddChild(seats);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        root.AddChild(loading);
        await App.Instance.ContentReady;
        loading.Text = "";
        _decks.AddRange(App.Instance.Decks.List(App.Instance.Module));

        for (int i = 0; i < 2; i++)
        {
            int seat = i;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 12);
            _seatTitles[seat] = MenuKit.SectionTitle($"Player {seat + 1}");
            box.AddChild(_seatTitles[seat]);
            _names[seat] = MenuKit.TextField(Settings.Current.PlayerNames.ElementAtOrDefault(seat) ?? $"Player {seat + 1}", "Name");
            box.AddChild(MenuKit.Row("Name", _names[seat], 90));
            _deckPickers[seat] = MenuKit.Options(_decks.Select(d => d.Name), Math.Min(seat, Math.Max(0, _decks.Count - 1)));
            _deckPickers[seat].ItemSelected += _ => UpdateSummary(seat);
            box.AddChild(MenuKit.Row("Deck", _deckPickers[seat], 90));
            _summaries[seat] = MenuKit.Hint("");
            box.AddChild(_summaries[seat]);
            var card = MenuKit.Card(box);
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            seats.AddChild(card);
        }

        root.AddChild(_sandbox);
        _sandbox.Disabled = App.Instance.Cards is null;

        _start = BoardStyle.MakePrimaryButton("Start game", 22);
        _start.CustomMinimumSize = new Vector2(260, 58);
        _start.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _start.Pressed += Start;
        root.AddChild(_start);

        if (_decks.Count == 0)
        {
            _summaries[0].Text = App.Instance.Cards is null
                ? "No content module: the game will use the generic demo decks."
                : "No decks yet. Build one in Decks, or the starter decks will appear once the content module is installed.";
        }
        UpdateSummary(0);
        UpdateSummary(1);
        SetMode(_vsBot);
    }

    private void SetMode(bool vsBot)
    {
        _vsBot = vsBot;
        // Restyle the two mode buttons as a segmented control.
        foreach (var (button, active) in new[] { (_botMode, vsBot), (_hotseatMode, !vsBot) })
        {
            var style = active ? BoardStyle.Box(BoardStyle.ActiveBorder, 8) : BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1);
            button.AddThemeStyleboxOverride("normal", style);
            button.AddThemeStyleboxOverride("hover", style);
            button.AddThemeColorOverride("font_color", active ? new Color("16171a") : BoardStyle.Text);
            button.AddThemeColorOverride("font_hover_color", active ? new Color("16171a") : BoardStyle.Text);
        }
        if (_seatTitles[1] is null) return;
        _seatTitles[0].Text = vsBot ? "You" : "Player 1";
        _seatTitles[1].Text = vsBot ? "Computer" : "Player 2";
        _names[1].Editable = !vsBot;
        _names[1].Text = vsBot ? "Computer" : Settings.Current.PlayerNames.ElementAtOrDefault(1) ?? "Player 2";
    }

    private DeckInfo? Selected(int seat) =>
        _decks.Count > 0 && _deckPickers[seat] is { } p && p.Selected >= 0 ? _decks[p.Selected] : null;

    private void UpdateSummary(int seat)
    {
        if (Selected(seat) is not { } info || App.Instance.Cards is not { } cards) return;
        var (deck, _) = App.Instance.Decks.Load(info);
        var format = App.Instance.FormatById(info.FormatId);
        var issues = DeckValidator.Validate(deck, format, cards);
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count - errors;
        _summaries[seat].Text = $"{deck.Main.Sum(e => e.Count)} cards · {format.Name}"
                                + (errors > 0 ? $" · {errors} problem(s): {issues.First(i => i.Severity == IssueSeverity.Error).Message}" : " · legal")
                                + (warnings > 0 ? $" · {warnings} card(s) not fully supported" : "");
    }

    private void Start()
    {
        Settings.Current.PlayerNames[0] = _names[0]?.Text is { Length: > 0 } n0 ? n0 : "Player 1";
        if (!_vsBot) Settings.Current.PlayerNames[1] = _names[1]?.Text is { Length: > 0 } n1 ? n1 : "Player 2";
        Settings.Save();

        if (App.Instance.Cards is { } cards && Selected(0) is { } first && Selected(1) is { } second)
        {
            GameSession.Seat Seat(int index, DeckInfo info)
            {
                var (deck, _) = App.Instance.Decks.Load(info);
                var (definitions, unknown) = deck.Resolve(cards);
                if (unknown.Count > 0) GD.PushWarning($"Deck '{info.Name}': unknown cards {string.Join(", ", unknown)}");
                bool bot = _vsBot && index == 1;
                return new GameSession.Seat(bot ? "Computer" : Settings.Current.PlayerNames[index], definitions, bot);
            }
            var life = App.Instance.FormatById(first.FormatId).StartingLife;
            App.Instance.PendingMatch = new MatchSetup(Seat(0, first), Seat(1, second), life, _sandbox.ButtonPressed);
        }
        else
        {
            App.Instance.PendingMatch = null; // generic demo
        }
        App.Instance.GoTo(App.GameBoardScene);
    }
}
