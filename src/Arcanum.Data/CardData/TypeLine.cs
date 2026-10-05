// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>Parses a type line such as "Legendary Creature — Human Wizard".</summary>
public static class TypeLine
{
    private static readonly Dictionary<string, CardType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Land"] = CardType.Land, ["Creature"] = CardType.Creature, ["Artifact"] = CardType.Artifact,
        ["Enchantment"] = CardType.Enchantment, ["Planeswalker"] = CardType.Planeswalker, ["Instant"] = CardType.Instant,
        ["Sorcery"] = CardType.Sorcery, ["Battle"] = CardType.Battle, ["Kindred"] = CardType.Kindred, ["Tribal"] = CardType.Kindred,
    };

    private static readonly Dictionary<string, Supertype> Supertypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Basic"] = Supertype.Basic, ["Legendary"] = Supertype.Legendary, ["Snow"] = Supertype.Snow, ["World"] = Supertype.World,
    };

    public static (Supertype Supertypes, CardType Types, IReadOnlyList<string> Subtypes) Parse(string typeLine)
    {
        var parts = typeLine.Split('—', 2);
        var supertypes = Supertype.None;
        var types = CardType.None;
        foreach (var word in parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Types.TryGetValue(word, out var t)) types |= t;
            else if (Supertypes.TryGetValue(word, out var st)) supertypes |= st;
            // Other words ("Token", ...) don't change the engine's view of the card.
        }
        var subtypes = parts.Length > 1
            ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        return (supertypes, types, subtypes);
    }
}
