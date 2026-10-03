// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Abilities;

/// <summary>What kind of object or player a target must be.</summary>
public enum TargetKind
{
    /// <summary>"Any target": a creature, player or planeswalker (rule 115.4).</summary>
    Any,
    Creature,
    Player,
    Permanent,
    Artifact,
    Enchantment,
    Land,
    /// <summary>A spell on the stack.</summary>
    Spell,
    Planeswalker,
    /// <summary>"Target creature or planeswalker".</summary>
    CreatureOrPlaneswalker,
    /// <summary>"Target player or planeswalker".</summary>
    PlayerOrPlaneswalker,
    /// <summary>A spell or an activated/triggered ability on the stack.</summary>
    SpellOrAbility,
    /// <summary>A card in a graveyard ("target creature card from your graveyard": controller filter = owner).</summary>
    GraveyardCard,
}

/// <summary>Restriction on who controls the target ("target creature you control", "target opponent"...).</summary>
public enum ControllerFilter { Any, You, Opponent }

/// <summary>One target requirement of a spell or ability.</summary>
/// <param name="Filter">Further requirements ("artifact or enchantment", "with flying", "tapped"...); its controller is ignored.</param>
/// <param name="Optional">"Up to one target ...": the player may choose no object (<see cref="Target.None"/>).</param>
public sealed record TargetSpec(TargetKind Kind, ControllerFilter Controller = ControllerFilter.Any, ObjectFilter? Filter = null, bool Optional = false)
{
    /// <summary>"Any number of target ..." (only as the last requirement): the player picks as many different ones as they like (at least one unless optional).</summary>
    public bool AnyNumber { get; init; }

    /// <summary>The chosen object must be attached to the target chosen for requirement N ("Equipment attached to that creature").</summary>
    public int? AttachedToTarget { get; init; }

    /// <summary>For stack objects: only ones with a single target.</summary>
    public bool SingleTargetOnly { get; init; }

    /// <summary>The controller must be the player the trigger event was about ("that player controls").</summary>
    public bool ControlledByTriggeredPlayer { get; init; }

    /// <summary>Text shown when choosing; set by card scripts so the player sees the printed wording.</summary>
    public string? Text { get; init; }

    public string Describe()
    {
        if (Text is not null) return Text;
        if (Kind == TargetKind.GraveyardCard) return Controller == ControllerFilter.You ? "target card in your graveyard" : "target card in a graveyard";
        if (Kind == TargetKind.Any) return "any target";
        if (Kind == TargetKind.CreatureOrPlaneswalker) return Controller == ControllerFilter.Opponent ? "target creature or planeswalker an opponent controls" : "target creature or planeswalker";
        if (Kind == TargetKind.PlayerOrPlaneswalker) return "target player or planeswalker";
        if (Kind == TargetKind.Player && Controller == ControllerFilter.Opponent) return "target opponent";
        var noun = Kind.ToString().ToLowerInvariant();
        return Controller switch
        {
            ControllerFilter.You => $"target {noun} you control",
            ControllerFilter.Opponent => $"target {noun} an opponent controls",
            _ => $"target {noun}",
        };
    }
}

/// <summary>A chosen target: a card (permanent or spell) or a player.</summary>
public readonly record struct Target(CardId? Card, PlayerId? Player, int? StackObject = null)
{
    public static Target Of(CardId card) => new(card, null);
    public static Target Of(PlayerId player) => new(null, player);

    /// <summary>An ability on the stack (by its stack object id).</summary>
    public static Target OfStack(int stackObject) => new(null, null, stackObject);

    /// <summary>No object chosen for an optional ("up to one") target.</summary>
    public static readonly Target None = new(null, null);

    public bool IsNone => Card is null && Player is null && StackObject is null;

    public override string ToString() => Card is { } c ? c.ToString() : Player is { } p ? p.ToString() : StackObject is { } s ? $"stack#{s}" : "none";
}

/// <summary>Who or what an effect applies to.</summary>
public enum SubjectKind
{
    /// <summary>The Nth chosen target.</summary>
    Target,
    /// <summary>The controller of the spell or ability ("you").</summary>
    You,
    EachOpponent,
    EachPlayer,
    /// <summary>The source object itself ("this creature").</summary>
    Self,
    /// <summary>The controller of the Nth target ("its controller").</summary>
    TargetController,
    /// <summary>Every permanent matching <see cref="Subject.Filter"/> ("each creature", "creatures you control").</summary>
    Each,
    /// <summary>The owner of the Nth target.</summary>
    TargetOwner,
    /// <summary>The object the trigger event was about ("that creature", "that spell", "that card").</summary>
    Triggered,
    /// <summary>The player the trigger event was about ("that player").</summary>
    TriggeredPlayer,
    /// <summary>The permanent the source Aura/Equipment is attached to ("enchanted creature", "equipped creature").</summary>
    Attached,
    /// <summary>Every chosen target (from <see cref="Subject.Index"/> on): "each of those creatures".</summary>
    EachTarget,
    /// <summary>A specific player fixed when an ability was granted ("you" of the spell that granted it).</summary>
    FixedPlayer,
    /// <summary>In a granted ability: the controller of the spell or ability that granted it (bound when granted).</summary>
    Granter,
    /// <summary>In a granted ability: the permanent that grants it.</summary>
    GranterPermanent,
}

public sealed record Subject(SubjectKind Kind, int Index = 0, ObjectFilter? Filter = null)
{
    /// <summary>For <see cref="SubjectKind.FixedPlayer"/>.</summary>
    public PlayerId? Player { get; init; }

    public static Subject Each(ObjectFilter filter) => new(SubjectKind.Each, Filter: filter);
    public static readonly Subject Triggered = new(SubjectKind.Triggered);
    public static readonly Subject TriggeredPlayer = new(SubjectKind.TriggeredPlayer);
    public static readonly Subject Attached = new(SubjectKind.Attached);

    public static readonly Subject You = new(SubjectKind.You);
    public static readonly Subject Self = new(SubjectKind.Self);
    public static readonly Subject EachOpponent = new(SubjectKind.EachOpponent);
    public static Subject TargetAt(int index) => new(SubjectKind.Target, index);
}
