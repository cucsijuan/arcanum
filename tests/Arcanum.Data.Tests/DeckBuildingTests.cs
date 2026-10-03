// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

public class DeckBuildingTests
{
    private static CardRecord Card(string name, string type, string cost, string[] colors, Dictionary<string, string>? legal = null, string text = "",
        string? p = null, string? t = null) => new()
    {
        OracleId = "o-" + name, Name = name, Layout = "normal", TypeLine = type, ManaCost = cost, Colors = colors, OracleText = text,
        Power = p, Toughness = t, Legalities = legal ?? new Dictionary<string, string> { ["eternal"] = "legal" },
    };

    private static readonly CardDatabase Db = new(new[]
    {
        Card("Forest", "Basic Land — Forest", "", Array.Empty<string>(), text: "({T}: Add {G}.)"),
        Card("Glade Cub", "Creature — Bear", "{1}{G}", new[] { "G" }, p: "2", t: "2"),
        Card("Ember Bolt", "Instant", "{R}", new[] { "R" }, text: "Ember Bolt deals 3 damage to any target."),
        Card("Sky Lancer", "Creature — Bird", "{2}{W}", new[] { "W" }, text: "Flying", p: "2", t: "2"),
        Card("Old Relic", "Artifact", "{3}", Array.Empty<string>(), new Dictionary<string, string> { ["eternal"] = "restricted" }),
        Card("Forbidden Idol", "Artifact", "{1}", Array.Empty<string>(), new Dictionary<string, string> { ["eternal"] = "banned" }),
    });

    [Fact]
    public void SearchFiltersByTextColorTypeAndManaValue()
    {
        Assert.Equal(new[] { "Ember Bolt" }, Db.Search(new CardQuery { Text = "damage" }).Select(e => e.Name));
        Assert.Equal(new[] { "Glade Cub" }, Db.Search(new CardQuery { Colors = new HashSet<string> { "G" } }).Select(e => e.Name));
        Assert.Equal(new[] { "Glade Cub", "Sky Lancer" }, Db.Search(new CardQuery { Types = CardType.Creature }).Select(e => e.Name));
        Assert.Equal(new[] { "Forbidden Idol", "Forest" }, Db.Search(new CardQuery { ManaValueMax = 1, Colorless = true }).Select(e => e.Name));
        Assert.DoesNotContain(Db.Search(new CardQuery { LegalIn = "eternal" }), e => e.Name == "Forbidden Idol");
        Assert.Contains(Db.Search(new CardQuery { LegalIn = "eternal" }), e => e.Name == "Old Relic");
    }

    [Fact]
    public void SupportedOnlyHidesCardsTheEngineCantRun()
    {
        var supported = Db.Search(new CardQuery { SupportedOnly = true }).Select(e => e.Name).ToList();
        Assert.Contains("Glade Cub", supported);
        Assert.DoesNotContain("Ember Bolt", supported); // needs a script
    }

    private static readonly FormatRules Eternal = FormatRules.Parse("""
        { "id": "eternal", "name": "Eternal", "legality": "eternal", "minDeckSize": 60, "maxCopies": 4, "sideboardMax": 15 }
        """);

    [Fact]
    public void ValidDeckHasNoErrors()
    {
        var deck = DeckList.Parse("40 Forest\n4 Glade Cub\n4 Sky Lancer\n12 Forest\n");
        var errors = DeckValidator.Validate(deck, Eternal, Db).Where(i => i.Severity == IssueSeverity.Error);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatorReportsSizeCopiesLegalityAndUnknownCards()
    {
        var deck = DeckList.Parse("""
            30 Forest
            5 Glade Cub
            2 Old Relic
            1 Forbidden Idol
            1 Mystery Card
            Sideboard
            16 Forest
            """);
        var messages = DeckValidator.Validate(deck, Eternal, Db).Select(i => i.Message).ToList();
        Assert.Contains(messages, m => m.Contains("needs at least 60"));
        Assert.Contains(messages, m => m.Contains("5 copies of Glade Cub"));
        Assert.Contains(messages, m => m.Contains("Old Relic is restricted"));
        Assert.Contains(messages, m => m.Contains("Forbidden Idol is banned"));
        Assert.Contains(messages, m => m.Contains("Unknown card: Mystery Card"));
        Assert.Contains(messages, m => m.Contains("Sideboard has 16"));
        Assert.DoesNotContain(messages, m => m.Contains("copies of Forest")); // basics are unlimited
    }

    [Fact]
    public void UnsupportedCardsAreWarningsNotErrors()
    {
        var deck = DeckList.Parse("56 Forest\n4 Ember Bolt");
        var issues = DeckValidator.Validate(deck, Eternal, Db);
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        Assert.Contains(issues, i => i.Card == "Ember Bolt");
    }

    [Fact]
    public void ExportRoundTripsThroughParse()
    {
        var deck = DeckList.Parse("Commander\n1 Sky Lancer\nDeck\n4 Glade Cub\n20 Forest\nSideboard\n2 Ember Bolt\n");
        var again = DeckList.Parse(deck.Export());
        Assert.Equal(deck.Commander, again.Commander);
        Assert.Equal(deck.Main, again.Main);
        Assert.Equal(deck.Sideboard, again.Sideboard);
    }

    [Fact]
    public void AdjustAddsAndRemovesCopies()
    {
        var deck = new DeckList();
        DeckList.Adjust(deck.Main, "Glade Cub", 2);
        DeckList.Adjust(deck.Main, "glade cub", 1);
        Assert.Equal(3, Assert.Single(deck.Main).Count);
        DeckList.Adjust(deck.Main, "Glade Cub", -3);
        Assert.Empty(deck.Main);
    }

    [Fact]
    public void CommonDeckSiteFormatsImport()
    {
        // Typical exports: set code + collector number suffixes, "Companion" section, blank-line separated sideboard.
        var deck = DeckList.Parse("Deck\n4 Glade Cub (ABC) 12\n2 Sky Lancer (XYZ) 7a\n\nCompanion\n1 Ember Bolt\n");
        Assert.Equal(2, deck.Main.Count);
        Assert.Equal("Sky Lancer", deck.Main[1].Name);
        Assert.Equal("Ember Bolt", Assert.Single(deck.Sideboard).Name);
    }
}
