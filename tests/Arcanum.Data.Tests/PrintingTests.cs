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
        {"oracle_id":"o-tok","id":"t-old","name":"Insect","layout":"token","type_line":"Token Creature — Insect","oracle_text":"","power":"1","toughness":"1","colors":["G"]}
        {"oracle_id":"o-tok2","id":"t-big","name":"Insect","layout":"token","type_line":"Token Creature — Insect","oracle_text":"","power":"3","toughness":"3","colors":["G"]}
        {"oracle_id":"o-tok3","id":"t-fly","name":"Insect","layout":"token","type_line":"Token Creature — Insect","oracle_text":"Flying","power":"1","toughness":"1","colors":["G"],"keywords":["Flying"]}
        {"oracle_id":"o-4","id":"p-hatch","name":"Late Hatcher","layout":"normal","mana_cost":"{1}{G}","type_line":"Sorcery","oracle_text":"At the beginning of the next upkeep, create a 1/1 green Insect creature token with flying.","colors":["G"],"all_parts":[{"component":"token","id":"t-fly","name":"Insect","type_line":"Token Creature — Insect"}]}
        {"oracle_id":"o-3","id":"p-boat","name":"Drafted Skiff","layout":"normal","mana_cost":"{2}{U}","type_line":"Artifact","oracle_text":"Whenever you attack, recruit.","colors":["U"],"all_parts":[{"component":"token","id":"t-soldier","name":"Human Soldier","type_line":"Token Creature — Human Soldier"}]}
        """;

    private const string Printings = """
        {"oracle_id":"o-1","id":"p-new","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10","rarity":"common","released_at":"2024-01-01","set_type":"expansion","booster":true,"lang":"en","finishes":["nonfoil","foil"]}
        {"oracle_id":"o-1","id":"p-star","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10★","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"]}
        {"oracle_id":"o-1","id":"p-old","name":"Glade Cub","set":"old","set_name":"Old Set","collector_number":"2","rarity":"uncommon","released_at":"1999-01-01","set_type":"core","booster":true,"lang":"en","finishes":["nonfoil"]}
        {"oracle_id":"o-1","id":"p-alt","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"300","rarity":"common","released_at":"2024-01-01","set_type":"expansion","booster":false,"lang":"en"}
        {"oracle_id":"o-1","id":"p-online","name":"Glade Cub","set":"web","set_name":"Online","collector_number":"1","rarity":"common","released_at":"2025-01-01","set_type":"expansion","digital":true,"lang":"en"}
        {"oracle_id":"o-1","id":"p-replica","name":"Glade Cub","set":"rep","set_name":"Replicas","collector_number":"1","rarity":"common","released_at":"2025-01-01","set_type":"memorabilia","lang":"en"}
        {"oracle_id":"o-tok","id":"t-old","name":"Insect","layout":"token","set":"told","set_name":"Old Tokens","collector_number":"1","rarity":"common","released_at":"1999-01-01","set_type":"token","lang":"en"}
        {"oracle_id":"o-tok","id":"t-new","name":"Insect","layout":"token","set":"tnew","set_name":"New Tokens","collector_number":"4","rarity":"common","released_at":"2024-01-01","set_type":"token","lang":"en"}
        {"oracle_id":"o-tok","id":"t-replica","name":"Insect","layout":"token","set":"rep","set_name":"Replicas","collector_number":"9","rarity":"common","released_at":"2025-01-01","set_type":"memorabilia","lang":"en"}
        {"oracle_id":"o-2","id":"p-hive","name":"Hive Keeper","set":"new","set_name":"New Set","collector_number":"11","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","booster":true,"lang":"en","all_parts":[{"component":"token","id":"t-new","name":"Insect","type_line":"Token Creature — Insect"}]}
        """;

    private const string HiveScript = """{ "abilities": [ { "trigger": "enters", "effects": [ { "tokens": 1, "token": { "name": "Insect", "types": "Creature — Insect", "power": 1, "toughness": 1, "colors": ["G"] } } ] } ] }""";

    private const string HatcherScript = """{ "spell": { "effects": [ { "atNextUpkeep": { "effects": [ { "tokens": 1, "token": { "name": "Insect", "types": "Creature — Insect", "power": 1, "toughness": 1, "colors": ["G"], "keywords": ["Flying"] } } ], "text": "Create a 1/1 green Insect creature token with flying." } } ] } }""";

    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    private static CardDatabase Database()
    {
        var printings = OracleJsonl.ImportPrintings(Text(Printings));
        var records = OracleJsonl.WithPrintings(OracleJsonl.Import(Text(Cards)), printings).ToList();
        // Through the compact form, as the client stores it.
        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(records, ms);
        ms.Position = 0;
        return new CardDatabase(OracleJsonl.ReadCompact(ms), new Dictionary<string, CardScript>
        {
            ["o-2"] = CardScriptParser.Parse(HiveScript),
            ["o-4"] = CardScriptParser.Parse(HatcherScript),
            ["o-3"] = CardScriptParser.Parse("""{ "abilities": [ { "trigger": "youAttack", "effects": [ { "recruit": true } ] } ] }"""),
        });
    }

    [Fact]
    public void PaperPrintingsAreImportedOldestFirst()
    {
        var cub = Database().Find("Glade Cub")!.Record;
        Assert.Equal(new[] { "p-old", "p-new", "p-star", "p-alt" }, cub.Printings.Select(p => p.Id));
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
        Assert.Equal(new[] { "10", "10★", "11", "300" }, db.PrintingsIn("new").Select(x => x.Printing.CollectorNumber));
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

        Assert.True(db.TryGet("Drafted Skiff", null, null, out var skiff)); // the token recruit makes has its picture too
        Assert.Equal("t-soldier", skiff.Abilities.SelectMany(a => a.Effects).OfType<Arcanum.Engine.Abilities.Recruit>().Single().Token.ImageKey);
    }

    [Fact]
    public void TokensOfAKindHaveEveryPictureOfTheirPrintings()
    {
        var db = Database();
        var insect = CardDatabase.TokensMadeBy(db.Find("Hive Keeper")!.Definition).Single();
        Assert.Equal("Insect|1/1|G", CardDatabase.TokenKind(insect));
        // The 3/3 Insect is another kind; replicas aren't pictures of the token.
        Assert.Equal(new[] { "t-old", "t-new" }, db.TokenArts(insect).Select(a => a.Id));
        Assert.Empty(db.Search(new CardQuery { Text = "Insect" }).Where(e => e.Record.IsToken));
    }

    [Fact]
    public void ATokenMadeLaterHasItsPictureAndKeywordsTellPicturesApart()
    {
        var db = Database();
        // The token is made by a delayed trigger ("at the beginning of the next upkeep"): it still gets its picture.
        var insect = CardDatabase.TokensMadeBy(db.Find("Late Hatcher")!.Definition).Single();
        Assert.Equal("t-fly", insect.ImageKey);
        // A flying 1/1 Insect is only shown by flying Insect pictures, never by the plain ones of the same size and color.
        Assert.Equal(new[] { "t-fly" }, db.TokenArts(insect).Select(a => a.Id));
        var plain = DeckList.Parse("1 Hive Keeper\n1 Late Hatcher").Resolve(db).Cards;
        Assert.Equal("t-fly", CardDatabase.TokensMadeBy(plain.Single(c => c.Name == "Late Hatcher")).Single().ImageKey);
    }

    [Fact]
    public void ADeckShowsItsTokensWithThePicturesItChose()
    {
        var db = Database();
        var deck = DeckList.Parse("4 Hive Keeper\n# token: Insect|1/1|G = t-new\n");
        Assert.Equal(new[] { "t-new" }, deck.TokenArt["Insect|1/1|G"]);
        Assert.Contains("# token: Insect|1/1|G = t-new", deck.Export());
        Assert.Equal(new[] { "t-new" }, DeckList.Parse(deck.Export()).TokenArt["Insect|1/1|G"]);

        static string? TokenImage(Arcanum.Engine.Cards.CardDefinition d) =>
            d.Abilities.SelectMany(a => a.Effects).OfType<Arcanum.Engine.Abilities.CreateTokens>().Single().Token.ImageKey;
        Assert.All(deck.Resolve(db).Cards, c => Assert.Equal("t-new", TokenImage(c)));
        // With none chosen, every copy shows the same one of the token's pictures.
        var plain = DeckList.Parse("4 Hive Keeper").Resolve(db).Cards.Select(TokenImage).Distinct().ToList();
        Assert.Single(plain);
        Assert.Contains(plain[0], new[] { "t-old", "t-new" });
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
        // A printing made only in foil (a collector booster's) is kept: every copy of it is a foil.
        Assert.True(cub.FindPrinting("new", "10★")!.FoilOnly);
        Assert.False(cub.FindPrinting("new", "10")!.FoilOnly);
        Assert.True(db.TryGet("Glade Cub", "NEW", "10★", foil: false, out var star) && star.Foil);
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

    [Fact]
    public void FractureFoilPrintingsAreMarkedAndTheirCopiesShowIt()
    {
        const string fracture = """{"oracle_id":"o-1","id":"p-frac","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"20","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"],"promo_types":["fracturefoil"]}""";
        var printings = OracleJsonl.ImportPrintings(Text(Printings + "\n" + fracture));
        var records = OracleJsonl.WithPrintings(OracleJsonl.Import(Text(Cards)), printings).ToList();
        var db = new CardDatabase(records, new Dictionary<string, CardScript>());
        var cub = db.Find("Glade Cub")!.Record;
        Assert.True(cub.FindPrinting("new", "20")!.Fracture);
        Assert.False(cub.FindPrinting("new", "10★")!.Fracture);
        Assert.True(db.IsFracture("Glade Cub", "NEW", "20"));
        Assert.True(db.TryGet("Glade Cub", "NEW", "20", foil: false, out var copy) && copy.Foil && copy.Fracture);
        Assert.True(db.TryGet("Glade Cub", "NEW", "10", foil: true, out var plain) && !plain.Fracture);

        // The compact card file keeps the mark.
        var compact = new MemoryStream();
        OracleJsonl.WriteCompact(records, compact);
        compact.Position = 0;
        var again = new CardDatabase(OracleJsonl.ReadCompact(compact), new Dictionary<string, CardScript>());
        Assert.True(again.IsFracture("Glade Cub", "NEW", "20"));
    }

    [Fact]
    public void APrintingOnlyInAnotherLanguageIsKeptButTranslationsAreNot()
    {
        const string prints = """
            {"oracle_id":"o-1","id":"p-en","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["nonfoil"]}
            {"oracle_id":"o-1","id":"p-de","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"10","rarity":"common","released_at":"2024-01-01","set_type":"expansion","lang":"de","finishes":["nonfoil"]}
            {"oracle_id":"o-1","id":"p-elvish","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"408","rarity":"uncommon","released_at":"2024-01-01","set_type":"expansion","lang":"qya","finishes":["nonfoil"]}
            {"oracle_id":"o-1","id":"p-elvish-z","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"408z","rarity":"uncommon","released_at":"2024-01-01","set_type":"expansion","lang":"qya","finishes":["foil"]}
            """;
        var printings = OracleJsonl.ImportPrintings(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(prints)));
        Assert.Equal(new[] { "p-en", "p-elvish", "p-elvish-z" }, printings["o-1"].Select(p => p.Id));
        Assert.True(printings["o-1"].Single(p => p.Id == "p-elvish-z").FoilOnly);
    }

    [Fact]
    public void AFoilOnlyPrintingShowsThePlainPrintingOfItsArtwork()
    {
        const string cards = """{"oracle_id":"o-1","name":"Glade Cub","layout":"normal","mana_cost":"{1}{G}","type_line":"Creature — Bear","oracle_text":"","power":"2","toughness":"2"}""";
        const string prints = """
            {"oracle_id":"o-1","id":"p-plain","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"408","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["nonfoil"],"illustration_id":"art-1"}
            {"oracle_id":"o-1","id":"p-serial","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"408z","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"],"illustration_id":"art-1"}
            {"oracle_id":"o-1","id":"p-surge","name":"Glade Cub","set":"new","set_name":"New Set","collector_number":"500","rarity":"rare","released_at":"2024-01-01","set_type":"expansion","lang":"en","finishes":["foil"],"illustration_id":"art-2"}
            """;
        static Stream Text(string s) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s));
        var db = new CardDatabase(OracleJsonl.WithPrintings(OracleJsonl.Import(Text(cards)), OracleJsonl.ImportPrintings(Text(prints))));
        Assert.True(db.TryGet("Glade Cub", "NEW", "408z", false, out var serial));
        Assert.Equal("p-plain", serial.ImageKey); // the same artwork exists plain: its picture, foil drawn by the game
        Assert.True(serial.Foil);
        Assert.True(db.TryGet("Glade Cub", "NEW", "500", false, out var surge));
        Assert.Equal("p-surge", surge.ImageKey); // no plain printing of that artwork: its own picture
        Assert.True(surge.Foil);
    }
}
