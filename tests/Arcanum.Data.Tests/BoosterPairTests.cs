// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Limited;

namespace Arcanum.Data.Tests;

/// <summary>Pair slots: a booster gives both cards of one of a sheet's pairs.</summary>
public class BoosterPairTests
{
    private static string Card(int i, string type = "Creature — Bear") =>
        $$"""{"oracle_id":"o-{{i}}","name":"Card {{i}}","layout":"normal","mana_cost":"{1}","type_line":"{{type}}","oracle_text":"","power":"1","toughness":"1"}""";

    private static string Print(int i, string rarity, string set = "abc") =>
        $$"""{"oracle_id":"o-{{i}}","id":"p-{{set}}-{{i}}","set":"{{set}}","set_name":"Set","collector_number":"{{i}}","rarity":"{{rarity}}","released_at":"2024-01-01","booster":true,"lang":"en","finishes":["nonfoil"]}""";

    /// <summary>Ten commons (1–10).</summary>
    private static CardDatabase Database()
    {
        var cards = Enumerable.Range(1, 10).Select(i => Card(i));
        var prints = Enumerable.Range(1, 10).Select(i => Print(i, "common"));
        static Stream Text(IEnumerable<string> lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        var printings = OracleJsonl.ImportPrintings(Text(prints));
        return new CardDatabase(OracleJsonl.WithPrintings(OracleJsonl.Import(Text(cards)), printings));
    }

    /// <summary>A pair slot of the "echo" sheet (two pairs of commons), then one common of the booster's own.</summary>
    private const string SetJson = """
        {
          "code": "abc", "name": "Alphabet",
          "booster": {
            "name": "Test Booster",
            "sheets": {
              "common": { "rarity": "common", "booster": true, "basic": false },
              "echo": { "rarity": "common", "booster": true, "basic": false, "pairs": [["Card 1", "Card 2"], ["Card 3", "Card 4"]] }
            },
            "slots": [
              { "count": 1, "pair": true, "sheets": { "echo": 1 } },
              { "count": 1, "sheet": "common" }
            ]
          },
          "cards": ["Card 1"]
        }
        """;

    [Fact]
    public void EveryBoosterHasBothCardsOfOnePairFirst()
    {
        var gen = new BoosterGenerator(SetDefinition.Parse(SetJson), Database());
        var random = new Random(5);
        var pairsSeen = new HashSet<string>();
        for (int i = 0; i < 500; i++)
        {
            var pack = gen.Open(random);
            Assert.Equal(3, pack.Count);
            var pair = pack.Take(2).Select(c => c.Name).ToList();
            Assert.Contains(string.Join("|", pair.OrderBy(n => n, StringComparer.Ordinal)), new[] { "Card 1|Card 2", "Card 3|Card 4" });
            pairsSeen.Add(string.Join("|", pair.OrderBy(n => n, StringComparer.Ordinal)));
            Assert.DoesNotContain(pack[2].Name, pair); // the common slot never repeats a card of the pair
        }
        Assert.Equal(2, pairsSeen.Count); // both pairs come up
    }

    [Fact]
    public void PairSlotCountsAsTwoCards()
    {
        var gen = new BoosterGenerator(SetDefinition.Parse(SetJson), Database());
        Assert.Equal(3, gen.Set.Booster!.CardCount); // the pair (2) and the common (1)
    }

    [Fact]
    public void PairSlotSheetWithoutPairsIsAFormatError()
    {
        const string json = """
            {
              "code": "abc", "name": "Alphabet",
              "booster": {
                "name": "Test Booster",
                "sheets": { "common": { "rarity": "common", "booster": true, "basic": false } },
                "slots": [ { "count": 1, "pair": true, "sheet": "common" } ]
              },
              "cards": ["Card 1"]
            }
            """;
        Assert.Throws<FormatException>(() => SetDefinition.Parse(json));
    }
}
