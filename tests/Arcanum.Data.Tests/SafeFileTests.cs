// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data.Tests;

/// <summary>Replacing a file through a temporary one, and getting back a file left aside by a cut-off replacement.</summary>
public class SafeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcanum-safefile-" + Guid.NewGuid().ToString("N"));

    public SafeFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ReplacesAnExistingFile()
    {
        var path = Path.Combine(_dir, "save.bin");
        File.WriteAllText(path, "old");
        File.WriteAllText(path + ".tmp", "new");
        SafeFile.Replace(path + ".tmp", path);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void RecoverPutsBackTheFileLeftAside()
    {
        var path = Path.Combine(_dir, "save.bin");
        File.WriteAllText(path + ".bak", "old");
        SafeFile.Recover(path);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
        SafeFile.Recover(path); // nothing aside: nothing changes
        Assert.Equal("old", File.ReadAllText(path));
    }
}
