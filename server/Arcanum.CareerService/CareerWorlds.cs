// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Career;
using Arcanum.Data.Modules;

namespace Arcanum.CareerService;

/// <summary>
/// The career worlds the service checks careers with: one per content module version, read from
/// <c>{Career:ContentDir}/{module id}/{version}/</c> (the module as released in <c>module/</c>, the game's compact card
/// data in <c>cards.jsonl.gz</c>; deploy/publish-content.sh puts them there) with the season the service publishes.
/// Building one takes seconds and a few hundred megabytes, so only the last one asked for is kept; it is built again when
/// its season file changes.
/// </summary>
public sealed class CareerWorlds(IConfiguration configuration, ILogger<CareerWorlds> log)
{
    private readonly string _contentDir = configuration["Career:ContentDir"] ?? "/var/lib/arcanum-career/content";
    private readonly string _seasonDir = configuration["Career:SeasonDir"] ?? "/srv/arcanum-career/career/season";
    private readonly object _lock = new();
    private (string Content, DateTime Season, CareerWorld World)? _current;

    /// <summary>
    /// Builds the world of the newest content there is (the one players sync with), so no player waits for it: at start,
    /// and again when its season changed.
    /// </summary>
    public void Warm()
    {
        if (!Directory.Exists(_contentDir)) return;
        var newest = Directory.GetDirectories(_contentDir)
            .SelectMany(module => Directory.GetDirectories(module).Select(version => (Id: Path.GetFileName(module), Version: Path.GetFileName(version), Time: Directory.GetLastWriteTimeUtc(version))))
            .OrderByDescending(c => c.Time).FirstOrDefault();
        if (newest.Id is null) return;
        try { For($"{newest.Id} {newest.Version}"); }
        catch (Exception e) { log.LogWarning("Career world {Id} {Version} couldn't be built: {Message}", newest.Id, newest.Version, e.Message); }
    }

    /// <summary>The world of a content ("id version"), or null when the service doesn't have that content.</summary>
    public CareerWorld? For(string content)
    {
        var parts = content.Split(' ');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Contains('/') || p.Contains('\\') || p.Contains(".."))) return null;
        var dir = Path.Combine(_contentDir, parts[0], parts[1]);
        var moduleDir = Path.Combine(dir, "module");
        var cardsFile = Path.Combine(dir, "cards.jsonl.gz");
        if (!File.Exists(Path.Combine(moduleDir, "manifest.json")) || !File.Exists(cardsFile)) return null;
        var seasonFile = Path.Combine(_seasonDir, parts[0] + ".json");
        var seasonTime = File.Exists(seasonFile) ? File.GetLastWriteTimeUtc(seasonFile) : default;
        lock (_lock)
        {
            if (_current is { } current && current.Content == content && current.Season == seasonTime) return current.World;
            _current = null; // let the old world go before building the next
            GC.Collect();
            var started = DateTime.UtcNow;
            var module = ContentModule.Load(moduleDir);
            CardDatabase cards;
            using (var input = File.OpenRead(cardsFile)) cards = new CardDatabase(OracleJsonl.ReadCompact(input), module.LoadScripts());
            var careerContent = module.LoadCareer() ?? throw new InvalidOperationException($"{content} has no career.");
            var season = File.Exists(seasonFile) ? CareerSeason.Parse(File.ReadAllText(seasonFile)) : module.LoadSeason();
            var deckNames = module.DeckNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var world = new CareerWorld(careerContent, cards, module.LoadSets(), n => deckNames.Contains(n) ? module.ReadDeck(n) : null, module.LoadFormats(), season);
            _current = (content, seasonTime, world);
            log.LogInformation("Career world {Content} ready in {Seconds:F1} s ({Cards} cards)", content, (DateTime.UtcNow - started).TotalSeconds, cards.Count);
            return world;
        }
    }
}
