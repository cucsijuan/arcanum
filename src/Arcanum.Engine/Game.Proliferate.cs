// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Proliferate (rule 701.34).</summary>
public sealed partial class Game
{
    /// <summary>
    /// "Choose any number of permanents and/or players, then give each another counter of each kind already there": the controller
    /// chooses among the permanents with counters, then for each player with poison counters whether to give them another.
    /// </summary>
    private async Task ProliferateAsync(EffectContext ctx)
    {
        // "If you would proliferate, proliferate twice instead": each such permanent doubles the number of times.
        int doublers = State.PermanentsControlledBy(ctx.Controller).Count(c => c.HasTextAbilities && c.Definition.ProliferateTwice);
        ctx.Results.Proliferated.Clear();
        for (int times = 1 << Math.Min(doublers, 10); times > 0; times--) await ProliferateOnceAsync(ctx);
    }

    private async Task ProliferateOnceAsync(EffectContext ctx)
    {
        var withCounters = State.Battlefield.Select(State.GetCard).Where(c => c.Counters.Values.Any(v => v > 0)).ToList();
        IReadOnlyList<Core.CardId> chosen = Array.Empty<Core.CardId>();
        if (withCounters.Count > 0)
        {
            chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                "Proliferate: choose any number of permanents to give another counter of each kind they have", ctx.Source.Id,
                withCounters.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, withCounters.Count, CardChoicePurpose.Keep));
            Require(chosen.Distinct().Count() == chosen.Count && chosen.All(id => withCounters.Any(c => c.Id == id)), "Choose permanents with counters.");
        }
        var poisoned = new List<Core.PlayerId>();
        foreach (var player in State.ApnapOrder().Where(p => State.GetPlayer(p).Poison > 0).ToList())
            if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                    new YesNoRequest($"Proliferate: give {State.GetPlayer(player).Name} another poison counter?", ctx.Source.Id)))
                poisoned.Add(player);
        BeginSimultaneous();
        foreach (var card in chosen.Select(State.GetCard).Where(c => c.Zone == Arcanum.Engine.State.Zone.Battlefield))
        {
            foreach (var kind in card.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
                PutCounters(card, kind, 1, ctx.Controller);
            if (!ctx.Results.Proliferated.Contains(card.Id)) ctx.Results.Proliferated.Add(card.Id);
        }
        EndSimultaneous();
        foreach (var player in poisoned)
        {
            State.GetPlayer(player).Poison++;
            Emit(new PoisonGiven(player, 1));
        }
    }

    /// <summary>Phases out permanents, with anything attached to them (indirect phasing, rule 702.26g); a permanent phases out only once.</summary>
    private void PhaseOutPermanents(IEnumerable<Card> permanents)
    {
        foreach (var card in permanents.Where(c => State.Battlefield.Contains(c.Id)).ToList())
        {
            // Already phased out with the permanent it's attached to.
            if (!State.Battlefield.Contains(card.Id)) continue;
            foreach (var phasing in State.Battlefield.Select(State.GetCard).Where(a => a.AttachedTo == card.Id).Append(card).ToList())
            {
                State.Battlefield.Remove(phasing.Id);
                State.Combat?.Remove(phasing.Id);
                State.PhasedOut.Add((phasing.Id, card.Controller));
                Emit(new PhasedOut(phasing.Id));
            }
        }
    }

    /// <summary>"Then choose any number of permanents you control that had a counter put on them this way. Those permanents phase out."</summary>
    private async Task PhaseOutChosenProliferatedAsync(EffectContext ctx)
    {
        var options = ctx.Results.Proliferated.Select(State.GetCard).Where(c => c.Zone == Arcanum.Engine.State.Zone.Battlefield && c.Controller == ctx.Controller).ToList();
        if (options.Count == 0) return;
        var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
            "Choose any number of these permanents to phase out", ctx.Source.Id,
            options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, options.Count, CardChoicePurpose.Keep));
        Require(chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(c => c.Id == id)), "Choose among the listed permanents.");
        PhaseOutPermanents(chosen.Select(State.GetCard));
    }
}
