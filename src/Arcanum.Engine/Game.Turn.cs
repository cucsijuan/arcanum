// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private bool _skipCombatDamageSteps;
    private bool _endTurnRequested;
    private int _extraCombats;

    private async Task RunTurnAsync(bool skipDraw)
    {
        State.TurnNumber++;
        var active = State.GetPlayer(State.ActivePlayer);
        active.LandsPlayedThisTurn = 0;
        State.CreaturesDiedThisTurn = 0;
        State.PermanentsSacrificedThisTurn = 0;
        State.BlocksThisTurn.Clear();
        State.CombatsThisTurn = 0;
        State.SpellsCastThisTurnCount = 0;
        State.PreventionShields.RemoveAll(p => p.Turn < State.TurnNumber);
        State.DamageTripled.RemoveAll(p => p.Turn < State.TurnNumber);
        State.CantAttackThisCombat.Clear();
        State.CantSacrificeThisTurn.Clear();
        foreach (var player in State.Players)
        {
            player.CreaturesDiedThisTurn = 0;
            player.PlayersAttackedThisTurn.Clear();
            player.SacrificedThisTurn.Clear();
            player.PermanentLeftThisTurn = false;
            player.EnteredThisTurn.Clear();
            player.DamageTakenThisTurn = 0;
            player.AttackedThisTurn = false;
            player.LifeGainedThisTurn = 0;
            player.LifeLostThisTurn = 0;
            player.CardsDrawnThisTurn = 0;
            player.LifeGainsThisTurn = 0;
            player.SpellsCastThisTurn.Clear();
            player.ExtraLandsThisTurn = 0;
            player.AttackersThisTurn = 0;
            player.EquipsThisTurn = 0;
            player.DrewInDrawStep = false;
            player.GraveyardTypesUsedThisTurn = 0;
        }
        foreach (var permanent in State.PermanentsControlledBy(active.Id)) permanent.ControlledSinceTurnStart = true;
        State.LastTurnOf[active.Id] = State.TurnNumber;
        State.Goads.RemoveAll(g => g.Goader == active.Id); // "until your next turn"
        State.LastingEffects.RemoveAll(e => e.UntilTurnOf == active.Id);
        active.Protected = false; // "protection from everything until your next turn"
        foreach (var card in State.Cards.Values)
        {
            card.ActivatedThisTurn.Clear();
            card.TriggeredThisTurn.Clear();
            card.DoneThisTurn.Clear();
            card.LoyaltyActivatedThisTurn = false;
            card.ResolvedThisTurn.Clear();
            card.DamagedThisTurnBy.Clear();
            card.CombatDamagedPlayers.Clear();
            card.AttacksThisTurn = 0;
        }
        State.PlayableFromGraveyard.RemoveAll(p => p.UntilTurn < State.TurnNumber);
        State.FlashbackGranted.RemoveAll(p => p.UntilTurn < State.TurnNumber);
        State.PlayableFromExile.RemoveAll(p => p.UntilTurn < State.TurnNumber
                                               || (p.UntilEndOfNextTurnOf is { } who && State.LastTurnOf.TryGetValue(who, out var last) && last > p.MadeOnTurn));
        Emit(new TurnBegan(State.TurnNumber, active.Id));

        _skipCombatDamageSteps = false;
        _endTurnRequested = false;
        _extraCombats = 0;
        var order = StepExtensions.TurnOrder.ToList();
        for (int index = 0; index < order.Count; index++)
        {
            var step = order[index];
            if (State.IsGameOver) return;
            if (_endTurnRequested && step != Step.Cleanup) continue; // "end the turn": straight to cleanup
            // An additional combat phase repeats the combat steps (rule 506.1).
            if (step == Step.PostcombatMain && _extraCombats > 0 && order[index - 1] == Step.EndCombat)
            {
                _extraCombats--;
                _skipCombatDamageSteps = false;
                index = order.IndexOf(Step.BeginCombat) - 1;
                continue;
            }
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
                // Phased-out permanents of the active player phase in first (rule 502.1).
                foreach (var phased in State.PhasedOut.Where(p => p.Controller == State.ActivePlayer).ToList())
                {
                    State.PhasedOut.Remove(phased);
                    if (State.GetCard(phased.Card).Zone != Zone.Battlefield) continue;
                    State.Battlefield.Add(phased.Card);
                    Emit(new PhasedIn(phased.Card));
                }
                RecomputeContinuousEffects();
                // An exerted permanent doesn't untap during its controller's next untap step (701.39a).
                var exerted = State.PermanentsControlledBy(State.ActivePlayer).Where(c => c.SkipsNextUntap).ToList();
                foreach (var permanent in exerted) permanent.SkipsNextUntap = false;
                foreach (var permanent in State.PermanentsControlledBy(State.ActivePlayer).Where(c => c.Tapped && !c.Definition.DoesntUntap && !c.Has(Cards.Keyword.DoesntUntap) && !exerted.Contains(c)).ToList())
                {
                    // A stun counter is removed instead of untapping (rule 122.1d).
                    if (permanent.CounterCount(Abilities.CounterKind.Stun) > 0)
                    {
                        permanent.Counters[Abilities.CounterKind.Stun]--;
                        continue;
                    }
                    if (permanent.Has(Cards.Keyword.UntapsByRemovingCounter))
                    {
                        // "Remove a +1/+1 counter from it instead. If you do, untap it. (Otherwise, it doesn't untap.)"
                        if (permanent.CounterCount(Abilities.CounterKind.PlusOnePlusOne) == 0) continue;
                        permanent.Counters[Abilities.CounterKind.PlusOnePlusOne]--;
                    }
                    Untap(permanent);
                }
                givesPriority = false; // rule 502.4
                break;
            case Step.Draw:
                await DrawAsync(State.ActivePlayer);
                break;
            case Step.PrecombatMain:
                // As the precombat main phase begins, its player puts a lore counter on each of their Sagas (714.3b).
                foreach (var saga in State.PermanentsControlledBy(State.ActivePlayer).Where(c => c.Definition.FinalChapter > 0).ToList())
                    PutCounters(saga, Abilities.CounterKind.Lore, 1, State.ActivePlayer);
                break;
            case Step.BeginCombat:
                State.Combat = new CombatState();
                State.CombatsThisTurn++;
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
            case Step.EndCombat:
                // "Sacrifice it at end of combat" (the Ring's third ability).
                foreach (var (card, version) in State.SacrificeAtEndOfCombat.ToList())
                    if (State.GetCard(card) is { Zone: Zone.Battlefield } doomed && doomed.Version == version) await SacrificePermanentAsync(card);
                State.SacrificeAtEndOfCombat.Clear();
                foreach (var (card, version) in State.ExileAtEndOfCombat.ToList())
                    if (State.GetCard(card) is { Zone: Zone.Battlefield } gone && gone.Version == version) await MoveCardAsync(card, Zone.Exile);
                State.ExileAtEndOfCombat.Clear();
                break;
            case Step.Cleanup:
                // Normally no player gets priority in the cleanup step (514.3). If state-based actions apply or abilities
                // trigger during it, they're handled and players get priority; then another cleanup step follows (514.3a).
                for (int rounds = 0; rounds < 20 && !State.IsGameOver; rounds++)
                {
                    await CleanupAsync();
                    bool anything = _pendingTriggers.Count > 0 || await ApplyStateBasedActionsOnceAsync();
                    if (!anything) break;
                    await RunPriorityAsync();
                    foreach (var player in State.Players) player.ManaPool.Clear(endOfTurn: false);
                }
                givesPriority = false;
                break;
        }

        if (givesPriority && !State.IsGameOver) await RunPriorityAsync();

        if (step == Step.EndCombat) State.Combat = null;
        foreach (var player in State.Players) // rule 500.4
            player.ManaPool.Clear(endOfTurn: step == Step.Cleanup, keep: Has(player.Id, Cards.Replacements.KeepGreenMana) ? Mana.ManaType.Green : null);
    }

    private async Task CleanupAsync()
    {
        var active = State.GetPlayer(State.ActivePlayer);
        int excess = Has(active.Id, Cards.Replacements.NoMaximumHandSize) || active.NoMaximumHandSize ? 0 : active.Hand.Count - Config.MaxHandSize;
        if (excess > 0)
        {
            var chosen = await ControllerOf(active.Id).ChooseDiscardAsync(ViewFor(active.Id), excess);
            Require(chosen.Count == excess && chosen.Distinct().Count() == excess && chosen.All(active.Hand.Contains),
                $"Must discard exactly {excess} distinct cards from hand.");
            foreach (var card in chosen) await DiscardCardAsync(active.Id, card, null); // discarding to hand size is discarding (514.1)
        }
        // Damage wears off and "until end of turn" effects end at the same time (rule 514.2).
        foreach (var permanent in State.Battlefield.Select(State.GetCard))
        {
            permanent.Damage = 0;
            permanent.RegenerationShields = 0; // "the next time it would be destroyed this turn"
        }
        State.UntilEndOfTurn.Clear();
        // "Until end of turn" and "until the end of your next turn" control effects end now.
        State.ControlEffects.RemoveAll(c => c.UntilEndOfTurn
                                            || (c.UntilEndOfNextTurnOf is { } who && who == State.ActivePlayer && State.TurnNumber > c.MadeOnTurn));
        State.ExileIfDies.Clear();
        State.CombatDamagePrevented.Clear();
        foreach (var emblem in State.EmblemsUntilEndOfTurn)
        {
            State.Emblems.Remove(emblem);
            State.GetCard(emblem).Zone = Zone.Exile; // gone (an emblem is no card)
        }
        State.EmblemsUntilEndOfTurn.Clear();
        RecomputeContinuousEffects();
    }
}
