// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Persist (rule 702.79) and undying (rule 702.93), from the keywords the permanent had as it left the battlefield.</summary>
public sealed partial class Game
{
    private static readonly TriggeredAbility PersistTrigger = new()
    {
        Trigger = TriggerEvent.PutIntoGraveyard,
        Effects = new Effect[] { new ReturnWithCounter(CounterKind.MinusOneMinusOne) },
        Text = "Persist — When this permanent is put into a graveyard from the battlefield, if it had no -1/-1 counters on it, return it to the battlefield under its owner's control with a -1/-1 counter on it.",
    };

    private static readonly TriggeredAbility UndyingTrigger = new()
    {
        Trigger = TriggerEvent.PutIntoGraveyard,
        Effects = new Effect[] { new ReturnWithCounter(CounterKind.PlusOnePlusOne) },
        Text = "Undying — When this permanent is put into a graveyard from the battlefield, if it had no +1/+1 counters on it, return it to the battlefield under its owner's control with a +1/+1 counter on it.",
    };

    /// <summary>
    /// Each persist and undying instance the permanent had as it left triggers (each one separately), if it had no counter of that
    /// kind (the intervening "if" is about that past state, so it is checked only now). The ability is the graveyard card's.
    /// </summary>
    private void QueuePersistAndUndying(Card card, PlayerId controller)
    {
        if (card.LastKnownInfo is not { } was) return;
        void Add(TriggeredAbility ability) =>
            _pendingTriggers.Add(new PendingTrigger(card.Id, ability, controller) { SourceVersion = card.Version });
        if (was.Keywords.Contains(Keyword.Persist) && was.Counters.GetValueOrDefault(CounterKind.MinusOneMinusOne) == 0) Add(PersistTrigger);
        if (was.Keywords.Contains(Keyword.Undying) && was.Counters.GetValueOrDefault(CounterKind.PlusOnePlusOne) == 0) Add(UndyingTrigger);
    }
}
