// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>The monarch (rule 724): a designation a player can have, with two inherent triggered abilities.</summary>
public sealed partial class Game
{
    /// <summary>"At the beginning of the monarch's end step, that player draws a card." (rule 724.2)</summary>
    private static readonly TriggeredAbility MonarchDraw = new()
    {
        Trigger = TriggerEvent.MonarchEndStep,
        Effects = new Effect[] { new DrawCards(1, Subject.You) },
        Text = "At the beginning of the monarch's end step, that player draws a card.",
    };

    /// <summary>"Whenever a creature deals combat damage to the monarch, its controller becomes the monarch." (rule 724.2)</summary>
    private static readonly TriggeredAbility MonarchTaken = new()
    {
        Trigger = TriggerEvent.DealsCombatDamageToPlayer,
        Effects = new Effect[] { new BecomeMonarch(Subject.Triggered) },
        Text = "Whenever a creature deals combat damage to the monarch, its controller becomes the monarch.",
    };

    /// <summary>
    /// The object shown as the source of the monarch's inherent abilities. They have no source (rule 724.2); this object
    /// only names them on the stack and in the log, and is controlled by the monarch.
    /// </summary>
    private CardId MonarchSource()
    {
        if (State.MonarchDesignation is not { } id)
        {
            id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
            var designation = new Card(id, new CardDefinition
            {
                Name = "The Monarch", IsEmblem = true, IsToken = true,
                OracleText = $"{MonarchDraw.Text}\n{MonarchTaken.Text}",
            }, State.Monarch ?? State.ActivePlayer) { Zone = Zone.Command };
            State.Cards.Add(id, designation);
            State.MonarchDesignation = id;
        }
        if (State.Monarch is { } monarch) State.GetCard(id).Controller = monarch;
        return id;
    }

    /// <summary>A player becomes the monarch; cards exiled until an opponent of their exiler does come back (rule 724.3).</summary>
    private void BecomeMonarch(PlayerId player)
    {
        if (State.GetPlayer(player).HasLost) return;
        State.Monarch = player;
        MonarchSource();
        Emit(new MonarchChanged(player));
        var returning = State.ExiledUntilOpponentIsMonarch.Where(e => e.Controller != player).ToList();
        ReturnFromMonarchExile(returning);
    }

    private void ReturnFromMonarchExile(List<(CardId Card, int Version, PlayerId Controller)> entries)
    {
        if (entries.Count == 0) return;
        foreach (var entry in entries) State.ExiledUntilOpponentIsMonarch.Remove(entry);
        BeginEnteringTogether();
        foreach (var (id, version, _) in entries)
            if (State.GetCard(id) is { Zone: Zone.Exile } card && card.Version == version)
                MoveCard(id, Zone.Battlefield, controller: card.Owner);
        EndEnteringTogether();
    }

    /// <summary>
    /// A player left the game (rule 800.4a): if they were the monarch, the active player becomes the monarch, or the next
    /// player in turn order if the active player is leaving too (rule 724.4). Their "until an opponent becomes the monarch"
    /// exiles end with them.
    /// </summary>
    private void NoteMonarchLeaving(IReadOnlyCollection<PlayerId> leaving)
    {
        ReturnFromMonarchExile(State.ExiledUntilOpponentIsMonarch.Where(e => leaving.Contains(e.Controller)).ToList());
        if (State.Monarch is not { } monarch || !leaving.Contains(monarch)) return;
        State.Monarch = null;
        var next = State.ActivePlayer;
        for (int i = 0; i < State.Players.Count && (leaving.Contains(next) || State.GetPlayer(next).HasLost); i++)
            next = State.Players[(next.Value + 1) % State.Players.Count].Id;
        if (!leaving.Contains(next) && !State.GetPlayer(next).HasLost) BecomeMonarch(next);
    }

    /// <summary>Monarch triggers at the beginning of an end step: the inherent draw and "at the beginning of the monarch's end step" abilities.</summary>
    private void QueueMonarchEndStep(PlayerId active)
    {
        if (State.Monarch != active) return;
        _pendingTriggers.Add(new PendingTrigger(MonarchSource(), MonarchDraw, active));
        foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
            Queue(card.Id, TriggerEvent.MonarchEndStep, card.Controller, new TriggerInfo(Player: active));
    }

    /// <summary>A creature dealt combat damage to the monarch: its controller will become the monarch.</summary>
    private void QueueMonarchTaken(Card creature, PlayerId hurt)
    {
        if (State.Monarch != hurt) return;
        _pendingTriggers.Add(new PendingTrigger(MonarchSource(), MonarchTaken, hurt, new TriggerInfo(creature.Id, creature.Version, creature.Controller)));
    }

    /// <summary>
    /// Regeneration (701.19): if the permanent has a regeneration shield, the destruction is replaced: it's tapped, all damage is
    /// removed from it and it's removed from combat. Returns true if that happened.
    /// </summary>
    private bool Regenerated(Card card, bool canBeRegenerated = true)
    {
        if (!canBeRegenerated || card.RegenerationShields <= 0 || card.Zone != Zone.Battlefield) return false;
        card.RegenerationShields--;
        card.Damage = 0;
        card.DamagedByDeathtouch = false;
        State.Combat?.Remove(card.Id);
        if (!card.Tapped)
        {
            card.Tapped = true;
            Emit(new PermanentTapped(card.Id));
        }
        Emit(new ChoiceMade(card.Id, "regenerated"));
        return true;
    }

    /// <summary>Whether <paramref name="attacker"/> may attack <paramref name="defender"/> this combat (restrictions only).</summary>
    private bool AttackForbidden(Card attacker, PlayerId defender) =>
        State.CantAttackThisCombat.Any(r => r.Attacker == attacker.Controller && r.Protected == defender && r.Turn == State.TurnNumber && (r.Combat == State.CombatsThisTurn || r.Combat == -1))
        || attacker.CantAttackPlayers.Contains(defender) || (attacker.CantAttackOwner && attacker.Owner == defender);
}
