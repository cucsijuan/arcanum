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

public class CommanderFormatTests
{
    private static CardRecord Card(string name, string type, string[] identity, string text = "", string[]? keywords = null) => new()
    {
        OracleId = "o-" + name, Name = name, Layout = "normal", TypeLine = type, ManaCost = "{1}", OracleText = text,
        ColorIdentity = identity, Keywords = keywords ?? Array.Empty<string>(), Power = type.Contains("Creature") ? "2" : null,
        Toughness = type.Contains("Creature") ? "2" : null, Legalities = new Dictionary<string, string> { ["edh"] = "legal" },
    };

    private static readonly CardDatabase Db = new(new[]
    {
        Card("Forest", "Basic Land — Forest", new[] { "G" }),
        Card("Grove Queen", "Legendary Creature — Elf", new[] { "G" }),
        Card("Anvil Duke", "Legendary Creature — Dwarf", new[] { "R" }),
        Card("Twin A", "Legendary Creature — Elf", new[] { "G" }, "Partner (You can have two commanders if both have partner.)", new[] { "Partner" }),
        Card("Twin B", "Legendary Creature — Dwarf", new[] { "R" }, "Partner", new[] { "Partner" }),
        Card("Meadow Hero", "Legendary Creature — Halfling", new[] { "G" }, "Partner with Loyal Friend\nVigilance", new[] { "Partner with", "Partner", "Vigilance" }),
        Card("Loyal Friend", "Legendary Creature — Halfling", new[] { "G" }, "Partner with Meadow Hero (When this creature enters, target player may put Meadow Hero into their hand from their library, then shuffle.)", new[] { "Partner with", "Partner" }),
        Card("Bard Hero", "Legendary Creature — Human", new[] { "R" }, "Choose a Background (You can have a Background as a second commander.)", new[] { "Choose a background" }),
        Card("Noble Upbringing", "Legendary Enchantment — Background", new[] { "G" }, "Commander creatures you own get +1/+1."),
        Card("Plain Bear", "Creature — Bear", new[] { "G" }),
        Card("Ember Imp", "Creature — Imp", new[] { "R" }),
    }.Concat(Enumerable.Range(0, 60).Select(i => Card($"Elf {i}", "Creature — Elf", new[] { "G" }))));

    private static readonly FormatRules Edh = FormatRules.Parse("""
        { "id": "edh", "name": "Commander", "legality": "edh", "minDeckSize": 100, "maxDeckSize": 100, "maxCopies": 1, "sideboardMax": 0, "commander": true, "startingLife": 40 }
        """);

    private static DeckList Deck(string commander, params string[] extra)
    {
        var text = $"Commander\n1 {commander}\nDeck\n39 Forest\n" + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"1 Elf {i}")) + "\n" + string.Join("\n", extra);
        return DeckList.Parse(text);
    }

    private static List<string> Errors(DeckList deck) =>
        DeckValidator.Validate(deck, Edh, Db).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void ValidCommanderDeck() => Assert.Empty(Errors(Deck("Grove Queen")));

    [Fact]
    public void DeckSizeCountsTheCommander()
    {
        var errors = Errors(Deck("Grove Queen", "1 Plain Bear"));
        Assert.Contains(errors, e => e.Contains("at most 100"));
    }

    [Fact]
    public void CardsOutsideColorIdentityAreIllegal()
    {
        var deck = Deck("Grove Queen");
        deck.Main.RemoveAt(deck.Main.Count - 1);
        deck.Main.Add(new DeckEntry(1, "Ember Imp"));
        Assert.Contains(Errors(deck), e => e.Contains("Ember Imp is outside your commander's color identity"));
    }

    [Fact]
    public void SingletonApplies()
    {
        var deck = Deck("Grove Queen");
        deck.Main[^1] = deck.Main[^2];
        Assert.Contains(Errors(deck), e => e.Contains("2 copies of"));
    }

    [Fact]
    public void CommanderMustBeALegendaryCreature()
    {
        Assert.Contains(Errors(Deck("Plain Bear")), e => e.Contains("can't be a commander"));
    }

    [Fact]
    public void TwoCommandersNeedPartner()
    {
        var two = Deck("Grove Queen");
        two.Commander.Add(new DeckEntry(1, "Anvil Duke"));
        two.Main.RemoveAt(two.Main.Count - 1);
        Assert.Contains(Errors(two), e => e.Contains("two that can be together"));

        var partners = Deck("Twin A");
        partners.Commander.Add(new DeckEntry(1, "Twin B"));
        partners.Main.RemoveAt(partners.Main.Count - 1);
        Assert.DoesNotContain(Errors(partners), e => e.Contains("partner"));
    }

    private static List<string> PairErrors(string first, string second)
    {
        var deck = Deck(first);
        deck.Commander.Add(new DeckEntry(1, second));
        deck.Main.RemoveAt(deck.Main.Count - 1);
        return Errors(deck).Where(e => !e.Contains("color identity")).ToList();
    }

    [Fact]
    public void PartnerWithPairsOnlyWithTheNamedCard()
    {
        Assert.Empty(PairErrors("Meadow Hero", "Loyal Friend"));
        // "Partner with" is not plain partner: it doesn't pair with other partners.
        Assert.Contains(PairErrors("Meadow Hero", "Twin A"), e => e.Contains("two that can be together"));
    }

    [Fact]
    public void ABackgroundIsACommanderOnlyBesideOneThatChoosesIt()
    {
        Assert.Empty(PairErrors("Bard Hero", "Noble Upbringing"));
        Assert.Contains(PairErrors("Grove Queen", "Noble Upbringing"), e => e.Contains("Noble Upbringing can't be a commander"));
    }

    [Fact]
    public void MissingCommanderIsReported()
    {
        var deck = DeckList.Parse("40 Forest\n" + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"1 Elf {i}")));
        Assert.Contains(Errors(deck), e => e.Contains("Choose a commander"));
    }
}
