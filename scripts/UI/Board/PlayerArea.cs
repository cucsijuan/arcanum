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
    private IReadOnlyDictionary<CardId, float> _blockerAlign = new Dictionary<CardId, float>();
    private Panel _lifeBox = null!;

    /// <summary>True for the top half: its creatures attack downwards, towards the opponent.</summary>
    public bool FacesDown { get; set; }
    private readonly List<CardNode> _handOrder = new();

    public PlayerId Player { get; set; }

    /// <summary>Smaller cards and margins, for tables with three or four players.</summary>
    public bool Compact { get; init; }

    private float Scale => Compact ? 0.7f : 1f;
    private Vector2 HandSize => BoardStyle.HandCardSize * Scale;
    private Vector2 FieldSize => BoardStyle.BattlefieldCardSize * Scale;
    private Vector2 PileSize => BoardStyle.PileCardSize * Scale;
    private float Peek => PilePeek * Scale;

    private readonly Label _commanderDamage = BoardStyle.MakeLabel("", 13, BoardStyle.Attacking);

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    /// <summary>The life counter or name badge was clicked (choosing this player as a target).</summary>
    public event Action<PlayerId>? PlayerClicked;

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

        var lifeBox = _lifeBox = new Panel { MouseFilter = MouseFilterEnum.Stop, Position = new Vector2(6, 6), Size = new Vector2(92, 46) };
        lifeBox.AddThemeStyleboxOverride("panel", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
        _life.HorizontalAlignment = HorizontalAlignment.Center;
        _life.VerticalAlignment = VerticalAlignment.Center;
        _life.SetAnchorsPreset(LayoutPreset.FullRect);
        lifeBox.AddChild(_life);
        AddChild(lifeBox);
        lifeBox.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) PlayerClicked?.Invoke(Player);
        };

        // Floating mana, shown under the life total while the pool is not empty.
        _commanderDamage.Position = new Vector2(8, 84);
        AddChild(_commanderDamage);
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
            pile.CardClicked += c => CardClicked?.Invoke(c); // e.g. casting a commander from the command zone
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

    public override void _Ready()
    {
        foreach (var pile in new[] { _library, _graveyard, _exile, _command }) pile.SetCardSize(PileSize);
        if (!Compact) return;
        _library.UseCompactLabel("Library");
        _graveyard.UseCompactLabel("Grave");
        _exile.UseCompactLabel("Exile");
        _command.UseCompactLabel("Cmdr");
        _handLabel.AddThemeFontSizeOverride("font_size", 11);
    }

    public void SetPlaymat(Texture2D? texture)
    {
        _playmat.Texture = texture;
        _playmat.Visible = texture is not null;
    }


    /// <summary>Applies a playmat setting: a built-in style id, or "custom:&lt;image path&gt;".</summary>
    public void SetPlaymatStyle(string id)
    {
        Playmat.Apply(_grid, _playmat, id, Player.Value);
    }

    public CardNode? FindCard(CardId id) => _cards.GetValueOrDefault(id);

    public IEnumerable<CardNode> Cards => _cards.Values;

    private void LayoutStatic()
    {
        if (_grid.Material is ShaderMaterial gridMaterial) gridMaterial.SetShaderParameter("rect_size", Size);
        _nameBadge.Position = new Vector2((Size.X - _nameBadge.Size.X) / 2, 6);

        float gap = PileGap * Scale;
        float x = Size.X - SideMargin - PileSize.X;
        float y = Size.Y - Peek;
        foreach (var pile in new[] { _command, _exile, _graveyard, _library })
        {
            pile.Position = new Vector2(x, y);
            x -= PileSize.X + gap;
        }
        _handLabel.Position = new Vector2(_library.Position.X - 78, Size.Y - 22);
    }

    /// <param name="stagedTaps">Sources picked in an unconfirmed mana payment; drawn as tapped.</param>
    /// <param name="attacking">Creatures shown stepped forward towards the opponent (declared or being declared).</param>
    /// <param name="blockerAlign">Blockers to place in front of their attacker: blocker → attacker's global center X.</param>
    public void Refresh(GameView view, bool isActive, IReadOnlySet<CardId>? stagedTaps = null, IReadOnlySet<CardId>? attacking = null,
        IReadOnlyDictionary<CardId, float>? blockerAlign = null)
    {
        _staged = stagedTaps ?? new HashSet<CardId>();
        _attacking = attacking ?? new HashSet<CardId>();
        _blockerAlign = blockerAlign ?? new Dictionary<CardId, float>();
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
        var commanderCard = me.Command.LastOrDefault();
        _command.Refresh(me.Command.Count, commanderCard, commanderCard is { CommanderTax: > 0 } c ? $" +{{{c.CommanderTax}}}" : "");
        _commanderDamage.Text = string.Join("\n", me.CommanderDamage.Where(kv => kv.Value > 0)
            .Select(kv => $"\u2694 {view.FindCard(kv.Key)?.Name ?? "Commander"}: {kv.Value}/21"));
        _commanderDamage.Position = new Vector2(8, me.ManaPool.Count > 0 ? 84 : 58);
        LayoutStatic();

        var battlefield = view.Battlefield.Where(c => c.Controller == Player).ToList();
        var wanted = me.Hand.Concat(battlefield).ToList();
        var wantedIds = wanted.Select(c => c.Id).ToHashSet();

        foreach (var stale in _cards.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            var node = _cards[stale];
            _cards.Remove(stale);
            // Cards that went to one of our piles fly there; anything else just fades out.
            Control? pile = me.Graveyard.Any(c => c.Id == stale) ? _graveyard : me.Exile.Any(c => c.Id == stale) ? _exile : null;
            AnimateAway(node, pile);
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

    private static void AnimateAway(CardNode node, Control? pile)
    {
        node.LayoutTween?.Kill();
        node.MouseFilter = MouseFilterEnum.Ignore;
        node.ZIndex = 150;
        var tween = node.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        float k = BoardStyle.AnimationScale;
        if (pile is not null)
        {
            tween.TweenProperty(node, "position", pile.Position, 0.4 * k);
            tween.TweenProperty(node, "size", pile.Size, 0.4 * k);
            tween.TweenProperty(node, "rotation_degrees", 0f, 0.4 * k);
            tween.TweenProperty(node, "modulate:a", 0.0f, 0.15 * k).SetDelay(0.3 * k);
        }
        else
        {
            tween.TweenProperty(node, "modulate:a", 0.0f, 0.35 * k);
            tween.TweenProperty(node, "scale", new Vector2(0.8f, 0.8f), 0.35 * k);
        }
        tween.Chain().TweenCallback(Callable.From(node.QueueFree));
    }

    /// <summary>The life counter, also the click target for choosing this player.</summary>
    public Control LifeBox => _lifeBox;

    /// <summary>Global center of the life counter, where damage to this player is shown.</summary>
    public Vector2 LifeGlobalCenter => _lifeBox.GetGlobalRect().GetCenter();

    /// <summary>Glows the life counter while this player is a legal target.</summary>
    public void SetPlayerTargetable(bool targetable)
    {
        _lifeBox.AddThemeStyleboxOverride("panel", targetable
            ? BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.Playable, 3)
            : BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
        _lifeBox.MouseDefaultCursorShape = targetable ? CursorShape.PointingHand : CursorShape.Arrow;
    }

    /// <summary>Brief red flash of the life counter.</summary>
    public void FlashLife(Color color)
    {
        _lifeBox.Modulate = color;
        _lifeBox.CreateTween().TweenProperty(_lifeBox, "modulate", Colors.White, 0.5);
    }

    /// <summary>Whether a global point is over this player's hand strip (dropping a card there cancels a drag).</summary>
    public bool IsOverHand(Vector2 globalPoint) =>
        GetGlobalRect().HasPoint(globalPoint) && globalPoint.Y > GlobalPosition.Y + Size.Y - 150;

    /// <summary>Where a card's current layout animation will end, as a global center point.</summary>
    public Vector2? TargetGlobalCenter(CardId id) =>
        _cards.TryGetValue(id, out var node) ? GlobalPosition + node.TargetPosition + node.TargetSize / 2 : null;

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
        node.Size = view.Zone == Zone.Hand ? HandSize : FieldSize;
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
        var size = HandSize;
        float spacing = Mathf.Min(size.X - 8, Mathf.Min(640f, Size.X * 0.5f) / n);
        float totalWidth = spacing * (n - 1) + size.X;
        float startX = (Size.X - totalWidth) / 2;

        for (int i = 0; i < n; i++)
        {
            var node = _handOrder[i];
            float t = n == 1 ? 0 : (i / (float)(n - 1)) * 2 - 1; // -1 .. 1 across the fan
            bool hovered = node.IsHovered;
            var target = new Vector2(startX + i * spacing, Size.Y - Peek + t * t * 8);
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
        float creatureY = Compact ? 56 : 62;
        float landY = creatureY + FieldSize.Y + (Compact ? 14 : 22);

        // Auras and Equipment attached to one of our permanents sit tucked behind it instead of taking a slot.
        var hosts = battlefield.Select(c => c.Id).ToHashSet();
        var attached = battlefield.Where(c => c.AttachedTo is { } host && hosts.Contains(host)).ToList();
        others = others.Except(attached).ToList();
        lands = lands.Except(attached).ToList();

        // Blockers leave their row and stand in front of the attacker they block.
        var blockers = others.Where(c => _blockerAlign.ContainsKey(c.Id)).ToList();
        LayoutRow(others.Except(blockers).ToList(), creatureY);
        LayoutRow(lands, landY);
        LayoutBlockers(blockers, creatureY);
        LayoutAttachments(attached);
    }

    private void LayoutRow(IReadOnlyList<CardView> row, float y)
    {
        if (row.Count == 0) return;
        var size = FieldSize;
        float slot = size.Y + 6; // tapped cards are rotated, so reserve their full height
        float available = Size.X - 2 * (Compact ? 40 : 230);
        float spacing = Mathf.Min(slot, available / row.Count);
        float totalWidth = spacing * (row.Count - 1) + slot;
        float startX = (Size.X - totalWidth) / 2 + (slot - size.X) / 2;

        for (int i = 0; i < row.Count; i++)
        {
            var node = _cards[row[i].Id];
            node.ZIndex = 10 + i; // room below for attachments tucked behind their host
            bool tapped = row[i].Tapped || _staged.Contains(row[i].Id);
            // Attackers step forward towards the opponent.
            float forward = _attacking.Contains(row[i].Id) ? (FacesDown ? 28 : -28) * Scale : 0;
            MoveTo(node, new Vector2(startX + i * spacing, y + forward), size, tapped ? 90 : 0);
        }
    }

    private void LayoutAttachments(IReadOnlyList<CardView> attached)
    {
        foreach (var group in attached.GroupBy(c => c.AttachedTo!.Value))
        {
            var host = _cards[group.Key];
            int i = 1;
            foreach (var card in group)
            {
                var node = _cards[card.Id];
                node.ZIndex = host.ZIndex - 1;
                // Peek out above the host so the attachment stays visible and hoverable.
                MoveTo(node, host.TargetPosition + new Vector2(10 * i, -22 * i), FieldSize, 0);
                i++;
            }
        }
    }

    private void LayoutBlockers(IReadOnlyList<CardView> blockers, float y)
    {
        var size = FieldSize;
        float forward = (FacesDown ? 40 : -40) * Scale;
        // Several blockers on the same attacker stand side by side, centered on it.
        foreach (var group in blockers.GroupBy(b => _blockerAlign[b.Id]))
        {
            var list = group.ToList();
            float slot = size.Y + 6;
            float localCenter = group.Key - GlobalPosition.X;
            for (int i = 0; i < list.Count; i++)
            {
                float x = localCenter + (i - (list.Count - 1) / 2f) * slot - size.X / 2;
                var node = _cards[list[i].Id];
                node.ZIndex = 50 + i;
                bool tapped = list[i].Tapped || _staged.Contains(list[i].Id);
                MoveTo(node, new Vector2(x, y + forward), size, tapped ? 90 : 0);
            }
        }
    }

    private static void MoveTo(CardNode node, Vector2 position, Vector2 size, float rotationDegrees)
    {
        node.TargetPosition = position;
        node.TargetSize = size;
        if (node.IsDragging) return;
        node.LayoutTween?.Kill(); // a newer layout always wins over one still animating
        var tween = node.LayoutTween = node.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        float duration = 0.18f * BoardStyle.AnimationScale;
        tween.TweenProperty(node, "position", position, duration);
        tween.TweenProperty(node, "size", size, duration);
        tween.TweenProperty(node, "rotation_degrees", rotationDegrees, duration);
    }
}
