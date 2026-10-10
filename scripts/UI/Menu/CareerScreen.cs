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
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// The career: starting one with a preconstructed deck, the day at the store (daily matches, unopened boosters, what
/// happened lately), the store itself (a picture whose shelves are the store's sections), the collection (selling
/// cards) and opening boosters. The career's decks are built in the deck builder's collection mode.
/// </summary>
public partial class CareerScreen : Control
{
    private enum Page { Hub, Store, Boosters, Precons, Collection, Opening }

    private static readonly Vector2 TileSize = new(150, 210);

    /// <summary>The page to open on (set before coming back from another screen).</summary>
    public static string? OpenOn { get; set; }

    private static bool _tested;

    private readonly VBoxContainer _content = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private readonly Label _status = BoardStyle.MakeLabel("", 17, BoardStyle.Text, bold: true);
    private Label _title = null!;
    private Page _page = Page.Hub;
    private string _reward = CareerRules.CashReward;
    private int _deckIndex;
    private string _filter = "";
    private string? _openingSet;
    private List<PoolCard> _opened = new();
    private bool _justOpened; // the booster on the opening page still has to open on screen

    private static CareerService Career => App.Instance.Career;
    private static CareerWorld World => Career.World!;
    private static CareerState State => Career.State;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        var header = MenuKit.AddHeader(this, "Career", Back);
        _title = header.GetChild<Label>(1);
        _status.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(_status);
        var frame = new MarginContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = 92, OffsetRight = -24, OffsetBottom = -20 };
        frame.AddChild(_content);
        AddChild(frame);
        _preview.Size = new Vector2(300, 418);
        AddChild(_preview);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        _content.AddChild(loading);
        if (!await App.Instance.ContentReady)
        {
            loading.Text = "Card data isn't available: install a content module to play a career.";
            return;
        }
        // Testing: ARCANUM_CAREER_START=<starting deck> starts a career when there is none; ARCANUM_CAREER_PAGE=<page> opens a page
        // (Store, Boosters, Precons, Collection, Opening (opens the first unopened booster), Decks (the deck builder) or Daily (plays a daily match)).
        if (!Career.HasCareer && OS.GetEnvironment("ARCANUM_CAREER_START") is { Length: > 0 } start) Career.Start(start, DeckTitle(start));
        if (OS.GetEnvironment("ARCANUM_CAREER_PAGE") is { Length: > 0 } test && !_tested)
        {
            _tested = true;
            if (test == "Decks") { OpenDeckBuilder(); return; }
            if (test == "Daily" && Career.HasCareer && Career.Decks.List(null) is [var first, ..])
            {
                Career.Do(new StartDaily(Career.Now, _reward));
                App.Instance.PendingMatch = Career.DailyMatch(Career.Decks.Load(first).Deck, Settings.PlayerName);
                App.Instance.GoTo(App.GameBoardScene);
                return;
            }
            OpenOn ??= test;
        }
        if (OpenOn == nameof(Page.Opening) && Career.HasCareer && State.Sealed.Count > 0)
        {
            OpenOn = null;
            OpenBooster(State.Sealed.Keys.First());
            return;
        }
        if (OpenOn is { } page && Enum.TryParse<Page>(page, out var p)) _page = p;
        OpenOn = null;
        // A daily match left before it ended counts as lost.
        if (Career.HasCareer && State.Pending is { } pending)
        {
            Career.Do(new FinishDaily(Career.Now, Won: false));
            MenuKit.Toast(this, $"The daily match against {OpponentName(pending.OpponentId)} wasn't finished: it counts as a loss.");
        }
        Redraw();
    }

    private void Back()
    {
        if (_page != Page.Hub && Career.HasCareer)
        {
            _page = _page is Page.Boosters or Page.Precons ? Page.Store : Page.Hub;
            Redraw();
        }
        else App.Instance.GoTo(App.MainMenuScene);
    }

    private void Redraw()
    {
        _preview.Visible = false;
        foreach (var child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }
        if (Career.World is null)
        {
            _title.Text = "Career";
            _content.AddChild(MenuKit.Card(MenuKit.Hint(Career.Problem ?? "The installed content has no career.")));
            return;
        }
        if (!Career.HasCareer)
        {
            ShowStart();
            return;
        }
        _status.Text = $"{CareerRules.Money(State.Cash)}   ·   {State.CollectionSize} cards";
        switch (_page)
        {
            case Page.Store: ShowStore(); break;
            case Page.Boosters: ShowBoosters(); break;
            case Page.Precons: ShowPrecons(); break;
            case Page.Collection: ShowCollection(); break;
            case Page.Opening: ShowOpening(); break;
            default: ShowHub(); break;
        }
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (CareerRuleException e) { MenuKit.Toast(this, e.Message); }
        Redraw();
    }

    // ---------------------------------------------------------------- a new career

    private void ShowStart()
    {
        _title.Text = "A new career";
        _status.Text = "";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        if (Career.Problem is { } problem) box.AddChild(Warning(problem + " A new career can be started."));
        var intro = MenuKit.Hint($"You're a regular at {World.Content.StoreName}. Every day you get {World.Content.Dailies.PerDay} free daily matches against the "
            + "store's regulars, paid in cash or boosters. Buy boosters and decks, sell what you don't need, and build your decks from the cards you own.\n\n"
            + $"Choose the deck you start with. You also get {CareerRules.Money(World.Content.Starter.Cash)} and {World.Content.Starter.Boosters} boosters of this week's featured set "
            + $"({World.SetName(World.FeaturedSet(CareerRules.DayOf(DateTime.UtcNow)))}).");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(intro);
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 12);
        flow.AddThemeConstantOverride("v_separation", 12);
        foreach (var deck in World.Content.Starter.Decks)
        {
            var title = DeckTitle(deck);
            var button = BoardStyle.MakeButton(title, 16);
            button.CustomMinimumSize = new Vector2(250, 64);
            button.TooltipText = DeckSummary(World.Deck(deck));
            button.Pressed += () => MenuKit.Modal(this, title, $"Start a career with this deck?\n{DeckSummary(World.Deck(deck))}", null,
                ("Cancel", () => { }), ("Start", () => Try(() => Career.Start(deck, title))));
            flow.AddChild(button);
        }
        box.AddChild(flow);
        _content.AddChild(Filled(MenuKit.Card(Scrolled(box))));
    }

    // ---------------------------------------------------------------- the day

    private void ShowHub()
    {
        var now = Career.Now;
        var day = CareerRules.DayOf(now);
        _title.Text = World.Content.StoreName;

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        // Left: today, the daily matches.
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        body.AddChild(left);
        var featured = World.FeaturedSet(day);
        left.AddChild(MenuKit.SectionTitle($"{day.DayOfWeek}, {day:MMMM d}"));
        left.AddChild(MenuKit.Hint($"This week's featured set: {World.SetName(featured)}. Daily matches reset at midnight UTC."));
        left.AddChild(Daily(now, featured));

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 12);
        actions.AddThemeConstantOverride("v_separation", 12);
        void Action(string text, string tip, Action action)
        {
            var b = BoardStyle.MakeButton(text, 17);
            b.CustomMinimumSize = new Vector2(190, 52);
            b.TooltipText = tip;
            b.Pressed += action;
            actions.AddChild(b);
        }
        Action("Visit the store", "Boosters, preconstructed decks and selling cards", () => { _page = Page.Store; Redraw(); });
        Action("Collection", "Every card you own; sell the ones you don't need", () => { _page = Page.Collection; Redraw(); });
        Action("Decks", "Build decks from your collection", OpenDeckBuilder);
        left.AddChild(actions);
        left.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        var abandon = BoardStyle.MakeButton("End this career", 14);
        abandon.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        abandon.Pressed += () => MenuKit.Modal(this, "End this career?", "Your cards, cash and career decks are gone for good (the save is kept as a backup file).", null,
            ("Keep playing", () => { }), ("End it", () => { Career.Abandon(); Redraw(); }));
        left.AddChild(abandon);

        // Right: unopened boosters and what happened lately.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        right.AddThemeConstantOverride("separation", 8);
        right.AddChild(MenuKit.SectionTitle("Unopened boosters"));
        if (State.Sealed.Count == 0) right.AddChild(MenuKit.Hint("None. Win daily matches or buy some at the store."));
        foreach (var (set, count) in State.Sealed)
        {
            var row = new HBoxContainer();
            var name = BoardStyle.MakeLabel($"{count} × {World.SetName(set)}", 16);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            var open = BoardStyle.MakePrimaryButton("Open", 16);
            open.CustomMinimumSize = new Vector2(100, 40);
            open.Pressed += () => OpenBooster(set);
            row.AddChild(open);
            right.AddChild(row);
        }
        right.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        right.AddChild(MenuKit.SectionTitle($"Record {State.Wins}–{State.Losses} · reputation {State.Reputation}"));
        var notes = new VBoxContainer();
        notes.AddThemeConstantOverride("separation", 4);
        foreach (var note in Enumerable.Reverse(State.Notes))
        {
            var label = BoardStyle.MakeLabel($"{note.Utc.ToLocalTime():MMM d, HH:mm}  {note.Text}", 14, BoardStyle.TextDim);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            notes.AddChild(label);
        }
        right.AddChild(Scrolled(notes));
        body.AddChild(MenuKit.Card(right, 14));
    }

    private Control Daily(DateTime now, string featured)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        int left = CareerRules.DailiesLeft(State, World, now);
        box.AddChild(BoardStyle.MakeTitle($"Daily matches: {left} of {World.Content.Dailies.PerDay} left today", 22));
        if (left == 0)
        {
            var tomorrow = DateTime.SpecifyKind(CareerRules.DayOf(now).AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) - now;
            box.AddChild(MenuKit.Hint($"Come back tomorrow: new daily matches in {(int)tomorrow.TotalHours} h {tomorrow.Minutes} min."));
            return MenuKit.Card(box);
        }
        var (opponent, deck) = CareerRules.NextDailyOpponent(State, World, now);
        box.AddChild(BoardStyle.MakeLabel($"Next opponent: {opponent.Name} ({DeckColors(World.Deck(deck))})", 18, BoardStyle.Playable, bold: true));
        if (opponent.Style.Length > 0) box.AddChild(MenuKit.Hint(opponent.Style));

        var decks = Career.Decks.List(null);
        if (decks.Count == 0)
        {
            box.AddChild(Warning("You have no decks: build one from your collection."));
            return MenuKit.Card(box);
        }
        _deckIndex = Math.Clamp(_deckIndex, 0, decks.Count - 1);
        var picker = MenuKit.Options(decks.Select(d => d.Name), _deckIndex);
        picker.CustomMinimumSize = new Vector2(320, 40);
        picker.ItemSelected += i => { _deckIndex = (int)i; Redraw(); };
        box.AddChild(MenuKit.Row("Your deck", picker, 160));
        var (chosen, _) = Career.Decks.Load(decks[_deckIndex]);
        var problems = DeckProblems(chosen);
        if (problems.Count > 0) box.AddChild(Warning(string.Join("\n", problems.Take(4))));

        var rewards = new HBoxContainer();
        rewards.AddThemeConstantOverride("separation", 10);
        var dailies = World.Content.Dailies;
        foreach (var (reward, text) in new[] { (CareerRules.CashReward, $"{CareerRules.Money(dailies.Win)}"), (CareerRules.BoosterReward, $"{dailies.WinBoosters} {World.SetName(featured)} booster{(dailies.WinBoosters == 1 ? "" : "s")}") })
        {
            var b = _reward == reward ? BoardStyle.MakePrimaryButton("✓ " + text, 16) : BoardStyle.MakeButton(text, 16);
            b.CustomMinimumSize = new Vector2(200, 44);
            b.Pressed += () => { _reward = reward; Redraw(); };
            rewards.AddChild(b);
        }
        box.AddChild(MenuKit.Row("Prize for a win", rewards, 160));
        box.AddChild(MenuKit.Hint($"A loss pays {CareerRules.Money(dailies.Loss)}. Leaving a match before it ends counts as a loss."));
        var play = BoardStyle.MakePrimaryButton($"Play against {opponent.Name}", 20);
        play.CustomMinimumSize = new Vector2(320, 56);
        play.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        play.Disabled = problems.Count > 0;
        play.Pressed += () =>
        {
            try
            {
                Career.Do(new StartDaily(Career.Now, _reward));
                App.Instance.PendingMatch = Career.DailyMatch(chosen, Settings.PlayerName);
                App.Instance.GoTo(App.GameBoardScene);
            }
            catch (CareerRuleException e)
            {
                MenuKit.Toast(this, e.Message);
                Redraw();
            }
        };
        box.AddChild(play);
        return MenuKit.Card(box);
    }

    /// <summary>Why a deck can't be played in a daily match: cards not owned, or not a legal casual deck.</summary>
    private static List<string> DeckProblems(DeckList deck)
    {
        var problems = Career.Missing(deck).Select(m => $"• You have {m.Owned} of the {m.Needed} {m.Name} this deck uses.").ToList();
        problems.AddRange(DeckValidator.Validate(deck, FormatRules.Casual, Cards).Where(i => i.Severity == IssueSeverity.Error).Select(i => "• " + i.Message));
        return problems;
    }

    private static void OpenDeckBuilder()
    {
        DeckBuilder.CareerMode = true;
        App.Instance.GoTo(App.DeckBuilderScene);
    }

    // ---------------------------------------------------------------- the store

    /// <summary>The parts of the store picture that are its sections, as fractions of the picture.</summary>
    private static readonly (string Name, string Tip, Rect2 Area, Page Page)[] Hotspots =
    {
        ("Boosters", "Boosters of every set on the shelf", new Rect2(0.47f, 0.16f, 0.25f, 0.63f), Page.Boosters),
        ("Decks", "Preconstructed decks", new Rect2(0.29f, 0.56f, 0.17f, 0.31f), Page.Precons),
        ("Sell cards", "The counter buys cards from your collection", new Rect2(0.25f, 0.87f, 0.6f, 0.13f), Page.Collection),
    };

    private void ShowStore()
    {
        _title.Text = World.Content.StoreName;
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        side.AddThemeConstantOverride("separation", 10);
        side.AddChild(MenuKit.SectionTitle("The store"));
        foreach (var (name, tip, _, page) in Hotspots)
        {
            var b = BoardStyle.MakeButton(name, 17);
            b.CustomMinimumSize = new Vector2(0, 50);
            b.TooltipText = tip;
            b.Pressed += () => { _page = page; Redraw(); };
            side.AddChild(b);
        }

        if (ResourceLoader.Exists(CareerService.StoreImage))
        {
            var texture = GD.Load<Texture2D>(CareerService.StoreImage);
            var holder = new AspectRatioContainer { Ratio = texture.GetWidth() / (float)texture.GetHeight(), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            var picture = new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
            holder.AddChild(picture);
            foreach (var (name, tip, area, page) in Hotspots)
            {
                var spot = new Button { Flat = true, FocusMode = FocusModeEnum.None, TooltipText = tip, MouseDefaultCursorShape = CursorShape.PointingHand,
                    AnchorLeft = area.Position.X, AnchorTop = area.Position.Y, AnchorRight = area.End.X, AnchorBottom = area.End.Y };
                spot.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
                spot.AddThemeStyleboxOverride("pressed", BoardStyle.Box(new Color(1, 0.85f, 0.4f, 0.18f), 10, BoardStyle.Playable, 3));
                spot.AddThemeStyleboxOverride("hover", BoardStyle.Box(new Color(1, 0.85f, 0.4f, 0.12f), 10, BoardStyle.Playable, 3));
                var label = BoardStyle.MakeLabel(name, 18, BoardStyle.Text, bold: true);
                var tag = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, AnchorLeft = 0.5f, AnchorRight = 0.5f, GrowHorizontal = GrowDirection.Both, OffsetTop = 8 };
                tag.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0, 0, 0, 0.75f), 8));
                label.MouseFilter = MouseFilterEnum.Ignore;
                tag.AddChild(label);
                spot.AddChild(tag);
                spot.MouseEntered += () => tag.Visible = true;
                spot.MouseExited += () => tag.Visible = false;
                spot.Pressed += () => { _page = page; Redraw(); };
                picture.AddChild(spot);
            }
            body.AddChild(holder);
        }
        else side.AddChild(MenuKit.Hint("(The store's picture isn't installed.)"));
        body.AddChild(MenuKit.Card(side, 14));
    }

    private void ShowBoosters()
    {
        _title.Text = "Boosters";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        var featured = World.FeaturedSet(CareerRules.DayOf(Career.Now));
        box.AddChild(MenuKit.Hint($"Every booster costs {CareerRules.Money(World.Content.BoosterPrice)}. Opened boosters go straight into your collection."));
        foreach (var set in World.Content.Sets.Where(World.SellsBoosters))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = BoardStyle.MakeLabel(World.SetName(set) + (set.Equals(featured, StringComparison.OrdinalIgnoreCase) ? "  ★ featured" : ""), 18);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            int owned = State.Sealed.GetValueOrDefault(set.ToLowerInvariant());
            if (owned > 0) row.AddChild(BoardStyle.MakeLabel($"{owned} unopened", 15, BoardStyle.TextDim));
            var buy = BoardStyle.MakePrimaryButton($"Buy · {CareerRules.Money(World.Content.BoosterPrice)}", 16);
            buy.CustomMinimumSize = new Vector2(150, 42);
            buy.Disabled = State.Cash < World.Content.BoosterPrice;
            buy.Pressed += () => Try(() => Career.Do(new BuyBooster(Career.Now, set)));
            row.AddChild(buy);
            var open = BoardStyle.MakeButton("Open one", 16);
            open.CustomMinimumSize = new Vector2(120, 42);
            open.Disabled = owned == 0;
            open.Pressed += () => OpenBooster(set);
            row.AddChild(open);
            box.AddChild(row);
        }
        _content.AddChild(Filled(MenuKit.Card(Scrolled(box))));
    }

    private void ShowPrecons()
    {
        _title.Text = "Preconstructed decks";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        box.AddChild(MenuKit.Hint("A preconstructed deck's cards go into your collection and the deck is added to your decks."));
        foreach (var precon in World.Content.Precons)
        {
            var deck = World.Deck(precon.Deck);
            int price = World.PreconPrice(precon);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var name = BoardStyle.MakeLabel($"{DeckTitle(precon.Deck)}  ·  {DeckSummary(deck)}", 16);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            var buy = BoardStyle.MakePrimaryButton($"Buy · {CareerRules.Money(price)}", 16);
            buy.CustomMinimumSize = new Vector2(150, 42);
            buy.Disabled = State.Cash < price;
            buy.Pressed += () => Try(() =>
            {
                Career.Do(new BuyPrecon(Career.Now, precon.Deck));
                Career.Decks.Save(UniqueDeckName(DeckTitle(precon.Deck)), "casual", deck);
                MenuKit.Toast(this, "Added to your collection and your decks");
            });
            row.AddChild(buy);
            box.AddChild(row);
        }
        _content.AddChild(Filled(MenuKit.Card(Scrolled(box))));
    }

    private static string UniqueDeckName(string name)
    {
        var taken = Career.Decks.List(null).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // ---------------------------------------------------------------- the collection

    private void ShowCollection()
    {
        _title.Text = "Collection";
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        var search = MenuKit.TextField(_filter, "Filter by name…");
        search.CustomMinimumSize = new Vector2(320, 40);
        search.TextSubmitted += text => { _filter = text; Redraw(); };
        top.AddChild(search);
        var apply = BoardStyle.MakeButton("Filter", 15);
        apply.Pressed += () => { _filter = search.Text; Redraw(); };
        top.AddChild(apply);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var extras = Extras();
        var sellExtras = BoardStyle.MakeButton($"Sell extras ({extras.Sum(e => e.Count)}) · {CareerRules.Money(extras.Sum(e => World.SellPrice(e.Card) * e.Count))}", 15);
        sellExtras.TooltipText = "Sells every copy beyond the fourth of each card (basic lands aren't bought)";
        sellExtras.Disabled = extras.Count == 0;
        sellExtras.Pressed += () => MenuKit.Modal(this, "Sell extras?", $"Sell {extras.Sum(e => e.Count)} cards for {CareerRules.Money(extras.Sum(e => World.SellPrice(e.Card) * e.Count))}?", null,
            ("Cancel", () => { }), ("Sell", () => Try(() => { foreach (var (card, count) in Extras()) Career.Do(new SellCard(Career.Now, card.ToString(), count)); })));
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
            var name = BoardStyle.MakeLabel($"{count} × {card.Name}", 15);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.ClipText = true;
            row.AddChild(name);
            row.AddChild(BoardStyle.MakeLabel(card.Set.Length > 0 ? $"{card.Set} #{card.Number}" : "", 13, BoardStyle.TextDim));
            int price = World.SellPrice(card);
            var sell = BoardStyle.MakeButton(price > 0 ? $"Sell 1 · {CareerRules.Money(price)}" : "Not bought", 13, compact: true);
            sell.CustomMinimumSize = new Vector2(130, 30);
            sell.Disabled = price == 0;
            sell.Pressed += () => Try(() => Career.Do(new SellCard(Career.Now, card.ToString(), 1)));
            row.AddChild(sell);
            var def = DefinitionOf(card);
            row.MouseEntered += () => ShowPreviewLeftOf(def, row);
            row.MouseExited += () => _preview.Visible = false;
            list.AddChild(row);
        }
        if (cards.Count > 300) list.AddChild(MenuKit.Hint($"…and {cards.Count - 300} more: filter by name to find them."));
        box.AddChild(Scrolled(list));
        body.AddChild(Filled(MenuKit.Card(box, 14)));
        body.AddChild(new Control { CustomMinimumSize = new Vector2(340, 0) }); // room for the card preview
    }

    /// <summary>Copies beyond the fourth of each card (by name, keeping the most valuable printings), basic lands left out.</summary>
    private static List<(CardKey Card, int Count)> Extras()
    {
        var extras = new List<(CardKey, int)>();
        foreach (var group in State.Cards.GroupBy(c => c.Card.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (World.IsBasicLand(group.Key) || World.SellPrice(group.First().Card) == 0 && group.All(c => World.SellPrice(c.Card) == 0)) continue;
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
        try
        {
            _opened = Career.Do(new OpenBooster(Career.Now, set)).Cards.ToList();
            _openingSet = set;
            _justOpened = true;
            _page = Page.Opening;
        }
        catch (CareerRuleException e) { MenuKit.Toast(this, e.Message); }
        Redraw();
    }

    private void ShowOpening()
    {
        var set = _openingSet ?? "";
        _title.Text = $"{World.SetName(set)} booster";
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 10);
        var nodes = new List<CardNode>();
        foreach (var card in _opened.OrderByDescending(c => World.Value(CardKey.Of(c))))
        {
            var holder = new Control { CustomMinimumSize = TileSize };
            var node = new CardNode { Size = TileSize };
            holder.AddChild(node);
            node.Setup(ViewOf(DefinitionOf(CardKey.Of(card))), showCostPips: false);
            node.HoverStarted += c => ShowPreviewLeftOf(DefinitionOf(CardKey.Of(card)), holder);
            node.HoverEnded += _ => _preview.Visible = false;
            node.TooltipText = $"{card.Rarity} · sells for {CareerRules.Money(World.SellPrice(CardKey.Of(card)))}";
            flow.AddChild(holder);
            nodes.Add(node);
        }
        box.AddChild(Scrolled(flow));
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        int left = State.Sealed.GetValueOrDefault(set);
        var again = BoardStyle.MakePrimaryButton($"Open another ({left} left)", 18);
        again.CustomMinimumSize = new Vector2(240, 50);
        again.Disabled = left == 0;
        again.Pressed += () => OpenBooster(set);
        actions.AddChild(again);
        var done = BoardStyle.MakeButton("Done", 18);
        done.CustomMinimumSize = new Vector2(140, 50);
        done.Pressed += () => { _page = Page.Hub; Redraw(); };
        actions.AddChild(done);
        box.AddChild(actions);
        _content.AddChild(Filled(box));
        bool animate = _justOpened;
        _justOpened = false;
        if (!animate) return;

        // The booster opens in 3D first, then the cards turn over.
        if (BoosterOpening.Available && Settings.Current.BoosterOpening != "skip" && World.Sets.TryGetValue(set, out var definition))
        {
            foreach (var node in nodes) node.Modulate = Colors.Transparent;
            var opening = new BoosterOpening(definition.PackImage, definition.PackImageSeals, definition.Name, automatic: Settings.Current.BoosterOpening == "auto");
            opening.Opened += _ => Reveal(nodes);
            AddChild(opening);
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

    /// <summary>"fdn-br" → "Foundations: black-red".</summary>
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

    private void ShowPreviewLeftOf(CardDefinition definition, Control anchor)
    {
        _preview.Setup(ViewOf(definition), false);
        var rect = anchor.GetGlobalRect();
        float x = rect.End.X + 16 + _preview.Size.X < GetViewportRect().Size.X ? rect.End.X + 16 : rect.Position.X - _preview.Size.X - 16;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 120, 90, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    private static Label Warning(string text)
    {
        var label = MenuKit.Hint(text);
        label.AddThemeColorOverride("font_color", new Color("ff8a70"));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    private static T Filled<T>(T control) where T : Control
    {
        control.SizeFlagsHorizontal = control.SizeFlagsVertical = SizeFlags.ExpandFill;
        return control;
    }

    private static ScrollContainer Scrolled(Control inner)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inner);
        return scroll;
    }
}
