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
        State.CreaturesDiedThisTurn = 0;
        foreach (var player in State.Players)
        {
            player.AttackedThisTurn = false;
            player.LifeGainedThisTurn = 0;
            player.LifeLostThisTurn = 0;
            player.CardsDrawnThisTurn = 0;
            player.LifeGainsThisTurn = 0;
        }
        foreach (var permanent in State.PermanentsControlledBy(active.Id)) permanent.ControlledSinceTurnStart = true;
        foreach (var card in State.Cards.Values)
        {
            card.ActivatedThisTurn.Clear();
            card.TriggeredThisTurn.Clear();
            card.LoyaltyActivatedThisTurn = false;
        }
        State.PlayableFromExile.RemoveAll(p => p.UntilTurn < State.TurnNumber);
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
                foreach (var permanent in State.PermanentsControlledBy(State.ActivePlayer).Where(c => c.Tapped && !c.Definition.DoesntUntap && !c.Has(Cards.Keyword.DoesntUntap)).ToList())
                {
                    // A stun counter is removed instead of untapping (rule 122.1d).
                    if (permanent.CounterCount(Abilities.CounterKind.Stun) > 0)
                    {
                        permanent.Counters[Abilities.CounterKind.Stun]--;
                        continue;
                    }
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
                givesPriority = false; // rule 514.3; a cleanup with pending SBAs or triggers isn't handled yet
                break;
        }

        if (givesPriority && !State.IsGameOver) await RunPriorityAsync();

        if (step == Step.EndCombat) State.Combat = null;
        foreach (var player in State.Players) player.ManaPool.Clear(); // rule 500.4
    }

    private async Task CleanupAsync()
    {
        var active = State.GetPlayer(State.ActivePlayer);
        int excess = Has(active.Id, Cards.Replacements.NoMaximumHandSize) ? 0 : active.Hand.Count - Config.MaxHandSize;
        if (excess > 0)
        {
            var chosen = await ControllerOf(active.Id).ChooseDiscardAsync(ViewFor(active.Id), excess);
            Require(chosen.Count == excess && chosen.Distinct().Count() == excess && chosen.All(active.Hand.Contains),
                $"Must discard exactly {excess} distinct cards from hand.");
            foreach (var card in chosen) MoveCard(card, Zone.Graveyard);
        }
        // Damage wears off and "until end of turn" effects end at the same time (rule 514.2).
        foreach (var permanent in State.Battlefield.Select(State.GetCard)) permanent.Damage = 0;
        State.UntilEndOfTurn.Clear();
        foreach (var control in State.TemporaryControl)
        {
            var card = State.GetCard(control.Card);
            if (card.Version != control.Version || card.Zone != Zone.Battlefield) continue;
            card.Controller = control.Original;
            card.ControlledSinceTurnStart = false;
            Emit(new ControlChanged(card.Id, control.Original));
        }
        State.TemporaryControl.Clear();
        State.ExileIfDies.Clear();
        State.CombatDamagePrevented.Clear();
        RecomputeContinuousEffects();
    }
}
