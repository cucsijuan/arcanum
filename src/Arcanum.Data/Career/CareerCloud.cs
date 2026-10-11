// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>
/// A career as kept in the cloud: its folder's files (the save, the career's decks, an event in progress) in one zip,
/// with when they were last changed. A bundle without a save is an ended career.
/// </summary>
public sealed class CareerBundle
{
    public const string SaveName = "career.sav";

    /// <summary>The files by their path in the career's folder ("career.sav", "decks/Burn.txt").</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>When the newest of them was changed.</summary>
    public DateTime SavedUtc { get; set; }

    public byte[]? Save => Files.GetValueOrDefault(SaveName);

    public byte[] Pack()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, data) in Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = SavedUtc;
                using var stream = entry.Open();
                stream.Write(data);
            }
            var stamp = archive.CreateEntry(".saved");
            using var writer = new StreamWriter(stamp.Open());
            writer.Write(SavedUtc.ToString("O"));
        }
        return zip.ToArray();
    }

    public static CareerBundle Unpack(byte[] data)
    {
        var bundle = new CareerBundle();
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            // Only plain relative paths inside the career's folder.
            if (entry.FullName.StartsWith('/') || entry.FullName.Contains("..") || entry.FullName.Contains('\\') || entry.FullName.EndsWith('/')) continue;
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (entry.FullName == ".saved")
                bundle.SavedUtc = DateTime.Parse(System.Text.Encoding.UTF8.GetString(copy.ToArray()), null, System.Globalization.DateTimeStyles.RoundtripKind);
            else bundle.Files[entry.FullName] = copy.ToArray();
        }
        return bundle;
    }
}

/// <summary>What to do with a career on this device and the one in the cloud.</summary>
public enum CloudSync
{
    /// <summary>They are the same.</summary>
    Same,
    /// <summary>This device's is ahead (or the cloud has none): send it.</summary>
    Upload,
    /// <summary>The cloud's is ahead (or this device has none): take it.</summary>
    Download,
    /// <summary>Both went on separately: the player chooses one.</summary>
    Conflict,
}

public static class CareerCloud
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Compares the career here with the cloud's by their journals: the one that continues the other is ahead; with the
    /// same journal the newer files win (decks edited); journals that went apart are a conflict. A career ended on one
    /// side (no save, newer) wins over the same career that went no further on the other.
    /// </summary>
    public static CloudSync Compare(CareerBundle? local, CareerBundle? cloud, byte[] key)
    {
        if (cloud is null) return local is null ? CloudSync.Same : CloudSync.Upload;
        if (local is null) return CloudSync.Download;
        var mine = Read(local, key);
        var theirs = Read(cloud, key);
        // One side ended its career (or never had one): the newer side wins.
        if (mine is null || theirs is null) return Newer(local, cloud);
        var a = Lines(mine);
        var b = Lines(theirs);
        int common = 0;
        while (common < a.Count && common < b.Count && a[common] == b[common]) common++;
        if (common == a.Count && common == b.Count) return Newer(local, cloud);
        if (common == b.Count) return CloudSync.Upload;   // this device went on from the cloud's
        if (common == a.Count) return CloudSync.Download; // the cloud went on from this device's
        return CloudSync.Conflict;
    }

    private static CloudSync Newer(CareerBundle local, CareerBundle cloud) =>
        local.SavedUtc == cloud.SavedUtc && local.Files.Count == cloud.Files.Count
        && local.Files.All(f => cloud.Files.TryGetValue(f.Key, out var other) && other.AsSpan().SequenceEqual(f.Value))
            ? CloudSync.Same
            : local.SavedUtc >= cloud.SavedUtc ? CloudSync.Upload : CloudSync.Download;

    /// <summary>The career in a bundle (null: none, or it can't be read).</summary>
    public static CareerSave? Read(CareerBundle bundle, byte[] key)
    {
        if (bundle.Save is not { } data) return null;
        try { return CareerSave.Read(data, key); }
        catch (Exception e) when (e is InvalidDataException or JsonException) { return null; }
    }

    private static List<string> Lines(CareerSave save) =>
        save.Journal.Select(a => JsonSerializer.Serialize(a, Options)).ToList();
}
