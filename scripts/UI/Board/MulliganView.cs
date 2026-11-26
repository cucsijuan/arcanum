// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Opening hand shown large across the local player's half while deciding a mulligan (and choosing cards to put
/// on the bottom), so the cards can be read without hovering. Hovering still opens the usual side preview.
/// Cards chosen from the library or anywhere else are shown in it too.
/// </summary>
public partial class MulliganView : Control
{
    private const float LeftRoom = 410;   // keeps the hover preview (left edge) clear
    private const float RightRoom = 360;  // keeps the action panel (right edge) clear
    private const float Gap = 14;

    /// <summary>The smallest a card gets in the large view before the rows scroll.</summary>
    private const float MinChoiceWidth = 104;

    /// <summary>Around the rows of the large view (the cards' glow isn't cut by the scrolling).</summary>
    private const float RowMargin = 12;

    private readonly ColorRect _shade = new() { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
    private readonly Label _title = BoardStyle.MakeTitle("", 26, BoardStyle.Text);
    private readonly Control _row = new() { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly ScrollContainer _scroll = new()
    {
        HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, ClipContents = false, MouseFilter = MouseFilterEnum.Pass,
    };
    private readonly HBoxContainer _answers = new() { Alignment = BoxContainer.AlignmentMode.Center, Visible = false };

    /// <summary>
    /// The whole screen, for small screens (the compact board): the screen darkened, the hand as large as it fits in the
    /// middle, and the answers (<see cref="SetAnswers"/>) as big buttons under it instead of in the action panel.
    /// </summary>
    public bool Large
    {
        get => _large;
        set
        {
            if (_large == value) return;
            _large = value;
            AnchorTop = value ? 0 : 0.5f;
            _shade.Color = new Color(0, 0, 0, value ? 0.78f : 0.55f);
            _title.AddThemeFontSizeOverride("font_size", value ? 34 : 26);
            if (!value) SetAnswers(Array.Empty<(string, Action, bool, bool)>());
        }
    }
    private bool _large;

    /// <summary>The answers shown under the hand in the large view: text, what it does, the default one, usable now.</summary>
    public void SetAnswers(IReadOnlyList<(string Text, Action Pressed, bool Primary, bool Enabled)> answers, bool small = false)
    {
        _answers.AddThemeConstantOverride("separation", small ? 20 : 48);
        // Out of the row at once: its height is measured from the answers in it.
        foreach (var child in _answers.GetChildren())
        {
            _answers.RemoveChild(child);
            child.QueueFree();
        }
        _main = null;
        foreach (var (text, pressed, primary, enabled) in answers)
        {
            var button = BoardStyle.MakeButton(text, small ? 22 : 30);
            button.CustomMinimumSize = small ? new Vector2(180, AnswerHeight) : new Vector2(260, 72);
            button.Disabled = !enabled;
            if (primary) button.AddThemeColorOverride("font_color", UiArt.Gold);
            if (primary && enabled) _main = button;
            button.Pressed += pressed;
            _answers.AddChild(button);
        }
        _answers.Visible = answers.Count > 0;
    }

    /// <summary>The default answer (where a controller's focus starts).</summary>
    private Button? _main;

    /// <summary>The default answer when it can be given now.</summary>
    public Button? Main => _answers.Visible ? _main : null;

    private const float AnswerHeight = 56;

    /// <summary>The card about to get the controller's focus (the view may be laid out again before it has it).</summary>
    private CardId? _placing;

    /// <summary>The choice being shown, to tell one shown again from a new one.</summary>
    private object? _about;
    private string? _shown;

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    public MulliganView()
    {
        AnchorTop = 0.5f; AnchorRight = 1; AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_shade);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_title);
        _scroll.AddChild(_row);
        AddChild(_scroll);
        _answers.AddThemeConstantOverride("separation", 48);
        AddChild(_answers);
    }

    /// <param name="selected">Cards picked so far (bottom selection); highlighted.</param>
    /// <param name="selectable">Cards can be clicked (choosing which to put on the bottom).</param>
    /// <param name="about">The choice (the same one while its cards are being picked).</param>
    /// <param name="complete">Every card wanted is picked: a controller's focus goes on to the answer.</param>
    /// <param name="startOnCards">A controller's focus starts on the cards, not on the default answer.</param>
    public void ShowHand(IReadOnlyList<CardView> hand, string title, ISet<CardId> selected, bool selectable,
        object? about = null, bool complete = false, bool startOnCards = false)
    {
        bool choosing = _large && selectable, opening = !ReferenceEquals(about, _about);
        // The same hand shown again (the board refreshes after every event): its cards stay.
        var shown = choosing ? null : $"{_large}|{selectable}|{Size}|{title}|"
            + string.Join(",", hand.Select(c => $"{c.Id.Value}{(c.IsHidden ? "h" : "")}{(selected.Contains(c.Id) ? "s" : "")}"));
        if (Visible && !opening && about is not null && shown is not null && shown == _shown)
        {
            if (_answers.Visible) PadFocus.Offer(_answers, _main);
            return;
        }
        _shown = shown;
        _about = about;
        Visible = true;
        _title.Text = title;
        var focused = _row.GetChildren().OfType<CardNode>().FirstOrDefault(c => !c.IsQueuedForDeletion() && c.HasFocus())?.Id ?? _placing;
        foreach (var child in _row.GetChildren()) child.QueueFree();
        PadFocus.KeepInside(this, choosing);
        _scroll.ClipContents = choosing;
        _title.AutowrapMode = choosing ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
        _title.VerticalAlignment = choosing ? VerticalAlignment.Center : VerticalAlignment.Top;
        _title.AddThemeFontSizeOverride("font_size", choosing ? 26 : _large ? 34 : 26);
        if (choosing)
        {
            if (opening) _scroll.ScrollVertical = 0;
            ShowChoice(hand, selected);
            if (_answers.Visible) PadFocus.Offer(_answers, _main);
            if (PlaceFocus(hand, focused, opening, complete, startOnCards) is { } card)
            {
                PadFocus.Prefer(card);
                if (PadFocus.UsingButtons)
                {
                    _placing = card.Id;
                    Callable.From(() =>
                    {
                        if (!IsInstanceValid(card) || card.IsQueuedForDeletion()) return; // laid out again: that one's turn
                        _placing = null;
                        if (card.IsVisibleInTree()) card.GrabFocus();
                    }).CallDeferred();
                }
            }
            return;
        }
        _scroll.Position = Vector2.Zero;
        _scroll.Size = Size;
        _row.CustomMinimumSize = Vector2.Zero;

        int n = Math.Max(1, hand.Count);
        // Large: the whole width but a margin, and room above for the title and below for the answers.
        float left = _large ? 32 : LeftRoom, right = _large ? 32 : RightRoom;
        float available = Size.X - left - right;
        float width = Math.Min(_large ? 300 : 210, (available - Gap * (n - 1)) / n);
        if (_large) width = Math.Min(width, (Size.Y - 260) / 1.395f);
        var size = new Vector2(width, width * 1.395f);
        float total = n * width + (n - 1) * Gap;
        float startX = left + (available - total) / 2;
        float y = _large ? (Size.Y - size.Y) / 2 - 30 : (Size.Y - size.Y) / 2 + 18;

        _title.Position = new Vector2(left, y - (_large ? 64 : 56));
        _title.Size = new Vector2(available, _large ? 46 : 36);
        _answers.Position = new Vector2(0, y + size.Y + 28);
        _answers.Size = new Vector2(Size.X, 72);

        for (int i = 0; i < hand.Count; i++)
        {
            AddCard(hand[i], size, new Vector2(startX + i * (width + Gap), y), selected, selectable);
        }
        if (_answers.Visible) PadFocus.Offer(_answers, _main);
    }

    private void AddCard(CardView view, Vector2 size, Vector2 position, ISet<CardId> selected, bool selectable)
    {
        var card = new CardNode { Size = size, Position = position };
        _row.AddChild(card);
        card.Setup(view, showCostPips: false);
        card.SetHighlight(selected.Contains(view.Id) ? CardHighlight.Selected : selectable ? CardHighlight.Playable : CardHighlight.None);
        if (selectable) PadFocus.Reachable(card); // a controller's arrows reach the cards to pick
        card.Clicked += c => CardClicked?.Invoke(c);
        card.HoverStarted += c => CardHoverStarted?.Invoke(c);
        card.HoverEnded += c => CardHoverEnded?.Invoke(c);
    }

    /// <summary>
    /// The cards of a choice on the whole screen: beside the room of the preview, in as many rows as show them largest
    /// (scrolling when they would get too small), between the question above and the answers below.
    /// </summary>
    private void ShowChoice(IReadOnlyList<CardView> cards, ISet<CardId> selected)
    {
        int n = Math.Max(1, cards.Count);
        float left = BoardStyle.PreviewSize.X + 32, right = 20;
        const float Top = 12, TitleHeight = 76, Bottom = 14, Space = 8;
        float available = Size.X - left - right;
        _title.Position = new Vector2(left, Top);
        _title.Size = new Vector2(available, TitleHeight);
        float answers = _answers.GetCombinedMinimumSize().Y;
        _answers.Position = new Vector2(left, Size.Y - Bottom - answers);
        _answers.Size = new Vector2(available, answers);
        _scroll.Position = new Vector2(left, Top + TitleHeight + Space);
        _scroll.Size = new Vector2(available, _answers.Position.Y - Space - _scroll.Position.Y);

        const float Ratio = 1.395f, ScrollBarRoom = 16;
        float roomX = _scroll.Size.X - 2 * RowMargin - ScrollBarRoom, roomY = _scroll.Size.Y - 2 * RowMargin;
        int columns = 1;
        float width = 0;
        for (int rows = 1; rows <= n; rows++)
        {
            int across = (n + rows - 1) / rows;
            float fits = Math.Min(300, Math.Min((roomX - Gap * (across - 1)) / across, (roomY - Gap * (rows - 1)) / rows / Ratio));
            if (fits > width) (width, columns) = (fits, across);
        }
        if (width < MinChoiceWidth)
        {
            columns = Math.Max(1, (int)((roomX + Gap) / (MinChoiceWidth + Gap)));
            width = (roomX - Gap * (columns - 1)) / columns;
        }
        var size = new Vector2(width, width * Ratio);
        int rowCount = (n + columns - 1) / columns;
        float height = rowCount * size.Y + (rowCount - 1) * Gap;
        float top = RowMargin + Math.Max(0, (roomY - height) / 2);
        _row.CustomMinimumSize = new Vector2(0, height + 2 * RowMargin);
        for (int i = 0; i < cards.Count; i++)
        {
            int line = i / columns, inLine = Math.Min(columns, cards.Count - line * columns);
            float start = RowMargin + (roomX - inLine * width - (inLine - 1) * Gap) / 2;
            AddCard(cards[i], size, new Vector2(start + i % columns * (width + Gap), top + line * (size.Y + Gap)), selected, selectable: true);
        }
    }

    /// <summary>The card a controller's focus goes to now that the cards were laid out (again); null leaves it to the answers.</summary>
    private CardNode? PlaceFocus(IReadOnlyList<CardView> cards, CardId? focused, bool opening, bool complete, bool startOnCards)
    {
        // (With a finger or a mouse in use, the first press of a direction goes to the preferred one.)
        if (opening || !PadFocus.UsingButtons && !complete) return startOnCards && cards.Count > 0 ? FindCard(cards[0].Id) : null;
        return focused is { } id && !complete ? FindCard(id) : null;
    }

    /// <summary>The shown card with this id, if it's shown.</summary>
    public CardNode? FindCard(CardId id) =>
        Visible ? _row.GetChildren().OfType<CardNode>().FirstOrDefault(c => !c.IsQueuedForDeletion() && c.Id == id) : null;

    public void HideHand()
    {
        if (!Visible) return;
        Visible = false;
        _about = null;
        _placing = null;
        foreach (var child in _row.GetChildren()) child.QueueFree();
    }
}
