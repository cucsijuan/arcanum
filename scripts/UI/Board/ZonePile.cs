// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>A labelled stack (Library, Graveyard, Exile, Commander) showing its top card.</summary>
public partial class ZonePile : Control
{
    private string _title;
    private readonly Label _label;
    private readonly Panel _slot = new();
    private readonly CardNode _top = new();

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    public ZonePile(string title)
    {
        _title = title;
        MouseFilter = MouseFilterEnum.Ignore;
        Size = BoardStyle.PileCardSize;

        _label = BoardStyle.MakeLabel(title, 13);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.Position = new Vector2(-20, -22);
        _label.Size = new Vector2(Size.X + 40, 20);
        AddChild(_label);

        _slot.MouseFilter = MouseFilterEnum.Ignore;
        _slot.Size = Size;
        _slot.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("17181b"), 6, BoardStyle.PanelBorder, 1));
        AddChild(_slot);

        _top.Size = Size;
        _top.Visible = false;
        _top.HoverStarted += c => CardHoverStarted?.Invoke(c);
        _top.Clicked += c => CardClicked?.Invoke(c);
        _top.HoverEnded += c => CardHoverEnded?.Invoke(c);
        AddChild(_top);
    }

    /// <summary>Resizes the pile (compact player areas use smaller cards).</summary>
    public void SetCardSize(Vector2 size)
    {
        Size = size;
        _slot.Size = size;
        _top.Size = size;
        _label.Size = new Vector2(size.X + 40, 20);
    }

    public CardNode TopCard => _top;

    /// <summary>Short title and smaller font for compact areas, where full labels would overlap.</summary>
    public void UseCompactLabel(string shortTitle)
    {
        _title = shortTitle;
        _label.AddThemeFontSizeOverride("font_size", 11);
        _label.Position = new Vector2(-6, -18);
        _label.Size = new Vector2(Size.X + 12, 18);
    }

    /// <summary>Changes the title shown above the pile.</summary>
    public void SetTitle(string title) => _title = title;

    /// <param name="topCard">Card to show face up/down on top, or null for an empty slot.</param>
    /// <param name="note">Extra text after the count, e.g. commander tax.</param>
    public void Refresh(int count, CardView? topCard, string note = "")
    {
        _label.Text = $"{_title} ({count}){note}";
        _top.Visible = topCard is not null;
        if (topCard is not null) _top.Setup(topCard, showCostPips: false);
    }
}
