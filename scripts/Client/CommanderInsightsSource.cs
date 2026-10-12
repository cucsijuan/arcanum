// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Modules;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace Arcanum.Client;

/// <summary>
/// Downloads a commander's community statistics and average deck from the module's source, keeping each page in
/// user://commander_insights for the days the module asks, so a commander is fetched at most once in that time. When the
/// source can't be reached an older copy is used.
/// </summary>
public static class CommanderInsightsSource
{
    private const string Folder = "user://commander_insights";
    private static HttpClient? _http;

    /// <summary>Whether the module offers commander statistics.</summary>
    public static bool Available(ContentModule? module) => module?.Sources.CommanderInsights is not null;

    /// <summary>The commander's statistics (for a theme or budget page when <paramref name="variant"/> is given), or null when unavailable.</summary>
    public static async Task<CommanderInsights?> PageAsync(ContentModule module, string slug, string? variant = null)
    {
        var json = await FetchAsync(module, slug, variant, averageDeck: false);
        if (json is null) return null;
        try { return CommanderInsights.Parse(json); }
        catch (System.Text.Json.JsonException e) { GD.PushWarning($"Commander statistics for {slug}: {e.Message}"); return null; }
    }

    /// <summary>The commander's average deck (for a theme or budget page when <paramref name="variant"/> is given), or null when unavailable.</summary>
    public static async Task<DeckList?> AverageDeckAsync(ContentModule module, string slug, string? variant = null)
    {
        var json = await FetchAsync(module, slug, variant, averageDeck: true);
        if (json is null) return null;
        try { return CommanderInsights.ParseAverageDeck(json); }
        catch (System.Text.Json.JsonException e) { GD.PushWarning($"Average deck for {slug}: {e.Message}"); return null; }
    }

    private static async Task<string?> FetchAsync(ContentModule module, string slug, string? variant, bool averageDeck)
    {
        if (module.CommanderInsightsUrl(slug, variant, averageDeck) is not { } url || module.Sources.CommanderInsights is not { } source) return null;
        var folder = ProjectSettings.GlobalizePath(Folder);
        var file = Path.Combine(folder, (averageDeck ? "deck." : "page.") + slug + (string.IsNullOrEmpty(variant) ? "" : "." + variant.Replace('/', '.')) + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(source.CacheDays))
            return await File.ReadAllTextAsync(file);
        try
        {
            _http ??= CreateClient(module.Sources.UserAgent);
            using var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(file, json);
                return json;
            }
            // A commander the source has no page for answers "forbidden" or "not found": nothing to show.
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound) return null;
            GD.PushWarning($"Commander statistics: {url} answered {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            GD.PushWarning($"Commander statistics: {url}: {e.Message}");
        }
        return File.Exists(file) ? await File.ReadAllTextAsync(file) : null;
    }

    private static HttpClient CreateClient(string userAgent)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
