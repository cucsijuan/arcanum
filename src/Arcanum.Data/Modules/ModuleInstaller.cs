// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;

namespace Arcanum.Data.Modules;

/// <summary>Installs a content module from a zip file (as published in a module's releases).</summary>
public static class ModuleInstaller
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="modulesDirectory"/>/&lt;module id&gt;, replacing an earlier
    /// copy of the same module. The zip may hold the module's files at its root or inside one top folder.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a module (no manifest), or a module this build can't use.</exception>
    public static ModuleManifest InstallFromZip(string zipPath, string modulesDirectory)
    {
        Directory.CreateDirectory(modulesDirectory);
        var staging = Path.Combine(modulesDirectory, $".installing-{Guid.NewGuid():N}");
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                        throw new InvalidDataException($"The zip has a file outside its folder: {entry.FullName}");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            var moduleRoot = FindManifest(staging) ?? throw new InvalidDataException("This zip isn't a content module (no manifest.json found).");
            var module = ContentModule.Load(moduleRoot); // checks the manifest and API version
            var id = module.Manifest.Id;
            if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.StartsWith('.'))
                throw new InvalidDataException($"Invalid module id '{id}'.");

            var destination = Path.Combine(modulesDirectory, id);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(moduleRoot, destination);
            return module.Manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>The folder holding manifest.json: the root, or a single top folder.</summary>
    private static string? FindManifest(string directory)
    {
        if (File.Exists(Path.Combine(directory, "manifest.json"))) return directory;
        var folders = Directory.GetDirectories(directory);
        return folders.Length == 1 && File.Exists(Path.Combine(folders[0], "manifest.json")) ? folders[0] : null;
    }
}
