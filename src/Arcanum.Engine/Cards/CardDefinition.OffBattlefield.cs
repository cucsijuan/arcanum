// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

/// <summary>"As long as this isn't on the battlefield, it's a P/T [subtype] creature in addition to its other types."</summary>
public sealed record CreatureOffBattlefield(int Power, int Toughness, string Subtype);

public sealed partial record CardDefinition
{
    /// <summary>What this card also is in every zone but the battlefield (a characteristic-defining static ability, rule 604.3).</summary>
    public CreatureOffBattlefield? CreatureOffBattlefield { get; init; }

    /// <summary>The card as it is off the battlefield, kept per definition object (not in a field: "with" would copy it).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardDefinition, CardDefinition> OffBattlefield = new();

    /// <summary>This card in a zone other than the battlefield: with <see cref="CreatureOffBattlefield"/>, also that creature.</summary>
    public CardDefinition AsOffBattlefield() => CreatureOffBattlefield is not { } creature ? this
        : OffBattlefield.GetValue(this, _ => this with
        {
            Types = Types | CardType.Creature,
            Subtypes = Subtypes.Contains(creature.Subtype, StringComparer.OrdinalIgnoreCase) ? Subtypes : Subtypes.Append(creature.Subtype).ToList(),
            Power = creature.Power,
            Toughness = creature.Toughness,
        });
}
