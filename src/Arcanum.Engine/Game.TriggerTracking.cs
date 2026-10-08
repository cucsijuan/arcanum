// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// Triggers about renown, blocks and targeting, and what the game remembers of a turn for them: damage dealt by each object and
/// spells cast last turn.
/// </summary>
public sealed partial class Game
{
    /// <summary>
    /// A permanent went from not renowned to renowned on the battlefield (rule 702.112b), whatever made it: "when this creature
    /// becomes renowned" and "whenever a creature you control becomes renowned" trigger, about that creature.
    /// </summary>
    private void QueueBecameRenowned(Card creature)
    {
        Queue(creature.Id, TriggerEvent.BecomesRenowned, creature.Controller, new TriggerInfo(creature.Id, creature.Version, creature.Controller));
        QueueObservers(TriggerEvent.CreatureBecomesRenowned, creature, creature.Controller);
    }

    /// <summary>"Whenever a creature you control becomes blocked": once for each attacker as it becomes blocked (rule 509.3c).</summary>
    private void QueueCreatureBecameBlocked(Card attacker) =>
        QueueObservers(TriggerEvent.CreatureBecomesBlocked, attacker, attacker.Controller);

    /// <summary>
    /// A permanent became the target of <paramref name="item"/> (put on the stack, or a target changed to it): watchers of "a spell or
    /// ability an opponent controls" see it when that spell or ability's controller is an opponent of theirs; watchers of "a spell or
    /// ability you control" when they control it (multiplayer: who controls the permanent doesn't decide either).
    /// </summary>
    private void QueueTargetedByController(StackItem item, Card aimed)
    {
        foreach (var (observer, abilities) in Observers())
            foreach (var ability in abilities)
            {
                bool watching = ability.Trigger switch
                {
                    TriggerEvent.PermanentBecomesTargetOfOpponent => State.OpponentsOf(observer.Controller).Contains(item.Controller),
                    TriggerEvent.BecomesTargetOfYours => item.Controller == observer.Controller,
                    _ => false,
                };
                if (watching && Matches(ability.Filter ?? DefaultFilter(ability.Trigger), aimed, aimed.Controller, observer, observer.Controller))
                    AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(aimed.Id, aimed.Version, aimed.Controller));
            }
    }

    /// <summary>
    /// The object a source of damage is: a permanent or spell as it is, a card that just left the battlefield as the permanent it was
    /// (its ability resolving with last known information, rule 608.2h).
    /// </summary>
    private static int DamageSourceVersion(Card source) =>
        source.Zone is not (Zone.Battlefield or Zone.Stack or Zone.Command) && source.LastKnownInfo is { } lki && lki.Version == source.Version - 1
            ? lki.Version : source.Version;

    /// <summary>Notes damage that was dealt (after prevention and replacement) for "has dealt damage this turn" and "dealt damage by that creature this turn".</summary>
    private void LogDamage(DamageDealt dealt)
    {
        State.DamageLog.RemoveAll(r => r.Turn != State.TurnNumber);
        var source = State.GetCard(dealt.Source);
        var target = dealt.TargetCard is { } t ? State.GetCard(t) : null;
        State.DamageLog.Add(new DamageRecord(State.TurnNumber, source.Id, DamageSourceVersion(source), target?.Id, target?.Version ?? 0, dealt.TargetPlayer, dealt.Amount));
    }

    /// <summary>Damage the object (card, version) has dealt this turn.</summary>
    private int DamageDealtThisTurnBy(CardId card, int version) =>
        State.DamageLog.Where(r => r.Turn == State.TurnNumber && r.Source == card && r.SourceVersion == version).Sum(r => r.Amount);

    /// <summary>The object the source is: the one the ability came from, or the source as it is (or last was on the battlefield).</summary>
    private static int SourceObjectVersion(Card source, int? abilityVersion) => abilityVersion ?? DamageSourceVersion(source);

    /// <summary>
    /// "A creature dealt damage by that creature this turn": <paramref name="obj"/> (as it last existed if it left the battlefield) was
    /// dealt damage this turn by the object the source remembers.
    /// </summary>
    private bool DamagedByRemembered(Card obj, Card? source, bool lastKnown)
    {
        if (source?.Remembered is not { } that) return false;
        int version = lastKnown && obj.Zone != Zone.Battlefield && obj.LastKnownInfo is { } lki ? lki.Version : obj.Version;
        return State.DamageLog.Any(r => r.Turn == State.TurnNumber && r.Source == that.Card && r.SourceVersion == that.Version
                                        && r.TargetCard == obj.Id && r.TargetVersion == version);
    }

    /// <summary>"If a player cast two or more spells last turn".</summary>
    private bool AnyPlayerCastLastTurn(int atLeast) => State.Players.Any(p => p.SpellsCastLastTurn >= atLeast);

    /// <summary>
    /// An effect of an ability that refers to its source ("exile her", "sacrifice this Aura") affects only the object the ability came
    /// from: not a new object the card became after it left the battlefield (rule 400.7).
    /// </summary>
    private static bool IsSourceObject(EffectContext ctx) => ctx.SourceVersion is not { } v || v == ctx.Source.Version;
}
