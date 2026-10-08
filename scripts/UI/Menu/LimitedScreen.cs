// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Draft and sealed: start an event, draft against computer players, build a 40-card deck from the pool, then play
/// Swiss rounds and see the standings. The event is saved after every step and can be resumed.
/// </summary>
public partial class LimitedScreen : Control
{
    private static readonly Vector2 TileSize = new(150, 210);

    private readonly VBoxContainer _content = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private Label _title = null!;
    private int _selected = -1;
    private bool _editingDuringEvent;

    // New-event choices.
    private LimitedMode _mode = LimitedMode.Draft;
    private int _sourceIndex, _seatsIndex = 6, _bestOfIndex;

    private static LimitedService Service => App.Instance.Limited;

    /// <summary>The event shown: an online one this device takes part in, or the local one.</summary>
    private static ILimitedSession Session => (ILimitedSession?)App.Instance.Online.EventSession ?? App.Instance.Limited;

    private readonly Label _timer = BoardStyle.MakeLabel("", 22, BoardStyle.Attacking, bold: true);
    private ILimitedSession? _watched;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override async void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        var header = MenuKit.AddHeader(this, "Limited");
        _title = header.GetChild<Label>(1);
        header.AddChild(_timer);
        _abandon.CustomMinimumSize = new Vector2(170, 44);
        _abandon.ZIndex = 500; // stays usable over a booster being opened
        _abandon.Pressed += AskAbandon;
        header.AddChild(_abandon);
        var frame = new MarginContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = 92, OffsetRight = -24, OffsetBottom = -20 };
        frame.AddChild(_content);
        AddChild(frame);
        _preview.Size = new Vector2(300, 418);
        AddChild(_preview);

        var loading = MenuKit.Hint(App.Instance.ContentStatus);
        _content.AddChild(loading);
        if (!await App.Instance.ContentReady)
        {
            loading.Text = "Card data isn't available: install a content module to play limited.";
            return;
        }
        if (!Session.IsOnline) Service.Load();
        _watched = Session;
        _watched.Changed += OnSessionChanged;
        Show();
        AutoPlay();
    }

    public override void _ExitTree()
    {
        if (_watched is not null) _watched.Changed -= OnSessionChanged;
    }

    private bool _showQueued;

    /// <summary>Online updates: redraw once per frame at most.</summary>
    private void OnSessionChanged()
    {
        if (_showQueued) return;
        _showQueued = true;
        Callable.From(() =>
        {
            _showQueued = false;
            if (IsInsideTree()) Show();
            if (_autoOnline) AutoOnline();
        }).CallDeferred();
    }

    public override void _Process(double delta)
    {
        int left = Session.SecondsLeft;
        _timer.Text = left >= 0 ? $"⏱ {left / 60}:{left % 60:00}" : "";
    }

    private static readonly bool _autoOnline = OS.GetEnvironment("ARCANUM_AUTOPLAY") == "1";

    /// <summary>
    /// Set when the player comes in from the main menu: an event left unfinished on this device is offered to be
    /// continued or abandoned before going on with it (not when coming back from one of its games).
    /// </summary>
    public static bool OfferToResume { get; set; }

    private readonly Button _abandon = BoardStyle.MakeButton("Abandon event", 14);
    private BoosterOpening? _opening;

    /// <summary>Abandons the event (online: leaves it) at whatever stage it is, after asking.</summary>
    private void AskAbandon()
    {
        Ask(Session.IsOnline ? "Leave this event? The computer plays for you from now on." : "Abandon this event? It can't be resumed.", () =>
        {
            if (IsInstanceValid(_opening)) _opening!.QueueFree();
            Session.Abandon();
            OfferToResume = false;
            Show();
        });
    }

    /// <summary>An event left unfinished: what it is and how far it got, to continue it or abandon it for a new one.</summary>
    private void ShowResume(LimitedEvent ev)
    {
        _title.Text = "Event in progress";
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        box.AddThemeConstantOverride("separation", 14);
        var source = Service.Sources().FirstOrDefault(s => s.Id == ev.Source)?.Name ?? ev.Source;
        box.AddChild(MenuKit.SectionTitle($"{(ev.Mode == LimitedMode.Draft ? "Draft" : "Sealed")} · {source}"));
        box.AddChild(BoardStyle.MakeLabel($"{ev.Seats.Count} players · best of {ev.BestOf}", 16, BoardStyle.TextDim));
        box.AddChild(BoardStyle.MakeLabel(Progress(ev), 18));
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 12);
        var abandon = BoardStyle.MakeButton("Abandon and start a new one", 16);
        abandon.CustomMinimumSize = new Vector2(280, 52);
        abandon.Pressed += AskAbandon;
        buttons.AddChild(abandon);
        var resume = BoardStyle.MakePrimaryButton("Continue", 20);
        resume.CustomMinimumSize = new Vector2(220, 52);
        resume.Pressed += () => { OfferToResume = false; Show(); };
        buttons.AddChild(resume);
        box.AddChild(buttons);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        center.AddChild(MenuKit.Card(box, 24));
        _content.AddChild(center);
        resume.CallDeferred(Control.MethodName.GrabFocus);
    }

    private string Progress(LimitedEvent ev) => ev.Stage switch
    {
        EventStage.Drafting when Session.DraftState is { } d => $"Drafting: pack {d.Round + 1} of {d.Rounds}, pick {d.PickInRound + 1}",
        EventStage.Drafting => "Drafting",
        EventStage.Building => "Building your deck",
        EventStage.Playing => $"Playing: round {ev.Rounds.Count} of {ev.RoundsTotal}, your record {Record(ev, Session.Seat)}",
        _ => $"Finished: your record {Record(ev, Session.Seat)}",
    };

    /// <summary>Automatic play of an online event (smoke tests): first card, automatic deck, always ready.</summary>
    private void AutoOnline()
    {
        var session = Session;
        if (!session.IsOnline || session.Current is not { } ev) return;
        GD.Print($"EVENT {ev.Stage} draft={session.DraftState?.Round}/{session.DraftState?.PickInRound} picked={session.DraftState?.Picked} "
                 + $"round={ev.Rounds.Count} status={session.Status} game={session.GameInProgress} waiting={session.WaitingForOpponent}");
        if (session.DraftState is { Picked: false, Pack.Count: > 0 }) session.Pick(0);
        var seat = ev.Seats[session.Seat];
        if (ev.Stage == EventStage.Building && session.Status is null)
        {
            seat.Deck = session.AutoBuild(seat.Pool);
            session.FinishBuilding();
        }
        if (ev.Stage == EventStage.Playing && !session.GameInProgress && !session.WaitingForOpponent && ev.CurrentMatch is not null) session.PlayNext();
        if (ev.Stage == EventStage.Finished)
            foreach (var s in ev.Standings()) GD.Print($"LIMITED standing {ev.Seats[s].Name} {Record(ev, s)} {ev.Points(s)} pts");
    }

    /// <summary>
    /// ARCANUM_LIMITED=draft|sealed runs a whole event without input (smoke tests): picks like a computer player,
    /// builds the deck automatically and plays every game with ARCANUM_AUTOPLAY.
    /// </summary>
    private void AutoPlay()
    {
        var mode = OS.GetEnvironment("ARCANUM_LIMITED");
        if (Session.IsOnline)
        {
            if (_autoOnline) AutoOnline();
            return;
        }
        if (mode.Length == 0) return;
        if (Service.Current is null)
        {
            // ARCANUM_LIMITED_SOURCE picks the source whose id contains it ("cube", "set:abc").
            var wanted = OS.GetEnvironment("ARCANUM_LIMITED_SOURCE");
            var source = Service.Sources().FirstOrDefault(s => wanted.Length == 0 || s.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (source is null) { GD.PrintErr("LIMITED no source"); GetTree().Quit(1); return; }
            var error = Service.Start(mode == "sealed" ? LimitedMode.Sealed : LimitedMode.Draft, source, 8, 1, "Player 1");
            if (error is not null) { GD.PrintErr("LIMITED " + error); GetTree().Quit(1); return; }
            GD.Print($"LIMITED started {mode} with {source.Name}");
        }
        var ev = Service.Current!;
        while (ev.Stage == EventStage.Drafting) Service.HumanPick(Service.AutoPickIndex());
        if (ev.Stage == EventStage.Building)
        {
            var seat = ev.Seats[ev.HumanSeat];
            GD.Print($"LIMITED pool {seat.Pool.Count} cards");
            seat.Deck = Service.AutoBuild(seat.Pool);
            var errors = Service.Validate(seat).Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (errors.Count > 0) { GD.PrintErr("LIMITED deck invalid: " + errors[0].Message); GetTree().Quit(1); return; }
            GD.Print($"LIMITED deck {seat.Deck.Main.Sum(e => e.Count)} cards ({DeckColors(seat.Deck)})");
            Service.StartPlaying();
        }
        if (ev.Stage == EventStage.Finished)
        {
            foreach (var s in ev.Standings()) GD.Print($"LIMITED standing {ev.Seats[s].Name} {Record(ev, s)} {ev.Points(s)} pts");
            Service.Abandon();
            GetTree().Quit();
            return;
        }
        GD.Print($"LIMITED round {ev.Rounds.Count} game");
        App.Instance.PendingMatch = Service.NextGame();
        App.Instance.GoTo(App.GameBoardScene);
    }

    /// <summary>Shows the part of the event that comes next.</summary>
    private new void Show()
    {
        foreach (var child in _content.GetChildren()) child.QueueFree();
        _preview.Visible = false;
        var ev = Session.Current;
        bool resuming = OfferToResume && ev is not null && !Session.IsOnline;
        _abandon.Visible = ev is not null && ev.Stage != EventStage.Finished && !resuming;
        _abandon.Text = Session.IsOnline ? "Leave event" : "Abandon event";
        if (resuming)
        {
            ShowResume(ev!);
            return;
        }
        OfferToResume = false;
        switch (ev?.Stage)
        {
            case null when Session.IsOnline:
                _title.Text = "Online event";
                _content.AddChild(MenuKit.Hint(Session.Status ?? "Waiting for the host…"));
                break;
            case null:
                ShowSetup();
                break;
            case EventStage.Drafting:
                ShowDraft();
                break;
            case EventStage.Building:
                ShowBuilder(finishLabel: Session.IsOnline ? "Deck done" : "Start playing", onFinish: () => { Session.FinishBuilding(); Show(); });
                break;
            case EventStage.Playing when _editingDuringEvent:
                ShowBuilder(finishLabel: "Done", onFinish: () => { _editingDuringEvent = false; Session.DeckEdited(); Show(); });
                break;
            case EventStage.Playing:
            case EventStage.Finished:
                ShowRounds();
                break;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static CardDefinition DefinitionOf(PoolCard card) =>
        Cards.TryGet(card.Name, card.Set.Length > 0 ? card.Set : null, card.Number.Length > 0 ? card.Number : null, out var d) ? d : Cards.Find(card.Name)!.Definition;

    private static CardDefinition DefinitionOf(DeckEntry entry) =>
        Cards.TryGet(entry.Name, entry.Set, entry.Number, out var d) ? d : Cards.Find(entry.Name)!.Definition;

    private static CardView ViewOf(CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey,
    };

    private CardNode Tile(CardDefinition definition, Control parent)
    {
        var holder = new Control { CustomMinimumSize = TileSize };
        var card = new CardNode { Size = TileSize };
        holder.AddChild(card);
        card.Setup(ViewOf(definition), showCostPips: false);
        card.HoverStarted += c => ShowPreview(c, holder);
        card.HoverEnded += _ => _preview.Visible = false;
        parent.AddChild(holder);
        return card;
    }

    private void ShowPreview(CardNode card, Control anchor)
    {
        if (card.View is not { } view) return;
        _preview.Setup(view, showCostPips: false);
        var rect = anchor.GetGlobalRect();
        var screen = GetViewportRect().Size;
        float x = rect.End.X + 12 + _preview.Size.X < screen.X ? rect.End.X + 12 : rect.Position.X - _preview.Size.X - 12;
        _preview.Position = new Vector2(x, Math.Clamp(rect.Position.Y - 60, 90, screen.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    /// <summary>The card large, beside a list row (lists sit on the right, so the preview goes to their left).</summary>
    private void ShowPreviewLeftOf(CardDefinition definition, Control row)
    {
        _preview.Setup(ViewOf(definition), false);
        var rect = row.GetGlobalRect();
        _preview.Position = new Vector2(rect.Position.X - _preview.Size.X - 16, Math.Clamp(rect.Position.Y - 120, 90, GetViewportRect().Size.Y - _preview.Size.Y - 20));
        _preview.Visible = true;
    }

    private static ScrollContainer Scroll(Control inner)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inner);
        return scroll;
    }

    private static string ColorGroup(CardDefinition d)
    {
        if (d.Is(CardType.Land)) return "Lands";
        var colors = d.ColorList;
        return colors.Count == 0 ? "Colorless" : colors.Count > 1 ? "Multicolor" : colors[0] switch
        {
            "W" => "White", "U" => "Blue", "B" => "Black", "R" => "Red", "G" => "Green", _ => "Other",
        };
    }

    private static readonly string[] GroupOrder = { "White", "Blue", "Black", "Red", "Green", "Multicolor", "Colorless", "Lands", "Other" };

    private static string DeckColors(DeckList? deck)
    {
        if (deck is null) return "";
        var colors = deck.Main.Select(e => Cards.Find(e.Name)?.Definition).OfType<CardDefinition>().Where(d => !d.Is(CardType.Land))
            .SelectMany(d => d.ColorList).Distinct().OrderBy(c => "WUBRG".IndexOf(c, StringComparison.Ordinal));
        return string.Concat(colors);
    }

    // ---------------------------------------------------------------- setup

    private void ShowSetup()
    {
        _title.Text = "Limited";
        var sources = Service.Sources();
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 18);
        _content.AddChild(Scroll(column));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        box.AddChild(MenuKit.SectionTitle("New event"));

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 12);
        foreach (var (mode, label, tip) in new[]
                 {
                     (LimitedMode.Draft, "Draft", "Open boosters with computer players and pass them around, taking one card at a time"),
                     (LimitedMode.Sealed, "Sealed", "Open six boosters and build a deck from what you get"),
                 })
        {
            var button = BoardStyle.MakeButton(label, 18);
            button.CustomMinimumSize = new Vector2(180, 48);
            button.TooltipText = tip;
            BoardStyle.StyleChoice(button, mode == _mode);
            button.Pressed += () => { _mode = mode; Show(); };
            modes.AddChild(button);
        }
        box.AddChild(modes);

        if (sources.Count == 0)
        {
            box.AddChild(MenuKit.Hint("No set with boosters or cube is available. Card data with printings is needed (it downloads on first start)."));
        }
        else
        {
            var sourcePick = MenuKit.Options(sources.Select(s => s.Name), Math.Min(_sourceIndex, sources.Count - 1));
            sourcePick.ItemSelected += i => _sourceIndex = (int)i;
            box.AddChild(MenuKit.Row("Boosters", sourcePick, 180));
            var seats = MenuKit.Options(Enumerable.Range(2, 7).Select(n => $"{n} players"), _seatsIndex);
            seats.ItemSelected += i => _seatsIndex = (int)i;
            box.AddChild(MenuKit.Row(_mode == LimitedMode.Draft ? "Draft table" : "Players", seats, 180));
            var bestOf = MenuKit.Options(new[] { "Best of one", "Best of three" }, _bestOfIndex);
            bestOf.ItemSelected += i => _bestOfIndex = (int)i;
            box.AddChild(MenuKit.Row("Matches", bestOf, 180));
            box.AddChild(MenuKit.Hint(_mode == LimitedMode.Draft
                ? "Everyone opens a booster, takes a card and passes the rest: left, then right, then left. Then build a deck of 40 or more cards and play three Swiss rounds against the computer players at your table."
                : "Open your boosters, build a deck of 40 or more cards from them plus any basic lands, and play three Swiss rounds."));

            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 12);
            var start = BoardStyle.MakePrimaryButton("Start", 20);
            start.CustomMinimumSize = new Vector2(200, 52);
            start.Pressed += () =>
            {
                var error = Service.Start(_mode, sources[Math.Min(_sourceIndex, sources.Count - 1)], _seatsIndex + 2, _bestOfIndex == 0 ? 1 : 3,
                    Settings.Current.PlayerNames.ElementAtOrDefault(0) ?? "Player 1");
                if (error is not null) MenuKit.Toast(this, error);
                Show();
            };
            actions.AddChild(start);
            var cube = BoardStyle.MakeButton("Add a cube…", 16);
            cube.CustomMinimumSize = new Vector2(160, 52);
            cube.Pressed += AskCube;
            actions.AddChild(cube);
            box.AddChild(actions);
        }
        column.AddChild(MenuKit.Card(box));
    }

    private void AskCube()
    {
        var dialog = new AcceptDialog { Title = "Add a cube", OkButtonText = "Save cube", Size = new Vector2I(720, 600) };
        var box = new VBoxContainer();
        var name = MenuKit.TextField("", "Cube name");
        box.AddChild(name);
        box.AddChild(MenuKit.Hint("Paste the cube's card list: one \"1 Card Name\" per line (a set code and collector number choose the printing). Boosters of 15 cards are dealt from it."));
        var text = new TextEdit { CustomMinimumSize = new Vector2(680, 420) };
        box.AddChild(text);
        dialog.AddChild(box);
        dialog.Confirmed += () =>
        {
            try
            {
                DeckList.Parse(text.Text);
                Service.SaveUserCube(name.Text.Trim().Length > 0 ? name.Text.Trim() : "My cube", text.Text);
                MenuKit.Toast(this, "Cube saved");
                Show();
            }
            catch (FormatException e) { MenuKit.Toast(this, e.Message); }
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    // ---------------------------------------------------------------- draft

    private void ShowDraft()
    {
        var draft = Session.DraftState!;
        _title.Text = $"Draft · pack {draft.Round + 1} of {draft.Rounds} · pick {draft.PickInRound + 1} · passing {(draft.PassesLeft ? "left" : "right")}";
        _selected = -1;

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        body.AddChild(left);
        left.AddChild(MenuKit.Hint(draft.Picked ? Session.Status ?? "Waiting for the other players…"
            : "Click a card to select it, then take it (or double-click). The other players pick at the same time."
              + (Session.IsOnline ? " When time runs out, a card is taken for you." : "")));
        var grid = new HFlowContainer();
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        left.AddChild(Scroll(grid));
        var take = BoardStyle.MakePrimaryButton("Take card", 20);
        take.CustomMinimumSize = new Vector2(220, 52);
        take.Disabled = true;
        take.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        left.AddChild(take);

        var nodes = new List<CardNode>();
        var pack = draft.Picked ? Array.Empty<PoolCard>() : draft.Pack;
        for (int i = 0; i < pack.Count; i++)
        {
            int index = i;
            var node = Tile(DefinitionOf(pack[i]), grid);
            node.TooltipText = pack[i].Rarity;
            nodes.Add(node);
            node.Clicked += _ =>
            {
                if (_selected == index) { Pick(index); return; } // second click on the same card takes it
                _selected = index;
                foreach (var (n, j) in nodes.Select((n, j) => (n, j))) n.SetHighlight(j == index ? CardHighlight.Selected : CardHighlight.None);
                take.Disabled = false;
            };
        }
        take.Pressed += () => { if (_selected >= 0) Pick(_selected); };

        // The first pick of a round is from the booster this player opens: it opens on screen first.
        var packKey = $"{Session.Current!.Seed}:{draft.Round}";
        if (!draft.Picked && draft.PickInRound == 0 && _openedPack != packKey && Settings.Current.BoosterOpening != "skip"
            && BoosterOpening.Available && !_autoOnline && OS.GetEnvironment("ARCANUM_LIMITED").Length == 0)
        {
            _openedPack = packKey;
            OpenBooster(Session.Current!, nodes);
        }

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        right.AddThemeConstantOverride("separation", 6);
        var picks = draft.Picks;
        right.AddChild(MenuKit.SectionTitle($"Your picks ({picks.Count})"));
        right.AddChild(PickList(picks));
        body.AddChild(MenuKit.Card(right, 14));
    }

    /// <summary>The event and round of the last booster opened on screen, so each opens once.</summary>
    private string? _openedPack;

    /// <summary>The booster opens in 3D over the pick screen, then its cards fly out of it to their places, face down, and turn over.</summary>
    private void OpenBooster(LimitedEvent ev, List<CardNode> cards)
    {
        var set = ev.Source.StartsWith("set:") ? App.Instance.Module?.LoadSets().FirstOrDefault(s => "set:" + s.Code == ev.Source) : null;
        foreach (var card in cards) card.Modulate = Colors.Transparent;
        var opening = new BoosterOpening(set?.PackImage, set?.PackImageSeals ?? (0.07, 0.06), set?.Name ?? "Booster",
            automatic: Settings.Current.BoosterOpening == "auto");
        opening.Opened += from => DealFrom(cards, from);
        AddChild(opening);
        _opening = opening;
    }

    private static void DealFrom(List<CardNode> cards, Vector2 from)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (!IsInstanceValid(card) || card.View is not { } face) continue;
            var place = card.Position;
            card.Position = card.GetParent<Control>().GetGlobalTransform().AffineInverse() * from - card.Size / 2;
            card.Scale = new Vector2(0.4f, 0.4f);
            card.Setup(face with { IsHidden = true }, showCostPips: false);
            var deal = card.CreateTween();
            deal.TweenInterval(i * 0.05);
            deal.TweenCallback(Callable.From(() => card.Modulate = Colors.White));
            deal.TweenProperty(card, "position", place, 0.45).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            deal.Parallel().TweenProperty(card, "scale", Vector2.One, 0.45).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            deal.TweenProperty(card, "scale:x", 0f, 0.09);
            deal.TweenCallback(Callable.From(() => card.Setup(face, showCostPips: false)));
            deal.TweenProperty(card, "scale:x", 1f, 0.09);
        }
    }

    private void Pick(int index)
    {
        Session.Pick(index);
        Show();
    }

    /// <summary>Cards grouped by color with counts.</summary>
    private Control PickList(IReadOnlyList<PoolCard> cards)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        foreach (var group in cards.Select(c => (Card: c, Def: DefinitionOf(c))).GroupBy(x => ColorGroup(x.Def)).OrderBy(g => Array.IndexOf(GroupOrder, g.Key)))
        {
            list.AddChild(BoardStyle.MakeLabel($"{group.Key} ({group.Count()})", 14, BoardStyle.TextDim, bold: true));
            foreach (var x in group.OrderBy(x => x.Def.ManaCost.ManaValue).ThenBy(x => x.Card.Name))
            {
                var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop };
                var name = BoardStyle.MakeLabel(x.Card.Name, 14);
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                name.ClipText = true;
                row.AddChild(name);
                row.AddChild(BoardStyle.MakeCostRow(x.Def.ManaCost.ToString(), 16, 10));
                var def = x.Def;
                row.MouseEntered += () => ShowPreviewLeftOf(def, row);
                row.MouseExited += () => _preview.Visible = false;
                list.AddChild(row);
            }
        }
        return Scroll(list);
    }

    // ---------------------------------------------------------------- deck building

    private void ShowBuilder(string finishLabel, Action onFinish)
    {
        var ev = Session.Current!;
        var seat = ev.Seats[Session.Seat];
        seat.Deck ??= new DeckList();
        var deck = seat.Deck;
        _title.Text = _editingDuringEvent ? "Sideboarding" : "Build your deck";

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        // Left: the pool cards not in the deck, by color.
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 10);
        body.AddChild(left);
        left.AddChild(MenuKit.Hint("Click a card of your pool to put it in your deck; click a card in the deck list to take it out. Basic lands are free."));
        var poolBox = new VBoxContainer();
        poolBox.AddThemeConstantOverride("separation", 10);
        left.AddChild(Scroll(poolBox));

        var inDeck = deck.Main.ToList();
        var sideboard = new List<PoolCard>();
        foreach (var card in seat.Pool)
        {
            var entry = card.ToEntry();
            int i = inDeck.FindIndex(e => e.SameCard(entry.Name, entry.Set, entry.Number) && e.Count > 0);
            if (i >= 0) inDeck[i] = inDeck[i] with { Count = inDeck[i].Count - 1 };
            else sideboard.Add(card);
        }
        foreach (var group in sideboard.Select(c => (Card: c, Def: DefinitionOf(c))).GroupBy(x => ColorGroup(x.Def)).OrderBy(g => Array.IndexOf(GroupOrder, g.Key)))
        {
            poolBox.AddChild(BoardStyle.MakeLabel($"{group.Key} ({group.Count()})", 15, BoardStyle.TextDim, bold: true));
            var flow = new HFlowContainer();
            flow.AddThemeConstantOverride("h_separation", 8);
            flow.AddThemeConstantOverride("v_separation", 8);
            poolBox.AddChild(flow);
            foreach (var x in group.OrderBy(x => x.Def.ManaCost.ManaValue).ThenBy(x => x.Card.Name))
            {
                var node = Tile(x.Def, flow);
                var entry = x.Card.ToEntry();
                node.Clicked += _ =>
                {
                    DeckList.Adjust(deck.Main, entry.Name, 1, entry.Set, entry.Number);
                    DeckList.Adjust(deck.Sideboard, entry.Name, -1, entry.Set, entry.Number);
                    Session.DeckEdited();
                    Show();
                };
            }
        }

        // Right: the deck, basic lands, checks.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 8);
        body.AddChild(MenuKit.Card(right, 14));
        right.AddChild(MenuKit.SectionTitle($"Deck ({deck.Main.Sum(e => e.Count)})"));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var basics = LimitedService.Basics.Values.ToHashSet();
        foreach (var entry in deck.Main.Where(e => !basics.Contains(e.Name)).OrderBy(e => DefinitionOf(e).Is(CardType.Land)).ThenBy(e => DefinitionOf(e).ManaCost.ManaValue).ThenBy(e => e.Name))
        {
            var def = DefinitionOf(entry);
            var row = new Button { Flat = true, FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, TooltipText = "Take it out of the deck" };
            row.Text = $"{entry.Count}  {entry.Name}";
            row.AddThemeFontSizeOverride("font_size", 14);
            row.MouseEntered += () => ShowPreviewLeftOf(def, row);
            row.MouseExited += () => _preview.Visible = false;
            row.Pressed += () =>
            {
                DeckList.Adjust(deck.Main, entry.Name, -1, entry.Set, entry.Number);
                DeckList.Adjust(deck.Sideboard, entry.Name, 1, entry.Set, entry.Number);
                Session.DeckEdited();
                Show();
            };
            list.AddChild(row);
        }
        right.AddChild(Scroll(list));

        right.AddChild(MenuKit.SectionTitle("Basic lands"));
        var lands = new HBoxContainer();
        lands.AddThemeConstantOverride("separation", 8);
        foreach (var (color, name) in LimitedService.Basics)
        {
            var basic = Session.BasicLand(color);
            int count = deck.Main.Where(e => e.Name == name).Sum(e => e.Count);
            var cell = new VBoxContainer();
            var (bg, fg) = BoardStyle.PipColors(ColorLetter(color));
            var label = BoardStyle.MakeLabel($"{name}\n{count}", 13, BoardStyle.Text);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            cell.AddChild(label);
            var buttons = new HBoxContainer();
            foreach (var (text, delta) in new[] { ("−", -1), ("+", 1) })
            {
                var b = BoardStyle.MakeButton(text, 14, compact: true);
                b.CustomMinimumSize = new Vector2(34, 30);
                b.Disabled = delta < 0 && count == 0;
                b.Pressed += () =>
                {
                    var existing = deck.Main.FirstOrDefault(e => e.Name == name);
                    DeckList.Adjust(deck.Main, name, delta, existing?.Set ?? basic.Set, existing?.Number ?? basic.Number);
                    Session.DeckEdited();
                    Show();
                };
                buttons.AddChild(b);
            }
            cell.AddChild(buttons);
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", BoardStyle.Box(bg with { A = 0.25f }, 6));
            panel.AddChild(cell);
            lands.AddChild(panel);
        }
        right.AddChild(lands);

        var issues = Session.Validate(seat);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        var status = MenuKit.Hint(Session.Status is { } note ? note
            : errors.Count == 0 ? "✓ Ready to play" : string.Join("\n", errors.Take(4).Select(i => "• " + i.Message)));
        status.AddThemeColorOverride("font_color", errors.Count == 0 ? new Color("6fd08c") : new Color("ff8a70"));
        right.AddChild(status);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        var auto = BoardStyle.MakeButton("Build for me", 16);
        auto.CustomMinimumSize = new Vector2(150, 46);
        auto.TooltipText = "Pick the best two colors and build a 40-card deck automatically";
        auto.Pressed += () => { seat.Deck = Session.AutoBuild(seat.Pool); Session.DeckEdited(); Show(); };
        actions.AddChild(auto);
        var clear = BoardStyle.MakeButton("Clear", 16);
        clear.CustomMinimumSize = new Vector2(100, 46);
        clear.Pressed += () => { seat.Deck = new DeckList(); Session.DeckEdited(); Show(); };
        actions.AddChild(clear);
        var finish = BoardStyle.MakePrimaryButton(finishLabel, 18);
        finish.CustomMinimumSize = new Vector2(160, 46);
        finish.Disabled = errors.Count > 0;
        finish.Pressed += onFinish;
        actions.AddChild(finish);
        right.AddChild(actions);
    }

    private static char ColorLetter(ManaType color) => color switch
    {
        ManaType.White => 'W', ManaType.Blue => 'U', ManaType.Black => 'B', ManaType.Red => 'R', ManaType.Green => 'G', _ => 'C',
    };

    // ---------------------------------------------------------------- rounds and standings

    private void ShowRounds()
    {
        var ev = Session.Current!;
        int human = Session.Seat;
        bool finished = ev.Stage == EventStage.Finished;
        _title.Text = finished ? "Event results" : $"Round {ev.Rounds.Count} of {ev.RoundsTotal}";

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        _content.AddChild(body);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 16);
        body.AddChild(left);

        var matchBox = new VBoxContainer();
        matchBox.AddThemeConstantOverride("separation", 12);
        if (ev.CurrentMatch is { } match && !finished)
        {
            int opponent = match.Opponent(human)!.Value;
            var colors = DeckColors(ev.Seats[opponent].Deck);
            matchBox.AddChild(MenuKit.SectionTitle($"Your match: {ev.Seats[human].Name} vs {ev.Seats[opponent].Name}" + (colors.Length > 0 ? $" ({colors})" : "")));
            matchBox.AddChild(BoardStyle.MakeLabel($"Games: {match.WinsOf(human)} – {match.WinsOf(opponent)}" + (match.Draws > 0 ? $" ({match.Draws} drawn)" : "")
                                                 + (ev.BestOf > 1 ? $" · best of {ev.BestOf}" : ""), 16));
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 12);
            int next = match.WinsA + match.WinsB + match.Draws + 1;
            if (Session.GameInProgress)
            {
                var back = BoardStyle.MakePrimaryButton($"Return to game {next}", 20);
                back.CustomMinimumSize = new Vector2(240, 52);
                back.Pressed += Session.ReturnToGame;
                actions.AddChild(back);
            }
            else if (Session.WaitingForOpponent)
            {
                actions.AddChild(BoardStyle.MakeLabel("Ready. Waiting for your opponent…", 16, BoardStyle.TextDim));
            }
            else
            {
                var play = BoardStyle.MakePrimaryButton(Session.IsOnline ? $"Ready for game {next}" : $"Play game {next}", 20);
                play.CustomMinimumSize = new Vector2(220, 52);
                play.Pressed += () =>
                {
                    Session.PlayNext();
                    if (Session.IsOnline) Show();
                };
                actions.AddChild(play);
            }
            var edit = BoardStyle.MakeButton(ev.BestOf > 1 && match.WinsA + match.WinsB + match.Draws > 0 ? "Sideboard" : "Edit deck", 16);
            edit.CustomMinimumSize = new Vector2(150, 52);
            edit.Pressed += () => { _editingDuringEvent = true; Show(); };
            edit.Disabled = Session.GameInProgress;
            actions.AddChild(edit);
            matchBox.AddChild(actions);
        }
        else if (!finished)
        {
            matchBox.AddChild(MenuKit.SectionTitle("Your match is over. Waiting for the other matches of this round…"));
        }
        else
        {
            int place = ev.Standings().IndexOf(human) + 1;
            matchBox.AddChild(MenuKit.SectionTitle($"You finished {Ordinal(place)} of {ev.Seats.Count} with {Record(ev, human)}."));
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 12);
            var save = BoardStyle.MakeButton("Save my deck", 16);
            save.CustomMinimumSize = new Vector2(160, 48);
            save.Pressed += () =>
            {
                App.Instance.Decks.Save($"{ev.Seats[human].Name}'s {(ev.Mode == LimitedMode.Draft ? "draft" : "sealed")} deck", LimitedService.Format.Id, ev.Seats[human].Deck!);
                MenuKit.Toast(this, "Deck saved to your decks");
            };
            actions.AddChild(save);
            var again = BoardStyle.MakePrimaryButton("New event", 18);
            again.CustomMinimumSize = new Vector2(180, 48);
            again.Text = Session.IsOnline ? "Leave event" : "New event";
            again.Pressed += () => { Session.Abandon(); Show(); };
            actions.AddChild(again);
            matchBox.AddChild(actions);
        }
        left.AddChild(MenuKit.Card(matchBox));

        var roundsBox = new VBoxContainer();
        roundsBox.AddThemeConstantOverride("separation", 6);
        for (int r = ev.Rounds.Count - 1; r >= 0; r--)
        {
            roundsBox.AddChild(BoardStyle.MakeLabel($"Round {r + 1}", 15, BoardStyle.TextDim, bold: true));
            foreach (var m in ev.Rounds[r])
            {
                string text = m.SeatB is not { } b
                    ? $"{ev.Seats[m.SeatA].Name} has a bye"
                    : $"{ev.Seats[m.SeatA].Name} {m.WinsA} – {m.WinsB} {ev.Seats[b].Name}" + (m.Done ? "" : " (in progress)");
                roundsBox.AddChild(BoardStyle.MakeLabel(text, 14, m.Involves(human) ? BoardStyle.Playable : BoardStyle.Text));
            }
        }
        left.AddChild(Scroll(roundsBox));

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 6);
        right.AddChild(MenuKit.SectionTitle("Standings"));
        int rank = 1;
        foreach (var s in ev.Standings())
        {
            var row = new HBoxContainer();
            var name = BoardStyle.MakeLabel($"{rank++}. {ev.Seats[s].Name}  {DeckColors(ev.Seats[s].Deck)}", 15, s == human ? BoardStyle.Playable : BoardStyle.Text);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(BoardStyle.MakeLabel($"{Record(ev, s)} · {ev.Points(s)} pts", 15, BoardStyle.TextDim));
            right.AddChild(row);
        }
        right.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
        if (finished) // an unfinished event is abandoned from the header, at any stage
        {
            var close = BoardStyle.MakeButton("Close event", 14);
            close.Pressed += () => Ask("Close this event?", () => { Session.Abandon(); Show(); });
            right.AddChild(close);
        }
        body.AddChild(MenuKit.Card(right, 14));
    }

    private static string Record(LimitedEvent ev, int seat)
    {
        var matches = ev.Rounds.SelectMany(r => r).Where(m => m.Done && m.Involves(seat)).ToList();
        int wins = matches.Count(m => m.SeatB is null || m.WinsOf(seat) > m.WinsOf(m.Opponent(seat)!.Value));
        int draws = matches.Count(m => m.SeatB is not null && m.WinsOf(seat) == m.WinsOf(m.Opponent(seat)!.Value));
        return $"{wins}–{matches.Count - wins - draws}" + (draws > 0 ? $"–{draws}" : "");
    }

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private void Ask(string question, Action yes)
    {
        var dialog = new ConfirmationDialog { Title = "Arcanum", DialogText = question, Size = new Vector2I(460, 160) };
        dialog.Confirmed += () => { yes(); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }
}
