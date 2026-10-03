// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>
/// Immutable characteristics of a card as printed (oracle). Shared by every instance of the card.
/// </summary>
public sealed record CardDefinition
{
    public required string Name { get; init; }

    /// <summary>Stable identity of the card's rules text across printings, used to find its ability script.</summary>
    public string? OracleId { get; init; }
    public ManaCost ManaCost { get; init; } = ManaCost.Zero;
    public CardType Types { get; init; }
    public Supertype Supertypes { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public string OracleText { get; init; } = "";

    /// <summary>Keyword abilities printed on the card (e.g. "Flying"), as listed by the card data source.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Mana this permanent can add with an intrinsic "{T}: Add one mana of these types" ability.
    /// Placeholder for basic land mana abilities until the ability system lands (M4).
    /// </summary>
    public IReadOnlyList<ManaType> TapForMana { get; init; } = Array.Empty<ManaType>();

    public bool Is(CardType type) => (Types & type) != 0;
}
