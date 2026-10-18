// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Modules;

namespace Arcanum.Data.Tests;

public class CommanderInsightsTests
{
    private const string Page = """
        {
          "creature": 24, "instant": 10, "land": 35, "battle": 0,
          "tag_counts": [{"count": 3817, "slug": "infect", "value": "Infect"}, {"count": 2206, "slug": "plus-1-plus-1-counters", "value": "+1/+1 Counters"}],
          "container": {"json_dict": {
            "card": {"name": "Sample Commander", "num_decks": 45537},
            "cardlists": [
              {"header": "Top Cards", "tag": "topcards", "cardviews": [
                {"name": "Glade Cub", "synergy": 0.27, "num_decks": 348, "potential_decks": 3562},
                {"name": "Ember Bolt", "synergy": -0.05, "num_decks": 1000, "potential_decks": 2000}]},
              {"header": "Empty", "tag": "empty", "cardviews": []},
              {"header": "Lands", "tag": "lands", "cardviews": [{"name": "Forest"}, {"synergy": 0.1}]}
            ]
          }}
        }
        """;

    [Fact]
    public void APageGivesTheSuggestedCardsByGroupWithTheirStatistics()
    {
        var insights = CommanderInsights.Parse(Page);

        Assert.Equal(45537, insights.Decks);
        Assert.Equal(new[] { "topcards", "lands" }, insights.Sections.Select(s => s.Key));
        Assert.Equal("Top Cards", insights.Sections[0].Title);
        var cub = insights.Sections[0].Cards[0];
        Assert.Equal(("Glade Cub", 0.27, 348, 3562), (cub.Name, cub.Synergy, cub.Decks, cub.PotentialDecks));
        Assert.Equal(0.5, insights.Sections[0].Cards[1].Inclusion);
        Assert.Equal(new[] { "Forest" }, insights.Sections[1].Cards.Select(c => c.Name)); // a card without a name is skipped
        Assert.Equal(0, insights.Sections[1].Cards[0].Inclusion);
    }

    [Fact]
    public void APageGivesTheThemesAndTheAverageDecksCardTypes()
    {
        var insights = CommanderInsights.Parse(Page);

        Assert.Equal(new[] { ("infect", "Infect", 3817), ("plus-1-plus-1-counters", "+1/+1 Counters", 2206) },
            insights.Themes.Select(t => (t.Slug, t.Name, t.Decks)));
        Assert.Equal(24, insights.AverageTypes["creature"]);
        Assert.Equal(35, insights.AverageTypes["land"]);
        Assert.False(insights.AverageTypes.ContainsKey("sorcery"));
    }

    [Fact]
    public void APageWithoutTheExpectedPartsHasNoSuggestions()
    {
        var insights = CommanderInsights.Parse("""{"header": "Something else"}""");

        Assert.Empty(insights.Sections);
        Assert.Empty(insights.Themes);
        Assert.Equal(0, insights.Decks);
    }

    [Fact]
    public void AnAverageDeckBecomesADeckList()
    {
        var deck = CommanderInsights.ParseAverageDeck("""
            {"deck": {"commander": ["Sample Commander"], "cards": {
              "Creature": [["Glade Cub", 1], ["Sky Lancer", 1]],
              "Land": [["Forest", 30], ["Bad Line"], ["Swamp", 0]]}}}
            """)!;

        Assert.Equal(new[] { "Sample Commander" }, deck.Commander.Select(e => e.Name));
        Assert.Equal(new[] { (1, "Glade Cub"), (1, "Sky Lancer"), (30, "Forest") }, deck.Main.Select(e => (e.Count, e.Name)));
        Assert.Null(CommanderInsights.ParseAverageDeck("""{"header": "no deck"}"""));
    }

    [Theory]
    [InlineData("Atraxa, Praetors' Voice", "atraxa-praetors-voice")]
    [InlineData("Lim-Dûl the Necromancer", "lim-dul-the-necromancer")]
    [InlineData("Jòdah, the Unifier", "jodah-the-unifier")]
    [InlineData("Sol'kanar the Swamp King", "solkanar-the-swamp-king")]
    [InlineData("Esika, God of the Tree // The Prismatic Bridge", "esika-god-of-the-tree")]
    public void ACommandersPageIsNamedAfterItsFrontFace(string name, string slug) =>
        Assert.Equal(slug, CommanderInsights.Slug(new[] { name }));

    [Fact]
    public void PartnersArePutInAlphabeticalOrder() =>
        Assert.Equal("orvane-the-mender-quill-ashen-scribe", CommanderInsights.Slug(new[] { "Quill, Ashen Scribe", "Orvane the Mender" }));

    private static ContentModule Load(string sources)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arcanum-module-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), """{"id":"sample","name":"Sample","version":"1.0.0","apiVersion":5,"license":"AGPL-3.0-or-later"}""");
            File.WriteAllText(Path.Combine(dir, "sources.json"), sources);
            return ContentModule.Load(dir);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Cards = """ "cards":{"format":"jsonl","index":"https://example.invalid/i","downloadField":"d","updatedField":"u"},"images":{"urlTemplate":"https://example.invalid/{name}"}""";

    [Fact]
    public void TheModuleNamesWhereCommanderStatisticsComeFrom()
    {
        var module = Load("{" + Cards + """, "commanderInsights":{"pageTemplate":"https://example.invalid/c/{slug}.json","averageDeckTemplate":"https://example.invalid/a/{slug}.json"}}""");

        Assert.Equal(7, module.Sources.CommanderInsights!.CacheDays);
        Assert.Equal("https://example.invalid/c/sample.json", module.CommanderInsightsUrl("sample"));
        Assert.Equal("https://example.invalid/a/sample/budget.json", module.CommanderInsightsUrl("sample", "budget", averageDeck: true));
    }

    [Fact]
    public void CommanderStatisticsAreOptional()
    {
        var module = Load("{" + Cards + "}");

        Assert.Null(module.Sources.CommanderInsights);
        Assert.Null(module.CommanderInsightsUrl("sample"));
    }
}
