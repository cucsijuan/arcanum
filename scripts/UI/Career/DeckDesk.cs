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
        _matArea = Hotspot.LoadArea(Kit + "desk.json", "mat", new Rect2(0.148f, 0.177f, 0.706f, 0.643f));
        if (picture is null)
        {
            var felt = CareerStyle.Box(new Color("1f3b3a"), new Color("c9a45c"), 3, 18);
            felt.ShadowColor = new Color(0, 0, 0, 0.5f);
            felt.ShadowSize = 14;
            _felt.AddThemeStyleboxOverride("panel", felt);
            AddChild(_felt);
        }
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
        float width = (inner.Size.X * 0.54f - (Columns - 1) * 8) / Columns;
        float height = (inner.Size.Y - 52 - (Rows - 1) * 8) / Rows;
        width = Math.Max(60, Math.Min(width, height / 1.39f));
        _tile = new Vector2(width, width * 1.39f);
        _left.CustomMinimumSize = new Vector2(Columns * width + (Columns - 1) * 8, 0);
        if (IsNodeReady()) Refresh();
    }

    private void BuildLayout()
    {
        var light = new Color(1f, 0.95f, 0.85f);
        _top.AddThemeConstantOverride("separation", 10);
        _top.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(_top);

        // Above the mat: leave, the deck chosen, its name, saving; then the filters.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var back = CareerStyle.Button("< Room", Leave, 20);
        back.TooltipText = "Back to your room (Esc)";
        row.AddChild(back);
        _decks.ItemSelected += i =>
        {
            int index = (int)i;
            if (index < _saved.Count) Confirm(() => Load(_saved[index]), SelectCurrent);
        };
        row.AddChild(_decks);
        _name.TextChanged += _ => Dirty();
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(_name);
        row.AddChild(CareerStyle.Button("New", () => Confirm(NewDeck), 20));
        row.AddChild(CareerStyle.Button("Delete", AskDelete, 20));
        row.AddChild(CareerStyle.Button("Save", Save, 20, primary: true));
        _top.AddChild(row);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 8);
        var search = new LineEdit { PlaceholderText = "Search your cards…", CustomMinimumSize = new Vector2(280, 0) };
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
        note.AddChild(_problems);
        _bottom.AddChild(note);
        _pageInfo.AddThemeColorOverride("font_color", light);
        Callable.From(Layout).CallDeferred();
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
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(_tile.X - 54, 4) };
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
            float step = 26, small = 0.78f;
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
