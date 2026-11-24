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

/// <summary>What the bot does with its deck's plan: which land it plays, its mulligans, and what it takes from the opponent.</summary>
public class BotPlanTests
{
    private static readonly CardDefinition EmberCub = Scenario.Creature("Ember Cub", 2, 2) with { ManaCost = ManaCost.Parse("{1}{R}") };

    /// <summary>The land it plays is the one its spells need: a Mountain for a red spell, not the Forest it has in hand.</summary>
    [Fact]
    public async Task PlaysTheLandItsSpellsNeed()
    {
        var bot = new BotController(Scenario.P0) { AlwaysKeep = true };
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var deck = Decks.Of((GenericCards.Forest, 10), (GenericCards.Mountain, 10), (EmberCub, 4));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, deck),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        bot.KnowDeck(deck);
        game.SetupPermanent(Scenario.P0, GenericCards.Forest);
        var forestInHand = game.SetupInHand(Scenario.P0, GenericCards.Forest);
        var mountain = game.SetupInHand(Scenario.P0, GenericCards.Mountain);
        var cub = game.SetupInHand(Scenario.P0, EmberCub);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.Equal(Zone.Battlefield, game.State.GetCard(mountain).Zone);
        Assert.NotEqual(Zone.Battlefield, game.State.GetCard(forestInHand).Zone); // kept over the Mountain (or discarded to hand size)
        Assert.Equal(Zone.Battlefield, game.State.GetCard(cub).Zone);
    }

    private static readonly CardDefinition WhelpR = Scenario.Creature("Ember Whelp", 1, 1) with { ManaCost = ManaCost.Parse("{R}") };
    private static readonly CardDefinition CubR = Scenario.Creature("Ember Cub Two", 2, 2) with { ManaCost = ManaCost.Parse("{1}{R}") };
    private static readonly CardDefinition BrawlerR = Scenario.Creature("Ember Brawler", 3, 3) with { ManaCost = ManaCost.Parse("{2}{R}") };
    private static readonly CardDefinition GiantR = Scenario.Creature("Ember Giant", 4, 4) with { ManaCost = ManaCost.Parse("{3}{R}") };

    /// <summary>A seven-card hand of three lands and four red spells is kept only when its lands make the spells' color.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MulligansAHandWithoutItsColors(bool landsMatch)
    {
        var bot = new BotController(Scenario.P0);
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var deck = Decks.Of((GenericCards.Mountain, 10), (GenericCards.Swamp, 10), (WhelpR, 1), (CubR, 1), (BrawlerR, 1), (GiantR, 1));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, deck),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        bot.KnowDeck(deck);
        var land = landsMatch ? GenericCards.Mountain : GenericCards.Swamp;
        for (int i = 0; i < 3; i++) game.SetupInHand(Scenario.P0, land);
        foreach (var spell in new[] { WhelpR, CubR, BrawlerR, GiantR }) game.SetupInHand(Scenario.P0, spell);

        var view = game.ViewFor(Scenario.P0);
        Assert.Equal(landsMatch, await bot.KeepHandAsync(view, 0));
    }

    /// <summary>"Borrowed Fury": the bot takes the opponent's best creature for the turn only when that attack is lethal.</summary>
    private static readonly CardDefinition BorrowedFury = new()
    {
        Name = "Borrowed Fury", Types = CardType.Sorcery, ManaCost = ManaCost.Parse("{2}{R}"),
        Spell = new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
            Effects = new Effect[]
            {
                new GainControl(Subject.TargetAt(0), UntilEndOfTurn: true),
                new UntapIt(Subject.TargetAt(0)),
                new PumpUntilEndOfTurn(0, 0, Subject.TargetAt(0), Keywords: new[] { Keyword.Haste }),
            },
        },
    };

    [Theory]
    [InlineData(7, true)]
    [InlineData(20, false)]
    public async Task TakesACreatureForTheTurnOnlyWhenItWins(int startingLife, bool expected)
    {
        var bot = new BotController(Scenario.P0) { AlwaysKeep = true };
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var deck = Decks.Of((GenericCards.Mountain, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0, StartingLife = startingLife }, new[]
        {
            new PlayerSetup("Bot", bot, deck),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        bot.KnowDeck(deck);
        for (int i = 0; i < 3; i++) game.SetupPermanent(Scenario.P0, GenericCards.Mountain);
        game.SetupPermanent(Scenario.P0, Scenario.Creature("Bear", 3, 3));
        var fury = game.SetupInHand(Scenario.P0, BorrowedFury);
        game.SetupPermanent(Scenario.P1, Scenario.Creature("Ogre Guard", 4, 4));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }

        if (expected)
        {
            Assert.Contains(game.Log, e => e is SpellCast c && c.Card == fury);
            Assert.Equal(Scenario.P0, game.State.Winner);
        }
        else Assert.Equal(Zone.Hand, game.State.GetCard(fury).Zone);
    }

    /// <summary>"Lasting Claim": the bot takes the opponent's best creature for good, not the smaller one.</summary>
    private static readonly CardDefinition LastingClaim = new()
    {
        Name = "Lasting Claim", Types = CardType.Sorcery, ManaCost = ManaCost.Parse("{2}{U}{U}"),
        Spell = new SpellAbility
        {
            Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
            Effects = new Effect[] { new GainControl(Subject.TargetAt(0)) },
        },
    };

    [Fact]
    public async Task TakesTheBestCreatureForGood()
    {
        var bot = new BotController(Scenario.P0) { AlwaysKeep = true };
        var passive = new TestController { Act = (_, _) => PassPriority.Instance, Attack = (_, _, _) => Array.Empty<AttackDeclaration>() };
        var deck = Decks.Of((GenericCards.Island, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", bot, deck),
            new PlayerSetup("Defender", passive, Decks.Of((GenericCards.Forest, 20))),
        });
        bot.UseCardRules(id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null);
        bot.KnowDeck(deck);
        for (int i = 0; i < 4; i++) game.SetupPermanent(Scenario.P0, GenericCards.Island);
        game.SetupInHand(Scenario.P0, LastingClaim);
        game.SetupPermanent(Scenario.P1, Scenario.Creature("Cub", 2, 2));
        var big = game.SetupPermanent(Scenario.P1, Scenario.Creature("Titan", 5, 5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }

        Assert.Equal(Scenario.P0, game.State.GetCard(big).Controller);
    }
}
