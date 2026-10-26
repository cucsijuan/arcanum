// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Encore (rule 702.141): exiled from the graveyard as a sorcery, a token copy for each opponent that has to attack them.</summary>
public sealed partial class Game
{
    /// <summary>
    /// One token copy of the exiled card for each opponent, required to attack that opponent this turn if able. Haste and the
    /// end step sacrifice come from the ability, not the copy (they aren't copiable values, rule 707.9b doesn't apply).
    /// </summary>
    private async Task EncoreAsync(EffectContext ctx)
    {
        foreach (var opponent in State.OpponentsOf(ctx.Controller).ToList())
        {
            int before = ctx.Results.Created.Count;
            await ApplyAsync(new CreateTokenCopy(Subject.Self, 1), ctx);
            foreach (var token in ctx.Results.Created.Skip(before).Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList())
            {
                State.AttackPlayerRequirements.Add((token.Id, token.Version, opponent, State.TurnNumber));
                State.LastingEffects.Add(new UntilEndOfTurnEffect(token.Id, token.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                State.AtNextEndStepEffects.Add((token.Id, ctx.Controller, token.Id, token.Version, new Effect[] { new SacrificeIt(Subject.Triggered) }, null, null));
            }
        }
    }
}
