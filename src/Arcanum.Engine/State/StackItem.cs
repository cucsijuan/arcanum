// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A chosen target plus the version of the object when it was chosen (a changed version is illegal).</summary>
public readonly record struct ChosenTarget(Target Target, int Version);

/// <summary>An object on the stack: a spell or an activated/triggered ability.</summary>
public abstract record StackItem(PlayerId Controller, IReadOnlyList<ChosenTarget> Targets)
{
    /// <summary>Identifies the object on the stack (abilities can be targeted by it).</summary>
    public int Id { get; init; }

    /// <summary>The value chosen for X.</summary>
    public int X { get; init; }

    /// <summary>The kicker cost was paid.</summary>
    public bool Kicked { get; init; }

    /// <summary>Permanents sacrificed to pay its costs (for "the sacrificed creature's toughness").</summary>
    public IReadOnlyList<CardId> SacrificedForCost { get; init; } = Array.Empty<CardId>();

    /// <summary>Cards discarded to pay its costs (for "if the discarded card was a creature card").</summary>
    public IReadOnlyList<CardId> DiscardedForCost { get; init; } = Array.Empty<CardId>();

    /// <summary>The card the spell is, or the source of the ability.</summary>
    public abstract CardId SourceCard { get; }
}

public sealed record SpellOnStack(CardId Card, PlayerId Controller, IReadOnlyList<ChosenTarget>? ChosenTargets = null)
    : StackItem(Controller, ChosenTargets ?? Array.Empty<ChosenTarget>())
{
    public override CardId SourceCard => Card;

    /// <summary>The spell's ability narrowed to the chosen modes (null: the card's own).</summary>
    public AbilityDefinition? Ability { get; init; }

    /// <summary>Cast with flashback: exiled instead of going anywhere else when it leaves the stack.</summary>
    public bool Flashback { get; init; }

    /// <summary>The opponent the caster promised the gift to.</summary>
    public PlayerId? GiftTo { get; init; }

    /// <summary>Times its multikicker cost was paid (rule 702.33c).</summary>
    public int KickCount { get; init; }

    /// <summary>Times its squad cost was paid (rule 702.157).</summary>
    public int SquadCount { get; init; }

    /// <summary>Cast for its dash cost (rule 702.109).</summary>
    public bool Dashed { get; init; }
}

public sealed record AbilityOnStack(CardId Source, AbilityDefinition Ability, PlayerId Controller, IReadOnlyList<ChosenTarget> ChosenTargets)
    : StackItem(Controller, ChosenTargets)
{
    public override CardId SourceCard => Source;

    /// <summary>For a triggered ability: what the trigger event was about.</summary>
    public TriggerInfo? Trigger { get; init; }

    /// <summary>
    /// The source's object version when the ability triggered or was activated: "return it" works only on that same
    /// object (a card that changed zones since then is a new object, rule 400.7).
    /// </summary>
    public int? SourceVersion { get; init; }
}

/// <summary>What a trigger event was about: an object (with its version then), a player and an amount.</summary>
public sealed record TriggerInfo(CardId? Subject = null, int SubjectVersion = 0, PlayerId? Player = null, int Amount = 0)
{
    /// <summary>For counters put on a permanent: each kind put on it in the event ("those kinds of counters").</summary>
    public List<Abilities.CounterKind>? CounterKinds { get; init; }

    /// <summary>For "whenever players finish voting": every vote cast, in order (voter, choice).</summary>
    public IReadOnlyList<(PlayerId Voter, string Choice)>? Votes { get; init; }
}
