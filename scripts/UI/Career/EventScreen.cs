// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Arcanum.UI.Menu;
using Godot;

namespace Arcanum.UI.Career;

/// <summary>
/// A calendar event at the store, on its table: drafting (the booster in front of the player, a click takes a card),
/// building the deck on the desk, then the Swiss rounds (the player's match, the standings) and the result with its
/// prizes. Leaving keeps the event; it goes on from the website.
/// </summary>
public partial class EventScreen : Control
{
    private static readonly Vector2 TileSize = new(150, 209);
    private static readonly Color Light = new(1f, 0.95f, 0.85f);

    private readonly VBoxContainer _content = new();
    private readonly Label _title = CareerStyle.Text("", 26, Light);
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private ArtScene _table = null!;

    private static CareerService Career => App.Instance.Career;
    private static LimitedService Service => Career.Events;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = CareerStyle.Notes;
        if (App.Instance.Cards is null || Career.CurrentEvent() is null)
        {
            Callable.From(() => App.Instance.GoTo(App.CareerScene)).CallDeferred();
            return;
        }
        _table = new ArtScene(Career.EventTable(Career.CurrentEvent()?.Spec) is { } table ? GD.Load<Texture2D>(table) : null, Array.Empty<Hotspot>());
        _table.BackRequested += Leave;
        AddChild(_table);
        var top = new HBoxContainer { AnchorRight = 1, OffsetLeft = 20, OffsetTop = 18, OffsetRight = -20 };
        top.AddThemeConstantOverride("separation", 14);
        top.AddChild(CareerStyle.Button("< Home", Leave, 20));
        top.AddChild(CareerStyle.Tag(_title));
        AddChild(top);
        var frame = new MarginContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 40, OffsetTop = 84, OffsetRight = -40, OffsetBottom = -30 };
        frame.AddChild(_content);
        _content.AddThemeConstantOverride("separation", 14);
        AddChild(frame);
        _preview.Size = new Vector2(300, 418);
        AddChild(_preview);
        // Testing: ARCANUM_CAREER_AUTO=draft drafts automatically; =play also builds the deck and starts the rounds; =desk opens the
        // desk once drafted; =collect collects a finished event's prizes.
        if (OS.GetEnvironment("ARCANUM_CAREER_AUTO") == "play" && OS.GetEnvironment("ARCANUM_AUTOPLAY").Length > 0
            && Career.CurrentEvent() is ({ } podEvent, _, null) && Career.Pod()!.Winners.Count < podEvent.Rounds)
        {
            App.Instance.PendingMatch = Career.PodGame();
            Callable.From(() => App.Instance.GoTo(App.GameBoardScene)).CallDeferred();
            return;
        }
        if (OS.GetEnvironment("ARCANUM_CAREER_AUTO") is { Length: > 0 } auto && Career.CurrentEvent() is (_, _, { } running))
        {
            while (running.Stage == EventStage.Drafting) Service.Pick(Service.AutoPickIndex());
            if (auto == "play" && running.Stage == EventStage.Building)
            {
                running.Seats[running.HumanSeat].Deck = Service.AutoBuild(running.Seats[running.HumanSeat].Pool);
                Service.FinishBuilding();
            }
            // With the board's autoplay (ARCANUM_AUTOPLAY) the games play themselves and come back here: the whole event runs.
            if (auto == "play" && running.Stage == EventStage.Playing && running.CurrentMatch is not null && OS.GetEnvironment("ARCANUM_AUTOPLAY").Length > 0)
            {
                Callable.From(Service.PlayNext).CallDeferred();
                return;
            }
            if (auto == "collect" && running.Stage == EventStage.Finished)
            {
                Callable.From(Collect).CallDeferred();
                return;
            }
            if (auto == "desk" && running.Stage == EventStage.Building)
            {
                Show();
                Callable.From(() => App.Instance.GoTo(App.DeckDeskScene)).CallDeferred();
                return;
            }
        }
        Show();
    }

    private static void Leave()
    {
        CareerScreen.OpenOn = nameof(CareerScreen.WebPage.Events);
        App.Instance.GoTo(App.CareerScene);
    }

    private new void Show()
    {
        _preview.Visible = false;
        foreach (var child in _content.GetChildren()) child.QueueFree();
        if (Career.CurrentEvent() is not var (spec, entry, running))
        {
            Leave();
            return;
        }
        if (running is not { } ev)
        {
            ShowPod(spec, entry);
            return;
        }
        // The round's time ran out between games: the match is over with the games played.
        if (Service.EndIfTimeUp()) MenuKit.Toast(this, "Time! The round is over: no new game begins, and the match ends with the games played.");
        switch (ev.Stage)
        {
            case EventStage.Drafting: ShowDraft(spec, entry); break;
            case EventStage.Building: ShowBuilding(spec, ev); break;
            default: ShowRounds(spec, ev); break;
        }
    }

    // ---------------------------------------------------------------- drafting

    private void ShowDraft(EventSpec spec, EventEntry entry)
    {
        var draft = Service.DraftState!;
        _title.Text = $"{spec.Name} · {Career.World!.SetName(entry.Set)} · pack {draft.Round + 1} of {draft.Rounds}, pick {draft.PickInRound + 1}";
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 24);
        _content.AddChild(body);

        // The booster in front of the player, its cards laid on the table.
        var pack = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pack.AddThemeConstantOverride("h_separation", 12);
        pack.AddThemeConstantOverride("v_separation", 12);
        var ratings = DraftGrades.ForPlayer(Service);
        for (int i = 0; i < draft.Pack.Count; i++)
        {
            int index = i;
            var card = Tile(draft.Pack[i], pack);
            card.TooltipText = "Take this card";
            if (ratings is not null) DraftGrades.Mark(card.GetParent<Control>(), card, ratings, draft.Pack[i].Name);
            card.Clicked += _ =>
            {
                Service.Pick(index);
                Show();
            };
        }
        body.AddChild(pack);

        // The picks so far, on a note.
        var note = Note(new Vector2(360, 0));
        var list = (VBoxContainer)note.GetChild(0);
        list.AddChild(CareerStyle.Text($"Your picks ({draft.Picks.Count})", 24, CareerStyle.PaperEdge));
        list.AddChild(CareerStyle.Text("Click a card on the table to take it; the rest of the booster goes to the next player.", 17, CareerStyle.InkDim, wrap: true));
        if (ratings is not null) list.AddChild(DraftGrades.Credit(ratings, CareerStyle.InkDim));
        var picks = new VBoxContainer();
        foreach (var card in draft.Picks.AsEnumerable().Reverse())
        {
            var row = CareerStyle.Text(card.Name, 18, CareerStyle.Ink);
            row.MouseFilter = MouseFilterEnum.Pass;
            var def = Definition(card);
            row.MouseEntered += () => ShowPreview(def, row);
            row.MouseExited += () => _preview.Visible = false;
            picks.AddChild(row);
        }
        list.AddChild(Scrolled(picks));
        body.AddChild(note);
    }

    // ---------------------------------------------------------------- building

    private void ShowBuilding(EventSpec spec, LimitedEvent ev)
    {
        _title.Text = $"{spec.Name} · build your deck";
        var seat = ev.Seats[ev.HumanSeat];
        seat.Deck ??= Service.AutoBuild(seat.Pool);
        // Straight to the desk: the drafted cards on the mat.
        DeckDesk.Building = new DeckDesk.PoolBuild(seat.Pool, seat.Deck, Problems, () => Service.AutoBuild(seat.Pool),
            deck => { seat.Deck = deck; Service.DeckEdited(); App.Instance.GoTo(App.EventScene); },
            deck =>
            {
                seat.Deck = deck;
                Service.FinishBuilding();
                App.Instance.GoTo(App.EventScene);
            }) { Ratings = DraftGrades.ForPlayer(Service) };
        var note = Note(new Vector2(560, 0));
        note.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        var box = (VBoxContainer)note.GetChild(0);
        box.AddChild(CareerStyle.Text("The draft is over", 30, CareerStyle.PaperEdge));
        box.AddChild(CareerStyle.Text($"You drafted {seat.Pool.Count} cards. Build a deck of 40 or more on your desk; basic lands are free.", 20, CareerStyle.Ink, wrap: true));
        box.AddChild(CareerStyle.Button("Build the deck", () => App.Instance.GoTo(App.DeckDeskScene), 24, primary: true));
        _content.AddChild(note);

        List<string> Problems(DeckList deck) =>
            DeckValidator.ValidateLimited(deck, LimitedService.Format, seat.Pool, Cards).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();
    }

    // ---------------------------------------------------------------- rounds

    private void ShowRounds(EventSpec spec, LimitedEvent ev)
    {
        int me = ev.HumanSeat;
        bool finished = ev.Stage == EventStage.Finished;
        _title.Text = finished ? $"{spec.Name} · final standings" : $"{spec.Name} · {RoundName(ev, ev.Rounds.Count - 1)}";
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 24);
        _content.AddChild(body);

        var main = Note(new Vector2(0, 0));
        main.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        main.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        var box = (VBoxContainer)main.GetChild(0);
        var (wins, losses, draws) = CareerService.Record(ev);
        if (finished)
        {
            int place = ev.Standings().IndexOf(me) + 1;
            box.AddChild(CareerStyle.Text($"You finished {CareerRules.Ordinal(place)} of {ev.Seats.Count}, {wins}-{losses}{(draws > 0 ? $"-{draws}" : "")}.", 30, CareerStyle.PaperEdge));
            if (MissedCut(ev, me))
                box.AddChild(CareerStyle.Text($"You missed the cut after round {ev.CutAfter} ({ev.CutPoints} points were needed to play on).", 20, CareerStyle.InkDim, wrap: true));
            else if (ev.Dropped.Contains(me))
                box.AddChild(CareerStyle.Text("You dropped from the event; the others played it out.", 20, CareerStyle.InkDim, wrap: true));
            var prize = CareerRules.PrizeFor(spec, wins, place, players: ev.Seats.Count);
            box.AddChild(CareerStyle.Text(PrizeText(prize, spec) + (spec.Kind == CareerRules.Draft ? " You keep the cards you drafted." : ""), 22, CareerStyle.Ink, wrap: true));
            var collect = CareerStyle.Button("Collect and go home", Collect, 24, primary: true);
            collect.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            box.AddChild(collect);
        }
        else if (ev.CurrentMatch is { } match)
        {
            int opponent = match.Opponent(me)!.Value;
            box.AddChild(CareerStyle.Text($"Your match: {ev.Seats[opponent].Name}", 30, CareerStyle.PaperEdge));
            box.AddChild(CareerStyle.Text($"Games {match.WinsOf(me)}-{match.WinsOf(opponent)}{(ev.BestOf > 1 ? $" · best of {ev.BestOf}" : "")} · your record {wins}-{losses}{(draws > 0 ? $"-{draws}" : "")}", 20, CareerStyle.InkDim));
            if (spec.Days > 1)
            {
                var last = CareerRules.LastDay(spec, Career.State.Event!.Start);
                box.AddChild(CareerStyle.Text($"{ev.Seats.Count} players. You can go home and come back: the rounds run until the end of {last:dddd, MMMM d} (UTC). "
                    + "A round not played by then is lost and you are dropped.", 17, CareerStyle.InkDim, wrap: true));
            }
            if (ev.Deadline(match) is { } deadline)
            {
                var clock = CareerStyle.Text("", 20, CareerStyle.Ink);
                void Tick()
                {
                    var left = deadline - DateTime.UtcNow;
                    clock.Text = left > TimeSpan.Zero ? $"Round time left: {(int)left.TotalMinutes:00}:{left.Seconds:00}" : "Time!";
                    if (left <= TimeSpan.Zero && IsInstanceValid(this)) Callable.From(Show).CallDeferred();
                }
                Tick();
                var timer = new Godot.Timer { WaitTime = 1, Autostart = true };
                timer.Timeout += Tick;
                clock.AddChild(timer);
                box.AddChild(clock);
            }
            else if (ev.RoundMinutes > 0 && !ev.IsPlayoff(ev.Rounds.Count - 1))
                box.AddChild(CareerStyle.Text($"Rounds are {ev.RoundMinutes} minutes: the clock starts with your first game.", 17, CareerStyle.InkDim, wrap: true));
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 12);
            actions.AddChild(CareerStyle.Button($"Play game {match.WinsA + match.WinsB + match.Draws + 1}", Service.PlayNext, 24, primary: true));
            if (spec.Kind == CareerRules.Draft)
                actions.AddChild(CareerStyle.Button("Edit deck", () =>
                {
                    var seat = ev.Seats[me];
                    DeckDesk.Building = new DeckDesk.PoolBuild(seat.Pool, seat.Deck!, deck => DeckValidator.ValidateLimited(deck, LimitedService.Format, seat.Pool, Cards)
                            .Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList(), () => Service.AutoBuild(seat.Pool),
                        deck => { seat.Deck = deck; Service.DeckEdited(); App.Instance.GoTo(App.EventScene); },
                        deck => { seat.Deck = deck; Service.DeckEdited(); App.Instance.GoTo(App.EventScene); }) { Ratings = DraftGrades.ForPlayer(Service) };
                    App.Instance.GoTo(App.DeckDeskScene);
                }, 20));
            actions.AddChild(CareerStyle.Button("Drop out", () => MenuKit.Modal(this, "Drop out?", "You concede this match and leave the event. The others play it out, and you keep your place in the final standings.", null,
                ("Keep playing", () => { }), ("Drop", () =>
                {
                    Career.FinishEvent(drop: true);
                    Leave();
                })), 20));
            box.AddChild(actions);
        }
        else box.AddChild(CareerStyle.Text("Waiting for the round's other matches…", 22, CareerStyle.Ink));

        // The rounds so far (in a big event, the player's own matches and the playoff's).
        var rounds = new VBoxContainer();
        bool big = ev.Seats.Count > 16;
        for (int r = ev.Rounds.Count - 1; r >= 0; r--)
        {
            var shown = ev.Rounds[r].Where(m => !big || ev.IsPlayoff(r) || m.Involves(me)).ToList();
            if (shown.Count == 0) continue;
            rounds.AddChild(CareerStyle.Text(RoundName(ev, r), 19, CareerStyle.PaperEdge));
            foreach (var m in shown)
            {
                var text = m.SeatB is not { } b ? $"{ev.Seats[m.SeatA].Name} has a bye" : $"{ev.Seats[m.SeatA].Name} {m.WinsA}-{m.WinsB} {ev.Seats[b].Name}{(m.Done ? "" : " (playing)")}";
                rounds.AddChild(CareerStyle.Text(text, 17, m.Involves(me) ? CareerStyle.Ink : CareerStyle.InkDim));
            }
        }
        box.AddChild(new HSeparator());
        box.AddChild(Scrolled(rounds, 360));
        body.AddChild(main);

        // Standings.
        var standings = Note(new Vector2(420, 0));
        standings.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        var table = (VBoxContainer)standings.GetChild(0);
        table.AddChild(CareerStyle.Text("Standings", 26, CareerStyle.PaperEdge));
        // The top of the standings, and the player's own place below it when it is further down.
        var order = ev.Standings();
        int myRank = order.IndexOf(me);
        for (int rank = 0; rank < order.Count; rank++)
        {
            if (rank >= 16 && rank != myRank) continue;
            if (rank >= 16 && rank == myRank) table.AddChild(CareerStyle.Text("…", 19, CareerStyle.InkDim));
            int s = order[rank];
            var row = new HBoxContainer();
            var name = CareerStyle.Text($"{rank + 1}. {ev.Seats[s].Name}", 19, s == me ? CareerStyle.Good : ev.Dropped.Contains(s) ? CareerStyle.InkDim : CareerStyle.Ink);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(CareerStyle.Text($"{ev.Points(s)} pts", 19, CareerStyle.InkDim));
            table.AddChild(row);
        }
        if (ev.CutAfter > 0 && ev.Rounds.Count <= ev.CutAfter)
            table.AddChild(CareerStyle.Text($"After round {ev.CutAfter}, only players with {ev.CutPoints} points or more play on.", 16, CareerStyle.InkDim, wrap: true));
        if (ev.TopCut > 0 && ev.Rounds.Count <= ev.RoundsTotal)
            table.AddChild(CareerStyle.Text($"After round {ev.RoundsTotal}, the top {ev.TopCut} play a single-elimination playoff.", 16, CareerStyle.InkDim, wrap: true));
        body.AddChild(standings);
    }

    /// <summary>A round's name: "Round 3 of 6", or the playoff's quarterfinals, semifinals and final.</summary>
    private static string RoundName(LimitedEvent ev, int round)
    {
        if (!ev.IsPlayoff(round)) return $"Round {round + 1} of {ev.RoundsTotal}";
        return (ev.PlayoffRounds - (round - ev.RoundsTotal)) switch
        {
            1 => "Final", 2 => "Semifinals", 3 => "Quarterfinals", var n => $"Top {1 << n}",
        };
    }

    private static bool MissedCut(LimitedEvent ev, int seat) =>
        ev.CutAfter > 0 && ev.Dropped.Contains(seat) && ev.Rounds.Count > ev.CutAfter && ev.Points(seat) < ev.CutPoints
        && !ev.Rounds.Skip(ev.CutAfter).Any(r => r.Any(m => m.Involves(seat)));

    private void Collect()
    {
        try { Career.FinishEvent(); }
        catch (CareerRuleException e) { MenuKit.Toast(this, e.Message); return; }
        CareerScreen.OpenOn = nameof(CareerScreen.WebPage.Cards);
        App.Instance.GoTo(App.CareerScene);
    }

    // ---------------------------------------------------------------- Commander pods

    private void ShowPod(EventSpec spec, EventEntry entry)
    {
        var pod = Career.Pod()!;
        int played = pod.Winners.Count;
        bool finished = played >= spec.Rounds;
        _title.Text = finished ? $"{spec.Name} · the night is over" : $"{spec.Name} · game {played + 1} of {spec.Rounds}";
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 24);
        _content.AddChild(body);

        var main = Note(Vector2.Zero);
        main.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        main.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        var box = (VBoxContainer)main.GetChild(0);
        int mine = pod.Winners.Count(w => w == 0);
        if (finished)
        {
            int place = 1 + Enumerable.Range(1, pod.Names.Count - 1).Count(s => pod.Winners.Count(w => w == s) > mine);
            box.AddChild(CareerStyle.Text($"You won {mine} of {spec.Rounds} games.", 30, CareerStyle.PaperEdge));
            box.AddChild(CareerStyle.Text(PrizeText(CareerRules.PrizeFor(spec, mine, place), spec), 22, CareerStyle.Ink, wrap: true));
            var collect = CareerStyle.Button("Collect and go home", Collect, 24, primary: true);
            collect.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            box.AddChild(collect);
        }
        else
        {
            box.AddChild(CareerStyle.Text("Your pod", 30, CareerStyle.PaperEdge));
            box.AddChild(CareerStyle.Text($"Four players, {Career.World!.CommanderFormat.StartingLife} life, the last one standing wins the game. {spec.Rounds} games tonight.", 20, CareerStyle.InkDim, wrap: true));
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 12);
            actions.AddChild(CareerStyle.Button($"Play game {played + 1}", () =>
            {
                App.Instance.PendingMatch = Career.PodGame();
                App.Instance.GoTo(App.GameBoardScene);
            }, 24, primary: true));
            actions.AddChild(CareerStyle.Button("Go home early", () => MenuKit.Modal(this, "Go home early?", "The night ends for you with the games played so far.", null,
                ("Stay", () => { }), ("Go home", () =>
                {
                    Career.FinishEvent(drop: true);
                    Leave();
                })), 20));
            box.AddChild(actions);
        }
        for (int g = 0; g < played; g++)
            box.AddChild(CareerStyle.Text($"Game {g + 1}: {(pod.Winners[g] < 0 ? "a draw" : pod.Names[pod.Winners[g]] + " won")}", 19, pod.Winners[g] == 0 ? CareerStyle.Good : CareerStyle.Ink));
        body.AddChild(main);

        // The players and their commanders.
        var players = Note(new Vector2(460, 0));
        players.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        var list = (VBoxContainer)players.GetChild(0);
        list.AddChild(CareerStyle.Text("At the table", 26, CareerStyle.PaperEdge));
        for (int i = 0; i < pod.Names.Count; i++)
        {
            var deck = i == 0 ? DeckList.Parse(entry.Deck) : Career.World!.Deck(pod.Decks[i]);
            var commander = string.Join(" & ", deck.Commander.Select(c => c.Name));
            list.AddChild(CareerStyle.Text($"{pod.Names[i]}: {(commander.Length > 0 ? commander : "no commander")} · {pod.Winners.Count(w => w == i)} won", 19, i == 0 ? CareerStyle.Good : CareerStyle.Ink, wrap: true));
        }
        body.AddChild(players);
    }

    private static string PrizeText(PrizeSpec? prize, EventSpec spec)
    {
        if (prize is null) return "No prize this time.";
        var parts = new List<string>();
        if (prize.Cash > 0) parts.Add(CareerRules.Money(prize.Cash));
        if (prize.Boosters > 0) parts.Add($"{prize.Boosters} booster{(prize.Boosters == 1 ? "" : "s")}");
        if (prize.Tokens > 0) parts.Add($"{prize.Tokens} qualification token{(prize.Tokens == 1 ? "" : "s")}");
        if (prize.Qualifies.Length > 0) parts.Add($"an invitation to {Career.World!.Event(prize.Qualifies, CareerRules.DayOf(Career.Now))?.Name ?? prize.Qualifies}");
        if (prize.Reputation > 0) parts.Add($"{prize.Reputation} reputation");
        return parts.Count == 0 ? "No prize this time." : $"Your prize: {string.Join(", ", parts)}.";
    }

    // ---------------------------------------------------------------- helpers

    private static PanelContainer Note(Vector2 minimum)
    {
        var note = new PanelContainer { CustomMinimumSize = minimum };
        note.AddThemeStyleboxOverride("panel", CareerStyle.Box(CareerStyle.Paper, CareerStyle.PaperEdge, 4, 6, 20));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        note.AddChild(box);
        return note;
    }

    private CardNode Tile(PoolCard card, Control parent)
    {
        var holder = new Control { CustomMinimumSize = TileSize };
        var node = new CardNode { Size = TileSize };
        holder.AddChild(node);
        var def = Definition(card);
        node.Setup(ViewOf(def), showCostPips: false);
        node.HoverStarted += _ => ShowPreview(def, holder);
        node.HoverEnded += _ => _preview.Visible = false;
        parent.AddChild(holder);
        return node;
    }

    private static Arcanum.Engine.Cards.CardDefinition Definition(PoolCard card) =>
        DeckList.TryResolve(Cards, card.ToEntry(), out var d) ? d : Cards.Find(card.Name)!.Definition;

    private static CardView ViewOf(Arcanum.Engine.Cards.CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey, Foil = d.Foil, Surge = d.Surge, Fracture = d.Fracture,
    };

    private void ShowPreview(Arcanum.Engine.Cards.CardDefinition definition, Control anchor)
    {
        _preview.Setup(ViewOf(definition), false);
        var rect = anchor.GetGlobalRect();
        float x = rect.End.X + 16 + _preview.Size.X < GetViewportRect().Size.X ? rect.End.X + 16 : rect.Position.X - _preview.Size.X - 16;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 100, 20, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    private static ScrollContainer Scrolled(Control inner, float height = 0)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, height),
        };
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inner);
        return scroll;
    }
}
