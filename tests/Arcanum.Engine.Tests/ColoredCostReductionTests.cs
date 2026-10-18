// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Tests;

/// <summary>"Costs {U} less" (rule 118.7c): only that color's symbols (then hybrid ones with it) are taken off, never the generic part.</summary>
public class ColoredCostReductionTests
{
    [Theory]
    [InlineData("{1}{U}{U}", 1, "{1}{U}")]
    [InlineData("{1}{U}{U}", 3, "{1}")]
    [InlineData("{2}{R}", 2, "{2}{R}")]
    [InlineData("{1}{U/R}", 1, "{1}")]
    public void ReducesOnlyThatColor(string cost, int amount, string expected) =>
        Assert.Equal(expected, ManaCost.Parse(cost).MinusColored(ManaType.Blue, amount).ToString());
}
