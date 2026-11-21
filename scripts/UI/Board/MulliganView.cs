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
    public void SetAnswers(IReadOnlyList<(string Text, Action Pressed, bool Primary, bool Enabled)> answers)
    {
        foreach (var child in _answers.GetChildren()) child.QueueFree();
        _main = null;
        foreach (var (text, pressed, primary, enabled) in answers)
        {
            var button = BoardStyle.MakeButton(text, 30);
            button.CustomMinimumSize = new Vector2(260, 72);
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
        _answers.AddThemeConstantOverride("separation", 48);
        AddChild(_answers);
    }

    /// <param name="selected">Cards picked so far (bottom selection); highlighted.</param>
    /// <param name="selectable">Cards can be clicked (choosing which to put on the bottom).</param>
    public void ShowHand(IReadOnlyList<CardView> hand, string title, ISet<Arcanum.Engine.Core.CardId> selected, bool selectable)
    {
        Visible = true;
        _title.Text = title;
        foreach (var child in _row.GetChildren()) child.QueueFree();

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
            var card = new CardNode { Size = size, Position = new Vector2(startX + i * (width + Gap), y) };
            _row.AddChild(card);
            card.Setup(hand[i], showCostPips: false);
            card.SetHighlight(selected.Contains(hand[i].Id) ? CardHighlight.Selected : selectable ? CardHighlight.Playable : CardHighlight.None);
            card.Clicked += c => CardClicked?.Invoke(c);
            card.HoverStarted += c => CardHoverStarted?.Invoke(c);
            card.HoverEnded += c => CardHoverEnded?.Invoke(c);
        }
        if (_answers.Visible) PadFocus.Offer(_answers, _main);
    }

    public void HideHand()
    {
        if (!Visible) return;
        Visible = false;
        foreach (var child in _row.GetChildren()) child.QueueFree();
    }
}
