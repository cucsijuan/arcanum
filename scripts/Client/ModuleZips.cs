// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Modules;
using Godot;

namespace Arcanum.Client;

/// <summary>
/// Content module zips put next to the game (on a console, copied to the game's folder on its memory card): each one
/// newer than the installed copy of its module, or of a module not installed, is offered for install when the game
/// starts, before the cards load. A zip the player said no to isn't offered again (a newer version of it is).
/// </summary>
public static class ModuleZips
{
    public sealed record Found(string Path, ModuleManifest Manifest, string? Installed);

    /// <summary>The zips next to the game worth offering, the newest of each module.</summary>
    public static IReadOnlyList<Found> Offered()
    {
        // Editor runs use the sibling checkout; a module path set for testing wins over installed copies.
        if (OS.HasFeature("editor") || OS.GetEnvironment("ARCANUM_MODULE_PATH").Length > 0) return Array.Empty<Found>();
        var folder = OS.GetExecutablePath().GetBaseDir();
        IEnumerable<string> zips;
        try
        {
            zips = Directory.EnumerateFiles(folder, "*.zip").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<Found>();
        }
        var modules = ProjectSettings.GlobalizePath("user://modules");
        var declined = Settings.Current.DeclinedModuleZips;
        return zips
            .Select(path => (Path: path, Manifest: ModuleInstaller.PeekManifest(path)))
            .Where(z => z.Manifest is { ApiVersion: <= ContentModule.SupportedApiVersion })
            .Select(z => new Found(z.Path, z.Manifest!, InstalledVersion(modules, z.Manifest!.Id)))
            .Where(f => Newer(f.Manifest.Version, f.Installed) && !declined.Contains(Key(f.Manifest)))
            .GroupBy(f => f.Manifest.Id)
            .Select(g => g.OrderByDescending(f => VersionOf(f.Manifest.Version)).First())
            .ToList();
    }

    /// <summary>Remembers that the player doesn't want this zip's module version.</summary>
    public static void Decline(Found found)
    {
        Settings.Current.DeclinedModuleZips = Settings.Current.DeclinedModuleZips.Append(Key(found.Manifest)).Distinct().ToArray();
        Settings.Save();
    }

    private static string Key(ModuleManifest manifest) => $"{manifest.Id} {manifest.Version}";

    private static string? InstalledVersion(string modules, string id)
    {
        var manifest = System.IO.Path.Combine(modules, id, "manifest.json");
        try
        {
            return File.Exists(manifest) ? ContentModule.ReadManifest(File.ReadAllText(manifest)).Version : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static bool Newer(string version, string? installed) =>
        installed is null || VersionOf(version) > VersionOf(installed);

    private static Version VersionOf(string text) => Version.TryParse(text, out var v) ? v : new Version(0, 0);
}
