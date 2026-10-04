// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using Arcanum.Data.Modules;

namespace Arcanum.Data.Tests;

public class ModuleInstallerTests
{
    private const string Manifest = """{"id":"sample","name":"Sample","version":"1.2.0","apiVersion":1,"license":"AGPL-3.0-or-later"}""";
    private const string Sources = """{"cards":{"format":"jsonl","index":"https://example.invalid/i","downloadField":"d","updatedField":"u"},"images":{"urlTemplate":"https://example.invalid/{name}"}}""";

    private static string Zip(string dir, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arcanum-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void InstallsAModuleFromItsTopFolderAndReplacesAnOlderCopy()
    {
        var dir = TempDir();
        var modules = Path.Combine(dir, "modules");
        Directory.CreateDirectory(Path.Combine(modules, "sample"));
        File.WriteAllText(Path.Combine(modules, "sample", "stale.txt"), "old");

        var zip = Zip(dir, ("sample-1.2.0/manifest.json", Manifest), ("sample-1.2.0/sources.json", Sources), ("sample-1.2.0/decks/a.txt", "4 Forest"));
        var manifest = ModuleInstaller.InstallFromZip(zip, modules);

        Assert.Equal("sample", manifest.Id);
        Assert.True(File.Exists(Path.Combine(modules, "sample", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(modules, "sample", "decks", "a.txt")));
        Assert.False(File.Exists(Path.Combine(modules, "sample", "stale.txt")));
        Assert.Single(Directory.GetDirectories(modules)); // no staging folder left behind
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void InstallsAModuleWithFilesAtTheZipRoot()
    {
        var dir = TempDir();
        var zip = Zip(dir, ("manifest.json", Manifest), ("sources.json", Sources));
        Assert.Equal("1.2.0", ModuleInstaller.InstallFromZip(zip, Path.Combine(dir, "modules")).Version);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void RefusesZipsThatAreNotModulesOrEscapeTheirFolder()
    {
        var dir = TempDir();
        var modules = Path.Combine(dir, "modules");
        Assert.Throws<InvalidDataException>(() => ModuleInstaller.InstallFromZip(Zip(dir, ("readme.txt", "hi")), modules));
        Assert.Throws<InvalidDataException>(() => ModuleInstaller.InstallFromZip(Zip(dir, ("../evil.txt", "x"), ("manifest.json", Manifest)), modules));
        Assert.False(File.Exists(Path.Combine(dir, "evil.txt")));
        Assert.Empty(Directory.GetDirectories(modules));
        Directory.Delete(dir, recursive: true);
    }
}
