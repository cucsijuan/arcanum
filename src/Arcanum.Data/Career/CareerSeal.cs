// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcanum.Data.Career;

/// <summary>
/// The career service's word on a career: it replayed the first <see cref="Count"/> actions of the journal with the
/// rules and its own clock, and they hold. <see cref="Hash"/> chains those actions (see <see cref="CareerSeals.Hash"/>);
/// <see cref="Signature"/> is the service's ECDSA P-256 signature (key <see cref="KeyId"/>) of
/// <see cref="CareerSeals.Message"/>. The game carries the public keys, so it can tell a real seal from a made-up one.
/// </summary>
/// <param name="Player">The player's online account (EOS product user id) the career belongs to.</param>
public sealed record CareerSeal(string Player, int Count, string Hash, string KeyId, string Signature);

public static class CareerSeals
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The service's public keys by id (SubjectPublicKeyInfo, base64). A retired key stays listed while seals made with it may exist.</summary>
    public static IReadOnlyDictionary<string, string> PublicKeys { get; } = new Dictionary<string, string>
    {
        ["k1"] = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAECk7RhDzI/pcPgzbSkRDEpZr53ZOncg66v+QrjVHEgMw5X10wXiCfq1vB0AI7zcWMa/ibUvqAqehTCm3xtBrAsQ==",
    };

    /// <summary>The hash of the journal's first <paramref name="count"/> actions: each step hashes the previous one with the next action's JSON.</summary>
    public static string Hash(IEnumerable<CareerAction> journal, int count)
    {
        var hash = new byte[32];
        foreach (var action in journal.Take(count)) hash = Next(hash, action);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The hash after one more action, from the hash so far (hex).</summary>
    public static string Extend(string hash, CareerAction action) => Convert.ToHexString(Next(Convert.FromHexString(hash), action)).ToLowerInvariant();

    private static byte[] Next(byte[] hash, CareerAction action)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(action, Options);
        return SHA256.HashData(hash.Concat(json).ToArray());
    }

    /// <summary>What the service signs.</summary>
    public static byte[] Message(string player, int count, string hash) => Encoding.UTF8.GetBytes($"arcanum-career-seal-v1\n{player}\n{count}\n{hash}");

    public static CareerSeal Sign(string player, int count, string hash, string keyId, ECDsa key) =>
        new(player, count, hash, keyId, Convert.ToBase64String(key.SignData(Message(player, count, hash), HashAlgorithmName.SHA256)));

    /// <summary>Whether the seal was made by the service (one of <paramref name="keys"/>, by default <see cref="PublicKeys"/>).</summary>
    public static bool IsGenuine(CareerSeal seal, IReadOnlyDictionary<string, string>? keys = null)
    {
        if (!(keys ?? PublicKeys).TryGetValue(seal.KeyId, out var publicKey)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(Message(seal.Player, seal.Count, seal.Hash), Convert.FromBase64String(seal.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="seal"/> is genuine and vouches for the start of this <paramref name="journal"/>.</summary>
    public static bool Covers(CareerSeal seal, IReadOnlyList<CareerAction> journal, IReadOnlyDictionary<string, string>? keys = null) =>
        seal.Count <= journal.Count && Hash(journal, seal.Count) == seal.Hash && IsGenuine(seal, keys);
}

/// <summary>
/// Sent to the career service (<c>POST /career/sync</c>): the actions after the <paramref name="Base"/> the service has
/// already checked (its seal's count and hash). The first time a career is sent, <paramref name="Base"/> is 0 and every
/// action goes, with the <paramref name="State"/> the game has for it (a career older than the service may be taken as
/// it is when its journal no longer replays with today's rules and content).
/// </summary>
/// <param name="Content">The content module and its version ("id version").</param>
/// <param name="Game">The game's version: the rules the actions were checked with.</param>
/// <param name="Versions">
/// The game versions the career was played with (see <see cref="CareerVersion"/>), with the state at the start of each
/// version change after <paramref name="Base"/>.
/// </param>
public sealed record CareerSyncRequest(string Content, string Game, int Base, string BaseHash, IReadOnlyList<CareerAction> Actions, CareerState? State = null,
    IReadOnlyList<CareerVersion>? Versions = null);

/// <summary>
/// From the journal's action <paramref name="From"/> on, the career was played with this game version and content. A
/// change of version keeps the state as the earlier version had it then (<paramref name="Before"/>): actions of the
/// previous version that the new rules no longer give the same result for (a booster from the same seed, say) are taken
/// as that version computed them.
/// </summary>
public sealed record CareerVersion(int From, string Game, string Content, CareerState? Before = null);

/// <summary>The service's answer to a sync.</summary>
/// <param name="Status">
/// <see cref="CareerSyncStatus"/>: everything checked; the service has another journal (<paramref name="Seal"/> is what
/// it has); some actions don't hold (the seal covers those before the first that doesn't, <paramref name="Reason"/> says
/// why); or the service can't check this content or game version yet.
/// </param>
public sealed record CareerSyncResponse(CareerSyncStatus Status, CareerSeal? Seal, string? Reason = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CareerSyncStatus>))]
public enum CareerSyncStatus { Ok, Conflict, Rejected, Unsupported }

/// <summary>The career the service has checked (<c>GET /career/profile</c>): its journal, the state it gives, and the seal.</summary>
public sealed record CareerProfile(CareerSeal Seal, List<CareerAction> Journal, CareerState State);
