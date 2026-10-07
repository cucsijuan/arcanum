// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Modules;

public sealed record ModuleManifest(string Id, string Name, string Version, int ApiVersion, string License, string Description);

/// <param name="IncludeIfLegalIn">Keep a card only if it is legal, restricted or banned in one of these formats (empty: keep all).</param>
/// <param name="ExcludeLayouts">Card layouts that are not playable cards and are skipped on import.</param>
public sealed record CardSource(
    string Format, string Index, string DownloadField, string UpdatedField,
    IReadOnlyList<string> IncludeIfLegalIn, IReadOnlyList<string> ExcludeLayouts)
{
    public Arcanum.Data.CardData.ImportFilter ToFilter() => new(IncludeIfLegalIn, ExcludeLayouts);
}

/// <param name="ByIdTemplate">URL for an exact image id ({id}), used when a name is ambiguous (tokens).</param>
/// <param name="BackUrlTemplate">URL of a double-faced card's back face picture by name ({name}); without it the back shows as text.</param>
/// <param name="BackByIdTemplate">URL of a double-faced printing's back face picture by its image id ({id}).</param>
public sealed record ImageSource(string UrlTemplate, int MinIntervalMs, string? ByIdTemplate = null, string? BackUrlTemplate = null, string? BackByIdTemplate = null);

/// <summary>Where every printing of every card comes from (sets, collector numbers, rarities, exact images).</summary>
public sealed record PrintingSource(string Index, string DownloadField);

/// <param name="Printings">Optional: without it cards have no set information and show their default picture.</param>
public sealed record ModuleSources(string UserAgent, CardSource Cards, ImageSource Images, PrintingSource? Printings = null);

/// <summary>
/// A content module installed on disk: where card data and images come from, card scripts, formats and decks.
/// Modules are data only (JSON, text, Lua), so the same module works on every platform.
/// </summary>
public sealed class ContentModule
{
    /// <summary>Engine API version this build understands; modules declaring a newer one are rejected.</summary>
    public const int SupportedApiVersion = 4;

    public string Directory { get; }
    public ModuleManifest Manifest { get; }
    public ModuleSources Sources { get; }

    private ContentModule(string directory, ModuleManifest manifest, ModuleSources sources)
    {
        Directory = directory;
        Manifest = manifest;
        Sources = sources;
    }

    public static ContentModule Load(string directory)
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var m = manifestDoc.RootElement;
        var manifest = new ModuleManifest(
            Required(m, "id"), Required(m, "name"), Required(m, "version"),
            m.GetProperty("apiVersion").GetInt32(), Required(m, "license"), Optional(m, "description") ?? "");
        if (manifest.ApiVersion > SupportedApiVersion)
            throw new InvalidDataException($"Module '{manifest.Id}' needs engine API {manifest.ApiVersion}; this build supports {SupportedApiVersion}.");

        using var sourcesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sources.json")));
        var s = sourcesDoc.RootElement;
        var cards = s.GetProperty("cards");
        var images = s.GetProperty("images");
        var sources = new ModuleSources(
            Optional(s, "userAgent") ?? "Arcanum",
            new CardSource(Required(cards, "format"), Required(cards, "index"), Required(cards, "downloadField"), Required(cards, "updatedField"),
                StringList(cards, "includeIfLegalIn"), StringList(cards, "excludeLayouts")),
            new ImageSource(Required(images, "urlTemplate"), images.TryGetProperty("minIntervalMs", out var ms) ? ms.GetInt32() : 100,
                Optional(images, "byIdTemplate"), Optional(images, "backUrlTemplate"), Optional(images, "backByIdTemplate")),
            s.TryGetProperty("printings", out var printings)
                ? new PrintingSource(Required(printings, "index"), Required(printings, "downloadField"))
                : null);

        return new ContentModule(directory, manifest, sources);
    }

    public IEnumerable<string> DeckNames() =>
        System.IO.Directory.Exists(DecksDirectory)
            ? System.IO.Directory.EnumerateFiles(DecksDirectory, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()
            : Enumerable.Empty<string>();

    public string ReadDeck(string name) => File.ReadAllText(Path.Combine(DecksDirectory, name + ".txt"));

    /// <summary>Cube lists in cubes/*.txt (deck-list format: one line per card, printing optional).</summary>
    public IEnumerable<string> CubeNames() =>
        System.IO.Directory.Exists(CubesDirectory)
            ? System.IO.Directory.EnumerateFiles(CubesDirectory, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()
            : Enumerable.Empty<string>();

    public string ReadCube(string name) => File.ReadAllText(Path.Combine(CubesDirectory, name + ".txt"));

    private string CubesDirectory => Path.Combine(Directory, "cubes");

    /// <summary>
    /// Card scripts in scripts/*.json, keyed by oracle id (the file name). Scripts that fail to parse are reported
    /// through <paramref name="errors"/> and skipped, so one bad script never breaks the whole module.
    /// </summary>
    public Dictionary<string, Scripts.CardScript> LoadScripts(List<string>? errors = null)
    {
        var scripts = new Dictionary<string, Scripts.CardScript>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(Directory, "scripts");
        if (!System.IO.Directory.Exists(dir)) return scripts;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                scripts[Path.GetFileNameWithoutExtension(file)] = Scripts.CardScriptParser.Parse(File.ReadAllText(file));
            }
            // An unknown name (a replacement, a counter kind...) is a script error too, not a reason to fail loading the module.
            catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                errors?.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        return scripts;
    }

    /// <summary>Deck construction formats in formats/*.json.</summary>
    public List<Formats.FormatRules> LoadFormats(List<string>? errors = null)
    {
        var formats = new List<Formats.FormatRules>();
        var dir = Path.Combine(Directory, "formats");
        if (!System.IO.Directory.Exists(dir)) return formats;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json").Order())
        {
            try { formats.Add(Formats.FormatRules.Parse(File.ReadAllText(file))); }
            catch (Exception e) when (e is FormatException or JsonException) { errors?.Add($"{Path.GetFileName(file)}: {e.Message}"); }
        }
        return formats;
    }

    /// <summary>Card sets in sets/*.json (their cards, boosters and limited settings).</summary>
    public List<Limited.SetDefinition> LoadSets(List<string>? errors = null)
    {
        var sets = new List<Limited.SetDefinition>();
        var dir = Path.Combine(Directory, "sets");
        if (!System.IO.Directory.Exists(dir)) return sets;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json").Order())
        {
            try { sets.Add(Limited.SetDefinition.Parse(File.ReadAllText(file))); }
            catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { errors?.Add($"{Path.GetFileName(file)}: {e.Message}"); }
        }
        return sets;
    }

    public string ImageUrl(string cardName) => Sources.Images.UrlTemplate.Replace("{name}", Uri.EscapeDataString(cardName));

    /// <summary>URL for an exact image id, or null if the module's image source doesn't support it.</summary>
    public string? ImageUrlById(string id) => Sources.Images.ByIdTemplate?.Replace("{id}", Uri.EscapeDataString(id));

    /// <summary>URL of a double-faced card's back face picture, by the back face's name or by image id; null if the module has none.</summary>
    public string? BackImageUrl(string faceName) => Sources.Images.BackUrlTemplate?.Replace("{name}", Uri.EscapeDataString(faceName));
    public string? BackImageUrlById(string id) => Sources.Images.BackByIdTemplate?.Replace("{id}", Uri.EscapeDataString(id));

    private string DecksDirectory => Path.Combine(Directory, "decks");

    private static string Required(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
            ? s : throw new InvalidDataException($"Missing '{name}' in module file.");

    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static IReadOnlyList<string> StringList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : Array.Empty<string>();
}
