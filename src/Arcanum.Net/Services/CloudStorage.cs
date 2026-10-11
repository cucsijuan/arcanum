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
