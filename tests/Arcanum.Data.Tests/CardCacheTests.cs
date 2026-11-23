// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Tests;

/// <summary>The card cache gives back the database it was made from: every record field, every lookup, every definition.</summary>
public class CardCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanum-cache-" + Guid.NewGuid().ToString("N"));

    public CardCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Printing FullPrinting = new("abc", "Alpha Set", "12a", "rare", "p-1", "2020-01-02", "expansion", true,
        new[] { new RelatedToken("Spirit", "t-1", "Token Creature — Spirit") }, Foil: true)
    {
        FoilOnly = true,
        Fracture = true,
        Illustration = "ill-1",
        Prices = new PrintingPrices(1.5, null, 2.25, 9.75),
        Etched = true,
        Glossy = true,
        FullArt = true,
        Textless = true,
        HighRes = true,
        Border = "borderless",
        Frame = "2015",
        FrameEffects = new[] { "showcase", "extendedart" },
        PrintedName = "Printed Name",
        FlavorName = "Flavor Name",
        PromoTypes = new[] { "prerelease" },
    };

    private static readonly CardRecord FullRecord = new()
    {
        OracleId = "o-full",
        Name = "Test Giant // Stomp",
        Layout = "adventure",
        ManaCost = "{3}{G}",
        TypeLine = "Creature — Giant // Sorcery — Adventure",
        OracleText = "Line one\nLine two",
        Power = "4",
        Toughness = "5",
        Loyalty = "3",
        Colors = new[] { "G" },
        ColorIdentity = new[] { "G", "R" },
        Keywords = new[] { "Trample" },
        ProducedMana = new[] { "G" },
        Legalities = new Dictionary<string, string> { ["commander"] = "legal", ["vintage"] = "restricted" },
        Faces = new[]
        {
            new CardFaceRecord("Test Giant", "{3}{G}", "Creature — Giant", "Trample", "4", "5") { Loyalty = "2", Colors = new[] { "G" } },
            new CardFaceRecord("Stomp", "{1}{R}", "Sorcery — Adventure", "Stomp deals 2 damage to any target.", null, null),
        },
        IsToken = true,
        Reserved = true,
        GameChanger = true,
        RelatedTokens = new[] { new RelatedToken("Spirit", "t-1", "Token Creature — Spirit") },
        DefaultPrintingId = "p-1",
        Printings = new[] { FullPrinting, FullPrinting with { Set = "def", Prices = PrintingPrices.None } },
    };

    [Fact]
    public void EveryRecordFieldIsKept()
    {
        // A field added later fails here until it has a value above, and so until the cache stores it.
        foreach (var (type, value) in new (Type, object)[] { (typeof(CardRecord), FullRecord), (typeof(Printing), FullPrinting),
                     (typeof(CardFaceRecord), FullRecord.Faces[0]), (typeof(RelatedToken), FullRecord.RelatedTokens[0]), (typeof(PrintingPrices), FullPrinting.Prices) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null && p.Name != "EqualityContract"))
            {
                var v = property.GetValue(value);
                bool empty = v is null || v is false || v is "" || (v is System.Collections.ICollection c && c.Count == 0);
                if (type == typeof(PrintingPrices) && property.Name == nameof(PrintingPrices.EurFoil)) continue; // left null on purpose: a missing price
                Assert.False(empty, $"{type.Name}.{property.Name} has no value in the test record");
            }
        }

        var token = FullRecord with { OracleId = "o-token", Name = "Spirit", Layout = "token", Faces = Array.Empty<CardFaceRecord>() };
        var plain = new CardRecord { OracleId = "o-plain", Name = "Plain Bear", Layout = "normal", TypeLine = "Creature — Bear", Power = "2", Toughness = "2" };
        var cards = new CardDatabase(new[] { plain, token });
        var back = RoundTrip(cards, _ => null);

        Assert.Equal(Json(plain), Json(back.Find("Plain Bear")!.Record));
        Assert.Equal(Json(token), Json(back.TokensByName["Spirit"].Single()));
        Assert.Same(PrintingPrices.None, back.TokensByName["Spirit"].Single().Printings[1].Prices);
    }

    [Fact]
    public void LookupsAndDefinitionsMatchTheDatabaseTheyCameFrom()
    {
        var scripts = new Dictionary<string, CardScript>(StringComparer.OrdinalIgnoreCase)
        {
            ["o-melder-a"] = CardScriptParser.Parse("""{ "meldInto": "Combined Titan" }"""),
            ["o-melder-b"] = CardScriptParser.Parse("""{ "meldInto": "Combined Titan" }"""),
        };
        var records = new List<CardRecord>
        {
            new() { OracleId = "o-bear", Name = "Plain Bear", Layout = "normal", ManaCost = "{1}{G}", TypeLine = "Creature — Bear", Power = "2", Toughness = "2", Colors = new[] { "G" } },
            new() { OracleId = "o-land", Name = "Odd Land", Layout = "normal", TypeLine = "Land", OracleText = "{T}: Add {C}." },
            new() { OracleId = "o-melder-a", Name = "Melder A", Layout = "meld", ManaCost = "{4}", TypeLine = "Artifact Creature — Golem", Power = "3", Toughness = "3" },
            new() { OracleId = "o-melder-b", Name = "Melder B", Layout = "meld", ManaCost = "{5}", TypeLine = "Artifact Creature — Golem", Power = "4", Toughness = "4" },
            new() { OracleId = "o-combined", Name = "Combined Titan", Layout = "meld", TypeLine = "Artifact Creature — Golem", Power = "9", Toughness = "9" },
            FullRecord with { IsToken = false },
            new() { OracleId = "o-bear", Name = "Plain Bear", Layout = "normal", TypeLine = "Creature — Bear" }, // a second record with a known name is skipped
            new() { OracleId = "o-tok", Name = "Spirit", Layout = "token", TypeLine = "Token Creature — Spirit", IsToken = true },
        };
        var eager = new CardDatabase(records, scripts);
        var cached = RoundTrip(eager, id => scripts.GetValueOrDefault(id));

        Assert.Equal(eager.ByName.Keys, cached.ByName.Keys);
        Assert.Equal(eager.Sorted.Select(e => e.Name), cached.Sorted.Select(e => e.Name));
        Assert.Equal(eager.Count, cached.Count);
        Assert.Equal(eager.Sets, cached.Sets);
        Assert.Equal(eager.TokensByName.Keys, cached.TokensByName.Keys);
        Assert.Equal(eager.NonbasicLandNames, cached.NonbasicLandNames);
        Assert.Equal(eager.CreatureCardNames, cached.CreatureCardNames);
        Assert.Equal(eager.Names, cached.Names);
        Assert.False(cached.Find("Plain Bear")!.IsMade);
        foreach (var (name, entry) in eager.ByName)
        {
            var other = cached.ByName[name];
            Assert.Equal(entry.Name, other.Name);
            Assert.Equal(entry.Support, other.Support);
            Assert.Equal(Summary(entry.Definition), Summary(other.Definition));
        }
        Assert.True(cached.Find("Combined Titan")!.Definition.IsMeldResult);
        Assert.Same(cached.Find("Combined Titan")!.Definition, cached.Find("Melder A")!.Definition.MeldResult);
        Assert.Same(cached.Find("Test Giant"), cached.Find("Test Giant // Stomp"));
    }

    [Fact]
    public void ADatabaseMadeOnDemandIsTheOneMadeAtOnce()
    {
        var scripts = new Dictionary<string, CardScript>(StringComparer.OrdinalIgnoreCase)
        {
            ["o-melder-a"] = CardScriptParser.Parse("""{ "meldInto": "Combined Titan" }"""),
            ["o-melder-b"] = CardScriptParser.Parse("""{ "meldInto": "Combined Titan" }"""),
        };
        var records = new List<CardRecord>
        {
            new() { OracleId = "o-bear", Name = "Plain Bear", Layout = "normal", ManaCost = "{1}{G}", TypeLine = "Creature — Bear", Power = "2", Toughness = "2", Colors = new[] { "G" } },
            new() { OracleId = "o-land", Name = "Odd Land", Layout = "normal", TypeLine = "Land", OracleText = "{T}: Add {C}." },
            new() { OracleId = "o-melder-a", Name = "Melder A", Layout = "meld", ManaCost = "{4}", TypeLine = "Artifact Creature — Golem", Power = "3", Toughness = "3" },
            new() { OracleId = "o-melder-b", Name = "Melder B", Layout = "meld", ManaCost = "{5}", TypeLine = "Artifact Creature — Golem", Power = "4", Toughness = "4" },
            new() { OracleId = "o-combined", Name = "Combined Titan", Layout = "meld", TypeLine = "Artifact Creature — Golem", Power = "9", Toughness = "9" },
            FullRecord with { IsToken = false },
            new()
            {
                OracleId = "o-flip", Name = "Night Watcher // Day Hunter", Layout = "transform", ManaCost = "{2}{B}", TypeLine = "Creature — Human // Creature — Horror",
                Faces = new[]
                {
                    new CardFaceRecord("Night Watcher", "{2}{B}", "Creature — Human", "", "2", "2"),
                    new CardFaceRecord("Day Hunter", "", "Creature — Horror", "", "4", "4") { Colors = new[] { "B" } },
                },
            },
            new()
            {
                OracleId = "o-modal", Name = "Spring Path // Autumn Path", Layout = "modal_dfc", ManaCost = "{1}{G}", TypeLine = "Sorcery // Land",
                Faces = new[]
                {
                    new CardFaceRecord("Spring Path", "{1}{G}", "Sorcery", "Draw a card.", null, null),
                    new CardFaceRecord("Autumn Path", "", "Land", "{T}: Add {G}.", null, null),
                },
            },
            new()
            {
                OracleId = "o-split", Name = "Left Half // Right Half", Layout = "split", ManaCost = "{R} // {U}", TypeLine = "Instant // Instant",
                Faces = new[]
                {
                    new CardFaceRecord("Left Half", "{R}", "Instant", "Draw a card.", null, null),
                    new CardFaceRecord("Right Half", "{U}", "Instant", "Draw a card.", null, null),
                },
            },
            new() { OracleId = "o-bear", Name = "Plain Bear", Layout = "normal", TypeLine = "Creature — Bear" }, // a second record with a known name is skipped
            new() { OracleId = "o-tok", Name = "Spirit", Layout = "token", TypeLine = "Token Creature — Spirit", IsToken = true },
        };
        var eager = new CardDatabase(records, scripts);
        var onDemand = CardDatabase.MadeOnDemand(records, id => scripts.GetValueOrDefault(id));

        Assert.False(onDemand.Find("Plain Bear")!.IsMade);
        Assert.Equal(eager.ByName.Keys, onDemand.ByName.Keys);
        Assert.Equal(eager.Sorted.Select(e => e.Name), onDemand.Sorted.Select(e => e.Name));
        Assert.Equal(eager.Sets, onDemand.Sets);
        Assert.Equal(eager.TokensByName.Keys, onDemand.TokensByName.Keys);
        foreach (var (name, entry) in eager.ByName)
        {
            var other = onDemand.ByName[name];
            Assert.Equal(entry.Name, other.Name);
            Assert.Equal(entry.Support, other.Support);
            Assert.Equal(Summary(entry.Definition), Summary(other.Definition));
        }
        Assert.Equal(eager.NonbasicLandNames, onDemand.NonbasicLandNames);
        Assert.Equal(eager.CreatureCardNames, onDemand.CreatureCardNames);
        Assert.Equal(eager.Names, onDemand.Names);
        foreach (var record in records.Where(r => !r.IsToken))
            Assert.Equal(CardFactory.Create(record, scripts.GetValueOrDefault(record.OracleId)).Definition.Name, CardFactory.NameOf(record));

        // Kept once made, it reads back as the one made at once does.
        var cached = RoundTrip(onDemand, id => scripts.GetValueOrDefault(id));
        Assert.Equal(eager.Sorted.Select(e => e.Name), cached.Sorted.Select(e => e.Name));
        Assert.Same(cached.Find("Combined Titan")!.Definition, cached.Find("Melder A")!.Definition.MeldResult);
    }

    [Fact]
    public void RecordsAloneAreWrittenAndReadBack()
    {
        var token = FullRecord with { OracleId = "o-token", Name = "Spirit", Layout = "token", Faces = Array.Empty<CardFaceRecord>() };
        var records = new List<CardRecord> { FullRecord with { IsToken = false }, token };
        var path = Path.Combine(_dir, OracleJsonl.PreparedFileName);
        using (var output = File.Create(path)) CardCache.WriteRecords(records, output);
        Assert.True(CardCache.IsRecordsFile(path));
        Assert.Equal(records.Select(Json), CardCache.ReadRecords(path)!.Select(Json));

        // A card cache isn't a records file, and a damaged one reads as missing.
        var cache = Path.Combine(_dir, "cards.cache");
        CardCache.Write(new CardDatabase(records), cache, "key-1");
        Assert.False(CardCache.IsRecordsFile(cache));
        Assert.Null(CardCache.ReadRecords(cache));
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^3]);
        Assert.Null(CardCache.ReadRecords(path));
        Assert.Null(CardCache.ReadRecords(Path.Combine(_dir, "none.bin")));
    }

    [Fact]
    public void AnotherKeyOrADamagedFileReadsAsMissing()
    {
        var cards = new CardDatabase(new[] { new CardRecord { OracleId = "o", Name = "Plain Bear", Layout = "normal", TypeLine = "Creature — Bear" } });
        var path = Path.Combine(_dir, "cards.cache");
        CardCache.Write(cards, path, "key-1");
        Assert.NotNull(CardCache.Read(path, "key-1", _ => null));
        Assert.Null(CardCache.Read(path, "key-2", _ => null));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^3]);
        Assert.Null(CardCache.Read(path, "key-1", _ => null));
        Assert.Null(CardCache.Read(Path.Combine(_dir, "none.cache"), "key-1", _ => null));
    }

    [Fact]
    public void TheKeyChangesWithTheCardFileAndEveryScript()
    {
        var cardFile = Path.Combine(_dir, "cards.jsonl.gz");
        var scripts = Path.Combine(_dir, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(cardFile, "cards");
        File.WriteAllText(Path.Combine(scripts, "a.json"), "{}");
        var key = CardCache.Key(cardFile, scripts);
        Assert.Equal(key, CardCache.Key(cardFile, scripts));

        File.WriteAllText(Path.Combine(scripts, "a.json"), "{ }");
        var edited = CardCache.Key(cardFile, scripts);
        Assert.NotEqual(key, edited);
        File.WriteAllText(Path.Combine(scripts, "b.json"), "{}");
        var added = CardCache.Key(cardFile, scripts);
        Assert.NotEqual(edited, added);
        File.WriteAllText(cardFile, "cards, more");
        Assert.NotEqual(added, CardCache.Key(cardFile, scripts));
    }

    [Fact]
    public void WhereEveryScriptHasTheSameTimeTheKeyFollowsTheirContents()
    {
        // A read-only image stamps every file with the time it was mounted: the times change on every start while
        // nothing else does, and an edit keeps the size and the time.
        var cardFile = Path.Combine(_dir, "cards.jsonl.gz");
        var scripts = Path.Combine(_dir, "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(cardFile, "cards");
        File.WriteAllText(Path.Combine(scripts, "a.json"), """{ "x": 1 }""");
        File.WriteAllText(Path.Combine(scripts, "b.json"), """{ "y": 2 }""");
        void Stamp(DateTime time)
        {
            foreach (var file in Directory.EnumerateFiles(scripts)) File.SetLastWriteTimeUtc(file, time);
        }
        Stamp(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var key = CardCache.Key(cardFile, scripts);
        Stamp(new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(key, CardCache.Key(cardFile, scripts));

        File.WriteAllText(Path.Combine(scripts, "b.json"), """{ "y": 3 }""");
        Stamp(new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(key, CardCache.Key(cardFile, scripts));
    }

    /// <summary>
    /// With ARCANUM_MODULE_PATH and the game's card data for it: the cached database of the real cards gives the same
    /// records, lookups, support and definitions as the one made directly.
    /// </summary>
    [Fact]
    public void RealCardsRoundTripWhenProvided()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        if (string.IsNullOrEmpty(dir)) return;
        var cardFile = RealCardData.Find(dir);
        if (string.IsNullOrEmpty(cardFile)) return;
        var module = Arcanum.Data.Modules.ContentModule.Load(dir);
        List<CardRecord> records;
        using (var file = File.OpenRead(cardFile)) records = OracleJsonl.ReadCompact(file);
        var eager = new CardDatabase(records, module.LoadScripts());
        var cached = RoundTrip(eager, module.ScriptReader());

        Assert.Equal(eager.ByName.Keys, cached.ByName.Keys);
        Assert.Equal(eager.Sorted.Select(e => e.Name), cached.Sorted.Select(e => e.Name));
        Assert.Equal(eager.Sets, cached.Sets);
        Assert.Equal(eager.NonbasicLandNames, cached.NonbasicLandNames);
        Assert.Equal(eager.CreatureCardNames, cached.CreatureCardNames);
        cached.MakeDefinitionsAsync().Wait();
        foreach (var (name, entry) in eager.ByName)
        {
            var other = cached.ByName[name];
            Assert.Equal(Json(entry.Record), Json(other.Record));
            Assert.Equal(entry.Support, other.Support);
            Assert.Equal(Summary(entry.Definition), Summary(other.Definition));
        }
        foreach (var (name, tokens) in eager.TokensByName)
            Assert.Equal(tokens.Select(Json), cached.TokensByName[name].Select(Json));

        // The records as a module brings them, and the database made from them on demand.
        var prepared = Path.Combine(_dir, OracleJsonl.PreparedFileName);
        using (var output = File.Create(prepared)) CardCache.WriteRecords(records, output);
        var onDemand = CardDatabase.MadeOnDemand(CardCache.ReadRecords(prepared)!, module.ScriptReader());
        Assert.Equal(eager.ByName.Keys, onDemand.ByName.Keys);
        Assert.Equal(eager.Sorted.Select(e => e.Name), onDemand.Sorted.Select(e => e.Name));
        Assert.Equal(eager.Sets, onDemand.Sets);
        onDemand.MakeDefinitionsAsync().Wait();
        foreach (var (name, entry) in eager.ByName)
        {
            var other = onDemand.ByName[name];
            Assert.Equal(Json(entry.Record), Json(other.Record));
            Assert.Equal(entry.Name, other.Name);
            Assert.Equal(entry.Support, other.Support);
            Assert.Equal(Summary(entry.Definition), Summary(other.Definition));
        }
        Assert.Equal(eager.NonbasicLandNames, onDemand.NonbasicLandNames);
        Assert.Equal(eager.CreatureCardNames, onDemand.CreatureCardNames);
    }

    private CardDatabase RoundTrip(CardDatabase cards, Func<string, CardScript?> scriptFor)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cache");
        CardCache.Write(cards, path, "key");
        return CardCache.Read(path, "key", scriptFor) ?? throw new InvalidOperationException("The cache didn't read back.");
    }

    private static string Json(CardRecord record) => JsonSerializer.Serialize(record);

    private static string Summary(CardDefinition d) => string.Join("|", d.Name, d.Types, d.Supertypes, string.Join(" ", d.Subtypes), d.ManaCost,
        d.Power, d.Toughness, d.Loyalty, d.Abilities.Count, d.Spell is null, d.IsMeldResult, d.MeldResult?.Name, d.BackFace?.Name, d.Adventure?.Name,
        string.Join(" ", d.Keywords), d.OracleText);
}
