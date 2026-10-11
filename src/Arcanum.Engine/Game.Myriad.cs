// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Myriad (rule 702.116): token copies attacking each other opponent, exiled at end of combat.</summary>
public sealed partial class Game
{
    /// <summary>
    /// "For each opponent other than defending player, you may create a token copy that's tapped and attacking that player or a
    /// planeswalker they control. Exile the tokens at end of combat." The copies weren't declared as attackers (no attack triggers).
    /// </summary>
    private async Task MyriadAsync(EffectContext ctx)
    {
        if (State.Combat is not { } combat) return;
        var defending = ctx.Trigger?.Defender;
        foreach (var opponent in State.OpponentsOf(ctx.Controller).Where(o => o != defending).ToList())
        {
            var name = State.GetPlayer(opponent).Name;
            if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                    new YesNoRequest($"Myriad: create a token copy of {ctx.Source.Name} attacking {name} or a planeswalker they control?", ctx.Source.Id)))
                continue;
            var walkers = State.PermanentsControlledBy(opponent).Where(c => c.Is(CardType.Planeswalker)).ToList();
            CardId? walker = null;
            if (walkers.Count > 0)
            {
                int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(
                    $"Myriad: choose what the copy attacks", ctx.Source.Id, new[] { name }.Concat(walkers.Select(w => w.Name)).ToList(), OptionKind.Other));
                Require(pick >= 0 && pick <= walkers.Count, "Choose the player or one of their planeswalkers.");
                if (pick > 0) walker = walkers[pick - 1].Id;
            }
            int before = ctx.Results.Created.Count;
            await ApplyAsync(new CreateTokenCopy(Subject.Self, 1, false, false) { Tapped = true, ExileAtEndOfCombat = true }, ctx);
            foreach (var made in ctx.Results.Created.Skip(before).Where(id => State.GetCard(id).Zone == Zone.Battlefield))
                combat.Attacks.Add(new AttackInfo { Attacker = made, Defender = opponent, Planeswalker = walker });
        }
    }
}
