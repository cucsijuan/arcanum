// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>What the board reads to warn a player before they target a warded permanent (rule 702.21).</summary>
public class WardViewTests
{
    [Fact]
    public void APermanentsViewListsItsWardCosts()
    {
        var s = new Scenario();
        var warded = s.Add(P1, Creature("Guarded Beast", 3, 3) with { WardMana = ManaCost.Parse("{2}") });
        var lifeWard = s.Add(P1, Creature("Costly Beast", 3, 3) with { WardLife = 3 });
        var plain = s.Add(P1, Creature("Plain Beast", 3, 3));
        Assert.Equal(new[] { "{2}" }, s.Game.ViewFor(P0).FindCard(warded)!.Wards);
        Assert.Equal(new[] { "3 life" }, s.Game.ViewFor(P0).FindCard(lifeWard)!.Wards);
        Assert.Empty(s.Game.ViewFor(P0).FindCard(plain)!.Wards);
    }
}
