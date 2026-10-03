// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Choose mode, number of players and decks, then start the match.</summary>
public partial class PlaySetup : Control
{
    private sealed class SeatControls
    {
        public required Label Title { get; init; }
        public required LineEdit Name { get; init; }
        public required OptionButton Deck { get; init; }
        public required Label Summary { get; init; }
    }

    private readonly List<DeckInfo> _decks = new();
    private readonly List<SeatControls> _seats = new();
    private readonly HFlowContainer _seatBox = new();
    private readonly OptionButton _playerCount = MenuKit.Options(new[] { "2 players", "3 players", "4 players" });
    private readonly CheckButton _sandbox = MenuKit.Toggle("Start from the sandbox board (test mode)", false);
    private readonly Label _rules = MenuKit.Hint("");
    private Button _hotseatMode = null!, _botMode = null!;
    private bool _vsBot = true;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Play");

        var scroll = new ScrollContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 100, OffsetRight = -140, OffsetBottom = -110,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 20);
        scroll.AddChild(root);

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 12);
        _botMode = BoardStyle.MakeButton("Vs computer", 18);
        _hotseatMode = BoardStyle.MakeButton("Hotseat", 18);
        foreach (var (button, bot, tip) in new[] { (_botMode, true, "Play against the computer"), (_hotseatMode, false, "Everyone plays on this device") })
        {
            button.CustomMinimumSize = new Vector2(200, 48);
            button.TooltipText = tip;
            button.Pressed += () => SetMode(bot);
            modes.AddChild(button);
        }
        _playerCount.CustomMinimumSize = new Vector2(160, 48);
        _playerCount.ItemSelected += _ => BuildSeats();
        modes.AddChild(_playerCount);
        modes.AddChild(MenuKit.Hint("Online play arrives in a later version."));
        root.AddChild(modes);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        root.AddChild(loading);
        await App.Instance.ContentReady;
        loading.QueueFree();
        _decks.AddRange(App.Instance.Decks.List(App.Instance.Module));

        _seatBox.AddThemeConstantOverride("h_separation", 20);
        _seatBox.AddThemeConstantOverride("v_separation", 20);
        root.AddChild(_seatBox);
        root.AddChild(_rules);
        root.AddChild(_sandbox);
        _sandbox.Disabled = App.Instance.Cards is null;

        var start = BoardStyle.MakePrimaryButton("Start game", 22);
        start.CustomMinimumSize = new Vector2(260, 58);
        start.AnchorLeft = 1; start.AnchorRight = 1; start.AnchorTop = 1; start.AnchorBottom = 1;
        start.GrowHorizontal = GrowDirection.Begin;
        start.GrowVertical = GrowDirection.Begin;
        start.OffsetRight = -140; start.OffsetBottom = -32;
        start.Pressed += Start;
        AddChild(start);

        BuildSeats();
        SetMode(_vsBot);
    }

    private int PlayerCount => _playerCount.Selected + 2;

    private void BuildSeats()
    {
        var previous = _seats.Select(s => s.Deck.Selected).ToList();
        foreach (var child in _seatBox.GetChildren()) child.QueueFree();
        _seats.Clear();
        for (int i = 0; i < PlayerCount; i++)
        {
            int seat = i;
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 10);
            var title = MenuKit.SectionTitle("");
            box.AddChild(title);
            var name = MenuKit.TextField(Settings.Current.PlayerNames.ElementAtOrDefault(seat) ?? $"Player {seat + 1}", "Name");
            box.AddChild(MenuKit.Row("Name", name, 70));
            int fallback = _decks.Count == 0 ? 0 : seat % _decks.Count;
            var deck = MenuKit.Options(_decks.Select(d => d.Name), previous.ElementAtOrDefault(seat) is var p && p > 0 ? p : fallback);
            deck.ItemSelected += _ => { UpdateSummary(seat); UpdateRules(); };
            box.AddChild(MenuKit.Row("Deck", deck, 70));
            var summary = MenuKit.Hint("");
            box.AddChild(summary);
            var card = MenuKit.Card(box);
            card.CustomMinimumSize = new Vector2(PlayerCount <= 2 ? 780 : 520, 0);
            _seatBox.AddChild(card);
            _seats.Add(new SeatControls { Title = title, Name = name, Deck = deck, Summary = summary });
            UpdateSummary(seat);
        }
        SetMode(_vsBot);
        UpdateRules();
    }

    private void SetMode(bool vsBot)
    {
        _vsBot = vsBot;
        foreach (var (button, active) in new[] { (_botMode, vsBot), (_hotseatMode, !vsBot) })
        {
            var style = active ? BoardStyle.Box(BoardStyle.ActiveBorder, 8) : BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1);
            button.AddThemeStyleboxOverride("normal", style);
            button.AddThemeStyleboxOverride("hover", style);
            button.AddThemeColorOverride("font_color", active ? new Color("16171a") : BoardStyle.Text);
            button.AddThemeColorOverride("font_hover_color", active ? new Color("16171a") : BoardStyle.Text);
        }
        for (int i = 0; i < _seats.Count; i++)
        {
            bool bot = vsBot && i > 0;
            _seats[i].Title.Text = i == 0 ? (vsBot ? "You" : "Player 1") : bot ? $"Computer {(PlayerCount > 2 ? i.ToString() : "")}".Trim() : $"Player {i + 1}";
            _seats[i].Name.Editable = !bot;
            _seats[i].Name.Text = bot ? _seats[i].Title.Text : Settings.Current.PlayerNames.ElementAtOrDefault(i) ?? $"Player {i + 1}";
        }
    }

    private DeckInfo? Selected(int seat) =>
        _decks.Count > 0 && seat < _seats.Count && _seats[seat].Deck.Selected >= 0 ? _decks[_seats[seat].Deck.Selected] : null;

    /// <summary>The game's rules follow player 1's deck format (e.g. commander rules and 40 life).</summary>
    private FormatRules GameFormat => Selected(0) is { } d ? App.Instance.FormatById(d.FormatId) : FormatRules.Casual;

    private void UpdateRules()
    {
        var format = GameFormat;
        var text = $"Rules: {format.Name} · {format.StartingLife} life";
        if (format.Commander) text += " · commanders, commander tax, 21 commander damage";
        if (format.Commander && Enumerable.Range(0, _seats.Count).Any(i => Selected(i) is { } d && App.Instance.FormatById(d.FormatId).Commander == false))
            text += " — every player needs a commander deck";
        _rules.Text = text;
    }

    private void UpdateSummary(int seat)
    {
        if (Selected(seat) is not { } info || App.Instance.Cards is not { } cards) return;
        var (deck, _) = App.Instance.Decks.Load(info);
        var format = App.Instance.FormatById(info.FormatId);
        var issues = DeckValidator.Validate(deck, format, cards);
        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count - errors;
        string commander = deck.Commander.Count > 0 ? $" · led by {string.Join(" & ", deck.Commander.Select(c => c.Name))}" : "";
        _seats[seat].Summary.Text = $"{deck.Main.Sum(e => e.Count) + deck.Commander.Sum(e => e.Count)} cards · {format.Name}{commander}"
                                    + (errors > 0 ? $" · {errors} problem(s): {issues.First(i => i.Severity == IssueSeverity.Error).Message}" : " · legal")
                                    + (warnings > 0 ? $" · {warnings} card(s) not fully supported" : "");
    }

    private void Start()
    {
        Settings.Current.PlayerNames[0] = _seats.ElementAtOrDefault(0)?.Name.Text is { Length: > 0 } n0 ? n0 : "Player 1";
        if (!_vsBot && _seats.Count > 1) Settings.Current.PlayerNames[1] = _seats[1].Name.Text is { Length: > 0 } n1 ? n1 : "Player 2";
        Settings.Save();

        if (App.Instance.Cards is not { } cards || Enumerable.Range(0, _seats.Count).Any(i => Selected(i) is null))
        {
            App.Instance.PendingMatch = null; // generic demo
            App.Instance.GoTo(App.GameBoardScene);
            return;
        }
        var format = GameFormat;
        var seats = new List<GameSession.Seat>();
        for (int i = 0; i < _seats.Count; i++)
        {
            var info = Selected(i)!;
            var (deck, _) = App.Instance.Decks.Load(info);
            var (definitions, unknown) = deck.Resolve(cards);
            if (unknown.Count > 0) GD.PushWarning($"Deck '{info.Name}': unknown cards {string.Join(", ", unknown)}");
            var commanders = format.Commander
                ? deck.Commander.SelectMany(e => cards.TryGet(e.Name, out var d) ? Enumerable.Repeat(d, e.Count) : Enumerable.Empty<Arcanum.Engine.Cards.CardDefinition>()).ToList()
                : null;
            if (!format.Commander) definitions.AddRange(deck.Commander.SelectMany(e => cards.TryGet(e.Name, out var d) ? new[] { d } : Array.Empty<Arcanum.Engine.Cards.CardDefinition>()));
            bool bot = _vsBot && i > 0;
            seats.Add(new GameSession.Seat(_seats[i].Name.Text is { Length: > 0 } n ? n : $"Player {i + 1}", definitions, bot, commanders));
        }
        App.Instance.PendingMatch = new MatchSetup(seats, format.StartingLife, format.Commander, _sandbox.ButtonPressed);
        App.Instance.GoTo(App.GameBoardScene);
    }
}
