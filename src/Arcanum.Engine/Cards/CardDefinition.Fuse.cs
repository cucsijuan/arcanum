// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;

namespace Arcanum.Engine.Cards;

/// <summary>
/// Fuse (rule 702.102): a split card with fuse cast from its owner's hand may be cast with both halves. The fused split spell
/// has both halves' characteristics combined and one mana cost, the total of both (702.102c-d, 709.4d); as it resolves, its
/// controller follows the left half's instructions and then the right half's (702.102e).
/// </summary>
public sealed partial record CardDefinition
{
    /// <summary>The <see cref="Card.CastHalf"/> value of a split card cast with both halves (fused).</summary>
    public const int FusedHalves = 2;

    /// <summary>Fuse: from its owner's hand this split card may be cast with both halves.</summary>
    public bool Fuse { get; init; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardDefinition, CardDefinition> Fused = new();

    /// <summary>This split card as a fused split spell: both halves combined, the left half's targets and instructions, then the right half's.</summary>
    public CardDefinition AsFused() => Fused.GetValue(this, _ =>
    {
        var halves = SplitHalves!;
        var left = halves[0].Spell ?? new SpellAbility();
        var right = halves[1].Spell ?? new SpellAbility();
        var spell = new SpellAbility
        {
            Targets = left.Targets.Concat(right.Targets).ToList(),
            Effects = new Effect[] { new ModeEffects(0, left.Effects), new ModeEffects(left.Targets.Count, right.Effects) },
            Text = $"{left.Text}\n{right.Text}",
            ExileAfterResolving = left.ExileAfterResolving || right.ExileAfterResolving,
        };
        return CombineDoors(halves) with { Spell = spell };
    });

    /// <summary>A fused spell needs halves whose spells it can put side by side: neither modal (each half's targets are fixed in number).</summary>
    public static bool CanFuse(IReadOnlyList<CardDefinition> halves) =>
        halves.Count == 2 && halves.All(h => h.Spell is null || (h.Spell.Modes is null && h.Spell.Targets.All(t => t.RepeatFrom is null)));
}
