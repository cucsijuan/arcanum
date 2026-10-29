// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Decayed (702.147), flanking (702.25), battle cry (702.91), mentor (702.134), demonstrate (702.144) and suspend from the hand (702.62).</summary>
public sealed partial class Game
{
    private static readonly TriggeredAbility DecayedTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[] { new SacrificeSelfAtEndOfCombat() },
        Text = "Decayed: when this creature attacks, sacrifice it at end of combat.",
    };

    private static readonly TriggeredAbility FlankingTrigger = new()
    {
        Trigger = TriggerEvent.BecomesBlocked,
        Effects = new Effect[] { new PumpUntilEndOfTurn(-1, -1, Subject.Triggered) },
        Text = "Flanking: the blocking creature gets -1/-1 until end of turn.",
    };

    private static readonly TriggeredAbility BattleCryTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[]
        {
            new PumpUntilEndOfTurn(1, 0, Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any, Other: true, Attacking: true))),
        },
        Text = "Battle cry: each other attacking creature gets +1/+0 until end of turn.",
    };

    private static readonly TriggeredAbility MyriadTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[] { new Abilities.Myriad() },
        Text = "Myriad: for each opponent other than defending player, you may create a token copy of this creature tapped and attacking that player or a planeswalker they control. Exile the tokens at end of combat.",
    };

    private static readonly TriggeredAbility MentorTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Any, new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any, Attacking: true) { LesserPowerThanSource = true }) },
        Effects = new Effect[] { new AddCounters(1, Subject.TargetAt(0)) },
        Text = "Mentor: put a +1/+1 counter on target attacking creature with lesser power.",
    };

    /// <summary>The attack triggers of decayed, battle cry and mentor: one for each instance (rule 702.91b, 702.134b).</summary>
    private void QueueAttackKeywordTriggers(Card attacker, TriggerInfo about)
    {
        foreach (var (keyword, trigger) in new[] { (Keyword.Decayed, DecayedTrigger), (Keyword.BattleCry, BattleCryTrigger), (Keyword.Mentor, MentorTrigger) })
            if (attacker.Has(keyword)) AddPending(attacker.Id, trigger, attacker.Controller, about);
        // Myriad given by another object ("equipped creature has myriad"); a card's own myriad is its script's trigger.
        if (attacker.Has(Keyword.Myriad) && !attacker.Definition.Abilities.OfType<TriggeredAbility>().Any(a => a.Effects.OfType<Abilities.Myriad>().Any()))
            AddPending(attacker.Id, MyriadTrigger, attacker.Controller, about);
    }

    /// <summary>Flanking: an attacker with flanking becomes blocked by a creature without it (each such blocker triggers it, 702.25b).</summary>
    private void QueueFlanking(BlockerDeclared b)
    {
        var attacker = State.GetCard(b.Attacker);
        var blocker = State.GetCard(b.Blocker);
        if (attacker.Has(Keyword.Flanking) && !blocker.Has(Keyword.Flanking))
            AddPending(attacker.Id, FlankingTrigger, attacker.Controller, new TriggerInfo(blocker.Id, blocker.Version, blocker.Controller));
    }

    /// <summary>Demonstrate: the caster may copy the spell; if they do, an opponent they choose copies it too. Each may choose new targets.</summary>
    private async Task DemonstrateAsync(EffectContext ctx)
    {
        var spell = State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == ctx.Source.Id);
        if (spell is null || ctx.Source.Definition.CantBeCopied) return;
        if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                new YesNoRequest($"Demonstrate: copy {ctx.Source.Name}? (An opponent you choose also copies it.)", ctx.Source.Id))) return;
        await CopySpellAsync(spell, ctx.Controller);
        if (await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Demonstrate: choose an opponent to also copy it") is { } opponent)
        {
            Emit(new ChoiceMade(ctx.Source.Id, State.GetPlayer(opponent).Name));
            await CopySpellAsync(spell, opponent);
        }
    }

    /// <summary>The counters the object a trigger is about has, or had as it last existed if it left that zone.</summary>
    private IReadOnlyDictionary<CounterKind, int> TriggeredCounters(EffectContext ctx)
    {
        // A trigger about its own source ("when this creature dies"): the source as it last existed on the battlefield.
        if (ctx.Trigger is not { Subject: { } about } info)
            return SourceOnBattlefield(ctx) ? ctx.Source.Counters : ctx.Source.LastKnownInfo?.Counters ?? new Dictionary<CounterKind, int>();
        var card = State.GetCard(about);
        return card.Version == info.SubjectVersion && card.Zone == Zone.Battlefield ? card.Counters
            : (card.LastKnownOf(info.SubjectVersion) ?? card.LastKnownInfo)?.Counters ?? new Dictionary<CounterKind, int>();
    }

    /// <summary>Suspend from the hand: the card, now exiled, gets its time counters and is suspended.</summary>
    private void SuspendFromHand(SuspendFromHand s, EffectContext ctx)
    {
        var card = ctx.Source;
        if (card.Zone != Zone.Exile) return;
        card.Counters[CounterKind.Time] = card.CounterCount(CounterKind.Time) + s.TimeCounters;
        card.Suspended = true;
        Emit(new ChoiceMade(card.Id, $"{State.GetPlayer(card.Owner).Name} suspended {card.Name} with {s.TimeCounters} time counter{(s.TimeCounters == 1 ? "" : "s")}"));
    }
}
