// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>The deck condition of a companion ability ("Companion — [condition]", rule 702.139a).</summary>
public enum CompanionCondition
{
    /// <summary>"No card in your starting deck has more than one of the same mana symbol in its mana cost."</summary>
    NoRepeatedManaSymbols,
}

/// <summary>Which lands tapped for mana give the bonus, and what it adds (<see cref="CardDefinition.LandManaBonus"/>).</summary>
public sealed record LandManaBonus(bool EnchantedLand, LandManaBonusKind Kind);

public enum LandManaBonusKind
{
    /// <summary>One mana of any color.</summary>
    AnyColor,
    /// <summary>One mana of the permanent's chosen color.</summary>
    ChosenColor,
    /// <summary>One mana of any type the land produced (any basic land, any player).</summary>
    SameTypeFromBasic,
}

public static class CompanionConditions
{
    /// <summary>Whether a starting deck (its cards, the commanders included) fulfills the condition.</summary>
    public static bool FulfilledBy(this CompanionCondition condition, IEnumerable<CardDefinition> startingDeck) => condition switch
    {
        CompanionCondition.NoRepeatedManaSymbols => startingDeck.All(c => !RepeatsASymbol(CardManaCost(c))),
        _ => false,
    };

    /// <summary>
    /// A card's mana cost as a card in a deck: a split card's halves combined (rule 709.4); a double-faced card's front face, an
    /// adventurer card's main card (rules 712.8a, 715.4).
    /// </summary>
    private static IReadOnlyList<ManaCost> CardManaCost(CardDefinition card) =>
        card.SplitHalves is { Count: > 0 } halves ? halves.Select(h => h.ManaCost).ToList() : new[] { card.ManaCost };

    /// <summary>Whether the costs together hold one mana symbol more than once ({R}{R}, {X}{X}, {G/U}{G/U}); a generic {3} is one symbol.</summary>
    private static bool RepeatsASymbol(IReadOnlyList<ManaCost> costs)
    {
        var symbols = new List<string>();
        foreach (var cost in costs)
        {
            if (cost.Generic > 0) symbols.Add($"{{{cost.Generic}}}");
            symbols.AddRange(Enumerable.Repeat("{X}", cost.XCount));
            symbols.AddRange(cost.Pips.Select(p => $"{{{p.ToSymbol()}}}"));
            symbols.AddRange(cost.Hybrid.Select(h => h.ToString()));
            symbols.AddRange(cost.Phyrexian.Select(p => $"{{{p.ToSymbol()}/P}}"));
            symbols.AddRange(cost.MonoHybrid.Select(p => $"{{2/{p.ToSymbol()}}}"));
        }
        return symbols.Count != symbols.Distinct().Count();
    }
}
