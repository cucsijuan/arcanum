// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Limited;

/// <summary>
/// Which printings a booster sheet draws from. Empty fields don't filter. The set defaults to the set the booster
/// belongs to.
/// </summary>
public sealed record SheetSpec
{
    public string? Set { get; init; }
    public string? Rarity { get; init; }

    /// <summary>Only printings found in boosters (true) or only ones that aren't (false).</summary>
    public bool? Booster { get; init; }

    /// <summary>Only basic lands (true) or only non-basic cards (false).</summary>
    public bool? Basic { get; init; }

    /// <summary>Only these card names.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>Only these collector numbers (ranges like "1-181" are expanded when parsed).</summary>
    public IReadOnlyList<string>? Numbers { get; init; }

    /// <summary>Card names left out.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    /// <summary>The sheet's cards are traditional foils (only printings made in foil).</summary>
    public bool Foil { get; init; }

    /// <summary>The sheet's cards are surge foils (a foil treatment with its own pattern; implies <see cref="Foil"/>).</summary>
    public bool Surge { get; init; }
}

/// <summary>
/// A booster slot: <see cref="Count"/> cards, each from one of the sheets, chosen with the given relative weights.
/// Cards are never repeated within a booster except in wildcard slots, which can repeat what other slots gave.
/// </summary>
public sealed record SlotSpec(int Count, IReadOnlyDictionary<string, int> Sheets, bool Wildcard = false)
{
    /// <summary>Sheets of this slot whose card may repeat a card already in the booster (a traditional foil replacing a common).</summary>
    public IReadOnlyList<string> RepeatSheets { get; init; } = Array.Empty<string>();
}

/// <summary>One way a booster can be made up, and how often (relative weight).</summary>
public sealed record BoosterVariant(int Weight, IReadOnlyList<SlotSpec> Slots);

/// <param name="Slots">The booster's slots (its first variant's, when it has several).</param>
public sealed record BoosterSpec(string Name, string Description, IReadOnlyDictionary<string, SheetSpec> Sheets, IReadOnlyList<SlotSpec> Slots)
{
    public int CardCount => Slots.Sum(s => s.Count);

    /// <summary>The booster's variants (a single one of <see cref="Slots"/> when it has none).</summary>
    public IReadOnlyList<BoosterVariant> Variants { get; init; } = Array.Empty<BoosterVariant>();

    /// <summary>A picture of this kind of booster, when it differs from the set's pack picture.</summary>
    public string? PackImage { get; init; }

    /// <summary>The sealed ends of <see cref="PackImage"/> (top, then bottom), when given with it.</summary>
    public (double Top, double Bottom)? PackImageSeals { get; init; }
}

/// <summary>
/// A scene: cards of a set whose art joins edge to edge into one picture, laid out in rows.
/// </summary>
/// <param name="Columns">Cards across the picture.</param>
/// <param name="Cells">
/// Each place in the picture, row by row from the top left: the collector numbers of the set that fill it (the same art
/// may be printed under several numbers, as a surge foil for one).
/// </param>
public sealed record SceneSpec(string Name, int Columns, IReadOnlyList<IReadOnlyList<string>> Cells)
{
    public int Rows => (Cells.Count + Columns - 1) / Columns;
}

/// <summary>
/// A card set described by a content module's sets/*.json: its cards, how its boosters are made and how many
/// boosters limited events use.
/// </summary>
public sealed record SetDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string Released { get; init; } = "";
    public IReadOnlyList<string> Cards { get; init; } = Array.Empty<string>();
    public BoosterSpec? Booster { get; init; }

    /// <summary>
    /// The set's other boosters, as they were sold, by kind ("set", "collector"...); limited events use only
    /// <see cref="Booster"/> (the play or draft booster).
    /// </summary>
    public IReadOnlyDictionary<string, BoosterSpec> Boosters { get; init; } = new Dictionary<string, BoosterSpec>();

    /// <summary>The set's booster of a kind: <see cref="Booster"/> for null or "", otherwise one of <see cref="Boosters"/>.</summary>
    public BoosterSpec? BoosterOf(string? kind) => string.IsNullOrEmpty(kind) ? Booster : Boosters.GetValueOrDefault(kind);

    /// <summary>Boosters each player opens in a draft.</summary>
    public int BoostersPerPlayer { get; init; } = 3;

    /// <summary>Boosters in a sealed pool.</summary>
    public int SealedBoosters { get; init; } = 6;

    /// <summary>
    /// Picture of the set's booster pack, front only (downloaded when a pack is opened, never bundled), or null. It is
    /// pasted on the 3D pack: its top and bottom <see cref="PackImageSeals"/> are the sealed ends.
    /// </summary>
    public string? PackImage { get; init; }

    /// <summary>The sealed ends of <see cref="PackImage"/> as fractions of its height: top, then bottom.</summary>
    public (double Top, double Bottom) PackImageSeals { get; init; } = (0.07, 0.06);

    /// <summary>The set's scenes (cards whose art joins into one picture), as the collection shows them.</summary>
    public IReadOnlyList<SceneSpec> Scenes { get; init; } = Array.Empty<SceneSpec>();

    public static SetDefinition Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var e = doc.RootElement;
        var limited = e.TryGetProperty("limited", out var l) ? l : default;
        return new SetDefinition
        {
            Code = Str(e, "code") ?? throw new FormatException("Set needs a \"code\"."),
            Name = Str(e, "name") ?? throw new FormatException("Set needs a \"name\"."),
            Released = Str(e, "released") ?? "",
            Cards = Strings(e, "cards") ?? new List<string>(),
            Booster = e.TryGetProperty("booster", out var b) ? ParseBooster(b) : null,
            Boosters = e.TryGetProperty("boosters", out var more) && more.ValueKind == JsonValueKind.Object
                ? more.EnumerateObject().ToDictionary(k => k.Name, k => ParseBooster(k.Value))
                : new Dictionary<string, BoosterSpec>(),
            BoostersPerPlayer = limited.ValueKind == JsonValueKind.Object && limited.TryGetProperty("boostersPerPlayer", out var bp) ? bp.GetInt32() : 3,
            SealedBoosters = limited.ValueKind == JsonValueKind.Object && limited.TryGetProperty("sealedBoosters", out var sb) ? sb.GetInt32() : 6,
            PackImage = Pack(e) is { } p && p.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String ? image.GetString() : null,
            PackImageSeals = Seals(Pack(e)) ?? (0.07, 0.06),
            Scenes = e.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array
                ? scenes.EnumerateArray().Select(ParseScene).ToList()
                : Array.Empty<SceneSpec>(),
        };
    }

    private static SceneSpec ParseScene(JsonElement scene)
    {
        var name = Str(scene, "name") ?? throw new FormatException("A scene needs a \"name\".");
        if (!scene.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array || cards.GetArrayLength() == 0)
            throw new FormatException($"Scene '{name}' needs its \"cards\".");
        var cells = cards.EnumerateArray().Select(c => (IReadOnlyList<string>)(c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(n => n.GetString() ?? "").ToList()
            : new List<string> { c.GetString() ?? "" })).ToList();
        int columns = scene.TryGetProperty("columns", out var col) ? col.GetInt32() : cells.Count;
        if (columns < 1 || cells.Any(c => c.Count == 0 || c.Any(n => n.Length == 0)))
            throw new FormatException($"Scene '{name}' needs \"columns\" and a collector number in each place.");
        return new SceneSpec(name, columns, cells);
    }

    private static JsonElement? Pack(JsonElement set) =>
        set.TryGetProperty("pack", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    /// <summary>The pack picture's sealed ends, if given as two numbers; each kept within 0..0.3 so the body stays.</summary>
    private static (double, double)? Seals(JsonElement? pack) =>
        pack is { } p && p.TryGetProperty("seals", out var seals) && seals.ValueKind == JsonValueKind.Array && seals.GetArrayLength() == 2
        && seals[0].ValueKind == JsonValueKind.Number && seals[1].ValueKind == JsonValueKind.Number
            ? (Math.Clamp(seals[0].GetDouble(), 0, 0.3), Math.Clamp(seals[1].GetDouble(), 0, 0.3))
            : null;

    private static BoosterSpec ParseBooster(JsonElement b)
    {
        var sheets = new Dictionary<string, SheetSpec>();
        foreach (var sheet in b.GetProperty("sheets").EnumerateObject())
        {
            var s = sheet.Value;
            sheets[sheet.Name] = new SheetSpec
            {
                Set = Str(s, "set"),
                Rarity = Str(s, "rarity"),
                Booster = s.TryGetProperty("booster", out var bo) ? bo.GetBoolean() : null,
                Basic = s.TryGetProperty("basic", out var ba) ? ba.GetBoolean() : null,
                Names = Strings(s, "names"),
                Numbers = Strings(s, "numbers")?.SelectMany(ExpandRange).ToList(),
                Exclude = Strings(s, "exclude") ?? new List<string>(),
                Foil = s.TryGetProperty("foil", out var fo) && fo.GetBoolean() || s.TryGetProperty("surge", out var su) && su.GetBoolean(),
                Surge = s.TryGetProperty("surge", out var surge) && surge.GetBoolean(),
            };
        }
        // Several variants, each with a weight and its slots, or one list of slots.
        var variants = b.TryGetProperty("variants", out var vs)
            ? vs.EnumerateArray().Select(v => new BoosterVariant(v.TryGetProperty("weight", out var w) ? w.GetInt32() : 1, ParseSlots(v.GetProperty("slots"), sheets))).ToList()
            : new List<BoosterVariant> { new(1, ParseSlots(b.GetProperty("slots"), sheets)) };
        if (variants.Count == 0) throw new FormatException("A booster needs \"slots\" or \"variants\".");
        return new BoosterSpec(Str(b, "name") ?? "Booster", Str(b, "description") ?? "", sheets, variants[0].Slots)
        {
            Variants = variants,
            PackImage = b.TryGetProperty("pack", out var pk) && pk.ValueKind == JsonValueKind.Object ? Str(pk, "image") : null,
            PackImageSeals = b.TryGetProperty("pack", out var pack) && pack.ValueKind == JsonValueKind.Object ? Seals(pack) : null,
        };
    }

    private static List<SlotSpec> ParseSlots(JsonElement list, Dictionary<string, SheetSpec> sheets)
    {
        var slots = new List<SlotSpec>();
        foreach (var slot in list.EnumerateArray())
        {
            int count = slot.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            var weights = new Dictionary<string, int>();
            if (Str(slot, "sheet") is { } single) weights[single] = 1;
            if (slot.TryGetProperty("sheets", out var many))
                foreach (var w in many.EnumerateObject()) weights[w.Name] = w.Value.GetInt32();
            if (weights.Count == 0) throw new FormatException("A booster slot needs a \"sheet\" or \"sheets\".");
            foreach (var name in weights.Keys)
                if (!sheets.ContainsKey(name)) throw new FormatException($"Booster slot uses unknown sheet '{name}'.");
            slots.Add(new SlotSpec(count, weights, slot.TryGetProperty("wildcard", out var wc) && wc.GetBoolean())
            {
                RepeatSheets = slot.TryGetProperty("repeatSheets", out var rs) ? rs.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
            });
        }
        return slots;
    }

    /// <summary>A collector number, or a range of plain numbers ("1-181").</summary>
    private static IEnumerable<string> ExpandRange(string number)
    {
        var parts = number.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out int from) && int.TryParse(parts[1], out int to) && from <= to)
            return Enumerable.Range(from, to - from + 1).Select(n => n.ToString());
        return new[] { number };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
}
