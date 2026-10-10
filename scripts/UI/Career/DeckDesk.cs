// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
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
    private const string MatImage = "res://ui-art/kit/career/deckmat.jpg";
    private static readonly Vector2 TileSize = new(132, 184);
    private const int Columns = 6, Rows = 3;
    private static readonly string[] ColumnNames = { "1", "2", "3", "4", "5", "6+", "Lands" };
    private static readonly (string Label, CardType Type)[] TypeFilters =
    {
        ("Any type", CardType.None), ("Creatures", CardType.Creature), ("Instants", CardType.Instant), ("Sorceries", CardType.Sorcery),
        ("Artifacts", CardType.Artifact), ("Enchantments", CardType.Enchantment), ("Planeswalkers", CardType.Planeswalker), ("Lands", CardType.Land),
    };

    private DeckList _deck = new();
    private DeckInfo? _info;
    private bool _dirty;
    private int _page;
    private string _search = "";
    private int _type;
    private readonly HashSet<char> _colors = new();

    private readonly LineEdit _name = new() { PlaceholderText = "Deck name", CustomMinimumSize = new Vector2(320, 0) };
    private readonly OptionButton _decks = new() { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(300, 0) };
    private readonly GridContainer _grid = new() { Columns = Columns };
    private readonly Label _pageInfo = CareerStyle.Text("", 18, new Color(1f, 0.95f, 0.85f));
    private readonly HBoxContainer _columns = new();
    private readonly HBoxContainer _basics = new();
    private readonly Label _count = CareerStyle.Text("", 26, new Color(1f, 0.95f, 0.85f));
    private readonly Label _problems = CareerStyle.Text("", 17, CareerStyle.Ink, wrap: true);
    private readonly CardNode _preview = new() { SharpImage = true, MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 300 };
    private List<DeckInfo> _saved = new();

    private static CareerService Career => App.Instance.Career;
    private static CardDatabase Cards => App.Instance.Cards!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = CareerStyle.Notes;
        if (!Career.HasCareer || App.Instance.Cards is null)
        {
            Callable.From(() => App.Instance.GoTo(App.CareerScene)).CallDeferred();
            return;
        }
        BuildTable();
        BuildLayout();
        _preview.Size = new Vector2(300, 418);
        AddChild(_preview);
        RefreshSaved();
        if (_saved.Count > 0) Load(_saved[0]);
        else NewDeck();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Leave();
        }
    }

    // ---------------------------------------------------------------- the table

    /// <summary>The desk and its playmat: the picture when installed, otherwise a felt mat on wood.</summary>
    private void BuildTable()
    {
        if (ResourceLoader.Exists(MatImage))
        {
            AddChild(new TextureRect
            {
                Texture = GD.Load<Texture2D>(MatImage), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            });
            return;
        }
        AddChild(new ColorRect { Color = new Color("3b2618"), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var mat = new Panel { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 18, OffsetTop = 18, OffsetRight = -18, OffsetBottom = -18, MouseFilter = MouseFilterEnum.Ignore };
        var felt = CareerStyle.Box(new Color("1f3b3a"), new Color("c9a45c"), 3, 26);
        felt.ShadowColor = new Color(0, 0, 0, 0.5f);
        felt.ShadowSize = 18;
        mat.AddThemeStyleboxOverride("panel", felt);
        AddChild(mat);
    }

    private void BuildLayout()
    {
        var root = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 48, OffsetTop = 36, OffsetRight = -48, OffsetBottom = -36 };
        root.AddThemeConstantOverride("separation", 14);
        AddChild(root);

        // Top: leave, the deck chosen, its name, saving.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        var back = CareerStyle.Button("< Room", Leave, 20);
        back.TooltipText = "Back to your room (Esc)";
        top.AddChild(back);
        _decks.ItemSelected += i =>
        {
            int index = (int)i;
            if (index < _saved.Count) Confirm(() => Load(_saved[index]), SelectCurrent);
        };
        top.AddChild(_decks);
        _name.TextChanged += _ => Dirty();
        top.AddChild(_name);
        top.AddChild(CareerStyle.Button("New", () => Confirm(NewDeck), 20));
        top.AddChild(CareerStyle.Button("Delete", AskDelete, 20));
        top.AddChild(CareerStyle.Button("Save", Save, 20, primary: true));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(_count);
        root.AddChild(top);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 28);
        root.AddChild(body);

        // Left: the cards owned.
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 10);
        body.AddChild(left);
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        var search = new LineEdit { PlaceholderText = "Search your cards…", CustomMinimumSize = new Vector2(260, 0) };
        search.TextChanged += text => { _search = text; ShowPage(0); };
        filters.AddChild(search);
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
            filters.AddChild(toggle);
        }
        var type = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var (label, _) in TypeFilters) type.AddItem(label);
        type.ItemSelected += i => { _type = (int)i; ShowPage(0); };
        filters.AddChild(type);
        left.AddChild(filters);
        _grid.AddThemeConstantOverride("h_separation", 10);
        _grid.AddThemeConstantOverride("v_separation", 10);
        left.AddChild(_grid);
        var pager = new HBoxContainer();
        pager.AddThemeConstantOverride("separation", 12);
        pager.AddChild(CareerStyle.Button("<", () => ShowPage(_page - 1), 20));
        pager.AddChild(_pageInfo);
        pager.AddChild(CareerStyle.Button(">", () => ShowPage(_page + 1), 20));
        left.AddChild(pager);
        left.AddChild(CareerStyle.Text("Click a card to put it in the deck · click a card of the deck to take it out", 16, new Color(1f, 0.95f, 0.85f, 0.7f)));

        // Right: the deck in columns, the basic lands and what's wrong with it.
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 10);
        body.AddChild(right);
        _columns.AddThemeConstantOverride("separation", 6);
        _columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_columns);
        right.AddChild(scroll);
        _basics.AddThemeConstantOverride("separation", 10);
        right.AddChild(_basics);
        var note = new PanelContainer();
        note.AddThemeStyleboxOverride("panel", CareerStyle.Box(new Color("f7e9a8"), new Color("c9b25a"), 1, 2, 12));
        note.AddChild(_problems);
        right.AddChild(note);
    }

    // ---------------------------------------------------------------- the collection

    private sealed record Owned(CardEntry Card, DeckEntry Printing, int Count);

    /// <summary>The cards owned that pass the filters, one per name (with the printing owned most), by mana value.</summary>
    private List<Owned> OwnedCards()
    {
        var owned = Career.State.Cards
            .GroupBy(c => c.Card.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Entry: Cards.Find(g.Key), Best: g.OrderByDescending(c => c.Count).First().Card, Count: g.Sum(c => c.Count)))
            .Where(x => x.Entry is not null && !Career.World!.IsBasicLand(x.Entry.Name))
            .Select(x => new Owned(x.Entry!, new DeckEntry(1, x.Entry!.Name, x.Best.Set.Length > 0 ? x.Best.Set : null, x.Best.Number.Length > 0 ? x.Best.Number : null), x.Count));
        return owned.Where(Passes).OrderBy(o => o.Card.Definition.Is(CardType.Land)).ThenBy(o => o.Card.ManaValue).ThenBy(o => o.Card.Name).ToList();
    }

    private bool Passes(Owned owned)
    {
        var d = owned.Card.Definition;
        if (_search.Length > 0 && !d.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)) return false;
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

    private void ShowPage(int page)
    {
        var cards = OwnedCards();
        int pageSize = Columns * Rows;
        int pages = Math.Max(1, (cards.Count + pageSize - 1) / pageSize);
        _page = Math.Clamp(page, 0, pages - 1);
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        foreach (var owned in cards.Skip(_page * pageSize).Take(pageSize))
        {
            int inDeck = InDeck(owned.Card.Name);
            var tile = Tile(Definition(owned.Printing), owned.Count - inDeck <= 0);
            Badge(tile, $"{inDeck}/{owned.Count}", inDeck > 0);
            var card = tile.GetChild<CardNode>(0);
            card.Clicked += _ => Add(owned);
            card.RightClicked += _ => Remove(owned.Card.Name);
            _grid.AddChild(tile);
        }
        _pageInfo.Text = cards.Count == 0 ? "No cards match" : $"{cards.Count} cards · page {_page + 1} of {pages}";
    }

    private Control Tile(CardDefinition definition, bool used)
    {
        var holder = new Control { CustomMinimumSize = TileSize };
        var card = new CardNode { Size = TileSize };
        holder.AddChild(card);
        card.Setup(ViewOf(definition), showCostPips: false);
        if (used) card.Modulate = new Color(1, 1, 1, 0.45f);
        card.HoverStarted += c => ShowPreview(c, holder);
        card.HoverEnded += _ => HidePreview();
        return holder;
    }

    private static void Badge(Control tile, string text, bool lit)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(TileSize.X - 58, 6) };
        panel.AddThemeStyleboxOverride("panel", CareerStyle.Box(lit ? BoardStyle.Playable : new Color(0, 0, 0, 0.7f), null, 0, 8, 3));
        panel.AddChild(CareerStyle.Text(text, 17, lit ? new Color("16171a") : Colors.White));
        tile.AddChild(panel);
    }

    // ---------------------------------------------------------------- the deck

    private int InDeck(string name) => _deck.Main.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count);

    private void Add(Owned owned)
    {
        if (InDeck(owned.Card.Name) >= owned.Count)
        {
            MenuKit.Toast(this, $"You have {owned.Count} {owned.Card.Name}.");
            return;
        }
        DeckList.Adjust(_deck.Main, owned.Card.Name, 1, owned.Printing.Set, owned.Printing.Number);
        Dirty();
        Refresh();
    }

    private void Remove(string name)
    {
        var entry = _deck.Main.LastOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        DeckList.Adjust(_deck.Main, entry.Name, -1, entry.Set, entry.Number, entry.Foil);
        Dirty();
        Refresh();
    }

    private void Refresh()
    {
        HidePreview();
        ShowPage(_page);
        ShowDeck();
    }

    private void ShowDeck()
    {
        foreach (var child in _columns.GetChildren()) child.QueueFree();
        var cards = _deck.Main.SelectMany(e => Enumerable.Repeat(e, e.Count))
            .Where(e => !Career.World!.IsBasicLand(e.Name))
            .Select(e => (Entry: e, Def: Definition(e))).ToList();
        for (int column = 0; column < ColumnNames.Length; column++)
        {
            var inColumn = cards.Where(c => ColumnOf(c.Def) == column).OrderBy(c => c.Def.ManaCost.ManaValue).ThenBy(c => c.Entry.Name).ToList();
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 2);
            box.AddChild(CareerStyle.Text($"{ColumnNames[column]}  ({inColumn.Count})", 16, new Color(1f, 0.95f, 0.85f, 0.85f)));
            // Cards overlap, each showing its title bar; the last one shows whole.
            float step = 32, small = 0.92f;
            var stack = new Control { CustomMinimumSize = new Vector2(TileSize.X * small, inColumn.Count == 0 ? 0 : step * (inColumn.Count - 1) + TileSize.Y * small) };
            for (int i = 0; i < inColumn.Count; i++)
            {
                var (entry, def) = inColumn[i];
                var card = new CardNode { Size = TileSize * small, Position = new Vector2(0, i * step) };
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

        int total = _deck.Main.Sum(e => e.Count);
        _count.Text = $"{total} cards";
        var problems = Career.Missing(_deck).Select(m => $"You have {m.Owned} of the {m.Needed} {m.Name}.")
            .Concat(DeckValidator.Validate(_deck, FormatRules.Casual, Cards).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)).ToList();
        _problems.Text = problems.Count == 0 ? "Ready to play." : string.Join("\n", problems.Take(3));
        _problems.AddThemeColorOverride("font_color", problems.Count == 0 ? CareerStyle.Good : CareerStyle.Bad);
    }

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
        _info = info;
        _name.Text = info.Name;
        _dirty = false;
        SelectCurrent();
        Refresh();
    }

    private void NewDeck()
    {
        _deck = new DeckList();
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
        _info = Career.Decks.Save(name, "casual", _deck, _info);
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

    private void Leave() => Confirm(() => App.Instance.GoTo(App.CareerScene));

    // ---------------------------------------------------------------- helpers

    private static CardDefinition Definition(DeckEntry entry) =>
        Cards.TryGet(entry.Name, entry.Set, entry.Number, out var d) ? d : Cards.Find(entry.Name)!.Definition;

    private static CardView ViewOf(CardDefinition d) => new()
    {
        Id = new CardId(-1), Owner = new PlayerId(0), Controller = new PlayerId(0), Zone = Zone.Hand, IsHidden = false,
        Name = d.Name, ManaCost = d.ManaCost.ToString(), Types = d.Types, Power = d.Power, Toughness = d.Toughness,
        Keywords = d.Keywords, BasePower = d.Power, BaseToughness = d.Toughness, ImageKey = d.ImageKey,
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
