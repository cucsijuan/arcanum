// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

/// <summary>Booster definitions and opening boosters.</summary>
public class BoosterTests
{
    private static string Card(int i, string type = "Creature — Bear") =>
        $$"""{"oracle_id":"o-{{i}}","name":"Card {{i}}","layout":"normal","mana_cost":"{1}","type_line":"{{type}}","oracle_text":"","power":"1","toughness":"1"}""";

    private static string Print(int i, string rarity, bool booster = true, string set = "abc", int? number = null) =>
        $$"""{"oracle_id":"o-{{i}}","id":"p-{{set}}-{{i}}","set":"{{set}}","set_name":"Set","collector_number":"{{number ?? i}}","rarity":"{{rarity}}","released_at":"2024-01-01","booster":{{(booster ? "true" : "false")}},"lang":"en"}""";

    /// <summary>10 commons (1–10), 5 uncommons (11–15), 2 rares (16–17), 1 mythic (18), a land (19, two arts), a card outside boosters (20), a guest from another set (21).</summary>
    private static CardDatabase Database()
    {
        var cards = Enumerable.Range(1, 18).Select(i => Card(i)).Append(Card(19, "Basic Land — Forest")).Append(Card(20)).Append(Card(21));
        var prints = Enumerable.Range(1, 10).Select(i => Print(i, "common"))
            .Concat(Enumerable.Range(11, 5).Select(i => Print(i, "uncommon")))
            .Concat(new[] { Print(16, "rare"), Print(17, "rare"), Print(18, "mythic"), Print(19, "common"), Print(19, "common", number: 90), Print(20, "rare", booster: false), Print(21, "rare", set: "gst", number: 7) });
        static Stream Text(IEnumerable<string> lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        var printings = OracleJsonl.ImportPrintings(Text(prints));
        return new CardDatabase(OracleJsonl.WithPrintings(OracleJsonl.Import(Text(cards)), printings));
    }

    private const string SetJson = """
        {
          "code": "abc", "name": "Alphabet",
          "booster": {
            "name": "Test Booster",
            "sheets": {
              "common": { "rarity": "common", "booster": true, "basic": false, "exclude": ["Card 10"] },
              "uncommon": { "rarity": "uncommon", "booster": true },
              "rare": { "rarity": "rare", "booster": true },
              "mythic": { "rarity": "mythic", "booster": true },
              "land": { "basic": true },
              "guest": { "set": "gst", "numbers": ["7"] }
            },
            "slots": [
              { "count": 5, "sheet": "common" },
              { "count": 3, "sheet": "uncommon" },
              { "count": 1, "sheets": { "rare": 7, "mythic": 1 } },
              { "count": 1, "sheet": "land" },
              { "count": 1, "sheets": { "common": 99, "guest": 1 } },
              { "count": 2, "wildcard": true, "sheets": { "common": 1, "uncommon": 1 } }
            ]
          },
          "limited": { "boostersPerPlayer": 3, "sealedBoosters": 6 },
          "cards": ["Card 1"]
        }
        """;

    [Fact]
    public void SheetsFollowTheDefinition()
    {
        var gen = new BoosterGenerator(SetDefinition.Parse(SetJson), Database());
        Assert.Equal(9, gen.Sheet("common").Count); // 1–9: card 10 excluded, the land is basic
        Assert.Equal(5, gen.Sheet("uncommon").Count);
        Assert.Equal(new[] { "Card 16", "Card 17" }, gen.Sheet("rare").Select(c => c.Name)); // 20 isn't in boosters
        Assert.Equal("gst", gen.Sheet("guest").Single().Set);
        Assert.Empty(gen.EmptySheets);
        Assert.Equal(13, gen.Set.Booster!.CardCount);
    }

    [Fact]
    public void BoostersHaveTheirSlotsAndNoRepeats()
    {
        var gen = new BoosterGenerator(SetDefinition.Parse(SetJson), Database());
        var random = new Random(7);
        int mythics = 0, guests = 0;
        var landArts = new HashSet<string>();
        for (int i = 0; i < 2000; i++)
        {
            var pack = gen.Open(random);
            Assert.Equal(13, pack.Count);
            var main = pack.Take(11).ToList();
            Assert.Equal(main.Count, main.Select(c => c.Name).Distinct().Count()); // no repeats outside wildcards
            Assert.Equal(3, pack.Skip(5).Take(3).Count(c => c.Rarity == "uncommon"));
            if (pack[8].Rarity == "mythic") mythics++;
            if (pack[10].Set == "gst") guests++;
            landArts.Add(pack[9].Number);
        }
        Assert.InRange(mythics, 150, 360); // 1 in 8
        Assert.InRange(guests, 5, 45); // 1 in 100
        Assert.Equal(new HashSet<string> { "19", "90" }, landArts); // both arts appear
    }

    [Fact]
    public void BoosterCardsKeepTheirPrinting()
    {
        var db = Database();
        var card = new BoosterGenerator(SetDefinition.Parse(SetJson), db).Sheet("guest").Single();
        var entry = card.ToEntry();
        Assert.Equal(("GST", "7"), (entry.Set, entry.Number));
        Assert.True(db.TryGet(entry.Name, entry.Set, entry.Number, out var def));
        Assert.Equal("p-gst-21", def.ImageKey);
    }

    /// <summary>
    /// Optional: with ARCANUM_MODULE_PATH, ARCANUM_CARD_FILE and ARCANUM_PRINTINGS_FILE set, every booster set of the
    /// module opens complete boosters from the real card data.
    /// </summary>
    [Fact]
    public void ModuleBoostersOpenWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        var cardFile = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        var printingsFile = Environment.GetEnvironmentVariable("ARCANUM_PRINTINGS_FILE");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(cardFile) || string.IsNullOrEmpty(printingsFile)) return;
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        Dictionary<string, List<Printing>> printings;
        using (var p = File.OpenRead(printingsFile)) printings = OracleJsonl.ImportPrintings(p);
        using var c = File.OpenRead(cardFile);
        var db = new CardDatabase(OracleJsonl.WithPrintings(OracleJsonl.Import(c, module.Sources.Cards.ToFilter()), printings), module.LoadScripts());
        var errors = new List<string>();
        foreach (var set in module.LoadSets(errors).Where(s => s.Booster is not null))
        {
            var gen = new BoosterGenerator(set, db);
            Assert.Empty(gen.EmptySheets);
            foreach (var (name, _) in set.Booster!.Sheets) Console.WriteLine($"{set.Code} sheet {name}: {gen.Sheet(name).Count} cards");
            var random = new Random(1);
            var unsupported = new HashSet<string>();
            for (int i = 0; i < 500; i++)
            {
                var pack = gen.Open(random);
                Assert.Equal(set.Booster.CardCount, pack.Count);
                foreach (var card in pack)
                    if (db.SupportOf(card.Name) != CardSupport.Full) unsupported.Add(card.Name);
            }
            Console.WriteLine($"{set.Code}: unsupported cards seen in boosters: {string.Join(", ", unsupported)}");
        }
        Assert.Empty(errors);
    }
}
