// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Deck builder: search the card pool with filters, click to add (right-click to remove), see the deck grouped
/// by type with a mana curve and live format validation; save, import and export deck lists.
/// </summary>
public partial class DeckBuilder : Control
{
    private const int Columns = 7;
    private const int Rows = 3;
    private static readonly Vector2 TileSize = new(168, 234);

    private static readonly (string Label, CardType Type)[] TypeFilters =
    {
        ("Any type", CardType.None), ("Creature", CardType.Creature), ("Instant", CardType.Instant), ("Sorcery", CardType.Sorcery),
        ("Artifact", CardType.Artifact), ("Enchantment", CardType.Enchantment), ("Planeswalker", CardType.Planeswalker), ("Land", CardType.Land),
    };

    private static readonly string[] ManaValueFilters = { "Any mana value", "0", "1", "2", "3", "4", "5", "6", "7+" };

    // State
    private DeckList _deck = new();
    private DeckInfo? _info;
    private bool _dirty;
    private bool _editingSideboard;
    private List<CardEntry> _results = new();
    private int _page;
    private readonly List<DeckInfo> _saved = new();

    // Search controls
    private readonly LineEdit _search = MenuKit.TextField("", "Search name, type or rules text…");
    private readonly Dictionary<string, Button> _colorToggles = new();
    private readonly OptionButton _typeFilter = MenuKit.Options(TypeFilters.Select(t => t.Label));
    private readonly OptionButton _mvFilter = MenuKit.Options(ManaValueFilters);
    private readonly CheckButton _supportedOnly = MenuKit.Toggle("Playable only", true);
    private readonly CheckButton _legalOnly = MenuKit.Toggle("Legal in format", true);
    private readonly Godot.Timer _searchDelay = new() { WaitTime = 0.3, OneShot = true };
    private readonly GridContainer _grid = new() { Columns = Columns };
    private readonly Label _resultInfo = BoardStyle.MakeLabel("", 14, BoardStyle.TextDim);
    private readonly Button _prev = BoardStyle.MakeButton("‹ Prev", 15);
    private readonly Button _next = BoardStyle.MakeButton("Next ›", 15);

    // Deck panel controls
    private readonly OptionButton _deckPicker = MenuKit.Options(Array.Empty<string>());
    private readonly LineEdit _name = MenuKit.TextField("", "Deck name");
    private readonly OptionButton _format = MenuKit.Options(Array.Empty<string>());
    private readonly Button _mainTab = BoardStyle.MakeButton("Main", 15);
    private readonly Button _sideTab = BoardStyle.MakeButton("Sideboard", 15);
    private readonly VBoxContainer _list = new();
    private readonly HBoxContainer _curve = new();
    private readonly RichTextLabel _validation = new() { BbcodeEnabled = true, FitContent = true, ScrollActive = false };

    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private readonly AcceptDialog _importDialog = new();
    private readonly TextEdit _importText = new();

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        var header = MenuKit.AddHeader(this, "Decks", () => ConfirmDiscard(() => App.Instance.GoTo(App.MainMenuScene)));
        _headerTitle = header.GetChild<Label>(1);
        BuildHeaderActions(header);
        BuildBody();
        BuildDialogs();

        _preview.Size = new Vector2(320, 446);
        AddChild(_preview);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        loading.Position = new Vector2(140, 140);
        AddChild(loading);
        if (!await App.Instance.ContentReady)
        {
            loading.Text = "Card data isn't available: install a content module to build decks.";
            return;
        }
        loading.QueueFree();

        foreach (var f in App.Instance.Formats) _format.AddItem(f.Name);
        _format.Selected = 0;
        RefreshSavedDecks();
        NewDeck();
        RunSearch();
        // Debug: ARCANUM_OPEN_DECK=<n> opens the n-th saved/starter deck (screenshots, smoke tests).
        if (int.TryParse(OS.GetEnvironment("ARCANUM_OPEN_DECK"), out int open) && open >= 0 && open < _saved.Count) LoadDeck(_saved[open]);
    }

    private CardDatabase Cards => App.Instance.Cards!;
    private FormatRules Format => App.Instance.Formats[Math.Max(0, _format.Selected)];

    // ---------------------------------------------------------------- layout

    private void BuildHeaderActions(HBoxContainer header)
    {
        header.AddChild(BoardStyle.MakeLabel("Open:", 15, BoardStyle.TextDim));
        _deckPicker.CustomMinimumSize = new Vector2(260, 40);
        // Item 0 is a placeholder; saved decks follow it.
        _deckPicker.ItemSelected += index =>
        {
            if (index <= 0) return;
            ConfirmDiscard(() => LoadDeck(_saved[(int)index - 1]), SelectCurrentInPicker);
        };
        header.AddChild(_deckPicker);
        void Action(string text, Action action, bool primary = false)
        {
            var b = primary ? BoardStyle.MakePrimaryButton(text, 16) : BoardStyle.MakeButton(text, 16);
            b.CustomMinimumSize = new Vector2(96, 44);
            b.Pressed += action;
            header.AddChild(b);
        }
        Action("New", () => ConfirmDiscard(NewDeck));
        Action("Import", () => { _importText.Text = ""; _importDialog.PopupCentered(); });
        Action("Export", Export);
        Action("Delete", AskDelete);
        Action("Save", Save, primary: true);
    }

    private void BuildBody()
    {
        var body = new HBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = 96, OffsetRight = -24, OffsetBottom = -20 };
        body.AddThemeConstantOverride("separation", 20);
        AddChild(body);

        // Left: filters, results, pages.
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        body.AddChild(left);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 10);
        _search.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _search.TextChanged += _ => _searchDelay.Start();
        filters.AddChild(_search);
        foreach (var color in new[] { "W", "U", "B", "R", "G", "C" })
        {
            var (bg, fg) = BoardStyle.PipColors(color[0]);
            var toggle = new Button { Text = color, ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(40, 40),
                TooltipText = color == "C" ? "Colorless" : "Color " + color };
            toggle.AddThemeColorOverride("font_color", fg);
            toggle.AddThemeColorOverride("font_pressed_color", fg);
            toggle.AddThemeStyleboxOverride("normal", BoardStyle.Box(bg with { A = 0.35f }, 20));
            toggle.AddThemeStyleboxOverride("hover", BoardStyle.Box(bg with { A = 0.6f }, 20));
            toggle.AddThemeStyleboxOverride("pressed", BoardStyle.Box(bg, 20, Colors.White, 2));
            toggle.Toggled += _ => RunSearch();
            _colorToggles[color] = toggle;
            filters.AddChild(toggle);
        }
        _typeFilter.ItemSelected += _ => RunSearch();
        _mvFilter.ItemSelected += _ => RunSearch();
        filters.AddChild(_typeFilter);
        filters.AddChild(_mvFilter);
        left.AddChild(filters);

        var toggles = new HBoxContainer();
        toggles.AddThemeConstantOverride("separation", 24);
        _supportedOnly.TooltipText = "Hide cards whose rules the engine can't run yet";
        _supportedOnly.Toggled += _ => RunSearch();
        _legalOnly.Toggled += _ => RunSearch();
        toggles.AddChild(_supportedOnly);
        toggles.AddChild(_legalOnly);
        toggles.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        toggles.AddChild(BoardStyle.MakeLabel("Click a card to add it · right-click to remove", 13, BoardStyle.TextDim));
        left.AddChild(toggles);

        _grid.AddThemeConstantOverride("h_separation", 12);
        _grid.AddThemeConstantOverride("v_separation", 12);
        _grid.SizeFlagsVertical = SizeFlags.ExpandFill;
        left.AddChild(_grid);

        var pages = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        pages.AddThemeConstantOverride("separation", 16);
        _prev.CustomMinimumSize = new Vector2(110, 40);
        _next.CustomMinimumSize = new Vector2(110, 40);
        _prev.Pressed += () => ShowPage(_page - 1);
        _next.Pressed += () => ShowPage(_page + 1);
        pages.AddChild(_prev);
        pages.AddChild(_resultInfo);
        pages.AddChild(_next);
        left.AddChild(pages);

        _searchDelay.Timeout += RunSearch;
        AddChild(_searchDelay);

        // Right: the deck.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(470, 0) };
        right.AddThemeConstantOverride("separation", 10);
        _name.TextChanged += _ => MarkDirty();
        right.AddChild(MenuKit.Row("Name", _name, 70));
        _format.ItemSelected += _ => { MarkDirty(); RunSearch(); };
        right.AddChild(MenuKit.Row("Format", _format, 70));

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 8);
        foreach (var tab in new[] { _mainTab, _sideTab })
        {
            tab.ToggleMode = true;
            tab.CustomMinimumSize = new Vector2(150, 38);
            tabs.AddChild(tab);
        }
        _mainTab.Pressed += () => { _editingSideboard = false; RefreshDeck(); };
        _sideTab.Pressed += () => { _editingSideboard = true; RefreshDeck(); };
        right.AddChild(tabs);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_list);
        right.AddChild(MenuKit.Card(scroll, 10));
        right.GetChild<Control>(right.GetChildCount() - 1).SizeFlagsVertical = SizeFlags.ExpandFill;

        right.AddChild(MenuKit.Hint("Mana curve (main deck, nonland)"));
        _curve.CustomMinimumSize = new Vector2(0, 70);
        _curve.AddThemeConstantOverride("separation", 6);
        right.AddChild(_curve);

        _validation.CustomMinimumSize = new Vector2(0, 90);
        _validation.AddThemeFontSizeOverride("normal_font_size", 13);
        _validation.AddThemeColorOverride("default_color", BoardStyle.Text);
        right.AddChild(MenuKit.Card(_validation, 10));
        body.AddChild(right);
    }

    private void BuildDialogs()
    {
        _importDialog.Title = "Import deck";
        _importDialog.OkButtonText = "Import";
        _importDialog.Size = new Vector2I(720, 560);
        var box = new VBoxContainer();
        box.AddChild(MenuKit.Hint("Paste a deck list: one \"4 Card Name\" per line. Set codes are ignored; \"Sideboard\", \"Commander\" and \"Companion\" sections are understood."));
        _importText.CustomMinimumSize = new Vector2(680, 420);
        _importText.AddThemeFontSizeOverride("font_size", 14);
        box.AddChild(_importText);
        _importDialog.AddChild(box);
        _importDialog.Confirmed += Import;
        AddChild(_importDialog);
    }

    // ---------------------------------------------------------------- search

    private void RunSearch()
    {
        if (App.Instance.Cards is null) return;
        var colors = _colorToggles.Where(kv => kv.Key != "C" && kv.Value.ButtonPressed).Select(kv => kv.Key).ToHashSet();
        int mv = _mvFilter.Selected;
        var query = new CardQuery
        {
            Text = _search.Text,
            Colors = colors,
            Colorless = _colorToggles["C"].ButtonPressed,
            Types = TypeFilters[Math.Max(0, _typeFilter.Selected)].Type,
            ManaValueMin = mv <= 0 ? null : mv - 1,
            ManaValueMax = mv <= 0 ? null : mv == ManaValueFilters.Length - 1 ? null : mv - 1,
            SupportedOnly = _supportedOnly.ButtonPressed,
            LegalIn = _legalOnly.ButtonPressed ? Format.Legality : null,
        };
        _results = Cards.Search(query).ToList();
        ShowPage(0);
    }

    private void ShowPage(int page)
    {
        int pageSize = Columns * Rows;
        int pages = Math.Max(1, (_results.Count + pageSize - 1) / pageSize);
        _page = Math.Clamp(page, 0, pages - 1);
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        foreach (var entry in _results.Skip(_page * pageSize).Take(pageSize)) _grid.AddChild(Tile(entry));
        _resultInfo.Text = _results.Count == 0 ? "No cards match" : $"{_results.Count:N0} cards · page {_page + 1} of {pages}";
        _prev.Disabled = _page == 0;
        _next.Disabled = _page >= pages - 1;
    }

    private static CardView ViewOf(CardDefinition d, int id) => new()
    {
        Id = new CardId(id), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness,
    };

    private Control Tile(CardEntry entry)
    {
        var tile = new Control { CustomMinimumSize = TileSize };
        var card = new CardNode { Size = TileSize };
        tile.AddChild(card);
        card.Setup(ViewOf(entry.Definition, -1), showCostPips: false);
        card.Clicked += _ => Add(entry.Name, 1);
        card.RightClicked += _ => Add(entry.Name, -1);
        card.HoverStarted += c => ShowPreview(c, tile);
        card.HoverEnded += _ => _preview.Visible = false;
        if (entry.Support != CardSupport.Full)
        {
            card.Modulate = new Color(1, 1, 1, 0.6f);
            card.TooltipText = "Not fully supported yet: some of its rules won't work in games.";
        }
        int copies = Section.Where(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);
        if (copies > 0)
        {
            var badge = BoardStyle.MakeLabel($"×{copies}", 18, new Color("16171a"), bold: true);
            var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(TileSize.X - 52, 8) };
            panel.AddThemeStyleboxOverride("panel", BoardStyle.Box(BoardStyle.Playable, 10));
            panel.AddChild(badge);
            tile.AddChild(panel);
        }
        return tile;
    }

    private void ShowPreview(CardNode card, Control anchor)
    {
        if (card.View is not { } view) return;
        _preview.Setup(view, showCostPips: false);
        var rect = anchor.GetGlobalRect();
        // Show it beside the hovered card, on whichever side has room.
        float x = rect.End.X + 12 + _preview.Size.X < GetViewportRect().Size.X - 480 ? rect.End.X + 12 : rect.Position.X - _preview.Size.X - 12;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 60, 90, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    // ---------------------------------------------------------------- deck editing

    private List<DeckEntry> Section => _editingSideboard ? _deck.Sideboard : _deck.Main;

    private void Add(string name, int delta)
    {
        DeckList.Adjust(Section, name, delta);
        MarkDirty();
        RefreshDeck();
        ShowPage(_page); // update copy badges
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    private Label? _headerTitle;

    private void UpdateTitle()
    {
        if (_headerTitle is not null)
            _headerTitle.Text = "Decks" + (_name.Text.Length > 0 ? $" · {_name.Text}" : "") + (_dirty ? " *" : "");
    }

    private void RefreshDeck()
    {
        _mainTab.Text = $"Main ({_deck.Main.Sum(e => e.Count)})";
        _sideTab.Text = $"Sideboard ({_deck.Sideboard.Sum(e => e.Count)})";
        _mainTab.SetPressedNoSignal(!_editingSideboard);
        _sideTab.SetPressedNoSignal(_editingSideboard);

        foreach (var child in _list.GetChildren()) child.QueueFree();
        var groups = Section
            .Select(e => (Entry: e, Card: Cards.Find(e.Name)))
            .GroupBy(x => GroupName(x.Card?.Definition))
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key));
        foreach (var group in groups)
        {
            _list.AddChild(BoardStyle.MakeLabel($"{group.Key} ({group.Sum(x => x.Entry.Count)})", 14, BoardStyle.TextDim, bold: true));
            foreach (var (entry, card) in group.OrderBy(x => x.Card?.ManaValue ?? 99).ThenBy(x => x.Entry.Name))
                _list.AddChild(DeckRow(entry, card));
        }
        if (Section.Count == 0) _list.AddChild(MenuKit.Hint(_editingSideboard ? "Sideboard is empty." : "Click cards on the left to add them."));

        RefreshCurve();
        RefreshValidation();
    }

    private static readonly string[] GroupOrder = { "Creatures", "Planeswalkers", "Instants", "Sorceries", "Artifacts", "Enchantments", "Lands", "Other", "Unknown" };

    private static string GroupName(CardDefinition? d) => d switch
    {
        null => "Unknown",
        _ when d.Is(CardType.Land) => "Lands",
        _ when d.Is(CardType.Creature) => "Creatures",
        _ when d.Is(CardType.Planeswalker) => "Planeswalkers",
        _ when d.Is(CardType.Instant) => "Instants",
        _ when d.Is(CardType.Sorcery) => "Sorceries",
        _ when d.Is(CardType.Artifact) => "Artifacts",
        _ when d.Is(CardType.Enchantment) => "Enchantments",
        _ => "Other",
    };

    private Control DeckRow(DeckEntry entry, CardEntry? card)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 8);
        var count = BoardStyle.MakeLabel(entry.Count.ToString(), 15, bold: true);
        count.CustomMinimumSize = new Vector2(26, 0);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(count);
        var name = BoardStyle.MakeLabel(entry.Name, 15, card is null ? BoardStyle.Attacking : card.Support == CardSupport.Full ? BoardStyle.Text : new Color("e0b050"));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Pass;
        if (card is not null)
        {
            row.MouseEntered += () =>
            {
                _preview.Setup(ViewOf(card.Definition, -2), false);
                var rect = row.GetGlobalRect();
                _preview.Position = new Vector2(rect.Position.X - _preview.Size.X - 16, Math.Clamp(rect.Position.Y - 120, 90, GetViewportRect().Size.Y - _preview.Size.Y - 20));
                _preview.Visible = true;
            };
            row.MouseExited += () => _preview.Visible = false;
        }
        row.AddChild(name);
        if (card is not null) row.AddChild(BoardStyle.MakeCostRow(card.Definition.ManaCost.ToString(), 18, 11));
        foreach (var (text, delta) in new[] { ("−", -1), ("+", 1) })
        {
            var b = BoardStyle.MakeButton(text, 14);
            b.CustomMinimumSize = new Vector2(30, 28);
            b.Pressed += () => Add(entry.Name, delta);
            row.AddChild(b);
        }
        return row;
    }

    private void RefreshCurve()
    {
        foreach (var child in _curve.GetChildren()) child.QueueFree();
        var counts = new int[8];
        foreach (var entry in _deck.Main)
        {
            if (Cards.Find(entry.Name) is not { } card || card.Definition.Is(CardType.Land)) continue;
            counts[Math.Min(7, card.ManaValue)] += entry.Count;
        }
        int max = Math.Max(1, counts.Max());
        for (int i = 0; i < counts.Length; i++)
        {
            var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 2);
            var bar = new Panel { CustomMinimumSize = new Vector2(0, 4 + 46f * counts[i] / max) };
            bar.AddThemeStyleboxOverride("panel", BoardStyle.Box(counts[i] > 0 ? BoardStyle.Playable : BoardStyle.PanelBorder, 3));
            column.AddChild(bar);
            var label = BoardStyle.MakeLabel(i == 7 ? "7+" : i.ToString(), 11, BoardStyle.TextDim);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            column.AddChild(label);
            column.TooltipText = $"{counts[i]} card(s) with mana value {(i == 7 ? "7+" : i)}";
            _curve.AddChild(column);
        }
    }

    private void RefreshValidation()
    {
        var issues = DeckValidator.Validate(_deck, Format, Cards);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        var warnings = issues.Where(i => i.Severity == IssueSeverity.Warning).ToList();
        var text = errors.Count == 0
            ? $"[color=#6fd08c]✓ Legal in {Format.Name}[/color]\n"
            : $"[color=#ff5a3c]✗ {errors.Count} problem(s) for {Format.Name}[/color]\n";
        foreach (var issue in errors.Take(5)) text += $"• {issue.Message}\n";
        if (errors.Count > 5) text += $"• …and {errors.Count - 5} more\n";
        if (warnings.Count > 0) text += $"[color=#e0b050]{warnings.Count} card(s) not fully supported yet.[/color]";
        _validation.Text = text;
    }

    // ---------------------------------------------------------------- files

    private void RefreshSavedDecks()
    {
        _saved.Clear();
        _saved.AddRange(App.Instance.Decks.List(App.Instance.Module));
        _deckPicker.Clear();
        _deckPicker.AddItem(_saved.Count == 0 ? "No saved decks" : "Choose a deck…");
        foreach (var d in _saved) _deckPicker.AddItem(d.Name);
        SelectCurrentInPicker();
    }

    private void SelectCurrentInPicker()
    {
        int index = _info is null ? -1 : _saved.FindIndex(d => d.Path == _info.Path);
        _deckPicker.Selected = index + 1;
    }

    private void NewDeck()
    {
        _deck = new DeckList();
        _info = null;
        _name.Text = "New deck";
        _editingSideboard = false;
        _dirty = false;
        SelectCurrentInPicker();
        RefreshDeck();
        UpdateTitle();
        ShowPage(_page);
    }

    private void LoadDeck(DeckInfo info)
    {
        var (deck, _) = App.Instance.Decks.Load(info);
        _deck = deck;
        _info = info;
        _name.Text = info.IsStarter ? info.Name.Replace(" (starter)", "") : info.Name;
        int formatIndex = App.Instance.Formats.FindIndex(f => f.Id == info.FormatId);
        _format.Selected = Math.Max(0, formatIndex);
        _editingSideboard = false;
        _dirty = false;
        SelectCurrentInPicker();
        RefreshDeck();
        UpdateTitle();
        RunSearch();
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            MenuKit.Toast(this, "Give the deck a name first.");
            return;
        }
        // Starter decks are read-only: saving one makes a personal copy.
        _info = App.Instance.Decks.Save(name, Format.Id, _deck, _info is { IsStarter: true } ? null : _info);
        _dirty = false;
        RefreshSavedDecks();
        UpdateTitle();
        MenuKit.Toast(this, $"Saved “{name}”");
    }

    private void AskDelete()
    {
        if (_info is null || _info.IsStarter)
        {
            MenuKit.Toast(this, _info is null ? "This deck isn't saved yet." : "Starter decks can't be deleted.");
            return;
        }
        Ask($"Delete “{_info.Name}”? This can't be undone.", () =>
        {
            App.Instance.Decks.Delete(_info);
            RefreshSavedDecks();
            NewDeck();
            MenuKit.Toast(this, "Deck deleted");
        });
    }

    private void Export()
    {
        DisplayServer.ClipboardSet(_deck.Export());
        MenuKit.Toast(this, "Deck list copied to the clipboard");
    }

    private void Import()
    {
        try
        {
            var deck = DeckList.Parse(_importText.Text);
            var unknown = deck.Main.Concat(deck.Sideboard).Concat(deck.Commander).Where(e => Cards.Find(e.Name) is null).Select(e => e.Name).Distinct().ToList();
            _deck = deck;
            _info = null;
            if (_name.Text is "" or "New deck") _name.Text = "Imported deck";
            _editingSideboard = false;
            MarkDirty();
            SelectCurrentInPicker();
            RefreshDeck();
            ShowPage(_page);
            MenuKit.Toast(this, unknown.Count == 0 ? "Deck imported" : $"Imported; {unknown.Count} unknown card(s) shown in red");
        }
        catch (FormatException e)
        {
            MenuKit.Toast(this, e.Message);
        }
    }

    private void ConfirmDiscard(Action proceed, Action? cancelled = null)
    {
        if (!_dirty) { proceed(); return; }
        Ask("Discard unsaved changes to this deck?", proceed, cancelled);
    }

    /// <summary>One-off yes/no dialog; freed when closed so its callbacks never leak into the next question.</summary>
    private void Ask(string question, Action yes, Action? no = null)
    {
        var dialog = new ConfirmationDialog { Title = "Arcanum", DialogText = question, Size = new Vector2I(460, 160) };
        dialog.Confirmed += () => { yes(); dialog.QueueFree(); };
        dialog.Canceled += () => { no?.Invoke(); dialog.QueueFree(); };
        AddChild(dialog);
        dialog.PopupCentered();
    }
}
