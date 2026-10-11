// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>"Reselect which player or permanent target attacking creature is attacking."</summary>
public sealed partial class Game
{
    /// <summary>
    /// The controller of the effect may have the attacking creature attack another player or planeswalker (never its own controller or
    /// their permanents, rule 506.2); it stays attacking, wasn't declared again (no attack triggers), and any block stays as it is.
    /// </summary>
    private async Task ReselectAttackAsync(ReselectAttack reselect, EffectContext ctx)
    {
        if (State.Combat is not { } combat) return;
        foreach (var creature in CardsFor(reselect.What, ctx).ToList())
        {
            if (combat.FindAttack(creature.Id) is not { } attack) continue;
            var choices = new List<(string Label, PlayerId Player, CardId? Walker)>();
            foreach (var player in State.OpponentsOf(creature.Controller))
            {
                choices.Add((State.GetPlayer(player).Name, player, null));
                foreach (var walker in State.PermanentsControlledBy(player).Where(c => c.Is(CardType.Planeswalker)))
                    choices.Add(($"{walker.Name} ({State.GetPlayer(player).Name})", player, walker.Id));
            }
            int current = choices.FindIndex(c => c.Player == attack.Defender && c.Walker == attack.Planeswalker);
            int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(
                $"{ctx.Source.Name}: choose what {creature.Name} attacks", creature.Id, choices.Select(c => c.Label).ToList(), OptionKind.Other));
            Require(pick >= 0 && pick < choices.Count, "Choose one of the players or planeswalkers.");
            if (pick == current) continue;
            var moved = new AttackInfo { Attacker = attack.Attacker, Defender = choices[pick].Player, Planeswalker = choices[pick].Walker, IsBlocked = attack.IsBlocked };
            moved.Blockers.AddRange(attack.Blockers);
            combat.Attacks[combat.Attacks.IndexOf(attack)] = moved;
            Emit(new Events.ChoiceMade(creature.Id, $"attacks {choices[pick].Label}"));
        }
    }
}
