// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Arcanum.UI.Menu;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// Building career decks on the desk, seen from above: the cards owned are laid out on the playmat on the left (filtered
/// and a page at a time), the deck on the right in columns by mana value, lands apart, basic lands free. Click a card of
/// the collection to put a copy in the deck (never more than owned), click a card of the deck to take it out.
/// </summary>
public partial class DeckDesk : Control
{
    /// <summary>
    /// A deck built from an event's pool instead of the collection: the pool, the deck so far, what's wrong with a deck,
    /// a deck built automatically, and what happens when the player leaves (keeping the deck) or is done.
    /// </summary>
    public sealed record PoolBuild(IReadOnlyList<PoolCard> Pool, DeckList Deck, Func<DeckList, List<string>> Problems,
        Func<DeckList> AutoBuild, Action<DeckList> Keep, Action<DeckList> Done)
    {
        /// <summary>The set's draft statistics, to show on the cards (null: none, or the player doesn't see them).</summary>
        public DraftRatings? Ratings { get; init; }
    }

    /// <summary>Set before opening the desk to build from a pool (an event's); null builds career decks.</summary>
    public static PoolBuild? Building { get; set; }

    private PoolBuild? _pool;

    /// <summary>
    /// Choosing one card on the mat instead of building (an ante's stake): the cards to choose from, the tag each card
    /// shows (its price), and what happens when one is picked or the player goes back.
    /// </summary>
    /// <param name="InDecks">The names of the cards in the player's decks: a filter then hides them.</param>
    public sealed record CardPick(string Title, IReadOnlyList<CardKey> Cards, Func<CardKey, string> Tag, Action<CardKey> Picked, Action Cancelled,
        IReadOnlySet<string>? InDecks = null);

    /// <summary>Set before opening the desk to pick a card; null builds a deck.</summary>
    public static CardPick? Picking { get; set; }

    private CardPick? _pick;
    private bool _hideInDecks;

    /// <summary>Cards across the collection's part of the mat: all of it when picking a card.</summary>
    private int GridColumns => _pick is null ? 6 : 10;

    private const int Rows = 3;
    private static readonly string[] ColumnNames = { "1", "2", "3", "4", "5", "6+", "Lands" };
    private static readonly (string Label, CardType Type)[] TypeFilters =
    {
        ("Any type", CardType.None), ("Creatures", CardType.Creature), ("Instants", CardType.Instant), ("Sorceries", CardType.Sorcery),
        ("Artifacts", CardType.Artifact), ("Enchantments", CardType.Enchantment), ("Planeswalkers", CardType.Planeswalker), ("Lands", CardType.Land),
    };

    private DeckList _deck = new();
    private bool _commander;

    // The deck's format: any constructed format of the module (casual by default); Commander decks have a commander column.
    private string _formatId = CareerFormatCasual;
    private const string CareerFormatCasual = "casual";
    private readonly OptionButton _format = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(220, 0), ClipText = true };
    private List<FormatRules> _formats = new();

    // Room on the mat for the deck's columns (set by the layout, so the columns never feed back into their own size).
    private float _deckRoom;
    private DeckInfo? _info;
    private bool _dirty;
    private int _page;
    private string _search = "";
    private int _type;
    private string? _set; // the set filter (code, upper case), null for every set
    private readonly OptionButton _sets = new() { FocusMode = FocusModeEnum.None };
    private readonly HashSet<char> _colors = new();

    private readonly LineEdit _name = new() { PlaceholderText = "Deck name", CustomMinimumSize = new Vector2(200, 0) };
    private readonly OptionButton _decks = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(280, 0), ClipText = true };
    private readonly GridContainer _grid = new();
    private readonly Label _pageInfo = CareerStyle.Text("", 18, new Color(1f, 0.95f, 0.85f));
    private readonly HBoxContainer _columns = new();
    private readonly HBoxContainer _basics = new();
    private readonly Label _count = CareerStyle.Text("", 26, new Color(1f, 0.95f, 0.85f));
    private readonly Label _problems = CareerStyle.Text("", 17, CareerStyle.Ink, wrap: true);
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private List<DeckInfo> _saved = new();

    // Suggestions for a Commander deck's commander (community statistics from the module's source), shown on the mat instead
    // of the collection: every suggested card, marked owned or not.
    private static readonly string[] SuggestionSorts = { "Most played", "Highest synergy" };
    private readonly HBoxContainer _filterControls = new();
    private readonly HBoxContainer _suggestBar = new() { Visible = false };
    private readonly Button _suggestButton = new() { Text = "Suggestions", ToggleMode = true, FocusMode = FocusModeEnum.None, Visible = false };
    private readonly OptionButton _suggestSection = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(260, 0), ClipText = true };
    private readonly OptionButton _suggestSort = new() { FocusMode = FocusModeEnum.None };
    private readonly OptionButton _suggestVariant = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(240, 0), ClipText = true };
    private readonly CheckButton _ownedOnly = new() { Text = "Only mine", FocusMode = FocusModeEnum.None };
    private readonly Label _noteInfo = CareerStyle.Text("", 15, CareerStyle.Ink, wrap: true);
    private bool _suggesting;
    private CommanderInsights? _insights;
    private string? _insightsKey;
    private string _insightsSlug = "";
    private readonly List<string?> _variants = new();
    private List<(CardEntry Card, InsightCard Stats, Owned? Owned)> _suggested = new();
    private string _suggestNote = "";
    private Dictionary<string, Owned>? _collection;

    private static CareerService Career => App.Instance.Career;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = CareerStyle.Notes;
        _pool = Building;
        Building = null;
        _pick = Picking;
        Picking = null;
        _grid.Columns = GridColumns;
        if (!Career.HasCareer || App.Instance.Cards is null)
        {
            Callable.From(() => App.Instance.GoTo(App.CareerScene)).CallDeferred();
            return;
        }
        BuildTable();
        BuildLayout();
        _preview.Size = new Vector2(300, 418);
        AddChild(_preview);
        if (_pool is not null)
        {
            _deck = DeckList.Parse(_pool.Deck.Export());
            Refresh();
            return;
        }
        if (_pick is not null)
        {
            ShowPage(0);
            return;
        }
        RefreshSaved();
        if (_saved.Count > 0) Load(_saved[0]);
        else NewDeck();
        // Testing: ARCANUM_SUGGEST=1 shows the suggestions for the first deck's commander.
        if (OS.GetEnvironment("ARCANUM_SUGGEST") == "1") SetSuggesting(true);
        // Testing: ARCANUM_DESK_FORMATS=commander,casual,... switches the first deck to those formats one after another.
        if (OS.GetEnvironment("ARCANUM_DESK_FORMATS") is { Length: > 0 } switches)
        {
            double delay = 1;
            foreach (var id in switches.Split(','))
            {
                int index = _formats.FindIndex(f => f.Id == id);
                if (index < 0) continue;
                GetTree().CreateTimer(delay).Timeout += () => { _format.Selected = index; _format.EmitSignal(OptionButton.SignalName.ItemSelected, index); };
                delay += 0.5;
            }
        }
    }

    // ---------------------------------------------------------------- the desk

    private const string Kit = "res://ui-art/kit/career/";
    private ArtScene _desk = null!;
    private Rect2 _matArea;
    private readonly Panel _felt = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _matPicture = new()
    {
        MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
    };
    private readonly VBoxContainer _top = new(), _left = new();
    private readonly HBoxContainer _matBox = new(), _bottom = new();
    private readonly ScrollContainer _deckScroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private Vector2 _tile = new(120, 167);

    /// <summary>
    /// The desk from above (desk.jpg, its playmat's area in desk.json): the deck's controls on the wood above the mat,
    /// the cards on the mat, the basic lands and the deck's state on the wood below. A playmat picture (mats/default.jpg)
    /// is drawn over the mat when there is one; without the desk, a felt mat on wood.
    /// </summary>
    private void BuildTable()
    {
        AddChild(new ColorRect { Color = new Color("3b2618"), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var picture = ResourceLoader.Exists(Kit + "desk.jpg") ? GD.Load<Texture2D>(Kit + "desk.jpg") : null;
        _desk = new ArtScene(picture, Array.Empty<Hotspot>());
        _desk.BackRequested += Leave;
        AddChild(_desk);
        _matArea = Hotspot.LoadArea(Kit + "desk.json", "mat", new Rect2(0.035f, 0.15f, 0.93f, 0.73f));
        // The playmat is drawn over the desk at the size the file gives (larger than the one in the picture): a dark cloth on
        // the desk picture, a felt one without it.
        var cloth = picture is null
            ? CareerStyle.Box(new Color("1f3b3a"), new Color("c9a45c"), 3, 18)
            : CareerStyle.Box(new Color("262429"), new Color("34313a"), 3, 16);
        cloth.ShadowColor = new Color(0, 0, 0, 0.55f);
        cloth.ShadowSize = 16;
        cloth.ShadowOffset = new Vector2(0, 6);
        _felt.AddThemeStyleboxOverride("panel", cloth);
        AddChild(_felt);
        if (ResourceLoader.Exists(Kit + "mats/default.jpg"))
        {
            _matPicture.Texture = GD.Load<Texture2D>(Kit + "mats/default.jpg");
            AddChild(_matPicture);
        }
        Resized += Layout;
    }

    private Rect2 MatOnScreen()
    {
        var from = _desk.ToScreen(_matArea.Position);
        return new Rect2(from, _desk.ToScreen(_matArea.End) - from);
    }

    private void Layout()
    {
        var mat = MatOnScreen();
        _felt.Position = _matPicture.Position = mat.Position;
        _felt.Size = _matPicture.Size = mat.Size;
        _top.Position = new Vector2(mat.Position.X, 14);
        _top.Size = new Vector2(mat.Size.X, Math.Max(0, mat.Position.Y - 28));
        var inner = mat.Grow(-22);
        _matBox.Position = inner.Position;
        _matBox.Size = inner.Size;
        _bottom.Position = new Vector2(mat.Position.X, mat.End.Y + 14);
        _bottom.Size = new Vector2(mat.Size.X, Math.Max(0, Size.Y - mat.End.Y - 28));
        // Cards of the collection: six across half the mat, three rows under the pager.
        int columns = GridColumns;
        float width = (inner.Size.X * (_pick is null ? 0.54f : 1f) - (columns - 1) * 8) / columns;
        float height = (inner.Size.Y - 52 - (Rows - 1) * 8) / Rows;
        width = Math.Max(60, Math.Min(width, height / 1.39f));
        _tile = new Vector2(width, width * 1.39f);
        _left.CustomMinimumSize = new Vector2(columns * width + (columns - 1) * 8, 0);
        _deckRoom = inner.Size.X - _left.CustomMinimumSize.X - 28 - 20; // the gap, the scroll bar
        if (IsNodeReady()) Refresh();
    }

    private void BuildLayout()
    {
        _top.AddThemeConstantOverride("separation", 10);
        _top.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(_top);

        // Above the mat: leave, the deck chosen, its name, saving; then the filters.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var back = CareerStyle.Button(_pick is not null ? "< Back" : _pool is null ? "< Room" : "< Event", Leave, 20);
        back.TooltipText = _pick is not null ? "Back without choosing (Esc)" : _pool is null ? "Back to your room (Esc)" : "Back to the event, keeping this deck (Esc)";
        row.AddChild(back);
        if (_pick is { } pick)
        {
            row.AddChild(CareerStyle.Text(pick.Title, 22, new Color(1f, 0.95f, 0.85f)));
            if (pick.InDecks is not null)
            {
                row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
                var deckOnly = new CheckButton { Text = "Hide cards in my decks", FocusMode = FocusModeEnum.None };
                deckOnly.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.85f));
                deckOnly.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.95f, 0.85f));
                deckOnly.AddThemeColorOverride("font_hover_color", new Color(1f, 0.95f, 0.85f));
                deckOnly.AddThemeFontSizeOverride("font_size", 20);
                deckOnly.Toggled += on => { _hideInDecks = on; ShowPage(0); };
                row.AddChild(deckOnly);
            }
            _top.AddChild(row);
            BuildFilters();
            _deckScroll.Visible = false;
            _bottom.Visible = false;
            return;
        }
        if (_pool is { } pool)
        {
            row.AddChild(CareerStyle.Text("Build your deck from the cards you drafted: 40 cards or more, basic lands are free.", 20, new Color(1f, 0.95f, 0.85f)));
            row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            row.AddChild(CareerStyle.Button("Build for me", () => { _deck = pool.AutoBuild(); Refresh(); }, 20));
            var done = CareerStyle.Button("Done", () =>
            {
                if (pool.Problems(_deck) is [var first, ..]) MenuKit.Toast(this, first);
                else pool.Done(_deck);
            }, 20, primary: true);
            row.AddChild(done);
            _top.AddChild(row);
            BuildFilters();
            return;
        }
        _decks.ItemSelected += i =>
        {
            int index = (int)i;
            if (index < _saved.Count) Confirm(() => Load(_saved[index]), SelectCurrent);
        };
        row.AddChild(_decks);
        _name.TextChanged += _ => Dirty();
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(_name);
        _formats = App.Instance.Formats.Where(f => !f.Limited).ToList();
        if (_formats.Count == 0) _formats = new List<FormatRules> { FormatRules.Casual, Career.World!.CommanderFormat };
        foreach (var format in _formats) _format.AddItem(format.Name + " deck");
        _format.ItemSelected += i =>
        {
            _formatId = _formats[(int)i].Id;
            _commander = _formats[(int)i].Commander;
            if (!_commander && _deck.Commander.Count > 0)
            {
                // The commanders go back into the deck.
                foreach (var c in _deck.Commander) DeckList.Adjust(_deck.Main, c.Name, c.Count, c.Set, c.Number, c.Foil);
                _deck.Commander.Clear();
            }
            Dirty();
            Refresh();
        };
        _format.TooltipText = "The deck's format: what it must follow to be played (Commander: 100 cards, one of each, led by a legendary creature)";
        row.AddChild(_format);
        _suggestButton.AddThemeFontSizeOverride("font_size", 20);
        _suggestButton.Toggled += SetSuggesting;
        row.AddChild(_suggestButton);
        row.AddChild(CareerStyle.Button("New", () => Confirm(NewDeck), 20));
        row.AddChild(CareerStyle.Button("Delete", AskDelete, 20));
        row.AddChild(CareerStyle.Button("Save", Save, 20, primary: true));
        _top.AddChild(row);
        BuildFilters();
    }

    private void BuildFilters()
    {
        var light = new Color(1f, 0.95f, 0.85f);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        _filterControls.AddThemeConstantOverride("separation", 8);
        filters.AddChild(_filterControls);
        if (_pick is null && _pool is null)
        {
            BuildSuggestionBar();
            filters.AddChild(_suggestBar);
        }
        var search = new LineEdit { Text = _search, PlaceholderText = CardSearchBox.Placeholder, CustomMinimumSize = new Vector2(380, 0) };
        CardSearchBox.Read(search, Cards);
        search.TextChanged += text => { _search = text; CardSearchBox.Read(search, Cards); ShowPage(0); };
        _filterControls.AddChild(search);
        foreach (char color in "WUBRGC")
        {
            var toggle = new Button { Text = color.ToString(), ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(40, 40) };
            var (bg, fg) = BoardStyle.PipColors(color);
            toggle.AddThemeStyleboxOverride("normal", CareerStyle.Box(bg with { A = 0.45f }, fg with { A = 0.4f }, 2, 20));
            toggle.AddThemeStyleboxOverride("hover", CareerStyle.Box(bg with { A = 0.7f }, fg, 2, 20));
            toggle.AddThemeStyleboxOverride("pressed", CareerStyle.Box(bg, CareerStyle.Gold, 3, 20));
            toggle.AddThemeColorOverride("font_color", fg);
            toggle.AddThemeColorOverride("font_pressed_color", fg);
            toggle.AddThemeColorOverride("font_hover_color", fg);
            toggle.Toggled += on => { if (on) _colors.Add(color); else _colors.Remove(color); ShowPage(0); };
            _filterControls.AddChild(toggle);
        }
        var type = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var (label, _) in TypeFilters) type.AddItem(label);
        type.ItemSelected += i => { _type = (int)i; ShowPage(0); };
        _filterControls.AddChild(type);
        // Sets: those of the cards on offer (filled once they are known).
        _sets.ItemSelected += i => { _set = i == 0 ? null : _sets.GetItemMetadata((int)i).AsString(); ShowPage(0); };
        _filterControls.AddChild(_sets);
        filters.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        filters.AddChild(_count);
        _top.AddChild(filters);

        // On the mat: the cards owned on the left, the deck on the right.
        _matBox.AddThemeConstantOverride("separation", 28);
        AddChild(_matBox);
        _left.AddThemeConstantOverride("separation", 8);
        _matBox.AddChild(_left);
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        _left.AddChild(_grid);
        var pager = new HBoxContainer();
        pager.AddThemeConstantOverride("separation", 12);
        pager.AddChild(CareerStyle.Button("<", () => ShowPage(_page - 1), 20));
        pager.AddChild(_pageInfo);
        pager.AddChild(CareerStyle.Button(">", () => ShowPage(_page + 1), 20));
        _left.AddChild(pager);
        _columns.AddThemeConstantOverride("separation", 6);
        _deckScroll.AddChild(_columns);
        _matBox.AddChild(_deckScroll);

        // Below the mat: basic lands and what's wrong with the deck.
        _bottom.AddThemeConstantOverride("separation", 16);
        _bottom.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(_bottom);
        _basics.AddThemeConstantOverride("separation", 10);
        _basics.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _bottom.AddChild(_basics);
        // A width of its own: a wrapping label measured at no width would make the bar tall.
        var note = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(380, 0) };
        note.AddThemeStyleboxOverride("panel", CareerStyle.Box(new Color("f7e9a8"), new Color("c9b25a"), 1, 2, 10));
        var noteText = new VBoxContainer();
        noteText.AddThemeConstantOverride("separation", 4);
        noteText.AddChild(_problems);
        // The suggestions' note, or the credit of the draft statistics shown on the pool's cards.
        _noteInfo.Visible = _pool?.Ratings is not null;
        if (_pool?.Ratings is { } ratings) _noteInfo.Text = ratings.Credit;
        noteText.AddChild(_noteInfo);
        note.AddChild(noteText);
        _bottom.AddChild(note);
        _pageInfo.AddThemeColorOverride("font_color", light);
        Callable.From(Layout).CallDeferred();
    }

    // ---------------------------------------------------------------- the collection

    private sealed record Owned(CardEntry Card, DeckEntry Printing, int Count);

    /// <summary>The cards owned that pass the filters, one per name (with the printing owned most), by mana value.</summary>
    private List<Owned> OwnedCards()
    {
        var filter = Cards.Filter(_search);
        bool Found(CardKey k) => _search.Length == 0 || Cards.Find(k.Name) is { } entry && filter.Matches(entry, k.Set, k.Number, k.Foil);
        if (_pick is not null)
            return _pick.Cards.Where(k => Cards.Find(k.Name) is not null && InSet(k) && Found(k) && (!_hideInDecks || _pick.InDecks?.Contains(k.Name) != true))
                .Select(k => new Owned(Cards.Find(k.Name)!, k.ToEntry(), Career.State.Owned(k)))
                .Where(Passes).OrderBy(o => o.Card.ManaValue).ThenBy(o => o.Card.Name).ToList();
        var cards = (_pool is not null
            ? _pool.Pool.Select(c => (Card: CardKey.Of(c), Count: 1))
            : Career.State.Cards).Where(c => InSet(c.Card) && Found(c.Card));
        return ByName(cards).Where(Passes).OrderBy(o => o.Card.Definition.Is(CardType.Land)).ThenBy(o => o.Card.ManaValue).ThenBy(o => o.Card.Name).ToList();
    }

    /// <summary>The cards one per name, with the printing owned most and the copies of every printing; basic lands left out.</summary>
    private static IEnumerable<Owned> ByName(IEnumerable<(CardKey Card, int Count)> cards) => cards
        .GroupBy(c => Cards.Find(c.Card.Name)?.Name ?? c.Card.Name, StringComparer.OrdinalIgnoreCase) // a two-faced card saved by either name
        .Select(g => (Entry: Cards.Find(g.Key), Best: g.OrderByDescending(c => c.Count).First().Card, Count: g.Sum(c => c.Count)))
        .Where(x => x.Entry is not null && !Career.World!.IsBasicLand(x.Entry.Name))
        .Select(x => new Owned(x.Entry!, new DeckEntry(1, x.Entry!.Name, x.Best.Set.Length > 0 ? x.Best.Set : null, x.Best.Number.Length > 0 ? x.Best.Number : null, x.Best.Foil) { Surge = x.Best.Surge }, x.Count));

    /// <summary>The whole collection by card name, whatever the filters.</summary>
    private Dictionary<string, Owned> Collection() =>
        _collection ??= ByName(Career.State.Cards).ToDictionary(o => o.Card.Name, StringComparer.OrdinalIgnoreCase);

    private bool InSet(CardKey card) => _set is null || card.Set.Equals(_set, StringComparison.OrdinalIgnoreCase);

    /// <summary>The set filter's choices: every set the cards on offer come from, by name.</summary>
    private void FillSets()
    {
        var keys = _pick is not null ? _pick.Cards
            : _pool is not null ? _pool.Pool.Select(CardKey.Of).ToList()
            : Career.State.Cards.Select(c => c.Card).ToList();
        _sets.Clear();
        _sets.AddItem("All sets");
        foreach (var code in keys.Select(k => k.Set.ToUpperInvariant()).Where(c => c.Length > 0).Distinct().OrderBy(c => Career.World!.SetName(c.ToLowerInvariant())))
        {
            _sets.AddItem(Cards.FindSet(code)?.Name ?? Career.World!.SetName(code.ToLowerInvariant()));
            _sets.SetItemMetadata(_sets.ItemCount - 1, code);
        }
        int selected = _set is null ? 0 : Enumerable.Range(1, _sets.ItemCount - 1).FirstOrDefault(i => _sets.GetItemMetadata(i).AsString() == _set);
        _sets.Selected = selected;
        if (selected == 0) _set = null;
    }

    private bool Passes(Owned owned)
    {
        var d = owned.Card.Definition;
        if (TypeFilters[_type].Type != CardType.None && !d.Is(TypeFilters[_type].Type)) return false;
        if (_colors.Count == 0) return true;
        var colors = ColorsOf(d);
        return colors.Count == 0 ? _colors.Contains('C') : colors.Any(_colors.Contains);
    }

    private static HashSet<char> ColorsOf(CardDefinition d)
    {
        var colors = d.ManaCost.ToString().Where(c => "WUBRG".Contains(c)).ToHashSet();
        foreach (var c in d.Colors) if (c.Length > 0) colors.Add(c[0]);
        return colors;
    }

    /// <summary>Clamps the page to the cards there are, empties the grid and gives the number of pages.</summary>
    private int TurnTo(int page, int count)
    {
        int pages = Math.Max(1, (count + GridColumns * Rows - 1) / (GridColumns * Rows));
        _page = Math.Clamp(page, 0, pages - 1);
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        return pages;
    }

    private void ShowPage(int page)
    {
        if (_sets.ItemCount == 0) FillSets();
        if (_suggesting)
        {
            ShowSuggestionPage(page);
            return;
        }
        var cards = OwnedCards();
        int pageSize = GridColumns * Rows;
        int pages = TurnTo(page, cards.Count);
        foreach (var owned in cards.Skip(_page * pageSize).Take(pageSize))
        {
            if (_pick is { } pick)
            {
                var key = CardKey.Of(owned.Printing);
                var pickTile = Tile(Definition(owned.Printing), false);
                Badge(pickTile, pick.Tag(key), false);
                var pickCard = pickTile.GetChild<CardNode>(0);
                pickCard.TooltipText = "Choose this card";
                pickCard.Clicked += _ => pick.Picked(key);
                _grid.AddChild(pickTile);
                continue;
            }
            int inDeck = InDeck(owned.Card.Name);
            var tile = Tile(Definition(owned.Printing), owned.Count - inDeck <= 0);
            Badge(tile, $"{inDeck}/{owned.Count}", inDeck > 0);
            var card = tile.GetChild<CardNode>(0);
            card.Clicked += _ => Add(owned);
            card.RightClicked += _ =>
            {
                if (_commander && CanLead(owned.Card.Definition)) MakeCommander(owned);
                else Remove(owned.Card.Name);
            };
            if (_commander && CanLead(owned.Card.Definition)) card.TooltipText = "Right-click to make it your commander";
            if (_pool?.Ratings is { } ratings) DraftGrades.Mark(tile, card, ratings, owned.Card.Name);
            _grid.AddChild(tile);
        }
        _pageInfo.Text = cards.Count == 0 ? "No cards match" : $"{cards.Count} cards · page {_page + 1} of {pages}";
    }

    private Control Tile(CardDefinition definition, bool used)
    {
        var holder = new Control { CustomMinimumSize = _tile };
        var card = new CardNode { Size = _tile };
        holder.AddChild(card);
        card.Setup(ViewOf(definition), showCostPips: false);
        if (used) card.Modulate = new Color(1, 1, 1, 0.45f);
        card.HoverStarted += c => ShowPreview(c, holder);
        card.HoverEnded += _ => HidePreview();
        return holder;
    }

    private void Badge(Control tile, string text, bool lit)
    {
        // In the card's top right corner, whatever its width.
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorLeft = 1, AnchorRight = 1, OffsetRight = -4, OffsetTop = 4, GrowHorizontal = GrowDirection.Begin };
        panel.AddThemeStyleboxOverride("panel", CareerStyle.Box(lit ? BoardStyle.Playable : new Color(0, 0, 0, 0.7f), null, 0, 8, 3));
        panel.AddChild(CareerStyle.Text(text, 17, lit ? new Color("16171a") : Colors.White));
        tile.AddChild(panel);
    }

    // ---------------------------------------------------------------- suggestions (commander statistics, decks like this one)

    /// <summary>A deck is looked up among the community decks of its format (a casual one among those of any 60-card format).</summary>
    private string SuggestFormat => _formatId;
    private int SuggestSize => FormatRulesOf(_formatId).MinDeckSize;

    // Decks like this one are looked for again on the next load (opening suggestions).
    private bool _refind;

    private void BuildSuggestionBar()
    {
        _suggestBar.AddThemeConstantOverride("separation", 8);
        _suggestSection.TooltipText = "Group of suggested cards";
        _suggestSection.ItemSelected += _ => ShowSuggestions();
        foreach (var sort in SuggestionSorts) _suggestSort.AddItem(sort);
        _suggestSort.TooltipText = "Most played: share of the decks that could play the card that do · Highest synergy: played the most above other decks of its colors";
        _suggestSort.ItemSelected += _ => ShowSuggestions();
        _suggestVariant.TooltipText = "All the commander's decks, only budget or expensive ones, or one theme";
        _suggestVariant.ItemSelected += _ => LoadInsights();
        var light = new Color(1f, 0.95f, 0.85f);
        _ownedOnly.AddThemeColorOverride("font_color", light);
        _ownedOnly.AddThemeColorOverride("font_pressed_color", light);
        _ownedOnly.AddThemeColorOverride("font_hover_color", light);
        _ownedOnly.AddThemeFontSizeOverride("font_size", 18);
        _ownedOnly.TooltipText = "Show only the suggested cards you have";
        _ownedOnly.Toggled += _ => ShowSuggestions();
        var average = CareerStyle.Button("Use average deck", AskAverageDeck, 18);
        average.TooltipText = "Replace the main deck with the average deck of the suggestions (or the deck chosen beside), with the cards you have";
        _suggestBar.AddChild(_suggestSection);
        _suggestBar.AddChild(_suggestSort);
        _suggestBar.AddChild(_suggestVariant);
        _suggestBar.AddChild(_ownedOnly);
        _suggestBar.AddChild(average);
    }

    private static string Signed(double share) => (share >= 0 ? "+" : "−") + Math.Abs(share).ToString("P0");

    /// <summary>
    /// Whether suggestions can be shown (not while building an event's deck or choosing a card): a Commander deck with its
    /// commander and the module's commander statistics, or a casual deck and the module's deck search.
    /// </summary>
    private bool CanSuggest => _pool is null && _pick is null && (_commander
        ? _deck.Commander.Count > 0 && CommanderInsightsSource.Available(App.Instance.Module)
        : SimilarDecksSource.Available(App.Instance.Module, SuggestFormat));

    /// <summary>What the suggestions are for: the commander, or casual decks (looked for again each time suggestions are opened).</summary>
    private string SuggestKey() => _commander ? CommanderInsights.Slug(_deck.Commander.Select(e => e.Name)) : "similar:" + SuggestFormat;

    /// <summary>Shows or hides the suggestions button as the deck changes, and reloads suggestions for a new commander.</summary>
    private void RefreshSuggestMode()
    {
        _suggestButton.Visible = CanSuggest;
        _suggestButton.TooltipText = _commander ? "The cards most played with your commander by the community's decks, marked as yours or not"
            : "The cards played by the community's decks most like yours, marked as yours or not";
        if (!CanSuggest && _suggesting) SetSuggesting(false);
        else if (_suggesting && SuggestKey() != _insightsSlug) LoadInsights();
        else if (_suggesting) RefreshSuggestInfo();
    }

    private void SetSuggesting(bool on)
    {
        _suggesting = on && CanSuggest;
        _suggestButton.SetPressedNoSignal(_suggesting);
        _filterControls.Visible = !_suggesting;
        _suggestBar.Visible = _suggesting;
        _noteInfo.Visible = _suggesting;
        if (_suggesting)
        {
            _suggestSort.TooltipText = _commander
                ? "Most played: share of the decks that could play the card that do · Highest synergy: played the most above other decks of its colors"
                : "Most played: share of the decks like yours that play the card · Highest synergy: played more by the decks most like yours";
            _suggestVariant.TooltipText = _commander ? "All the commander's decks, only budget or expensive ones, or one theme"
                : "All the decks like yours, or one of them (the ones with the most of your cards first)";
            if (!_commander)
            {
                // The deck may have changed since the decks like it were found.
                _refind = true;
                _insightsKey = null;
                _insightsSlug = "";
            }
            LoadInsights();
        }
        else
        {
            if (_insights is null) _insightsKey = null; // try again next time
            ShowPage(0);
        }
    }

    private string? SelectedVariant => _suggestVariant.Selected >= 0 && _suggestVariant.Selected < _variants.Count ? _variants[_suggestVariant.Selected] : null;

    /// <summary>Downloads (or reads from the cache) the statistics for the deck's commander and the chosen theme, then shows them.</summary>
    private async void LoadInsights()
    {
        if (App.Instance.Module is not { } module) return;
        string slug = SuggestKey();
        bool commander = _commander;
        if (slug != _insightsSlug)
        {
            _variants.Clear();
            _suggestVariant.Clear();
            _insightsSlug = slug;
        }
        string? variant = SelectedVariant;
        string key = slug + "|" + variant;
        if (key == _insightsKey)
        {
            // Already shown, or still loading.
            if (_insights is not null) ShowSuggestions();
            return;
        }
        _insightsKey = key;
        _insights = null;
        _suggested = new();
        _suggestNote = commander ? "Loading suggestions…" : "Looking for decks like yours…";
        _noteInfo.Text = "";
        ShowPage(0);
        bool refind = _refind;
        _refind = false;
        var insights = commander
            ? await CommanderInsightsSource.PageAsync(module, slug, variant)
            : await SimilarDecksSource.SuggestAsync(module, Cards, SuggestFormat, SuggestSize, _deck, variant, refind);
        if (_insightsKey != key || !IsInsideTree()) return; // the commander, deck kind or theme changed while loading
        _insights = insights;
        if (insights is null)
        {
            _suggestNote = commander ? "No suggestions for this commander" : "No decks like yours";
            _noteInfo.Text = commander ? "There are no community statistics for this commander (or they couldn't be downloaded)."
                : "No community decks like this one were found (or they couldn't be downloaded).";
            if (_suggesting) ShowPage(0);
            return;
        }
        if (_variants.Count == 0)
        {
            // Themes come with the commander's own page; a theme's page lists the same ones. For a casual deck each deck
            // like it is a "theme", the ones with the most of the player's cards first.
            void Variant(string label, string? value) { _variants.Add(value); _suggestVariant.AddItem(label); }
            if (commander)
            {
                Variant($"All decks ({insights.Decks:N0})", null);
                Variant("Budget", "budget");
                Variant("Expensive", "expensive");
                foreach (var theme in insights.Themes.Take(30)) Variant($"{theme.Name} ({theme.Decks:N0})", theme.Slug);
            }
            else
            {
                Variant($"All decks like yours ({insights.Decks})", null);
                var collection = Collection();
                foreach (var (deck, owned) in SimilarDecksSource.LastFound(SuggestFormat).Select(d => (d, OwnedShare(d.List, collection))).OrderByDescending(x => x.Item2))
                    Variant($"{deck.Found.Name} · {owned:P0} yours", deck.Found.Id.ToString());
            }
            _suggestVariant.Selected = 0;
        }
        _suggestSection.Clear();
        _suggestSection.AddItem("All suggestions");
        foreach (var section in insights.Sections) _suggestSection.AddItem($"{section.Title} ({section.Cards.Count})");
        _suggestSection.Selected = 0;
        ShowSuggestions();
    }

    /// <summary>Lists the chosen group of suggestions the card pool has, sorted as chosen, each with the copies owned (if any).</summary>
    private void ShowSuggestions()
    {
        if (_insights is not { } insights) return;
        int section = _suggestSection.Selected;
        var cards = section <= 0 || section > insights.Sections.Count
            ? insights.Sections.SelectMany(s => s.Cards).DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : insights.Sections[section - 1].Cards;
        cards = _suggestSort.Selected == 1 ? cards.OrderByDescending(c => c.Synergy) : cards.OrderByDescending(c => c.Inclusion);
        var collection = Collection();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _suggested = new();
        foreach (var suggestion in cards)
        {
            // Basic lands are free: they are added from below the mat.
            if (Cards.Find(suggestion.Name) is not { } entry || Career.World!.IsBasicLand(entry.Name) || !seen.Add(entry.Name)) continue;
            var owned = collection.GetValueOrDefault(entry.Name);
            if (owned is null && _ownedOnly.ButtonPressed) continue;
            _suggested.Add((entry, suggestion, owned));
        }
        _suggestNote = _ownedOnly.ButtonPressed ? "You have none of these cards" : "No suggestions";
        ShowPage(0);
        RefreshSuggestInfo();
    }

    private void ShowSuggestionPage(int page)
    {
        int pageSize = GridColumns * Rows;
        int pages = TurnTo(page, _suggested.Count);
        foreach (var (entry, stats, owned) in _suggested.Skip(_page * pageSize).Take(pageSize))
        {
            int inDeck = InDeck(entry.Name);
            var tile = Tile(owned is null ? entry.Definition : Definition(owned.Printing), owned is null || owned.Count - inDeck <= 0);
            Badge(tile, owned is null ? "Not yours" : $"{inDeck}/{owned.Count}", inDeck > 0);
            // How much it is played, at the card's foot.
            DraftGrades.Strip(tile, $"{stats.Inclusion:P0} · {Signed(stats.Synergy)}", Colors.White);
            var card = tile.GetChild<CardNode>(0);
            card.TooltipText = (owned is null ? "You don't have this card." : "Click to put a copy in the deck, right-click to take one out.")
                + $"\nIn {stats.Inclusion:P0} of the decks that could play it ({stats.Decks:N0} of {stats.PotentialDecks:N0})."
                + (_commander
                    ? $"\nSynergy {Signed(stats.Synergy)}: how much more it is played with this commander than in other decks of its colors."
                    : $"\nSynergy {Signed(stats.Synergy)}: how much more the decks most like yours play it.");
            card.Clicked += _ =>
            {
                if (owned is null) MenuKit.Toast(this, $"You don't have {entry.Name}.");
                else Add(owned);
            };
            card.RightClicked += _ => Remove(entry.Name);
            _grid.AddChild(tile);
        }
        int mine = _suggested.Count(s => s.Owned is not null);
        _pageInfo.Text = _suggested.Count == 0 ? _suggestNote : $"{_suggested.Count} cards, {mine} yours · page {_page + 1} of {pages}";
    }

    /// <summary>The number of decks behind the suggestions and the average deck's card types beside this deck's.</summary>
    private void RefreshSuggestInfo()
    {
        if (_insights is not { } insights) return;
        var mine = _deck.Main.GroupBy(e => TypeKey(Definition(e))).ToDictionary(g => g.Key, g => g.Sum(e => e.Count));
        var types = new (string Key, string Label)[]
        {
            ("creature", "creatures"), ("planeswalker", "planeswalkers"), ("instant", "instants"), ("sorcery", "sorceries"),
            ("artifact", "artifacts"), ("enchantment", "enchantments"), ("land", "lands"),
        };
        var parts = types.Where(t => insights.AverageTypes.GetValueOrDefault(t.Key) > 0)
            .Select(t => $"{insights.AverageTypes[t.Key]} {t.Label} (yours {mine.GetValueOrDefault(t.Key)})");
        _noteInfo.Text = (_commander
                             ? $"From {insights.Decks:N0} community decks · % = decks that play it · synergy = above other decks of its colors\n"
                             : $"From {insights.Decks:N0} community deck{(insights.Decks == 1 ? "" : "s")} like yours · % = decks that play it · synergy = played more by the decks most like yours\n")
                         + (insights.Decks == 1 ? "This deck: " : "Average deck: ") + string.Join(" · ", parts);
    }

    /// <summary>Share of a deck's main cards (basic lands aside, which are free) the player has copies for.</summary>
    private double OwnedShare(DeckList deck, Dictionary<string, Owned> collection)
    {
        int total = 0, have = 0;
        foreach (var line in deck.Main)
        {
            if (Career.World!.IsBasicLand(line.Name)) continue;
            total += line.Count;
            have += Math.Min(line.Count, collection.GetValueOrDefault(line.Name)?.Count ?? 0);
        }
        return total == 0 ? 1 : (double)have / total;
    }

    private static string TypeKey(CardDefinition d) => d switch
    {
        _ when d.Is(CardType.Land) => "land",
        _ when d.Is(CardType.Creature) => "creature",
        _ when d.Is(CardType.Planeswalker) => "planeswalker",
        _ when d.Is(CardType.Instant) => "instant",
        _ when d.Is(CardType.Sorcery) => "sorcery",
        _ when d.Is(CardType.Artifact) => "artifact",
        _ when d.Is(CardType.Enchantment) => "enchantment",
        _ => "other",
    };

    private void AskAverageDeck()
    {
        if (App.Instance.Module is not { } module || !CanSuggest) return;
        string slug = SuggestKey();
        string? variant = SelectedVariant;
        bool commander = _commander;
        string theme = variant is null ? "" : $" ({_suggestVariant.GetItemText(_suggestVariant.Selected)})";
        MenuKit.Modal(this, commander || variant is null ? "Use the average deck?" : "Use this deck?",
            commander ? $"The main deck becomes the average deck of this commander{theme}, with the cards you have; the ones you don't have are left out."
            : variant is null ? "The main deck becomes the average of the decks like yours, with the cards you have; the ones you don't have are left out."
            : $"The main deck becomes {_suggestVariant.GetItemText(_suggestVariant.Selected)}, with the cards you have; the ones you don't have are left out.", null,
            ("Keep my deck", () => { }),
            ("Use it", async () =>
            {
                var average = commander
                    ? await CommanderInsightsSource.AverageDeckAsync(module, slug, variant)
                    : SimilarDecksSource.Average(SuggestFormat, variant, SuggestSize);
                if (!IsInsideTree() || SuggestKey() != slug) return;
                if (average is null) MenuKit.Toast(this, commander ? "There is no average deck for this commander." : "There are no decks like yours yet.");
                else UseAverageDeck(average);
            }));
    }

    /// <summary>Makes the average deck the main deck with the copies owned (basic lands are free), and lists what was left out.</summary>
    private void UseAverageDeck(DeckList average)
    {
        var collection = Collection();
        var missing = new List<string>();
        _deck.Main.Clear();
        foreach (var line in average.Main)
        {
            var entry = Cards.Find(line.Name);
            if (entry is not null && Career.World!.IsBasicLand(entry.Name))
            {
                DeckList.Adjust(_deck.Main, entry.Name, line.Count);
                continue;
            }
            if (entry is null || collection.GetValueOrDefault(entry.Name) is not { } owned)
            {
                missing.Add(line.Name);
                continue;
            }
            int copies = Math.Min(line.Count, owned.Count - InDeck(owned.Card.Name));
            if (copies > 0) DeckList.Adjust(_deck.Main, owned.Card.Name, copies, owned.Printing.Set, owned.Printing.Number, owned.Printing.Foil, owned.Printing.Surge);
            if (copies < line.Count) missing.Add(owned.Card.Name);
        }
        Dirty();
        Refresh();
        int total = _deck.Main.Sum(e => e.Count) + _deck.Commander.Sum(e => e.Count);
        if (missing.Count == 0)
        {
            MenuKit.Toast(this, $"Average deck loaded: {total} cards");
            return;
        }
        MenuKit.Modal(this, "Average deck",
            $"Average deck loaded with the cards you have: {total} cards.\n\nYou don't have ({missing.Count}): " + string.Join(", ", missing), null,
            ("OK", () => { }));
    }

    // ---------------------------------------------------------------- the deck

    private int InDeck(string name) => _deck.Main.Concat(_deck.Commander).Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);

    /// <summary>A legendary creature can lead a Commander deck.</summary>
    private static bool CanLead(Arcanum.Engine.Cards.CardDefinition d) => (d.Supertypes & Supertype.Legendary) != 0 && d.Is(CardType.Creature);

    /// <summary>Makes a card the deck's commander (the one before goes back into the deck).</summary>
    private void MakeCommander(Owned owned)
    {
        foreach (var c in _deck.Commander) DeckList.Adjust(_deck.Main, c.Name, c.Count, c.Set, c.Number, c.Foil);
        _deck.Commander.Clear();
        var inMain = _deck.Main.FirstOrDefault(e => e.Name.Equals(owned.Card.Name, StringComparison.OrdinalIgnoreCase));
        if (inMain is not null) DeckList.Adjust(_deck.Main, inMain.Name, -1, inMain.Set, inMain.Number, inMain.Foil);
        else if (InDeck(owned.Card.Name) >= owned.Count)
        {
            MenuKit.Toast(this, $"You have {owned.Count} {owned.Card.Name}.");
            return;
        }
        _deck.Commander.Add(owned.Printing with { Count = 1 });
        Dirty();
        Refresh();
    }

    private void Add(Owned owned)
    {
        if (InDeck(owned.Card.Name) >= owned.Count)
        {
            MenuKit.Toast(this, $"You have {owned.Count} {owned.Card.Name}.");
            return;
        }
        DeckList.Adjust(_deck.Main, owned.Card.Name, 1, owned.Printing.Set, owned.Printing.Number, owned.Printing.Foil, owned.Printing.Surge);
        Dirty();
        Refresh();
    }

    private void Remove(string name)
    {
        var entry = _deck.Main.LastOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        DeckList.Adjust(_deck.Main, entry.Name, -1, entry.Set, entry.Number, entry.Foil, entry.Surge);
        Dirty();
        Refresh();
    }

    private void Refresh()
    {
        HidePreview();
        ShowPage(_page);
        if (_pick is null) ShowDeck();
        if (_pick is null && _pool is null) RefreshSuggestMode();
    }

    private void ShowDeck()
    {
        foreach (var child in _columns.GetChildren()) child.QueueFree();
        if (_formats.Count > 0) _format.Selected = Math.Max(0, _formats.FindIndex(f => f.Id == _formatId));
        float columnWidth = _tile.X * DeckScale();
        if (_commander)
        {
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(columnWidth, 0) };
            box.AddThemeConstantOverride("separation", 2);
            box.AddChild(ColumnTitle("Commander", CareerStyle.Gold, columnWidth));
            var size = _tile * DeckScale();
            foreach (var entry in _deck.Commander)
            {
                var card = new CardNode { Size = size, CustomMinimumSize = size };
                card.Setup(ViewOf(Definition(entry)), showCostPips: false);
                card.HoverStarted += c => ShowPreview(c, c);
                card.HoverEnded += _ => HidePreview();
                card.Clicked += _ => { _deck.Commander.Remove(entry); Dirty(); Refresh(); };
                card.TooltipText = "Click to take it out";
                box.AddChild(card);
            }
            if (_deck.Commander.Count == 0)
            {
                var hint = CareerStyle.Text("Right-click a legendary creature of yours", 14, new Color(1f, 0.95f, 0.85f, 0.7f), wrap: true);
                hint.CustomMinimumSize = new Vector2(columnWidth, 0);
                box.AddChild(hint);
            }
            _columns.AddChild(box);
        }
        var cards = _deck.Main.SelectMany(e => Enumerable.Repeat(e, e.Count))
            .Where(e => !Career.World!.IsBasicLand(e.Name))
            .Select(e => (Entry: e, Def: Definition(e))).ToList();
        for (int column = 0; column < ColumnNames.Length; column++)
        {
            var inColumn = cards.Where(c => ColumnOf(c.Def) == column).OrderBy(c => c.Def.ManaCost.ManaValue).ThenBy(c => c.Entry.Name).ToList();
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(columnWidth, 0) };
            box.AddThemeConstantOverride("separation", 2);
            box.AddChild(ColumnTitle($"{ColumnNames[column]} ({inColumn.Count})", new Color(1f, 0.95f, 0.85f, 0.85f), columnWidth));
            // Cards overlap, each showing its title bar; the last one shows whole.
            float step = 26, small = DeckScale();
            var stack = new Control { CustomMinimumSize = new Vector2(_tile.X * small, inColumn.Count == 0 ? 0 : step * (inColumn.Count - 1) + _tile.Y * small) };
            for (int i = 0; i < inColumn.Count; i++)
            {
                var (entry, def) = inColumn[i];
                var card = new CardNode { Size = _tile * small, Position = new Vector2(0, i * step) };
                card.Setup(ViewOf(def), showCostPips: false);
                card.HoverStarted += c => ShowPreview(c, c);
                card.HoverEnded += _ => HidePreview();
                card.Clicked += _ => Remove(entry.Name);
                card.TooltipText = "Click to take it out";
                stack.AddChild(card);
            }
            box.AddChild(stack);
            _columns.AddChild(box);
        }

        // Basic lands are free.
        foreach (var child in _basics.GetChildren()) child.QueueFree();
        _basics.AddChild(CareerStyle.Text("Basic lands (free):", 18, new Color(1f, 0.95f, 0.85f)));
        foreach (var (color, name) in LimitedService.Basics)
        {
            int count = InDeck(name);
            var (bg, fg) = BoardStyle.PipColors(ColorLetter(color));
            var cell = new HBoxContainer();
            cell.AddThemeConstantOverride("separation", 2);
            cell.AddChild(CareerStyle.Button("-", () => { Remove(name); }, 18));
            var label = CareerStyle.Text($" {name[0]} {count} ", 20, fg);
            var chip = new PanelContainer();
            chip.AddThemeStyleboxOverride("panel", CareerStyle.Box(bg, null, 0, 6, 4));
            chip.AddChild(label);
            chip.TooltipText = name;
            cell.AddChild(chip);
            cell.AddChild(CareerStyle.Button("+", () => { DeckList.Adjust(_deck.Main, name, 1); Dirty(); Refresh(); }, 18));
            _basics.AddChild(cell);
        }

        int total = _deck.Main.Sum(e => e.Count) + _deck.Commander.Sum(e => e.Count);
        _count.Text = $"{total} cards";
        var problems = _pool is not null ? _pool.Problems(_deck) : CareerRules.DeckProblems(_deck, Career.State, Career.World!, FormatRulesOf(_formatId))
            .Select(p => p.Replace(" this deck uses", "")).ToList();
        _problems.Text = problems.Count == 0 ? "Ready to play." : string.Join("\n", problems.Take(_suggesting ? 1 : _noteInfo.Visible ? 2 : 3)); // room for the note below
        _problems.AddThemeColorOverride("font_color", problems.Count == 0 ? CareerStyle.Good : CareerStyle.Bad);
    }

    /// <summary>
    /// The deck's cards are drawn smaller so that its columns (seven, eight with the commander) fit the room the layout
    /// leaves beside the collection.
    /// </summary>
    private float DeckScale()
    {
        int columns = ColumnNames.Length + (_commander ? 1 : 0);
        return Math.Clamp((_deckRoom - (columns - 1) * 6) / columns / _tile.X, 0.3f, 0.85f);
    }

    /// <summary>A deck column's title, cut to the column's width.</summary>
    private static Label ColumnTitle(string text, Color color, float width)
    {
        var label = CareerStyle.Text(text, 16, color);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.CustomMinimumSize = new Vector2(width, 0);
        return label;
    }

    /// <summary>The rules a deck of the format follows in the career (the career's own casual and Commander rules for those).</summary>
    private FormatRules FormatRulesOf(string id) =>
        id == CareerRules.Commander ? Career.World!.CommanderFormat
        : id == CareerFormatCasual ? FormatRules.Casual
        : _formats.FirstOrDefault(f => f.Id == id) ?? FormatRules.Casual;

    private static int ColumnOf(CardDefinition d) => d.Is(CardType.Land) ? 6 : Math.Clamp(d.ManaCost.ManaValue - 1, 0, 5);

    private static char ColorLetter(ManaType color) => color switch
    {
        ManaType.White => 'W', ManaType.Blue => 'U', ManaType.Black => 'B', ManaType.Red => 'R', ManaType.Green => 'G', _ => 'C',
    };

    // ---------------------------------------------------------------- saving

    private void RefreshSaved()
    {
        _saved = Career.Decks.List(null);
        _decks.Clear();
        foreach (var deck in _saved) _decks.AddItem(deck.Name);
        _decks.AddItem("(new deck)");
        SelectCurrent();
    }

    private void SelectCurrent()
    {
        int index = _info is null ? -1 : _saved.FindIndex(d => d.Path == _info.Path);
        _decks.Selected = index >= 0 ? index : _saved.Count;
    }

    private void Load(DeckInfo info)
    {
        (_deck, _) = Career.Decks.Load(info);
        _formatId = _deck.Commander.Count > 0 ? CareerRules.Commander
            : _formats.Any(f => f.Id == info.FormatId) || info.FormatId == CareerRules.Commander ? info.FormatId : CareerFormatCasual;
        _commander = _formatId == CareerRules.Commander;
        _info = info;
        _name.Text = info.Name;
        _dirty = false;
        SelectCurrent();
        Refresh();
    }

    private void NewDeck()
    {
        _deck = new DeckList();
        _commander = false;
        _formatId = CareerFormatCasual;
        _info = null;
        _name.Text = "New deck";
        _dirty = false;
        SelectCurrent();
        Refresh();
    }

    private void Dirty() => _dirty = true;

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            MenuKit.Toast(this, "Give the deck a name first.");
            return;
        }
        _info = Career.Decks.Save(name, _formatId, _deck, _info);
        _dirty = false;
        RefreshSaved();
        MenuKit.Toast(this, $"Saved “{name}”");
    }

    private void AskDelete()
    {
        if (_info is null)
        {
            MenuKit.Toast(this, "This deck isn't saved yet.");
            return;
        }
        var info = _info;
        MenuKit.Modal(this, "Delete this deck?", $"“{info.Name}” goes away; its cards stay in your collection.", null,
            ("Keep it", () => { }), ("Delete", () => { Career.Decks.Delete(info); RefreshSaved(); NewDeck(); }));
    }

    private void Confirm(Action proceed, Action? cancelled = null)
    {
        if (!_dirty) { proceed(); return; }
        MenuKit.Modal(this, "Unsaved changes", "Discard the changes to this deck?", null,
            ("Keep editing", () => cancelled?.Invoke()), ("Discard", proceed));
    }

    private void Leave()
    {
        if (_pick is not null) _pick.Cancelled();
        else if (_pool is not null) _pool.Keep(_deck);
        else Confirm(() => App.Instance.GoTo(App.CareerScene));
    }

    // ---------------------------------------------------------------- helpers

    private static CardDefinition Definition(DeckEntry entry) =>
        DeckList.TryResolve(Cards, entry, out var d) ? d : Cards.Find(entry.Name)!.Definition;

    private static CardView ViewOf(CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey, Foil = d.Foil, Surge = d.Surge,
    };

    private void ShowPreview(CardNode card, Control anchor)
    {
        if (card.View is not { } view) return;
        _preview.Setup(view, false);
        var rect = anchor.GetGlobalRect();
        float x = rect.End.X + 14 + _preview.Size.X < GetViewportRect().Size.X ? rect.End.X + 14 : rect.Position.X - _preview.Size.X - 14;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 80, 20, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    private void HidePreview() => _preview.Visible = false;
}
