// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Commanders that went to a graveyard, exile, a hand or a library since the last check.</summary>
    private readonly List<CardId> _commandersToOffer = new();

    private void NoteCommanderMove(Card card, Zone to)
    {
        if (Config.Commander is not null && card.IsCommander && to is Zone.Graveyard or Zone.Exile or Zone.Hand or Zone.Library)
            _commandersToOffer.Add(card.Id);
    }

    /// <summary>
    /// The owner of a commander that was put into a graveyard or exile (903.9a), or into a hand or library (903.9b),
    /// may move it to the command zone. Offered before the next priority, in APNAP order of owners. The hand and
    /// library cases are offered right after the move rather than as a true replacement, which only differs when
    /// something looks at that hand or library in between.
    /// </summary>
    private async Task OfferCommanderReturnsAsync()
    {
        if (_commandersToOffer.Count == 0) return;
        var pending = _commandersToOffer.Distinct().ToList();
        _commandersToOffer.Clear();
        var order = State.ApnapOrder().ToList();
        foreach (var id in pending.OrderBy(id => order.IndexOf(State.GetCard(id).Owner)))
        {
            var card = State.GetCard(id);
            if (card.Zone is not (Zone.Graveyard or Zone.Exile or Zone.Hand or Zone.Library)) continue;
            var owner = card.Owner;
            if (State.GetPlayer(owner).HasLost) continue;
            var request = new YesNoRequest($"Move {card.Name} from your {card.Zone.ToString().ToLowerInvariant()} to the command zone?", id);
            if (!await ControllerOf(owner).ChooseYesNoAsync(ViewFor(owner), request)) continue;
            MoveCard(id, Zone.Command);
            Emit(new CommanderReturned(id, owner));
        }
    }

    private void RecordCommanderDamage(Card source, PlayerId target, int amount)
    {
        if (Config.Commander is null || !source.IsCommander || amount <= 0) return;
        var taken = State.GetPlayer(target).CommanderDamageTaken;
        taken[source.Id] = taken.GetValueOrDefault(source.Id) + amount;
    }

    /// <summary>A player dealt 21 or more combat damage by the same commander loses (704.6c).</summary>
    private bool CommanderDamageLoss(Player player) =>
        Config.Commander is { } rules && player.CommanderDamageTaken.Values.Any(d => d >= rules.CommanderDamageToLose);
}
