// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Tests;

public class ManaFlowTests
{
    private static readonly IReadOnlyList<Cards.CardDefinition> RedGreen =
        Decks.Of((CoreCards.Forest, 10), (CoreCards.Mountain, 10), (CoreCards.GrizzlyBears, 20));

    [Fact]
    public void ApplyReportsRemainingAndExcess()
    {
        var cost = ManaCost.Parse("{1}{G}");
        Assert.Equal(ManaCost.Parse("{G}"), ManaPayment.Apply(cost, new[] { ManaType.Red }).Remaining);
        Assert.Equal(0, ManaPayment.Apply(cost, new[] { ManaType.Red, ManaType.Green }).Remaining.ManaValue);
        Assert.Equal(1, ManaPayment.Apply(cost, new[] { ManaType.Green, ManaType.Green, ManaType.Green }).Excess);
        // A wrong color can only go to generic.
        var (remaining, excess) = ManaPayment.Apply(cost, new[] { ManaType.Red, ManaType.Red });
        Assert.Equal(ManaCost.Parse("{G}"), remaining);
        Assert.Equal(1, excess);
    }

    [Fact]
    public async Task FloatingManaIsSpentAndEmptiesBetweenSteps()
    {
        // P1 taps every land for mana in main 1 before doing anything else, then plays normally.
        bool floatedThisTurn = false;
        int poolAtStepStart = -1;
        var floater = new TestController();
        floater.Act = (view, legal) =>
        {
            var tap = legal.OfType<ActivateManaAbility>().FirstOrDefault();
            if (view.Step == Step.PrecombatMain && tap is not null && !floatedThisTurn) return tap;
            floatedThisTurn = true;
            return TestController.Greedy(view, legal);
        };
        var game = Decks.NewGame(21, (RedGreen, floater), (RedGreen, new TestController()));
        game.EventRaised += e =>
        {
            if (e is TurnBegan) floatedThisTurn = false;
            if (e is StepBegan) poolAtStepStart = Math.Max(poolAtStepStart, game.State.Players.Sum(p => p.ManaPool.Total));
        };
        await game.RunWithTimeout();

        Assert.Contains(game.Log, e => e is ManaAdded { Source: not null });
        Assert.Equal(0, poolAtStepStart); // rule 500.4: pools are always empty when a new step starts
        Assert.Contains(game.Log, e => e is SpellCast);
    }

    [Fact]
    public async Task CastingWithFloatingManaTapsNothingElse()
    {
        ManaPaymentRequest? seen = null;
        var p1 = new TestController();
        p1.Act = (view, legal) =>
        {
            // Float all mana first, then cast.
            var tap = legal.OfType<ActivateManaAbility>().FirstOrDefault();
            if (legal.OfType<CastSpell>().Any() && tap is not null) return tap;
            return TestController.Greedy(view, legal);
        };
        p1.Pay = (view, request) => { seen ??= request; return request.SuggestedTaps; };
        var game = Decks.NewGame(22, (RedGreen, p1), (RedGreen, new TestController()));
        await game.RunWithTimeout();

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.FromPool.Count);
        Assert.Empty(seen.SuggestedTaps);
        Assert.Equal(0, seen.RemainingAfterPool.ManaValue);
    }

    [Fact]
    public async Task PlayerCanChooseDifferentSources()
    {
        int customPayments = 0;
        var p1 = new TestController
        {
            // Prefer tapping the highest-id usable sources instead of the suggestion.
            Pay = (_, request) =>
            {
                var taps = new List<ManaTap>();
                var remaining = request.RemainingAfterPool;
                foreach (var source in request.Sources.OrderByDescending(s => s.Source.Value))
                {
                    if (remaining.ManaValue == 0) break;
                    var type = source.Types.FirstOrDefault(t => remaining.Pips.Contains(t), source.Types[0]);
                    var (next, excess) = ManaPayment.Apply(remaining, new[] { type });
                    if (excess > 0) continue;
                    taps.Add(new ManaTap(source.Source, type));
                    remaining = next;
                }
                customPayments++;
                return taps;
            },
        };
        var game = Decks.NewGame(23, (RedGreen, p1), (RedGreen, new TestController()));
        await game.RunWithTimeout();
        Assert.True(customPayments > 0);
        Assert.True(game.State.IsGameOver);
    }

    [Fact]
    public async Task OverpayingIsRejected()
    {
        var p1 = new TestController
        {
            Pay = (_, request) => request.Sources.Select(s => new ManaTap(s.Source, s.Types[0])).ToList(),
        };
        // Give P1 enough lands that "tap everything" overpays at some point.
        var game = Decks.NewGame(24, (Decks.Of((CoreCards.Forest, 30), (CoreCards.GrizzlyBears, 10)), p1), (RedGreen, new TestController()));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => game.RunWithTimeout());
    }

    [Fact]
    public async Task WrongColorIsRejected()
    {
        var p1 = new TestController
        {
            Pay = (_, request) => request.SuggestedTaps.Select(t => t with { Type = ManaType.Red }).ToList(),
        };
        var game = Decks.NewGame(25, (Decks.ForestBears, p1), (RedGreen, new TestController()));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => game.RunWithTimeout());
    }

    [Fact]
    public async Task CancellingPaymentKeepsCardInHandAndUntapped()
    {
        int cancels = 0;
        var p1 = new TestController();
        p1.Pay = (_, _) => { cancels++; return null; };
        // After a cancel the greedy policy would retry forever, so pass once something was cancelled this turn.
        int cancelsAtLastAct = 0;
        p1.Act = (view, legal) =>
        {
            if (cancels > cancelsAtLastAct) { cancelsAtLastAct = cancels; return PassPriority.Instance; }
            return TestController.Greedy(view, legal);
        };
        var game = Decks.NewGame(26, (Decks.ForestBears, p1), (Decks.ForestBears, new TestController()));
        await game.RunWithTimeout();

        Assert.True(cancels > 0);
        Assert.DoesNotContain(game.Log, e => e is SpellCast c && c.Player.Value == 0);
        Assert.DoesNotContain(game.Log, e => e is PermanentTapped t && game.State.GetCard(t.Card).Owner.Value == 0
                                             && game.State.GetCard(t.Card).Is(Cards.CardType.Land));
    }
}
