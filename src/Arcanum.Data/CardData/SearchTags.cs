// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text.Json;

namespace Arcanum.Data.CardData;

/// <summary>
/// The cards and printings behind the search's curated <c>is:</c> values (land cycle nicknames, <c>is:bear</c>,
/// <c>is:atypical</c>...), as the card data's source tags them: a module's search/tags.json.gz.
/// </summary>
/// <param name="Aliases">Other names of the same values ("karoo" for "bounceland").</param>
/// <param name="Cards">Card-level values: their cards' oracle ids.</param>
/// <param name="Printings">Printing-level values: their printings' ids.</param>
public sealed record SearchTags(
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Cards,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Printings)
{
    public static SearchTags Parse(Stream gzipped)
    {
        using var gzip = new GZipStream(gzipped, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gzip);
        var root = doc.RootElement;
        Dictionary<string, IReadOnlySet<string>> Lists(string name) =>
            root.TryGetProperty(name, out var lists)
                ? lists.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlySet<string>)p.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToHashSet())
                : new Dictionary<string, IReadOnlySet<string>>();
        var aliases = root.TryGetProperty("aliases", out var a)
            ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : new Dictionary<string, string>();
        return new SearchTags(aliases, Lists("cards"), Lists("printings"));
    }

    /// <summary>The value an alias stands for (the value itself when it isn't one).</summary>
    public string Canonical(string value) => Aliases.TryGetValue(value, out var canonical) ? canonical : value;
}

/// <summary>What the search knows about the card data beyond the cards: the curated tags and the promo kinds printings have.</summary>
public sealed record SearchVocabulary(SearchTags? Tags, IReadOnlySet<string> PromoTypes)
{
    public static readonly SearchVocabulary None = new(null, new HashSet<string>());
}
