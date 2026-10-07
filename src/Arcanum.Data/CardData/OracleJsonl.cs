// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Arcanum.Data.CardData;

/// <summary>Which imported cards to keep; the rules come from the content module.</summary>
public sealed record ImportFilter(IReadOnlyList<string> IncludeIfLegalIn, IReadOnlyList<string> ExcludeLayouts)
{
    public static readonly ImportFilter All = new(Array.Empty<string>(), Array.Empty<string>());

    public bool Accepts(CardRecord record)
    {
        if (ExcludeLayouts.Contains(record.Layout)) return false;
        // Tokens and cards without legality data are kept: the filter only drops cards known to be unplayable.
        if (record.IsToken || IncludeIfLegalIn.Count == 0 || record.Legalities.Count == 0) return true;
        return IncludeIfLegalIn.Any(format => record.Legalities.TryGetValue(format, out var status) && status != "not_legal");
    }
}

/// <summary>
/// Reads and writes card records as JSON Lines (one card object per line), optionally gzip-compressed.
/// Parsing is manual (JsonDocument per line) so it works with ahead-of-time compilation on every platform.
/// </summary>
public static class OracleJsonl
{
    public static Stream OpenMaybeGzip(Stream raw)
    {
        var buffered = new BufferedStream(raw);
        var header = new byte[2];
        int read = buffered.Read(header, 0, 2);
        // Can't seek a network or gzip stream reliably, so re-wrap what we peeked.
        var prefix = new MemoryStream(header, 0, read);
        var joined = new ConcatStream(prefix, buffered);
        return read == 2 && header[0] == 0x1f && header[1] == 0x8b ? new GZipStream(joined, CompressionMode.Decompress) : joined;
    }

    /// <summary>Imports the card source's JSON Lines file, keeping the cards <paramref name="filter"/> accepts.</summary>
    public static IEnumerable<CardRecord> Import(Stream source, ImportFilter? filter = null)
    {
        filter ??= ImportFilter.All;
        using var reader = new StreamReader(OpenMaybeGzip(source), Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var record = FromSource(doc.RootElement);
            if (record is not null && filter.Accepts(record)) yield return record;
        }
    }

    private static CardRecord? FromSource(JsonElement c)
    {
        string layout = Str(c, "layout") ?? "normal";
        if (Str(c, "oracle_id") is not { } oracleId || Str(c, "name") is not { } name) return null;

        var faces = c.TryGetProperty("card_faces", out var f)
            ? f.EnumerateArray().Select(face => new CardFaceRecord(
                Str(face, "name") ?? "", Str(face, "mana_cost") ?? "", Str(face, "type_line") ?? "", Str(face, "oracle_text") ?? "",
                Str(face, "power"), Str(face, "toughness"))
            {
                Loyalty = Str(face, "loyalty"),
                Colors = StrList(face, "colors"),
            }).ToList()
            : new List<CardFaceRecord>();

        return new CardRecord
        {
            OracleId = oracleId,
            Name = name,
            Layout = layout,
            ManaCost = Str(c, "mana_cost") ?? "",
            TypeLine = Str(c, "type_line") ?? "",
            OracleText = Str(c, "oracle_text") ?? "",
            Power = Str(c, "power"),
            Toughness = Str(c, "toughness"),
            Loyalty = Str(c, "loyalty"),
            Colors = StrList(c, "colors"),
            ColorIdentity = StrList(c, "color_identity"),
            Keywords = StrList(c, "keywords"),
            ProducedMana = StrList(c, "produced_mana"),
            Legalities = c.TryGetProperty("legalities", out var l)
                ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                : new Dictionary<string, string>(),
            Faces = faces,
            IsToken = layout == "token" || (Str(c, "type_line")?.StartsWith("Token") ?? false),
            RelatedTokens = Tokens(c),
            DefaultPrintingId = Str(c, "id"),
            Printings = c.TryGetProperty("printings", out var printings)
                ? printings.EnumerateArray().Select(PrintingFrom).OfType<Printing>().ToList()
                : new List<Printing>(),
        };
    }

    private static List<RelatedToken> Tokens(JsonElement c) =>
        c.TryGetProperty("all_parts", out var parts)
            ? parts.EnumerateArray()
                .Where(p => Str(p, "component") == "token" && Str(p, "id") is not null && Str(p, "name") is not null)
                .Select(p => new RelatedToken(Str(p, "name")!, Str(p, "id")!, Str(p, "type_line") ?? ""))
                .ToList()
            : new List<RelatedToken>();

    /// <summary>A printing from a line of the printings source (or from Arcanum's compact form, which uses the same names).</summary>
    private static Printing? PrintingFrom(JsonElement c)
    {
        if (Str(c, "set") is not { } set || Str(c, "id") is not { } id || Str(c, "collector_number") is not { } number) return null;
        return new Printing(set, Str(c, "set_name") ?? set.ToUpperInvariant(), number, Str(c, "rarity") ?? "common", id,
            Str(c, "released_at") ?? "", Str(c, "set_type") ?? "", c.TryGetProperty("booster", out var b) && b.ValueKind == JsonValueKind.True,
            Tokens(c), Finishes(c).Contains("foil") || (c.TryGetProperty("foil", out var f) && f.ValueKind == JsonValueKind.True));
    }

    /// <summary>The finishes a printing was made in ("nonfoil", "foil", "etched"); empty when the source doesn't say.</summary>
    private static List<string> Finishes(JsonElement c) =>
        c.TryGetProperty("finishes", out var finishes) && finishes.ValueKind == JsonValueKind.Array
            ? finishes.EnumerateArray().Select(f => f.GetString() ?? "").ToList()
            : new List<string>();

    /// <summary>Set types whose printings are not playing cards (collector replicas, token sets) and are skipped.</summary>
    private static readonly HashSet<string> SkippedSetTypes = new() { "memorabilia", "token", "minigame" };

    /// <summary>
    /// Reads the printings source (one line per printing) and returns the paper printings of each card by oracle id,
    /// oldest first. Digital-only printings, oversized cards and other languages are skipped, and so are printings made
    /// only in foil: their pictures show the foil, which the game draws itself on foil copies of the other printings.
    /// </summary>
    public static Dictionary<string, List<Printing>> ImportPrintings(Stream source)
    {
        var byCard = new Dictionary<string, List<Printing>>();
        using var reader = new StreamReader(OpenMaybeGzip(source), Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var c = doc.RootElement;
            if (Str(c, "oracle_id") is not { } oracleId) continue;
            if (c.TryGetProperty("digital", out var digital) && digital.ValueKind == JsonValueKind.True) continue;
            if (c.TryGetProperty("oversized", out var oversized) && oversized.ValueKind == JsonValueKind.True) continue;
            if (Str(c, "lang") is { } lang && lang != "en") continue;
            if (SkippedSetTypes.Contains(Str(c, "set_type") ?? "") || Str(c, "layout") == "token") continue;
            if (Finishes(c) is { Count: > 0 } finishes && !finishes.Contains("nonfoil")) continue;
            if (PrintingFrom(c) is not { } printing) continue;
            if (!byCard.TryGetValue(oracleId, out var list)) byCard[oracleId] = list = new List<Printing>();
            list.Add(printing);
        }
        foreach (var list in byCard.Values)
            list.Sort((a, b) => string.CompareOrdinal(a.Released, b.Released) is var d && d != 0 ? d : CompareNumbers(a.CollectorNumber, b.CollectorNumber));
        return byCard;
    }

    /// <summary>Adds the printings found by <see cref="ImportPrintings"/> to each record.</summary>
    public static IEnumerable<CardRecord> WithPrintings(IEnumerable<CardRecord> records, IReadOnlyDictionary<string, List<Printing>> printings) =>
        records.Select(r => printings.TryGetValue(r.OracleId, out var list) ? r with { Printings = list } : r);

    /// <summary>Collector numbers in numeric order ("9" before "10"), with any letters after the digits breaking ties.</summary>
    public static int CompareNumbers(string a, string b)
    {
        static (int, string) Key(string n)
        {
            int i = 0;
            while (i < n.Length && !char.IsDigit(n[i])) i++;
            int start = i;
            while (i < n.Length && char.IsDigit(n[i])) i++;
            return (i > start && int.TryParse(n[start..i], out int v) ? v : int.MaxValue, n);
        }
        var (na, sa) = Key(a);
        var (nb, sb) = Key(b);
        return na != nb ? na.CompareTo(nb) : string.CompareOrdinal(sa, sb);
    }

    /// <summary>Writes records in Arcanum's own compact JSON Lines form (gzip).</summary>
    public static void WriteCompact(IEnumerable<CardRecord> records, Stream destination)
    {
        using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        var newline = "\n"u8.ToArray();
        foreach (var r in records)
        {
            using (var writer = new Utf8JsonWriter(gzip))
            {
                writer.WriteStartObject();
                writer.WriteString("oracle_id", r.OracleId);
                writer.WriteString("name", r.Name);
                writer.WriteString("layout", r.Layout);
                WriteIf(writer, "mana_cost", r.ManaCost);
                WriteIf(writer, "type_line", r.TypeLine);
                WriteIf(writer, "oracle_text", r.OracleText);
                WriteIf(writer, "power", r.Power);
                WriteIf(writer, "toughness", r.Toughness);
                WriteIf(writer, "loyalty", r.Loyalty);
                WriteList(writer, "colors", r.Colors);
                WriteList(writer, "color_identity", r.ColorIdentity);
                WriteList(writer, "keywords", r.Keywords);
                WriteList(writer, "produced_mana", r.ProducedMana);
                if (r.Legalities.Count > 0)
                {
                    writer.WriteStartObject("legalities");
                    foreach (var (format, status) in r.Legalities) writer.WriteString(format, status);
                    writer.WriteEndObject();
                }
                WriteTokens(writer, r.RelatedTokens);
                WriteIf(writer, "id", r.DefaultPrintingId);
                if (r.Printings.Count > 0)
                {
                    writer.WriteStartArray("printings");
                    foreach (var p in r.Printings)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("set", p.Set);
                        writer.WriteString("set_name", p.SetName);
                        writer.WriteString("collector_number", p.CollectorNumber);
                        writer.WriteString("rarity", p.Rarity);
                        writer.WriteString("id", p.Id);
                        WriteIf(writer, "released_at", p.Released);
                        WriteIf(writer, "set_type", p.SetType);
                        if (p.Booster) writer.WriteBoolean("booster", true);
                        if (p.Foil) writer.WriteBoolean("foil", true);
                        WriteTokens(writer, p.Tokens);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (r.Faces.Count > 0)
                {
                    writer.WriteStartArray("card_faces");
                    foreach (var face in r.Faces)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", face.Name);
                        WriteIf(writer, "mana_cost", face.ManaCost);
                        WriteIf(writer, "type_line", face.TypeLine);
                        WriteIf(writer, "oracle_text", face.OracleText);
                        WriteIf(writer, "power", face.Power);
                        WriteIf(writer, "toughness", face.Toughness);
                        WriteIf(writer, "loyalty", face.Loyalty);
                        WriteList(writer, "colors", face.Colors);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            gzip.Write(newline);
        }
    }

    /// <summary>Reads records written by <see cref="WriteCompact"/>.</summary>
    public static List<CardRecord> ReadCompact(Stream source)
    {
        var records = new List<CardRecord>();
        using var reader = new StreamReader(new GZipStream(source, CompressionMode.Decompress), Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (FromSource(doc.RootElement) is { } record) records.Add(record);
        }
        return records;
    }

    /// <summary>Tokens in the same shape as the source's all_parts, so reading uses the same code.</summary>
    private static void WriteTokens(Utf8JsonWriter writer, IReadOnlyList<RelatedToken> tokens)
    {
        if (tokens.Count == 0) return;
        writer.WriteStartArray("all_parts");
        foreach (var token in tokens)
        {
            writer.WriteStartObject();
            writer.WriteString("component", "token");
            writer.WriteString("id", token.Id);
            writer.WriteString("name", token.Name);
            WriteIf(writer, "type_line", token.TypeLine);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIf(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteString(name, value);
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> StrList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : Array.Empty<string>();

    /// <summary>Reads one stream, then another, as a single forward-only stream.</summary>
    private sealed class ConcatStream(Stream first, Stream second) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = first.Read(buffer, offset, count);
            return n > 0 ? n : second.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { first.Dispose(); second.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
