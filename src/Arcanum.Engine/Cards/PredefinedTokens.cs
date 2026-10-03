// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Cards;

/// <summary>Tokens whose characteristics the rules define by name (rule 111.10).</summary>
public static class PredefinedTokens
{
    /// <summary>"A colorless Treasure artifact token with '{T}, Sacrifice this token: Add one mana of any color.'"</summary>
    public static readonly CardDefinition Treasure = new()
    {
        Name = "Treasure",
        Types = CardType.Artifact,
        Subtypes = new[] { "Treasure" },
        TapForMana = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green },
        SacrificeForMana = true,
        OracleText = "{T}, Sacrifice this token: Add one mana of any color.",
        IsToken = true,
    };

    /// <summary>"A colorless Food artifact token with '{2}, {T}, Sacrifice this token: You gain 3 life.'"</summary>
    public static readonly CardDefinition Food = new()
    {
        Name = "Food",
        Types = CardType.Artifact,
        Subtypes = new[] { "Food" },
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility
            {
                Cost = new AbilityCost(ManaCost.Parse("{2}"), Tap: true, SacrificeSelf: true),
                Effects = new Effect[] { new GainLife(3, Subject.You) },
                Text = "{2}, {T}, Sacrifice this token: You gain 3 life.",
            },
        },
        OracleText = "{2}, {T}, Sacrifice this token: You gain 3 life.",
        IsToken = true,
    };

    /// <summary>"A colorless Clue artifact token with '{2}, Sacrifice this token: Draw a card.'"</summary>
    public static readonly CardDefinition Clue = new()
    {
        Name = "Clue",
        Types = CardType.Artifact,
        Subtypes = new[] { "Clue" },
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility
            {
                Cost = new AbilityCost(ManaCost.Parse("{2}"), SacrificeSelf: true),
                Effects = new Effect[] { new DrawCards(1, Subject.You) },
                Text = "{2}, Sacrifice this token: Draw a card.",
            },
        },
        OracleText = "{2}, Sacrifice this token: Draw a card.",
        IsToken = true,
    };

    public static CardDefinition? ByName(string name) => name.ToLowerInvariant() switch
    {
        "treasure" => Treasure,
        "food" => Food,
        "clue" => Clue,
        _ => null,
    };
}
