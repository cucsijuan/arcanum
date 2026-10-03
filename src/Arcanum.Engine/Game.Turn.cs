// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private bool _skipCombatDamageSteps;

    private async Task RunTurnAsync(bool skipDraw)
    {
        State.TurnNumber++;
        var active = State.GetPlayer(State.ActivePlayer);
        active.LandsPlayedThisTurn = 0;
        foreach (var permanent in State.PermanentsControlledBy(active.Id)) permanent.ControlledSinceTurnStart = true;
        Emit(new TurnBegan(State.TurnNumber, active.Id));

        _skipCombatDamageSteps = false;
        foreach (var step in StepExtensions.TurnOrder)
        {
            if (State.IsGameOver) return;
            if (step == Step.Draw && skipDraw) continue;
            if (_skipCombatDamageSteps && step is Step.DeclareBlockers or Step.CombatDamage) continue;
            if (step == Step.CombatDamage && CombatHasFirstStrike())
            {
                // First or double strike: an extra combat damage step before the regular one (rule 510.4).
                _damagePass = DamagePass.FirstStrike;
                await RunStepAsync(step);
                if (State.IsGameOver) return;
                _damagePass = DamagePass.Regular;
                await RunStepAsync(step);
                _damagePass = DamagePass.Only;
                continue;
            }
            await RunStepAsync(step);
        }
    }

    private async Task RunStepAsync(Step step)
    {
        State.Step = step;
        Emit(new StepBegan(step, State.ActivePlayer));

        bool givesPriority = true;
        switch (step)
        {
            case Step.Untap:
                foreach (var permanent in State.PermanentsControlledBy(State.ActivePlayer).Where(c => c.Tapped).ToList())
                {
                    permanent.Tapped = false;
                    Emit(new PermanentUntapped(permanent.Id));
                }
                givesPriority = false; // rule 502.4
                break;
            case Step.Draw:
                Draw(State.ActivePlayer);
                break;
            case Step.BeginCombat:
                State.Combat = new CombatState();
                break;
            case Step.DeclareAttackers:
                await DeclareAttackersAsync();
                break;
            case Step.DeclareBlockers:
                await DeclareBlockersAsync();
                break;
            case Step.CombatDamage:
                await DealCombatDamageAsync();
                break;
            case Step.Cleanup:
                await CleanupAsync();
                givesPriority = false; // rule 514.3, unless SBAs or triggers happen (handled with triggers in M4)
                break;
        }

        if (givesPriority && !State.IsGameOver) await RunPriorityAsync();

        if (step == Step.EndCombat) State.Combat = null;
        foreach (var player in State.Players) player.ManaPool.Clear(); // rule 500.4
    }

    private async Task CleanupAsync()
    {
        var active = State.GetPlayer(State.ActivePlayer);
        int excess = active.Hand.Count - Config.MaxHandSize;
        if (excess > 0)
        {
            var chosen = await ControllerOf(active.Id).ChooseDiscardAsync(ViewFor(active.Id), excess);
            Require(chosen.Count == excess && chosen.Distinct().Count() == excess && chosen.All(active.Hand.Contains),
                $"Must discard exactly {excess} distinct cards from hand.");
            foreach (var card in chosen) MoveCard(card, Zone.Graveyard);
        }
        foreach (var permanent in State.Battlefield.Select(State.GetCard)) permanent.Damage = 0;
    }
}
