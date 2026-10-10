// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Choose the mode, then players and decks, and start the match. The modes are the content's formats (each offering only
/// the decks legal in it, its rules governing the game) and the limited events, draft and sealed, which go on to the
/// limited screen.
/// </summary>
public partial class PlaySetup : Control
{
    private sealed class SeatControls
    {
        public required Label Title { get; init; }
        public required LineEdit Name { get; init; }
        public required OptionButton Deck { get; init; }
        public required Label Summary { get; init; }
    }

    private readonly List<DeckInfo> _allDecks = new();
    private readonly List<DeckInfo> _decks = new(); // those legal in the chosen format
    private readonly Dictionary<string, List<DeckInfo>> _legal = new();
    private readonly HFlowContainer _modes = new();
    private readonly Label _modeInfo = MenuKit.Hint("");
    private readonly Label _noDecks = MenuKit.Hint("");
    private Button _start = null!;
    private FormatRules _format = FormatRules.Casual;
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

        foreach (var label in new[] { _modeInfo, _noDecks })
        {
            label.AddThemeFontSizeOverride("font_size", 17); // what the mode is reads above the rest, not as a footnote
            label.AddThemeColorOverride("font_color", BoardStyle.Text);
        }
        _modes.AddThemeConstantOverride("h_separation", 10);
        _modes.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(_modes);
        root.AddChild(_modeInfo);

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
        root.AddChild(modes);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        root.AddChild(loading);
        await App.Instance.ContentReady;
        loading.QueueFree();
        _allDecks.AddRange(App.Instance.Decks.List(App.Instance.Module));

        root.AddChild(_noDecks);
        _seatBox.AddThemeConstantOverride("h_separation", 20);
        _seatBox.AddThemeConstantOverride("v_separation", 20);
        root.AddChild(_seatBox);
        root.AddChild(_rules);
        root.AddChild(_sandbox);
        _sandbox.Disabled = App.Instance.Cards is null;

        var start = _start = BoardStyle.MakePrimaryButton("Start game", 22);
        start.CustomMinimumSize = new Vector2(260, 58);
        start.AnchorLeft = 1; start.AnchorRight = 1; start.AnchorTop = 1; start.AnchorBottom = 1;
        start.GrowHorizontal = GrowDirection.Begin;
        start.GrowVertical = GrowDirection.Begin;
        start.OffsetRight = -140; start.OffsetBottom = -32;
        start.Pressed += Start;
        AddChild(start);

        BuildModes();
        ChooseMode(Settings.Current.PlayMode is "draft" or "sealed" ? FormatRules.Casual.Id : Settings.Current.PlayMode); // only formats open here
    }

    /// <summary>One button per format of the content (limited aside), then draft and sealed when there are cards to open.</summary>
    private void BuildModes()
    {
        var modes = App.Instance.Formats.Where(f => !f.Limited).Select(f => (f.Id, f.Name, f.Description)).ToList();
        if (App.Instance.Cards is not null)
        {
            modes.Add(("draft", "Draft", "Open boosters with computer players and pass them around, taking one card at a time; then build a deck and play rounds."));
            modes.Add(("sealed", "Sealed", "Open six boosters, build a deck from what you get and play rounds."));
        }
        foreach (var (id, name, description) in modes)
        {
            var button = BoardStyle.MakeButton(name, 17);
            button.CustomMinimumSize = new Vector2(140, 44);
            button.TooltipText = description;
            button.SetMeta("mode", id);
            button.Pressed += () => ChooseMode(id);
            _modes.AddChild(button);
        }
    }

    /// <summary>A format offers its legal decks and sets the game's rules; draft and sealed go on to the limited screen.</summary>
    private void ChooseMode(string id)
    {
        if (id is "draft" or "sealed")
        {
            if (App.Instance.Cards is null) id = FormatRules.Casual.Id;
            else
            {
                // Not remembered as the mode: coming back to this screen must not open the event screen again.
                LimitedScreen.StartMode = id == "draft" ? Arcanum.Data.Limited.LimitedMode.Draft : Arcanum.Data.Limited.LimitedMode.Sealed;
                LimitedScreen.OfferToResume = true;
                App.Instance.GoTo(App.LimitedScene);
                return;
            }
        }
        _format = App.Instance.Formats.FirstOrDefault(f => f.Id == id && !f.Limited) ?? App.Instance.Formats.FirstOrDefault(f => !f.Limited) ?? FormatRules.Casual;
        Settings.Current.PlayMode = _format.Id;
        Settings.Save();
        foreach (var button in _modes.GetChildren().OfType<Button>()) BoardStyle.StyleChoice(button, (string)button.GetMeta("mode") == _format.Id);
        _modeInfo.Text = _format.Description;

        var keep = _seats.Select(seat => Selected(_seats.IndexOf(seat))?.Name).ToList();
        _decks.Clear();
        _decks.AddRange(LegalDecks(_format));
        _noDecks.Text = _decks.Count == 0 ? $"No deck is legal in {_format.Name} yet: build one under Decks, or choose another mode." : "";
        _noDecks.Visible = _decks.Count == 0;
        _seatBox.Visible = _decks.Count > 0 || App.Instance.Cards is null;
        _start.Disabled = _decks.Count == 0 && App.Instance.Cards is not null;
        BuildSeats(keep);
    }

    /// <summary>The saved and starter decks with no problem in <paramref name="format"/> (all of them without card data to check).</summary>
    private List<DeckInfo> LegalDecks(FormatRules format)
    {
        if (App.Instance.Cards is not { } cards) return _allDecks;
        if (!_legal.TryGetValue(format.Id, out var legal))
            _legal[format.Id] = legal = _allDecks
                .Where(d => DeckValidator.Validate(App.Instance.Decks.Load(d).Deck, format, cards).All(i => i.Severity != IssueSeverity.Error))
                .ToList();
        return legal;
    }

    private int PlayerCount => _playerCount.Selected + 2;

    private void BuildSeats() => BuildSeats(_seats.Select((_, i) => Selected(i)?.Name).ToList());

    /// <summary>The seats again, each keeping the deck named in <paramref name="previous"/> when it is still offered.</summary>
    private void BuildSeats(IReadOnlyList<string?> previous)
    {
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
            int kept = _decks.FindIndex(d => d.Name == previous.ElementAtOrDefault(seat));
            var deck = MenuKit.Options(_decks.Select(d => d.Name), kept >= 0 ? kept : fallback);
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
        foreach (var (button, active) in new[] { (_botMode, vsBot), (_hotseatMode, !vsBot) }) BoardStyle.StyleChoice(button, active);
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

    /// <summary>The game's rules are the chosen mode's (e.g. commander rules and 40 life).</summary>
    private FormatRules GameFormat => _format;

    private void UpdateRules()
    {
        var format = GameFormat;
        var text = $"Rules: {format.Name} · {format.StartingLife} life";
        if (format.Commander) text += " · commanders, commander tax, 21 commander damage";
        _rules.Text = text;
    }

    private void UpdateSummary(int seat)
    {
        if (Selected(seat) is not { } info || App.Instance.Cards is not { } cards) return;
        var (deck, _) = App.Instance.Decks.Load(info);
        var format = GameFormat;
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
                ? deck.ResolveCommanders(cards)
                : null;
            if (!format.Commander) definitions.AddRange(deck.ResolveCommanders(cards));
            bool bot = _vsBot && i > 0;
            seats.Add(new GameSession.Seat(_seats[i].Name.Text is { Length: > 0 } n ? n : $"Player {i + 1}", definitions, bot, commanders));
        }
        App.Instance.PendingMatch = new MatchSetup(seats, format.StartingLife, format.Commander, _sandbox.ButtonPressed);
        App.Instance.GoTo(App.GameBoardScene);
    }
}
