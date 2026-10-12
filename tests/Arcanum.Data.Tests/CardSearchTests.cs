// SPDX-License-Identifier: AGPL-3.0-or-later
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
}
