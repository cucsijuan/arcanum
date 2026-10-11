// SPDX-License-Identifier: AGPL-3.0-or-later
// The season editor: a page on this computer (http://127.0.0.1:5099) to plan a content module's season on a calendar,
// check it as the game and the career service will, save it into the module and publish it.
//   dotnet run --project tools/SeasonEditor -- <module dir> [user@host]
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arcanum.Data.Career;
using Arcanum.Data.Modules;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: SeasonEditor <module dir> [user@host]");
    return 2;
}
string moduleDir = Path.GetFullPath(args[0]);
string host = args.Length > 1 ? args[1] : "ubuntu@arcanum-career.duckdns.org";
var module = ContentModule.Load(moduleDir);
var content = module.LoadCareer() ?? throw new InvalidOperationException("The module has no career.");
var sets = module.LoadSets().ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
var decks = module.DeckNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
string seasonFile = Path.Combine(moduleDir, "career", "season.json");
string publishScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../server/Arcanum.CareerService/deploy/publish-season.sh"));
string seasonUrl = $"https://arcanum-career.duckdns.org/career/season/{module.Manifest.Id}.json";
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// The published season (days already played can't change): read from the career service.
CareerSeason? published = null;
string publishedNote = "";
async Task ReadPublished()
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.GetAsync(seasonUrl);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { published = CareerSeason.None; publishedNote = "Nothing published yet."; return; }
        response.EnsureSuccessStatusCode();
        published = CareerSeason.Parse(await response.Content.ReadAsStringAsync());
        publishedNote = $"Published season: {published.Changes.Count} changes.";
    }
    catch (Exception e) { published = null; publishedNote = $"The published season couldn't be read ({e.GetBaseException().Message}): played days aren't checked."; }
}
await ReadPublished();

var soldSets = content.Sets.Where(s => sets.TryGetValue(s, out var d) && d.Booster is not null)
    .Select(s => new { code = s.ToLowerInvariant(), name = sets[s].Name }).ToList();
var kinds = content.BoosterPrices.Keys.ToList();

DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

// Everything wrong with a season, as the publish check sees it.
List<string> Check(CareerSeason season)
{
    var problems = season.Problems(content, sets, decks).ToList();
    if (published?.ChangedPast(season, Today()) is { } past) problems.Add(past);
    return problems;
}

(CareerSeason? Season, string? Error) Read(JsonNode? node)
{
    try { return (CareerSeason.Parse(node?.ToJsonString() ?? "{}"), null); }
    catch (Exception e) when (e is FormatException or JsonException) { return (null, e.Message); }
}

string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = projectDir, WebRootPath = Path.Combine(projectDir, "wwwroot") });
builder.WebHost.UseUrls("http://127.0.0.1:5099");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// What the page needs to know about the module.
app.MapGet("/api/content", () => Results.Json(new
{
    module = module.Manifest.Name,
    today = Today(),
    sets = soldSets,
    kinds,
    boosterPrice = content.BoosterPrice,
    boosterPrices = content.BoosterPrices,
    events = content.Events,
    tiers = content.Tiers.Select(t => t.Name),
    decks = decks.OrderBy(d => d),
    publishedNote,
}, web));

// The season in the module (every field filled in).
app.MapGet("/api/season", () =>
{
    var season = File.Exists(seasonFile) ? CareerSeason.Parse(File.ReadAllText(seasonFile)) : CareerSeason.None;
    return Results.Content(season.ToJson(), "application/json");
});

// The calendar of a season from a day: each day's events, featured set, prices and news, and the season's problems.
app.MapPost("/api/calendar", (JsonObject body) =>
{
    var (season, error) = Read(body["season"]);
    if (season is null) return Results.Json(new { problems = new[] { error }, days = Array.Empty<object>() }, web);
    var from = DateOnly.Parse(body["from"]!.GetValue<string>());
    int count = body["days"]?.GetValue<int>() ?? 42;
    var today = Today();
    var days = Enumerable.Range(0, count).Select(i => from.AddDays(i)).Select(day =>
    {
        var events = season.Events(content, day).Where(e => CareerRules.Happens(e, day)).Select(e => new
        {
            e.Id,
            e.Name,
            e.Kind,
            set = e.Kind == CareerRules.Draft ? (e.Set.Equals("featured", StringComparison.OrdinalIgnoreCase) ? season.FeaturedSet(content, day) : e.Set) : null,
            e.Entry,
            starts = e.Online?.Starts,
            pvpOnly = e.Online?.PvpOnly ?? false,
            source = content.Events.FirstOrDefault(m => m.Id == e.Id) is { } original ? (original == e ? "module" : "changed") : "season",
        });
        var cancelled = content.Events.Where(e => CareerRules.Happens(e, day) && season.Events(content, day).All(x => x.Id != e.Id)).Select(e => e.Name);
        var featured = season.FeaturedSet(content, day);
        return new
        {
            date = day,
            past = day <= today,
            events,
            cancelled,
            featured,
            featuredName = sets.TryGetValue(featured, out var s) ? s.Name : featured,
            featuredChanged = featured != CareerSeason.None.FeaturedSet(content, day),
            prices = new[] { (string?)null }.Concat(kinds).ToDictionary(k => k ?? "play", k => season.BoosterPrice(content, k, day)),
            news = season.On(day).Select(c => c.News).OfType<string>(),
        };
    });
    return Results.Json(new { problems = Check(season), days }, web);
});

// Saves the season into the module (it must read; problems are allowed in a draft, not when publishing).
app.MapPost("/api/save", (JsonObject body) =>
{
    var (season, error) = Read(body["season"]);
    if (season is null) return Results.Json(new { ok = false, message = error }, web);
    File.WriteAllText(seasonFile, season.ToJson() + "\n");
    return Results.Json(new { ok = true, message = $"Saved {seasonFile}." }, web);
});

// Saves, checks and publishes with the deploy script.
app.MapPost("/api/publish", async (JsonObject body) =>
{
    var (season, error) = Read(body["season"]);
    if (season is null) return Results.Json(new { ok = false, message = error }, web);
    await ReadPublished();
    if (Check(season) is { Count: > 0 } problems) return Results.Json(new { ok = false, message = string.Join("\n", problems) }, web);
    File.WriteAllText(seasonFile, season.ToJson() + "\n");
    var run = Process.Start(new ProcessStartInfo("sh", new[] { publishScript, moduleDir, host }) { RedirectStandardOutput = true, RedirectStandardError = true })!;
    string output = await run.StandardOutput.ReadToEndAsync() + await run.StandardError.ReadToEndAsync();
    await run.WaitForExitAsync();
    await ReadPublished();
    return Results.Json(new { ok = run.ExitCode == 0, message = output.Trim() }, web);
});

Console.WriteLine($"Season editor for {module.Manifest.Name}: http://127.0.0.1:5099  (Ctrl+C to stop)");
if (Environment.GetEnvironmentVariable("SEASON_EDITOR_NO_BROWSER") != "1")
    try { Process.Start(new ProcessStartInfo("xdg-open", "http://127.0.0.1:5099") { UseShellExecute = false }); }
    catch (Exception) { /* no browser to open: the address is printed */ }
app.Run();
return 0;
