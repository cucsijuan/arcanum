// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Players;

/// <summary>
/// Priority stops: decides when a human player is asked for an action and when priority is passed
/// for them automatically.
/// <list type="bullet">
/// <item>Never stops when the only options are passing or floating mana.</item>
/// <item>With a spell on the stack, stops only if an opponent controls the top object (a chance to respond).</item>
/// <item>With an empty stack, stops only on the steps the player marked, separately for their own and opponents' turns.</item>
/// <item>"End turn" passes everything until the next turn, unless an opponent casts something the player can answer.</item>
/// </list>
/// </summary>
public sealed class AutoPassPolicy
{
    public HashSet<Step> OwnTurnStops { get; } = new() { Step.PrecombatMain, Step.PostcombatMain };
    public HashSet<Step> OpponentTurnStops { get; } = new();

    /// <summary>Stop at every priority, even with nothing to do ("full control").</summary>
    public bool FullControl { get; set; }

    /// <summary>Turn number being skipped with "End turn", or null.</summary>
    public int? PassingTurn { get; private set; }

    public event Action? Changed;

    public bool HasStop(bool ownTurn, Step step) => (ownTurn ? OwnTurnStops : OpponentTurnStops).Contains(step);

    public void ToggleStop(bool ownTurn, Step step)
    {
        var stops = ownTurn ? OwnTurnStops : OpponentTurnStops;
        if (!stops.Remove(step)) stops.Add(step);
        Changed?.Invoke();
    }

    public void SetFullControl(bool value)
    {
        FullControl = value;
        Changed?.Invoke();
    }

    public void PassTurn(int turnNumber) => PassingTurn = turnNumber;

    public void CancelPassTurn() => PassingTurn = null;

    public bool ShouldAutoPass(GameView view, IReadOnlyList<PlayerAction> legal)
    {
        bool canAct = legal.Any(a => a is not (PassPriority or ActivateManaAbility));
        bool opponentOnTop = view.Stack.Count > 0 && view.Stack[^1].Controller != view.Viewer;

        if (PassingTurn is { } turn)
        {
            if (view.TurnNumber != turn) PassingTurn = null;
            else if (opponentOnTop && canAct) PassingTurn = null; // something new happened: hand control back
            else return true;
        }

        if (FullControl) return false;
        if (!canAct) return true;
        if (view.Stack.Count > 0) return !opponentOnTop; // let our own spells resolve
        return !HasStop(view.ActivePlayer == view.Viewer, view.Step);
    }
}
