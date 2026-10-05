// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Transport;

namespace Arcanum.Net.Services;

/// <summary>
/// A public description of a hosted lobby: what the lobby browser lists and what joining by invite code finds. Transport
/// hints (<see cref="Address"/>, <see cref="Port"/>) are filled in by providers that connect directly.
/// </summary>
public sealed record LobbyListing(string LobbyId, string HostName, string Format, bool Commander, int Seats, int OpenSeats,
    string Version, string Content, string InviteCode, bool Listed = true, string? Event = null)
{
    public string? Address { get; init; }
    public int Port { get; init; }
}

/// <summary>What the lobby browser asks for: the player's version and content must match; full lobbies can be hidden.</summary>
public sealed record LobbyQuery(string Version, string Content, bool OnlyWithOpenSeats = true);

/// <summary>A player signed in to an online service.</summary>
public sealed record OnlineIdentity(string UserId, string DisplayName);

/// <summary>
/// Online services: who the player is, where lobbies are listed, and how players reach a host (NAT traversal or relay).
/// Implementations: a local one for tests and offline development, one for the local network, and a hosted service.
/// </summary>
public interface IOnlineServices : IDisposable
{
    /// <summary>Shown to players ("Local network", …).</summary>
    string Name { get; }

    /// <summary>Whether the service can be used on this device now (configured, reachable, signed in when needed).</summary>
    bool IsAvailable { get; }

    /// <summary>Signs the player in (a device account where the service supports it); returns who they are.</summary>
    Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default);

    ILobbyDirectory Lobbies { get; }

    IRelayNetwork Network { get; }
}

/// <summary>Where lobbies are published, searched and found by invite code.</summary>
public interface ILobbyDirectory
{
    /// <summary>Lists a lobby (or only makes it findable by its invite code when it isn't <see cref="LobbyListing.Listed"/>).</summary>
    Task<LobbyListing> PublishAsync(LobbyListing listing, CancellationToken cancel = default);

    /// <summary>Updates what's shown (seats taken, started…).</summary>
    Task UpdateAsync(LobbyListing listing, CancellationToken cancel = default);

    Task RemoveAsync(string lobbyId, CancellationToken cancel = default);

    Task<IReadOnlyList<LobbyListing>> SearchAsync(LobbyQuery query, CancellationToken cancel = default);

    Task<LobbyListing?> FindByCodeAsync(string inviteCode, CancellationToken cancel = default);
}

/// <summary>How players reach a host: the host listens for a lobby; players connect to it (directly, through NAT traversal or a relay).</summary>
public interface IRelayNetwork
{
    /// <summary>Accepts players' connections to this lobby (the lobby and game hosts add it as a listener).</summary>
    IConnectionListener Listen(string lobbyId);

    /// <summary>Connects to a lobby's host.</summary>
    Task<IConnection> ConnectAsync(LobbyListing lobby, CancellationToken cancel = default);
}

/// <summary>Helpers shared by service providers.</summary>
public static class LobbyIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}
