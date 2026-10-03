// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Tests;

public class ManaTests
{
    [Theory]
    [InlineData("{2}{G}{G}", 2, 2, 4)]
    [InlineData("{W}", 0, 1, 1)]
    [InlineData("{0}", 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    [InlineData("{10}{C}", 10, 1, 11)]
    public void ParsesManaCosts(string text, int generic, int pips, int manaValue)
    {
        var cost = ManaCost.Parse(text);
        Assert.Equal(generic, cost.Generic);
        Assert.Equal(pips, cost.Pips.Count);
        Assert.Equal(manaValue, cost.ManaValue);
    }

    [Fact]
    public void RoundTripsToString() => Assert.Equal("{3}{R}{R}", ManaCost.Parse("{3}{R}{R}").ToString());

    [Theory]
    [InlineData("{X}")]
    [InlineData("{G/W}")]
    [InlineData("2G")]
    public void RejectsUnsupportedSymbols(string text) => Assert.Throws<FormatException>(() => ManaCost.Parse(text));

    private static GameState StateWithLands(params CardDefinition[] lands)
    {
        var player = new Player(new PlayerId(0), "P", 20);
        var state = new GameState(new[] { player, new Player(new PlayerId(1), "Q", 20) });
        int id = 1;
        foreach (var def in lands)
        {
            var card = new Card(new CardId(id++), def, player.Id) { Zone = Zone.Battlefield };
            state.Cards.Add(card.Id, card);
            state.Battlefield.Add(card.Id);
        }
        return state;
    }

    [Fact]
    public void PaysColoredPipsWithMatchingSources()
    {
        var state = StateWithLands(CoreCards.Mountain, CoreCards.Forest, CoreCards.Forest);
        var plan = ManaPayment.FindPlan(state, new PlayerId(0), ManaCost.Parse("{1}{G}{G}"));
        Assert.NotNull(plan);
        Assert.Equal(3, plan!.Taps.Count);
        Assert.Equal(2, plan.Taps.Count(t => t.Type == ManaType.Green));
    }

    [Fact]
    public void FailsWhenColorsAreMissing()
    {
        var state = StateWithLands(CoreCards.Mountain, CoreCards.Mountain, CoreCards.Forest);
        Assert.Null(ManaPayment.FindPlan(state, new PlayerId(0), ManaCost.Parse("{G}{G}")));
    }

    [Fact]
    public void IgnoresTappedSources()
    {
        var state = StateWithLands(CoreCards.Forest, CoreCards.Forest);
        state.GetCard(new CardId(1)).Tapped = true;
        Assert.Null(ManaPayment.FindPlan(state, new PlayerId(0), ManaCost.Parse("{1}{G}")));
    }

    [Fact]
    public void UsesFloatingManaFirst()
    {
        var state = StateWithLands(CoreCards.Forest);
        state.GetPlayer(new PlayerId(0)).ManaPool.Add(ManaType.Red);
        var plan = ManaPayment.FindPlan(state, new PlayerId(0), ManaCost.Parse("{1}{G}"));
        Assert.NotNull(plan);
        Assert.Equal(new[] { ManaType.Red }, plan!.FromPool);
        Assert.Single(plan.Taps);
    }
}
