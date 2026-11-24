// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Tests;

public class GeneratedCopiersTests
{
    /// <summary>
    /// The game's generated copiers match its fields as they are now. ARCANUM_WRITE_COPIERS=1 writes them again.
    /// </summary>
    [Fact]
    public void GeneratedCopiersAreCurrent()
    {
        var path = CopierGenerator.SourcePath();
        var generated = CopierGenerator.Generate();
        if (Environment.GetEnvironmentVariable("ARCANUM_WRITE_COPIERS") == "1") File.WriteAllText(path, generated);
        Assert.True(File.Exists(path), $"{path} is missing: run this test with ARCANUM_WRITE_COPIERS=1");
        Assert.True(File.ReadAllText(path) == generated, $"{path} is out of date: run this test with ARCANUM_WRITE_COPIERS=1");
    }
}
