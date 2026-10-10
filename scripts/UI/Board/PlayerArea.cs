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

    /// <summary>Who plays the seat now, in online games ("computer", "disconnected 1:42"); shown beside the name.</summary>
    public string? SeatNote { get; set; }
    private readonly PanelContainer _nameBadge = new();

    /// <summary>What applies to the player for the rest of the game or for now (Ring, city's blessing, poison…), beside the name.</summary>
    private readonly HBoxContainer _tags = new();
    private readonly Label _handLabel = BoardStyle.MakeLabel("Hand (0)", 12, BoardStyle.TextDim);

    /// <summary>Who is looking at this hand right now (an opponent's spell or ability lets them), beside the hand label.</summary>
    private readonly Label _handNote = BoardStyle.MakeLabel("", 12, new Color("f2c14e"), bold: true);
    private readonly ZonePile _library = new("Library");
    private readonly ZonePile _graveyard = new("Graveyard");
    private readonly ZonePile _exile = new("Exile");
    private readonly ZonePile _command = new("Commander");

    /// <summary>The second commander in the command zone (partners, a Background).</summary>
    private readonly ZonePile _command2 = new("Commander");
    private readonly HBoxContainer _pool = new();
    private readonly Dictionary<CardId, CardNode> _cards = new();
    private IReadOnlySet<CardId> _staged = new HashSet<CardId>();
    private IReadOnlySet<CardId> _attacking = new HashSet<CardId>();
    private IReadOnlyDictionary<CardId, float> _blockerAlign = new Dictionary<CardId, float>();
    private Panel _lifeBox = null!;

    /// <summary>
    /// True for the top half, laid out as the bottom half turned over: its edge (hand, piles, lands) is the top of the
    /// screen, its creatures stand nearest the middle and attack downwards, towards the opponent.
    /// </summary>
    public bool FacesDown { get; set; }

    /// <summary>Top of a rectangle <paramref name="fromMiddle"/> pixels from the middle-facing side of this half (the top side in the bottom half, the bottom side in the top half).</summary>
    private float FlipY(float fromMiddle, float height) => FacesDown ? Size.Y - fromMiddle - height : fromMiddle;
    private readonly List<CardNode> _handOrder = new();

    public PlayerId Player { get; set; }

    /// <summary>Smaller cards and margins, for tables with three or four players.</summary>
    public bool Compact { get; init; }

    /// <summary>Room kept free at the right edge (the menu buttons sit over the top half's piles there).</summary>
    public float RightInset { get; init; }

    private float Scale => Compact ? 0.7f : 1f;
    private Vector2 HandSize => BoardStyle.HandCardSize * Scale;
    private Vector2 FieldSize => BoardStyle.BattlefieldCardSize * Scale;
    private Vector2 PileSize => BoardStyle.PileCardSize * Scale;
    private float Peek => PilePeek * Scale;

    /// <summary>Room kept above the hand for the cost pips its cards show over their top edge.</summary>
    private const float HandPipsRoom = 22;

    private bool _poolShown;
    private readonly Label _commanderDamage = BoardStyle.MakeLabel("", 13, BoardStyle.Attacking);

    public event Action<CardNode>? CardClicked;
    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    /// <summary>This player's graveyard or exile pile was clicked (to see every card in it).</summary>
    public event Action<PlayerId, Zone>? ZoneClicked;

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
        _tags.MouseFilter = MouseFilterEnum.Ignore;
        _tags.AddThemeConstantOverride("separation", 4);
        AddChild(_tags);

        AddChild(_handLabel);
        _handNote.Visible = false;
        AddChild(_handNote);
        _command2.Visible = false;
        _graveyard.CardClicked += _ => ZoneClicked?.Invoke(Player, Zone.Graveyard);
        _exile.CardClicked += _ => ZoneClicked?.Invoke(Player, Zone.Exile);
        foreach (var pile in new[] { _library, _graveyard, _exile, _command, _command2 })
        {
            if (pile != _graveyard && pile != _exile) pile.CardClicked += c => CardClicked?.Invoke(c); // e.g. casting a commander from the command zone
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
        foreach (var pile in new[] { _library, _graveyard, _exile, _command, _command2 })
        {
            pile.LabelBelow = FacesDown;
            pile.TopCard.BackUpsideDown = FacesDown;
            pile.SetCardSize(PileSize);
        }
        if (!Compact) return;
        _library.UseCompactLabel("Library");
        _graveyard.UseCompactLabel("Grave");
        _exile.UseCompactLabel("Exile");
        _command.UseCompactLabel("Cmdr");
        _command2.UseCompactLabel("Cmdr");
        _handLabel.AddThemeFontSizeOverride("font_size", 11);
    }

    public void SetPlaymat(Texture2D? texture)
    {
        _playmat.Texture = texture;
        _playmat.Visible = texture is not null;
    }


    private string? _playmatStyle;

    /// <summary>Applies a playmat setting: a built-in style id, or "custom:&lt;image path&gt;".</summary>
    public void SetPlaymatStyle(string id)
    {
        if (id == _playmatStyle) return;
        _playmatStyle = id;
        Playmat.Apply(_grid, _playmat, id, Player.Value);
    }

    /// <summary>
    /// The node that shows a card: its own, or the top of the stack of identical tokens it is part of
    /// (so highlights, arrows and effects aimed at any token of a stack land on what the player sees).
    /// </summary>
    public CardNode? FindCard(CardId id) => _shownBy.TryGetValue(id, out var top) ? top : _cards.GetValueOrDefault(id);

    /// <summary>Marks that make otherwise identical tokens differ for now (picked as an attacker, a target...); a different mark splits a stack.</summary>
    public Func<CardId, string?>? StackMark { get; set; }

    private readonly Dictionary<CardId, CardNode> _shownBy = new();
    private readonly Dictionary<CardId, CardStack> _stackOf = new();
    private readonly Dictionary<CardId, string> _kindOf = new();
    private readonly Dictionary<string, List<CardId>> _kinds = new();

    /// <summary>Every token on the stack this card is part of, the one drawn on top first; just the card when it stands alone.</summary>
    public IReadOnlyList<CardId> StackMembers(CardId id) =>
        _stackOf.TryGetValue(id, out var stack) ? stack.Cards.Select(c => c.Id).ToList() : new[] { id };

    /// <summary>Every token that looks the same as this one, whatever their marks (the stacks of attackers and non-attackers of one kind).</summary>
    public IReadOnlyList<CardId> SameKind(CardId id) =>
        _kindOf.TryGetValue(id, out var kind) ? _kinds[kind] : new[] { id };

    public IEnumerable<CardNode> Cards => _cards.Values;

    private void LayoutStatic()
    {
        if (_grid.Material is ShaderMaterial gridMaterial) gridMaterial.SetShaderParameter("rect_size", Size);
        _lifeBox.Position = new Vector2(6, FlipY(6, _lifeBox.Size.Y));
        _pool.Position = new Vector2(8, FlipY(58, _pool.GetCombinedMinimumSize().Y));
        _commanderDamage.Position = new Vector2(8, FlipY(_poolShown ? 84 : 58, _commanderDamage.GetCombinedMinimumSize().Y));
        _nameBadge.Position = new Vector2((Size.X - _nameBadge.Size.X) / 2, FlipY(6, _nameBadge.Size.Y));
        _tags.ResetSize();
        _tags.Position = new Vector2(_nameBadge.Position.X + _nameBadge.Size.X + 6, _nameBadge.Position.Y + (_nameBadge.Size.Y - _tags.Size.Y) / 2);

        float gap = PileGap * Scale;
        float x = Size.X - SideMargin - RightInset - PileSize.X;
        float y = FacesDown ? Peek - PileSize.Y : Size.Y - Peek;
        foreach (var pile in new[] { _command2, _command, _exile, _graveyard, _library })
        {
            if (!pile.Visible) continue;
            pile.Position = new Vector2(x, y);
            x -= PileSize.X + gap;
        }
        _handLabel.Position = new Vector2(_library.Position.X - 78, FacesDown ? 4 : Size.Y - 22);
        _handNote.ResetSize();
        _handNote.Position = new Vector2(_handLabel.Position.X - _handNote.Size.X - 10, _handLabel.Position.Y);
    }

    /// <summary>Shows who is looking at this hand ("Ana is looking at your hand"); null hides it.</summary>
    public void SetHandNote(string? note)
    {
        _handNote.Text = note ?? "";
        _handNote.Visible = note is not null;
        LayoutStatic();
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
        _name.Text = (me.HasLost ? $"{me.Name} (defeated)" : me.Name) + (SeatNote is { } note ? $" · {note}" : "");
        _nameBadge.ResetSize();
        RefreshTags(me, view);
        _activeBorder.Visible = isActive;
        ApplyTurnGlow(isActive);
        _handLabel.Text = $"{(FacesDown ? "⌃" : "⌄")} Hand ({me.Hand.Count})";

        // The library always shows the card back; we build a hidden view rather than reveal its top.
        CardView? libraryTop = me.LibraryTop ?? (me.LibraryCount > 0
            ? new CardView { Id = new CardId(-1 - Player.Value), Owner = Player, Controller = Player, Zone = Zone.Library, IsHidden = true }
            : null);
        _library.Refresh(me.LibraryCount, libraryTop);
        _graveyard.Refresh(me.Graveyard.Count, me.Graveyard.LastOrDefault());
        // Cards exiled only while a permanent stays on the battlefield sit under that permanent, not in the pile.
        var pileExile = me.Exile.Where(c => c.HeldUnder is null).ToList();
        _exile.Refresh(pileExile.Count, pileExile.LastOrDefault());
        // Each commander has its own pile, so either can be cast; the tax is per commander (rule 903.8).
        string Tax(CardView? c) => c is { CommanderTax: > 0 } ? $" +{{{c.CommanderTax}}}" : "";
        var commanderCard = me.Command.FirstOrDefault();
        var secondCommander = me.Command.Count > 1 ? me.Command[1] : null;
        // Two piles side by side need short titles so they don't overlap.
        _command.SetTitle(secondCommander is not null || Compact ? "Cmdr" : "Commander");
        _command2.SetTitle("Cmdr");
        _command.Refresh(me.Command.Count > 1 ? 1 : me.Command.Count, commanderCard, Tax(commanderCard));
        _command2.Visible = secondCommander is not null;
        _command2.Refresh(1, secondCommander, Tax(secondCommander));
        _commanderDamage.Text = string.Join("\n", me.CommanderDamage.Where(kv => kv.Value > 0)
            .Select(kv => $"\u2694 {view.FindCard(kv.Key)?.Name ?? "Commander"}: {kv.Value}/21"));
        _poolShown = me.ManaPool.Count > 0;
        LayoutStatic();

        // An Aura or Equipment is shown on the permanent it's attached to, even on another player's side.
        PlayerId SideOf(CardView c) => c.AttachedTo is { } host && view.FindCard(host) is { Zone: Zone.Battlefield } h ? h.Controller : c.Controller;
        var battlefield = view.Battlefield.Where(c => SideOf(c) == Player).ToList();
        // Exiled cards (any owner's) held under a permanent shown on this side are drawn tucked under it.
        var held = view.Players.SelectMany(p => p.Exile)
            .Where(c => c.HeldUnder is { } host && view.Battlefield.FirstOrDefault(b => b.Id == host) is { } h && SideOf(h) == Player).ToList();
        var wanted = me.Hand.Concat(battlefield).Concat(held).ToList();
        var wantedIds = wanted.Select(c => c.Id).ToHashSet();

        foreach (var stale in _cards.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            var node = _cards[stale];
            _cards.Remove(stale);
            // Cards that went to one of our piles fly there; anything else just fades out.
            Control? pile = me.Graveyard.Any(c => c.Id == stale) ? _graveyard : pileExile.Any(c => c.Id == stale) ? _exile : null;
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
            node.SetCaption(null);
            node.SetStack(1, false);
        }

        _handOrder.Clear();
        _handOrder.AddRange(me.Hand.Select(c => _cards[c.Id]));
        LayoutHand();
        LayoutBattlefield(battlefield, held, view);
        SortByDrawOrder();
    }

    private static readonly string[] RingAbilities =
    {
        "Your Ring-bearer is legendary and can't be blocked by creatures with greater power.",
        "Whenever your Ring-bearer attacks, draw a card, then discard a card.",
        "Whenever your Ring-bearer becomes blocked by a creature, that creature's controller sacrifices it at end of combat.",
        "Whenever your Ring-bearer deals combat damage to a player, each opponent loses 3 life.",
    };

    /// <summary>Player-wide states as chips beside the name, each with what it means as its tooltip.</summary>
    private void RefreshTags(PlayerView me, GameView view)
    {
        foreach (var child in _tags.GetChildren()) { _tags.RemoveChild(child); child.QueueFree(); }
        if (me.RingLevel > 0)
        {
            var bearer = me.RingBearer is { } id ? view.FindCard(id)?.Name : null;
            AddTag($"Ring {Math.Min(me.RingLevel, 4)}" + (bearer is not null ? $" · {bearer}" : ""), new Color("f2c14e"),
                $"The Ring has tempted {me.Name} {me.RingLevel} time{(me.RingLevel == 1 ? "" : "s")}.\n"
                + (bearer is not null ? $"Ring-bearer: {bearer}." : "No Ring-bearer right now.") + "\n\n"
                + string.Join("\n", RingAbilities.Take(Math.Min(me.RingLevel, 4))));
        }
        if (view.Monarch == me.Id) AddTag("Monarch", new Color("ffd166"), $"{me.Name} is the monarch: they draw a card at the beginning of their end step. A creature that deals combat damage to them makes its controller the monarch.");
        if (me.EnduringStory) AddTag("Enduring story", new Color("b48cff"), $"{me.Name} has an enduring story for the rest of the game.");
        if (me.CitysBlessing) AddTag("City's blessing", new Color("6fd08c"), $"{me.Name} has the city's blessing for the rest of the game.");
        if (me.Protected) AddTag("Protection", new Color("7fb2ff"), $"{me.Name} has protection from everything until their next turn.");
        if (me.NoMaximumHandSize) AddTag("No max hand size", BoardStyle.TextDim, $"{me.Name} has no maximum hand size for the rest of the game.");
        foreach (var emblem in me.Emblems)
            AddTag($"Emblem: {emblem.Name}", new Color("e89ad8"), $"{me.Name}'s emblem{(emblem.UntilEndOfTurn ? " (until end of turn)" : "")}:\n{emblem.Text}");
        if (me.Poison > 0) AddTag($"Poison {me.Poison}/10", new Color("8fd14f"), $"{me.Name} has {me.Poison} poison counter{(me.Poison == 1 ? "" : "s")}; ten or more loses the game.");
    }

    private void AddTag(string text, Color accent, string tooltip)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = tooltip };
        var box = BoardStyle.Box(new Color(0.08f, 0.08f, 0.1f, 0.94f), 6, accent, 1);
        box.BorderWidthLeft = 4;
        box.SetContentMarginAll(Compact ? 2 : 3);
        box.ContentMarginLeft = Compact ? 6 : 8;
        box.ContentMarginRight = Compact ? 6 : 8;
        panel.AddThemeStyleboxOverride("panel", box);
        panel.AddChild(BoardStyle.MakeLabel(text, Compact ? 11 : 12, accent.Lerp(Colors.White, 0.35f), bold: true));
        _tags.AddChild(panel);
    }

    /// <summary>
    /// Mouse input goes to the control that is last in the tree, not to the one with the highest z-index. Keeping the
    /// tree in z order makes the card drawn on top (e.g. a creature over its aura) the one that gets the hover.
    /// </summary>
    private void SortByDrawOrder()
    {
        int index = 0;
        foreach (var node in _cards.Values.OrderBy(n => n.ZIndex)) _cardLayer.MoveChild(node, index++);
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
        GetGlobalRect().HasPoint(globalPoint) && (FacesDown ? globalPoint.Y < GlobalPosition.Y + 150 : globalPoint.Y > GlobalPosition.Y + Size.Y - 150);

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
        var node = new CardNode { BackUpsideDown = FacesDown };
        // New cards slide in from the library pile.
        node.Size = view.Zone == Zone.Hand ? HandSize : FieldSize;
        node.Position = _library.Position;
        node.Clicked += c => CardClicked?.Invoke(c);
        node.HoverStarted += c =>
        {
            CardHoverStarted?.Invoke(c);
            if (_handOrder.Contains(c)) { LayoutHand(); SortByDrawOrder(); }
            LiftGroup(c);
        };
        node.HoverEnded += c =>
        {
            CardHoverEnded?.Invoke(c);
            if (_handOrder.Contains(c)) { LayoutHand(); SortByDrawOrder(); }
            if (_hoveredInGroup == c) DropGroup();
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
            // The fan hangs from the top edge in the top half (the bottom half's fan turned over).
            var target = new Vector2(startX + i * spacing, FacesDown ? Peek - size.Y - t * t * 8 : Size.Y - Peek + t * t * 8);
            float rotation = FacesDown ? -t * 6f : t * 6f;
            if (hovered)
            {
                target.Y = FacesDown ? 2 : Size.Y - size.Y - 2; // the edge stays under the cursor, so hover never flickers
                rotation = 0;
            }
            node.ZIndex = hovered ? 100 : i;
            MoveTo(node, target, size, rotation);
        }
    }

    // Z index of the first card of each row: the front row draws over the ones behind it when a short table squeezes them together.
    private const int LandsZ = 10, OtherZ = 110, CreaturesZ = 210, BlockersZ = 270;
    private const float StackStep = 4;   // how far each token behind the top of a stack peeks out
    private const int StackPeeks = 3;

    /// <summary>
    /// Rows from the top of this half: creatures, then the other permanents (artifacts, enchantments, planeswalkers,
    /// unattached Equipment), then lands. Identical tokens stand as one stack; an Aura or Equipment attached to one of our
    /// permanents tucks behind it instead of taking a slot, and so do the cards held under one.
    /// </summary>
    private void LayoutBattlefield(IReadOnlyList<CardView> battlefield, IReadOnlyList<CardView> held, GameView view)
    {
        _shownBy.Clear();
        _stackOf.Clear();
        _kindOf.Clear();
        _kinds.Clear();

        var hosts = battlefield.Select(c => c.Id).ToHashSet();
        var attached = battlefield.Where(c => c.AttachedTo is { } host && hosts.Contains(host)).ToList();
        var carrying = attached.Select(c => c.AttachedTo!.Value).Concat(held.Select(c => c.HeldUnder!.Value)).ToHashSet();
        var free = battlefield.Except(attached).ToList();

        // Blockers leave their row and stand in front of the attacker they block.
        var blockers = free.Where(c => BattlefieldLayout.RowOf(c) != BattlefieldRow.Lands && _blockerAlign.ContainsKey(c.Id)).ToList();
        var inRows = free.Except(blockers).ToList();

        var attacks = view.Attacks.ToDictionary(a => a.Attacker);
        bool Tapped(CardView c) => c.Tapped || _staged.Contains(c.Id);
        string? KeyOf(CardView c)
        {
            if (BattlefieldLayout.StackKey(c, Tapped(c), carrying.Contains(c.Id)) is not { } kind) return null;
            if (!_kindOf.ContainsKey(c.Id))
            {
                _kindOf[c.Id] = kind;
                if (!_kinds.TryGetValue(kind, out var list)) _kinds[kind] = list = new List<CardId>();
                list.Add(c.Id);
            }
            string attack = attacks.TryGetValue(c.Id, out var a) ? $"{a.Defender.Value}/{a.Planeswalker}/{a.IsBlocked}/{string.Join(',', a.Blockers)}" : "";
            return $"{kind}#{StackMark?.Invoke(c.Id)}#{attack}#{(_attacking.Contains(c.Id) ? 1 : 0)}#{(_blockerAlign.TryGetValue(c.Id, out var x) ? x : 0):0}";
        }

        IReadOnlyList<CardStack> Stacks(IEnumerable<CardView> cards)
        {
            var stacks = BattlefieldLayout.Stack(cards, KeyOf);
            foreach (var stack in stacks)
                foreach (var card in stack.Cards) _stackOf[card.Id] = stack;
            return stacks;
        }

        var creatures = Stacks(inRows.Where(c => BattlefieldLayout.RowOf(c) == BattlefieldRow.Creatures));
        var others = Stacks(inRows.Where(c => BattlefieldLayout.RowOf(c) == BattlefieldRow.Other));
        var lands = Stacks(inRows.Where(c => BattlefieldLayout.RowOf(c) == BattlefieldRow.Lands));
        var blockerStacks = Stacks(blockers);

        float creatureY = Compact ? 56 : 62;
        var rows = new List<IReadOnlyList<CardStack>> { creatures };
        if (others.Count > 0) rows.Add(others);
        if (lands.Count > 0) rows.Add(lands);

        // Cards lying under a permanent peek out above it, title by title. Their titles go into the gap above their row:
        // on this side the gap towards the middle (before the row, slot 0's being the margin by the middle), across the
        // table the gap towards that player's edge (after the row). With room to spare a gap grows to hold the titles
        // peeking into it; without, they show whole over the edge of the next row, and the rows close up as before.
        var underCount = attached.Select(c => c.AttachedTo!.Value).Concat(held.Select(c => c.HeldUnder!.Value))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        int Under(IReadOnlyList<CardStack> row) => row.Count == 0 ? 0 : row.Max(st => underCount.GetValueOrDefault(st.Top.Id));
        float baseGap = Compact ? 14 : 22;
        int TitlesInGap(int g) => FacesDown ? Under(rows[g]) : Under(rows[g + 1]); // the gap between rows g and g + 1
        var gaps = Enumerable.Range(0, rows.Count - 1).Select(g => Math.Max(baseGap, TitlesInGap(g) * TitleStrip * Scale + 3)).ToList();
        float room = Size.Y - Peek - HandPipsRoom - creatureY - FieldSize.Y * rows.Count;
        if (gaps.Count > 0 && Size.Y > 100 && gaps.Sum() > room)
        {
            float plain = Mathf.Max(36 - FieldSize.Y, Mathf.Min(baseGap, room / gaps.Count));
            gaps = gaps.Select(_ => plain).ToList();
        }
        var offsets = new List<float> { creatureY };
        foreach (var gap in gaps) offsets.Add(offsets[^1] + FieldSize.Y + gap);
        float RowY(int index) => FlipY(offsets[index], FieldSize.Y);
        for (int r = 0; r < rows.Count; r++)
            LayoutRow(rows[r], RowY(r), rows[r] == creatures ? CreaturesZ : rows[r] == others ? OtherZ : LandsZ);
        LayoutBlockers(blockerStacks, RowY(0));
        LayoutUnder(attached, held);
    }

    private void LayoutRow(IReadOnlyList<CardStack> row, float y, int zBase)
    {
        if (row.Count == 0) return;
        var size = FieldSize;
        float peek = StackStep * Math.Min(row.Max(s => s.Count) - 1, StackPeeks);
        float slot = size.Y + 6 + peek; // tapped cards are rotated, so reserve their full height
        float available = Size.X - 2 * (Compact ? 40 : 230);
        float spacing = Mathf.Min(slot, available / row.Count);
        float totalWidth = spacing * (row.Count - 1) + slot;
        float startX = (Size.X - totalWidth) / 2 + (slot - peek - size.X) / 2;

        for (int i = 0; i < row.Count; i++)
        {
            var top = row[i].Top;
            bool tapped = top.Tapped || _staged.Contains(top.Id);
            // Attackers step forward towards the opponent.
            float forward = _attacking.Contains(top.Id) ? (FacesDown ? 28 : -28) * Scale : 0;
            PlaceStack(row[i], new Vector2(startX + i * spacing, y + forward), size, tapped ? 90 : 0, zBase + Math.Min(i * 4, 90) + StackPeeks);
        }
    }

    /// <summary>Puts the top token of a stack at <paramref name="position"/> and the others just behind it, peeking out.</summary>
    private void PlaceStack(CardStack stack, Vector2 position, Vector2 size, float rotationDegrees, int z)
    {
        var top = _cards[stack.Top.Id];
        top.ZIndex = z;
        top.SetStack(stack.Count, false);
        MoveTo(top, position, size, rotationDegrees);
        for (int k = 0; k < stack.Count; k++) _shownBy[stack.Cards[k].Id] = top;
        for (int k = 1; k < stack.Count; k++)
        {
            var behind = _cards[stack.Cards[k].Id];
            int depth = Math.Min(k, StackPeeks);
            behind.ZIndex = z - depth;
            behind.SetStack(1, true);
            MoveTo(behind, position + new Vector2(StackStep * depth, -StackStep * depth), size, rotationDegrees);
        }
    }

    /// <summary>
    /// Auras and Equipment attached to a permanent, then the cards held in exile under it, lie behind it one under the
    /// other, each peeking out just above the card in front of it so every title shows.
    /// </summary>
    private void LayoutUnder(IReadOnlyList<CardView> attached, IReadOnlyList<CardView> held)
    {
        _groups.Clear();
        _liftedGroup = null; // the layout just set every depth again
        var under = attached.Select(c => (Card: c, Host: c.AttachedTo!.Value)).Concat(held.Select(c => (Card: c, Host: c.HeldUnder!.Value)));
        foreach (var group in under.GroupBy(x => x.Host))
        {
            var host = _cards[group.Key];
            var members = new List<CardNode> { host };
            _groups[host] = members;
            int i = 1;
            foreach (var (card, _) in group)
            {
                var node = _cards[card.Id];
                members.Add(node);
                _groups[node] = members;
                node.ZIndex = host.ZIndex - i;
                MoveTo(node, host.TargetPosition + new Vector2(0, -TitleStrip * Scale * i), FieldSize, 0);
                i++;
            }
        }
        // Still pointed at after the board changed: the group stays lifted.
        if (_hoveredInGroup is { } hovered && _groups.ContainsKey(hovered)) Lift(_groups[hovered]);
        else _hoveredInGroup = null;
    }

    /// <summary>A permanent and the cards under it, by each of them.</summary>
    private readonly Dictionary<CardNode, List<CardNode>> _groups = new();
    private List<CardNode>? _liftedGroup;
    private readonly List<int> _liftedDepths = new();
    private CardNode? _hoveredInGroup;

    /// <summary>The depth of a lifted permanent: above every row and blocker, below the board's overlays (stack, preview...).</summary>
    private const int LiftedZ = 360;

    /// <summary>Pointing at a permanent or a card under it brings the whole group above the rows around it while pointed at.</summary>
    private void LiftGroup(CardNode hovered)
    {
        DropGroup();
        if (!_groups.TryGetValue(hovered, out var group)) return;
        _hoveredInGroup = hovered;
        Lift(group);
        SortByDrawOrder();
    }

    /// <summary>Raises a group to <see cref="LiftedZ"/>, the cards under the permanent still one behind the other.</summary>
    private void Lift(List<CardNode> group)
    {
        _liftedGroup = group;
        _liftedDepths.Clear();
        _liftedDepths.AddRange(group.Select(n => n.ZIndex));
        for (int i = 0; i < group.Count; i++) group[i].ZIndex = LiftedZ - i;
    }

    private void DropGroup()
    {
        if (_liftedGroup is { } group)
            for (int i = 0; i < group.Count; i++)
                if (IsInstanceValid(group[i])) group[i].ZIndex = _liftedDepths[i];
        _liftedGroup = null;
        _hoveredInGroup = null;
        SortByDrawOrder();
    }

    /// <summary>How much of a card behind another shows above it: its title.</summary>
    private const float TitleStrip = 19;

    private void LayoutBlockers(IReadOnlyList<CardStack> blockers, float y)
    {
        var size = FieldSize;
        float forward = (FacesDown ? 40 : -40) * Scale;
        // Several blockers on the same attacker stand side by side, centered on it.
        foreach (var group in blockers.GroupBy(b => _blockerAlign[b.Top.Id]))
        {
            var list = group.ToList();
            float slot = size.Y + 6;
            float localCenter = group.Key - GlobalPosition.X;
            for (int i = 0; i < list.Count; i++)
            {
                float x = localCenter + (i - (list.Count - 1) / 2f) * slot - size.X / 2;
                var top = list[i].Top;
                bool tapped = top.Tapped || _staged.Contains(top.Id);
                PlaceStack(list[i], new Vector2(x, y + forward), size, tapped ? 90 : 0, BlockersZ + Math.Min(i * 4, 20) + StackPeeks);
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
