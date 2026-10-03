// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// One player's half of the board: playmat, life, name badge, battlefield rows,
/// hand fan peeking from the bottom edge and the zone piles in the bottom-right corner.
/// </summary>
public partial class PlayerArea : Control
{
    private const float PileGap = 8;
    private const float PilePeek = 78;   // how much of the hand/piles shows above the bottom edge
    private const float SideMargin = 14;

    private readonly ColorRect _grid = new();
    private readonly TextureRect _playmat = new();
    private readonly Control _cardLayer = new();
    private readonly Panel _activeBorder = new();
    private readonly Label _life = BoardStyle.MakeLabel("20", 26, bold: true);
    private readonly Label _name = BoardStyle.MakeLabel("Player", 14, bold: true);
    private readonly PanelContainer _nameBadge = new();
    private readonly Label _handLabel = BoardStyle.MakeLabel("Hand (0)", 12, BoardStyle.TextDim);
    private readonly ZonePile _library = new("Library");
    private readonly ZonePile _graveyard = new("Graveyard");
    private readonly ZonePile _exile = new("Exile");
    private readonly ZonePile _command = new("Commander");
    private readonly HBoxContainer _pool = new();
    private readonly Dictionary<CardId, CardNode> _cards = new();
    private IReadOnlySet<CardId> _staged = new HashSet<CardId>();
    private IReadOnlySet<CardId> _attacking = new HashSet<CardId>();

    /// <summary>True for the top half: its creatures attack downwards, towards the opponent.</summary>
    public bool FacesDown { get; set; }
    private readonly List<CardNode> _handOrder = new();

    public PlayerId Player { get; set; }

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    public PlayerArea()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Pass;

        _grid.MouseFilter = MouseFilterEnum.Ignore;
        _grid.SetAnchorsPreset(LayoutPreset.FullRect);
        _grid.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grid_playmat.gdshader") };
        AddChild(_grid);

        _playmat.MouseFilter = MouseFilterEnum.Ignore;
        _playmat.SetAnchorsPreset(LayoutPreset.FullRect);
        _playmat.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _playmat.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        _playmat.Modulate = new Color(1, 1, 1, 0.55f); // keep cards readable over busy art
        _playmat.Visible = false;
        AddChild(_playmat);

        var lifeBox = new Panel { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(6, 6), Size = new Vector2(92, 46) };
        lifeBox.AddThemeStyleboxOverride("panel", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
        _life.HorizontalAlignment = HorizontalAlignment.Center;
        _life.VerticalAlignment = VerticalAlignment.Center;
        _life.SetAnchorsPreset(LayoutPreset.FullRect);
        lifeBox.AddChild(_life);
        AddChild(lifeBox);

        // Floating mana, shown under the life total while the pool is not empty.
        _pool.MouseFilter = MouseFilterEnum.Ignore;
        _pool.Position = new Vector2(8, 58);
        _pool.AddThemeConstantOverride("separation", 6);
        AddChild(_pool);

        _nameBadge.MouseFilter = MouseFilterEnum.Ignore;
        _nameBadge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("0e0e10"), 6));
        _nameBadge.AddChild(_name);
        AddChild(_nameBadge);

        AddChild(_handLabel);
        foreach (var pile in new[] { _library, _graveyard, _exile, _command })
        {
            pile.CardHoverStarted += c => CardHoverStarted?.Invoke(c);
            pile.CardHoverEnded += c => CardHoverEnded?.Invoke(c);
            AddChild(pile);
        }

        _cardLayer.MouseFilter = MouseFilterEnum.Ignore;
        _cardLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_cardLayer);

        _activeBorder.MouseFilter = MouseFilterEnum.Ignore;
        _activeBorder.SetAnchorsPreset(LayoutPreset.FullRect);
        _activeBorder.AddThemeStyleboxOverride("panel", BoardStyle.Box(Colors.Transparent, 4, BoardStyle.ActiveBorder, 2));
        _activeBorder.Visible = false;
        AddChild(_activeBorder);

        Resized += LayoutStatic;
    }

    public void SetPlaymat(Texture2D? texture)
    {
        _playmat.Texture = texture;
        _playmat.Visible = texture is not null;
    }

    public CardNode? FindCard(CardId id) => _cards.GetValueOrDefault(id);

    public IEnumerable<CardNode> Cards => _cards.Values;

    private void LayoutStatic()
    {
        ((ShaderMaterial)_grid.Material).SetShaderParameter("rect_size", Size);
        _nameBadge.Position = new Vector2((Size.X - _nameBadge.Size.X) / 2, 6);

        float x = Size.X - SideMargin - BoardStyle.PileCardSize.X;
        float y = Size.Y - PilePeek;
        foreach (var pile in new[] { _command, _exile, _graveyard, _library })
        {
            pile.Position = new Vector2(x, y);
            x -= BoardStyle.PileCardSize.X + PileGap;
        }
        _handLabel.Position = new Vector2(_library.Position.X - 78, Size.Y - 22);
    }

    /// <param name="stagedTaps">Sources picked in an unconfirmed mana payment; drawn as tapped.</param>
    /// <param name="attacking">Creatures shown stepped forward towards the opponent (declared or being declared).</param>
    public void Refresh(GameView view, bool isActive, IReadOnlySet<CardId>? stagedTaps = null, IReadOnlySet<CardId>? attacking = null)
    {
        _staged = stagedTaps ?? new HashSet<CardId>();
        _attacking = attacking ?? new HashSet<CardId>();
        var me = view.Players[Player.Value];
        RefreshPool(me);
        _life.Text = me.Life.ToString();
        _life.AddThemeColorOverride("font_color", me.HasLost ? BoardStyle.TextDim : BoardStyle.Text);
        _name.Text = me.HasLost ? $"{me.Name} (defeated)" : me.Name;
        _nameBadge.ResetSize();
        _activeBorder.Visible = isActive;
        _handLabel.Text = $"⌄ Hand ({me.Hand.Count})";

        // The library always shows the card back; we build a hidden view rather than reveal its top.
        CardView? libraryTop = me.LibraryCount > 0
            ? new CardView { Id = new CardId(-1 - Player.Value), Owner = Player, Controller = Player, Zone = Zone.Library, IsHidden = true }
            : null;
        _library.Refresh(me.LibraryCount, libraryTop);
        _graveyard.Refresh(me.Graveyard.Count, me.Graveyard.LastOrDefault());
        _exile.Refresh(me.Exile.Count, me.Exile.LastOrDefault());
        _command.Refresh(me.Command.Count, me.Command.LastOrDefault());
        LayoutStatic();

        var battlefield = view.Battlefield.Where(c => c.Controller == Player).ToList();
        var wanted = me.Hand.Concat(battlefield).ToList();
        var wantedIds = wanted.Select(c => c.Id).ToHashSet();

        foreach (var stale in _cards.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            _cards[stale].QueueFree();
            _cards.Remove(stale);
        }

        foreach (var cardView in wanted)
        {
            if (!_cards.TryGetValue(cardView.Id, out var node))
            {
                node = CreateCardNode(cardView);
                _cards[cardView.Id] = node;
            }
            node.Setup(cardView, showCostPips: cardView.Zone == Zone.Hand);
            node.SetHighlight(CardHighlight.None);
            node.SetAssignedDamage(null);
        }

        _handOrder.Clear();
        _handOrder.AddRange(me.Hand.Select(c => _cards[c.Id]));
        LayoutHand();
        LayoutBattlefield(battlefield);
    }

    private void RefreshPool(PlayerView me)
    {
        foreach (var child in _pool.GetChildren()) child.QueueFree();
        foreach (var (type, amount) in me.ManaPool)
        {
            var entry = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            entry.AddThemeConstantOverride("separation", 2);
            entry.AddChild(BoardStyle.MakePip(type.ToSymbol().ToString(), 20, 12));
            entry.AddChild(BoardStyle.MakeLabel($"\u00d7{amount}", 13));
            _pool.AddChild(entry);
        }
        _pool.TooltipText = "Floating mana";
    }

    private CardNode CreateCardNode(CardView view)
    {
        var node = new CardNode();
        // New cards slide in from the library pile.
        node.Size = view.Zone == Zone.Hand ? BoardStyle.HandCardSize : BoardStyle.BattlefieldCardSize;
        node.Position = _library.Position;
        node.Clicked += c => CardClicked?.Invoke(c);
        node.HoverStarted += c =>
        {
            CardHoverStarted?.Invoke(c);
            if (_handOrder.Contains(c)) LayoutHand();
        };
        node.HoverEnded += c =>
        {
            CardHoverEnded?.Invoke(c);
            if (_handOrder.Contains(c)) LayoutHand();
        };
        _cardLayer.AddChild(node);
        return node;
    }

    private void LayoutHand()
    {
        int n = _handOrder.Count;
        if (n == 0) return;
        var size = BoardStyle.HandCardSize;
        float spacing = Mathf.Min(size.X - 8, 640f / n);
        float totalWidth = spacing * (n - 1) + size.X;
        float startX = (Size.X - totalWidth) / 2;

        for (int i = 0; i < n; i++)
        {
            var node = _handOrder[i];
            float t = n == 1 ? 0 : (i / (float)(n - 1)) * 2 - 1; // -1 .. 1 across the fan
            bool hovered = node.IsHovered;
            var target = new Vector2(startX + i * spacing, Size.Y - PilePeek + t * t * 8);
            float rotation = t * 6f;
            if (hovered)
            {
                target.Y = Size.Y - size.Y - 2; // bottom edge stays under the cursor, so hover never flickers
                rotation = 0;
            }
            node.ZIndex = hovered ? 100 : i;
            MoveTo(node, target, size, rotation);
        }
    }

    private void LayoutBattlefield(IReadOnlyList<CardView> battlefield)
    {
        var lands = battlefield.Where(c => (c.Types & CardType.Land) != 0).ToList();
        var others = battlefield.Where(c => (c.Types & CardType.Land) == 0).ToList();
        float creatureY = 62;
        float landY = creatureY + BoardStyle.BattlefieldCardSize.Y + 22;
        LayoutRow(others, creatureY);
        LayoutRow(lands, landY);
    }

    private void LayoutRow(IReadOnlyList<CardView> row, float y)
    {
        if (row.Count == 0) return;
        var size = BoardStyle.BattlefieldCardSize;
        float slot = size.Y + 6; // tapped cards are rotated, so reserve their full height
        float available = Size.X - 2 * 230;
        float spacing = Mathf.Min(slot, available / row.Count);
        float totalWidth = spacing * (row.Count - 1) + slot;
        float startX = (Size.X - totalWidth) / 2 + (slot - size.X) / 2;

        for (int i = 0; i < row.Count; i++)
        {
            var node = _cards[row[i].Id];
            node.ZIndex = i;
            bool tapped = row[i].Tapped || _staged.Contains(row[i].Id);
            // Attackers step forward towards the opponent.
            float forward = _attacking.Contains(row[i].Id) ? (FacesDown ? 28 : -28) : 0;
            MoveTo(node, new Vector2(startX + i * spacing, y + forward), size, tapped ? 90 : 0);
        }
    }

    private static void MoveTo(CardNode node, Vector2 position, Vector2 size, float rotationDegrees)
    {
        node.LayoutTween?.Kill(); // a newer layout always wins over one still animating
        var tween = node.LayoutTween = node.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(node, "position", position, 0.18);
        tween.TweenProperty(node, "size", size, 0.18);
        tween.TweenProperty(node, "rotation_degrees", rotationDegrees, 0.18);
    }
}
