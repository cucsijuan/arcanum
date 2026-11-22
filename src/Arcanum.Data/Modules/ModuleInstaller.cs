// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;

namespace Arcanum.Data.Modules;

/// <summary>Installs a content module from a zip file (as published in a module's releases).</summary>
public static class ModuleInstaller
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="modulesDirectory"/>/&lt;module id&gt;, replacing an earlier
    /// copy of the same module. The zip may hold the module's files at its root or inside one top folder. The card
    /// scripts (thousands of small files, slow to write one by one on some devices' memory cards) go into one file,
    /// scripts.zip (see <see cref="ContentModule.ScriptsArchive"/>).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a module (no manifest), or a module this build can't use.</exception>
    public static ModuleManifest InstallFromZip(string zipPath, string modulesDirectory, Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(modulesDirectory);
        var staging = Path.Combine(modulesDirectory, $".installing-{Guid.NewGuid():N}");
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                var scripts = ModulePrefix(zip) + "scripts/";
                ZipArchive? scriptsArchive = null;
                try
                {
                    int done = 0, total = zip.Entries.Count;
                    foreach (var entry in zip.Entries)
                    {
                        progress?.Invoke(done++, total);
                        var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                        if (!target.StartsWith(root, StringComparison.Ordinal))
                            throw new InvalidDataException($"The zip has a file outside its folder: {entry.FullName}");
                        if (entry.FullName.StartsWith(scripts, StringComparison.Ordinal))
                        {
                            var name = entry.FullName[scripts.Length..];
                            if (name.Length == 0 || name.Contains('/')) continue;
                            if (scriptsArchive is null)
                            {
                                var archivePath = Path.Combine(staging, scripts[..^1] + ".zip");
                                Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
                                scriptsArchive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
                            }
                            using var from = entry.Open();
                            using var to = scriptsArchive.CreateEntry(name, CompressionLevel.Fastest).Open();
                            from.CopyTo(to);
                            continue;
                        }
                        if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                    }
                }
                finally
                {
                    scriptsArchive?.Dispose();
                }
            }

            var moduleRoot = FindManifest(staging) ?? throw new InvalidDataException("This zip isn't a content module (no manifest.json found).");
            var module = ContentModule.Load(moduleRoot); // checks the manifest and API version
            var id = module.Manifest.Id;
            if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.StartsWith('.'))
                throw new InvalidDataException($"Invalid module id '{id}'.");

            var destination = Path.Combine(modulesDirectory, id);
            if (Directory.Exists(destination))
            {
                // Out of the way at once (deleting thousands of files can take minutes on a memory card); CleanUp
                // removes it on a later start.
                Directory.Move(destination, Path.Combine(modulesDirectory, $".old-{Guid.NewGuid():N}"));
            }
            Directory.Move(moduleRoot, destination);
            // The game uses the module installed last; extracting kept the zip's own file times. Written again, the
            // manifest takes the install time (some devices can't set a file's time directly).
            var manifestFile = Path.Combine(destination, "manifest.json");
            File.WriteAllText(manifestFile, File.ReadAllText(manifestFile));
            return module.Manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// The manifest of the module in <paramref name="zipPath"/> (at its root or inside one top folder), read without
    /// extracting it; null when the file isn't a zip or holds no module.
    /// </summary>
    public static ModuleManifest? PeekManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null)
            {
                var tops = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
                if (tops.Count == 1) entry = zip.GetEntry($"{tops[0]}/manifest.json");
            }
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open());
            return ContentModule.ReadManifest(reader.ReadToEnd());
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException
                                      or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"" when the zip holds the module's files at its root, else its one top folder ("name/").</summary>
    private static string ModulePrefix(ZipArchive zip)
    {
        if (zip.GetEntry("manifest.json") is not null) return "";
        var tops = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
        return tops.Count == 1 ? tops[0] + "/" : "";
    }

    /// <summary>Removes what earlier installs left behind (a replaced copy, an install cut short), in the background.</summary>
    public static void CleanUp(string modulesDirectory)
    {
        if (!Directory.Exists(modulesDirectory)) return;
        foreach (var dir in Directory.GetDirectories(modulesDirectory))
            if (Path.GetFileName(dir) is var name && (name.StartsWith(".old-") || name.StartsWith(".installing-")))
                _ = Task.Run(() => DeleteQuietly(dir));
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The folder holding manifest.json: the root, or a single top folder.</summary>
    private static string? FindManifest(string directory)
    {
        if (File.Exists(Path.Combine(directory, "manifest.json"))) return directory;
        var folders = Directory.GetDirectories(directory);
        return folders.Length == 1 && File.Exists(Path.Combine(folders[0], "manifest.json")) ? folders[0] : null;
    }
}
