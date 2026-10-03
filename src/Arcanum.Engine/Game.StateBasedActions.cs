// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Performs state-based actions until none apply (rule 704.3).</summary>
    private void CheckStateBasedActions()
    {
        while (!State.IsGameOver && ApplyStateBasedActionsOnce()) { }
    }

    private bool ApplyStateBasedActionsOnce()
    {
        bool any = false;

        foreach (var player in State.LivingPlayers.ToList())
        {
            if (Has(player.Id, Cards.Replacements.YouCantLose)) continue;
            if (player.Life <= 0) { Lose(player.Id, "life total 0 or less"); any = true; }               // 704.5a
            else if (player.AttemptedDrawFromEmptyLibrary) { Lose(player.Id, "drew from an empty library"); any = true; } // 704.5b
            else if (player.Poison >= 10) { Lose(player.Id, "ten poison counters"); any = true; } // 704.5c
            else if (CommanderDamageLoss(player)) { Lose(player.Id, "21 combat damage from a commander"); any = true; } // 704.6c
        }
        if (State.IsGameOver) return any;

        // Auras attached to nothing legal go to the graveyard (704.5m); Equipment just becomes unattached (704.5n).
        foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
        {
            if (card.Definition.EnchantTarget is { } enchant)
            {
                bool legal = card.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield } h
                             && (enchant.Kind != Abilities.TargetKind.Creature || h.IsCreature) && !h.Has(Keyword.ProtectionFromEverything);
                if (!legal) { MoveCard(card.Id, Zone.Graveyard); any = true; }
            }
            else if (card.AttachedTo is { } equipped && (State.GetCard(equipped) is not { Zone: Zone.Battlefield, IsCreature: true } eq || eq.Has(Keyword.ProtectionFromEverything)))
            {
                card.AttachedTo = null;
                RecomputeContinuousEffects();
                any = true;
            }
        }

        // +1/+1 and -1/-1 counters on the same permanent cancel out (704.5q).
        foreach (var card in State.Battlefield.Select(State.GetCard))
        {
            int pairs = Math.Min(card.CounterCount(Abilities.CounterKind.PlusOnePlusOne), card.CounterCount(Abilities.CounterKind.MinusOneMinusOne));
            if (pairs == 0) continue;
            card.Counters[Abilities.CounterKind.PlusOnePlusOne] -= pairs;
            card.Counters[Abilities.CounterKind.MinusOneMinusOne] -= pairs;
            any = true;
        }

        // A planeswalker with no loyalty goes to its owner's graveyard (704.5i).
        foreach (var walker in State.Battlefield.Select(State.GetCard).Where(c => c.Is(CardType.Planeswalker) && c.CounterCount(Abilities.CounterKind.Loyalty) <= 0).ToList())
        {
            MoveCard(walker.Id, Zone.Graveyard);
            any = true;
        }

        var dying = State.Battlefield.Select(State.GetCard)
            .Where(c => c.IsCreature && (
                c.Toughness <= 0 // 704.5f: put into the graveyard, even if indestructible
                || (!c.Has(Keyword.Indestructible) && (c.Damage >= c.Toughness || c.DamagedByDeathtouch)))) // 704.5g, 704.5h
            .ToList();
        foreach (var survivor in State.Battlefield.Select(State.GetCard)) survivor.DamagedByDeathtouch = false;
        foreach (var creature in dying)
        {
            MoveCard(creature.Id, Zone.Graveyard);
            Emit(new CreatureDied(creature.Id));
            any = true;
        }
        return any;
    }

    private void Lose(PlayerId playerId, string reason)
    {
        var player = State.GetPlayer(playerId);
        player.HasLost = true;
        Emit(new PlayerLost(playerId, reason));

        var living = State.LivingPlayers.ToList();
        if (living.Count <= 1)
        {
            State.IsGameOver = true;
            State.Winner = living.Count == 1 ? living[0].Id : null;
            State.PriorityPlayer = null;
            Emit(new GameEnded(State.Winner));
            return;
        }

        // The game goes on without them: their objects leave the game (rule 800.4a).
        foreach (var id in State.Battlefield.Where(id => State.GetCard(id).Owner == playerId).ToList())
            MoveCard(id, Zone.Exile);
        State.Stack.RemoveAll(s => s.Controller == playerId);
        State.Combat?.Attacks.RemoveAll(a => a.Defender == playerId);
    }
}
