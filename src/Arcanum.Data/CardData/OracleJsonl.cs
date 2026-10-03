// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Arcanum.Data.CardData;

/// <summary>
/// Reads and writes card records as JSON Lines (one card object per line), optionally gzip-compressed.
/// Parsing is manual (JsonDocument per line) so it works with ahead-of-time compilation on every platform.
/// </summary>
public static class OracleJsonl
{
    /// <summary>Layouts that are not playable cards (art cards, planes, schemes...).</summary>
    private static readonly HashSet<string> SkippedLayouts = new(StringComparer.Ordinal)
    {
        "art_series", "planar", "scheme", "vanguard", "emblem", "front_card", "augment", "host", "double_faced_token",
    };

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

    /// <summary>Imports the card source's JSON Lines file, keeping only playable cards and tokens.</summary>
    public static IEnumerable<CardRecord> Import(Stream source)
    {
        using var reader = new StreamReader(OpenMaybeGzip(source), Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var record = FromSource(doc.RootElement);
            if (record is not null) yield return record;
        }
    }

    private static CardRecord? FromSource(JsonElement c)
    {
        string layout = Str(c, "layout") ?? "normal";
        if (SkippedLayouts.Contains(layout)) return null;
        if (c.TryGetProperty("games", out var games) && !games.EnumerateArray().Any(g => g.GetString() == "paper")) return null;
        if (Str(c, "oracle_id") is not { } oracleId || Str(c, "name") is not { } name) return null;

        var faces = c.TryGetProperty("card_faces", out var f)
            ? f.EnumerateArray().Select(face => new CardFaceRecord(
                Str(face, "name") ?? "", Str(face, "mana_cost") ?? "", Str(face, "type_line") ?? "", Str(face, "oracle_text") ?? "",
                Str(face, "power"), Str(face, "toughness"))).ToList()
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
        };
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
