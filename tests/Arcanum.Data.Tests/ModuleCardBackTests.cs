// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Modules;

namespace Arcanum.Data.Tests;

public class ModuleCardBackTests
{
    private const string Manifest = """{"id":"sample","name":"Sample","version":"1.0.0","apiVersion":5,"license":"AGPL-3.0-or-later"}""";
    private const string Cards = """ "cards":{"format":"jsonl","index":"https://example.invalid/i","downloadField":"d","updatedField":"u"},"images":{"urlTemplate":"https://example.invalid/{name}"}""";

    private static ContentModule Load(string sources)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arcanum-module-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest);
            File.WriteAllText(Path.Combine(dir, "sources.json"), sources);
            return ContentModule.Load(dir);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CardBacksAreReadWithTheirPictureUrls()
    {
        var module = Load("{" + Cards + """, "cardBacks":[{"id":"classic","name":"Classic","url":"https://example.invalid/back.jpg"},{"id":"alt","name":"Alternate","url":"https://example.invalid/alt.png"}]}""");

        Assert.Equal(new[] { "classic", "alt" }, module.Sources.CardBacks!.Select(b => b.Id));
        Assert.Equal("Alternate", module.Sources.CardBacks![1].Name);
        Assert.Equal("https://example.invalid/back.jpg", module.CardBackUrl("classic"));
        Assert.Null(module.CardBackUrl("missing"));
    }

    [Fact]
    public void CardBacksAreOptional()
    {
        var module = Load("{" + Cards + "}");

        Assert.Empty(module.Sources.CardBacks!);
        Assert.Null(module.CardBackUrl("classic"));
    }

    [Fact]
    public void ACardBackWithoutAPictureIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Load("{" + Cards + """, "cardBacks":[{"id":"classic","name":"Classic"}]}"""));
    }
}
