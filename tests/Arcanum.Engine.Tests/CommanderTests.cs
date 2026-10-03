// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Tests;

public class CommanderTests
{
    private static readonly PlayerId P0 = new(0), P1 = new(1);

    private static CardDefinition Legend(string name, int power, int toughness, string cost = "{2}{G}") => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Creature, Supertypes = Supertype.Legendary,
        Power = power, Toughness = toughness,
    };

    private static readonly CardDefinition Bolt = new()
    {
        Name = "Bolt", Types = CardType.Instant, ManaCost = ManaCost.Parse("{R}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new DealDamage(5, Subject.TargetAt(0)) } },
    };

    private static Game CommanderGame(TestController p0, TestController p1, CardDefinition commander, int lands = 8)
    {
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0, StartingLife = 40, Commander = new CommanderRules() }, new[]
        {
            new PlayerSetup("A", p0, Decks.Of((GenericCards.Forest, 30)), new[] { commander }),
            new PlayerSetup("B", p1, Decks.Of((GenericCards.Mountain, 30))),
        });
        for (int i = 0; i < lands; i++) game.SetupPermanent(P0, GenericCards.Forest);
        return game;
    }

    private static async Task RunUntilTurn(Game game, int turn)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == turn) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
    }

    private static Card CommanderOf(Game game) => game.State.Cards.Values.Single(c => c.IsCommander);

    [Fact]
    public async Task CommanderStartsInCommandZoneAndCanBeCast()
    {
        var p0 = new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = CommanderGame(p0, new TestController(), Legend("Leader", 3, 3));
        Assert.Equal(Zone.Command, CommanderOf(game).Zone);
        await RunUntilTurn(game, 2);
        Assert.Equal(Zone.Battlefield, CommanderOf(game).Zone);
        Assert.Equal(1, game.State.GetPlayer(P0).CommanderCasts[CommanderOf(game).Id]);
    }

    [Fact]
    public async Task CommanderReturnsToCommandZoneAndCostsTwoMoreEachCast()
    {
        // P1 kills the commander every time it lands; P0 recasts it with growing tax.
        var p0 = new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var p1 = new TestController
        {
            // One Bolt at a time, only once the commander has resolved.
            Act = (view, legal) => view.Stack.Count == 0
                ? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance
                : PassPriority.Instance,
            Targets = (_, r) => r.Legal.Select(l => l[0]).ToList(),
        };
        var game = CommanderGame(p0, p1, Legend("Leader", 3, 3, "{1}{G}"), lands: 8);
        for (int i = 0; i < 2; i++)
        {
            game.SetupPermanent(P1, GenericCards.Mountain);
            game.SetupInHand(P1, Bolt);
        }
        var costs = new List<int>();
        p0.Pay = (_, r) => { if (r.Source == CommanderOf(game).Id) costs.Add(r.Cost.ManaValue); return r.SuggestedTaps; };
        await RunUntilTurn(game, 2);

        Assert.Equal(2, game.Log.OfType<CommanderReturned>().Count());
        Assert.Equal(new[] { 2, 4 }, costs); // {1}{G}, then +{2}; a third cast (+{4}) is more than the 9 lands left
        Assert.Equal(Zone.Command, CommanderOf(game).Zone);
        Assert.Equal(2, game.State.GetPlayer(P0).CommanderCasts[CommanderOf(game).Id]);
    }

    [Fact]
    public async Task OwnerCanLeaveTheCommanderInTheGraveyard()
    {
        var p0 = new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>(), YesNo = (_, _) => false };
        var p1 = new TestController { Act = (view, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance };
        var game = CommanderGame(p0, p1, Legend("Leader", 3, 3, "{1}{G}"));
        game.SetupPermanent(P1, GenericCards.Mountain);
        game.SetupInHand(P1, Bolt);
        await RunUntilTurn(game, 2);
        Assert.Equal(Zone.Graveyard, CommanderOf(game).Zone);
        Assert.Empty(game.Log.OfType<CommanderReturned>());
    }

    [Fact]
    public async Task TwentyOneCommanderDamageLoses()
    {
        var p0 = new TestController();
        var p1 = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = CommanderGame(p0, p1, Legend("Titan", 11, 11, "{G}"));
        await RunUntilTurn(game, 6);

        var lost = Assert.Single(game.Log.OfType<PlayerLost>());
        Assert.Equal(P1, lost.Player);
        Assert.Equal("21 combat damage from a commander", lost.Reason);
        Assert.Equal(18, game.State.GetPlayer(P1).Life); // well above 0: it was the commander damage
    }

    [Fact]
    public async Task MultiplayerAttacksCanTargetDifferentOpponents()
    {
        var attacker = new TestController
        {
            Attack = (_, attackers, defenders) => attackers.Select((a, i) => new AttackDeclaration(a, defenders[i % defenders.Count])).ToList(),
        };
        IReadOnlyList<PlayerId>? seenDefenders = null;
        var spy = attacker.Attack;
        attacker.Attack = (v, a, d) => { seenDefenders ??= d; return spy(v, a, d); };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, new[]
        {
            new PlayerSetup("A", attacker, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("B", new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() }, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("C", new TestController { Attack = (_, _, _) => Array.Empty<AttackDeclaration>() }, Decks.Of((GenericCards.Forest, 20))),
        });
        game.SetupPermanent(P0, Scenario.Creature("Bear", 2, 2));
        game.SetupPermanent(P0, Scenario.Creature("Bear", 2, 2));
        await RunUntilTurn(game, 2);

        Assert.Equal(2, seenDefenders!.Count);
        Assert.Equal(18, game.State.GetPlayer(new PlayerId(1)).Life);
        Assert.Equal(18, game.State.GetPlayer(new PlayerId(2)).Life);
    }

    [Theory]
    [InlineData(1UL, 3)] [InlineData(2UL, 3)] [InlineData(3UL, 4)] [InlineData(4UL, 4)]
    public async Task MultiplayerBotCommanderGamesFinish(ulong seed, int players)
    {
        var bots = Enumerable.Range(0, players).Select(i => new BotController(new PlayerId(i))).ToList();
        var deck = Decks.Of((GenericCards.Forest, 18), (GenericCards.GladeCub, 20), (GenericCards.GreatWurm, 6),
            (Scenario.Creature("Flyer", 2, 2, Keyword.Flying) with { ManaCost = ManaCost.Parse("{1}{G}") }, 6));
        var game = new Game(new GameConfig { Seed = seed, StartingLife = 40, Commander = new CommanderRules() },
            bots.Select((b, i) => new PlayerSetup($"Bot {i}", b, deck, new[] { Legend($"Leader {i}", 4, 4) })).ToList());
        foreach (var b in bots) b.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        await game.RunWithTimeout(30);

        Assert.True(game.State.IsGameOver);
        Assert.Equal(players - 1, game.Log.OfType<PlayerLost>().Count());
        Assert.Contains(game.Log, e => e is SpellCast c && game.State.GetCard(c.Card).IsCommander);
    }
}
