// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Moves a card between zones. Cards always go to their owner's per-player zones (rule 400.3).</summary>
    private void MoveCard(CardId id, Zone to, bool toBottom = false, PlayerId? controller = null, CardId? attachTo = null)
    {
        var card = State.GetCard(id);
        var from = card.Zone;
        var owner = State.GetPlayer(card.Owner);
        var lastController = card.Controller;

        switch (from)
        {
            case Zone.Battlefield:
                State.Battlefield.Remove(id);
                State.Combat?.Remove(id);
                break;
            case Zone.Stack:
                State.Stack.RemoveAll(s => s is SpellOnStack spell && spell.Card == id);
                break;
            default:
                owner.GetZone(from).Remove(id);
                break;
        }

        card.ResetStatus();
        card.Zone = to;
        switch (to)
        {
            case Zone.Battlefield:
                card.Controller = controller ?? card.Owner;
                card.AttachedTo = attachTo;
                // Replacement effects that modify how the permanent enters (rule 614.1c).
                if (card.Definition.EntersTapped) card.Tapped = true;
                if (card.Definition.EntersWithCounters > 0) card.Counters[Abilities.CounterKind.PlusOnePlusOne] = card.Definition.EntersWithCounters;
                State.Battlefield.Add(id);
                break;
            case Zone.Stack:
                card.Controller = controller ?? card.Owner;
                break;
            case Zone.Library:
                if (toBottom) owner.Library.Add(id);
                else owner.Library.Insert(0, id);
                break;
            default:
                owner.GetZone(to).Add(id);
                break;
        }
        RecomputeContinuousEffects();
        Emit(new CardMoved(id, card.Owner, from, to, lastController));

        // A token that leaves the battlefield ceases to exist (rule 111.7, 704.5d).
        if (card.Definition.IsToken && to != Zone.Battlefield && to != Zone.Stack) owner.GetZone(to).Remove(id);
    }

    private void Draw(PlayerId playerId, int count = 1)
    {
        var player = State.GetPlayer(playerId);
        for (int i = 0; i < count; i++)
        {
            if (player.Library.Count == 0)
            {
                player.AttemptedDrawFromEmptyLibrary = true; // loses at next SBA check (rule 704.5b)
                return;
            }
            var top = player.Library[0];
            MoveCard(top, Zone.Hand);
            Emit(new CardDrawn(playerId, top));
        }
    }

    private void Shuffle(Player player)
    {
        Rng.Shuffle(player.Library);
        Emit(new LibraryShuffled(player.Id));
    }
}
