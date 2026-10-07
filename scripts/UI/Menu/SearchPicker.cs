// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// A drop-down for long lists: pressing it opens a list right below it, no taller than <see cref="ListHeight"/>, with a
/// search box on top. Typing filters the list loosely: by an item's code ("hoc"), by words of its name ("hobbit eternal"),
/// or by letters in order ("lotr"); the best matches come first. Enter picks the first one, Escape closes.
/// </summary>
public partial class SearchPicker : Button
{
    /// <summary>One choice: what is shown, and an optional short code it can also be found by (a set code).</summary>
    public sealed record Item(string Label, string? Code = null);

    private readonly List<Item> _items = new();
    private readonly Label _arrow = BoardStyle.MakeLabel("⌄", 16);
    private PopupPanel? _popup;
    private int _selected;

    public event Action<int>? ItemSelected;

    /// <summary>Height of the open list.</summary>
    public float ListHeight { get; set; } = 420;

    public SearchPicker()
    {
        FocusMode = FocusModeEnum.None;
        Alignment = HorizontalAlignment.Left;
        ClipText = true;
        CustomMinimumSize = new Vector2(190, 40);
        AddThemeFontSizeOverride("font_size", 15);
        UiArt.StyleButton(this, primary: false);
        _arrow.AnchorLeft = 1; _arrow.AnchorRight = 1; _arrow.AnchorBottom = 1;
        _arrow.OffsetLeft = -30; _arrow.OffsetRight = -12;
        _arrow.VerticalAlignment = VerticalAlignment.Center;
        AddChild(_arrow);
        Pressed += Open;
    }

    public int ItemCount => _items.Count;

    public void AddItem(string label, string? code = null)
    {
        _items.Add(new Item(label, code));
        if (_items.Count == 1) Select(0);
    }

    /// <summary>The chosen item's index (selecting doesn't raise <see cref="ItemSelected"/>).</summary>
    public int Selected
    {
        get => _selected;
        set => Select(value);
    }

    private void Select(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _selected = index;
        Text = _items[index].Label + "      "; // room for the arrow
    }

    private void Open()
    {
        _popup?.QueueFree();
        var popup = _popup = new PopupPanel();
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        popup.AddChild(box);
        var search = MenuKit.TextField("", "Search by name or code…");
        box.AddChild(search);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(list);
        box.AddChild(scroll);

        List<int> shown = new();
        void Fill(string query)
        {
            foreach (var child in list.GetChildren()) child.QueueFree();
            shown = Rank(query);
            foreach (int index in shown)
            {
                var entry = BoardStyle.MakeModalButton(_items[index].Label, index == _selected, 15);
                entry.Alignment = HorizontalAlignment.Left;
                entry.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                entry.ClipText = true;
                entry.Pressed += () => Choose(index);
                list.AddChild(entry);
            }
            if (shown.Count == 0) list.AddChild(BoardStyle.MakeLabel("  Nothing matches", 14, BoardStyle.TextDim));
        }
        search.TextChanged += Fill;
        search.TextSubmitted += _ => { if (shown.Count > 0) Choose(shown[0]); };
        popup.PopupHide += () => { popup.QueueFree(); if (_popup == popup) _popup = null; };
        AddChild(popup);
        Fill("");

        float width = Math.Max(Size.X, 380);
        popup.Popup(new Rect2I((Vector2I)GetScreenPosition() + new Vector2I(0, (int)Size.Y + 2), new Vector2I((int)width, (int)ListHeight)));
        search.GrabFocus();
    }

    private void Choose(int index)
    {
        _popup?.Hide();
        if (index == _selected) return;
        Select(index);
        ItemSelected?.Invoke(index);
    }

    /// <summary>The items matching the query, best first (all of them in order for an empty query).</summary>
    private List<int> Rank(string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Enumerable.Range(0, _items.Count).ToList();
        var scored = new List<(int Index, int Score, int Weakest)>();
        for (int i = 0; i < _items.Count; i++)
        {
            int total = 0, weakest = int.MaxValue;
            foreach (var word in words)
            {
                int score = Score(_items[i], word);
                if (score == 0) { total = 0; break; }
                total += score;
                weakest = Math.Min(weakest, score);
            }
            if (total > 0) scored.Add((i, total, weakest));
        }
        // Letters-in-order matches are only a fallback: they're dropped as soon as something matches by code or words.
        if (scored.Any(s => s.Weakest >= LooseMatch)) scored.RemoveAll(s => s.Weakest < LooseMatch);
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => s.Index).ToList();
    }

    /// <summary>The lowest score of a real match (a code or words of the name); below it are letters-in-order matches.</summary>
    private const int LooseMatch = 25;

    /// <summary>How well one word of the query matches an item (0: not at all).</summary>
    private static int Score(Item item, string word)
    {
        var code = item.Code?.ToLowerInvariant();
        var label = item.Label.ToLowerInvariant();
        if (code == word) return 100;
        if (code is not null && code.StartsWith(word, StringComparison.Ordinal)) return 60;
        var labelWords = label.Split(new[] { ' ', '-', ':', '(', ')', ',', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        if (labelWords.Any(w => w == word)) return 50;
        if (labelWords.Any(w => w.StartsWith(word, StringComparison.Ordinal))) return 40;
        if (label.Contains(word, StringComparison.Ordinal)) return 25;
        // Letters in order, like the initials of a name ("lotr").
        int at = 0;
        foreach (char c in label)
            if (at < word.Length && c == word[at]) at++;
        return at == word.Length ? 8 : 0;
    }
}
