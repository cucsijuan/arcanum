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
        // No staging folder left behind; the replaced copy waits aside for CleanUp.
        Assert.DoesNotContain(Directory.GetDirectories(modules), d => Path.GetFileName(d).StartsWith(".installing-"));
        Assert.Single(Directory.GetDirectories(modules), d => Path.GetFileName(d).StartsWith(".old-"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void KeepsTheCardScriptsInOneFileAndReadsThemFromIt()
    {
        var dir = TempDir();
        var modules = Path.Combine(dir, "modules");
        var zip = Zip(dir, ("sample/manifest.json", Manifest), ("sample/sources.json", Sources),
            ("sample/scripts/aaaa.json", """{"name":"Test Golem","abilities":[]}"""),
            ("sample/scripts/bbbb.json", """{"name":"Test Wisp","abilities":[]}"""),
            ("sample/scripts/broken.json", "{ not json"));
        ModuleInstaller.InstallFromZip(zip, modules);

        var installed = Path.Combine(modules, "sample");
        Assert.False(Directory.Exists(Path.Combine(installed, "scripts")));
        Assert.True(File.Exists(Path.Combine(installed, "scripts.zip")));
        var module = ContentModule.Load(installed);
        var errors = new List<string>();
        var scripts = module.LoadScripts(errors);
        Assert.Equal(new[] { "aaaa", "bbbb" }, scripts.Keys.Order());
        Assert.Contains(errors, e => e.StartsWith("broken.json"));
        Assert.NotNull(module.ScriptReader()("bbbb"));
        Assert.Null(module.ScriptReader()("cccc"));
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

    [Fact]
    public void ReadsTheManifestOfAZipWithoutExtractingIt()
    {
        var dir = TempDir();
        var inFolder = Zip(dir, ("sample-1.2.0/manifest.json", Manifest), ("sample-1.2.0/sources.json", Sources));
        var atRoot = Zip(dir, ("manifest.json", Manifest), ("decks/a.txt", "4 Forest"));
        var notAModule = Zip(dir, ("readme.txt", "hello"));
        var notAZip = Path.Combine(dir, "broken.zip");
        File.WriteAllText(notAZip, "not a zip");

        Assert.Equal(("sample", "Sample", "1.2.0"), ModuleInstaller.PeekManifest(inFolder) is { } a ? (a.Id, a.Name, a.Version) : default);
        Assert.Equal("sample", ModuleInstaller.PeekManifest(atRoot)?.Id);
        Assert.Null(ModuleInstaller.PeekManifest(notAModule));
        Assert.Null(ModuleInstaller.PeekManifest(notAZip));
        Assert.Empty(Directory.GetDirectories(dir));
        Assert.Null(ModuleInstaller.PeekManifest(atRoot)?.Updates);
    }

    [Fact]
    public void TheManifestSaysWhereTheModulesReleasesAre()
    {
        var manifest = ContentModule.ReadManifest(Manifest.Replace("}", ",\"updates\":\"https://example.invalid/latest\"}"));
        Assert.Equal("https://example.invalid/latest", manifest.Updates);
    }

    [Fact]
    public void TellsWhetherAZipBringsItsCardData()
    {
        var dir = TempDir();
        Assert.False(ModuleInstaller.BringsCardData(Zip(dir, ("sample/manifest.json", Manifest), ("sample/sources.json", Sources))));
        Assert.True(ModuleInstaller.BringsCardData(Zip(dir, ("sample/manifest.json", Manifest), ("sample/sources.json", Sources),
            ($"sample/{Arcanum.Data.CardData.OracleJsonl.PreparedFileName}", "records"))));
        Assert.True(ModuleInstaller.BringsCardData(Zip(dir, ("manifest.json", Manifest), (Arcanum.Data.CardData.OracleJsonl.PreparedFileName, "records"))));
        Assert.False(ModuleInstaller.BringsCardData(Path.Combine(dir, "none.zip")));
        Directory.Delete(dir, recursive: true);
    }
}
