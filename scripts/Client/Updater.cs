// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace Arcanum.Client;

/// <summary>A published release: its version, notes and the zip to download for this system (if there is one).</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string Notes, string PageUrl, string? ZipUrl, string? ZipName, long ZipSize);

/// <summary>
/// Updates from published releases: checks whether a newer game (from the game's GitHub releases) or content module
/// (from where the module's manifest says, see <see cref="Arcanum.Data.Modules.ModuleManifest.Updates"/>) is published, downloads
/// it, and installs it. The game can't overwrite itself while running, so a small script waits for it to close,
/// copies the new files over the installed ones and starts it again.
/// </summary>
public static class Updater
{
    private const string GameRepo = "cucsijuan/arcanum";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Arcanum/{GameVersion}");
        return client;
    }

    /// <summary>The running game's version (config/version), or ARCANUM_UPDATE_FROM to try the update check as an older version.</summary>
    public static string GameVersion => OS.GetEnvironment("ARCANUM_UPDATE_FROM") is { Length: > 0 } pretend ? pretend
        : ProjectSettings.GetSetting("application/config/version", "0.0.0").AsString();

    /// <summary>Whether to look for a newer game: where it can update itself, or when trying the check (ARCANUM_UPDATE_FROM).</summary>
    public static bool ChecksGame => CanUpdateGame || OS.GetEnvironment("ARCANUM_UPDATE_FROM").Length > 0;

    /// <summary>
    /// Whether this build can replace itself: an exported build (not the editor) on Windows, Linux or the console, the
    /// systems releases are published for.
    /// </summary>
    public static bool CanUpdateGame => !OS.HasFeature("editor") && (OS.GetName() is "Windows" or "Linux" or "Switch");

    private static string? PlatformZipSuffix => OS.GetName() switch
    {
        "Windows" => "-windows.zip", "Linux" => "-linux.zip", "Switch" => "-console.zip", _ => null,
    };

    /// <summary>The newest game release, when it is newer than this build (null otherwise or when it can't be reached).</summary>
    public static async Task<ReleaseInfo?> NewerGameAsync() =>
        await LatestAsync($"https://api.github.com/repos/{GameRepo}/releases/latest", PlatformZipSuffix) is { } release && ParseVersion(GameVersion) is { } current && release.Version > current ? release : null;

    /// <summary>
    /// The newest release of the installed module <paramref name="installed"/>, when it is newer (none when its manifest
    /// doesn't say where its releases are).
    /// </summary>
    public static async Task<ReleaseInfo?> NewerModuleAsync(Arcanum.Data.Modules.ModuleManifest installed) =>
        installed.Updates is { Length: > 0 } url && await LatestAsync(url, ".zip") is { } release
        && ParseVersion(installed.Version) is { } current && release.Version > current ? release : null;

    /// <summary>
    /// A "latest release" document at <paramref name="url"/> (GitHub's releases API format). A connection that can't be
    /// made is tried again a few times (a device's network may still be coming up when the game starts).
    /// </summary>
    private static async Task<ReleaseInfo?> LatestAsync(string url, string? zipSuffix)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await LatestOnceAsync(url, zipSuffix);
            }
            catch (HttpRequestException e) when (attempt < 4)
            {
                GD.Print($"Update check at {url}: {e.Message}; trying again");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch (Exception e)
            {
                GD.Print($"Update check at {url} failed: {e.Message}");
                return null;
            }
        }
    }

    private static async Task<ReleaseInfo?> LatestOnceAsync(string url, string? zipSuffix)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            GD.Print($"Update check at {url}: {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } version) return null;
        string? zipUrl = null, zipName = null;
        long size = 0;
        if (zipSuffix is not null && root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(zipSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                zipUrl = asset.GetProperty("browser_download_url").GetString();
                zipName = name;
                size = asset.GetProperty("size").GetInt64();
                break;
            }
        return new ReleaseInfo(tag, version, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "", zipUrl, zipName, size);
    }

    /// <summary>"v0.12.0" / "0.12.0" → 0.12.0.</summary>
    public static Version? ParseVersion(string text) => Version.TryParse(text.Trim().TrimStart('v', 'V'), out var v) ? v : null;

    /// <summary>Downloads the release's zip into the updates folder, reporting progress (0–1).</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, Action<double> progress)
    {
        if (release.ZipUrl is null || release.ZipName is null) throw new InvalidOperationException("This release has no download for this system.");
        var folder = ProjectSettings.GlobalizePath("user://updates");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.ZipName);
        using var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? release.ZipSize;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(path))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress(Math.Min(1, done / (double)total));
            }
        }
        return path;
    }

    /// <summary>The folder the game runs from (where its executable and data folder are).</summary>
    public static string InstallFolder => Path.GetDirectoryName(OS.GetExecutablePath())!;

    /// <summary>Whether the game may write into its own folder (it can't update itself otherwise).</summary>
    public static bool InstallFolderWritable()
    {
        try
        {
            var probe = Path.Combine(InstallFolder, $".arcanum-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks a downloaded game zip and starts the script that installs it once this process has exited, then
    /// relaunches the game. The caller quits right after.
    /// </summary>
    public static void InstallGameAndRestart(string zipPath)
    {
        if (OS.GetName() == "Switch")
        {
            InstallConsoleGame(zipPath);
            return;
        }
        var staging = Path.Combine(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath));
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);

        var exe = Path.GetFileName(OS.GetExecutablePath());
        if (!File.Exists(Path.Combine(staging, exe))) throw new InvalidDataException($"The download doesn't contain {exe}.");
        int pid = OS.GetProcessId();
        var target = InstallFolder;
        if (OS.GetName() == "Windows")
        {
            var script = Path.Combine(Path.GetDirectoryName(zipPath)!, "install-update.cmd");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                ":wait",
                // Full paths: another find on the PATH (e.g. from Git) would never see the game and copy too early.
                // (ping waits a second; timeout.exe needs a console, which this script may not have)
                $"\"%SystemRoot%\\System32\\tasklist.exe\" /FI \"PID eq {pid}\" 2>NUL | \"%SystemRoot%\\System32\\find.exe\" \"{pid}\" >NUL && (\"%SystemRoot%\\System32\\PING.EXE\" -n 2 127.0.0.1 >NUL & goto wait)",
                // xcopy /Y replaces every file, even ones with the same size and time as the installed ones (robocopy skips those).
                $"\"%SystemRoot%\\System32\\xcopy.exe\" \"{staging}\\*\" \"{target}\\\" /E /Y /H /R /I /Q >NUL",
                $"rmdir /S /Q \"{staging}\"",
                $"start \"\" \"{Path.Combine(target, exe)}\"",
                "(goto) 2>NUL & del \"%~f0\"", ""));
            OS.CreateProcess("cmd.exe", new[] { "/c", script });
        }
        else
        {
            var script = Path.Combine(Path.GetDirectoryName(zipPath)!, "install-update.sh");
            File.WriteAllText(script, string.Join("\n",
                "#!/bin/sh",
                $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done",
                $"cp -a \"{staging}/.\" \"{target}/\"",
                $"chmod +x \"{target}/{exe}\"",
                $"rm -rf \"{staging}\"",
                $"\"{target}/{exe}\" &",
                "rm -- \"$0\"", ""));
            OS.CreateProcess("/bin/sh", new[] { script });
        }
    }

    /// <summary>
    /// The console: the running program can't be replaced while it runs, so the new one goes to the update slot not
    /// running (update_a.bin / update_b.bin beside it), with update.install; the loader starts it once this game has
    /// closed, and the console port makes it the version the menu opens (arcanum.nro) as it starts.
    /// </summary>
    private static void InstallConsoleGame(string zipPath)
    {
        var folder = InstallFolder;
        var slot = Path.Combine(folder, Path.GetFileName(OS.GetExecutablePath()) == "update_a.bin" ? "update_b.bin" : "update_a.bin");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var program = zip.Entries.FirstOrDefault(e => e.Name == "arcanum.nro")
                          ?? throw new InvalidDataException("The download doesn't contain arcanum.nro.");
            program.ExtractToFile(slot, overwrite: true);
        }
        File.Delete(zipPath);
        File.WriteAllText(Path.Combine(folder, "update.install"), "");
        if (OS.CreateProcess(slot, []) < 0)
            throw new IOException("This loader can't start the new version: start the game again from the menu to finish.");
    }

    /// <summary>Installs a downloaded content module zip (it is used from the next start).</summary>
    public static Arcanum.Data.Modules.ModuleManifest InstallModule(string zipPath)
    {
        try
        {
            return Arcanum.Data.Modules.ModuleInstaller.InstallFromZip(zipPath, ProjectSettings.GlobalizePath("user://modules"));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }
}
