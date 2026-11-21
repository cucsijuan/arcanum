// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Arcanum.Client;

/// <summary>
/// Files' last write times where the game sets them itself (a downloaded file dated as the server's copy). Some file
/// systems can't change a file's times (a console's memory card): there the time is kept in a small list next to the
/// files, together with the time the file system gave the file, and stands only while the file keeps that time (a file
/// written again later reads with its own time).
/// </summary>
public sealed class FileTimes(string listPath)
{
    private sealed record Entry(DateTime Written, DateTime Meant);

    /// <summary>The time the file was last written, or the one the game set on it.</summary>
    public DateTime GetUtc(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        return Read().TryGetValue(Path.GetFullPath(path), out var entry) && entry.Written == written ? entry.Meant : written;
    }

    public void SetUtc(string path, DateTime utc)
    {
        var list = Read();
        var key = Path.GetFullPath(path);
        try
        {
            File.SetLastWriteTimeUtc(path, utc);
            if (!list.Remove(key)) return;
        }
        catch (IOException)
        {
            list[key] = new Entry(File.GetLastWriteTimeUtc(path), utc);
        }
        list = list.Where(e => File.Exists(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
        File.WriteAllText(listPath, JsonSerializer.Serialize(list));
    }

    private Dictionary<string, Entry> Read()
    {
        try
        {
            return File.Exists(listPath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(listPath)) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }
}
