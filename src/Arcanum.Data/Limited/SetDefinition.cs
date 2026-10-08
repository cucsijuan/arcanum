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

public sealed record BoosterSpec(string Name, string Description, IReadOnlyDictionary<string, SheetSpec> Sheets, IReadOnlyList<SlotSpec> Slots)
{
    public int CardCount => Slots.Sum(s => s.Count);
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
            BoostersPerPlayer = limited.ValueKind == JsonValueKind.Object && limited.TryGetProperty("boostersPerPlayer", out var bp) ? bp.GetInt32() : 3,
            SealedBoosters = limited.ValueKind == JsonValueKind.Object && limited.TryGetProperty("sealedBoosters", out var sb) ? sb.GetInt32() : 6,
            PackImage = Pack(e) is { } p && p.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String ? image.GetString() : null,
            PackImageSeals = Seals(Pack(e)) ?? (0.07, 0.06),
        };
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
            };
        }
        var slots = new List<SlotSpec>();
        foreach (var slot in b.GetProperty("slots").EnumerateArray())
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
        return new BoosterSpec(Str(b, "name") ?? "Booster", Str(b, "description") ?? "", sheets, slots);
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
