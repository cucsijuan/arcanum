// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Tests;

public class GameFlowTests
{
    [Fact]
    public async Task ForestCubsGamePlaysToAWinner()
    {
        var game = Decks.NewGame(1, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();

        Assert.True(game.State.IsGameOver);
        Assert.NotNull(game.State.Winner);
        var loser = game.State.Players.Single(p => p.Id != game.State.Winner);
        Assert.True(loser.Life <= 0);
        Assert.Contains(game.Log, e => e is SpellResolved);
        Assert.Contains(game.Log, e => e is DamageDealt { TargetPlayer: not null });
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(99UL)]
    public async Task SameSeedProducesIdenticalGame(ulong seed)
    {
        async Task<string> Play()
        {
            var game = Decks.NewGame(seed, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, new TestController()));
            await game.RunWithTimeout();
            return string.Join("\n", game.Log);
        }
        Assert.Equal(await Play(), await Play());
    }

    [Fact]
    public async Task StartingPlayerSkipsFirstDrawInTwoPlayerGames()
    {
        var game = Decks.NewGame(5, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();

        var firstTurnSteps = game.Log.SkipWhile(e => e is not TurnBegan).Skip(1).TakeWhile(e => e is not TurnBegan)
            .OfType<StepBegan>().Select(s => s.Step).ToList();
        Assert.DoesNotContain(Step.Draw, firstTurnSteps);
        Assert.Contains(Step.PrecombatMain, firstTurnSteps);

        var secondTurnSteps = game.Log.SkipWhile(e => e is not TurnBegan).Skip(1).SkipWhile(e => e is not TurnBegan).Skip(1)
            .TakeWhile(e => e is not TurnBegan).OfType<StepBegan>().Select(s => s.Step);
        Assert.Contains(Step.Draw, secondTurnSteps);
    }

    [Fact]
    public async Task CreaturesCannotAttackTheTurnTheyEnter()
    {
        var game = Decks.NewGame(3, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, new TestController()));
        int turn = 0;
        var enteredOnTurn = new Dictionary<Core.CardId, int>();
        game.EventRaised += e =>
        {
            switch (e)
            {
                case TurnBegan t: turn = t.TurnNumber; break;
                case SpellResolved r: enteredOnTurn[r.Card] = turn; break;
                case AttackerDeclared a: Assert.True(enteredOnTurn[a.Attacker] < turn, "summoning-sick creature attacked"); break;
            }
        };
        await game.RunWithTimeout();
        Assert.Contains(game.Log, e => e is AttackerDeclared);
    }

    [Fact]
    public async Task DrawingFromEmptyLibraryLoses()
    {
        var tinyDeck = Decks.Of((GenericCards.Forest, 7));
        var game = Decks.NewGame(2, (tinyDeck, new TestController()), (tinyDeck, new TestController()));
        await game.RunWithTimeout();

        var lost = Assert.Single(game.Log.OfType<PlayerLost>());
        Assert.Equal("drew from an empty library", lost.Reason);
        // The player on the draw is the first one to draw.
        var starting = game.Log.OfType<GameStarted>().Single().StartingPlayer;
        Assert.NotEqual(starting, lost.Player);
        Assert.Equal(starting, game.State.Winner);
    }

    [Fact]
    public async Task LondonMulliganPutsCardsOnBottom()
    {
        var mulliganer = new TestController { Keep = (_, taken) => taken >= 2 };
        var game = Decks.NewGame(4, (Decks.ForestCubs, mulliganer), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();

        var kept = game.Log.OfType<HandKept>().ToDictionary(k => k.Player, k => k.HandSize);
        Assert.Equal(5, kept[new Core.PlayerId(0)]);
        Assert.Equal(7, kept[new Core.PlayerId(1)]);
        Assert.Equal(2, game.Log.OfType<MulliganTaken>().Count(m => m.Player.Value == 0));
    }

    [Fact]
    public async Task MultiplayerFirstMulliganIsFree()
    {
        var mulliganer = new TestController { Keep = (_, taken) => taken >= 1 };
        var game = new Game(new GameConfig { Seed = 8 }, new[]
        {
            new PlayerSetup("A", mulliganer, Decks.ForestCubs),
            new PlayerSetup("B", new TestController(), Decks.ForestCubs),
            new PlayerSetup("C", new TestController(), Decks.ForestCubs),
        });
        await game.RunWithTimeout();
        Assert.Equal(7, game.Log.OfType<HandKept>().Single(k => k.Player.Value == 0).HandSize);
    }

    [Fact]
    public async Task AMulliganDownToNoCardsIsOffered()
    {
        int offers = 0;
        var mulliganer = new TestController { Keep = (_, _) => { offers++; return false; } };
        var game = Decks.NewGame(4, (Decks.ForestCubs, mulliganer), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();

        Assert.Equal(7, offers); // after the seventh mulligan keeping already means no cards
        Assert.Equal(7, game.Log.OfType<MulliganTaken>().Count(m => m.Player.Value == 0));
        Assert.Equal(0, game.Log.OfType<HandKept>().Single(k => k.Player.Value == 0).HandSize);
    }

    [Fact]
    public async Task MulligansAreDeclaredInTurnOrderThenTakenTogether()
    {
        var game = default(Game)!;
        var calls = new List<string>();
        TestController Player(string name, int mulligansWanted) => new()
        {
            Keep = (_, taken) =>
            {
                // How many mulligans have been taken by everyone when this player is asked.
                calls.Add($"{name}{taken}:{game.Log.OfType<MulliganTaken>().Count()}");
                return taken >= mulligansWanted;
            },
        };
        game = new Game(new GameConfig { Seed = 5, StartingPlayer = new Core.PlayerId(0) }, new[]
        {
            new PlayerSetup("A", Player("A", 2), Decks.ForestCubs),
            new PlayerSetup("B", Player("B", 1), Decks.ForestCubs),
        });
        await game.RunWithTimeout();

        // A and B declare, both mulligan together; then A declares again, B keeps; then A alone mulligans once more.
        Assert.Equal(new[] { "A0:0", "B0:0", "A1:2", "B1:2", "A2:3" }, calls);
        var kept = game.Log.OfType<HandKept>().ToDictionary(k => k.Player.Value, k => k.HandSize);
        Assert.Equal(5, kept[0]);
        Assert.Equal(6, kept[1]);
    }

    [Fact]
    public async Task ThreePlayerGameEndsWithOneWinner()
    {
        var game = new Game(new GameConfig { Seed = 11 }, new[]
        {
            new PlayerSetup("A", new TestController(), Decks.ForestCubs),
            new PlayerSetup("B", new TestController(), Decks.ForestCubs),
            new PlayerSetup("C", new TestController(), Decks.ForestCubs),
        });
        await game.RunWithTimeout();

        Assert.True(game.State.IsGameOver);
        Assert.Equal(2, game.Log.OfType<PlayerLost>().Count());
        Assert.NotNull(game.State.Winner);
        // Nobody skips their first draw in multiplayer (rule 103.8c).
        var firstTurn = game.Log.SkipWhile(e => e is not TurnBegan).Skip(1).TakeWhile(e => e is not TurnBegan);
        Assert.Contains(firstTurn, e => e is StepBegan { Step: Step.Draw });
    }

    [Fact]
    public async Task BlockedBearsTradeAndUnblockedDamageIsSkipped()
    {
        var blocker = new TestController
        {
            Block = (_, blockers, attackers) =>
                blockers.Zip(attackers, (b, a) => new BlockDeclaration(b, a)).ToList(),
        };
        var game = Decks.NewGame(6, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, blocker));
        var blocks = new List<BlockerDeclared>();
        game.EventRaised += e => { if (e is BlockerDeclared b) blocks.Add(b); };
        await game.RunWithTimeout();

        Assert.NotEmpty(blocks);
        var died = game.Log.OfType<CreatureDied>().Select(d => d.Card).ToHashSet();
        var first = blocks[0];
        Assert.Contains(first.Blocker, died);
        Assert.Contains(first.Attacker, died);
    }

    [Fact]
    public async Task IllegalDecisionIsRejected()
    {
        var cheater = new TestController { Act = (_, _) => new PlayLand(new Core.CardId(9999)) };
        var game = Decks.NewGame(1, (Decks.ForestCubs, cheater), (Decks.ForestCubs, cheater));
        await Assert.ThrowsAsync<InvalidDecisionException>(() => game.RunWithTimeout());
    }

    [Fact]
    public async Task OpponentHandIsHiddenInViews()
    {
        Views.GameView? seen = null;
        var spy = new TestController
        {
            Act = (view, legal) => { seen ??= view; return TestController.Greedy(view, legal); },
        };
        var game = Decks.NewGame(1, (Decks.ForestCubs, spy), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();

        Assert.NotNull(seen);
        var opponent = seen!.Players.Single(p => p.Id != seen.Viewer);
        Assert.All(opponent.Hand, c => { Assert.True(c.IsHidden); Assert.Null(c.Name); });
        Assert.All(seen.Self.Hand, c => Assert.False(c.IsHidden));
    }

    [Fact]
    public async Task CallingTimeDrawsTheGameFiveTurnsLater()
    {
        // Nobody attacks, so nobody can lose in the additional turns.
        var passive = new TestController { Attack = (_, _, _) => new List<AttackDeclaration>() };
        var game = Decks.NewGame(3, (Decks.ForestCubs, passive), (Decks.ForestCubs, passive));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 3 }) game.CallTime(); };
        await game.RunWithTimeout();

        Assert.Equal<int?>(3, game.TimeCalledAt);
        var called = Assert.Single(game.Log.OfType<TimeCalled>());
        Assert.Equal(3, called.TurnNumber);
        Assert.Equal(5, called.AdditionalTurns);
        // Turn 3 is turn 0; turns 4 to 8 are the additional turns, and the game is a draw at the end of turn 8.
        Assert.Equal(Enumerable.Range(1, 8), game.Log.OfType<TurnBegan>().Select(t => t.TurnNumber));
        Assert.Equal(8, game.State.TurnNumber);
        Assert.True(game.State.IsGameOver);
        Assert.Null(game.State.Winner);
        Assert.Null(Assert.Single(game.Log.OfType<GameEnded>()).Winner);
        Assert.Empty(game.Log.OfType<PlayerLost>());
    }

    [Fact]
    public async Task CallingTimeTwiceKeepsTheFirstCall()
    {
        var passive = new TestController { Attack = (_, _, _) => new List<AttackDeclaration>() };
        var game = Decks.NewGame(3, (Decks.ForestCubs, passive), (Decks.ForestCubs, passive));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber is 3 or 5) game.CallTime(); };
        await game.RunWithTimeout();

        Assert.Equal<int?>(3, game.TimeCalledAt);
        var called = Assert.Single(game.Log.OfType<TimeCalled>());
        Assert.Equal(3, called.TurnNumber);
        Assert.Equal(8, game.State.TurnNumber);
        Assert.True(game.State.IsGameOver);
        Assert.Null(game.State.Winner);
    }

    [Fact]
    public async Task APlayerWhoLosesInTheAdditionalTurnsLosesNormally()
    {
        // Player A draws from an empty library on turn 3, which is one of the additional turns after time is called on turn 1.
        var tinyDeck = Decks.Of((GenericCards.Forest, 7));
        var game = new Game(new GameConfig { Seed = 2, StartingPlayer = new Core.PlayerId(0) }, new[]
        {
            new PlayerSetup("A", new TestController(), tinyDeck),
            new PlayerSetup("B", new TestController(), Decks.ForestCubs),
        });
        int turn = 0;
        int? lostOnTurn = null;
        game.EventRaised += e =>
        {
            switch (e)
            {
                case TurnBegan t:
                    turn = t.TurnNumber;
                    if (turn == 1) game.CallTime();
                    break;
                case PlayerLost: lostOnTurn = turn; break;
            }
        };
        await game.RunWithTimeout();

        Assert.Equal<int?>(3, lostOnTurn);
        Assert.Equal(new Core.PlayerId(0), Assert.Single(game.Log.OfType<PlayerLost>()).Player);
        Assert.Equal(new Core.PlayerId(1), game.State.Winner);
        Assert.Single(game.Log.OfType<TimeCalled>());
        Assert.NotNull(Assert.Single(game.Log.OfType<GameEnded>()).Winner);
    }
}

public class LosingTests
{
    [Fact]
    public async Task TwoPlayerLoserKeepsTheirBoardWhenTheGameEnds()
    {
        var game = Decks.NewGame(1, (Decks.ForestCubs, new TestController()), (Decks.ForestCubs, new TestController()));
        await game.RunWithTimeout();
        var loser = game.State.Players.Single(p => p.Id != game.State.Winner);
        Assert.Empty(loser.Exile);
        Assert.Contains(game.State.Battlefield, id => game.State.GetCard(id).Owner == loser.Id);
    }
}
