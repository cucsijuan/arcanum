// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;

namespace Arcanum.Data.Tests;

public class CardSearchTests
{
    private static Printing Print(string set, string number, string rarity, string released, bool foil = true, bool booster = true, string type = "expansion") =>
        new(set, set.ToUpperInvariant() + " Set", number, rarity, $"{set}-{number}", released, type, booster, Array.Empty<RelatedToken>(), foil);

    private static CardRecord Card(string name, string type, string cost, string[] colors, string text = "", string? p = null, string? t = null,
        string[]? identity = null, string[]? keywords = null, string[]? produces = null, Printing[]? printings = null, string? legal = "legal") => new()
    {
        OracleId = "o-" + name, Name = name, Layout = "normal", TypeLine = type, ManaCost = cost, Colors = colors, OracleText = text,
        Power = p, Toughness = t, ColorIdentity = identity ?? colors, Keywords = keywords ?? Array.Empty<string>(),
        ProducedMana = produces ?? Array.Empty<string>(),
        Legalities = legal is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["commander"] = legal },
        Printings = printings ?? new[] { Print("old", "1", "common", "2010-07-16") },
    };

    private static readonly CardDatabase Db = new(new[]
    {
        Card("Glade Cub", "Creature — Bear", "{1}{G}", new[] { "G" }, p: "2", t: "2",
            printings: new[] { Print("old", "12", "common", "2010-07-16"), Print("new", "140", "uncommon", "2024-11-15") }),
        Card("Ember Bolt", "Instant", "{R}", new[] { "R" }, "Ember Bolt deals 3 damage to any target.",
            printings: new[] { Print("new", "80", "common", "2024-11-15") }),
        Card("Sky Lancer", "Creature — Human Knight", "{2}{W}{W}", new[] { "W" }, "Flying\nVigilance", "3", "2", keywords: new[] { "Flying", "Vigilance" }),
        Card("Azure Sage", "Legendary Creature — Human Wizard", "{1}{W}{U}", new[] { "W", "U" }, "When this creature enters, draw a card.", "1", "4",
            printings: new[] { Print("new", "200", "mythic", "2024-11-15") }),
        Card("Ruin Totem", "Artifact", "{3}", Array.Empty<string>(), "{T}: Add {C}.", produces: new[] { "C" }, legal: "banned"),
        Card("Wild Grove", "Land", "", Array.Empty<string>(), "Wild Grove enters tapped.\n{T}: Add {G} or {U}.", identity: new[] { "G", "U" }, produces: new[] { "G", "U" }),
        Card("Fire Giant", "Creature — Giant", "{4}{R}{R}", new[] { "R" }, "Trample", "6", "6", keywords: new[] { "Trample" }),
    });

    private static List<string> Find(string search) => Db.Search(new CardQuery { Text = search }).Select(e => e.Name).ToList();

    [Theory]
    [InlineData("cub", "Glade Cub")]
    [InlineData("\"glade cub\"", "Glade Cub")]
    [InlineData("!\"Glade Cub\"", "Glade Cub")]
    [InlineData("t:bear", "Glade Cub")]
    [InlineData("t:legendary t:wizard", "Azure Sage")]
    [InlineData("o:\"draw a card\"", "Azure Sage")]
    [InlineData("o:\"~ enters tapped\"", "Wild Grove")]
    [InlineData("o:\"~ enters, draw\"", "Azure Sage")]
    [InlineData("kw:flying", "Sky Lancer")]
    [InlineData("c:uw", "Azure Sage")]
    [InlineData("c:m", "Azure Sage")]
    [InlineData("c=2", "Azure Sage")]
    [InlineData("c:azorius", "Azure Sage")]
    [InlineData("id:c t:artifact", "Ruin Totem")]
    [InlineData("m:2WW", "Sky Lancer")]
    [InlineData("m>=RR", "Fire Giant")]
    [InlineData("mv>=6", "Fire Giant")]
    [InlineData("mv=3 c:c", "Ruin Totem")]
    [InlineData("pow>tou", "Sky Lancer")]
    [InlineData("tou>=4 t:creature -t:giant", "Azure Sage")]
    [InlineData("pt=4", "Glade Cub")]
    [InlineData("produces=gu", "Wild Grove")]
    [InlineData("produces:c", "Ruin Totem")]
    [InlineData("devotion:rr", "Fire Giant")]
    [InlineData("is:bear", "Glade Cub")]
    [InlineData("is:commander", "Azure Sage")]
    [InlineData("is:frenchvanilla t:knight", "Sky Lancer")]
    [InlineData("r:mythic", "Azure Sage")]
    [InlineData("s:new r:uncommon", "Glade Cub")]
    [InlineData("s:new cn>150", "Azure Sage")]
    [InlineData("in:uncommon", "Glade Cub")]
    [InlineData("banned:commander", "Ruin Totem")]
    [InlineData("sets=2", "Glade Cub")]
    [InlineData("year<2011 t:creature -t:knight -t:giant", "Glade Cub")]
    [InlineData("t:creature -t:human -t:giant", "Glade Cub")]
    [InlineData("t:instant or t:land", "Ember Bolt", "Wild Grove")]
    [InlineData("t:creature (c:r or c:g)", "Fire Giant", "Glade Cub")]
    [InlineData("-(t:creature or t:land or t:artifact)", "Ember Bolt")]
    [InlineData("o:/deals \\d damage/", "Ember Bolt")]
    [InlineData("name:/^fire/", "Fire Giant")]
    public void FindsWhatTheSearchAsksFor(string search, params string[] expected) =>
        Assert.Equal(expected.OrderBy(n => n), Find(search).OrderBy(n => n));

    [Fact]
    public void PrintingTermsMustHoldForTheSamePrinting()
    {
        Assert.Empty(Find("s:old r:uncommon")); // the old printing is common, the new one uncommon
        Assert.Equal(new[] { "Glade Cub" }, Find("s:old t:bear"));
    }

    [Fact]
    public void ACopyIsJudgedByItsOwnPrintingAndFinish()
    {
        var cub = Db.Find("Glade Cub")!;
        var filter = Db.Filter("s:new is:foil");
        Assert.True(filter.Matches(cub, "NEW", "140", foil: true));
        Assert.False(filter.Matches(cub, "NEW", "140", foil: false));
        Assert.False(filter.Matches(cub, "OLD", "12", foil: true));
    }

    [Fact]
    public void AScreensOwnNumberTermsLookAtTheItemSearched()
    {
        var terms = new SearchTerms(new Dictionary<string, Func<CardEntry, Printing?, object?, double?>>
        {
            ["usd"] = (_, _, item) => item is (double price, int) ? price : null,
            ["qty"] = (_, _, item) => item is (double, int copies) ? copies : null,
        }, "help");
        var cub = Db.Find("Glade Cub")!;
        var filter = Db.Filter("usd>=1.5 qty<3", terms);

        Assert.Empty(filter.Warnings);
        Assert.True(filter.Matches(cub, null, null, (2.0, 2)));
        Assert.False(filter.Matches(cub, null, null, (1.0, 2)));
        Assert.False(filter.Matches(cub, null, null, (2.0, 3)));
        Assert.True(filter.Matches(cub, null, null, "no values")); // a term without a value finds the card
        Assert.Empty(Db.Filter("usd>=1.5").Warnings); // without the screen's terms, the printings' own prices
        Assert.Single(Db.Filter("qty>=abc", terms).Warnings);
    }

    [Fact]
    public void UnknownOrUnavailableTermsAreIgnoredWithAWarning()
    {
        var filter = Db.Filter("t:bear a:someone frob:x");
        Assert.Equal(2, filter.Warnings.Count);
        Assert.Equal(new[] { "Glade Cub" }, Find("t:bear a:someone frob:x"));
        Assert.Empty(Db.Filter("t:bear c:rg").Warnings);
    }

    [Fact]
    public void SortsWhenAsked()
    {
        Assert.Equal(new[] { "Fire Giant", "Sky Lancer", "Azure Sage", "Glade Cub" }, Find("t:creature order:mv direction:desc"));
        Assert.Equal(new[] { "Azure Sage", "Fire Giant", "Glade Cub", "Sky Lancer" }, Find("t:creature"));
    }

    private static List<string> FindIn(CardDatabase db, string search) => db.Search(new CardQuery { Text = search }).Select(e => e.Name).ToList();

    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    private static Printing Priced(Printing p, double? eur, double? eurFoil, double? usd, double? usdFoil) =>
        p with { Prices = new PrintingPrices(eur, eurFoil, usd, usdFoil) };

    // Invented cards with listed prices (euros and dollars, nonfoil and foil).
    private static readonly CardDatabase Market = new(new[]
    {
        Card("Copper Ox", "Creature — Ox", "{3}{R}", new[] { "R" }, p: "3", t: "3",
            printings: new[] { Priced(Print("pri", "5", "rare", "2024-01-01"), 1.50, 3.00, 2.00, 3.00) }),
        Card("Tin Hound", "Creature — Dog", "{1}{B}", new[] { "B" }, p: "1", t: "1",
            printings: new[] { Priced(Print("pri", "9", "common", "2024-01-01"), 0.80, null, null, 0.90) }),
        Card("Ember Crown", "Artifact", "{2}", Array.Empty<string>(),
            printings: new[] { Priced(Print("pri", "12", "mythic", "2024-01-01"), null, null, 10.00, null) }),
        Card("Ash Relic", "Artifact", "{1}", Array.Empty<string>()), // no price listed
    });

    [Theory]
    [InlineData("usd>=2", "Copper Ox", "Ember Crown")]
    [InlineData("usd<1", "Tin Hound")]
    [InlineData("usd:0.9", "Tin Hound")]
    [InlineData("usd<=2", "Copper Ox", "Tin Hound")]
    [InlineData("eur<1", "Tin Hound")]
    [InlineData("eur>=1", "Copper Ox")]
    [InlineData("eur:1.5", "Copper Ox")]
    public void PriceTermsReadTheListedPrice(string search, params string[] expected) =>
        Assert.Equal(expected.OrderBy(n => n), FindIn(Market, search).OrderBy(n => n));

    [Fact]
    public void PriceTermsAskForTheFoilCopyWhenTheSearchKnowsIt()
    {
        var ox = Market.Find("Copper Ox")!;
        var filter = Market.Filter("usd>2.5");
        Assert.False(filter.Matches(ox, ox.Record.Printings[0], foil: false)); // 2.00 as a nonfoil
        Assert.True(filter.Matches(ox, ox.Record.Printings[0], foil: true)); // 3.00 as a foil

        var tin = Market.Find("Tin Hound")!;
        Assert.False(Market.Filter("usd<1").Matches(tin, tin.Record.Printings[0], foil: false)); // no nonfoil price listed
        Assert.True(Market.Filter("usd<1").Matches(tin, tin.Record.Printings[0], foil: true));
    }

    [Fact]
    public void PriceOrdersPutUnpricedCardsLast()
    {
        Assert.Equal(new[] { "Tin Hound", "Copper Ox", "Ember Crown", "Ash Relic" }, FindIn(Market, "order:usd"));
        Assert.Equal(new[] { "Tin Hound", "Copper Ox", "Ash Relic", "Ember Crown" }, FindIn(Market, "order:eur"));
        Assert.Equal(new[] { "Ember Crown", "Copper Ox", "Tin Hound", "Ash Relic" }, FindIn(Market, "order:usd dir:desc"));
    }

    // Invented cards, each with one printing feature or card-level flag that the search asks about.
    private static readonly CardDatabase Printed = new(new[]
    {
        Card("Hollow Drake", "Creature — Drake", "{2}{U}", new[] { "U" }, p: "2", t: "2",
            printings: new[] { Print("bdr", "20", "rare", "2021-01-01") with { Border = "borderless", Frame = "2015" } }),
        Card("Ivory Scribe", "Creature — Human", "{1}{W}", new[] { "W" }, p: "1", t: "1",
            printings: new[] { Print("bdr", "21", "common", "2021-01-01") with { Border = "black", Frame = "1997" } }),
        Card("Wicked Mask", "Enchantment", "{1}{B}", new[] { "B" },
            printings: new[] { Print("ext", "3", "uncommon", "2021-01-01") with { Frame = "2015", FrameEffects = new[] { "showcase" } } }),
        Card("Extended Tide", "Instant", "{U}", new[] { "U" },
            printings: new[] { Print("ext", "4", "rare", "2021-01-01") with { FrameEffects = new[] { "extendedart" } } }),
        Card("Etched Lantern", "Artifact", "{2}", Array.Empty<string>(),
            printings: new[] { Print("pri", "3", "uncommon", "2024-01-01") with { Etched = true } }),
        Card("Glossy Idol", "Artifact", "{1}", Array.Empty<string>(),
            printings: new[] { Print("pri", "4", "common", "2024-01-01") with { Glossy = true } }),
        Card("Broad Vista", "Sorcery", "{G}", new[] { "G" },
            printings: new[] { Print("pri", "6", "common", "2024-01-01") with { FullArt = true } }),
        Card("Blank Tablet", "Artifact", "{0}", Array.Empty<string>(),
            printings: new[] { Print("pri", "7", "common", "2024-01-01") with { Textless = true } }),
        Card("Sharp Scan", "Sorcery", "{R}", new[] { "R" },
            printings: new[] { Print("pri", "8", "common", "2024-01-01") with { HighRes = true } }),
        Card("Pale Herald", "Creature — Angel", "{3}{W}", new[] { "W" }, p: "3", t: "3",
            printings: new[] { Print("pro", "1", "rare", "2024-02-01", booster: false) with { PromoTypes = new[] { "prerelease" } } }),
        Card("Dated Charm", "Artifact", "{1}", Array.Empty<string>(),
            printings: new[] { Print("pro", "2", "rare", "2024-02-01", booster: false) with { PromoTypes = new[] { "datestamped" } } }),
        Card("Boosted Fang", "Creature — Wolf", "{1}{G}", new[] { "G" }, p: "2", t: "2",
            printings: new[] { Print("pri", "10", "rare", "2024-01-01") with { PromoTypes = new[] { "boosterfun" } } }),
        Card("Reserved Tome", "Artifact", "{4}", Array.Empty<string>()) with { Reserved = true },
        Card("Gamey Engine", "Artifact", "{5}", Array.Empty<string>()) with { GameChanger = true },
        Card("Old Standard", "Instant", "{1}{R}", new[] { "R" },
            printings: new[] { Print("old", "7", "common", "2000-01-01"), Print("new", "7", "common", "2020-01-01") }),
    });

    [Theory]
    [InlineData("border:borderless", "Hollow Drake")]
    [InlineData("is:borderless", "Hollow Drake")]
    [InlineData("border:black", "Ivory Scribe")]
    [InlineData("frame:2015", "Hollow Drake", "Wicked Mask")]
    [InlineData("frame:1997", "Ivory Scribe")]
    [InlineData("frame:showcase", "Wicked Mask")]
    [InlineData("is:showcase", "Wicked Mask")]
    [InlineData("is:extendedart", "Extended Tide")]
    [InlineData("is:etched", "Etched Lantern")]
    [InlineData("is:glossy", "Glossy Idol")]
    [InlineData("is:fullart", "Broad Vista")]
    [InlineData("is:textless", "Blank Tablet")]
    [InlineData("is:hires", "Sharp Scan")]
    [InlineData("is:prerelease", "Pale Herald")]
    [InlineData("is:datestamped", "Dated Charm")]
    [InlineData("is:boosterfun", "Boosted Fang")]
    [InlineData("is:reserved", "Reserved Tome")]
    [InlineData("is:gamechanger", "Gamey Engine")]
    [InlineData("is:reprint", "Old Standard")]
    public void PrintingAndCardTermsFindTheFeatureTheyName(string search, params string[] expected) =>
        Assert.Equal(expected.OrderBy(n => n), FindIn(Printed, search).OrderBy(n => n));

    [Fact]
    public void FirstPrintingIsTheOldestOne()
    {
        var card = Printed.Find("Old Standard")!;
        var filter = Printed.Filter("is:firstprint");
        Assert.True(filter.Matches(card, card.Record.Printings[0], null));
        Assert.False(filter.Matches(card, card.Record.Printings[1], null));
    }

    // Invented cards with the source's curated tags: card-level by oracle id, printing-level by printing id.
    private static readonly CardDatabase Tagged = new(new[]
    {
        Card("Ember Gate", "Land — Mountain Plains", "", Array.Empty<string>()),
        Card("Three Crown", "Land — Plains Island Swamp", "", Array.Empty<string>()),
        Card("Plain Field", "Basic Land — Plains", "", Array.Empty<string>(), printings: new[]
        {
            Print("old", "1", "common", "1994-04-01") with { Frame = "1993" },
            Print("new", "2", "common", "2020-01-01") with { Frame = "2015", PromoTypes = new[] { "crossoverone", "promopack" } },
        }),
    })
    {
        SearchTags = new SearchTags(
            new Dictionary<string, string> { ["triome"] = "tricycleland", ["snarl"] = "shadowland" },
            new Dictionary<string, IReadOnlySet<string>> { ["shockland"] = new HashSet<string> { "o-Ember Gate" }, ["tricycleland"] = new HashSet<string> { "o-Three Crown" } },
            new Dictionary<string, IReadOnlySet<string>> { ["atypical"] = new HashSet<string> { "new-2" } }),
    };

    [Theory]
    [InlineData("is:shockland", "Ember Gate")]
    [InlineData("is:tricycleland", "Three Crown")]
    [InlineData("is:triome", "Three Crown")] // an alias
    [InlineData("-is:shockland t:land", "Three Crown", "Plain Field")]
    [InlineData("is:atypical", "Plain Field")]
    [InlineData("is:default", "Ember Gate", "Three Crown", "Plain Field")] // every card has a printing that isn't atypical
    [InlineData("is:crossoverone", "Plain Field")] // any promo kind the printings have
    [InlineData("is:promo_pack", "Plain Field")]
    [InlineData("is:old", "Plain Field")]
    [InlineData("is:new", "Plain Field")]
    [InlineData("is:old is:atypical")]
    public void CuratedTagsFindExactlyTheTaggedCards(string search, params string[] expected) =>
        Assert.Equal(expected.OrderBy(n => n), FindIn(Tagged, search).OrderBy(n => n));

    [Fact]
    public void CuratedValuesWithoutTagsAreReported()
    {
        Assert.Contains("no data", Assert.Single(Tagged.Filter("is:snarl").Warnings)); // an alias of a value with no list here
        Assert.Contains("no data", Assert.Single(Db.Filter("is:shockland").Warnings));
        Assert.Contains("doesn't know", Assert.Single(Db.Filter("is:nothinglikeit").Warnings));
        var plain = Tagged.Find("Plain Field")!;
        Assert.False(Tagged.Filter("is:default").Matches(plain, plain.Record.Printings[1], null));
    }

    [Fact]
    public void AlchemyTermMatchesNothing()
    {
        Assert.Empty(FindIn(Tagged, "is:alchemy"));
        Assert.Empty(FindIn(Printed, "is:alchemy"));
    }

    [Fact]
    public void CompactFormatRoundTripsPrintingDetails()
    {
        var card = Card("Copper Ox", "Creature — Ox", "{3}{R}", new[] { "R" }, p: "3", t: "3", printings: new[]
        {
            Print("pri", "5", "rare", "2024-01-01") with
            {
                Prices = new PrintingPrices(1.50, 3.00, 2.00, null), Etched = true, Glossy = true, FullArt = true, Textless = true,
                HighRes = true, Border = "borderless", Frame = "2015", FrameEffects = new[] { "showcase" },
                PromoTypes = new[] { "prerelease", "boosterfun" }, Illustration = "art-1",
            },
            Print("pri", "6", "common", "2024-01-01", foil: false, booster: false),
        }) with { Reserved = true, GameChanger = true };

        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(new[] { card }, ms);
        ms.Position = 0;
        var back = Assert.Single(OracleJsonl.ReadCompact(ms));

        Assert.True(back.Reserved);
        Assert.True(back.GameChanger);
        Assert.Equal(2, back.Printings.Count);
        var p = back.Printings[0];
        Assert.Equal(card.Printings[0].Prices, p.Prices);
        Assert.True(p.Etched && p.Glossy && p.FullArt && p.Textless && p.HighRes);
        Assert.Equal("borderless", p.Border);
        Assert.Equal("2015", p.Frame);
        Assert.Equal(new[] { "showcase" }, p.FrameEffects);
        Assert.Equal(new[] { "prerelease", "boosterfun" }, p.PromoTypes);
        Assert.Equal("art-1", p.Illustration);
        Assert.True(p.Foil);
        var plain = back.Printings[1];
        Assert.Equal(PrintingPrices.None, plain.Prices);
        Assert.False(plain.Foil || plain.Booster || plain.Etched || plain.FullArt || plain.Textless || plain.HighRes);
        Assert.Equal("", plain.Border);
        Assert.Empty(plain.PromoTypes);
    }

    [Fact]
    public void ImportedPrintingsReadTheirPriceStrings()
    {
        const string lines = """
            {"oracle_id":"o-1","id":"p-1","name":"Copper Ox","set":"pri","set_name":"Priced Set","collector_number":"5","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","booster":true,"lang":"en","finishes":["nonfoil","foil"],"prices":{"eur":"1.50","eur_foil":"3.00","usd":"2.00","usd_foil":null}}
            {"oracle_id":"o-1","id":"p-2","name":"Copper Ox","set":"pri","set_name":"Priced Set","collector_number":"6","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"],"prices":{"eur":null,"eur_foil":"0.25","usd":null,"usd_foil":"0.30"}}
            {"oracle_id":"o-2","id":"p-3","name":"Tin Hound","set":"pri","set_name":"Priced Set","collector_number":"9","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["nonfoil"]}
            """;
        var printings = OracleJsonl.ImportPrintings(Text(lines));
        Assert.Equal(new PrintingPrices(1.50, 3.00, 2.00, null), printings["o-1"][0].Prices);
        Assert.Equal(new PrintingPrices(null, 0.25, null, 0.30), printings["o-1"][1].Prices);
        Assert.Equal(PrintingPrices.None, printings["o-2"][0].Prices);
    }
}
