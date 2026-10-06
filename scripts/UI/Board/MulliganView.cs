// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Opening hand shown large across the local player's half while deciding a mulligan (and choosing cards to put
/// on the bottom), so the cards can be read without hovering. Hovering still opens the usual side preview.
/// </summary>
public partial class MulliganView : Control
{
    private const float LeftRoom = 410;   // keeps the hover preview (left edge) clear
    private const float RightRoom = 360;  // keeps the action panel (right edge) clear
    private const float Gap = 14;

    private readonly ColorRect _shade = new() { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
    private readonly Label _title = BoardStyle.MakeTitle("", 26, BoardStyle.Text);
    private readonly Control _row = new() { MouseFilter = MouseFilterEnum.Ignore };

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
        AddChild(_row);
    }

    /// <param name="selected">Cards picked so far (bottom selection); highlighted.</param>
    /// <param name="selectable">Cards can be clicked (choosing which to put on the bottom).</param>
    public void ShowHand(IReadOnlyList<CardView> hand, string title, ISet<Arcanum.Engine.Core.CardId> selected, bool selectable)
    {
        Visible = true;
        _title.Text = title;
        foreach (var child in _row.GetChildren()) child.QueueFree();

        int n = Math.Max(1, hand.Count);
        float available = Size.X - LeftRoom - RightRoom;
        float width = Math.Min(210, (available - Gap * (n - 1)) / n);
        var size = new Vector2(width, width * 1.395f);
        float total = n * width + (n - 1) * Gap;
        float startX = LeftRoom + (available - total) / 2;
        float y = (Size.Y - size.Y) / 2 + 18;

        _title.Position = new Vector2(LeftRoom, y - 56);
        _title.Size = new Vector2(available, 36);

        for (int i = 0; i < hand.Count; i++)
        {
            var card = new CardNode { Size = size, Position = new Vector2(startX + i * (width + Gap), y) };
            _row.AddChild(card);
            card.Setup(hand[i], showCostPips: false);
            card.SetHighlight(selected.Contains(hand[i].Id) ? CardHighlight.Selected : selectable ? CardHighlight.Playable : CardHighlight.None);
            card.Clicked += c => CardClicked?.Invoke(c);
            card.HoverStarted += c => CardHoverStarted?.Invoke(c);
            card.HoverEnded += c => CardHoverEnded?.Invoke(c);
        }
    }

    public void HideHand()
    {
        if (!Visible) return;
        Visible = false;
        foreach (var child in _row.GetChildren()) child.QueueFree();
    }
}
