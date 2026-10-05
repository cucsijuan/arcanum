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

public class BotTests
{
    private static readonly CardDefinition Spark = new()
    {
        Name = "Spark", Types = CardType.Instant, ManaCost = ManaCost.Parse("{R}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) } },
    };
    private static readonly CardDefinition Surge = new()
    {
        Name = "Surge", Types = CardType.Instant, ManaCost = ManaCost.Parse("{G}"),
        Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new PumpUntilEndOfTurn(3, 3, Subject.TargetAt(0)) } },
    };
    private static readonly CardDefinition Pinger = new()
    {
        Name = "Pinger", Types = CardType.Creature, ManaCost = ManaCost.Parse("{2}{R}"), Power = 1, Toughness = 1,
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility { Cost = AbilityCost.TapOnly, Text = "{T}: 1 damage", Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) } },
        },
    };
    private static readonly CardDefinition Flyer = Scenario.Creature("Flyer", 2, 2, Keyword.Flying) with { ManaCost = ManaCost.Parse("{1}{G}") };
    private static readonly CardDefinition Brute = Scenario.Creature("Brute", 4, 3, Keyword.Trample) with { ManaCost = ManaCost.Parse("{3}{R}") };
    private static readonly CardDefinition Tough = Scenario.Creature("Wall", 0, 5, Keyword.Defender) with { ManaCost = ManaCost.Parse("{1}{G}") };

    private static IReadOnlyList<CardDefinition> RichDeck() => BotTestsDecks.Rich();

    internal static IReadOnlyList<CardDefinition> RichDeckCards() => Decks.Of(
        (GenericCards.Forest, 9), (GenericCards.Mountain, 9),
        (GenericCards.GladeCub, 4), (GenericCards.OgreBrute, 3), (GenericCards.GreatWurm, 2), (Flyer, 3), (Brute, 2), (Tough, 1),
        (Spark, 3), (Surge, 2), (Pinger, 2));

    private static (Game Game, BotController A, BotController B) BotGame(ulong seed, IReadOnlyList<CardDefinition>? deckA = null, IReadOnlyList<CardDefinition>? deckB = null)
    {
        var a = new BotController(new PlayerId(0));
        var b = new BotController(new PlayerId(1));
        var game = new Game(new GameConfig { Seed = seed }, new[]
        {
            new PlayerSetup("Bot A", a, deckA ?? RichDeck()),
            new PlayerSetup("Bot B", b, deckB ?? RichDeck()),
        });
        Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
        a.UseCardRules(rules);
        b.UseCardRules(rules);
        return (game, a, b);
    }

    [Theory]
    [InlineData(1UL)] [InlineData(2UL)] [InlineData(3UL)] [InlineData(4UL)] [InlineData(5UL)]
    [InlineData(6UL)] [InlineData(7UL)] [InlineData(8UL)] [InlineData(9UL)] [InlineData(10UL)]
    public async Task BotVersusBotGamesFinishWithoutErrors(ulong seed)
    {
        var (game, _, _) = BotGame(seed);
        await game.RunWithTimeout(20);
        Assert.True(game.State.IsGameOver);
        Assert.Contains(game.Log, e => e is SpellCast);
        Assert.Contains(game.Log, e => e is AttackerDeclared);
    }

    [Fact]
    public async Task BotUsesBurnAndAbilitiesAcrossGames()
    {
        int spells = 0, activations = 0, targetedSpells = 0;
        for (ulong seed = 1; seed <= 8; seed++)
        {
            var (game, _, _) = BotGame(seed);
            await game.RunWithTimeout(20);
            spells += game.Log.Count(e => e is SpellCast c && game.State.GetCard(c.Card).Definition.Spell is not null);
            targetedSpells += game.Log.Count(e => e is SpellCast c && game.State.GetCard(c.Card).Name is "Spark" or "Surge");
            activations += game.Log.Count(e => e is AbilityActivated);
        }
        Assert.True(targetedSpells > 0, "bot never cast a targeted instant");
        Assert.True(activations > 0, "bot never used an activated ability");
    }

    [Fact]
    public async Task BotBeatsAPassiveOpponent()
    {
        var bot = new BotController(new PlayerId(0));
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = new Game(new GameConfig { Seed = 3 }, new[]
        {
            new PlayerSetup("Bot", bot, RichDeck()),
            new PlayerSetup("Passive", passive, RichDeck()),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        await game.RunWithTimeout(20);
        Assert.Equal(new PlayerId(0), game.State.Winner);
    }

    [Fact]
    public async Task BotChumpBlocksWhenTheAttackIsLethal()
    {
        var s = new Scenario();
        var bot = new BotController(Scenario.P1);
        // Rebuild the scenario's defender as a bot: same seat, rules lookup from this game.
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0, StartingLife = 3 }, new[]
        {
            new PlayerSetup("Attacker", s.Attacker, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Bot", bot, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        var giant = game.SetupPermanent(Scenario.P0, Scenario.Creature("Giant", 5, 5));
        var cub = game.SetupPermanent(Scenario.P1, Scenario.Creature("Cub", 1, 1));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.Contains(game.Log, e => e is BlockerDeclared b && b.Blocker == cub && b.Attacker == giant);
        Assert.Equal(3, game.State.GetPlayer(Scenario.P1).Life);
    }

    [Fact]
    public async Task BotDoesNotAttackIntoABadBlock()
    {
        var bot = new BotController(Scenario.P0);
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        var cub = game.SetupPermanent(Scenario.P0, Scenario.Creature("Cub", 2, 2));
        game.SetupPermanent(Scenario.P1, Scenario.Creature("Wall", 3, 4));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.DoesNotContain(game.Log, e => e is AttackerDeclared a && a.Attacker == cub);
    }

    [Fact]
    public async Task BotAimsRemovalAtTheBiggestThreatItCanKill()
    {
        var bot = new BotController(Scenario.P0) { AlwaysKeep = true };
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, Decks.Of((GenericCards.Mountain, 20))),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        game.SetupPermanent(Scenario.P0, GenericCards.Mountain);
        game.SetupInHand(Scenario.P0, Spark);
        var small = game.SetupPermanent(Scenario.P1, Scenario.Creature("Cub", 1, 1));
        var flyer = game.SetupPermanent(Scenario.P1, Scenario.Creature("Drake", 3, 3, Keyword.Flying));
        game.SetupPermanent(Scenario.P1, Scenario.Creature("Giant", 6, 6));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 3 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.Equal(Zone.Graveyard, game.State.GetCard(flyer).Zone); // killable and the most valuable of those
        Assert.Equal(Zone.Battlefield, game.State.GetCard(small).Zone);
    }

    [Fact]
    public async Task BotOnlyAsksAboutCardsItCanSee()
    {
        var (game, a, _) = BotGame(4);
        var asked = new List<CardId>();
        a.UseCardRules(id =>
        {
            var card = game.State.GetCard(id);
            Assert.False(card.Zone == Zone.Library, "bot looked at a library card");
            Assert.False(card.Zone == Zone.Hand && card.Owner != new PlayerId(0), "bot looked at the opponent's hand");
            asked.Add(id);
            return card.Definition;
        });
        await game.RunWithTimeout(20);
        Assert.NotEmpty(asked);
    }
}

internal static class BotTestsDecks
{
    public static IReadOnlyList<CardDefinition> Rich() => BotTests.RichDeckCards();
}

public class BotStrengthTests
{
    [Fact]
    public async Task BotBeatsTheGreedyTestPlayerMostOfTheTime()
    {
        int botWins = 0, games = 20;
        for (ulong seed = 1; seed <= (ulong)games; seed++)
        {
            // Alternate seats so the starting player advantage evens out.
            bool botFirst = seed % 2 == 0;
            var bot = new BotController(new PlayerId(botFirst ? 0 : 1));
            var greedy = new TestController();
            var deck = BotTestsDecks.Rich();
            var seats = botFirst
                ? new[] { new PlayerSetup("Bot", bot, deck), new PlayerSetup("Greedy", greedy, deck) }
                : new[] { new PlayerSetup("Greedy", greedy, deck), new PlayerSetup("Bot", bot, deck) };
            var game = new Game(new GameConfig { Seed = seed }, seats);
            bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
            await game.RunWithTimeout(20);
            if (game.State.Winner == new PlayerId(botFirst ? 0 : 1)) botWins++;
        }
        Console.WriteLine($"bot won {botWins}/{games}");
        Assert.True(botWins >= games * 0.7, $"bot won only {botWins}/{games}");
    }
}

public class BotDefenseTests
{
    private static async Task<Game> RunBotTurn(params CardDefinition[] botCreatures)
    {
        var bot = new BotController(Scenario.P0);
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        foreach (var c in botCreatures) game.SetupPermanent(Scenario.P0, c);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        return game;
    }

    [Fact]
    public async Task ZeroPowerCreaturesNeverAttack()
    {
        var game = await RunBotTurn(Scenario.Creature("Wall", 0, 8));
        Assert.DoesNotContain(game.Log, e => e is AttackerDeclared);
    }

    [Fact]
    public async Task WallLikeCreaturesStayHomeButAttackersGo()
    {
        var game = await RunBotTurn(Scenario.Creature("Turtle", 1, 6), Scenario.Creature("Raider", 3, 2));
        var attackers = game.Log.OfType<AttackerDeclared>().Select(a => game.State.GetCard(a.Attacker).Name).ToList();
        Assert.Equal(new[] { "Raider" }, attackers);
    }
}

public class BotMultiplayerTests
{
    private static Game ThreePlayers(BotController bot, out PlayerId weak, out PlayerId strong)
    {
        weak = new PlayerId(1);
        strong = new PlayerId(2);
        TestController Passive() => new() { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0, StartingLife = 40 }, new[]
        {
            new PlayerSetup("Bot", bot, Decks.Of((GenericCards.Forest, 30))),
            new PlayerSetup("Weak", Passive(), Decks.Of((GenericCards.Forest, 30))),
            new PlayerSetup("Strong", Passive(), Decks.Of((GenericCards.Forest, 30))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        return game;
    }

    private static async Task RunTurn(Game game)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task AttacksTheBiggestThreatInsteadOfTheWeakestPlayer()
    {
        var bot = new BotController(Scenario.P0);
        var game = ThreePlayers(bot, out var weak, out var strong);
        game.SetupPermanent(Scenario.P0, Scenario.Creature("Drake", 3, 3, Keyword.Flying));
        // The strong player has a big ground board (no flyers, so the drake attacks safely); the weak one has nothing.
        for (int i = 0; i < 3; i++) game.SetupPermanent(strong, Scenario.Creature("Giant", 5, 5));
        game.State.GetPlayer(weak).Life = 12;
        await RunTurn(game);

        var attack = Assert.Single(game.Log.OfType<AttackerDeclared>());
        Assert.Equal(strong, attack.Defender);
    }

    [Fact]
    public async Task StillFinishesOffAPlayerItCanKill()
    {
        var bot = new BotController(Scenario.P0);
        var game = ThreePlayers(bot, out var weak, out var strong);
        game.SetupPermanent(Scenario.P0, Scenario.Creature("Drake", 3, 3, Keyword.Flying));
        for (int i = 0; i < 3; i++) game.SetupPermanent(strong, Scenario.Creature("Giant", 5, 5));
        game.State.GetPlayer(weak).Life = 3;
        await RunTurn(game);

        Assert.Equal(weak, Assert.Single(game.Log.OfType<AttackerDeclared>()).Defender);
        Assert.True(game.State.GetPlayer(weak).HasLost);
    }

    [Fact]
    public async Task KeepsABlockerHomeWhenSomeoneCouldHitHard()
    {
        var bot = new BotController(Scenario.P0);
        var game = ThreePlayers(bot, out _, out var strong);
        var drake = game.SetupPermanent(Scenario.P0, Scenario.Creature("Drake", 3, 3, Keyword.Flying));
        var guard = game.SetupPermanent(Scenario.P0, Scenario.Creature("Guard", 3, 4, Keyword.Flying));
        // Five 3/2 attackers threaten 15 damage; the 3/4 guard blocks them well, so it stays home.
        for (int i = 0; i < 5; i++) game.SetupPermanent(strong, Scenario.Creature("Raider", 3, 2));
        await RunTurn(game);

        var attackers = game.Log.OfType<AttackerDeclared>().Select(a => a.Attacker).ToList();
        Assert.DoesNotContain(guard, attackers);
        Assert.Contains(drake, attackers);
    }

    [Fact]
    public void AurasAreJudgedByEverythingTheyDoToTheEnchantedCreature()
    {
        CardDefinition Aura(params AbilityDefinition[] abilities) => new()
        {
            Name = "Test Aura", Types = CardType.Enchantment, Subtypes = new[] { "Aura" },
            EnchantTarget = new TargetSpec(TargetKind.Creature), Abilities = abilities,
        };
        var enchanted = new AffectedFilter(AffectedScope.Enchanted);
        // Loses its abilities and doesn't untap, with no change to power and toughness: meant for an opponent's creature.
        Assert.True(Evaluation.AuraIsHarmful(Aura(new StaticAbility(enchanted, Keywords: new[] { Keyword.DoesntUntap }) { LosesAllAbilities = true })));
        // Tapped and stripped of counters as the Aura enters.
        Assert.True(Evaluation.AuraIsHarmful(Aura(new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield,
            Effects = new Effect[] { new TapIt(Subject.Attached), new RemoveAllCounters(Subject.Attached) } })));
        Assert.True(Evaluation.AuraIsHarmful(Aura(new StaticAbility(enchanted, -2, -2))));
        Assert.True(Evaluation.AuraIsHarmful(Aura(new StaticAbility(enchanted, Keywords: new[] { Keyword.CantAttack, Keyword.CantBlock }))));
        // Bonuses, even with a drawback, are for the caster's own creature.
        Assert.False(Evaluation.AuraIsHarmful(Aura(new StaticAbility(enchanted, 2, 2))));
        Assert.False(Evaluation.AuraIsHarmful(Aura(new StaticAbility(enchanted, 3, 0, new[] { Keyword.CantBlock }))));
    }
}
