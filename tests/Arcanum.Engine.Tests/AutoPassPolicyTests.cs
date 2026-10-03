// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Tests;

public class AutoPassPolicyTests
{
    private static readonly PlayerId Me = new(0), Opponent = new(1);
    private static readonly PlayerAction[] OnlyPass = { PassPriority.Instance };
    private static readonly PlayerAction[] PassOrMana = { PassPriority.Instance, new ActivateManaAbility(new CardId(1), Mana.ManaType.Green) };
    private static readonly PlayerAction[] CanCast = { PassPriority.Instance, new CastSpell(new CardId(2)) };

    private static GameView View(Step step, PlayerId active, int turn = 3, PlayerId? stackTop = null)
    {
        var stack = stackTop is { } controller
            ? new[] { new StackItemView(new CardView { Id = new CardId(9), Owner = controller, Controller = controller, Zone = Zone.Stack, IsHidden = false }, controller, null, Array.Empty<Abilities.Target>()) }
            : Array.Empty<StackItemView>();
        return new GameView
        {
            Viewer = Me, TurnNumber = turn, ActivePlayer = active, PriorityPlayer = Me, Step = step,
            Players = Array.Empty<PlayerView>(), Battlefield = Array.Empty<CardView>(), Stack = stack,
            Attacks = Array.Empty<AttackView>(), IsGameOver = false, Winner = null,
        };
    }

    [Fact]
    public void PassesWhenOnlyPassOrManaIsPossible()
    {
        var policy = new AutoPassPolicy();
        Assert.True(policy.ShouldAutoPass(View(Step.PrecombatMain, Me), OnlyPass));
        Assert.True(policy.ShouldAutoPass(View(Step.PrecombatMain, Me), PassOrMana));
    }

    [Fact]
    public void StopsOnDefaultMainPhasesOfOwnTurn()
    {
        var policy = new AutoPassPolicy();
        Assert.False(policy.ShouldAutoPass(View(Step.PrecombatMain, Me), CanCast));
        Assert.False(policy.ShouldAutoPass(View(Step.PostcombatMain, Me), CanCast));
        Assert.True(policy.ShouldAutoPass(View(Step.Upkeep, Me), CanCast));
        Assert.True(policy.ShouldAutoPass(View(Step.PrecombatMain, Opponent), CanCast));
    }

    [Fact]
    public void ToggledStopsApplyPerTurnOwner()
    {
        var policy = new AutoPassPolicy();
        policy.ToggleStop(ownTurn: false, Step.End);
        policy.ToggleStop(ownTurn: true, Step.PrecombatMain);
        Assert.False(policy.ShouldAutoPass(View(Step.End, Opponent), CanCast));
        Assert.True(policy.ShouldAutoPass(View(Step.End, Me), CanCast));
        Assert.True(policy.ShouldAutoPass(View(Step.PrecombatMain, Me), CanCast));
    }

    [Fact]
    public void StopsToRespondOnlyToOpponentsSpells()
    {
        var policy = new AutoPassPolicy();
        Assert.False(policy.ShouldAutoPass(View(Step.Upkeep, Opponent, stackTop: Opponent), CanCast));
        Assert.True(policy.ShouldAutoPass(View(Step.PrecombatMain, Me, stackTop: Me), CanCast));
    }

    [Fact]
    public void EndTurnSkipsUntilNextTurnUnlessOpponentActs()
    {
        var policy = new AutoPassPolicy();
        policy.PassTurn(3);
        Assert.True(policy.ShouldAutoPass(View(Step.PostcombatMain, Me, turn: 3), CanCast));
        Assert.Equal(3, policy.PassingTurn);

        Assert.False(policy.ShouldAutoPass(View(Step.End, Me, turn: 3, stackTop: Opponent), CanCast));
        Assert.Null(policy.PassingTurn);

        policy.PassTurn(3);
        Assert.False(policy.ShouldAutoPass(View(Step.PrecombatMain, Me, turn: 4), CanCast)); // next turn: normal stops again
        Assert.Null(policy.PassingTurn);
    }

    [Fact]
    public void FullControlStopsEverywhereButStillHonoursEndTurn()
    {
        var policy = new AutoPassPolicy();
        policy.SetFullControl(true);
        Assert.False(policy.ShouldAutoPass(View(Step.Upkeep, Opponent), OnlyPass));
        policy.PassTurn(3);
        Assert.True(policy.ShouldAutoPass(View(Step.Upkeep, Opponent), OnlyPass));
    }
}
