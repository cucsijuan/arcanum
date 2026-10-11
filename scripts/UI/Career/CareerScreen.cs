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

    public enum WebPage { Matches, Practice, Challenges, Events, Market, Cards }

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
        Hotspot.Rect("offers", "Today's offers", 0.83f, 0.45f, 0.14f, 0.21f),
    };

    /// <summary>Set before coming back to this screen: what to show first ("store", or a website page).</summary>
    public static string? OpenOn { get; set; }

    private static bool _tested;

    private ArtScene _room = null!, _store = null!;
    private Place _place = Place.Room;
    private readonly Control _hud = new() { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 50 };
    private readonly Label _back = CareerStyle.Text("", 20, new Color(1f, 0.95f, 0.85f));
    private readonly Label _money = CareerStyle.Text("", 20, new Color(1f, 0.95f, 0.85f));
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };

    // The computer drawn over the dimmed room, the website on its screen, and a note over the store (when one is open).
    private Control? _monitor, _casing, _web;
    private Control? _note;
    private WebPage _webPage = WebPage.Matches;
    private string _reward = CareerRules.CashReward;
    private int _deckIndex, _commanderDeckIndex;
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
        _room.Activated += OnRoom;
        _room.BackRequested += () => { if (_web is not null) CloseWeb(); else App.Instance.GoTo(App.MainMenuScene); };
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
            Career.Do(new FinishMatch(Career.Now, Won: false));
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
    /// store, boosters, precons, counter, a website page (Matches, Practice, Challenges, Events, Market, Cards), opening
    /// (the first unopened booster), decks (the deck builder), daily or ante (plays one), enter (today's first event), event (the event entered), buy:&lt;deck&gt; (buys a preconstructed deck).
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
                var testDeck = Career.Decks.Load(first).Deck;
                Career.Do(new StartDaily(Career.Now, _reward, testDeck.Export()));
                App.Instance.PendingMatch = Career.Match(testDeck, Settings.PlayerName);
                App.Instance.GoTo(App.GameBoardScene);
                return true;
            case "ante" when Career.Decks.List(null) is [var anteDeck, ..]:
                var list = Career.Decks.Load(anteDeck).Deck;
                Career.Do(new StartAnte(Career.Now, CareerRules.Opponents(State, World)[0].Id, list.Export()));
                Play(list, WebPage.Practice);
                return true;
            case "event" when Career.CurrentEvent() is not null:
                App.Instance.GoTo(App.EventScene);
                return true;
            case "enter" when CareerRules.EventsOn(World, CareerRules.DayOf(Career.Now)) is [var today, ..]:
                Career.Enter(today, today.Kind == CareerRules.Draft ? CareerRules.PayBoosters : CareerRules.PayCash,
                    today.Kind == CareerRules.Draft ? null : Career.Decks.Load(Career.Decks.List(null)
                        .First(d => (d.FormatId == CareerRules.Commander) == (today.Kind == CareerRules.Commander))).Deck);
                App.Instance.GoTo(App.EventScene);
                return true;
            case var buy when buy.StartsWith("buy:"):
                var precon = World.Deck(test[4..]);
                Career.Do(new BuyPrecon(Career.Now, test[4..]));
                Career.Decks.Save(UniqueDeckName(DeckTitle(test[4..])), precon.Commander.Count > 0 ? CareerRules.Commander : "casual", precon);
                OpenWeb(WebPage.Events, instant: true);
                return true;
            case "store": GoTo(Place.Store); return true;
            case "boosters" or "precons" or "counter" or "offers": GoTo(Place.Store); OnStore(test.ToLowerInvariant()); return true;
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

    private static void OpenDeckBuilder() => App.Instance.GoTo(App.DeckDeskScene);

    // ---------------------------------------------------------------- the website

    private void OpenWeb(WebPage page, bool instant = false)
    {
        _webPage = page;
        _room.Interactive = false;
        if (_monitor is not null)
        {
            ShowWebPage();
            return;
        }
        // The computer is drawn (not the picture zoomed in, which would blur): it grows from where it stands in the room.
        _monitor = new Control { ZIndex = 5, MouseFilter = MouseFilterEnum.Stop, Theme = CareerStyle.Web };
        _monitor.SetAnchorsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        _monitor.AddChild(dim);
        _casing = Casing(out _web);
        _monitor.AddChild(_casing);
        AddChild(_monitor);
        MoveChild(_monitor, _room.GetIndex() + 1);
        _monitor.Resized += PlaceCasing;
        PlaceCasing();
        ShowWebPage();

        var final = new Rect2(_casing.Position, _casing.Size);
        var from = _room.Spot("computer") is { } spot ? _room.ScreenRect(spot) : new Rect2(final.GetCenter(), Vector2.One);
        float seconds = instant ? 0.01f : 0.45f;
        _casing.Position = from.Position;
        _casing.Scale = Vector2.One * Math.Max(0.05f, from.Size.X / final.Size.X);
        var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(_casing, "position", final.Position, seconds);
        tween.TweenProperty(_casing, "scale", Vector2.One, seconds);
        tween.TweenProperty(dim, "color:a", 0.7f, seconds);
        UpdateHud();
    }

    private void CloseWeb(bool instant = false)
    {
        var monitor = _monitor;
        var casing = _casing;
        _monitor = _casing = _web = null;
        UpdateHud();
        if (monitor is null || casing is null) return;
        var to = _room.Spot("computer") is { } spot ? _room.ScreenRect(spot) : new Rect2(casing.Position + casing.Size / 2, Vector2.One);
        var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        float seconds = instant ? 0.01f : 0.3f;
        tween.TweenProperty(casing, "position", to.Position, seconds);
        tween.TweenProperty(casing, "scale", Vector2.One * Math.Max(0.05f, to.Size.X / casing.Size.X), seconds);
        tween.TweenProperty(monitor, "modulate:a", 0f, seconds);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            monitor.QueueFree();
            _room.Interactive = _note is null && _monitor is null;
        }));
    }

    /// <summary>The computer as large as the window allows (an old monitor: a 4:3 screen in a beige casing with a chin).</summary>
    private void PlaceCasing()
    {
        if (_monitor is null || _casing is null) return;
        var room = _monitor.Size;
        const float aspect = 1.3f;
        var size = new Vector2(Math.Min(room.X * 0.92f, room.Y * 0.95f * aspect), 0);
        size.Y = size.X / aspect;
        _casing.Size = size;
        _casing.Position = (room - size) / 2;
    }

    private static Control Casing(out Control screen)
    {
        var casing = new Control { MouseFilter = MouseFilterEnum.Ignore };
        var shell = new Panel { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        var plastic = CareerStyle.Box(new Color("d9d1bd"), new Color("a99f87"), 4, 34);
        plastic.ShadowColor = new Color(0, 0, 0, 0.55f);
        plastic.ShadowSize = 30;
        plastic.ShadowOffset = new Vector2(0, 12);
        shell.AddThemeStyleboxOverride("panel", plastic);
        casing.AddChild(shell);

        // The screen sits in a dark recessed bezel; the chin below has the brand, a vent and the power light.
        var bezel = new PanelContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 42, OffsetTop = 40, OffsetRight = -42, OffsetBottom = -86 };
        var recess = CareerStyle.Box(new Color("1b1d22"), new Color("8e856f"), 6, 20, 14);
        bezel.AddThemeStyleboxOverride("panel", recess);
        casing.AddChild(bezel);
        screen = new Control { ClipContents = true };
        bezel.AddChild(screen);

        var brand = CareerStyle.Text("ARCANUM 486", 22, new Color("7d735e"));
        brand.SetAnchorsPreset(LayoutPreset.BottomLeft);
        brand.OffsetLeft = 52; brand.OffsetTop = -66; brand.OffsetBottom = -30;
        casing.AddChild(brand);
        for (int i = 0; i < 6; i++)
        {
            var slot = new ColorRect { Color = new Color("b7ad95"), MouseFilter = MouseFilterEnum.Ignore };
            slot.SetAnchorsPreset(LayoutPreset.CenterBottom);
            slot.OffsetLeft = -90 + i * 32; slot.OffsetRight = slot.OffsetLeft + 22; slot.OffsetTop = -54; slot.OffsetBottom = -48;
            casing.AddChild(slot);
        }
        var led = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var light = CareerStyle.Box(new Color("6dff8a"), null, 0, 7);
        light.ShadowColor = new Color(0.4f, 1f, 0.5f, 0.6f);
        light.ShadowSize = 8;
        led.AddThemeStyleboxOverride("panel", light);
        led.SetAnchorsPreset(LayoutPreset.BottomRight);
        led.OffsetLeft = -74; led.OffsetRight = -60; led.OffsetTop = -56; led.OffsetBottom = -42;
        casing.AddChild(led);
        return casing;
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
        foreach (var (p, text) in new[] { (WebPage.Matches, "Daily matches"), (WebPage.Practice, "Practice & ante"), (WebPage.Challenges, "Challenges"), (WebPage.Events, "Events"), (WebPage.Market, "Sell cards"), (WebPage.Cards, "My cards") })
        {
            var link = CareerStyle.LinkButton(text, p == _webPage);
            link.Pressed += () => { _webPage = p; ShowWebPage(); };
            nav.AddChild(link);
        }
        nav.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var visit = CareerStyle.LinkButton("Visit the store >>");
        visit.Pressed += () => GoTo(Place.Store);
        nav.AddChild(visit);
        body.AddChild(nav);
        body.AddChild(new HSeparator());

        var content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 10);
        body.AddChild(content);
        switch (_webPage)
        {
            case WebPage.Practice: PracticePage(content); break;
            case WebPage.Challenges: ChallengesPage(content); break;
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
        page.AddChild(CareerStyle.Text($"{opponent.Style}  Plays {DeckColors(CareerRules.OpponentDeck(State, World, opponent.Id, deck))}.{(opponent.Rival ? " A rival: their decks get better every week." : "")}", 20, CareerStyle.InkDim, wrap: true));

        if (DeckPicker(page) is not var (chosen, problems)) return;

        var dailies = World.Content.Dailies;
        var prizes = new HBoxContainer();
        prizes.AddThemeConstantOverride("separation", 12);
        prizes.AddChild(CareerStyle.Text("Prize for a win:", 22));
        foreach (var (reward, text) in new[] { (CareerRules.CashReward, CareerRules.Money(dailies.Win)), (CareerRules.BoosterReward, $"{dailies.WinBoosters} {World.SetName(featured)} booster{(dailies.WinBoosters == 1 ? "" : "s")}") })
        {
            var b = CareerStyle.Button((_reward == reward ? "(*) " : "( ) ") + text, () => { _reward = reward; ShowWebPage(); }, 20);
            prizes.AddChild(b);
        }
        page.AddChild(prizes);
        page.AddChild(CareerStyle.Text($"A loss pays {CareerRules.Money(dailies.Loss)}. Leaving a match before it ends counts as a loss.", 18, CareerStyle.InkDim));
        var play = CareerStyle.Button($"Play against {opponent.Name}", () =>
        {
            try
            {
                Career.Do(new StartDaily(Career.Now, _reward, chosen.Export()));
                Play(chosen, WebPage.Matches);
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
        page.AddChild(CareerStyle.Text($"Your record: {State.Wins} wins, {State.Losses} losses  ·  {TierText()}", 18, CareerStyle.InkDim));
    }

    /// <summary>"Regular (45 reputation, 55 to Local favorite)".</summary>
    private static string TierText()
    {
        var tiers = World.Content.Tiers;
        int tier = CareerRules.Tier(State, World);
        var next = tier + 1 < tiers.Count ? $", {tiers[tier + 1].Reputation - State.Reputation} to {tiers[tier + 1].Name}" : "";
        return $"{tiers[tier].Name} ({State.Reputation} reputation{next})";
    }

    /// <summary>The career deck chooser (shared by every kind of match) and what keeps the chosen deck from being played; null without decks.</summary>
    private (DeckList Deck, List<string> Problems)? DeckPicker(VBoxContainer page, bool commander = false)
    {
        var decks = Career.Decks.List(null).Where(d => (d.FormatId == CareerRules.Commander) == commander).ToList();
        if (decks.Count == 0)
        {
            page.AddChild(CareerStyle.Text(commander
                ? "You have no Commander decks: buy a Commander deck at the store or build one with the boxes of cards on your shelf."
                : "You have no decks: build one with the boxes of cards on your shelf.", 22, CareerStyle.Bad, wrap: true));
            return null;
        }
        ref int index = ref commander ? ref _commanderDeckIndex : ref _deckIndex;
        index = Math.Clamp(index, 0, decks.Count - 1);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(CareerStyle.Text(commander ? "Your Commander deck:" : "Your deck:", 22));
        var picker = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(360, 0) };
        foreach (var d in decks) picker.AddItem(d.Name);
        picker.Selected = index;
        picker.ItemSelected += i =>
        {
            if (commander) _commanderDeckIndex = (int)i;
            else _deckIndex = (int)i;
            ShowWebPage();
        };
        row.AddChild(picker);
        page.AddChild(row);
        var (chosen, _) = Career.Decks.Load(decks[index]);
        var problems = CareerRules.DeckProblems(chosen, State, World, commander);
        foreach (var problem in problems.Take(3)) page.AddChild(CareerStyle.Text(problem, 18, CareerStyle.Bad, wrap: true));
        return (chosen, problems);
    }

    /// <summary>Goes to the board for the match just started; afterwards the website opens on <paramref name="back"/>.</summary>
    private static void Play(DeckList deck, WebPage back)
    {
        App.Instance.PendingMatch = Career.Match(deck, Settings.PlayerName);
        OpenOn = back.ToString();
        App.Instance.GoTo(App.GameBoardScene);
    }

    private string _practiceOpponent = "", _anteOpponent = "";

    private void PracticePage(VBoxContainer page)
    {
        var now = Career.Now;
        if (DeckPicker(page) is not var (chosen, problems)) return;
        var opponents = CareerRules.Opponents(State, World);

        // Practice.
        var practice = World.Content.Practice;
        int paid = CareerRules.PaidPracticeLeft(State, World, now);
        page.AddChild(CareerStyle.Text("Practice", 28, CareerStyle.TitleBar));
        page.AddChild(CareerStyle.Text(paid > 0
            ? $"Play any regular you know. {paid} more practice match{(paid == 1 ? "" : "es")} pay today ({CareerRules.Money(practice.Win)} for a win, {CareerRules.Money(practice.Loss)} for a loss)."
            : "Play any regular you know. Practice doesn't pay any more today, but it's still practice.", 19, CareerStyle.InkDim, wrap: true));
        page.AddChild(OpponentRow(opponents, _practiceOpponent, id => _practiceOpponent = id, "Practice", problems.Count > 0, id =>
        {
            Career.Do(new StartPractice(Career.Now, id, chosen.Export()));
            Play(chosen, WebPage.Practice);
        }));

        // Ante.
        int antes = CareerRules.AntesLeft(State, World, now);
        page.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        page.AddChild(CareerStyle.Text("Ante match", 28, CareerStyle.TitleBar));
        page.AddChild(CareerStyle.Text("Playing for keeps: each of you antes a card at random from your deck, and the winner keeps both. "
            + $"The anted cards stay out of the game. {antes} ante match{(antes == 1 ? "" : "es")} left today.", 19, CareerStyle.InkDim, wrap: true));
        page.AddChild(OpponentRow(opponents, _anteOpponent, id => _anteOpponent = id, "Play for keeps", problems.Count > 0 || antes == 0, id =>
        {
            Career.Do(new StartAnte(Career.Now, id, chosen.Export()));
            var match = State.Pending!;
            var mine = CardKey.Parse(match.PlayerAnte);
            var theirs = CardKey.Parse(match.OpponentAnte);
            var cards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            cards.AddThemeConstantOverride("separation", 30);
            foreach (var (who, card) in new[] { ("You ante", mine), ($"{OpponentName(match.OpponentId)} antes", theirs) })
            {
                var column = new VBoxContainer();
                column.AddChild(CareerStyle.Text(who, 20, CareerStyle.Gold));
                var node = new CardNode { Size = new Vector2(200, 279), CustomMinimumSize = new Vector2(200, 279) };
                node.Setup(ViewOf(DefinitionOf(card)), showCostPips: false);
                column.AddChild(node);
                cards.AddChild(column);
            }
            MenuKit.Modal(this, "The antes", "The winner keeps both cards.", cards, ("Play", () => Play(chosen, WebPage.Practice)));
        }));
        Record(page);
    }

    /// <summary>A regular to play (a chooser) and the button that starts the match.</summary>
    private Control OpponentRow(IReadOnlyList<OpponentSpec> opponents, string chosen, Action<string> choose, string action, bool disabled, Action<string> start)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var picker = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(300, 0) };
        int selected = 0;
        for (int i = 0; i < opponents.Count; i++)
        {
            picker.AddItem($"{opponents[i].Name} · {World.Content.Tiers[Math.Min(opponents[i].Tier, World.Content.Tiers.Count - 1)].Name}");
            if (opponents[i].Id == chosen) selected = i;
        }
        picker.Selected = selected;
        picker.ItemSelected += i => { choose(opponents[(int)i].Id); ShowWebPage(); };
        row.AddChild(picker);
        var opponent = opponents[selected];
        var style = CareerStyle.Text(opponent.Style, 17, CareerStyle.InkDim, wrap: true);
        style.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(style);
        var button = CareerStyle.Button(action, () =>
        {
            try { start(opponent.Id); }
            catch (CareerRuleException e)
            {
                MenuKit.Toast(this, e.Message);
                Refresh();
            }
        }, 22, primary: true);
        button.Disabled = disabled;
        row.AddChild(button);
        return row;
    }

    private static void ChallengesPage(VBoxContainer page)
    {
        var day = CareerRules.DayOf(Career.Now);
        var monday = DateOnly.FromDayNumber(CareerRules.WeekOf(day) * 7);
        page.AddChild(CareerStyle.Text($"This week's challenges (until {monday.AddDays(6):dddd, MMMM d})", 26));
        foreach (var challenge in CareerRules.WeekChallenges(State, World, day))
        {
            int progress = Math.Min(challenge.Count, CareerRules.Progress(State, challenge, day));
            bool done = progress >= challenge.Count;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 14);
            var text = CareerStyle.Text((done ? "Done: " : "") + challenge.Text, 22, done ? CareerStyle.Good : CareerStyle.Ink);
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(text);
            var prize = string.Join(" + ", new[]
            {
                challenge.Cash > 0 ? CareerRules.Money(challenge.Cash) : null,
                challenge.Boosters > 0 ? $"{challenge.Boosters} booster{(challenge.Boosters == 1 ? "" : "s")}" : null,
            }.OfType<string>());
            row.AddChild(CareerStyle.Text(prize, 18, CareerStyle.InkDim));
            var bar = new ProgressBar { MaxValue = challenge.Count, Value = progress, ShowPercentage = false, CustomMinimumSize = new Vector2(180, 18), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            bar.AddThemeStyleboxOverride("background", CareerStyle.Box(new Color("ddd6c8"), CareerStyle.InkDim, 1));
            bar.AddThemeStyleboxOverride("fill", CareerStyle.Box(CareerStyle.Good));
            row.AddChild(bar);
            row.AddChild(CareerStyle.Text($"{progress}/{challenge.Count}", 18, CareerStyle.InkDim));
            page.AddChild(row);
        }
        page.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        page.AddChild(CareerStyle.Text("Reputation", 26));
        page.AddChild(CareerStyle.Text($"You are a {TierText()}. Reputation comes from matches and challenges; each tier lets you meet tougher regulars.", 19, CareerStyle.InkDim, wrap: true));
        foreach (var (tier, i) in World.Content.Tiers.Select((t, i) => (t, i)))
        {
            var names = World.Content.Opponents.Where(o => o.Tier == i).Select(o => o.Name).ToList();
            bool reached = State.Reputation >= tier.Reputation;
            page.AddChild(CareerStyle.Text($"{(reached ? "[x]" : "[ ]")} {tier.Name} ({tier.Reputation}){(names.Count > 0 ? ": " + string.Join(", ", names) : "")}", 18, reached ? CareerStyle.Ink : CareerStyle.InkDim, wrap: true));
        }
    }

    /// <summary>The calendar: the event the player is in, today's events with their entries, and what comes next.</summary>
    private void EventsPage(VBoxContainer page)
    {
        var now = Career.Now;
        var today = CareerRules.DayOf(now);
        if (Career.CurrentEvent() is var (current, entry, _))
        {
            page.AddChild(CareerStyle.Text($"You are playing {current.Name} ({entry.Day:dddd})", 28, CareerStyle.TitleBar));
            page.AddChild(CareerStyle.Button("Go to the event", () => App.Instance.GoTo(App.EventScene), 24, primary: true));
            return;
        }

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 10);
        var todays = CareerRules.EventsOn(World, today);
        list.AddChild(CareerStyle.Text(todays.Count == 0 ? $"No event today ({today:dddd})." : $"Today, {today:dddd}", 28, CareerStyle.TitleBar));
        (DeckList Deck, List<string> Problems)? deck = null, commanderDeck = null;
        if (todays.Any(e => e.Kind == CareerRules.Constructed)) deck = DeckPicker(list);
        if (todays.Any(e => e.Kind == CareerRules.Commander)) commanderDeck = DeckPicker(list, commander: true);
        foreach (var e in todays)
        {
            var chosen = e.Kind == CareerRules.Commander ? commanderDeck : deck;
            EventRow(list, e, today, chosen?.Deck, chosen?.Problems ?? new List<string>());
        }

        list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        list.AddChild(CareerStyle.Text("Coming up", 26, CareerStyle.TitleBar));
        foreach (var e in World.Content.Events.OrderBy(e => CareerRules.NextDay(e, today.AddDays(1))))
        {
            var day = CareerRules.NextDay(e, today.AddDays(1));
            list.AddChild(CareerStyle.Text($"{day:dddd, MMMM d}: {e.Name}. {Describe(e, day)}", 18, CareerStyle.InkDim, wrap: true));
        }
        list.AddChild(CareerStyle.Text($"Qualification tokens: {State.Tokens}", 18, CareerStyle.InkDim));
        page.AddChild(Scrolled(list));
    }

    private static string Describe(EventSpec e, DateOnly day)
    {
        var kind = e.Kind switch
        {
            CareerRules.Draft => $"Draft of {World.SetName(CareerRules.EventSet(World, e, day))}",
            CareerRules.Commander => "Commander, bring your Commander deck; pods of",
            _ => "Constructed, bring your deck",
        };
        if (e.Kind == CareerRules.Commander) return $"{kind} {e.Players}, {e.Rounds} games. Entry: {CareerRules.Money(e.Entry)}.";
        var entry = new List<string> { CareerRules.Money(e.Entry) };
        if (e.EntryBoosters > 0) entry.Add($"{e.EntryBoosters} boosters of the set");
        if (e.EntryTokens > 0) entry.Add($"{e.EntryTokens} qualification token{(e.EntryTokens == 1 ? "" : "s")}");
        var tier = e.MinTier > 0 ? $" For {World.Content.Tiers[Math.Min(e.MinTier, World.Content.Tiers.Count - 1)].Name}s and up." : "";
        return $"{kind}, {e.Players} players, {e.Rounds} rounds{(e.BestOf > 1 ? $" (best of {e.BestOf})" : "")}. Entry: {string.Join(" or ", entry)}.{tier}";
    }

    private void EventRow(VBoxContainer list, EventSpec e, DateOnly day, DeckList? deck, List<string> problems)
    {
        list.AddChild(CareerStyle.Text(e.Name, 26));
        list.AddChild(CareerStyle.Text(Describe(e, day), 19, CareerStyle.InkDim, wrap: true));
        var prizes = e.Prizes.Select(p => (p.Wins >= 0 ? $"{p.Wins} wins" : p.Place switch { 0 => "everyone else", 1 => "1st", _ => $"top {p.Place}" }) + ": "
            + string.Join(", ", new[]
            {
                p.Cash > 0 ? CareerRules.Money(p.Cash) : null, p.Boosters > 0 ? $"{p.Boosters} boosters" : null,
                p.Tokens > 0 ? $"{p.Tokens} token{(p.Tokens == 1 ? "" : "s")}" : null, p.Reputation > 0 ? $"{p.Reputation} reputation" : null,
            }.OfType<string>()));
        list.AddChild(CareerStyle.Text("Prizes: " + string.Join(" · ", prizes), 17, CareerStyle.InkDim, wrap: true));
        if (CareerRules.Played(State, e, day))
        {
            list.AddChild(CareerStyle.Text("You played it today.", 19, CareerStyle.Good));
            return;
        }
        if (CareerRules.Tier(State, World) < e.MinTier)
        {
            list.AddChild(CareerStyle.Text("Your reputation isn't high enough yet.", 19, CareerStyle.Bad));
            return;
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        void Way(string pay, string text, bool can)
        {
            var b = CareerStyle.Button(text, () =>
            {
                try
                {
                    Career.Enter(e, pay, e.Kind is CareerRules.Constructed or CareerRules.Commander ? deck : null);
                    App.Instance.GoTo(App.EventScene);
                }
                catch (CareerRuleException x)
                {
                    MenuKit.Toast(this, x.Message);
                    Refresh();
                }
            }, 20, primary: true);
            b.Disabled = !can || (e.Kind is CareerRules.Constructed or CareerRules.Commander && (deck is null || problems.Count > 0));
            row.AddChild(b);
        }
        Way(CareerRules.PayCash, $"Enter · {CareerRules.Money(e.Entry)}", State.Cash >= e.Entry);
        if (e.Kind == CareerRules.Draft && e.EntryBoosters > 0)
        {
            var set = CareerRules.EventSet(World, e, day);
            Way(CareerRules.PayBoosters, $"Enter · {e.EntryBoosters} boosters", State.Sealed.GetValueOrDefault(set) >= e.EntryBoosters);
        }
        if (e.EntryTokens > 0) Way(CareerRules.PayTokens, $"Enter · {e.EntryTokens} token{(e.EntryTokens == 1 ? "" : "s")}", State.Tokens >= e.EntryTokens);
        list.AddChild(row);
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

    // ---------------------------------------------------------------- the store

    private void OnStore(string id)
    {
        switch (id)
        {
            case "boosters": OpenNote("Boosters", BoostersNote); break;
            case "precons": OpenNote("Preconstructed decks", PreconsNote); break;
            case "counter": OpenNote("The counter buys cards", box => CollectionList(box, CareerStyle.Ink)); break;
            case "offers": OpenNote("Today's offers", OffersNote); break;
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
            var name = CareerStyle.Text(World.SetName(set) + (set.Equals(featured, StringComparison.OrdinalIgnoreCase) ? "  (featured)" : ""), 22);
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

    /// <summary>The regulars' offers of the day: a bargain and a trade, each with its cards.</summary>
    private void OffersNote(VBoxContainer box)
    {
        var day = State.Day < CareerRules.DayOf(Career.Now) ? CareerRules.DayOf(Career.Now) : State.Day;
        var offers = CareerRules.Offers(State, World, day);
        box.AddChild(CareerStyle.Text("The regulars' offers change every day. Hover a card to see it.", 20, CareerStyle.InkDim, wrap: true));
        if (offers.Count == 0) box.AddChild(CareerStyle.Text("Nothing today.", 22));
        foreach (var offer in offers)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 16);
            var who = OpponentName(offer.OpponentId);
            var get = CardKey.Parse(offer.Get);
            CardKey? give = offer.Give.Length > 0 ? CardKey.Parse(offer.Give) : null;
            row.AddChild(SmallCard(get));
            var text = offer.Kind == CareerRules.Bargain
                ? $"{who} needs cash and sells {get.Name} for {CareerRules.Money(offer.Price)} (it's worth {CareerRules.Money(World.Value(get))})."
                : $"{who} offers {get.Name} for your {give!.Value.Name}. You have {State.OwnedByName(give.Value.Name)}.";
            var label = CareerStyle.Text(text, 20, CareerStyle.Ink, wrap: true);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            if (give is { } asked) row.AddChild(SmallCard(asked));
            bool taken = CareerRules.OfferTaken(State, day, offer.Index);
            var take = CareerStyle.Button(taken ? "Taken" : offer.Kind == CareerRules.Bargain ? "Buy" : "Trade", () =>
            {
                Try(() =>
                {
                    Career.Do(new TakeOffer(Career.Now, offer.Index));
                    MenuKit.Toast(this, $"{get.Name} is yours.");
                });
                RefreshNote();
            }, 20, primary: true);
            take.Disabled = taken || (offer.Kind == CareerRules.Bargain ? State.Cash < offer.Price : State.OwnedByName(give!.Value.Name) == 0);
            take.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(take);
            box.AddChild(row);
        }
    }

    /// <summary>A card small enough for a row, its preview on hover.</summary>
    private Control SmallCard(CardKey key)
    {
        var size = new Vector2(110, 153);
        var holder = new Control { CustomMinimumSize = size };
        var node = new CardNode { Size = size };
        node.Setup(ViewOf(DefinitionOf(key)), showCostPips: false);
        node.HoverStarted += _ => ShowPreview(DefinitionOf(key), holder);
        node.HoverEnded += _ => _preview.Visible = false;
        holder.AddChild(node);
        return holder;
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
                    Career.Decks.Save(UniqueDeckName(DeckTitle(precon.Deck)), deck.Commander.Count > 0 ? CareerRules.Commander : "casual", deck);
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
            var name = CareerStyle.Text($"{count} × {card.Name}{(card.Foil ? " (foil)" : "")}", 19, ink);
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
        Cards.TryGet(card.Name, card.Set.Length > 0 ? card.Set : null, card.Number.Length > 0 ? card.Number : null, card.Foil, out var d) ? d : Cards.Find(card.Name)!.Definition;

    private static CardView ViewOf(CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey, Foil = d.Foil,
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
