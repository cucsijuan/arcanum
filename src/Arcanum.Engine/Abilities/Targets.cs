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
}

/// <summary>Restriction on who controls the target ("target creature you control", "target opponent"...).</summary>
public enum ControllerFilter { Any, You, Opponent }

/// <summary>One target requirement of a spell or ability.</summary>
public sealed record TargetSpec(TargetKind Kind, ControllerFilter Controller = ControllerFilter.Any)
{
    public string Describe()
    {
        if (Kind == TargetKind.Any) return "any target";
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
public readonly record struct Target(CardId? Card, PlayerId? Player)
{
    public static Target Of(CardId card) => new(card, null);
    public static Target Of(PlayerId player) => new(null, player);

    public override string ToString() => Card is { } c ? c.ToString() : Player!.Value.ToString();
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
}

public sealed record Subject(SubjectKind Kind, int Index = 0)
{
    public static readonly Subject You = new(SubjectKind.You);
    public static readonly Subject Self = new(SubjectKind.Self);
    public static readonly Subject EachOpponent = new(SubjectKind.EachOpponent);
    public static Subject TargetAt(int index) => new(SubjectKind.Target, index);
}
