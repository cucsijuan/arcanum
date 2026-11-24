// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots.Simulation;
using Arcanum.Cards;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Tests;

/// <summary>What the bot imagines an opponent's hidden hand and library hold: the right number of cards, drawn from what fits the seen ones.</summary>
public class OpponentModelTests
{
    private static CardDefinition GreenSpell(string name) => Scenario.Creature(name, 2, 2) with { ManaCost = ManaCost.Parse("{1}{G}"), ColorIdentity = new[] { "G" } };

    private static CardDefinition RedSpell(string name) => Scenario.Creature(name, 2, 2) with { ManaCost = ManaCost.Parse("{1}{R}"), ColorIdentity = new[] { "R" } };

    private static CardDefinition GreenCommander() => Scenario.Creature("Grove Commander", 3, 3) with { ManaCost = ManaCost.Parse("{2}{G}"), ColorIdentity = new[] { "G" } };

    /// <summary>Two green spells and a Forest seen, in a forty-card deck with seven cards in hand and twenty in the library.</summary>
    private static OpponentKnowledge FortyCardKnowledge() => new(
        Seen: new[] { GenericCards.Forest, GreenSpell("Grove Sprout"), GreenSpell("Grove Bloom") },
        Commanders: Array.Empty<CardDefinition>(),
        DeckSize: 40,
        HiddenHand: 3,
        HiddenLibrary: 20);

    /// <summary>A commander game: one green commander, a green spell seen, and sixty hidden library cards plus seven in hand.</summary>
    private static OpponentKnowledge CommanderKnowledge() => new(
        Seen: new[] { GenericCards.Forest, GreenSpell("Grove Sprout") },
        Commanders: new[] { GreenCommander() },
        DeckSize: 100,
        HiddenHand: 7,
        HiddenLibrary: 60);

    /// <summary>Ten green spells and a Forest: a pool in the color seen.</summary>
    private static List<CardGuess> GreenPool()
    {
        var pool = Enumerable.Range(1, 10).Select(i => new CardGuess(GreenSpell($"Grove Candidate {i:00}"), 1)).ToList();
        pool.Add(new CardGuess(GenericCards.Forest, 1));
        return pool;
    }

    /// <summary>Forty green spells (one of them the seen Grove Sprout), ten red spells, and a Forest and a Mountain.</summary>
    private static List<CardGuess> CommanderPool()
    {
        var pool = Enumerable.Range(0, 40).Select(i => new CardGuess(GreenSpell($"Grove Candidate {i:00}"), 1)).ToList();
        pool.Add(new CardGuess(GreenSpell("Grove Sprout"), 1));
        pool.AddRange(Enumerable.Range(1, 10).Select(i => new CardGuess(RedSpell($"Ember Candidate {i:00}"), 1)));
        pool.Add(new CardGuess(GenericCards.Forest, 1));
        pool.Add(new CardGuess(GenericCards.Mountain, 1));
        return pool;
    }

    [Fact]
    public void ImaginedCardsFillTheHiddenSlots()
    {
        var (hand, library) = OpponentModel.Imagine(FortyCardKnowledge(), GreenPool(), new Random(1));

        Assert.Equal(3, hand.Count);
        Assert.Equal(20, library.Count);
    }

    [Fact]
    public void WithoutCandidatesOnlySeenCardsAreImagined()
    {
        var known = FortyCardKnowledge();
        var seenNames = known.Seen.Select(c => c.Name).ToHashSet();

        var (hand, library) = OpponentModel.Imagine(known, null, new Random(1));

        Assert.Equal(3, hand.Count);
        Assert.Equal(20, library.Count);
        Assert.All(hand.Concat(library), c => Assert.Contains(c.Name, seenNames));
    }

    [Fact]
    public void CommanderKeepsToTheIdentity()
    {
        var (hand, library) = OpponentModel.Imagine(CommanderKnowledge(), CommanderPool(), new Random(1));

        Assert.Equal(7, hand.Count);
        Assert.Equal(60, library.Count);
        Assert.All(hand.Concat(library), c => Assert.All(c.ColorIdentity, color => Assert.Equal("G", color)));
    }

    [Fact]
    public void CommanderImaginesNonbasicCardsOnce()
    {
        var known = CommanderKnowledge();
        var seenNonbasic = known.Seen.Where(c => (c.Supertypes & Supertype.Basic) == 0).Select(c => c.Name).ToHashSet();

        var (hand, library) = OpponentModel.Imagine(known, CommanderPool(), new Random(1));
        var nonbasic = hand.Concat(library).Where(c => (c.Supertypes & Supertype.Basic) == 0).Select(c => c.Name).ToList();

        Assert.Equal(67, hand.Count + library.Count);
        Assert.Equal(nonbasic.Count, nonbasic.Distinct().Count());
        Assert.Empty(nonbasic.Intersect(seenNonbasic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaceHiddenCardChangesACardInHand(bool inLibrary)
    {
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", new TestController(), Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", new TestController(), Decks.Of((GenericCards.Forest, 20))),
        });
        var id = inLibrary
            ? game.SetupInLibrary(Scenario.P0, Scenario.Creature("Hidden Cub", 2, 2))
            : game.SetupInHand(Scenario.P0, Scenario.Creature("Hidden Cub", 2, 2));
        var zone = game.State.GetCard(id).Zone;
        var owner = game.State.GetCard(id).Owner;

        game.ReplaceHiddenCard(id, Scenario.Creature("Imagined Cub", 3, 3));

        Assert.Equal("Imagined Cub", game.State.GetCard(id).Definition.Name);
        Assert.Equal(zone, game.State.GetCard(id).Zone);
        Assert.Equal(owner, game.State.GetCard(id).Owner);
    }

    [Fact]
    public void ReplaceHiddenCardRefusesABattlefieldCard()
    {
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = Scenario.P0 }, new[]
        {
            new PlayerSetup("Bot", new TestController(), Decks.Of((GenericCards.Forest, 20))),
            new PlayerSetup("Defender", new TestController(), Decks.Of((GenericCards.Forest, 20))),
        });
        var id = game.SetupPermanent(Scenario.P0, Scenario.Creature("Visible Cub", 2, 2));

        Assert.Throws<InvalidOperationException>(() => game.ReplaceHiddenCard(id, Scenario.Creature("Imagined Cub", 3, 3)));
    }

    [Fact]
    public void ReseedRepeatsTheSequence()
    {
        var a = new DeterministicRng(1);
        var b = new DeterministicRng(2);
        for (int i = 0; i < 5; i++) a.NextUInt();

        a.Reseed(7);
        b.Reseed(7);

        for (int i = 0; i < 10; i++) Assert.Equal(a.NextUInt(), b.NextUInt());
    }
}
