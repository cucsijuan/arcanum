// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.Tests;

public class ImportTests
{
    // Invented cards in the card source's JSON Lines shape.
    private const string Fixture = """
        {"oracle_id":"o-1","name":"Glade Cub","layout":"normal","mana_cost":"{1}{G}","type_line":"Creature — Bear","oracle_text":"","power":"2","toughness":"2","colors":["G"],"keywords":[],"games":["paper"],"legalities":{"standard":"legal"}}
        {"oracle_id":"o-2","name":"Forest","layout":"normal","mana_cost":"","type_line":"Basic Land — Forest","oracle_text":"({T}: Add {G}.)","games":["paper"]}
        {"oracle_id":"o-3","name":"Sky Lancer","layout":"normal","mana_cost":"{2}{W}","type_line":"Creature — Bird Soldier","oracle_text":"Flying","power":"2","toughness":"2","keywords":["Flying"],"games":["paper"]}
        {"oracle_id":"o-4","name":"Ember Sage","layout":"normal","mana_cost":"{1}{R}","type_line":"Creature — Human Shaman","oracle_text":"When this creature enters, it deals 1 damage to any target.","power":"1","toughness":"1","games":["paper"]}
        {"oracle_id":"o-5","name":"Old Art","layout":"art_series","type_line":"Card","games":["paper"]}
        {"oracle_id":"o-6","name":"Online Only","layout":"normal","type_line":"Creature","power":"1","toughness":"1","games":["digital"]}
        {"oracle_id":"o-7","name":"Twin Paths","layout":"modal_dfc","type_line":"Sorcery // Land","games":["paper"],"card_faces":[{"name":"Twin","mana_cost":"{G}","type_line":"Sorcery","oracle_text":"Draw a card."},{"name":"Paths","type_line":"Land","oracle_text":"{T}: Add {G}."}]}
        {"oracle_id":"o-8","name":"Wild Shape","layout":"normal","mana_cost":"{2}{G}","type_line":"Creature — Shapeshifter","power":"*","toughness":"*","games":["paper"]}
        {"oracle_id":"o-9","name":"Glade Cub Token","layout":"token","type_line":"Token Creature — Bear","power":"2","toughness":"2","games":["paper"]}
        """;

    private static Stream Gzip(string text)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(text));
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void ImportSkipsNonPlayableAndDigitalOnlyCards()
    {
        var records = OracleJsonl.Import(Gzip(Fixture)).ToList();
        Assert.DoesNotContain(records, r => r.Name == "Old Art");
        Assert.DoesNotContain(records, r => r.Name == "Online Only");
        Assert.Equal(7, records.Count);
        Assert.True(records.Single(r => r.Name == "Glade Cub Token").IsToken);
        Assert.Equal(2, records.Single(r => r.Name == "Twin Paths").Faces.Count);
    }

    [Fact]
    public void ImportAcceptsUncompressedInput()
    {
        var records = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(Fixture))).ToList();
        Assert.Equal(7, records.Count);
    }

    [Fact]
    public void CompactFormatRoundTrips()
    {
        var records = OracleJsonl.Import(Gzip(Fixture)).ToList();
        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(records, ms);
        ms.Position = 0;
        var back = OracleJsonl.ReadCompact(ms);
        Assert.Equal(records.Select(r => r.Name), back.Select(r => r.Name));
        var cub = back.Single(r => r.Name == "Glade Cub");
        Assert.Equal("{1}{G}", cub.ManaCost);
        Assert.Equal("legal", cub.Legalities["standard"]);
        Assert.True(back.Single(r => r.Name == "Glade Cub Token").IsToken);
    }

    [Theory]
    [InlineData("Legendary Creature — Human Wizard", Supertype.Legendary, CardType.Creature, "Human Wizard")]
    [InlineData("Basic Land — Forest", Supertype.Basic, CardType.Land, "Forest")]
    [InlineData("Artifact Creature — Golem", Supertype.None, CardType.Artifact | CardType.Creature, "Golem")]
    [InlineData("Instant", Supertype.None, CardType.Instant, "")]
    public void ParsesTypeLines(string line, Supertype supertypes, CardType types, string subtypes)
    {
        var parsed = TypeLine.Parse(line);
        Assert.Equal(supertypes, parsed.Supertypes);
        Assert.Equal(types, parsed.Types);
        Assert.Equal(subtypes, string.Join(" ", parsed.Subtypes));
    }

    [Fact]
    public void FactoryMarksSupportAccordingToEngineCapabilities()
    {
        var records = OracleJsonl.Import(Gzip(Fixture)).ToDictionary(r => r.Name);
        var (cub, cubSupport) = CardFactory.Create(records["Glade Cub"]);
        Assert.Equal(CardSupport.Full, cubSupport);
        Assert.Equal(2, cub.Power);
        Assert.Equal(ManaCost.Parse("{1}{G}"), cub.ManaCost);

        var (forest, forestSupport) = CardFactory.Create(records["Forest"]);
        Assert.Equal(CardSupport.Full, forestSupport);
        Assert.Equal(new[] { ManaType.Green }, forest.TapForMana);

        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(records["Ember Sage"]).Support); // needs a script
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(records["Twin Paths"]).Support); // two faces
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(records["Wild Shape"]).Support); // */* stats
    }

    [Fact]
    public void KeywordCardsBecomeSupportedOnceTheKeywordIs()
    {
        var lancer = OracleJsonl.Import(Gzip(Fixture)).Single(r => r.Name == "Sky Lancer");
        bool had = CardFactory.SupportedKeywords.Contains("Flying");
        try
        {
            CardFactory.SupportedKeywords.Remove("Flying");
            Assert.Equal(CardSupport.Unsupported, CardFactory.Create(lancer).Support);
            CardFactory.SupportedKeywords.Add("Flying");
            Assert.Equal(CardSupport.Full, CardFactory.Create(lancer).Support);
        }
        finally
        {
            if (!had) CardFactory.SupportedKeywords.Remove("Flying");
        }
    }

    [Fact]
    public void DeckListsParseAndResolve()
    {
        var db = new CardDatabase(OracleJsonl.Import(Gzip(Fixture)));
        var deck = DeckList.Parse("""
            # comment
            Deck
            20 Forest
            4x Glade Cub (ABC) 123
            1 Nonexistent Card

            Sideboard
            2 Sky Lancer
            """);
        Assert.Equal(3, deck.Main.Count);
        Assert.Single(deck.Sideboard);
        var (cards, unknown) = deck.Resolve(db);
        Assert.Equal(24, cards.Count);
        Assert.Equal(new[] { "Nonexistent Card" }, unknown);
        Assert.False(db.TryGet("Glade Cub Token", out _)); // tokens aren't deck cards
    }

    /// <summary>Optional check against a real downloaded card file: set ARCANUM_CARD_FILE to its path.</summary>
    [Fact]
    public void ImportsRealCardFileWhenProvided()
    {
        var path = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        if (string.IsNullOrEmpty(path)) return;
        using var file = File.OpenRead(path);
        var records = OracleJsonl.Import(file).ToList();
        var db = new CardDatabase(records);
        int supported = db.All.Count(d => db.SupportOf(d.Name) == CardSupport.Full);
        Console.WriteLine($"records={records.Count} cards={db.Count} supported={supported}");
        Assert.True(records.Count > 1000);
    }
}
