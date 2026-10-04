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
        {"oracle_id":"o-1","name":"Glade Cub","layout":"normal","mana_cost":"{1}{G}","type_line":"Creature — Bear","oracle_text":"","power":"2","toughness":"2","colors":["G"],"keywords":[],"games":["digital"],"legalities":{"standard":"legal","eternal":"legal"}}
        {"oracle_id":"o-2","name":"Forest","layout":"normal","mana_cost":"","type_line":"Basic Land — Forest","oracle_text":"({T}: Add {G}.)","games":["paper"]}
        {"oracle_id":"o-3","name":"Sky Lancer","layout":"normal","mana_cost":"{2}{W}","type_line":"Creature — Bird Soldier","oracle_text":"Flying","power":"2","toughness":"2","keywords":["Flying"],"games":["paper"]}
        {"oracle_id":"o-4","name":"Ember Sage","layout":"normal","mana_cost":"{1}{R}","type_line":"Creature — Human Shaman","oracle_text":"When this creature enters, it deals 1 damage to any target.","power":"1","toughness":"1","games":["paper"]}
        {"oracle_id":"o-5","name":"Old Art","layout":"art_series","type_line":"Card","games":["paper"]}
        {"oracle_id":"o-6","name":"Online Only","layout":"normal","type_line":"Creature","power":"1","toughness":"1","games":["digital"],"legalities":{"eternal":"not_legal"}}
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

    // Module-provided rules: keep cards legal somewhere in "eternal" (cards without legality data pass) and skip art cards.
    private static readonly ImportFilter Filter = new(new[] { "eternal" }, new[] { "art_series" });

    [Fact]
    public void ImportAppliesTheModuleFilter()
    {
        var records = OracleJsonl.Import(Gzip(Fixture), Filter).ToList();
        Assert.DoesNotContain(records, r => r.Name == "Old Art");
        Assert.DoesNotContain(records, r => r.Name == "Online Only");
        Assert.Contains(records, r => r.Name == "Glade Cub"); // the printing doesn't matter, the card's legality does
        Assert.Equal(7, records.Count);
        Assert.True(records.Single(r => r.Name == "Glade Cub Token").IsToken);
        Assert.Equal(2, records.Single(r => r.Name == "Twin Paths").Faces.Count);
    }

    [Fact]
    public void ImportAcceptsUncompressedInput()
    {
        var records = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(Fixture)), Filter).ToList();
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

    [Fact]
    public void PartnerWithLetsAPlayerFetchThePartner()
    {
        var friend = new CardRecord
        {
            OracleId = "o-f", Name = "Loyal Friend", Layout = "normal", TypeLine = "Legendary Creature — Halfling", ManaCost = "{1}{G}", Power = "2", Toughness = "2",
            OracleText = "Partner with Hobbit Hero (When this creature enters, target player may put Hobbit Hero into their hand from their library, then shuffle.)",
            Keywords = new[] { "Partner with", "Partner" },
        };
        var (definition, support) = CardFactory.Create(friend);
        Assert.Equal(CardSupport.Full, support);
        var trigger = Assert.Single(definition.Abilities.OfType<Arcanum.Engine.Abilities.TriggeredAbility>());
        Assert.Equal(Arcanum.Engine.Abilities.TriggerEvent.EntersBattlefield, trigger.Trigger);
        Assert.Equal(Arcanum.Engine.Abilities.TargetKind.Player, Assert.Single(trigger.Targets).Kind);
        var search = Assert.IsType<Arcanum.Engine.Abilities.SearchLibrary>(Assert.Single(trigger.Effects));
        Assert.Equal("Hobbit Hero", search.Filter.Name);
        Assert.True(search.Optional);
        Assert.Equal(Arcanum.Engine.Abilities.Subject.TargetAt(0), search.Who);
    }

    [Fact]
    public void HexproofFromIsNotPlainHexproof()
    {
        static CardRecord Knight(string text) => new()
        {
            OracleId = "o-k", Name = "Knight", Layout = "normal", TypeLine = "Creature — Knight", Power = "2", Toughness = "2",
            OracleText = text, Keywords = new[] { "First strike", "Hexproof from", "Hexproof" },
        };
        var partial = CardFactory.Create(Knight("First strike\nHexproof from black (This creature can't be the target of black spells or abilities your opponents control.)")).Definition;
        Assert.DoesNotContain("Hexproof", partial.Keywords);
        var both = CardFactory.Create(Knight("First strike, hexproof\nHexproof from black")).Definition;
        Assert.Contains("Hexproof", both.Keywords);
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

public class ModuleTests
{
    /// <summary>
    /// Optional check of a real content module: set ARCANUM_MODULE_PATH (and optionally ARCANUM_CARD_FILE to also
    /// report coverage). Every script must parse.
    /// </summary>
    [Fact]
    public void ModuleScriptsParseWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        if (string.IsNullOrEmpty(dir)) return;
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        var errors = new List<string>();
        var scripts = module.LoadScripts(errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
        Console.WriteLine($"scripts={scripts.Count}");

        var cardFile = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        if (string.IsNullOrEmpty(cardFile)) return;
        using var file = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), scripts);
        int supported = db.All.Count(d => db.SupportOf(d.Name) == CardSupport.Full);
        Console.WriteLine($"cards={db.Count} supported={supported}");

        // Sets: every card of a set the module describes is known and fully supported.
        foreach (var set in module.LoadSets())
        {
            var unknown = set.Cards.Where(name => db.Find(name) is null).ToList();
            var unsupported = set.Cards.Where(name => db.Find(name) is not null && db.SupportOf(name) != CardSupport.Full).ToList();
            Console.WriteLine($"set {set.Code}: {set.Cards.Count - unknown.Count - unsupported.Count}/{set.Cards.Count} supported"
                              + (unknown.Count > 0 ? $", unknown {string.Join(", ", unknown)}" : "")
                              + (unsupported.Count > 0 ? $", unsupported {string.Join(", ", unsupported)}" : ""));
            Assert.Empty(unknown);
            Assert.Empty(unsupported);
        }

        // Cubes: every card known and fully supported.
        foreach (var cubeName in module.CubeNames())
        {
            var unknown = new List<string>();
            var cube = Arcanum.Data.Limited.CubeBoosters.CardsFrom(Arcanum.Data.Decks.DeckList.Parse(module.ReadCube(cubeName)), db, unknown);
            var unsupported = cube.Where(c => db.SupportOf(c.Name) != CardSupport.Full).Select(c => c.Name).ToList();
            Console.WriteLine($"cube {cubeName}: {cube.Count} cards, unknown {string.Join(", ", unknown)}, unsupported {string.Join(", ", unsupported)}");
            Assert.Empty(unknown);
            Assert.Empty(unsupported);
        }

        // Starter decks must be legal in their format (commander decks: the module's commander format).
        var formats = module.LoadFormats();
        foreach (var name in module.DeckNames())
        {
            var deck = Arcanum.Data.Decks.DeckList.Parse(module.ReadDeck(name));
            var format = deck.Commander.Count > 0 ? formats.First(f => f.Commander) : formats.First();
            var problems = Arcanum.Data.Formats.DeckValidator.Validate(deck, format, db);
            var deckErrors = problems.Where(i => i.Severity == Arcanum.Data.Formats.IssueSeverity.Error).Select(i => i.Message).ToList();
            Console.WriteLine($"deck {name} ({format.Name}): {deckErrors.Count} errors, {problems.Count - deckErrors.Count} warnings {string.Join(" | ", deckErrors.Take(3))}");
            Assert.Empty(deckErrors);
        }
    }
}
