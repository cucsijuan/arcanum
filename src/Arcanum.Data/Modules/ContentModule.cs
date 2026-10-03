// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Data.Modules;

public sealed record ModuleManifest(string Id, string Name, string Version, int ApiVersion, string License, string Description);

public sealed record CardSource(string Format, string Index, string DownloadField, string UpdatedField);

public sealed record ImageSource(string UrlTemplate, int MinIntervalMs);

public sealed record ModuleSources(string UserAgent, CardSource Cards, ImageSource Images);

/// <summary>
/// A content module installed on disk: where card data and images come from, card scripts, formats and decks.
/// Modules are data only (JSON, text, Lua), so the same module works on every platform.
/// </summary>
public sealed class ContentModule
{
    /// <summary>Engine API version this build understands; modules declaring a newer one are rejected.</summary>
    public const int SupportedApiVersion = 1;

    public string Directory { get; }
    public ModuleManifest Manifest { get; }
    public ModuleSources Sources { get; }

    private ContentModule(string directory, ModuleManifest manifest, ModuleSources sources)
    {
        Directory = directory;
        Manifest = manifest;
        Sources = sources;
    }

    public static ContentModule Load(string directory)
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var m = manifestDoc.RootElement;
        var manifest = new ModuleManifest(
            Required(m, "id"), Required(m, "name"), Required(m, "version"),
            m.GetProperty("apiVersion").GetInt32(), Required(m, "license"), Optional(m, "description") ?? "");
        if (manifest.ApiVersion > SupportedApiVersion)
            throw new InvalidDataException($"Module '{manifest.Id}' needs engine API {manifest.ApiVersion}; this build supports {SupportedApiVersion}.");

        using var sourcesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "sources.json")));
        var s = sourcesDoc.RootElement;
        var cards = s.GetProperty("cards");
        var images = s.GetProperty("images");
        var sources = new ModuleSources(
            Optional(s, "userAgent") ?? "Arcanum",
            new CardSource(Required(cards, "format"), Required(cards, "index"), Required(cards, "downloadField"), Required(cards, "updatedField")),
            new ImageSource(Required(images, "urlTemplate"), images.TryGetProperty("minIntervalMs", out var ms) ? ms.GetInt32() : 100));

        return new ContentModule(directory, manifest, sources);
    }

    public IEnumerable<string> DeckNames() =>
        System.IO.Directory.Exists(DecksDirectory)
            ? System.IO.Directory.EnumerateFiles(DecksDirectory, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()
            : Enumerable.Empty<string>();

    public string ReadDeck(string name) => File.ReadAllText(Path.Combine(DecksDirectory, name + ".txt"));

    public string ImageUrl(string cardName) => Sources.Images.UrlTemplate.Replace("{name}", Uri.EscapeDataString(cardName));

    private string DecksDirectory => Path.Combine(Directory, "decks");

    private static string Required(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
            ? s : throw new InvalidDataException($"Missing '{name}' in module file.");

    private static string? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
}
