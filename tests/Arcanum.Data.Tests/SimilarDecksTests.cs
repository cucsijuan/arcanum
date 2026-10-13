// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;

namespace Arcanum.Data.Tests;

public class SimilarDecksTests
{
    private static bool IsBasicLand(string name) => name is "Forest" or "Mountain";

    private static string? TypeOf(string name) => name switch
    {
        "Glade Cub" or "River Scout" => "creature",
        "Ember Spark" => "instant",
        _ => null,
    };

    private static DeckList Deck(params (string Name, int Count)[] cards)
    {
        var deck = new DeckList();
        foreach (var (name, count) in cards) DeckList.Adjust(deck.Main, name, count);
        return deck;
    }

    private static SimilarDeck Similar(int id, DeckList list, int shared = 0) =>
        new(new FoundDeck(id, $"Deck {id}", list.Main.Sum(e => e.Count), 10), list, shared);

    [Fact]
    public void ASearchPageGivesTheFoundDecks()
    {
        var found = SimilarDecks.ParseSearch("""
            {"count":2,"results":[{"id":11,"name":"A","size":60,"viewCount":500},{"id":12,"name":"B","size":75,"viewCount":20}]}
            """);

        Assert.Equal(new[] { new FoundDeck(11, "A", 60, 500), new FoundDeck(12, "B", 75, 20) }, found);
    }

    [Fact]
    public void ASearchPageWithoutResultsFindsNoDecks() =>
        Assert.Empty(SimilarDecks.ParseSearch("""{"count":0}"""));

    [Fact]
    public void ADeckPageGivesItsMainAndSideboardAndLeavesOutTheMaybeboard()
    {
        var deck = SimilarDecks.ParseDeck("""
            {"categories":[{"name":"Sideboard","includedInDeck":true},{"name":"Maybeboard","includedInDeck":false},{"name":"Creature","includedInDeck":true}],
             "cards":[{"quantity":4,"categories":["Creature"],"card":{"oracleCard":{"name":"Glade Cub"}}},
                      {"quantity":2,"categories":["Sideboard"],"card":{"oracleCard":{"name":"Ember Spark"}}},
                      {"quantity":1,"categories":["Maybeboard"],"card":{"oracleCard":{"name":"Tide Shaper"}}},
                      {"quantity":20,"categories":["Land"],"card":{"oracleCard":{"name":"Forest"}}}]}
            """)!;

        Assert.Equal(new[] { (4, "Glade Cub"), (20, "Forest") }, deck.Main.Select(e => (e.Count, e.Name)));
        Assert.Equal(new[] { (2, "Ember Spark") }, deck.Sideboard.Select(e => (e.Count, e.Name)));
        Assert.DoesNotContain(deck.Main, e => e.Name == "Tide Shaper");
        Assert.DoesNotContain(deck.Sideboard, e => e.Name == "Tide Shaper");
    }

    [Fact]
    public void ADeckPageWithoutCardsIsNoDeck() =>
        Assert.Null(SimilarDecks.ParseDeck("""{"categories":[],"cards":[]}"""));

    [Fact]
    public void TheKeyCardsAreTheMostPlayedNonBasicCards()
    {
        var deck = Deck(("Forest", 20), ("Glade Cub", 4), ("Ember Spark", 2), ("River Scout", 4));

        Assert.Equal(new[] { "Glade Cub", "River Scout" }, SimilarDecks.KeyCards(deck, IsBasicLand, 2));
    }

    [Fact]
    public void SharedCardsCountsTheDistinctNonBasicCardsInCommon()
    {
        var mine = Deck(("Glade Cub", 4), ("Ember Spark", 2), ("Forest", 20));
        var other = Deck(("Glade Cub", 2), ("River Scout", 4), ("Ember Spark", 1), ("Forest", 18));

        Assert.Equal(2, SimilarDecks.SharedCards(mine, other, IsBasicLand));
    }

    [Fact]
    public void SuggestionsAreTheCardsOfTheSimilarDecksNotInTheDeckFirst()
    {
        var mine = Deck(("Glade Cub", 4), ("Forest", 20));
        var oneList = Deck(("Glade Cub", 4), ("Ember Spark", 2), ("Forest", 20));
        var twoList = Deck(("River Scout", 4), ("Forest", 20));
        var one = Similar(1, oneList, SimilarDecks.SharedCards(mine, oneList, IsBasicLand));
        var two = Similar(2, twoList, SimilarDecks.SharedCards(mine, twoList, IsBasicLand));

        var insights = SimilarDecks.Suggestions(mine, new[] { one, two }, IsBasicLand, TypeOf);

        Assert.Equal(2, insights.Decks);
        Assert.Equal(2, insights.Themes.Count);
        Assert.Equal(new[] { "1", "2" }, insights.Themes.Select(t => t.Slug));
        Assert.Equal(new[] { 1, 0 }, insights.Themes.Select(t => t.Decks));

        var newCards = insights.Sections.Single(s => s.Key == "new").Cards;
        Assert.Contains(newCards, c => c.Name == "Ember Spark");
        Assert.Contains(newCards, c => c.Name == "River Scout");
        Assert.DoesNotContain(newCards, c => c.Name == "Glade Cub");

        var creatures = insights.Sections.Single(s => s.Key == "creature").Cards;
        Assert.Contains(creatures, c => c.Name == "Glade Cub");
        Assert.Contains(creatures, c => c.Name == "River Scout");

        Assert.DoesNotContain(insights.Sections.SelectMany(s => s.Cards), c => c.Name == "Forest");

        var ember = newCards.Single(c => c.Name == "Ember Spark");
        Assert.Equal(1, ember.Decks);
        Assert.Equal(2, ember.PotentialDecks);
        Assert.Equal(0.5, ember.Inclusion);
        Assert.True(ember.Synergy > 0);
        Assert.Equal(0.5, ember.Synergy, 6);

        var scout = newCards.Single(c => c.Name == "River Scout");
        Assert.Equal(-0.5, scout.Synergy, 6);

        Assert.Equal(20, insights.AverageTypes["land"]);
        Assert.Equal(4, insights.AverageTypes["creature"]);
        Assert.Equal(1, insights.AverageTypes["instant"]);
    }

    [Fact]
    public void ThemesCanBeGivenApartFromTheDecksTheSuggestionsComeFrom()
    {
        var mine = Deck(("Glade Cub", 4), ("Forest", 20));
        var oneList = Deck(("Glade Cub", 4), ("Ember Spark", 2), ("Forest", 20));
        var twoList = Deck(("River Scout", 4), ("Forest", 20));
        var one = Similar(1, oneList, SimilarDecks.SharedCards(mine, oneList, IsBasicLand));
        var two = Similar(2, twoList, SimilarDecks.SharedCards(mine, twoList, IsBasicLand));

        var insights = SimilarDecks.Suggestions(mine, new[] { one }, IsBasicLand, TypeOf, themes: new[] { one, two });

        Assert.Equal(1, insights.Decks);
        Assert.Equal(2, insights.Themes.Count);
    }

    [Fact]
    public void AnAverageDeckHasTheCardsOfTheSimilarDecksByAverageCopies()
    {
        var a = Deck(("Glade Cub", 4), ("Forest", 20));
        var b = Deck(("Glade Cub", 2), ("Forest", 18), ("Ember Spark", 4));

        var average = SimilarDecks.Average(new[] { Similar(1, a), Similar(2, b) }, 30);

        // Copies in all: Forest 38, Glade Cub 6, Ember Spark 4; the average of two decks is 19, 3 and 2 copies.
        // The decks have 24 cards between them, so the average deck has 24 cards rather than 30.
        Assert.Equal(new[] { (19, "Forest"), (3, "Glade Cub"), (2, "Ember Spark") }, average.Main.Select(e => (e.Count, e.Name)));
        Assert.Equal(24, average.Main.Sum(e => e.Count));
    }
}
