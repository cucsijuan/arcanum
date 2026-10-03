// SPDX-License-Identifier: AGPL-3.0-or-later
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
            if (player.Life <= 0) { Lose(player.Id, "life total 0 or less"); any = true; }               // 704.5a
            else if (player.AttemptedDrawFromEmptyLibrary) { Lose(player.Id, "drew from an empty library"); any = true; } // 704.5b
        }
        if (State.IsGameOver) return any;

        var dying = State.Battlefield.Select(State.GetCard)
            .Where(c => c.IsCreature && (c.Toughness <= 0 || c.Damage >= c.Toughness)) // 704.5f, 704.5g
            .ToList();
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
