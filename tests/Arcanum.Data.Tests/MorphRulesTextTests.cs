// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;

namespace Arcanum.Data.Tests;

/// <summary>Morph and impending costs read from the keyword lines of a card's rules text (rules 702.37 and 702.176).</summary>
public class MorphRulesTextTests
{
    [Fact]
    public void MorphLineGivesTheMorphCost()
    {
        var (definition, _) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-v", Name = "Veiled Colossus", Layout = "normal", TypeLine = "Creature — Colossus", ManaCost = "{5}{R}{R}",
            Power = "6", Toughness = "6", OracleText = "Flying\nMorph {3}{R}{R}{R}", Keywords = new[] { "Flying", "Morph" },
        });
        Assert.Equal("{3}{R}{R}{R}", definition.Morph!.ToString());
    }

    [Fact]
    public void ImpendingLineGivesTheCostAndTheCount()
    {
        var (definition, _) = CardFactory.Create(new CardRecord
        {
            OracleId = "o-d", Name = "Dusk Colossus", Layout = "normal", TypeLine = "Creature — Colossus", ManaCost = "{4}{W}{W}",
            Power = "5", Toughness = "5", OracleText = "Impending 4—{2}{W}{W}", Keywords = new[] { "Impending" },
        });
        Assert.Equal("{2}{W}{W}", definition.Impending!.ToString());
        Assert.Equal(4, definition.ImpendingCount);
    }
}
