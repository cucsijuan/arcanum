// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.Rules;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// The compact board, for small screens (handheld consoles, phones): one table large at a time. On this screen's own
/// turn its table takes most of the screen, and a strip along the top keeps the other players' permanents in sight; on
/// another player's turn that player's table takes most of it, and a strip along the bottom
/// keeps this screen's permanents and piles in sight (its hand opens on demand). A list of the players at the left
/// shows anyone's table; their life totals in it stand for the life counters of the tables not shown (choosing a
/// player to attack or target).
/// </summary>
public partial class GameBoard
{
    /// <summary>Where the line between the shown table and this screen's strip sits (the strip with its hand open, and without).</summary>
    private const float StripDivider = 0.66f, StripWithHandDivider = 0.5f;

    /// <summary>Where the strip of the other players' tables ends, above this screen's own table on its turn.</summary>
    private const float TopStripDivider = 1 - StripDivider;

    /// <summary>
    /// How tall a side is at its cards' usual size (half of the board's usual 1080 points): a side with more or less room than that
    /// draws its cards, hand and piles bigger or smaller, so one table alone on the screen is easier to read.
    /// </summary>
    private const float UsualSideHeight = 540;

    private static float ScaleFor(float height, bool withHand) =>
        Mathf.Clamp(height / (withHand ? UsualSideHeight : UsualSideHeight - 30), 0.6f, 1.5f);

    private bool _compact;

    /// <summary>How much bigger this screen's hand is drawn on its own table.</summary>
    private const float OwnHandScale = 1.3f;

    /// <summary>Where the list of players ends, at the left.</summary>
    private const float PlayerListRight = 196;

    /// <summary>The phase bar starts right of the list of players.</summary>
    private const float PhaseBarLeftOfList = 196;

    /// <summary>A table the player chose in the list; null follows the turn (the active player's table).</summary>
    private PlayerId? _viewed;

    /// <summary>The hand opened in the strip (with the button, or because a decision is about cards in it).</summary>
    private bool _handOpen;

    /// <summary>The turn the view was last laid out for: a new turn shows the new active player's table again.</summary>
    private (int Turn, PlayerId Active)? _compactTurn;

    private readonly VBoxContainer _playerList = new() { Visible = false };
    private readonly StopsPanel _stopsPanel = new() { ZIndex = BoardStyle.Z.Picker };
    private readonly Button _handButton = BoardStyle.MakeButton("Hand", 14, compact: true);

    /// <summary>The steps of the turn as a column at the right (the compact board's phase bar).</summary>
    private readonly StepTrack _stepTrack = new() { Visible = false };

    /// <summary>Where the step column starts: below the buttons at the top right.</summary>
    private const float StepTrackTop = 300;
    private double _screenCheck;

    private void BuildCompactControls()
    {
        _playerList.AnchorTop = _playerList.AnchorBottom = 0;
        _playerList.OffsetLeft = 8;
        _playerList.OffsetTop = 60;
        _playerList.AddThemeConstantOverride("separation", 6);
        _playerList.ZIndex = BoardStyle.Z.PhaseBar;
        AddChild(_playerList);

        _handButton.Visible = false;
        _handButton.CustomMinimumSize = new Vector2(96, 40);
        _handButton.AnchorTop = _handButton.AnchorBottom = 1;
        _handButton.OffsetLeft = 64; _handButton.OffsetTop = -54; _handButton.OffsetBottom = -14;
        _handButton.ZIndex = BoardStyle.Z.PhaseBar;
        _handButton.Pressed += () =>
        {
            _handOpen = !_handOpen;
            Refresh();
        };
        AddChild(_handButton);

        AddChild(_stopsPanel);
        _phaseBar.PanelRequested += _stopsPanel.Open;

        _stepTrack.AnchorLeft = _stepTrack.AnchorRight = 1;
        _stepTrack.OffsetLeft = -_stepTrack.Size.X - 8;
        _stepTrack.OffsetRight = -8;
        _stepTrack.OffsetTop = StepTrackTop;
        _stepTrack.OffsetBottom = StepTrackTop + _stepTrack.Size.Y;
        _stepTrack.ZIndex = BoardStyle.Z.PhaseBar;
        _stepTrack.PanelRequested += _stopsPanel.Open;
        AddChild(_stepTrack);
    }

    /// <summary>Notices the screen getting smaller or bigger (a console taken out of its dock) and changes the view.</summary>
    private void CheckScreen(double delta)
    {
        _screenCheck -= delta;
        if (_screenCheck > 0 || _areas.Count == 0) return;
        _screenCheck = 0.5;
        if (Settings.CompactBoard != _compact) Refresh();
    }

    /// <summary>
    /// Lays the areas out for the view in use (compact or every table), before they are refreshed: their rows of cards
    /// are laid out within their size.
    /// </summary>
    private void PlaceAreas(GameView view, Decision? decision)
    {
        bool compact = Settings.CompactBoard;
        if (compact != _compact)
        {
            _compact = compact;
            _viewed = null;
            _handOpen = false;
            _compactTurn = null;
            if (!compact) PlaceEveryTable();
        }
        _playerList.Visible = compact;
        // The compact board shows the steps as a column at the right instead of the bar.
        _phaseBar.Visible = !compact;
        _stepTrack.Visible = compact;
        // The action panel stands left of the step column.
        _actionPanel.OffsetRight = compact ? -_stepTrack.Size.X - 16 : -16;
        _phaseBar.OffsetLeft = compact ? PhaseBarLeftOfList : 110;
        _phaseBar.OpensPanel = compact; // its markers are too small for a finger: the stops panel instead
        if (!compact)
        {
            _handButton.Visible = false;
            return;
        }

        var turn = (view.TurnNumber, view.ActivePlayer);
        if (_compactTurn != turn)
        {
            _compactTurn = turn;
            _viewed = null;
            _handOpen = false;
        }
        var shown = _viewed ?? view.ActivePlayer;
        bool mine = shown == Bottom;
        // A decision about cards in this screen's hand (discarding, choosing cards from it) opens it.
        bool handWanted = decision is not null && decision.Player == Bottom && decision switch
        {
            SelectCardsDecision => true,
            ChooseCardsDecision c => c.Request.Options.Any(o => o.Zone == Arcanum.Engine.State.Zone.Hand && o.Owner == Bottom),
            _ => false,
        };
        bool handShown = mine || _handOpen || handWanted;
        float divider = handShown ? StripWithHandDivider : StripDivider;

        // On this screen's own table the other players' tables stay in sight as a strip across the top, side by side.
        var across = mine
            ? _areas.Where(a => a.Player != Bottom).OrderBy(a => (a.Player.Value - Bottom.Value + _areas.Count) % _areas.Count).ToList()
            : new List<PlayerArea>();
        foreach (var area in _areas)
        {
            bool isShown = area.Player == shown, isStrip = !mine && area.Player == Bottom;
            int acrossAt = across.IndexOf(area);
            area.Visible = isShown || isStrip || acrossAt >= 0;
            area.OpenEdge = false;
            area.SmallPiles = acrossAt >= 0 && across.Count > 1;
            // This screen's own table alone: its hand bigger (it's the hand being played from), right of the list.
            area.HandScale = isShown && mine ? OwnHandScale : 1;
            area.HandLeftInset = isShown && mine ? PlayerListRight : 0;
            area.AnchorLeft = 0; area.AnchorRight = 1;
            area.OffsetLeft = area.OffsetRight = area.OffsetTop = area.OffsetBottom = 0;
            area.RightInset = isShown && !mine ? 64 : 0;
            // An opponent's side drawn small for a crowded table (Compact) gets the whole width here: undone.
            float unCompact = area.Compact ? 1 / 0.7f : 1;
            if (isShown && mine)
            {
                area.AnchorTop = TopStripDivider; area.AnchorBottom = 1;
                area.HideHand = false;
                area.CardScale = ScaleFor(Size.Y * (1 - TopStripDivider), withHand: true);
            }
            else if (isShown)
            {
                area.AnchorTop = 0; area.AnchorBottom = divider;
                area.HideHand = false;
                area.CardScale = unCompact * ScaleFor(Size.Y * divider, withHand: true);
            }
            else if (isStrip)
            {
                area.AnchorTop = divider; area.AnchorBottom = 1;
                area.HideHand = !handShown;
                area.CardScale = ScaleFor(Size.Y * (1 - divider), withHand: handShown);
            }
            else if (acrossAt >= 0)
            {
                // Its hand stays out of the strip (how many cards it holds shows by its name). The strip's own size already
                // makes the cards small: not smaller again for a crowded table.
                area.AnchorLeft = acrossAt / (float)across.Count; area.AnchorRight = (acrossAt + 1) / (float)across.Count;
                area.AnchorTop = 0; area.AnchorBottom = TopStripDivider;
                area.RightInset = acrossAt == across.Count - 1 ? 64 : 0;
                area.HideHand = true;
                area.CardScale = unCompact * ScaleFor(Size.Y * TopStripDivider, withHand: false);
            }
        }
        // The phase bar stays on the line between the tables (at the top of this screen's own table). The stack goes on
        // the table shown large, the one whose turn it usually is, where its cards have room: always near the top.
        float line = mine ? 0.06f : divider;
        _phaseBar.AnchorTop = _phaseBar.AnchorBottom = line;
        _stackView.AnchorTop = _stackView.AnchorBottom = 0.06f;
        _stackView.OffsetLeft = PhaseBarLeftOfList + 10;

        _handButton.Visible = !mine && !handWanted;
        var hand = view.Players[Bottom.Value].Hand;
        _handButton.Text = _handOpen ? "Hide hand" : $"Hand ({hand.Count})";
        bool playable = decision is PriorityDecision p && p.Player == Bottom && hand.Any(c => UsableNow(p, c.Id));
        _handButton.Modulate = playable && !_handOpen ? BoardStyle.Playable : Colors.White;

        RefreshPlayerList(view, shown, decision);
        PlacePlayerList(onOwnTable: mine);
    }

    /// <summary>
    /// The list of players stands at the top left; on this screen's own table it goes down to its bottom left corner (the
    /// rows of cards keep clear of the sides), so the strip of the other tables across the top has the whole width.
    /// </summary>
    private void PlacePlayerList(bool onOwnTable)
    {
        float height = _playerList.GetCombinedMinimumSize().Y;
        _playerList.AnchorTop = _playerList.AnchorBottom = onOwnTable ? 1 : 0;
        _playerList.OffsetTop = onOwnTable ? -64 - height : 60; // above the log button
        _playerList.OffsetBottom = _playerList.OffsetTop + height;
    }

    /// <summary>Back to every table at once: this screen's seat across the bottom, the others across the top.</summary>
    private void PlaceEveryTable()
    {
        int count = _areas.Count, opponents = count - 1;
        float divider = AtTable ? TableDivider : 0.5f;
        _phaseBar.AnchorTop = _phaseBar.AnchorBottom = divider;
        _stackView.AnchorTop = _stackView.AnchorBottom = divider;
        _stackView.OffsetLeft = 120;
        foreach (var area in _areas)
        {
            int place = (area.Player.Value - Bottom.Value + count) % count;
            area.Visible = true;
            area.HideHand = false;
            area.SmallPiles = false;
            area.HandScale = 1;
            area.HandLeftInset = 0;
            area.CardScale = 1;
            area.RightInset = place == opponents ? 64 : 0;
            area.OffsetLeft = area.OffsetRight = area.OffsetTop = area.OffsetBottom = 0;
            if (place == 0)
            {
                area.AnchorLeft = 0; area.AnchorTop = divider; area.AnchorRight = 1; area.AnchorBottom = 1;
            }
            else
            {
                area.AnchorLeft = (place - 1) / (float)opponents; area.AnchorRight = place / (float)opponents;
                area.AnchorTop = 0; area.AnchorBottom = divider;
            }
        }
    }

    /// <summary>
    /// One row per player: their life (picked as a target or attacked, like a life counter) and their name (shows their
    /// table; this screen's own name, or the table shown, goes back to following the turn).
    /// </summary>
    private void RefreshPlayerList(GameView view, PlayerId shown, Decision? decision)
    {
        // Out of the list at once: its height is measured from the rows in it.
        foreach (var child in _playerList.GetChildren())
        {
            _playerList.RemoveChild(child);
            child.QueueFree();
        }
        for (int i = 0; i < view.Players.Count; i++)
        {
            var seat = new PlayerId(i);
            var player = view.Players[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);

            var life = BoardStyle.MakeButton(player.Life.ToString(), 16, compact: true, plain: true);
            life.CustomMinimumSize = new Vector2(48, 40);
            life.TooltipText = "Life";
            bool pickable = decision is AttackDecision a && a.Defenders.Contains(seat) || decision is TargetDecision t && TargetsPlayer(t, seat);
            if (pickable) life.Modulate = BoardStyle.Playable;
            life.Pressed += () => OnPlayerClicked(seat);
            row.AddChild(life);

            string marks = (seat == view.ActivePlayer ? "▶ " : "") + (player.HasLost ? "✕ " : "");
            var name = BoardStyle.MakeButton(marks + Shorten(player.Name), 14, compact: true, plain: true);
            name.CustomMinimumSize = new Vector2(120, 40);
            name.Alignment = HorizontalAlignment.Left;
            name.TooltipText = $"{player.Name}: hand {player.Hand.Count}, library {player.LibraryCount}";
            name.AddThemeColorOverride("font_color", BoardStyle.PlayerColor(i));
            // Flat rows in the player's color (the framed buttons' art doesn't suit a list); the table shown stands out.
            var color = BoardStyle.PlayerColor(i);
            var rest = BoardStyle.Box(seat == shown ? new Color(1, 1, 1, 0.18f) : new Color(0.06f, 0.06f, 0.07f, 0.85f), 6, seat == shown ? color : color.Darkened(0.45f), seat == shown ? 2 : 1);
            var lit = BoardStyle.Box(new Color(1, 1, 1, 0.24f), 6, color, 2);
            foreach (var button in new[] { life, name })
            {
                button.AddThemeStyleboxOverride("normal", rest);
                button.AddThemeStyleboxOverride("hover", lit);
                button.AddThemeStyleboxOverride("pressed", lit);
            }
            name.Pressed += () =>
            {
                // The table chosen already (other than the turn's): back to following the turn.
                _viewed = seat == shown && _viewed is not null ? null : seat;
                Refresh();
            };
            row.AddChild(name);
            _playerList.AddChild(row);
        }
    }

    /// <summary>
    /// A career table on the compact board: its picture behind the whole screen, and under each table shown its seat's
    /// playmat (as wide as at the table, as tall as the rows of cards it holds); a big tournament's mat under both.
    /// </summary>
    private void LayoutCompactTable(string json)
    {
        var shared = _match!.TournamentMat;
        var span = new Rect2();
        int shown = 0;
        float line = 0;
        foreach (var mat in _tableMats) mat.Visible = false;
        for (int i = 0; i < _areas.Count; i++)
        {
            var area = _areas[i];
            area.FieldBand = null;
            area.OpenEdge = shared is not null;
            if (!area.Visible) continue;
            bool mine = area.Player == Bottom;
            var place = Arcanum.UI.Career.Hotspot.LoadArea(json, mine ? "matBottom" : "matTop", mine ? TableMatBottom : TableMatTop);
            // The rows' band, measured from the area's edge towards the middle of the table (its top, or its bottom when
            // it faces down), with the mat's margin around it.
            var (start, end) = area.DefaultFieldBand;
            float top = area.FacesDown ? area.Size.Y - end : start;
            var rect = new Rect2(place.Position.X * Size.X, area.Position.Y + top - MatMargin, place.Size.X * Size.X, end - start + 2 * MatMargin);
            var mat = _tableMats[mine ? 1 : 0];
            if (_match.TableMats?.ElementAtOrDefault(i) is { } id) mat.SetStyle(id);
            mat.Turned = !mine;
            mat.Visible = shared is null && _match.TableMats?.ElementAtOrDefault(i) is not null;
            mat.Position = rect.Position;
            mat.Size = rect.Size;
            span = shown++ > 0 ? span.Merge(rect) : rect;
            // The tournament's band lies between the two tables shown (at the top of this screen's strip), else along
            // the far edge of the one shown.
            if (mine) line = area.Position.Y;
        }
        bool any = shown > 0;
        if (shown == 1) line = span.Position.Y;
        _tournamentMat.Visible = shared is not null && any;
        if (shared is not null && any)
        {
            _tournamentMat.Position = span.Position;
            _tournamentMat.Size = span.Size;
            _tournamentMat.SetName(shared);
            _tournamentMat.SetLine(line - span.Position.Y);
        }
        _broadcast.Place(_phaseBar.AnchorTop * Size.Y, any ? span.End.X : Size.X * 0.765f);
    }

    private bool TargetsPlayer(TargetDecision t, PlayerId player) =>
        t.Request.CanAddMore(_chosenTargets.Count)
        && t.Request.LegalAt(_chosenTargets.Count).Any(o => o.Player == player && t.Request.IsAllowed(_chosenTargets.Count, o, _chosenTargets));
}
