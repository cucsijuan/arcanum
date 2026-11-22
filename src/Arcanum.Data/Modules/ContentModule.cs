// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Modules;

/// <summary>
/// A module's manifest.json. <paramref name="Updates"/>: where the module's newest release is looked up (a "latest
/// release" document in the format of GitHub's releases API: tag_name, body, html_url, assets with name,
/// browser_download_url and size); none, and the game doesn't look for updates of it.
/// </summary>
public sealed record ModuleManifest(string Id, string Name, string Version, int ApiVersion, string License, string Description, string? Updates = null);

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
/// <param name="MinIntervalMs">How far apart requests to the templates above start.</param>
/// <param name="ByNameMinIntervalMs">How far apart the requests by name start, when the source allows fewer of them
/// (null: <paramref name="MinIntervalMs"/>).</param>
/// <param name="FileByIdTemplate">The picture file itself by image id, on a host without the request limits above
/// ({id}; {id0} and {id1} are its first and second characters); tried before <paramref name="ByIdTemplate"/>.</param>
/// <param name="FileBackByIdTemplate">The same for a double-faced printing's back face.</param>
/// <param name="FileMinIntervalMs">How far apart requests to the file host start.</param>
public sealed record ImageSource(string UrlTemplate, int MinIntervalMs, string? ByIdTemplate = null, string? BackUrlTemplate = null, string? BackByIdTemplate = null,
    int? ByNameMinIntervalMs = null, string? FileByIdTemplate = null, string? FileBackByIdTemplate = null, int FileMinIntervalMs = 0);

/// <summary>Where every printing of every card comes from (sets, collector numbers, rarities, exact images).</summary>
public sealed record PrintingSource(string Index, string DownloadField);

/// <summary>
/// A card back design the module offers beside the built-in ones: its picture is downloaded from <paramref name="Url"/>
/// when first shown and cached, like card images, so it is never bundled with the app or the module.
/// </summary>
public sealed record CardBackSource(string Id, string Name, string Url);

/// <summary>
/// Where the deck builder gets community statistics for a commander (<see cref="Decks.CommanderInsights"/>): the
/// commander's page and its average deck, by the commander's page name ({slug}; a theme or budget page is
/// "{slug}/theme"). Downloads are kept for <paramref name="CacheDays"/> days.
/// </summary>
public sealed record CommanderInsightSource(string PageTemplate, string AverageDeckTemplate, int CacheDays);

/// <summary>
/// Where the deck builder finds community decks like the player's, for formats other than Commander and limited
/// (<see cref="Decks.SimilarDecks"/>): a search for decks of a format ({format}, the source's id for it from
/// <paramref name="Formats"/>, by this game's format id) playing a card ({card}), the most viewed first; a search
/// without a card for an empty deck; and a deck's page ({id}). <paramref name="MinIntervalMs"/> spaces the requests;
/// downloads are kept for <paramref name="CacheDays"/> days.
/// </summary>
public sealed record DeckSearchSource(string SearchTemplate, string PopularTemplate, string DeckTemplate,
    IReadOnlyDictionary<string, string> Formats, int CacheDays, int MinIntervalMs);

/// <param name="Printings">Optional: without it cards have no set information and show their default picture.</param>
/// <param name="CardBacks">Optional card back designs offered in the settings beside the built-in ones.</param>
/// <param name="CommanderInsights">Optional: without it the deck builder offers no commander suggestions.</param>
/// <param name="DeckSearch">Optional: without it the deck builder offers no suggestions for other formats.</param>
public sealed record ModuleSources(string UserAgent, CardSource Cards, ImageSource Images, PrintingSource? Printings = null,
    IReadOnlyList<CardBackSource>? CardBacks = null, CommanderInsightSource? CommanderInsights = null, DeckSearchSource? DeckSearch = null);

/// <summary>
/// A content module installed on disk: where card data and images come from, card scripts, formats and decks.
/// Modules are data only (JSON, text, Lua), so the same module works on every platform.
/// </summary>
public sealed class ContentModule
{
    /// <summary>Engine API version this build understands; modules declaring a newer one are rejected.</summary>
    public const int SupportedApiVersion = 21;

    public string Directory { get; }
    public ModuleManifest Manifest { get; }
    public ModuleSources Sources { get; }

    private ContentModule(string directory, ModuleManifest manifest, ModuleSources sources)
    {
        Directory = directory;
        Manifest = manifest;
        Sources = sources;
    }

    /// <summary>A module's manifest.json (not checked against this build's API version).</summary>
    public static ModuleManifest ReadManifest(string json)
    {
        using var manifestDoc = JsonDocument.Parse(json);
        var m = manifestDoc.RootElement;
        return new ModuleManifest(
            Required(m, "id"), Required(m, "name"), Required(m, "version"),
            m.GetProperty("apiVersion").GetInt32(), Required(m, "license"), Optional(m, "description") ?? "", Optional(m, "updates"));
    }

    public static ContentModule Load(string directory)
    {
        var manifest = ReadManifest(File.ReadAllText(Path.Combine(directory, "manifest.json")));
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
                Optional(images, "byIdTemplate"), Optional(images, "backUrlTemplate"), Optional(images, "backByIdTemplate"),
                images.TryGetProperty("byNameMinIntervalMs", out var byName) ? byName.GetInt32() : null,
                Optional(images, "fileByIdTemplate"), Optional(images, "fileBackByIdTemplate"),
                images.TryGetProperty("fileMinIntervalMs", out var fileMs) ? fileMs.GetInt32() : 0),
            s.TryGetProperty("printings", out var printings)
                ? new PrintingSource(Required(printings, "index"), Required(printings, "downloadField"))
                : null,
            s.TryGetProperty("cardBacks", out var backs) && backs.ValueKind == JsonValueKind.Array
                ? backs.EnumerateArray().Select(b => new CardBackSource(Required(b, "id"), Required(b, "name"), Required(b, "url"))).ToList()
                : Array.Empty<CardBackSource>(),
            s.TryGetProperty("commanderInsights", out var insights)
                ? new CommanderInsightSource(Required(insights, "pageTemplate"), Required(insights, "averageDeckTemplate"),
                    insights.TryGetProperty("cacheDays", out var days) ? days.GetInt32() : 7)
                : null,
            s.TryGetProperty("deckSearch", out var search)
                ? new DeckSearchSource(Required(search, "searchTemplate"), Required(search, "popularTemplate"), Required(search, "deckTemplate"),
                    search.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Object
                        ? formats.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString() ?? "")
                        : new Dictionary<string, string>(),
                    search.TryGetProperty("cacheDays", out var searchDays) ? searchDays.GetInt32() : 7,
                    search.TryGetProperty("minIntervalMs", out var interval) ? interval.GetInt32() : 300)
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
    /// <param name="progress">Called now and then with the script files read and their total.</param>
    public Dictionary<string, Scripts.CardScript> LoadScripts(List<string>? errors = null, Action<int, int>? progress = null)
    {
        var scripts = new Dictionary<string, Scripts.CardScript>(StringComparer.OrdinalIgnoreCase);
        var files = ScriptFiles();
        int done = 0;
        foreach (var (name, text) in files)
        {
            if (++done % 200 == 0) progress?.Invoke(done, files.Count);
            if (TryParseScript(name, text(), errors) is { } script) scripts[Path.GetFileNameWithoutExtension(name)] = script;
        }
        progress?.Invoke(files.Count, files.Count);
        return scripts;
    }

    /// <summary>Where the card scripts are (scripts/*.json).</summary>
    public string ScriptsDirectory => Path.Combine(Directory, "scripts");

    /// <summary>
    /// The card scripts of an installed module, kept in one file (<see cref="ModuleInstaller"/> puts them there: thousands
    /// of small files are slow to write on some devices' memory cards). Used when there is no scripts folder.
    /// </summary>
    public string ScriptsArchive => ScriptsDirectory + ".zip";

    /// <summary>The script files (scripts/*.json, else the entries of scripts.zip): each one's file name and text.</summary>
    private List<(string Name, Func<string> Text)> ScriptFiles()
    {
        if (System.IO.Directory.Exists(ScriptsDirectory))
            return System.IO.Directory.EnumerateFiles(ScriptsDirectory, "*.json")
                .Select(file => (Path.GetFileName(file), (Func<string>)(() => File.ReadAllText(file)))).ToList();
        if (!File.Exists(ScriptsArchive)) return new();
        // Read whole (a few megabytes): the zip can't be read from several threads at once, and scripts are asked for
        // from any thread.
        using var zip = System.IO.Compression.ZipFile.OpenRead(ScriptsArchive);
        var files = new List<(string, Func<string>)>(zip.Entries.Count);
        foreach (var entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains('/')) continue;
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            files.Add((entry.Name, () => text));
        }
        return files;
    }

    /// <summary>
    /// A card's script by oracle id, read and parsed the first time it is asked for (safe to call from any thread):
    /// the same script <see cref="LoadScripts"/> would give it, or null.
    /// </summary>
    public Func<string, Scripts.CardScript?> ScriptReader()
    {
        // Every file named for the card, in the order LoadScripts goes through them: the last that parses is the one.
        var files = new Dictionary<string, List<(string Name, Func<string> Text)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ScriptFiles())
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (!files.TryGetValue(id, out var named)) files[id] = named = new(1);
            named.Add(file);
        }
        var parsed = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Scripts.CardScript?>>(StringComparer.OrdinalIgnoreCase);
        return oracleId => files.TryGetValue(oracleId, out var named)
            ? parsed.GetOrAdd(oracleId, _ => new Lazy<Scripts.CardScript?>(
                () => named.Select(file => TryParseScript(file.Name, file.Text(), null)).LastOrDefault(script => script is not null))).Value
            : null;
    }

    private static Scripts.CardScript? TryParseScript(string file, string text, List<string>? errors)
    {
        try
        {
            return Scripts.CardScriptParser.Parse(text);
        }
        // An unknown name (a replacement, a counter kind...) is a script error too, not a reason to fail loading the module.
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            errors?.Add($"{Path.GetFileName(file)}: {e.Message}");
            return null;
        }
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

    /// <summary>A set's draft statistics (ratings/&lt;set&gt;.json), or null when the module has none for it.</summary>
    public Limited.DraftRatings? LoadDraftRatings(string set, List<string>? errors = null)
    {
        var file = Path.Combine(Directory, "ratings", set.ToLowerInvariant() + ".json");
        if (!File.Exists(file)) return null;
        try { return Limited.DraftRatings.Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { errors?.Add($"{Path.GetFileName(file)}: {e.Message}"); return null; }
    }

    /// <summary>The curated search tags in search/tags.json.gz, or null when the module has none.</summary>
    public CardData.SearchTags? LoadSearchTags(List<string>? errors = null)
    {
        var file = Path.Combine(Directory, "search", "tags.json.gz");
        if (!File.Exists(file)) return null;
        try
        {
            using var input = File.OpenRead(file);
            return CardData.SearchTags.Parse(input);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException) { errors?.Add($"{Path.GetFileName(file)}: {e.Message}"); return null; }
    }

    /// <summary>The career settings in career/career.json, or null when the module has no career.</summary>
    public Career.CareerContent? LoadCareer(List<string>? errors = null)
    {
        var file = Path.Combine(Directory, "career", "career.json");
        if (!File.Exists(file)) return null;
        try { return Career.CareerContent.Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is FormatException or JsonException) { errors?.Add($"career.json: {e.Message}"); return null; }
    }

    /// <summary>The season the module shipped with (career/season.json), or null when it has none.</summary>
    public Career.CareerSeason? LoadSeason(List<string>? errors = null)
    {
        var file = Path.Combine(Directory, "career", "season.json");
        if (!File.Exists(file)) return null;
        try { return Career.CareerSeason.Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is FormatException or JsonException) { errors?.Add($"season.json: {e.Message}"); return null; }
    }

    public string ImageUrl(string cardName) => Sources.Images.UrlTemplate.Replace("{name}", Uri.EscapeDataString(cardName));

    /// <summary>URL for an exact image id, or null if the module's image source doesn't support it.</summary>
    public string? ImageUrlById(string id) => Sources.Images.ByIdTemplate?.Replace("{id}", Uri.EscapeDataString(id));

    /// <summary>URL of a double-faced card's back face picture, by the back face's name or by image id; null if the module has none.</summary>
    public string? BackImageUrl(string faceName) => Sources.Images.BackUrlTemplate?.Replace("{name}", Uri.EscapeDataString(faceName));
    public string? BackImageUrlById(string id) => Sources.Images.BackByIdTemplate?.Replace("{id}", Uri.EscapeDataString(id));

    /// <summary>The picture file by image id on the module's file host (front or back face); null if it names none.</summary>
    public string? FileImageUrlById(string id, bool back) =>
        (back ? Sources.Images.FileBackByIdTemplate : Sources.Images.FileByIdTemplate) is { } template && id.Length >= 2
            ? template.Replace("{id0}", Uri.EscapeDataString(id[..1])).Replace("{id1}", Uri.EscapeDataString(id[1..2])).Replace("{id}", Uri.EscapeDataString(id))
            : null;

    /// <summary>URL of the picture of one of the module's card back designs, or null if it offers no such design.</summary>
    public string? CardBackUrl(string id) => Sources.CardBacks?.FirstOrDefault(b => b.Id == id)?.Url;

    /// <summary>
    /// URL of a commander's statistics page (or, with <paramref name="averageDeck"/>, of its average deck), optionally
    /// for one theme or budget; null if the module has no such source.
    /// </summary>
    public string? CommanderInsightsUrl(string slug, string? variant = null, bool averageDeck = false) =>
        Sources.CommanderInsights is { } source
            ? (averageDeck ? source.AverageDeckTemplate : source.PageTemplate).Replace("{slug}", string.IsNullOrEmpty(variant) ? slug : slug + "/" + variant)
            : null;

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
