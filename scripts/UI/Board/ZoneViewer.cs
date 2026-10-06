// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Every card in one player's graveyard or exile, opened by clicking that pile. Cards that can be used right now
/// (cast from exile after an Adventure, flashback, graveyard abilities, targets) are highlighted and act on click.
/// </summary>
public partial class ZoneViewer : PanelContainer
{
    private static readonly Vector2 CardSize = BoardStyle.HandCardSize * 1.25f;

    private readonly Label _title = BoardStyle.MakeTitle("", 18, BoardStyle.Text);
    private readonly Label _hint = BoardStyle.MakeLabel("", 13, BoardStyle.TextDim);
    private readonly HFlowContainer _grid = new();

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    /// <summary>Whose zone is shown, and which one.</summary>
    public PlayerId Player { get; private set; }
    public Zone Zone { get; private set; }

    public ZoneViewer()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = BoardStyle.Z.ActionPanel - 5;
        AddThemeStyleboxOverride("panel", BoardStyle.DialogBox(PanelKind.Board, new Color(0.06f, 0.06f, 0.07f, 0.97f), 10, BoardStyle.PanelBorder, 1, artPadding: 10));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_title);
        var close = BoardStyle.MakeButton("Close", 14);
        close.Pressed += Close;
        header.AddChild(close);
        box.AddChild(header);
        box.AddChild(_hint);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        _grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_grid);
        box.AddChild(scroll);
    }

    public void Open(PlayerId player, Zone zone)
    {
        Player = player;
        Zone = zone;
        Visible = true;
    }

    public void Close()
    {
        Visible = false;
        foreach (var child in _grid.GetChildren()) child.QueueFree();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Visible && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <param name="usable">Cards that do something when clicked now (highlighted).</param>
    public void Refresh(GameView view, Func<CardId, bool> usable)
    {
        if (!Visible) return;
        var owner = view.Players[Player.Value];
        var cards = (Zone == Zone.Graveyard ? owner.Graveyard : owner.Exile).Reverse().ToList(); // most recent first
        _title.Text = $"{(Zone == Zone.Graveyard ? "Graveyard" : "Exile")} of {owner.Name} ({cards.Count})";
        int playable = cards.Count(c => usable(c.Id));
        _hint.Text = cards.Count == 0 ? "Empty."
            : playable > 0 ? $"Highlighted cards can be used now ({playable}): click one."
            : "Most recent first. Hover a card to see it large.";

        foreach (var child in _grid.GetChildren()) child.QueueFree();
        foreach (var view1 in cards)
        {
            var node = new CardNode { CustomMinimumSize = CardSize, Size = CardSize };
            node.Setup(view1, showCostPips: false);
            node.SetHighlight(usable(view1.Id) ? CardHighlight.Playable : CardHighlight.None);
            node.Clicked += n => CardClicked?.Invoke(n);
            node.HoverStarted += n => CardHoverStarted?.Invoke(n);
            node.HoverEnded += n => CardHoverEnded?.Invoke(n);
            _grid.AddChild(node);
        }

        // Centered over the board, wide enough for seven cards per row, as tall as needed up to most of the screen.
        var parent = GetParentAreaSize();
        int perRow = Math.Clamp(cards.Count, 4, 7);
        float width = perRow * (CardSize.X + 8) + 40;
        int rows = Math.Max(1, (int)Math.Ceiling(cards.Count / (double)perRow));
        float height = Math.Min(parent.Y * 0.75f, rows * (CardSize.Y + 8) + 100);
        Size = new Vector2(width, height);
        Position = (parent - Size) / 2;
    }
}
