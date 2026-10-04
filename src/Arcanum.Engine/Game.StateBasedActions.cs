// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Performs state-based actions until none apply (rule 704.3).</summary>
    private async Task CheckStateBasedActionsAsync()
    {
        while (!State.IsGameOver && await ApplyStateBasedActionsOnceAsync()) { }
    }

    /// <summary>
    /// One check: every state-based action that applies is performed at the same time (rule 704.3), so permanents
    /// put into graveyards together see each other leave.
    /// </summary>
    private async Task<bool> ApplyStateBasedActionsOnceAsync()
    {
        bool any = false;

        var losers = new List<(PlayerId Player, string Reason)>();
        foreach (var player in State.LivingPlayers)
        {
            if (Has(player.Id, Cards.Replacements.YouCantLose)) continue;
            if (player.Life <= 0) losers.Add((player.Id, "life total 0 or less"));                                  // 704.5a
            else if (player.AttemptedDrawFromEmptyLibrary) losers.Add((player.Id, "drew from an empty library"));    // 704.5b
            else if (player.Poison >= 10) losers.Add((player.Id, "ten poison counters"));                            // 704.5c
            else if (CommanderDamageLoss(player)) losers.Add((player.Id, "21 combat damage from a commander"));      // 704.6c
        }
        if (losers.Count > 0) { LoseAll(losers); any = true; }
        if (State.IsGameOver) return any;

        var permanents = State.Battlefield.Select(State.GetCard).ToList();
        var toGraveyard = new List<Card>();
        var died = new List<Card>();
        var unattach = new List<Card>();

        foreach (var card in permanents)
        {
            // Auras attached to nothing legal go to the graveyard (704.5m); Equipment just becomes unattached (704.5n).
            if (card.Definition.EnchantTarget is { } enchant)
            {
                bool legal = card.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield } h
                             && (enchant.Kind != Abilities.TargetKind.Creature || h.IsCreature) && !h.Has(Keyword.ProtectionFromEverything);
                if (!legal) toGraveyard.Add(card);
            }
            else if (card.AttachedTo is { } equipped && (State.GetCard(equipped) is not { Zone: Zone.Battlefield, IsCreature: true } eq || eq.Has(Keyword.ProtectionFromEverything)))
            {
                unattach.Add(card);
            }
            // A planeswalker with no loyalty goes to its owner's graveyard (704.5i).
            if (card.Is(CardType.Planeswalker) && card.CounterCount(Abilities.CounterKind.Loyalty) <= 0) toGraveyard.Add(card);
            // 704.5f: toughness 0 or less (even if indestructible); 704.5g, 704.5h: lethal damage or deathtouch damage.
            if (card.IsCreature && (card.Toughness <= 0 || (!card.Has(Keyword.Indestructible) && (card.Damage >= card.Toughness || card.DamagedByDeathtouch))))
                died.Add(card);
        }

        // Legend rule (704.5j): a player with two or more legendary permanents with the same name chooses one; the
        // rest go to their owners' graveyards.
        foreach (var group in permanents.Where(c => (c.Definition.Supertypes & Supertype.Legendary) != 0)
                     .GroupBy(c => (c.Controller, c.Name)).Where(g => g.Count() > 1).ToList())
        {
            var options = group.Select(c => ViewBuilder.Card(State, c.Id, group.Key.Controller, reveal: true)).ToList();
            var keep = await ControllerOf(group.Key.Controller).ChooseCardsAsync(ViewFor(group.Key.Controller),
                new CardChoiceRequest($"Legend rule: choose the {group.Key.Name} to keep", group.First().Id, options, 1, 1, CardChoicePurpose.Keep));
            var kept = keep.Count == 1 && group.Any(c => c.Id == keep[0]) ? keep[0] : group.Last().Id;
            toGraveyard.AddRange(group.Where(c => c.Id != kept));
        }

        // World rule (704.5k): only the newest world permanent stays.
        var worlds = permanents.Where(c => (c.Definition.Supertypes & Supertype.World) != 0).ToList();
        if (worlds.Count > 1) toGraveyard.AddRange(worlds.Take(worlds.Count - 1));

        // +1/+1 and -1/-1 counters on the same permanent cancel out (704.5q).
        foreach (var card in permanents)
        {
            int pairs = Math.Min(card.CounterCount(Abilities.CounterKind.PlusOnePlusOne), card.CounterCount(Abilities.CounterKind.MinusOneMinusOne));
            if (pairs == 0) continue;
            card.Counters[Abilities.CounterKind.PlusOnePlusOne] -= pairs;
            card.Counters[Abilities.CounterKind.MinusOneMinusOne] -= pairs;
            any = true;
        }

        foreach (var card in unattach) card.AttachedTo = null;
        if (unattach.Count > 0) { RecomputeContinuousEffects(); any = true; }

        foreach (var survivor in permanents) survivor.DamagedByDeathtouch = false;
        BeginSimultaneous(); // all at once (rule 704.3)
        foreach (var card in died.Concat(toGraveyard).Distinct().ToList())
        {
            if (card.Zone != Zone.Battlefield) continue;
            MoveCard(card.Id, Zone.Graveyard);
            if (died.Contains(card)) Emit(new CreatureDied(card.Id));
            any = true;
        }
        EndSimultaneous();
        return any;
    }

    private void Lose(PlayerId playerId, string reason) => LoseAll(new[] { (playerId, reason) });

    /// <summary>
    /// Players who lose at the same time (rule 104.4a: if every remaining player loses at once, the game is a draw).
    /// </summary>
    private void LoseAll(IReadOnlyList<(PlayerId Player, string Reason)> losers)
    {
        foreach (var (playerId, reason) in losers)
        {
            State.GetPlayer(playerId).HasLost = true;
            Emit(new PlayerLost(playerId, reason));
        }

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
        foreach (var (playerId, _) in losers)
        {
            foreach (var id in State.Battlefield.Where(id => State.GetCard(id).Owner == playerId).ToList())
                MoveCard(id, Zone.Exile);
            State.Stack.RemoveAll(s => s.Controller == playerId);
            State.Combat?.Attacks.RemoveAll(a => a.Defender == playerId);
        }
    }
}
