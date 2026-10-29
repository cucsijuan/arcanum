// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>
/// A career as saved: the journal of everything done since it started, and the state it gives (so loading doesn't
/// replay it). The file is compressed and sealed with an HMAC: it catches damaged files and casual editing, but the
/// key ships with the game, so it is not a protection against a determined player (checking a career needs a server
/// that replays the journal).
/// </summary>
public sealed class CareerSave
{
    public int Version { get; set; } = 1;

    /// <summary>Content module and version the career was last played with.</summary>
    public string Content { get; set; } = "";

    public List<CareerAction> Journal { get; set; } = new();
    public CareerState State { get; set; } = new();

    /// <summary>The career service's seal on the start of the journal (null: never checked).</summary>
    public CareerSeal? Seal { get; set; }

    /// <summary>The game versions the career was played with, oldest first (empty for careers from before they were noted).</summary>
    public List<CareerVersion> Versions { get; set; } = new();

    private static readonly byte[] Header = "ARCCAR1"u8.ToArray();

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] Write(byte[] key)
    {
        using var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, this, Options);
        var body = payload.ToArray();
        var mac = HMACSHA256.HashData(key, body);
        return Header.Concat(mac).Concat(body).ToArray();
    }

    /// <summary>Reads a saved career; throws <see cref="InvalidDataException"/> when the file isn't one or was changed outside the game.</summary>
    public static CareerSave Read(byte[] data, byte[] key)
    {
        if (data.Length < Header.Length + 32 || !data.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new InvalidDataException("This isn't a saved career.");
        var mac = data.AsSpan(Header.Length, 32);
        var body = data.AsSpan(Header.Length + 32).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(key, body)))
            throw new InvalidDataException("The saved career was changed outside the game or is damaged.");
        using var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CareerSave>(gzip, Options) ?? throw new InvalidDataException("The saved career is empty.");
    }

    /// <summary>The default sealing key (builds may pass their own).</summary>
    public static byte[] DefaultKey => SHA256.HashData(Encoding.UTF8.GetBytes("arcanum-career-save-v1"));
}
