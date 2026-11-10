// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Rooms (rule 709.5): a Room spell is one of its doors; it enters with that door unlocked. Its controller unlocks a locked door as a
/// special action any time they could cast a sorcery, paying that door's mana cost (116.2m).
/// </summary>
public sealed partial class Game
{
    /// <summary>What unlocking this Room's door costs its controller now: the door's mana cost, less "unlock costs you pay cost {1} less".</summary>
    private ManaCost UnlockCost(Card room, int door, PlayerId payer) =>
        room.PrintedDefinition.SplitHalves![door].ManaCost.MinusGeneric(State.PermanentsControlledBy(payer)
            .Where(p => p.HasTextAbilities).Sum(p => p.Definition.UnlockCostsLess));

    /// <summary>The locked doors of the player's Rooms they can pay to unlock now (sorcery timing only).</summary>
    private IEnumerable<PlayerAction> UnlockActions(PlayerId playerId)
    {
        foreach (var room in State.PermanentsControlledBy(playerId).Where(c => !c.TurnedFaceDown && c.PrintedDefinition.IsRoom).ToList())
            for (int door = 0; door < 2; door++)
                if (!room.IsUnlocked(door) && AffordableForSpecialAction(playerId, UnlockCost(room, door, playerId), unlock: true))
                    yield return new UnlockDoor(room.Id, door);
    }

    /// <summary>Unlocking a door as a special action: pay its cost; it doesn't use the stack (116.2m).</summary>
    private async Task<bool> UnlockDoorAsync(Player player, UnlockDoor action)
    {
        var room = State.GetCard(action.Card);
        Require(room.Zone == Zone.Battlefield && room.Controller == player.Id && room.PrintedDefinition.IsRoom && !room.TurnedFaceDown
                && action.Door is 0 or 1 && !room.IsUnlocked(action.Door), "Only a locked door of a Room you control can be unlocked.");
        if (!await PayForSpecialActionAsync(player, room.Id, UnlockCost(room, action.Door, player.Id), unlock: true)) return false;
        Unlock(room, action.Door, player.Id);
        return true;
    }

    /// <summary>A door becomes unlocked: the Room has that door's name, cost and rules from now on.</summary>
    private void Unlock(Card room, int door, PlayerId by)
    {
        if (room.IsUnlocked(door)) return;
        room.UnlockedDoors |= 1 << door;
        RecomputeContinuousEffects();
        Emit(new DoorUnlocked(room.Id, by, door, Fully: room.UnlockedDoors == 3, room.PrintedDefinition.SplitHalves![door].Name));
    }

    /// <summary>A door becomes locked again: the Room loses that door's name, cost and rules.</summary>
    private void Lock(Card room, int door, PlayerId by)
    {
        if (!room.IsUnlocked(door)) return;
        room.UnlockedDoors &= ~(1 << door);
        RecomputeContinuousEffects();
        Emit(new DoorLocked(room.Id, by, door, room.PrintedDefinition.SplitHalves![door].Name));
    }

    /// <summary>
    /// "Unlock a locked door of a Room you control" (the player chooses the Room, without targeting); "unlock a locked door of up to one
    /// target Room you control"; "lock or unlock a door of target Room you control".
    /// </summary>
    private async Task UnlockADoorAsync(UnlockADoor effect, EffectContext ctx)
    {
        var who = ctx.Controller;
        var rooms = effect.Room is { } subject
            ? CardsFor(subject, ctx).Where(c => c.Zone == Zone.Battlefield && c.PrintedDefinition.IsRoom).ToList()
            : State.PermanentsControlledBy(who).Where(c => c.PrintedDefinition.IsRoom && !c.TurnedFaceDown && c.UnlockedDoors != 3).ToList();
        if (!effect.OrLock) rooms = rooms.Where(r => r.UnlockedDoors != 3).ToList();
        if (rooms.Count == 0) return;
        var room = rooms[0];
        if (rooms.Count > 1)
        {
            var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{ctx.Source.Name}: choose a Room",
                ctx.Source.Id, rooms.Select(r => ViewBuilder.Card(State, r.Id, who)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && rooms.Any(r => r.Id == pick[0]), "Choose one of your Rooms.");
            room = State.GetCard(pick[0]);
        }
        var doors = room.PrintedDefinition.SplitHalves!;
        var choices = Enumerable.Range(0, 2).Where(d => effect.OrLock || !room.IsUnlocked(d))
            .Select(d => (Door: d, Unlock: !room.IsUnlocked(d))).ToList();
        var chosen = choices[0];
        if (choices.Count > 1)
        {
            int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{ctx.Source.Name}: which door?", room.Id,
                choices.Select(c => $"{(c.Unlock ? "Unlock" : "Lock")} {doors[c.Door].Name}").ToList(), OptionKind.Other));
            Require(i >= 0 && i < choices.Count, "Choose a door.");
            chosen = choices[i];
        }
        if (chosen.Unlock) Unlock(room, chosen.Door, who);
        else Lock(room, chosen.Door, who);
    }

    /// <summary>The power of a beheld creature or card: as it is if it is still that object, or as it last was on the battlefield.</summary>
    private static int BeheldPower(Card card, int version) =>
        card.Version == version ? (card.Zone == Zone.Battlefield ? card.Power : card.Definition.Power ?? 0)
        : card.LastKnownOf(version)?.Power ?? card.PrintedDefinition.Power ?? 0;

    /// <summary>
    /// A card's miracle cost: printed, or "each enchantment card in your hand has miracle; its miracle cost is equal to its mana cost reduced by {4}".
    /// </summary>
    private Mana.ManaCost? MiracleCost(Card card) =>
        card.Definition.Miracle
        ?? (card.Zone == Zone.Hand && card.Is(Cards.CardType.Enchantment) && HasRule(card.Owner, Cards.ExtraRules.HandEnchantmentsHaveMiracle)
            ? card.Definition.ManaCost.WithX(0).MinusGeneric(4)
            : card.Zone == Zone.Hand && (card.Is(Cards.CardType.Instant) || card.Is(Cards.CardType.Sorcery)) && HasRule(card.Owner, Cards.ExtraRules.HandInstantsAndSorceriesHaveMiracleTwo)
                ? Mana.ManaCost.Parse("{2}") : null);

    /// <summary>Whether a permanent the player controls (with its abilities) has this card-wide rule.</summary>
    private bool HasRule(PlayerId player, Cards.ExtraRules rule) =>
        !State.GetPlayer(player).HasLost && State.PermanentsControlledBy(player).Any(c => c.HasTextAbilities && (c.Definition.ExtraRules & rule) != 0);

    /// <summary>The unlocked doors among Rooms the player controls, and how many different names they have.</summary>
    private (int Doors, int Names) UnlockedDoorsOf(PlayerId player)
    {
        var names = new List<string>();
        foreach (var room in State.PermanentsControlledBy(player).Where(c => c.PrintedDefinition.IsRoom && !c.TurnedFaceDown))
            for (int door = 0; door < 2; door++)
                if (room.IsUnlocked(door)) names.Add(room.PrintedDefinition.SplitHalves![door].Name);
        return (names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
