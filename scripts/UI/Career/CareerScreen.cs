// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Arcanum.UI.Menu;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// The career, played on pictures that fill the window. Home is the player's room: its computer zooms in to the store's
/// website (daily matches, events, selling cards, unopened boosters, signing up for a new career), the boxes of cards on
/// the shelf are the decks, the stack on the desk is the collection. The store is the store's picture: the booster rack,
/// the preconstructed decks and the counter that buys cards, each opening a note over the picture.
/// </summary>
public partial class CareerScreen : Control
{
    private enum Place { Room, Store }

    public enum WebPage { Matches, Events, Market, Cards }

    private const string Kit = "res://ui-art/kit/career/";
    private static readonly Vector2 TileSize = new(150, 210);

    private static readonly Hotspot[] RoomSpots =
    {
        Hotspot.Rect("computer", "Computer", 0.377f, 0.265f, 0.246f, 0.375f),
        Hotspot.Rect("decks", "Decks", 0.825f, 0.497f, 0.175f, 0.141f),
        Hotspot.Rect("collection", "My cards", 0.054f, 0.586f, 0.116f, 0.172f),
    };

    private static readonly Hotspot[] StoreSpots =
    {
        Hotspot.Rect("boosters", "Boosters", 0.47f, 0.16f, 0.25f, 0.63f),
        Hotspot.Rect("precons", "Preconstructed decks", 0.29f, 0.56f, 0.17f, 0.31f),
        Hotspot.Rect("counter", "Sell cards", 0.25f, 0.87f, 0.6f, 0.13f),
    };

    /// <summary>Set before coming back to this screen: what to show first ("store", or a website page).</summary>
    public static string? OpenOn { get; set; }

    private static bool _tested;

    private ArtScene _room = null!, _store = null!;
    private Rect2 _screenArea;
    private Place _place = Place.Room;
    private readonly Control _hud = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 50 };
    private readonly Label _back = CareerStyle.Text("", 20, new Color(1f, 0.95f, 0.85f));
    private readonly Label _money = CareerStyle.Text("", 20, new Color(1f, 0.95f, 0.85f));
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };

    // The website on the computer (when zoomed in) and a note over the store (when one is open).
    private Control? _web;
    private Control? _note;
    private WebPage _webPage = WebPage.Matches;
    private string _reward = CareerRules.CashReward;
    private int _deckIndex;
    private string _filter = "";

    private static CareerService Career => App.Instance.Career;
    private static CareerWorld World => Career.World!;
    private static CareerState State => Career.State;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = CareerStyle.Web; // the pixel font everywhere on these screens
        AddChild(new ColorRect { Color = new Color("0d0b0a"), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        _preview.Size = new Vector2(300, 418);

        var loading = CareerStyle.Text(App.Instance.ContentStatus, 22, new Color(1f, 0.95f, 0.85f));
        loading.Position = new Vector2(40, 40);
        AddChild(loading);
        if (!await App.Instance.ContentReady || Career.World is null)
        {
            loading.Text = Career.World is null && App.Instance.Cards is not null
                ? Career.Problem ?? "The installed content has no career."
                : "Card data isn't available: install a content module to play a career.";
            AddChild(BackTag(CareerStyle.Text("Esc  Back to the menu", 20, new Color(1f, 0.95f, 0.85f)), () => App.Instance.GoTo(App.MainMenuScene)));
            return;
        }
        loading.QueueFree();

        _room = new ArtScene(Picture("room.jpg"), Hotspot.Load(Kit + "room.json", RoomSpots));
        _screenArea = Hotspot.LoadArea(Kit + "room.json", "screen", new Rect2(0.4f, 0.307f, 0.199f, 0.275f));
        _room.Activated += OnRoom;
        _room.BackRequested += () => { if (_web is not null) CloseWeb(); else App.Instance.GoTo(App.MainMenuScene); };
        _room.ViewChanged += PlaceWeb;
        AddChild(_room);

        _store = new ArtScene(Picture("store.jpg"), Hotspot.Load(Kit + "store.json", StoreSpots)) { Visible = false };
        _store.Activated += OnStore;
        _store.BackRequested += () => { if (_note is not null) CloseNote(); else GoTo(Place.Room); };
        AddChild(_store);

        BuildHud();
        AddChild(_preview);

        // A daily match left before it ended counts as lost.
        if (Career.HasCareer && State.Pending is { } pending)
        {
            Career.Do(new FinishDaily(Career.Now, Won: false));
            MenuKit.Toast(this, $"The daily match against {OpponentName(pending.OpponentId)} wasn't finished: it counts as a loss.");
        }

        if (RunTestHooks()) return;
        var open = OpenOn;
        OpenOn = null;
        if (open == "store" && Career.HasCareer) GoTo(Place.Store);
        else if (!Career.HasCareer) OpenWeb(WebPage.Matches, instant: true);
        else if (Enum.TryParse<WebPage>(open, out var page)) OpenWeb(page, instant: true);
        UpdateHud();
        _room.GrabFocus();
    }

    private static Texture2D? Picture(string file) => ResourceLoader.Exists(Kit + file) ? GD.Load<Texture2D>(Kit + file) : null;

    /// <summary>
    /// Testing: ARCANUM_CAREER_START=&lt;starting deck&gt; starts a career when there is none; ARCANUM_CAREER_PAGE=&lt;page&gt; opens
    /// store, boosters, precons, counter, a website page (Matches, Events, Market, Cards), opening (the first unopened
    /// booster), decks (the deck builder) or daily (plays a daily match).
    /// </summary>
    private bool RunTestHooks()
    {
        if (!Career.HasCareer && OS.GetEnvironment("ARCANUM_CAREER_START") is { Length: > 0 } start) Career.Start(start, DeckTitle(start));
        if (_tested || OS.GetEnvironment("ARCANUM_CAREER_PAGE") is not { Length: > 0 } test || !Career.HasCareer) return false;
        _tested = true;
        switch (test.ToLowerInvariant())
        {
            case "decks": OpenDeckBuilder(); return true;
            case "daily" when Career.Decks.List(null) is [var first, ..]:
                Career.Do(new StartDaily(Career.Now, _reward));
                App.Instance.PendingMatch = Career.DailyMatch(Career.Decks.Load(first).Deck, Settings.PlayerName);
                App.Instance.GoTo(App.GameBoardScene);
                return true;
            case "store": GoTo(Place.Store); return true;
            case "boosters" or "precons" or "counter": GoTo(Place.Store); OnStore(test.ToLowerInvariant()); return true;
            case "opening" when State.Sealed.Count > 0: OpenBooster(State.Sealed.Keys.First()); return true;
        }
        if (Enum.TryParse<WebPage>(test, true, out var page)) OpenWeb(page, instant: true);
        return false;
    }

    // ---------------------------------------------------------------- places and the hud

    private void GoTo(Place place)
    {
        _place = place;
        CloseNote();
        if (_web is not null) CloseWeb(instant: true);
        _room.Visible = place == Place.Room;
        _store.Visible = place == Place.Store;
        (place == Place.Room ? _room : _store).GrabFocus();
        UpdateHud();
    }

    private void BuildHud()
    {
        _hud.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_hud);
        _hud.AddChild(BackTag(_back, Back));
        var money = CareerStyle.Tag(_money);
        money.SetAnchorsPreset(LayoutPreset.TopRight);
        money.GrowHorizontal = GrowDirection.Begin;
        money.OffsetRight = -20;
        money.OffsetTop = 20;
        _hud.AddChild(money);
    }

    private static PanelContainer BackTag(Label text, Action pressed)
    {
        var tag = CareerStyle.Tag(text);
        tag.Position = new Vector2(20, 20);
        tag.MouseFilter = MouseFilterEnum.Stop;
        tag.MouseDefaultCursorShape = CursorShape.PointingHand;
        tag.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) pressed();
        };
        return tag;
    }

    private void Back()
    {
        if (_note is not null) CloseNote();
        else if (_web is not null) CloseWeb();
        else if (_place == Place.Store) GoTo(Place.Room);
        else App.Instance.GoTo(App.MainMenuScene);
    }

    private void UpdateHud()
    {
        _back.Text = _note is not null || _web is not null ? "Esc  Close" : _place == Place.Store ? "Esc  Back home" : "Esc  Main menu";
        if (!Career.HasCareer)
        {
            _money.Text = World.Content.StoreName;
            return;
        }
        var now = Career.Now;
        _money.Text = $"{CareerRules.DayOf(now):ddd MMM d}   ·   {CareerRules.Money(State.Cash)}   ·   {CareerRules.DailiesLeft(State, World, now)} daily left";
    }

    private void Refresh()
    {
        UpdateHud();
        if (_web is not null) ShowWebPage();
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (CareerRuleException e) { MenuKit.Toast(this, e.Message); }
        Refresh();
    }

    private void OnRoom(string id)
    {
        if (!Career.HasCareer && id != "computer")
        {
            MenuKit.Toast(this, "Sign up on the store's website first: click the computer.");
            return;
        }
        switch (id)
        {
            case "computer": OpenWeb(_webPage); break;
            case "decks": OpenDeckBuilder(); break;
            case "collection": OpenWeb(WebPage.Cards); break;
        }
    }

    private static void OpenDeckBuilder()
    {
        DeckBuilder.CareerMode = true;
        App.Instance.GoTo(App.DeckBuilderScene);
    }

    // ---------------------------------------------------------------- the website

    private void OpenWeb(WebPage page, bool instant = false)
    {
        _webPage = page;
        _room.Interactive = false;
        if (_web is not null)
        {
            ShowWebPage();
            return;
        }
        _web = new Control { ZIndex = 5 };
        _web.Theme = CareerStyle.Web;
        AddChild(_web);
        MoveChild(_web, _room.GetIndex() + 1);
        _web.Visible = false;
        void Shown()
        {
            if (_web is null) return;
            _web.Visible = true;
            PlaceWeb();
            ShowWebPage();
        }
        // The view zooms into the screen, with a little margin of the monitor around it.
        var area = _screenArea.Grow(0.012f);
        _room.Zoom(area, instant ? 0.01 : 0.6, Shown);
        UpdateHud();
    }

    private void CloseWeb(bool instant = false)
    {
        _web?.QueueFree();
        _web = null;
        _room.Zoom(null, instant ? 0.01 : 0.5, () => _room.Interactive = _note is null);
        UpdateHud();
    }

    /// <summary>The website covers the computer's screen wherever the view puts it.</summary>
    private void PlaceWeb()
    {
        if (_web is null) return;
        var from = _room.ToScreen(_screenArea.Position);
        var to = _room.ToScreen(_screenArea.End);
        _web.Position = from;
        _web.Size = to - from;
    }

    private void ShowWebPage()
    {
        if (_web is null) return;
        foreach (var child in _web.GetChildren()) child.QueueFree();
        var window = new PanelContainer { AnchorRight = 1, AnchorBottom = 1 };
        window.AddThemeStyleboxOverride("panel", CareerStyle.Box(CareerStyle.Chrome, CareerStyle.InkDim, 2));
        _web.AddChild(window);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        window.AddChild(column);

        // Title bar.
        var bar = new PanelContainer();
        bar.AddThemeStyleboxOverride("panel", CareerStyle.Box(CareerStyle.TitleBar, null, 0, 0, 6));
        var barRow = new HBoxContainer();
        var url = World.Content.StoreName.ToLowerInvariant().Replace(" & ", "and").Replace(' ', '-').Replace("'", "");
        var title = CareerStyle.Text($"{World.Content.StoreName}  —  www.{url}.com", 20, Colors.White);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        barRow.AddChild(title);
        var close = CareerStyle.Button("X", () => CloseWeb(), 18);
        close.TooltipText = "Close (Esc)";
        barRow.AddChild(close);
        bar.AddChild(barRow);
        column.AddChild(bar);

        // The page.
        var page = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddThemeStyleboxOverride("panel", CareerStyle.Box(CareerStyle.Page, null, 0, 0, 18));
        column.AddChild(page);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        page.AddChild(body);

        var banner = CareerStyle.Text(World.Content.StoreName, 40, CareerStyle.TitleBar);
        body.AddChild(banner);
        if (!Career.HasCareer)
        {
            SignUpPage(body);
            return;
        }
        var nav = new HBoxContainer();
        nav.AddThemeConstantOverride("separation", 18);
        foreach (var (p, text) in new[] { (WebPage.Matches, "Daily matches"), (WebPage.Events, "Events"), (WebPage.Market, "Sell cards"), (WebPage.Cards, "My cards") })
        {
            var link = CareerStyle.LinkButton(text, p == _webPage);
            link.Pressed += () => { _webPage = p; ShowWebPage(); };
            nav.AddChild(link);
        }
        nav.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var visit = CareerStyle.LinkButton("Visit the store →");
        visit.Pressed += () => GoTo(Place.Store);
        nav.AddChild(visit);
        body.AddChild(nav);
        body.AddChild(new HSeparator());

        var content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 10);
        body.AddChild(content);
        switch (_webPage)
        {
            case WebPage.Events: EventsPage(content); break;
            case WebPage.Market: CollectionList(content, CareerStyle.Ink); break;
            case WebPage.Cards: CardsPage(content); break;
            default: MatchesPage(content); break;
        }
    }

    private void SignUpPage(VBoxContainer body)
    {
        body.AddChild(CareerStyle.Text("Join the store's league", 28, CareerStyle.Ink));
        if (Career.Problem is { } problem) body.AddChild(CareerStyle.Text(problem + " You can start a new career.", 20, CareerStyle.Bad, wrap: true));
        body.AddChild(CareerStyle.Text($"Members play {World.Content.Dailies.PerDay} free daily matches every day against the store's regulars, for cash or boosters. "
            + $"New members get {CareerRules.Money(World.Content.Starter.Cash)} and {World.Content.Starter.Boosters} boosters of this week's featured set "
            + $"({World.SetName(World.FeaturedSet(CareerRules.DayOf(DateTime.UtcNow)))}). Choose the deck you start with:", 20, CareerStyle.InkDim, wrap: true));
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 10);
        foreach (var deck in World.Content.Starter.Decks)
        {
            var title = DeckTitle(deck);
            var button = CareerStyle.Button(title, () => MenuKit.Modal(this, title, $"Start a career with this deck?\n{DeckSummary(World.Deck(deck))}", null,
                ("Cancel", () => { }), ("Start", () => Try(() =>
                {
                    Career.Start(deck, title);
                    _webPage = WebPage.Cards;
                    MenuKit.Toast(this, "Welcome! Your boosters are waiting on the “My cards” page.");
                }))), 18);
            button.TooltipText = DeckSummary(World.Deck(deck));
            flow.AddChild(button);
        }
        body.AddChild(Scrolled(flow));
    }

    private void MatchesPage(VBoxContainer page)
    {
        var now = Career.Now;
        var day = CareerRules.DayOf(now);
        var featured = World.FeaturedSet(day);
        int left = CareerRules.DailiesLeft(State, World, now);
        page.AddChild(CareerStyle.Text($"{day:dddd, MMMM d}  ·  {left} of {World.Content.Dailies.PerDay} daily matches left  ·  featured set: {World.SetName(featured)}", 20, CareerStyle.InkDim));
        if (left == 0)
        {
            var tomorrow = DateTime.SpecifyKind(day.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) - now;
            page.AddChild(CareerStyle.Text($"That's all for today. New daily matches in {(int)tomorrow.TotalHours} h {tomorrow.Minutes} min (midnight UTC).", 24, CareerStyle.Ink, wrap: true));
            Record(page);
            return;
        }
        var (opponent, deck) = CareerRules.NextDailyOpponent(State, World, now);
        page.AddChild(CareerStyle.Text($"Today you play {opponent.Name}", 30, CareerStyle.Ink));
        page.AddChild(CareerStyle.Text($"{opponent.Style}  Plays {DeckColors(World.Deck(deck))}.", 20, CareerStyle.InkDim, wrap: true));

        var decks = Career.Decks.List(null);
        if (decks.Count == 0)
        {
            page.AddChild(CareerStyle.Text("You have no decks: build one with the boxes of cards on your shelf.", 22, CareerStyle.Bad, wrap: true));
            return;
        }
        _deckIndex = Math.Clamp(_deckIndex, 0, decks.Count - 1);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(CareerStyle.Text("Your deck:", 22));
        var picker = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(360, 0) };
        foreach (var d in decks) picker.AddItem(d.Name);
        picker.Selected = _deckIndex;
        picker.ItemSelected += i => { _deckIndex = (int)i; ShowWebPage(); };
        row.AddChild(picker);
        page.AddChild(row);
        var (chosen, _) = Career.Decks.Load(decks[_deckIndex]);
        var problems = DeckProblems(chosen);
        foreach (var problem in problems.Take(3)) page.AddChild(CareerStyle.Text(problem, 18, CareerStyle.Bad, wrap: true));

        var dailies = World.Content.Dailies;
        var prizes = new HBoxContainer();
        prizes.AddThemeConstantOverride("separation", 12);
        prizes.AddChild(CareerStyle.Text("Prize for a win:", 22));
        foreach (var (reward, text) in new[] { (CareerRules.CashReward, CareerRules.Money(dailies.Win)), (CareerRules.BoosterReward, $"{dailies.WinBoosters} {World.SetName(featured)} booster{(dailies.WinBoosters == 1 ? "" : "s")}") })
        {
            var b = CareerStyle.Button((_reward == reward ? "◉ " : "○ ") + text, () => { _reward = reward; ShowWebPage(); }, 20);
            prizes.AddChild(b);
        }
        page.AddChild(prizes);
        page.AddChild(CareerStyle.Text($"A loss pays {CareerRules.Money(dailies.Loss)}. Leaving a match before it ends counts as a loss.", 18, CareerStyle.InkDim));
        var play = CareerStyle.Button($"Play against {opponent.Name}", () =>
        {
            try
            {
                Career.Do(new StartDaily(Career.Now, _reward));
                App.Instance.PendingMatch = Career.DailyMatch(chosen, Settings.PlayerName);
                OpenOn = nameof(WebPage.Matches); // back from the match, to the website's matches page
                App.Instance.GoTo(App.GameBoardScene);
            }
            catch (CareerRuleException e)
            {
                MenuKit.Toast(this, e.Message);
                Refresh();
            }
        }, 26, primary: true);
        play.Disabled = problems.Count > 0;
        play.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        page.AddChild(play);
        Record(page);
    }

    private static void Record(VBoxContainer page)
    {
        page.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        page.AddChild(CareerStyle.Text($"Your record: {State.Wins} wins, {State.Losses} losses  ·  reputation {State.Reputation}", 18, CareerStyle.InkDim));
    }

    private static void EventsPage(VBoxContainer page)
    {
        page.AddChild(CareerStyle.Text("Events calendar", 28));
        page.AddChild(CareerStyle.Text("Coming soon to the store: draft nights, commander nights, the Saturday tournament and the monthly championship. "
            + "Keep playing your daily matches and building your collection!", 20, CareerStyle.InkDim, wrap: true));
    }

    private void CardsPage(VBoxContainer page)
    {
        page.AddChild(CareerStyle.Text($"{State.CollectionSize} cards in your collection", 26));
        page.AddChild(CareerStyle.Text("Unopened boosters", 24, CareerStyle.TitleBar));
        if (State.Sealed.Count == 0) page.AddChild(CareerStyle.Text("None. Win daily matches or buy some at the store.", 20, CareerStyle.InkDim));
        foreach (var (set, count) in State.Sealed)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = CareerStyle.Text($"{count} × {World.SetName(set)}", 22);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(CareerStyle.Button("Open one", () => OpenBooster(set), 20, primary: true));
            page.AddChild(row);
        }
        page.AddChild(CareerStyle.Text("Latest news", 24, CareerStyle.TitleBar));
        var notes = new VBoxContainer();
        foreach (var note in Enumerable.Reverse(State.Notes))
            notes.AddChild(CareerStyle.Text($"{note.Utc.ToLocalTime():MMM d, HH:mm}  {note.Text}", 18, CareerStyle.InkDim, wrap: true));
        page.AddChild(Scrolled(notes));
        var end = CareerStyle.Button("End this career", () => MenuKit.Modal(this, "End this career?", "Your cards, cash and career decks are gone for good (the save is kept as a backup file).", null,
            ("Keep playing", () => { }), ("End it", () => { Career.Abandon(); Refresh(); })), 16);
        end.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        page.AddChild(end);
    }

    /// <summary>Why a deck can't be played in a daily match: cards not owned, or not a legal casual deck.</summary>
    private static List<string> DeckProblems(DeckList deck)
    {
        var problems = Career.Missing(deck).Select(m => $"You have {m.Owned} of the {m.Needed} {m.Name} this deck uses.").ToList();
        problems.AddRange(DeckValidator.Validate(deck, FormatRules.Casual, Cards).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
        return problems;
    }

    // ---------------------------------------------------------------- the store

    private void OnStore(string id)
    {
        switch (id)
        {
            case "boosters": OpenNote("Boosters", BoostersNote); break;
            case "precons": OpenNote("Preconstructed decks", PreconsNote); break;
            case "counter": OpenNote("The counter buys cards", box => CollectionList(box, CareerStyle.Ink)); break;
        }
    }

    private Action<VBoxContainer>? _noteContent;
    private string _noteTitle = "";

    /// <summary>A note over the store's picture (the picture dims and stops reacting until it closes).</summary>
    private void OpenNote(string title, Action<VBoxContainer> content)
    {
        _noteTitle = title;
        _noteContent = content;
        _store.Interactive = false;
        _note?.QueueFree();
        _note = new Control { ZIndex = 20, MouseFilter = MouseFilterEnum.Stop };
        _note.SetAnchorsPreset(LayoutPreset.FullRect);
        _note.Theme = CareerStyle.Notes;
        _note.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        AddChild(_note);
        MoveChild(_note, _hud.GetIndex());
        DrawNote();
        UpdateHud();
    }

    private void DrawNote()
    {
        if (_note is null || _noteContent is null) return;
        while (_note.GetChildCount() > 1) { var c = _note.GetChild(1); _note.RemoveChild(c); c.QueueFree(); }
        var paper = new PanelContainer { AnchorLeft = 0.2f, AnchorRight = 0.8f, AnchorTop = 0.1f, AnchorBottom = 0.92f };
        paper.AddThemeStyleboxOverride("panel", CareerStyle.Box(CareerStyle.Paper, CareerStyle.PaperEdge, 4, 6, 24));
        _note.AddChild(paper);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        paper.AddChild(box);
        var head = new HBoxContainer();
        var title = CareerStyle.Text(_noteTitle, 34, CareerStyle.PaperEdge);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        head.AddChild(CareerStyle.Button("X", CloseNote, 20));
        box.AddChild(head);
        _noteContent(box);
    }

    private void CloseNote()
    {
        _note?.QueueFree();
        _note = null;
        _noteContent = null;
        _store.Interactive = true;
        UpdateHud();
    }

    private void RefreshNote()
    {
        DrawNote();
        Refresh();
    }

    private void BoostersNote(VBoxContainer box)
    {
        var featured = World.FeaturedSet(CareerRules.DayOf(Career.Now));
        box.AddChild(CareerStyle.Text($"{CareerRules.Money(World.Content.BoosterPrice)} each. You have {CareerRules.Money(State.Cash)}.", 22, CareerStyle.InkDim));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 8);
        foreach (var set in World.Content.Sets.Where(World.SellsBoosters))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = CareerStyle.Text(World.SetName(set) + (set.Equals(featured, StringComparison.OrdinalIgnoreCase) ? "  ★" : ""), 22);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            int owned = State.Sealed.GetValueOrDefault(set.ToLowerInvariant());
            if (owned > 0) row.AddChild(CareerStyle.Text($"{owned} unopened", 18, CareerStyle.InkDim));
            var buy = CareerStyle.Button("Buy", () => { Try(() => Career.Do(new BuyBooster(Career.Now, set))); RefreshNote(); }, 20, primary: true);
            buy.Disabled = State.Cash < World.Content.BoosterPrice;
            row.AddChild(buy);
            var open = CareerStyle.Button("Open", () => OpenBooster(set), 20);
            open.Disabled = owned == 0;
            row.AddChild(open);
            list.AddChild(row);
        }
        box.AddChild(Scrolled(list));
    }

    private void PreconsNote(VBoxContainer box)
    {
        box.AddChild(CareerStyle.Text($"The cards go into your collection and the deck into your decks. You have {CareerRules.Money(State.Cash)}.", 20, CareerStyle.InkDim, wrap: true));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 8);
        foreach (var precon in World.Content.Precons)
        {
            var deck = World.Deck(precon.Deck);
            int price = World.PreconPrice(precon);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = CareerStyle.Text(DeckTitle(precon.Deck), 20);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.TooltipText = DeckSummary(deck);
            name.MouseFilter = MouseFilterEnum.Pass;
            row.AddChild(name);
            var buy = CareerStyle.Button($"Buy · {CareerRules.Money(price)}", () =>
            {
                Try(() =>
                {
                    Career.Do(new BuyPrecon(Career.Now, precon.Deck));
                    Career.Decks.Save(UniqueDeckName(DeckTitle(precon.Deck)), "casual", deck);
                    MenuKit.Toast(this, "Added to your collection and your decks");
                });
                RefreshNote();
            }, 18, primary: true);
            buy.Disabled = State.Cash < price;
            row.AddChild(buy);
            list.AddChild(row);
        }
        box.AddChild(Scrolled(list));
    }

    private static string UniqueDeckName(string name)
    {
        var taken = Career.Decks.List(null).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // ---------------------------------------------------------------- selling cards (website and counter)

    private void CollectionList(VBoxContainer box, Color ink)
    {
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        var search = new LineEdit { Text = _filter, PlaceholderText = "Filter by name…", CustomMinimumSize = new Vector2(300, 0) };
        search.TextSubmitted += text => { _filter = text; Redraw(); };
        top.AddChild(search);
        top.AddChild(CareerStyle.Button("Filter", () => { _filter = search.Text; Redraw(); }, 18));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var extras = Extras();
        var sellExtras = CareerStyle.Button($"Sell extras ({extras.Sum(e => e.Count)}) · {CareerRules.Money(extras.Sum(e => World.SellPrice(e.Card) * e.Count))}", () =>
            MenuKit.Modal(this, "Sell extras?", $"Sell every copy beyond the fourth of each card: {extras.Sum(e => e.Count)} cards for {CareerRules.Money(extras.Sum(e => World.SellPrice(e.Card) * e.Count))}?", null,
                ("Cancel", () => { }), ("Sell", () => { Try(() => { foreach (var (card, count) in Extras()) Career.Do(new SellCard(Career.Now, card.ToString(), count)); }); Redraw(); })), 18);
        sellExtras.Disabled = extras.Count == 0;
        top.AddChild(sellExtras);
        box.AddChild(top);

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var cards = State.Cards
            .Where(c => _filter.Length == 0 || c.Card.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => World.Value(c.Card)).ThenBy(c => c.Card.Name).ToList();
        foreach (var (card, count) in cards.Take(300))
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop };
            row.AddThemeConstantOverride("separation", 12);
            var name = CareerStyle.Text($"{count} × {card.Name}", 19, ink);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.ClipText = true;
            row.AddChild(name);
            row.AddChild(CareerStyle.Text(card.Set.Length > 0 ? $"{card.Set} #{card.Number}" : "", 16, CareerStyle.InkDim));
            int price = World.SellPrice(card);
            var sell = CareerStyle.Button(price > 0 ? $"Sell · {CareerRules.Money(price)}" : "Not bought", () => { Try(() => Career.Do(new SellCard(Career.Now, card.ToString(), 1))); Redraw(); }, 16);
            sell.CustomMinimumSize = new Vector2(150, 0);
            sell.Disabled = price == 0;
            row.AddChild(sell);
            var def = DefinitionOf(card);
            row.MouseEntered += () => ShowPreview(def, row);
            row.MouseExited += () => _preview.Visible = false;
            list.AddChild(row);
        }
        if (cards.Count > 300) list.AddChild(CareerStyle.Text($"…and {cards.Count - 300} more: filter by name to find them.", 18, CareerStyle.InkDim));
        box.AddChild(Scrolled(list));
    }

    /// <summary>Redraws whichever panel lists the collection (the website or a note).</summary>
    private void Redraw()
    {
        _preview.Visible = false;
        if (_note is not null) DrawNote();
        Refresh();
    }

    /// <summary>Copies beyond the fourth of each card (by name, keeping the most valuable printings); basic lands left out.</summary>
    private static List<(CardKey Card, int Count)> Extras()
    {
        var extras = new List<(CardKey, int)>();
        foreach (var group in State.Cards.GroupBy(c => c.Card.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (World.IsBasicLand(group.Key) || group.All(c => World.SellPrice(c.Card) == 0)) continue;
            int keep = 4;
            foreach (var (card, count) in group.OrderByDescending(c => World.Value(c.Card)))
            {
                int kept = Math.Min(keep, count);
                keep -= kept;
                if (count > kept) extras.Add((card, count - kept));
            }
        }
        return extras;
    }

    // ---------------------------------------------------------------- opening boosters

    private void OpenBooster(string set)
    {
        List<PoolCard> cards;
        try { cards = Career.Do(new OpenBooster(Career.Now, set)).Cards.ToList(); }
        catch (CareerRuleException e)
        {
            MenuKit.Toast(this, e.Message);
            return;
        }
        Refresh();
        if (_note is not null) DrawNote();

        var overlay = new Control { ZIndex = 60, MouseFilter = MouseFilterEnum.Stop, Theme = CareerStyle.Notes };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.8f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var box = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 40, OffsetTop = 40, OffsetRight = -40, OffsetBottom = -30 };
        box.AddThemeConstantOverride("separation", 16);
        overlay.AddChild(box);
        box.AddChild(CareerStyle.Text($"{World.SetName(set)} booster", 34, CareerStyle.Gold));
        var flow = new HFlowContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 10);
        var nodes = new List<CardNode>();
        foreach (var card in cards)
        {
            var key = CardKey.Of(card);
            var holder = new Control { CustomMinimumSize = TileSize };
            var node = new CardNode { Size = TileSize };
            holder.AddChild(node);
            node.Setup(ViewOf(DefinitionOf(key)), showCostPips: false);
            node.HoverStarted += _ => ShowPreview(DefinitionOf(key), holder);
            node.HoverEnded += _ => _preview.Visible = false;
            node.TooltipText = $"{card.Rarity} · sells for {CareerRules.Money(World.SellPrice(key))}";
            flow.AddChild(holder);
            nodes.Add(node);
        }
        box.AddChild(Scrolled(flow));
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        int left = State.Sealed.GetValueOrDefault(set);
        var again = CareerStyle.Button($"Open another ({left} left)", () => { overlay.QueueFree(); _preview.Visible = false; OpenBooster(set); }, 22, primary: true);
        again.Disabled = left == 0;
        actions.AddChild(again);
        actions.AddChild(CareerStyle.Button("Done", () => { overlay.QueueFree(); _preview.Visible = false; }, 22));
        box.AddChild(actions);
        AddChild(overlay);
        MoveChild(_preview, GetChildCount() - 1);

        // The booster opens in 3D first, then the cards turn over.
        if (BoosterOpening.Available && Settings.Current.BoosterOpening != "skip" && World.Sets.TryGetValue(set, out var definition))
        {
            foreach (var node in nodes) node.Modulate = Colors.Transparent;
            var opening = new BoosterOpening(definition.PackImage, definition.PackImageSeals, definition.Name, automatic: Settings.Current.BoosterOpening == "auto");
            opening.Opened += _ => Reveal(nodes);
            overlay.AddChild(opening);
        }
    }

    private static void Reveal(List<CardNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (!IsInstanceValid(node)) continue;
            var tween = node.CreateTween();
            tween.TweenInterval(i * 0.06);
            tween.TweenProperty(node, "modulate", Colors.White, 0.25);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string OpponentName(string id) => World.Content.Opponents.FirstOrDefault(o => o.Id == id)?.Name ?? id;

    /// <summary>"fdn-br" → "Foundations (black-red)".</summary>
    private static string DeckTitle(string deck)
    {
        var parts = deck.Split('-', 2);
        return parts.Length == 2 && World.Sets.ContainsKey(parts[0]) ? $"{World.SetName(parts[0])} ({DeckColors(World.Deck(deck))})" : deck;
    }

    private static string DeckSummary(DeckList deck) =>
        $"{deck.Main.Sum(e => e.Count)} cards, {DeckColors(deck)}, worth {CareerRules.Money(deck.Main.Sum(e => World.Value(CardKey.Of(e)) * e.Count))}";

    private static string DeckColors(DeckList deck)
    {
        var colors = new HashSet<char>();
        foreach (var entry in deck.Main)
            if (Cards.Find(entry.Name) is { } card)
                foreach (char c in card.Definition.ManaCost.ToString())
                    if ("WUBRG".Contains(c)) colors.Add(c);
        var names = new[] { ('W', "white"), ('U', "blue"), ('B', "black"), ('R', "red"), ('G', "green") };
        var chosen = names.Where(n => colors.Contains(n.Item1)).Select(n => n.Item2).ToList();
        return chosen.Count == 0 ? "colorless" : string.Join("-", chosen);
    }

    private static CardDefinition DefinitionOf(CardKey card) =>
        Cards.TryGet(card.Name, card.Set.Length > 0 ? card.Set : null, card.Number.Length > 0 ? card.Number : null, out var d) ? d : Cards.Find(card.Name)!.Definition;

    private static CardView ViewOf(CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey,
    };

    private void ShowPreview(CardDefinition definition, Control anchor)
    {
        _preview.Setup(ViewOf(definition), false);
        var rect = anchor.GetGlobalRect();
        float x = rect.End.X + 16 + _preview.Size.X < GetViewportRect().Size.X ? rect.End.X + 16 : rect.Position.X - _preview.Size.X - 16;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 120, 20, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    private static ScrollContainer Scrolled(Control inner)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inner);
        return scroll;
    }
}
