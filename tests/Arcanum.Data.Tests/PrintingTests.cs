// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Scripts;

namespace Arcanum.Data.Tests;

/// <summary>Sets and printings: importing them, picking a printing's art and tokens, and deck lines that name one.</summary>
public class PrintingTests
{
    // Invented cards and printings in the card source's JSON Lines shape.
    private const string Cards = """
        {"oracle_id":"o-1","id":"p-default","name":"Glade Cub","layout":"normal","mana_cost":"{1}{G}","type_line":"Creature — Bear","oracle_text":"","power":"2","toughness":"2","colors":["G"]}
        {"oracle_id":"o-2","name":"Hive Keeper","layout":"normal","mana_cost":"{2}{G}","type_line":"Creature — Insect","oracle_text":"When this creature enters, create a 1/1 green Insect creature token.","power":"1","toughness":"1","colors":["G"],"all_parts":[{"component":"token","id":"t-old","name":"Insect","type_line":"Token Creature — Insect"}]}
        """;

    private const string Printings = """
        {"oracle_id":"o-1","id":"p-new","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10","rarity":"common","released_at":"2024-01-01","set_type":"expansion","booster":true,"lang":"en","finishes":["nonfoil","foil"]}
        {"oracle_id":"o-1","id":"p-star","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10★","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"]}
        {"oracle_id":"o-1","id":"p-old","name":"Glade Cub","set":"old","set_name":"Old Set","collector_number":"2","rarity":"uncommon","released_at":"1999-01-01","set_type":"core","booster":true,"lang":"en","finishes":["nonfoil"]}
        {"oracle_id":"o-1","id":"p-alt","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"300","rarity":"common","released_at":"2024-01-01","set_type":"expansion","booster":false,"lang":"en"}
        {"oracle_id":"o-1","id":"p-online","name":"Glade Cub","set":"web","set_name":"Online","collector_number":"1","rarity":"common","released_at":"2025-01-01","set_type":"expansion","digital":true,"lang":"en"}
        {"oracle_id":"o-1","id":"p-replica","name":"Glade Cub","set":"rep","set_name":"Replicas","collector_number":"1","rarity":"common","released_at":"2025-01-01","set_type":"memorabilia","lang":"en"}
        {"oracle_id":"o-2","id":"p-hive","name":"Hive Keeper","set":"new","set_name":"New Set","collector_number":"11","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","booster":true,"lang":"en","all_parts":[{"component":"token","id":"t-new","name":"Insect","type_line":"Token Creature — Insect"}]}
        """;

    private const string HiveScript = """{ "abilities": [ { "trigger": "enters", "effects": [ { "tokens": 1, "token": { "name": "Insect", "types": "Creature — Insect", "power": 1, "toughness": 1, "colors": ["G"] } } ] } ] }""";

    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    private static CardDatabase Database()
    {
        var printings = OracleJsonl.ImportPrintings(Text(Printings));
        var records = OracleJsonl.WithPrintings(OracleJsonl.Import(Text(Cards)), printings).ToList();
        // Through the compact form, as the client stores it.
        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(records, ms);
        ms.Position = 0;
        return new CardDatabase(OracleJsonl.ReadCompact(ms), new Dictionary<string, CardScript> { ["o-2"] = CardScriptParser.Parse(HiveScript) });
    }

    [Fact]
    public void PaperPrintingsAreImportedOldestFirst()
    {
        var cub = Database().Find("Glade Cub")!.Record;
        Assert.Equal(new[] { "p-old", "p-new", "p-alt" }, cub.Printings.Select(p => p.Id));
        Assert.Equal("p-default", cub.DefaultPrintingId);
        Assert.False(cub.FindPrinting("new", "300")!.Booster);
        Assert.Equal("uncommon", cub.FindPrinting("OLD")!.Rarity);
    }

    [Fact]
    public void SetsAreListedNewestFirstAndSearchable()
    {
        var db = Database();
        Assert.Equal(new[] { "new", "old" }, db.Sets.Select(s => s.Code));
        Assert.Equal("New Set", db.FindSet("NEW")!.Name);
        Assert.Equal(new[] { "Glade Cub" }, db.Search(new CardQuery { Set = "old" }).Select(e => e.Name));
        Assert.Equal(new[] { "10", "11", "300" }, db.PrintingsIn("new").Select(x => x.Printing.CollectorNumber));
    }

    [Fact]
    public void APrintingBringsItsPictureAndItsTokens()
    {
        var db = Database();
        Assert.True(db.TryGet("Glade Cub", "OLD", "2", out var old));
        Assert.Equal("p-old", old.ImageKey);
        Assert.True(db.TryGet("Glade Cub", null, null, out var plain));
        Assert.Null(plain.ImageKey);

        Assert.True(db.TryGet("Hive Keeper", null, null, out var hive));
        Assert.True(db.TryGet("Hive Keeper", "new", "11", out var hiveNew));
        static string? TokenImage(Arcanum.Engine.Cards.CardDefinition d) =>
            d.Abilities.SelectMany(a => a.Effects).OfType<Arcanum.Engine.Abilities.CreateTokens>().Single().Token.ImageKey;
        Assert.Equal("t-old", TokenImage(hive));
        Assert.Equal("t-new", TokenImage(hiveNew));
        Assert.Equal(hive.Abilities.Count, hiveNew.Abilities.Count); // same rules
    }

    [Fact]
    public void DeckLinesKeepTheirPrinting()
    {
        var db = Database();
        var deck = DeckList.Parse("""
            2 Glade Cub (OLD) 2
            1 Glade Cub (new) 10
            1 Glade Cub
            """);
        Assert.Equal(3, deck.Main.Count);
        Assert.Equal(new[] { "p-old", "p-old", "p-new", null }, deck.Resolve(db).Cards.Select(c => c.ImageKey));
        Assert.Contains("1 Glade Cub (NEW) 10", deck.Export());
        Assert.Equal(deck.Export(), DeckList.Parse(deck.Export()).Export());

        DeckList.Adjust(deck.Main, "Glade Cub", 1, "OLD", "2");
        Assert.Equal(3, deck.Main.Single(e => e.Set == "OLD").Count);
        DeckList.Adjust(deck.Main, "Glade Cub", -1);
        Assert.Equal(2, deck.Main.Count); // only the line without a printing went away
    }

    [Fact]
    public void FoilIsAFinishOfPrintingsThatWereMadeInFoil()
    {
        var db = Database();
        var cub = db.Find("Glade Cub")!.Record;
        // A printing made only in foil is not a printing to choose: its picture shows the foil.
        Assert.Null(cub.FindPrinting("new", "10★"));
        Assert.True(cub.FindPrinting("new", "10")!.Foil);
        Assert.False(cub.FindPrinting("old", "2")!.Foil);
        Assert.True(db.CanBeFoil("Glade Cub", "NEW", "10"));
        Assert.False(db.CanBeFoil("Glade Cub", "OLD", "2"));

        var deck = DeckList.Parse("""
            2 Glade Cub (NEW) 10 *F*
            1 Glade Cub (NEW) 10
            1 Glade Cub (OLD) 2 *F*
            """);
        Assert.Equal(new[] { true, false, true }, deck.Main.Select(e => e.Foil));
        // Foil only where the printing exists in foil; same picture either way.
        Assert.Equal(new[] { true, true, false, false }, deck.Resolve(db).Cards.Select(c => c.Foil));
        Assert.All(deck.Resolve(db).Cards.Take(3), c => Assert.Equal("p-new", c.ImageKey));
        Assert.Contains("2 Glade Cub (NEW) 10 *F*", deck.Export());
        Assert.Equal(deck.Export(), DeckList.Parse(deck.Export()).Export());

        // Foil and regular copies of a printing are separate lines.
        DeckList.Adjust(deck.Main, "Glade Cub", 1, "NEW", "10", foil: true);
        Assert.Equal(3, deck.Main.Single(e => e.Set == "NEW" && e.Foil).Count);
        Assert.Equal(1, deck.Main.Single(e => e.Set == "NEW" && !e.Foil).Count);
    }
}
