// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Touch screens have no pointer hovering over a card, so a touch does what hovering and clicking do with a mouse: the
/// first touch on a card picks it and shows it in the preview. While the player has priority, touching it again does what
/// clicking it does (plays it, taps a land for mana, uses its ability), and so does every touch after that while it stays
/// picked; a card in the hand is also played by sliding it out of the hand. Any other choice, one a touch can take back
/// (attackers, blockers, damage, mana to pay with, cards or targets to pick), happens at the first touch, which shows the
/// card too. Touching anything else puts the preview away.
/// </summary>
public partial class GameBoard
{
    /// <summary>The last input came from a touch screen (a mouse moving switches back).</summary>
    private bool _touch;

    /// <summary>The card the last touch picked.</summary>
    private CardNode? _touchPick;

    /// <summary>The current touch landed on a card (otherwise the preview goes away when it ends).</summary>
    private bool _touchOnCard;

    /// <summary>Mouse events made from touches carry this device id.</summary>
    private const int EmulatedMouse = -1;

    private void TrackPointer(InputEvent @event)
    {
        switch (@event)
        {
            // The engine hands out the click it makes from a touch before the touch itself, so the click is where a touch
            // is first seen (before the card under it gets the click).
            case InputEventMouseButton { Device: EmulatedMouse, Pressed: true }:
                _touch = true;
                _touchOnCard = false;
                Callable.From(PutPreviewAwayIfMissed).CallDeferred();
                break;
            case InputEventScreenTouch { Pressed: true }:
                _touch = true;
                break;
            case InputEventMouseMotion { Device: not EmulatedMouse }:
                _touch = false;
                break;
        }
    }

    /// <summary>A touch that didn't land on a card puts the picked card and its preview away.</summary>
    private void PutPreviewAwayIfMissed()
    {
        if (_touchOnCard) return;
        _touchPick = null;
        HidePreview(_preview);
    }

    /// <summary>
    /// With a mouse, a card on the table shows in the preview while the pointer is over it. Touched, only the touch picks
    /// what shows (cards moving under a resting finger, e.g. an attacker going back to its place, don't take its place).
    /// </summary>
    private void HoverStarted(CardNode node)
    {
        // Reached with a controller's arrows, a card shows in the preview at once (the confirm button then acts on it).
        if (!_touch || FocusedWithButtons(node)) ShowPreview(node);
    }

    /// <summary>With a mouse, the preview goes away when the pointer leaves the card; touched, it stays until the next touch.</summary>
    private void HoverEnded(CardNode node)
    {
        if (!_touch || Arcanum.UI.PadFocus.UsingButtons) HidePreview(node);
    }

    private static bool FocusedWithButtons(CardNode node) => Arcanum.UI.PadFocus.UsingButtons && node.HasFocus();

    /// <summary>
    /// The back button, on a card focused with a controller's arrows, puts its preview away (moving to another card shows
    /// that one); true if it did.
    /// </summary>
    private bool PutPadPreviewAway()
    {
        if (!_preview.Visible || GetViewport().GuiGetFocusOwner() is not CardNode node || !FocusedWithButtons(node)) return false;
        _touchPick = null;
        HidePreview(_preview);
        return true;
    }

    /// <summary>
    /// Whether a touch on this card does what a click does now; otherwise it only picks the card and shows it (a card in
    /// the hand can then be slid out to play it).
    /// </summary>
    private bool TouchActs(CardNode node)
    {
        _touchOnCard = true;
        var decision = _session.CurrentDecision;
        bool again = ReferenceEquals(node, _touchPick);
        _touchPick = node;
        ShowPreview(node);
        // Choices a touch can take back (attackers, blockers, damage, mana to tap, cards or targets to pick) happen at the
        // first touch; playing a card or using an ability waits for the second.
        if (again || decision is not (null or PriorityDecision)) return true;
        if (decision is PriorityDecision p && node.View is { Zone: Arcanum.Engine.State.Zone.Hand } card
            && SourceActions(p, node.Id) is [var only and (PlayLand or CastSpell)])
            _drag = new DragState
            {
                Node = node, Area = AreaOf(card.Owner), Start = GetGlobalMousePosition(), Decision = p, Action = only, PlaysOnRelease = false,
            };
        return false;
    }
}
