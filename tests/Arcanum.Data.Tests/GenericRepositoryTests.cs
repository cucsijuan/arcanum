// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arcanum.Data.Tests;

public class GenericRepositoryTests
{
    /// <summary>
    /// Optional guard with ARCANUM_MODULE_PATH: the repository is generic, so no full card name of the content module
    /// (two words or more) appears in the C# sources, scripts or tests.
    /// </summary>
    [Fact]
    public void NoModuleCardNameAppearsInTheRepository()
    {
        var dir = Environment.GetEnvironmentVariable("ARCANUM_MODULE_PATH");
        if (string.IsNullOrEmpty(dir)) return;
        var scripts = Path.Combine(dir, "scripts");
        if (!Directory.Exists(scripts)) return;
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Arcanum.csproj"))) root = Path.GetDirectoryName(root);
        if (root is null) return;

        var names = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(scripts, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { } name && name.Contains(' '))
                names.Add(name);
        }
        if (names.Count == 0) return;
        var pattern = new Regex(@"\b(" + string.Join("|", names.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")\b", RegexOptions.Compiled);

        var self = Path.GetFullPath(Path.Combine(root, "tests", "Arcanum.Data.Tests", "GenericRepositoryTests.cs"));
        var sep = Path.DirectorySeparatorChar;
        var found = new List<string>();
        foreach (var top in new[] { "src", "scripts", "tests" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(file) == self || file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (pattern.Match(lines[i]) is { Success: true } m) found.Add($"{Path.GetRelativePath(root, file)}:{i + 1} {m.Value}");
            }
        Assert.True(found.Count == 0, "Module card names in the repository:\n" + string.Join("\n", found.Take(30)));
    }
}
