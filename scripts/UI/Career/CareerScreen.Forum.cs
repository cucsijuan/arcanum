// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.UI.Menu;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// The store's forum on the website: a pinned post with the coming events, a post for each of today's events (where the
/// player registers), and the regulars' posts offering ante and practice games (where the player contacts them). It looks
/// like a forum dressed up as a game table: felt category bars, posts on card stock, a colored token for each author.
/// </summary>
public partial class CareerScreen
{
    /// <summary>The thread open in the forum; null shows the boards.</summary>
    private static string? _forumThread;

    private enum ThreadKind { Pinned, Event, Ante, Practice, Listing, MyPosts, NewPost }

    /// <summary>One thread: its id (kept while the page refreshes), title, author and what it is about.</summary>
    private sealed record ForumThread(string Id, ThreadKind Kind, string Title, string Author, OpponentSpec? Opponent = null, EventSpec? Event = null, string Body = "",
        CareerListing? Listing = null);

    private static readonly Color Felt = new("2f5d3a");
    private static readonly Color FeltDark = new("23472c");
    private static readonly Color Stock = new("fbf5e6");
    private static readonly Color Cardboard = new("8a6a45");
    private static readonly Color[] Tokens = { new("c0392b"), new("2e86c1"), new("27ae60"), new("f39c12"), new("8e44ad"), new("16a085"), new("d35400"), new("34495e") };

    private void ForumPage(VBoxContainer page)
    {
        // The player's posts catch up with the time gone by (sold, offered, expired) as the forum opens.
        if (State.Posts.Any(p => CareerRules.SaleCheckDue(p, Career.Now)))
            Try(() => Career.Do(new CheckSales(Career.Now)));
        var threads = ForumThreads();
        if (_forumThread is { } id && threads.FirstOrDefault(t => t.Id == id) is { } open)
        {
            ThreadPage(page, open);
            return;
        }
        _forumThread = null;
        var boards = new VBoxContainer();
        boards.AddThemeConstantOverride("separation", 14);
        boards.AddChild(CareerStyle.Text($"Welcome to the {World.Content.Forum.Name}: the store's events and players looking for a game.", 19, CareerStyle.InkDim, wrap: true));

        if (Career.CurrentEvent() is var (current, _, _))
        {
            var playing = new HBoxContainer();
            playing.AddThemeConstantOverride("separation", 12);
            playing.AddChild(CareerStyle.Text($"You are registered in {current.Name}.", 20, CareerStyle.Good));
            playing.AddChild(CareerStyle.Button("Go to the event", () => App.Instance.GoTo(App.EventScene), 18, primary: true));
            boards.AddChild(playing);
        }

        boards.AddChild(Board("Events", "Store announcements and today's events.", threads.Where(t => t.Kind is ThreadKind.Pinned or ThreadKind.Event)));
        var practice = World.Content.Practice;
        int paid = CareerRules.PaidPracticeLeft(State, World, Career.Now);
        int antes = CareerRules.AntesLeft(State, World, Career.Now);
        boards.AddChild(Board("Match offers",
            $"Regulars looking for a game. Ante: {antes} left today. Practice: {(paid > 0 ? $"{paid} more pay today ({CareerRules.Money(practice.Win)} a win, {CareerRules.Money(practice.Loss)} a loss)" : "no more pay today")}.",
            threads.Where(t => t.Kind is ThreadKind.Ante or ThreadKind.Practice)));
        if (CareerMarket.Trading(World, CareerRules.DayOf(Career.Now)))
            boards.AddChild(Board("Buy & Sell", "Players selling cards (make them an offer!) and your own sale posts. Prices swing: a bargain one day, steep the next.",
                threads.Where(t => t.Kind is ThreadKind.Listing or ThreadKind.MyPosts or ThreadKind.NewPost)));
        page.AddChild(Scrolled(boards));
        page.AddChild(CareerStyle.Text($"Your record: {State.Wins} wins, {State.Losses} losses  ·  {TierText()}", 18, CareerStyle.InkDim));
    }

    /// <summary>A category: a felt header bar and its threads on card stock.</summary>
    private Control Board(string name, string description, IEnumerable<ForumThread> threads)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        var header = new PanelContainer();
        var headerStyle = CareerStyle.Box(Felt, FeltDark, 2, 0, 10);
        headerStyle.CornerRadiusTopLeft = headerStyle.CornerRadiusTopRight = 10;
        header.AddThemeStyleboxOverride("panel", headerStyle);
        var headerText = new VBoxContainer();
        headerText.AddThemeConstantOverride("separation", 2);
        headerText.AddChild(CareerStyle.Text(name, 26, CareerStyle.Gold));
        headerText.AddChild(CareerStyle.Text(description, 16, new Color("d9e8d6"), wrap: true));
        header.AddChild(headerText);
        box.AddChild(header);

        var list = new PanelContainer();
        var listStyle = CareerStyle.Box(Stock, Cardboard, 2, 0, 0);
        listStyle.CornerRadiusBottomLeft = listStyle.CornerRadiusBottomRight = 10;
        list.AddThemeStyleboxOverride("panel", listStyle);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 0);
        list.AddChild(rows);
        var items = threads.ToList();
        if (items.Count == 0) rows.AddChild(Padded(CareerStyle.Text("Nothing here right now. Check back later!", 18, CareerStyle.InkDim)));
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) rows.AddChild(new ColorRect { Color = new Color(Cardboard, 0.35f), CustomMinimumSize = new Vector2(0, 1) });
            rows.AddChild(ThreadRow(items[i]));
        }
        box.AddChild(list);
        return box;
    }

    private static MarginContainer Padded(Control inner, int horizontal = 14, int vertical = 10)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        margin.AddChild(inner);
        return margin;
    }

    /// <summary>A thread in its board: the author's token, the title (a link), who wrote it, and a tag for its kind.</summary>
    private Control ThreadRow(ForumThread thread)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        row.AddChild(AuthorToken(thread.Author, 38));
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        var title = CareerStyle.LinkButton(thread.Title, size: 20);
        title.Alignment = HorizontalAlignment.Left;
        title.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        title.Pressed += () => { _forumThread = thread.Id; ShowWebPage(); };
        text.AddChild(title);
        text.AddChild(CareerStyle.Text($"by {thread.Author}", 15, CareerStyle.InkDim));
        row.AddChild(text);
        var (tag, color) = thread.Kind switch
        {
            ThreadKind.Pinned => ("PINNED", new Color("b3261e")),
            ThreadKind.Event => ("TODAY", new Color("b7791f")),
            ThreadKind.Ante => ("ANTE", new Color("7d3c98")),
            ThreadKind.Listing when CareerRules.ListingBought(State, CareerRules.DayOf(Career.Now), thread.Listing!.Index) => ("SOLD", new Color("7f7f7f")),
            ThreadKind.Listing => (CareerRules.Money(thread.Listing!.Price), new Color("1e7a3c")),
            ThreadKind.MyPosts => ("YOURS", new Color("24345e")),
            ThreadKind.NewPost => ("NEW", new Color("b7791f")),
            _ => ("PRACTICE", new Color("2e6f9e")),
        };
        var badge = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        badge.AddThemeStyleboxOverride("panel", CareerStyle.Box(color, color.Darkened(0.3f), 1, 6, 4));
        badge.AddChild(CareerStyle.Text(tag, 14, Colors.White));
        row.AddChild(badge);
        return Padded(row);
    }

    /// <summary>A round game token in the author's color with their initial.</summary>
    private static Control AuthorToken(string author, float size)
    {
        var color = Tokens[(int)(StableHash(author) % (uint)Tokens.Length)];
        var token = new PanelContainer { CustomMinimumSize = new Vector2(size, size), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var style = CareerStyle.Box(color, color.Darkened(0.35f), 3, (int)(size / 2));
        style.ShadowColor = new Color(0, 0, 0, 0.25f);
        style.ShadowSize = 2;
        style.ShadowOffset = new Vector2(1, 2);
        token.AddThemeStyleboxOverride("panel", style);
        var initial = CareerStyle.Text(author.Length > 0 ? author[..1].ToUpperInvariant() : "?", (int)(size * 0.45f), Colors.White);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.VerticalAlignment = VerticalAlignment.Center;
        token.AddChild(initial);
        return token;
    }

    /// <summary>A thread opened: where it is, then the post with its author on the left.</summary>
    private void ThreadPage(VBoxContainer page, ForumThread thread)
    {
        var crumbs = new HBoxContainer();
        crumbs.AddThemeConstantOverride("separation", 6);
        var forum = CareerStyle.LinkButton(World.Content.Forum.Name, size: 18);
        forum.Pressed += () => { _forumThread = null; ShowWebPage(); };
        crumbs.AddChild(forum);
        var board = thread.Kind switch { ThreadKind.Pinned or ThreadKind.Event => "Events", ThreadKind.Ante or ThreadKind.Practice => "Match offers", _ => "Buy & Sell" };
        crumbs.AddChild(CareerStyle.Text($"›  {board}  ›", 18, CareerStyle.InkDim));
        page.AddChild(crumbs);
        page.AddChild(CareerStyle.Text(thread.Title, 30, CareerStyle.TitleBar));

        var post = new PanelContainer();
        post.AddThemeStyleboxOverride("panel", CareerStyle.Box(Stock, Cardboard, 2, 10, 0));
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 0);
        post.AddChild(columns);

        var authorColumn = new PanelContainer { CustomMinimumSize = new Vector2(190, 0) };
        var authorStyle = CareerStyle.Box(new Color("efe4c8"), null, 0, 0, 14);
        authorStyle.CornerRadiusTopLeft = authorStyle.CornerRadiusBottomLeft = 10;
        authorColumn.AddThemeStyleboxOverride("panel", authorStyle);
        var author = new VBoxContainer();
        author.AddThemeConstantOverride("separation", 6);
        var token = AuthorToken(thread.Author, 64);
        token.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        author.AddChild(token);
        var name = CareerStyle.Text(thread.Author, 18, CareerStyle.Ink, wrap: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        author.AddChild(name);
        var role = thread.Opponent is { } o ? World.Content.Tiers[Math.Min(o.Tier, World.Content.Tiers.Count - 1)].Name
            : thread.Kind is ThreadKind.MyPosts or ThreadKind.NewPost ? "You" : thread.Kind == ThreadKind.Listing ? "Member" : "Store staff";
        var roleText = CareerStyle.Text(role, 15, CareerStyle.InkDim, wrap: true);
        roleText.HorizontalAlignment = HorizontalAlignment.Center;
        author.AddChild(roleText);
        authorColumn.AddChild(author);
        columns.AddChild(authorColumn);

        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        switch (thread.Kind)
        {
            case ThreadKind.Pinned: UpcomingPost(body); break;
            case ThreadKind.Event: EventPost(body, thread.Event!); break;
            case ThreadKind.Listing: ListingPost(body, thread.Listing!); break;
            case ThreadKind.MyPosts: MyPostsPost(body); break;
            case ThreadKind.NewPost: NewSalePost(body); break;
            default: OfferPost(body, thread); break;
        }
        columns.AddChild(Padded(body, 18, 14));
        columns.GetChild<Control>(1).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        page.AddChild(Scrolled(post));
    }

    /// <summary>The pinned post: the coming week, a date per heading, its events as bullets and their details under them.</summary>
    private static void UpcomingPost(VBoxContainer body)
    {
        var today = CareerRules.DayOf(Career.Now);
        foreach (var news in World.News(today)) body.AddChild(CareerStyle.Text(news, 19, CareerStyle.Ink, wrap: true));
        body.AddChild(CareerStyle.Text("Here's what's on at the store this week. See you there!", 19, CareerStyle.Ink, wrap: true));
        for (int i = 0; i < 7; i++)
        {
            var day = today.AddDays(i);
            var events = CareerRules.EventsOn(World, day).Where(e => i == 0 || CareerRules.Happens(e, day)).ToList();
            if (events.Count == 0) continue;
            body.AddChild(CareerStyle.Text(i == 0 ? $"Today, {day:dddd MMMM d}" : $"{day:dddd, MMMM d}", 22, CareerStyle.TitleBar));
            foreach (var e in events)
            {
                body.AddChild(Bullet(e.Name, 0, 19, CareerStyle.Ink));
                foreach (var detail in EventDetails(e, day)) body.AddChild(Bullet(detail, 1, 16, CareerStyle.InkDim));
            }
        }
        body.AddChild(CareerStyle.Text($"Your qualification tokens: {State.Tokens}", 16, CareerStyle.InkDim));
        foreach (var (id, start) in State.Qualified)
            if (DateOnly.FromDayNumber(start) is var day && day >= today && World.Event(id, day) is { } invited)
                body.AddChild(CareerStyle.Text($"You're qualified for {invited.Name}, {day:dddd, MMMM d}.", 16, CareerStyle.Good));
    }

    private static Control Bullet(string text, int level, int size, Color color)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(18 + level * 26, 0) });
        row.AddChild(CareerStyle.Text(level == 0 ? "•" : "–", size, color));
        var label = CareerStyle.Text(text, size, color, wrap: true);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        return row;
    }

    /// <summary>An event's facts, one per line: the format, the table, the entry, the prizes and when it's played online.</summary>
    private static IEnumerable<string> EventDetails(EventSpec e, DateOnly day)
    {
        yield return e.Kind switch
        {
            CareerRules.Draft => $"Format: draft of {World.SetName(CareerRules.EventSet(World, e, day))}",
            CareerRules.Commander => "Format: Commander (bring your Commander deck)",
            _ => "Format: constructed (bring your deck)",
        };
        var start = CareerRules.Start(e, day) ?? day;
        if (e.Days > 1) yield return $"From {start:dddd, MMMM d} to {CareerRules.LastDay(e, start):dddd, MMMM d}: play the rounds on any of these days";
        var players = e.PlayersMax > e.Players ? $"{e.Players}–{e.PlayersMax} players" : $"{e.Players} players";
        var rounds = e.Rounds > 0 ? $"{e.Rounds} Swiss rounds" : "Swiss rounds by attendance";
        yield return e.Kind == CareerRules.Commander ? $"Pods of {e.Players}, {e.Rounds} games"
            : $"{players}, {rounds}{(e.BestOf > 1 ? $", best of {e.BestOf}" : "")}"
              + (e.CutAfter > 0 ? $", a cut to {e.CutPoints} points after round {e.CutAfter}" : "") + (e.TopCut > 0 ? $", then a top {e.TopCut}" : "");
        if (e.Invitational) yield return "By invitation: only qualified players may enter";
        var entry = new List<string> { CareerRules.Money(e.Entry) };
        if (e.EntryBoosters > 0) entry.Add($"{e.EntryBoosters} boosters of the set");
        if (e.EntryTokens > 0) entry.Add($"{e.EntryTokens} qualification token{(e.EntryTokens == 1 ? "" : "s")}");
        yield return "Entry: " + string.Join(" or ", entry);
        var prizes = CareerRules.PrizesOf(e).Select(p => (p.Wins >= 0 ? $"{p.Wins} wins" : p.Place switch { 0 => "everyone else", 1 => "1st", _ => $"top {p.Place}" })
            + (p.MinPlayers > 0 ? $" (with {p.MinPlayers}+ players)" : "") + ": "
            + string.Join(", ", new[]
            {
                p.Cash > 0 ? CareerRules.Money(p.Cash) : null, p.Boosters > 0 ? $"{p.Boosters} booster{(p.Boosters == 1 ? "" : "s")}" : null,
                p.Tokens > 0 ? $"{p.Tokens} token{(p.Tokens == 1 ? "" : "s")}" : null,
                p.Qualifies.Length > 0 ? $"an invitation to {World.Event(p.Qualifies, day)?.Name ?? p.Qualifies}" : null,
                p.Reputation > 0 ? $"{p.Reputation} reputation" : null,
            }.OfType<string>())).ToList();
        if (prizes.Count > 0) yield return "Prizes: " + string.Join(" · ", prizes);
        if (e.MinTier > 0) yield return $"For {World.Content.Tiers[Math.Min(e.MinTier, World.Content.Tiers.Count - 1)].Name}s and up";
        if (OnlineText(e, day) is { Length: > 0 } online) yield return online.Trim();
    }

    /// <summary>Today's event: its details, then registering (one link per way to pay the entry).</summary>
    private void EventPost(VBoxContainer body, EventSpec e)
    {
        var today = CareerRules.DayOf(Career.Now);
        body.AddChild(CareerStyle.Text(e.Days > 1 && CareerRules.Start(e, today) != today ? $"{e.Name} is on! Here are the details:" : $"{e.Name} is on today! Here are the details:", 20, CareerStyle.Ink, wrap: true));
        foreach (var detail in EventDetails(e, today)) body.AddChild(Bullet(detail, 0, 18, CareerStyle.Ink));
        if (Career.CurrentEvent() is var (current, _, _))
        {
            body.AddChild(CareerStyle.Text(current.Id == e.Id ? "You're registered." : $"You're registered in {current.Name}; finish it first.", 19, CareerStyle.Good));
            body.AddChild(CareerStyle.Button("Go to the event", () => App.Instance.GoTo(App.EventScene), 20, primary: true));
            return;
        }
        if (CareerRules.Played(State, e, today)) { body.AddChild(CareerStyle.Text(e.Days > 1 ? "You played it." : "You played it today.", 19, CareerStyle.Good)); return; }
        if (e.Invitational && !CareerRules.IsQualified(State, e, today))
        {
            body.AddChild(CareerStyle.Text("It's by invitation, and you aren't qualified for it.", 19, CareerStyle.Bad, wrap: true));
            return;
        }
        if (e.Online is { PvpOnly: true }) { body.AddChild(CareerStyle.Text("It's played online only.", 19, CareerStyle.InkDim)); return; }
        if (CareerRules.Tier(State, World) < e.MinTier) { body.AddChild(CareerStyle.Text("Your reputation isn't high enough yet.", 19, CareerStyle.Bad)); return; }

        (DeckList Deck, List<string> Problems)? deck = null;
        if (e.Kind is CareerRules.Constructed or CareerRules.Commander)
        {
            var format = World.FormatOf(e);
            if (e.Kind == CareerRules.Constructed && !string.IsNullOrEmpty(e.Format))
                body.AddChild(CareerStyle.Text($"{format.Name}: your deck must be legal in {format.Name}.", 19, CareerStyle.InkDim, wrap: true));
            deck = DeckPicker(body, commander: e.Kind == CareerRules.Commander, format: format);
            if (deck is null) return;
        }
        var links = new HFlowContainer();
        links.AddThemeConstantOverride("h_separation", 20);
        void Way(string pay, string price, bool can)
        {
            var link = CareerStyle.LinkButton($"Register ({price}) and Go to LGS", size: 20);
            link.Disabled = !can || deck is { Problems.Count: > 0 };
            link.Pressed += () =>
            {
                try
                {
                    Career.Enter(e, pay, deck?.Deck);
                    App.Instance.GoTo(App.EventScene);
                }
                catch (CareerRuleException x)
                {
                    MenuKit.Toast(this, x.Message);
                    Refresh();
                }
            };
            links.AddChild(link);
        }
        Way(CareerRules.PayCash, CareerRules.Money(e.Entry), State.Cash >= e.Entry);
        if (e.Kind == CareerRules.Draft && e.EntryBoosters > 0)
            Way(CareerRules.PayBoosters, $"{e.EntryBoosters} boosters", State.Sealed.GetValueOrDefault(CareerRules.EventSet(World, e, today)) >= e.EntryBoosters);
        if (e.EntryTokens > 0) Way(CareerRules.PayTokens, $"{e.EntryTokens} token{(e.EntryTokens == 1 ? "" : "s")}", State.Tokens >= e.EntryTokens);
        body.AddChild(links);
    }

    /// <summary>A regular's offer: their post, what they play, and Contact (the ante chat, or a short chat before a practice game).</summary>
    private void OfferPost(VBoxContainer body, ForumThread thread)
    {
        var opponent = thread.Opponent!;
        body.AddChild(CareerStyle.Text(thread.Body, 20, CareerStyle.Ink, wrap: true));
        body.AddChild(CareerStyle.Text($"— {opponent.Name}. {opponent.Style}", 17, CareerStyle.InkDim, wrap: true));
        if (thread.Kind == ThreadKind.Ante)
            body.AddChild(CareerStyle.Text("An ante match: you pick one of the cards they put up and offer one of yours; the winner keeps both.", 16, CareerStyle.InkDim, wrap: true));
        if (DeckPicker(body) is not var (deck, problems)) return;
        var contact = CareerStyle.LinkButton($"Contact {opponent.Name}", size: 22);
        contact.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        contact.Disabled = problems.Count > 0;
        contact.Pressed += () =>
        {
            if (thread.Kind == ThreadKind.Ante) OpenChat(opponent.Id, deck);
            else OpenPracticeChat(opponent.Id, deck);
        };
        body.AddChild(contact);
        if (problems.Count > 0) body.AddChild(CareerStyle.Text("This deck can't be played yet: fix it in the deck builder or choose another deck.", 17, CareerStyle.Bad, wrap: true));
    }

    /// <summary>
    /// The threads right now: the pinned post, today's events, an ante offer for each ante match left (up to eight) and a
    /// few practice offers. The offers are drawn again after each match, so the board changes as the day goes on.
    /// </summary>
    private static List<ForumThread> ForumThreads()
    {
        var now = Career.Now;
        var today = CareerRules.DayOf(now);
        var store = World.Content.StoreName;
        var threads = new List<ForumThread> { new("pinned", ThreadKind.Pinned, "Upcoming events", store) };
        threads.AddRange(CareerRules.EventsOn(World, today).Select(e => new ForumThread($"event:{e.Id}", ThreadKind.Event, $"[TODAY!] {e.Name}", store, Event: e)));

        var forum = World.Content.Forum;
        var regulars = CareerRules.Opponents(State, World);
        uint seed = StableHash($"{State.Seed}/{today.DayNumber}/{State.AntesPlayed}/{State.PracticePlayed}/{State.Wins + State.Losses}");
        var random = new Random((int)seed);
        IEnumerable<OpponentSpec> Posters(int count)
        {
            var order = regulars.OrderBy(_ => random.Next()).ToList();
            for (int i = 0; i < count && order.Count > 0; i++) yield return order[i % order.Count];
        }
        string Pick(IReadOnlyList<string> lines) => lines.Count == 0 ? "" : lines[random.Next(lines.Count)];

        int antes = Math.Min(8, CareerRules.AntesLeft(State, World, now));
        int n = 0;
        foreach (var o in Posters(antes))
            threads.Add(new ForumThread($"ante:{seed}:{n++}", ThreadKind.Ante, Pick(forum.AnteTitles), o.Name, o, Body: Pick(forum.AnteBodies)));
        n = 0;
        foreach (var o in Posters(Math.Min(4, regulars.Count)))
            threads.Add(new ForumThread($"practice:{seed}:{n++}", ThreadKind.Practice, Pick(forum.PracticeTitles), o.Name, o, Body: Pick(forum.PracticeBodies)));

        if (CareerMarket.Trading(World, today))
        {
            threads.Add(new ForumThread("newpost", ThreadKind.NewPost, "Sell a card: write a post", Settings.PlayerName));
            if (State.Posts.Count > 0)
                threads.Add(new ForumThread("myposts", ThreadKind.MyPosts, $"Your sale posts ({State.Posts.Count(p => p.Status is SaleStatus.Open or SaleStatus.Offered)} up)", Settings.PlayerName));
            foreach (var listing in CareerRules.Listings(State, World, today))
                threads.Add(new ForumThread($"listing:{today.DayNumber}:{listing.Index}", ThreadKind.Listing, listing.Title, listing.Seller,
                    regulars.FirstOrDefault(o => o.Name == listing.Seller), Body: listing.Body, Listing: listing));
        }
        return threads;
    }

    /// <summary>The same number for the same text in every run (string hash codes change from run to run).</summary>
    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return hash;
    }

    /// <summary>Answering a practice post: the regular says hi, and the game is one click away.</summary>
    private void OpenPracticeChat(string opponentId, DeckList deck)
    {
        _chat.Clear();
        _chatWith = opponentId;
        _chatDeck = deck;
        _chatWant = null;
        _chatPractice = true;
        _chatStep = ChatStep.Agreed;
        _chat.Add(new ChatLine(true, "Saw your post. Still looking for a game?"));
        _chat.Add(new ChatLine(false, Say(World.Content.Forum.PracticeGreetings)));
        _webPage = WebPage.Chat;
        ShowWebPage();
    }

    // ---------------------------------------------------------------- buy & sell

    /// <summary>The haggling under each of today's listings, as it went (kept while the game runs).</summary>
    private static readonly Dictionary<string, List<(bool Mine, string Text)>> _haggling = new();

    /// <summary>The card picked for a new sale post (on the desk), until it's posted.</summary>
    private static CardKey? _saleCard;

    private static string FillMarket(IReadOnlyList<string> lines, string card, int price) =>
        (lines.Count == 0 ? "" : lines[Random.Shared.Next(lines.Count)]).Replace("{card}", card).Replace("{price}", CareerRules.Money(price));

    /// <summary>An amount of money to type in, in dollars with cents.</summary>
    private static SpinBox MoneyBox(int cents, int max)
    {
        var box = new SpinBox { MinValue = 0.01, MaxValue = Math.Max(0.01, max / 100.0), Step = 0.01, Value = cents / 100.0, Prefix = "$", CustomMinimumSize = new Vector2(150, 0) };
        box.GetLineEdit().AddThemeFontSizeOverride("font_size", 20);
        return box;
    }

    private static int Cents(SpinBox box) => (int)Math.Round(box.Value * 100);

    /// <summary>A listing: the seller's post with the card, the haggling so far, and buying it or making an offer.</summary>
    private void ListingPost(VBoxContainer body, CareerListing listing)
    {
        var day = CareerRules.DayOf(Career.Now);
        var key = $"{day.DayNumber}:{listing.Index}";
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 16);
        top.AddChild(SmallCard(listing.Card));
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 6);
        text.AddChild(CareerStyle.Text(listing.Body, 20, CareerStyle.Ink, wrap: true));
        text.AddChild(CareerStyle.Text($"Asking {CareerRules.Money(listing.Price)}{(ArtTag(listing.Card, listing.Art) is { } tag ? $"  ·  {tag}" : "")}", 19, CareerStyle.TitleBar));
        text.AddChild(CareerStyle.Text($"It usually goes for about {CareerRules.Money(CareerMarket.Price(State, World, listing.Card, day))}.", 16, CareerStyle.InkDim, wrap: true));
        top.AddChild(text);
        body.AddChild(top);

        // The replies so far.
        foreach (var (mine, line) in _haggling.GetValueOrDefault(key) ?? new())
            body.AddChild(Reply(mine ? Settings.PlayerName : listing.Seller, line, mine));

        if (CareerRules.ListingBought(State, day, listing.Index))
        {
            body.AddChild(CareerStyle.Text("Sold.", 20, CareerStyle.Good));
            return;
        }
        int price = CareerRules.ListingPrice(State, listing, day);
        int left = CareerRules.OffersLeft(State, World, day, listing.Index);
        var buy = CareerStyle.LinkButton($"Buy it for {CareerRules.Money(price)}", size: 22);
        buy.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        buy.Disabled = State.Cash < price;
        buy.Pressed += () =>
        {
            Try(() =>
            {
                Career.Do(new BuyListing(Career.Now, listing.Index));
                Haggle(key, true, $"I'll take it for {CareerRules.Money(price)}.");
                Haggle(key, false, FillMarket(World.Content.Market.Accepts, listing.Card.Name, price));
                MenuKit.Toast(this, $"{listing.Card.Name} is yours.");
            });
            ShowWebPage();
        };
        body.AddChild(buy);
        if (left == 0)
        {
            body.AddChild(CareerStyle.Text($"{listing.Seller} isn't taking more offers today.", 17, CareerStyle.InkDim));
            return;
        }
        var offerRow = new HBoxContainer();
        offerRow.AddThemeConstantOverride("separation", 10);
        offerRow.AddChild(CareerStyle.Text("Your offer:", 20));
        var amount = MoneyBox((int)Math.Round(price * 0.85), price);
        offerRow.AddChild(amount);
        var send = CareerStyle.LinkButton("Make an offer", size: 20);
        send.Pressed += () =>
        {
            int offered = Cents(amount);
            if (offered > State.Cash) { MenuKit.Toast(this, "You don't have that much."); return; }
            Try(() =>
            {
                int before = CareerRules.ListingPrice(State, listing, day);
                Career.Do(new OfferOnListing(Career.Now, listing.Index, offered));
                var market = World.Content.Market;
                Haggle(key, true, FillMarket(market.BuyerOffers, listing.Card.Name, offered));
                int now = CareerRules.ListingPrice(State, listing, day);
                if (CareerRules.ListingBought(State, day, listing.Index)) Haggle(key, false, FillMarket(market.Accepts, listing.Card.Name, offered));
                else if (now < before) Haggle(key, false, FillMarket(market.Counters, listing.Card.Name, now));
                else Haggle(key, false, FillMarket(market.Declines, listing.Card.Name, offered));
            });
            ShowWebPage();
        };
        offerRow.AddChild(send);
        offerRow.AddChild(CareerStyle.Text($"({left} offer{(left == 1 ? "" : "s")} left today)", 16, CareerStyle.InkDim));
        body.AddChild(offerRow);
    }

    private static void Haggle(string key, bool mine, string text)
    {
        if (!_haggling.TryGetValue(key, out var lines)) _haggling[key] = lines = new();
        lines.Add((mine, text));
    }

    /// <summary>A reply under a post: who wrote it and what (the player's to the right).</summary>
    private static Control Reply(string author, string text, bool mine)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        if (mine) row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var bubble = new PanelContainer();
        bubble.AddThemeStyleboxOverride("panel", CareerStyle.Box(mine ? new Color("dfe9d8") : new Color("efe4c8"), Cardboard, 1, 8, 10));
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 10);
        line.AddChild(AuthorToken(author, 28));
        line.AddChild(CareerStyle.Text($"{author}: {text}", 18, CareerStyle.Ink, wrap: true));
        line.GetChild<Control>(1).CustomMinimumSize = new Vector2(380, 0);
        bubble.AddChild(line);
        row.AddChild(bubble);
        if (!mine) row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        return row;
    }

    /// <summary>The player's sale posts: each card, its price and what became of it, with the buyers' offers to answer.</summary>
    private void MyPostsPost(VBoxContainer body)
    {
        var market = World.Content.Market;
        var day = CareerRules.DayOf(Career.Now);
        body.AddChild(CareerStyle.Text($"Posts stay up {market.PostDays} days. Twice a day (midnight and midday, UTC) a post may sell, get a lower offer, or nothing; the closer the price to what the card goes for, the sooner it sells.", 17, CareerStyle.InkDim, wrap: true));
        foreach (var post in State.Posts.AsEnumerable().Reverse())
        {
            var card = CardKey.Parse(post.Card);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 14);
            row.AddChild(SmallCard(card));
            var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            info.AddThemeConstantOverride("separation", 6);
            info.AddChild(CareerStyle.Text($"{card.Name} for {CareerRules.Money(post.Ask)}", 20));
            int daysLeft = Math.Max(0, market.PostDays - (day.DayNumber - post.Day.DayNumber));
            var (status, color) = post.Status switch
            {
                SaleStatus.Open => ($"Up for {daysLeft} more day{(daysLeft == 1 ? "" : "s")}. Chance to sell each day: {CareerRules.SaleChance(State, World, card, post.Ask, day):0%}.", CareerStyle.InkDim),
                SaleStatus.Offered => ($"A buyer offers {CareerRules.Money(post.Offer)}.", CareerStyle.TitleBar),
                SaleStatus.Sold => ($"Sold for {CareerRules.Money(post.SoldFor)}.", CareerStyle.Good),
                SaleStatus.Expired => ("Nobody bought it; the card is back in your collection.", CareerStyle.InkDim),
                _ => ("Taken down.", CareerStyle.InkDim),
            };
            info.AddChild(CareerStyle.Text(status, 17, color, wrap: true));
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 18);
            void Act(string label, CareerAction action)
            {
                var link = CareerStyle.LinkButton(label, size: 18);
                link.Pressed += () => { Try(() => Career.Do(action)); ShowWebPage(); };
                actions.AddChild(link);
            }
            if (post.Status == SaleStatus.Offered)
            {
                Act($"Accept {CareerRules.Money(post.Offer)}", new AnswerSaleOffer(Career.Now, post.Id, true));
                Act("Decline", new AnswerSaleOffer(Career.Now, post.Id, false));
            }
            if (post.Status is SaleStatus.Open or SaleStatus.Offered) Act("Take it down", new TakeDownSale(Career.Now, post.Id));
            info.AddChild(actions);
            row.AddChild(info);
            body.AddChild(row);
        }
    }

    /// <summary>Writing a sale post: pick a card on the desk, set a price (up to what anyone would pay) and post it.</summary>
    private void NewSalePost(VBoxContainer body)
    {
        var day = CareerRules.DayOf(Career.Now);
        var market = World.Content.Market;
        int up = State.Posts.Count(p => p.Status is SaleStatus.Open or SaleStatus.Offered);
        body.AddChild(CareerStyle.Text($"Post a card for sale: buyers answer over the next days. Selling here is slower than at the counter and not sure to happen, but it can pay more. You have {up} of {market.PostsMax} posts up.", 18, CareerStyle.Ink, wrap: true));
        var pick = CareerStyle.LinkButton(_saleCard is null ? "Choose the card…" : "Choose another card…", size: 20);
        pick.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        pick.Pressed += PickSaleCard;
        if (_saleCard is not { } card || State.Owned(card) < 1)
        {
            _saleCard = null;
            body.AddChild(pick);
            return;
        }
        int price = CareerMarket.Price(State, World, card, day);
        int cap = CareerRules.SaleCap(State, World, card, day);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        row.AddChild(SmallCard(card));
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 8);
        form.AddChild(CareerStyle.Text(card.Name + (ArtTag(card, World.ArtOf(card)) is { } tag ? $"  ·  {tag}" : ""), 20));
        form.AddChild(CareerStyle.Text($"It goes for about {CareerRules.Money(price)}; nobody pays more than {CareerRules.Money(cap)}. The counter would pay {CareerRules.Money(CareerMarket.SellPrice(State, World, card, day))}.", 17, CareerStyle.InkDim, wrap: true));
        var priceRow = new HBoxContainer();
        priceRow.AddThemeConstantOverride("separation", 10);
        priceRow.AddChild(CareerStyle.Text("Your price:", 20));
        var ask = MoneyBox(price, cap);
        priceRow.AddChild(ask);
        var chance = CareerStyle.Text("", 16, CareerStyle.InkDim);
        void ShowChance(double _) => chance.Text = $"about {CareerRules.SaleChance(State, World, card, Cents(ask), day):0%} a day to sell";
        ask.ValueChanged += ShowChance;
        ShowChance(0);
        priceRow.AddChild(chance);
        form.AddChild(priceRow);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 18);
        var post = CareerStyle.LinkButton("Post it", size: 22);
        post.Disabled = up >= market.PostsMax;
        post.Pressed += () =>
        {
            Try(() =>
            {
                Career.Do(new PostSale(Career.Now, card.ToString(), Cents(ask)));
                _saleCard = null;
                _forumThread = "myposts";
                MenuKit.Toast(this, "Posted! Check back over the next days.");
            });
            ShowWebPage();
        };
        actions.AddChild(post);
        actions.AddChild(pick);
        form.AddChild(actions);
        row.AddChild(form);
        body.AddChild(row);
    }

    /// <summary>Picks the card to sell on the desk (every card but basic lands, with what it goes for); back to the post after.</summary>
    private static void PickSaleCard()
    {
        var day = CareerRules.DayOf(Career.Now);
        void Back()
        {
            _forumThread = "newpost";
            OpenOn = nameof(WebPage.Forum);
            App.Instance.GoTo(App.CareerScene);
        }
        DeckDesk.Picking = new DeckDesk.CardPick("Choose the card to sell", Stakes(),
            card => CareerRules.Money(CareerMarket.Price(State, World, card, day)),
            card => { _saleCard = card; Back(); },
            Back,
            NotInDecks());
        App.Instance.GoTo(App.DeckDeskScene);
    }
}
