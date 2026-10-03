// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Rules;
using Arcanum.Engine.Players;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The game screen: two player areas split horizontally, turn/menu controls top-right,
/// card preview on hover, an action panel for the pending decision and a toggleable game log.
/// </summary>
public partial class GameBoard : Control
{
    private static readonly PlayerId Bottom = new(0);
    private static readonly PlayerId Top = new(1);

    private GameSession _session = null!;
    private readonly PlayerArea _topArea = new() { Player = Top };
    private readonly PlayerArea _bottomArea = new() { Player = Bottom };
    private readonly CardNode _preview = new();
    private readonly Label _turnNumber = BoardStyle.MakeLabel("1", 22, bold: true);
    private readonly Label _stepLabel = BoardStyle.MakeLabel("", 12, BoardStyle.TextDim);
    private readonly PanelContainer _actionPanel = new();
    private readonly Label _prompt = BoardStyle.MakeLabel("", 15);
    private readonly HBoxContainer _actionButtons = new();
    private readonly PanelContainer _logPanel = new();
    private readonly RichTextLabel _log = new();
    private readonly Label _logBadge = BoardStyle.MakeLabel("", 10, Colors.White);
    private readonly Control _gameOver = new();
    private readonly Label _gameOverText = BoardStyle.MakeLabel("", 40, bold: true);
    private int _unreadLog;

    // In-progress choices for the pending decision.
    private readonly HashSet<CardId> _selected = new();
    private readonly Dictionary<CardId, CardId> _blocks = new(); // blocker -> attacker
    private CardId? _pendingBlocker;
    private readonly List<ManaTap> _staged = new();  // sources picked for an unconfirmed payment
    private CardId? _manaChoiceSource;                 // multi-type source waiting for a color choice
    private readonly VBoxContainer _actionExtra = new();
    private readonly Dictionary<CardId, int> _damageSplit = new(); // blocker -> damage, for an unconfirmed assignment
    private readonly ArrowLayer _arrows = new();
    private readonly PhaseBar _phaseBar = new();
    private readonly StackView _stackView = new();
    private Button _undoButton = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildLayout();
        // ARCANUM_SEED makes the first game reproducible (debugging, screenshots).
        StartNewGame(ulong.TryParse(OS.GetEnvironment("ARCANUM_SEED"), out var seed) ? seed : (ulong)Time.GetTicksUsec());
    }

    private void StartNewGame(ulong seed) => StartSession(GameSession.CreateHotseatDemo(seed));

    private void StartSession(GameSession session)
    {
        _session = session;
        _session.Changed += Refresh;
        _session.Failed += e => ShowGameOver($"Engine error:\n{e.Message}");
        _session.Game.EventRaised += OnGameEvent;
        _phaseBar.Bind(session.Policy);
        _log.Clear();
        _unreadLog = 0;
        UpdateLogBadge();
        _gameOver.Visible = false;
        _lastDecision = null;
        _session.Start();
    }

    /// <summary>Test-mode undo: rebuild the game up to the last decision a person made and ask it again.</summary>
    private void Undo()
    {
        if (_session.CreateUndo() is { } previous) StartSession(previous);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        AddChild(new ColorRect { Color = BoardStyle.Background, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        _topArea.AnchorRight = 1; _topArea.AnchorBottom = 0.5f;
        _topArea.FacesDown = true;
        _bottomArea.AnchorTop = 0.5f; _bottomArea.AnchorRight = 1; _bottomArea.AnchorBottom = 1;
        foreach (var area in new[] { _topArea, _bottomArea })
        {
            area.CardClicked += OnCardClicked;
            area.CardHoverStarted += ShowPreview;
            area.CardHoverEnded += HidePreview;
            AddChild(area);
        }

        AddChild(_arrows);

        // Top-right: menu, turn counter, log toggle.
        var corner = new VBoxContainer { AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -62, OffsetTop = 14, OffsetRight = -14 };
        corner.AddThemeConstantOverride("separation", 8);
        var menu = BoardStyle.MakeButton("≡", 22);
        menu.CustomMinimumSize = new Vector2(48, 40);
        menu.Pressed += () => StartNewGame((ulong)Time.GetTicksUsec()); // placeholder until the in-game menu (M5)
        menu.TooltipText = "New game";
        corner.AddChild(menu);

        var turnBox = new PanelContainer { CustomMinimumSize = new Vector2(48, 48) };
        turnBox.AddThemeStyleboxOverride("panel", BoardStyle.Box(BoardStyle.Panel, 6, BoardStyle.PanelBorder, 1));
        var turnStack = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        turnStack.AddThemeConstantOverride("separation", -4);
        var turnTitle = BoardStyle.MakeLabel("TURN", 9, BoardStyle.TextDim);
        turnTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _turnNumber.HorizontalAlignment = HorizontalAlignment.Center;
        turnStack.AddChild(turnTitle);
        turnStack.AddChild(_turnNumber);
        turnBox.AddChild(turnStack);
        corner.AddChild(turnBox);

        _undoButton = BoardStyle.MakeButton("↶", 22);
        _undoButton.CustomMinimumSize = new Vector2(48, 40);
        _undoButton.TooltipText = "Undo (Ctrl+Z)";
        _undoButton.Pressed += Undo;
        corner.AddChild(_undoButton);

        var logToggle = BoardStyle.MakeButton("▤", 18);
        logToggle.CustomMinimumSize = new Vector2(48, 40);
        logToggle.TooltipText = "Game log";
        logToggle.Pressed += ToggleLog;
        corner.AddChild(logToggle);
        AddChild(corner);

        // Phase bar on the divider, left of the top player's hand.
        _phaseBar.AnchorTop = 0.5f; _phaseBar.AnchorBottom = 0.5f;
        _phaseBar.OffsetLeft = 110;
        _phaseBar.GrowVertical = GrowDirection.Both;
        AddChild(_phaseBar);

        // Stack: right of center, clear of the zone piles.
        _stackView.AnchorLeft = 1; _stackView.AnchorRight = 1; _stackView.AnchorTop = 0.5f; _stackView.AnchorBottom = 0.5f;
        _stackView.OffsetLeft = -560; _stackView.OffsetTop = -120;
        _stackView.CardHoverStarted += ShowPreview;
        _stackView.CardHoverEnded += HidePreview;
        AddChild(_stackView);

        // Action panel: right side of the bottom half, clear of both players' zone piles.
        _actionPanel.AnchorLeft = 1; _actionPanel.AnchorRight = 1; _actionPanel.AnchorTop = 0.75f; _actionPanel.AnchorBottom = 0.75f;
        _actionPanel.GrowHorizontal = GrowDirection.Begin;
        _actionPanel.GrowVertical = GrowDirection.Both;
        _actionPanel.OffsetRight = -16;
        _actionPanel.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.06f, 0.06f, 0.07f, 0.92f), 10, BoardStyle.PanelBorder, 1));
        var actionBox = new VBoxContainer();
        actionBox.AddThemeConstantOverride("separation", 6);
        _stepLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _prompt.HorizontalAlignment = HorizontalAlignment.Right;
        _actionButtons.Alignment = BoxContainer.AlignmentMode.End;
        _actionButtons.AddThemeConstantOverride("separation", 8);
        actionBox.AddChild(_stepLabel);
        actionBox.AddChild(_prompt);
        _actionExtra.AddThemeConstantOverride("separation", 6);
        actionBox.AddChild(_actionExtra);
        actionBox.AddChild(_actionButtons);
        _actionPanel.AddChild(actionBox);
        AddChild(_actionPanel);

        // Bottom-left log button with unread badge.
        var logButton = BoardStyle.MakeButton("Log", 13);
        logButton.AnchorTop = 1; logButton.AnchorBottom = 1;
        logButton.OffsetLeft = 14; logButton.OffsetTop = -54; logButton.OffsetRight = 54; logButton.OffsetBottom = -14;
        logButton.Pressed += ToggleLog;
        var badge = new Panel { Position = new Vector2(26, -8), Size = new Vector2(20, 18), MouseFilter = MouseFilterEnum.Ignore, Name = "Badge" };
        badge.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color("b3261e"), 9));
        _logBadge.HorizontalAlignment = HorizontalAlignment.Center;
        _logBadge.Size = badge.Size;
        badge.AddChild(_logBadge);
        logButton.AddChild(badge);
        AddChild(logButton);

        _logPanel.AnchorTop = 1; _logPanel.AnchorBottom = 1;
        _logPanel.OffsetLeft = 14; _logPanel.OffsetTop = -420; _logPanel.OffsetRight = 384; _logPanel.OffsetBottom = -64;
        _logPanel.AddThemeStyleboxOverride("panel", BoardStyle.Box(new Color(0.06f, 0.06f, 0.07f, 0.95f), 8, BoardStyle.PanelBorder, 1));
        _log.ScrollFollowing = true;
        _log.BbcodeEnabled = true;
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        _log.AddThemeColorOverride("default_color", BoardStyle.Text);
        _logPanel.AddChild(_log);
        _logPanel.Visible = false;
        AddChild(_logPanel);

        // Hover preview on the left edge.
        _preview.MouseFilter = MouseFilterEnum.Ignore;
        _preview.SharpImage = true;
        _preview.Size = BoardStyle.PreviewSize;
        _preview.Visible = false;
        _preview.ZIndex = 200;
        AddChild(_preview);

        // Game over overlay.
        _gameOver.SetAnchorsPreset(LayoutPreset.FullRect);
        _gameOver.Visible = false;
        _gameOver.ZIndex = 300;
        _gameOver.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.7f), AnchorRight = 1, AnchorBottom = 1 });
        var overBox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, AnchorRight = 1, AnchorBottom = 1 };
        overBox.AddThemeConstantOverride("separation", 24);
        _gameOverText.HorizontalAlignment = HorizontalAlignment.Center;
        overBox.AddChild(_gameOverText);
        var again = BoardStyle.MakePrimaryButton("New game", 20);
        again.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        again.CustomMinimumSize = new Vector2(220, 52);
        again.Pressed += () => StartNewGame((ulong)Time.GetTicksUsec());
        overBox.AddChild(again);
        _gameOver.AddChild(overBox);
        AddChild(_gameOver);

        UpdateLogBadge();
    }

    // ---------------------------------------------------------------- refresh

    private PlayerArea AreaOf(PlayerId player) => player == Top ? _topArea : _bottomArea;

    private CardNode? FindCard(CardId id) => _bottomArea.FindCard(id) ?? _topArea.FindCard(id);

    private void Refresh()
    {
        var decision = _session.CurrentDecision;
        if (decision is not null && !ReferenceEquals(decision, _lastDecision))
        {
            _selected.Clear();
            _blocks.Clear();
            _pendingBlocker = null;
            _manaChoiceSource = null;
            _staged.Clear();
            if (decision is ManaPaymentDecision pay) _staged.AddRange(pay.Request.SuggestedTaps);
            _damageSplit.Clear();
            if (decision is DamageAssignmentDecision dmg)
                foreach (var (blocker, amount) in dmg.Request.Suggested) _damageSplit[blocker] = amount;
            _lastDecision = decision;
        }

        var view = _session.ViewFor(Bottom);
        var staged = _staged.Select(t => t.Source).ToHashSet();
        var attacking = view.Attacks.Select(a => a.Attacker).ToHashSet();
        if (decision is AttackDecision) attacking.UnionWith(_selected);
        _topArea.Refresh(view, view.ActivePlayer == Top, staged, attacking);
        _bottomArea.Refresh(view, view.ActivePlayer == Bottom, staged, attacking);
        _turnNumber.Text = Math.Max(1, view.TurnNumber).ToString();
        _stepLabel.Text = view.TurnNumber == 0 ? "Mulligan" : EventLogFormatter.StepName(view.Step);
        _phaseBar.SetCurrentStep(view.TurnNumber == 0 ? null : view.Step);
        _stackView.Refresh(view.Stack);
        _undoButton.Disabled = !_session.CanUndo;

        if (view.IsGameOver)
        {
            var winner = view.Winner is { } w ? view.Players[w.Value].Name + " wins!" : "Draw";
            ShowGameOver(winner);
        }

        ApplyHighlights(view, decision);
        UpdateArrows(view, decision);
        BuildActionPanel(view, decision);
        if (_autoplay && decision is not null)
        {
            // ARCANUM_AUTOPLAY=showcase[:DecisionType] freezes on the first decision of that type (default:
            // ManaPaymentDecision) so screenshots can capture it.
            if (_showcase is { } freezeOn && decision.GetType().Name == freezeOn)
            {
                GD.Print($"SHOWCASE frozen on {freezeOn}");
                if (decision is ManaPaymentDecision pay && FindCard(pay.Request.Spell) is { } spellNode) ShowPreview(spellNode);
                return;
            }
            // ARCANUM_TEST_UNDO=1: every 7th manual decision, undo once first (exercises replay end to end).
            int manual = _session.Log.Entries.Count(e => e.Manual);
            if (_testUndo && manual > 0 && manual % 7 == 0 && _undoneAt.Add(manual))
            {
                GD.Print($"TEST_UNDO at {manual} manual decisions (log {_session.Log.Entries.Count})");
                GetTree().CreateTimer(0.2).Timeout += Undo;
                return;
            }
            GetTree().CreateTimer(0.35).Timeout += () => AutoAnswer(decision, view);
        }
    }

    /// <summary>Debug autopilot (ARCANUM_AUTOPLAY=1): plays both seats greedily for smoke tests and screenshots.</summary>
    private readonly bool _testUndo = OS.GetEnvironment("ARCANUM_TEST_UNDO") == "1";
    private readonly HashSet<int> _undoneAt = new();

    private static readonly string AutoplayEnv = OS.GetEnvironment("ARCANUM_AUTOPLAY");
    private readonly bool _autoplay = AutoplayEnv == "1" || AutoplayEnv.StartsWith("showcase");
    private readonly string? _showcase = AutoplayEnv.StartsWith("showcase")
        ? (AutoplayEnv.Contains(':') ? AutoplayEnv[(AutoplayEnv.IndexOf(':') + 1)..] : nameof(ManaPaymentDecision))
        : null;

    private static void AutoAnswer(Decision decision, GameView view)
    {
        if (decision.IsAnswered) return;
        switch (decision)
        {
            case MulliganDecision m: m.Answer(true); break;
            case PriorityDecision p: p.Answer(p.Legal.FirstOrDefault(a => a is PlayLand or CastSpell) ?? PassPriority.Instance); break;
            case ManaPaymentDecision pay: pay.Answer(pay.Request.SuggestedTaps); break;
            case DamageAssignmentDecision dmg: dmg.Answer(dmg.Request.Suggested); break;
            case AttackDecision a: a.Answer(a.PossibleAttackers.Select(id => new AttackDeclaration(id, a.Defenders[0])).ToList()); break;
            // Double-block the first attacker when possible so damage assignment gets exercised too.
            case BlockDecision b: b.Answer(b.PossibleBlockers.Take(2).Select(bl => new BlockDeclaration(bl, b.Attackers[0])).ToList()); break;
            case SelectCardsDecision s: s.Answer(view.Players[s.Player.Value].Hand.Take(s.Count).Select(c => c.Id).ToList()); break;
        }
    }

    private Decision? _lastDecision;

    private void ApplyHighlights(GameView view, Decision? decision)
    {
        foreach (var attack in view.Attacks)
        {
            FindCard(attack.Attacker)?.SetHighlight(CardHighlight.Attacking);
            foreach (var blocker in attack.Blockers) FindCard(blocker)?.SetHighlight(CardHighlight.Blocking);
        }

        switch (decision)
        {
            case PriorityDecision p:
                foreach (var action in p.Legal)
                {
                    var id = action switch { PlayLand l => l.Card, CastSpell c => c.Card, _ => (CardId?)null };
                    if (id is { } cardId) FindCard(cardId)?.SetHighlight(CardHighlight.Playable);
                }
                break;
            case ManaPaymentDecision pay:
            {
                FindCard(pay.Request.Spell)?.SetHighlight(CardHighlight.Selected);
                var remaining = RemainingCost(pay);
                foreach (var source in pay.Request.Sources)
                {
                    if (_staged.Any(t => t.Source == source.Source)) FindCard(source.Source)?.SetHighlight(CardHighlight.Selected);
                    else if (UsefulType(source, remaining) is not null) FindCard(source.Source)?.SetHighlight(CardHighlight.Playable);
                }
                break;
            }
            case DamageAssignmentDecision dmg:
                FindCard(dmg.Request.Attacker)?.SetHighlight(CardHighlight.Attacking);
                foreach (var id in dmg.Request.Blockers)
                {
                    FindCard(id)?.SetHighlight(CardHighlight.Blocking);
                    FindCard(id)?.SetAssignedDamage(_damageSplit.GetValueOrDefault(id));
                }
                break;
            case AttackDecision a:
                foreach (var id in a.PossibleAttackers)
                    FindCard(id)?.SetHighlight(_selected.Contains(id) ? CardHighlight.Attacking : CardHighlight.Playable);
                break;
            case BlockDecision b:
                foreach (var id in b.Attackers) FindCard(id)?.SetHighlight(CardHighlight.Attacking);
                foreach (var id in b.PossibleBlockers)
                {
                    var h = _pendingBlocker == id ? CardHighlight.Selected
                        : _blocks.ContainsKey(id) ? CardHighlight.Blocking
                        : CardHighlight.Playable;
                    FindCard(id)?.SetHighlight(h);
                }
                break;
            case SelectCardsDecision s:
                foreach (var card in view.Players[s.Player.Value].Hand)
                    FindCard(card.Id)?.SetHighlight(_selected.Contains(card.Id) ? CardHighlight.Selected : CardHighlight.Playable);
                break;
        }
    }

    /// <summary>Blocker → attacker arrows for declared and in-progress blocks, plus one following the mouse.</summary>
    private void UpdateArrows(GameView view, Decision? decision)
    {
        var arrows = new List<ArrowLayer.Arrow>();
        if (view.IsGameOver) { _arrows.SetArrows(arrows); return; }
        void Add(CardId from, CardId? to, Color color)
        {
            var fromNode = FindCard(from);
            CardNode? toNode = to is { } t ? FindCard(t) : null;
            if (fromNode is not null && (to is null || toNode is not null)) arrows.Add(new(fromNode, toNode, color));
        }

        foreach (var attack in view.Attacks)
            foreach (var blocker in attack.Blockers) Add(blocker, attack.Attacker, BoardStyle.Blocking);
        if (decision is BlockDecision)
        {
            foreach (var (blocker, attacker) in _blocks) Add(blocker, attacker, BoardStyle.Blocking);
            if (_pendingBlocker is { } pending) Add(pending, null, BoardStyle.Selected);
        }
        _arrows.SetArrows(arrows);
    }

    private void BuildActionPanel(GameView view, Decision? decision)
    {
        foreach (var child in _actionButtons.GetChildren()) child.QueueFree();
        foreach (var child in _actionExtra.GetChildren()) child.QueueFree();
        _actionExtra.Visible = false;
        _actionPanel.Visible = decision is not null && !view.IsGameOver;
        if (decision is null) return;

        string who = view.Players[decision.Player.Value].Name;
        switch (decision)
        {
            case MulliganDecision m:
                _prompt.Text = m.MulligansTaken == 0 ? $"{who}: keep this hand?" : $"{who}: keep? ({m.MulligansTaken} mulligan{(m.MulligansTaken > 1 ? "s" : "")})";
                AddButton("Mulligan", () => m.Answer(false));
                AddButton("Keep", () => m.Answer(true), primary: true);
                break;

            case PriorityDecision p when _manaChoiceSource is { } source:
                _prompt.Text = $"{who}: add which mana?";
                AddButton("Cancel", () => { _manaChoiceSource = null; Refresh(); });
                foreach (var tap in p.Legal.OfType<ActivateManaAbility>().Where(a => a.Source == source))
                    AddButton(tap.Type.ToSymbol().ToString(), () => p.Answer(tap));
                break;

            case ManaPaymentDecision pay:
            {
                var spell = view.FindCard(pay.Request.Spell)?.Name ?? "spell";
                var remaining = RemainingCost(pay);
                _prompt.Text = $"{who}: cast {spell}";
                _actionExtra.Visible = true;
                _actionExtra.AddChild(CostLine("Cost", pay.Request.Cost.ToString()));
                if (pay.Request.FromPool.Count > 0)
                    _actionExtra.AddChild(CostLine("Floating mana used", string.Concat(pay.Request.FromPool.Select(t => $"{{{t.ToSymbol()}}}"))));
                if (remaining.ManaValue > 0)
                    _actionExtra.AddChild(CostLine("Still needed", remaining.ToString(), BoardStyle.Attacking));
                else
                    _actionExtra.AddChild(BoardStyle.MakeLabel("Fully paid \u2713", 14, new Color("6fd08c")));
                AddButton("Cancel", () => pay.Answer(null));
                AddButton("Auto", () => { _staged.Clear(); _staged.AddRange(pay.Request.SuggestedTaps); Refresh(); });
                var confirmPay = AddButton("Confirm", () => pay.Answer(_staged.ToList()), primary: true);
                confirmPay.Disabled = remaining.ManaValue > 0;
                break;
            }

            case PriorityDecision p:
                _prompt.Text = $"{who}: your move";
                AddButton("End turn", () =>
                {
                    _session.Policy.PassTurn(view.TurnNumber); // skip the rest of the turn unless an opponent acts
                    p.Answer(PassPriority.Instance);
                });
                AddButton(view.Stack.Count > 0 ? "Resolve" : "Next", () => p.Answer(PassPriority.Instance), primary: true);
                break;

            case DamageAssignmentDecision dmg:
            {
                var attackerName = view.FindCard(dmg.Request.Attacker)?.Name ?? "Attacker";
                int remaining = dmg.Request.Power - _damageSplit.Values.Sum();
                _prompt.Text = $"{who}: assign {dmg.Request.Power} damage from {attackerName}";
                _actionExtra.Visible = true;
                foreach (var blocker in dmg.Request.Blockers) _actionExtra.AddChild(DamageRow(view, dmg, blocker, remaining));
                _actionExtra.AddChild(BoardStyle.MakeLabel(remaining == 0 ? "All damage assigned \u2713" : $"Left to assign: {remaining}", 14,
                    remaining == 0 ? new Color("6fd08c") : BoardStyle.Attacking));
                AddButton("Auto", () => { _damageSplit.Clear(); foreach (var (b, n) in dmg.Request.Suggested) _damageSplit[b] = n; Refresh(); });
                var confirmDamage = AddButton("Confirm", () => dmg.Answer(new Dictionary<CardId, int>(_damageSplit)), primary: true);
                confirmDamage.Disabled = remaining != 0;
                break;
            }

            case AttackDecision a:
                _prompt.Text = $"{who}: choose attackers";
                if (_selected.Count < a.PossibleAttackers.Count)
                    AddButton("All", () => { _selected.UnionWith(a.PossibleAttackers); Refresh(); });
                AddButton(_selected.Count == 0 ? "No attacks" : $"Attack ({_selected.Count})", () =>
                    a.Answer(_selected.Select(id => new AttackDeclaration(id, a.Defenders[0])).ToList()), primary: true);
                break;

            case BlockDecision b:
                _prompt.Text = _pendingBlocker is null ? $"{who}: choose a blocker" : $"{who}: choose what it blocks";
                AddButton(_blocks.Count == 0 ? "No blocks" : $"Confirm blocks ({_blocks.Count})", () =>
                    b.Answer(_blocks.Select(kv => new BlockDeclaration(kv.Key, kv.Value)).ToList()), primary: true);
                break;

            case SelectCardsDecision s:
                string what = s.Reason == SelectCardsReason.MulliganBottom ? "put on the bottom" : "discard";
                _prompt.Text = $"{who}: choose {s.Count} card{(s.Count > 1 ? "s" : "")} to {what}";
                var confirm = AddButton($"Confirm ({_selected.Count}/{s.Count})", () => s.Answer(_selected.ToList()), primary: true);
                confirm.Disabled = _selected.Count != s.Count;
                break;
        }
    }

    /// <summary>One blocker in the damage assignment panel: name, lethal hint and −/+ controls.</summary>
    private Control DamageRow(GameView view, DamageAssignmentDecision dmg, CardId blocker, int remaining)
    {
        var card = view.FindCard(blocker);
        int lethal = Math.Max(0, (card?.Toughness ?? 0) - (card?.Damage ?? 0));
        int amount = _damageSplit.GetValueOrDefault(blocker);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);
        // Hovering a row points out which card it is (names can repeat).
        row.MouseEntered += () => FindCard(blocker)?.SetHighlight(CardHighlight.Selected);
        row.MouseExited += () => FindCard(blocker)?.SetHighlight(CardHighlight.Blocking);
        row.AddChild(BoardStyle.MakeLabel(card?.Name ?? "?", 14));
        row.AddChild(BoardStyle.MakeLabel($"lethal {lethal}", 12, amount >= lethal ? new Color("6fd08c") : BoardStyle.TextDim));

        var minus = BoardStyle.MakeButton("\u2212", 16);
        minus.CustomMinimumSize = new Vector2(34, 32);
        minus.Disabled = amount == 0;
        minus.Pressed += () => { _damageSplit[blocker] = amount - 1; Refresh(); };
        var value = BoardStyle.MakeLabel(amount.ToString(), 18, bold: true);
        value.CustomMinimumSize = new Vector2(28, 0);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var plus = BoardStyle.MakeButton("+", 16);
        plus.CustomMinimumSize = new Vector2(34, 32);
        plus.Disabled = remaining == 0;
        plus.Pressed += () => { _damageSplit[blocker] = amount + 1; Refresh(); };

        row.AddChild(minus);
        row.AddChild(value);
        row.AddChild(plus);
        return row;
    }

    private static Control CostLine(string title, string cost, Color? titleColor = null)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(BoardStyle.MakeLabel(title, 13, titleColor ?? BoardStyle.TextDim));
        row.AddChild(BoardStyle.MakeCostRow(cost));
        return row;
    }

    /// <summary>What the staged taps still leave unpaid.</summary>
    private ManaCost RemainingCost(ManaPaymentDecision pay) =>
        ManaPayment.Apply(pay.Request.RemainingAfterPool, _staged.Select(t => t.Type)).Remaining;

    /// <summary>A mana type this source can add that still helps pay <paramref name="remaining"/>, preferring colored pips.</summary>
    private static ManaType? UsefulType(ManaSourceOption source, ManaCost remaining)
    {
        foreach (var type in source.Types) if (remaining.Pips.Contains(type)) return type;
        return remaining.Generic > 0 ? source.Types[0] : null;
    }

    private Button AddButton(string text, Action onPressed, bool primary = false)
    {
        var button = primary ? BoardStyle.MakePrimaryButton(text) : BoardStyle.MakeButton(text, 17);
        button.CustomMinimumSize = new Vector2(primary ? 150 : 100, 44);
        button.Pressed += () =>
        {
            if (_session.CurrentDecision is null) return;
            onPressed();
            if (_session.CurrentDecision is null) _actionPanel.Visible = false; // wait for the engine's next question
        };
        _actionButtons.AddChild(button);
        return button;
    }

    // ---------------------------------------------------------------- input

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z, CtrlPressed: true })
        {
            Undo();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Space confirms the primary action.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space } && _actionPanel.Visible)
        {
            var primary = _actionButtons.GetChildren().OfType<Button>().LastOrDefault();
            if (primary is { Disabled: false }) primary.EmitSignal(BaseButton.SignalName.Pressed);
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnCardClicked(CardNode node)
    {
        var id = node.Id;
        switch (_session.CurrentDecision)
        {
            case PriorityDecision p:
            {
                var action = p.Legal.FirstOrDefault(a => a is PlayLand l && l.Card == id || a is CastSpell c && c.Card == id);
                if (action is not null) { p.Answer(action); return; }
                // Clicking an untapped mana source floats its mana.
                var manaOptions = p.Legal.OfType<ActivateManaAbility>().Where(a => a.Source == id).ToList();
                if (manaOptions.Count == 1) { p.Answer(manaOptions[0]); return; }
                if (manaOptions.Count > 1) _manaChoiceSource = id;
                break;
            }

            case ManaPaymentDecision pay:
            {
                int staged = _staged.FindIndex(t => t.Source == id);
                if (staged >= 0) _staged.RemoveAt(staged); // untap: pick something else instead
                else if (pay.Request.Sources.FirstOrDefault(s => s.Source == id) is { } source
                         && UsefulType(source, RemainingCost(pay)) is { } type)
                    _staged.Add(new ManaTap(id, type)); // can never tap more than what is still needed
                break;
            }

            case DamageAssignmentDecision dmg when dmg.Request.Blockers.Contains(id):
                // Clicking a blocker adds one damage to it.
                if (_damageSplit.Values.Sum() < dmg.Request.Power) _damageSplit[id] = _damageSplit.GetValueOrDefault(id) + 1;
                break;

            case AttackDecision a when a.PossibleAttackers.Contains(id):
                if (!_selected.Remove(id)) _selected.Add(id);
                break;

            case BlockDecision b:
                if (b.PossibleBlockers.Contains(id))
                {
                    if (_blocks.Remove(id) || _pendingBlocker == id) _pendingBlocker = null;
                    else _pendingBlocker = id;
                }
                else if (b.Attackers.Contains(id) && _pendingBlocker is { } blocker)
                {
                    _blocks[blocker] = id;
                    _pendingBlocker = null;
                }
                break;

            case SelectCardsDecision s when node.View?.Zone == Arcanum.Engine.State.Zone.Hand && node.View.Owner == s.Player:
                if (!_selected.Remove(id) && _selected.Count < s.Count) _selected.Add(id);
                break;

            default:
                return;
        }
        Refresh();
    }

    // ---------------------------------------------------------------- preview, log, game over

    private void ShowPreview(CardNode node)
    {
        if (node.View is not { IsHidden: false } view) return;
        _preview.Setup(view, showCostPips: false);
        _preview.Position = new Vector2(16, (Size.Y - _preview.Size.Y) / 2);
        _preview.Visible = true;
    }

    private void HidePreview(CardNode node) => _preview.Visible = false;

    private void OnGameEvent(GameEvent e)
    {
        var line = EventLogFormatter.Format(_session.Game, e, _session.RevealAll);
        if (line is null) return;
        _log.AppendText((e is TurnBegan ? "\n[b]" + line + "[/b]" : line) + "\n");
        if (!_logPanel.Visible) _unreadLog++;
        UpdateLogBadge();
    }

    private void ToggleLog()
    {
        _logPanel.Visible = !_logPanel.Visible;
        if (_logPanel.Visible) _unreadLog = 0;
        UpdateLogBadge();
    }

    private void UpdateLogBadge()
    {
        _logBadge.Text = _unreadLog > 99 ? "99+" : _unreadLog.ToString();
        _logBadge.GetParent<Control>().Visible = _unreadLog > 0;
    }

    private void ShowGameOver(string text)
    {
        _gameOverText.Text = text;
        _gameOver.Visible = true;
        _actionPanel.Visible = false;
    }
}
