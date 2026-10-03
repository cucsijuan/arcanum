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

    /// <summary>Its mana ability also sacrifices it: "{T}, Sacrifice this artifact: Add one mana of any color."</summary>
    public bool SacrificeForMana { get; init; }

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

    /// <summary>"This creature enters with X +1/+1 counters on it" (X as chosen when it was cast).</summary>
    public bool EntersWithXCounters { get; init; }

    /// <summary>Colors for objects without a mana cost to derive them from (tokens). Letters W, U, B, R, G.</summary>
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Identifies the exact picture of this object in the content module's image source (e.g. a token's printing),
    /// for objects whose name alone is ambiguous. Null: look the image up by name.
    /// </summary>
    public string? ImageKey { get; init; }

    /// <summary>"This spell can't be countered."</summary>
    public bool CantBeCountered { get; init; }

    /// <summary>"You have hexproof." (while this permanent is on the battlefield)</summary>
    public bool GivesControllerHexproof { get; init; }

    /// <summary>"Hexproof from [color]": colors (W, U, B, R, G) of opponents' sources that can't target it.</summary>
    public IReadOnlyList<string> HexproofFromColors { get; init; } = Array.Empty<string>();

    /// <summary>"Players can't gain life." (while this permanent is on the battlefield)</summary>
    public bool PlayersCantGainLife { get; init; }

    /// <summary>Kicker cost: an optional additional cost paid as the spell is cast (rule 702.33).</summary>
    public ManaCost? Kicker { get; init; }

    /// <summary>Flashback cost: the card may be cast from the graveyard for it, then exiled (rule 702.34).</summary>
    public ManaCost? Flashback { get; init; }

    /// <summary>Ward: opponents' spells and abilities that target it are countered unless they pay this (rule 702.21).</summary>
    public ManaCost? WardMana { get; init; }

    /// <summary>Ward life payment ("Ward—Pay 3 life"); combined with <see cref="WardMana"/> when both are printed.</summary>
    public int WardLife { get; init; }

    /// <summary>Costs paid in addition to the mana cost ("As an additional cost to cast this spell, discard a card").</summary>
    public ExtraCost? AdditionalCost { get; init; }

    /// <summary>"This spell costs {N} less to cast if [condition]" — or for each matching object, see <see cref="CostReduction"/>.</summary>
    public CostReduction? SelfCostReduction { get; init; }

    /// <summary>"This creature attacks each combat if able."</summary>
    public bool AttacksEachCombat { get; init; }

    /// <summary>"This creature doesn't untap during your untap step."</summary>
    public bool DoesntUntap { get; init; }

    /// <summary>Marks a token definition (tokens cease to exist outside the battlefield, rule 111.7).</summary>
    public bool IsToken { get; init; }

    public bool Is(CardType type) => (Types & type) != 0;

    private IReadOnlyList<string>? _colorList;

    /// <summary>Colors: the explicit <see cref="Colors"/>, otherwise those of the mana cost's colored symbols.</summary>
    public IReadOnlyList<string> ColorList => _colorList ??= Colors.Count > 0 ? Colors : ManaCost.Colors();

    private IReadOnlySet<Keyword>? _parsedKeywords;

    /// <summary>Engine-supported keywords among <see cref="Keywords"/>.</summary>
    public IReadOnlySet<Keyword> KeywordAbilities => _parsedKeywords ??= Cards.Keywords.ParseAll(Keywords);
}
