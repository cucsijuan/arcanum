// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
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
    /// Card data fills it from basic land types and simple "{T}: Add ..." rules text.
    /// </summary>
    public IReadOnlyList<ManaType> TapForMana { get; init; } = Array.Empty<ManaType>();

    /// <summary>What an instant or sorcery does (null for permanents and spells without effects).</summary>
    public SpellAbility? Spell { get; init; }

    /// <summary>Activated and triggered abilities of the permanent.</summary>
    public IReadOnlyList<AbilityDefinition> Abilities { get; init; } = Array.Empty<AbilityDefinition>();

    /// <summary>For an Aura: what it can enchant; casting it targets one such object (rule 303.4a).</summary>
    public TargetSpec? EnchantTarget { get; init; }

    /// <summary>Replacement effect "This permanent enters tapped" (rule 614.1c).</summary>
    public bool EntersTapped { get; init; }

    /// <summary>Replacement effect "This creature enters with N +1/+1 counters on it" (rule 614.1c).</summary>
    public int EntersWithCounters { get; init; }

    /// <summary>Colors for objects without a mana cost to derive them from (tokens). Letters W, U, B, R, G.</summary>
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Identifies the exact picture of this object in the content module's image source (e.g. a token's printing),
    /// for objects whose name alone is ambiguous. Null: look the image up by name.
    /// </summary>
    public string? ImageKey { get; init; }

    /// <summary>Marks a token definition (tokens cease to exist outside the battlefield, rule 111.7).</summary>
    public bool IsToken { get; init; }

    public bool Is(CardType type) => (Types & type) != 0;

    private IReadOnlySet<Keyword>? _parsedKeywords;

    /// <summary>Engine-supported keywords among <see cref="Keywords"/>.</summary>
    public IReadOnlySet<Keyword> KeywordAbilities => _parsedKeywords ??= Cards.Keywords.ParseAll(Keywords);
}
