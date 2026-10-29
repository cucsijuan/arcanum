// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Net.Services;

/// <summary>The signed-in player's own files in the online services (their career, kept across devices).</summary>
public interface ICloudStorage
{
    /// <summary>Signed in, and this build can keep the player's files.</summary>
    bool IsReady { get; }

    /// <summary>A file's contents, or null when the player has no such file.</summary>
    Task<byte[]?> ReadAsync(string name, CancellationToken cancel = default);

    Task WriteAsync(string name, byte[] data, CancellationToken cancel = default);
}

/// <summary>
/// Proof of who the player is for a service of the game's own (the career service): the online services' signed ID
/// token for the signed-in player.
/// </summary>
public interface IIdentityTokens
{
    /// <summary>A current ID token (a JWT), or null when the player isn't signed in.</summary>
    Task<string?> IdTokenAsync(CancellationToken cancel = default);
}
