// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Blight N: the player puts N -1/-1 counters on a creature they control. As a cost (<see cref="ExtraCost.Blight"/>) it can only be
/// paid with such a creature; as an effect (<see cref="Abilities.Blight"/>) a player without one does nothing.
/// </summary>
public sealed partial class Game
{
    /// <summary>Whether -1/-1 counters can be put on the permanent now (no effect forbids them).</summary>
    private bool CanHaveMinusCountersPut(Card card) =>
        !CountersBannedOn(card, null) && !card.Has(Keyword.CantHaveCounters)
        && !(card.IsCreature && State.PermanentsControlledBy(card.Controller).Any(p => p.HasTextAbilities && p.Definition.YourCreaturesCantHaveMinusCounters));

    private static string CountersText(int count) => count == 1 ? "a -1/-1 counter" : $"{count} -1/-1 counters";

    /// <summary>The player blights: puts the counters on the chosen creature, as counters that player puts.</summary>
    private void Blight(PlayerId player, int count, CardId creature)
    {
        Emit(new Blighted(player, creature, count));
        PutCounters(State.GetCard(creature), CounterKind.MinusOneMinusOne, count, player);
    }

    /// <summary>
    /// "Blight N" as an effect: each player named chooses a creature they control in APNAP order, then the counters are put
    /// on at the same time (rule 101.4).
    /// </summary>
    private async Task ApplyBlightAsync(Abilities.Blight blight, EffectContext ctx)
    {
        int count = Eval(blight.Count, ctx);
        if (count <= 0) return;
        var picks = new List<(PlayerId Player, CardId Creature)>();
        foreach (var player in PlayersFor(blight.Who, ctx).ToList())
        {
            var candidates = BlightCandidates(player);
            if (candidates.Count == 0) continue;
            var pick = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                $"Blight {count}: put {CountersText(count)} on a creature you control", ctx.Source.Id,
                candidates.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 1, 1, CardChoicePurpose.Weaken) { Amount = count });
            Require(pick.Count == 1 && candidates.Any(c => c.Id == pick[0]), "Choose a creature you control.");
            picks.Add((player, pick[0]));
        }
        BeginSimultaneous();
        foreach (var (player, creature) in picks) Blight(player, count, creature);
        EndSimultaneous();
        // "The blighted creature": the creatures chosen are "found" for the next effects.
        ctx.Results.Found.Clear();
        ctx.Results.Found.AddRange(picks.Select(p => p.Creature));
    }
}
