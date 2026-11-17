// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Tests;

/// <summary>
/// The real card data for the optional module checks: ARCANUM_CARD_FILE when set, otherwise the card data the game has
/// downloaded and converted for that module version (user://card_data/&lt;module id&gt;/cards.v*-&lt;module version&gt;.jsonl.gz,
/// written by ContentLoader the first time the game starts with that module).
/// </summary>
internal static class RealCardData
{
    /// <summary>The card file for the module in <paramref name="moduleDir"/>, or null when there is none.</summary>
    public static string? Find(string? moduleDir)
    {
        var given = Environment.GetEnvironmentVariable("ARCANUM_CARD_FILE");
        if (!string.IsNullOrEmpty(given)) return given;
        if (string.IsNullOrEmpty(moduleDir) || Manifest(moduleDir) is not var (id, version)) return null;
        var dir = Path.Combine(GameUserDir(), "card_data", id);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, $"cards.v*-{version}.jsonl.gz").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>Like <see cref="Find"/>, but says what to do when there is no card data instead of skipping the check.</summary>
    public static string Require(string moduleDir) => Find(moduleDir)
        ?? throw new InvalidOperationException($"No card data for the module in {moduleDir} (version {Manifest(moduleDir)?.Version ?? "unknown: no manifest.json"}): " +
                                               "start the game once with this module version, or set ARCANUM_CARD_FILE.");

    private static (string Id, string Version)? Manifest(string moduleDir)
    {
        var path = Path.Combine(moduleDir, "manifest.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) && root.TryGetProperty("version", out var version) ? (id.GetString()!, version.GetString()!) : null;
    }

    /// <summary>The game's user:// folder, where the engine keeps it on each system.</summary>
    private static string GameUserDir()
    {
        string data;
        if (OperatingSystem.IsWindows()) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot");
        else if (OperatingSystem.IsMacOS()) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Godot");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            data = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg, "godot");
        }
        return Path.Combine(data, "app_userdata", "Arcanum");
    }
}
