// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

public sealed partial record CardDefinition
{
    /// <summary>
    /// Bestow [cost] (rule 702.103a): "As you cast this spell, you may choose to cast it bestowed. If you do, you pay [cost] rather than its mana
    /// cost." Null: no bestow.
    /// </summary>
    public Mana.ManaCost? Bestow { get; init; }

    /// <summary>The card as a bestowed Aura, kept per definition object (not in a field: "with" would copy it).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardDefinition, CardDefinition> Bestowed = new();

    /// <summary>
    /// This card cast bestowed (rule 702.103b): it becomes an Aura enchantment and gains enchant creature. It isn't a creature (its creature
    /// type and creature subtypes go with it, rule 205.1b), so its power and toughness don't apply.
    /// </summary>
    public CardDefinition AsBestowed() => Bestowed.GetValue(this, _ => this with
    {
        Types = CardType.Enchantment,
        Subtypes = new[] { "Aura" },
        EnchantTarget = new Arcanum.Engine.Abilities.TargetSpec(Arcanum.Engine.Abilities.TargetKind.Creature, Arcanum.Engine.Abilities.ControllerFilter.Any),
    });
}
