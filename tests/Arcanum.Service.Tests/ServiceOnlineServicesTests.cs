// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;
using Xunit;

namespace Arcanum.Service.Tests;

/// <summary>The game's side of the service's lobbies and relay, against the relay on a loopback port.</summary>
public class ServiceOnlineServicesTests
{
    private static ServiceOnlineServices Services(RelayServer server, string player) =>
        new(server.Http, _ => Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["X-Player"] = player }));

    private static LobbyListing Listing(string id = "lobby-1", string code = "ABC123", int port = 0) =>
        new(id, "Host", "Standard", false, 2, 1, "1.0.0", "content", code) { Port = port };

    private static async Task<T> Until<T>(Func<T?> probe, int seconds = 5) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found) return found;
            await Task.Delay(10);
        }
        throw new TimeoutException("Nothing came.");
    }

    private static async Task Until(Func<bool> probe, int seconds = 5)
    {
        await Until(() => probe() ? "yes" : null, seconds);
    }

    /// <summary>The lobby once the service has it (publishing is answered before the service reads it).</summary>
    private static Task<LobbyListing> Find(ServiceOnlineServices services, string code = "ABC123") =>
        Until(() => services.Lobbies.FindByCodeAsync(code).Result);

    /// <summary>Connects while the host keeps accepting, as the game's host does every frame.</summary>
    private static async Task<(IConnection ToHost, IConnection AtHost)> Connect(IOnlineServices player, LobbyListing lobby, IConnectionListener listener)
    {
        var accepting = Accept(listener);
        var toHost = await player.Network.ConnectAsync(lobby);
        return (toHost, await accepting);
    }

    private static Task<IConnection> Accept(IConnectionListener listener) =>
        Until(() => listener.TryAccept(out var connection) ? connection : null);

    private static Task<string> Receive(IConnection connection) =>
        Until(() => connection.TryReceive(out var message) ? message : null);

    [Fact]
    public async Task SigningIn_NeedsAnIdentity()
    {
        await using var server = await RelayServer.StartAsync();
        var signedOut = new ServiceOnlineServices(server.Http, _ => Task.FromResult<IReadOnlyDictionary<string, string>?>(null));
        Assert.False(signedOut.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => signedOut.SignInAsync("Me"));

        var signedIn = Services(server, "player-1");
        var identity = await signedIn.SignInAsync("Me");
        Assert.True(signedIn.IsAvailable);
        Assert.Equal("player-1", identity.UserId);
    }

    [Fact]
    public async Task APublishedLobby_IsFoundBySearchAndCode_AndGoneWhenRemoved()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = Services(server, "host");
        using var browser = Services(server, "browser");
        await host.Lobbies.PublishAsync(Listing());

        await Find(browser);
        var found = Assert.Single(await browser.Lobbies.SearchAsync(new LobbyQuery("1.0.0", "content")));
        Assert.Equal("lobby-1", found.LobbyId);
        Assert.Equal("127.0.0.1", found.Address);
        Assert.Equal("lobby-1", (await browser.Lobbies.FindByCodeAsync("ABC123"))!.LobbyId);
        Assert.Null(await browser.Lobbies.FindByCodeAsync("NOPE"));

        await host.Lobbies.UpdateAsync(Listing() with { OpenSeats = 0 });
        await Until(() => browser.Lobbies.SearchAsync(new LobbyQuery("1.0.0", "content")).Result.Count == 0);

        await host.Lobbies.RemoveAsync("lobby-1");
        await Until(() => browser.Lobbies.SearchAsync(new LobbyQuery("1.0.0", "content", OnlyWithOpenSeats: false)).Result.Count == 0);
    }

    [Fact]
    public async Task APlayerReachesTheHostThroughTheRelay_WithMessagesOfAnySize()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = Services(server, "host");
        using var player = Services(server, "player");
        using var listener = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing());

        var (toHost, atHost) = await Connect(player, await Find(player), listener);

        toHost.Send("hello");
        Assert.Equal("hello", await Receive(atHost));
        // Larger than a relayed packet: split and joined again.
        var big = string.Concat(Enumerable.Range(0, 20000).Select(i => (char)('a' + i % 26)));
        atHost.Send(big);
        Assert.Equal(big, await Receive(toHost));
        atHost.Send("é ✓ 🂡");
        Assert.Equal("é ✓ 🂡", await Receive(toHost));

        toHost.Close();
        await Until(() => !atHost.IsOpen);
    }

    [Fact]
    public async Task TheHostLeaving_DropsItsRelayedPlayers()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = Services(server, "host");
        using var player = Services(server, "player");
        using var listener = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing());
        var (toHost, atHost) = await Connect(player, await Find(player), listener);
        atHost.Send("last words");

        await host.Lobbies.RemoveAsync("lobby-1");
        await Until(() => !toHost.IsOpen);
        Assert.Equal("last words", await Receive(toHost)); // what came before the end is still read
    }

    [Fact]
    public async Task SeveralPlayers_EachHaveTheirOwnConnection()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = Services(server, "host");
        using var listener = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing());
        var lobby = await Find(host);

        using var first = Services(server, "first");
        using var second = Services(server, "second");
        var (firstToHost, firstAtHost) = await Connect(first, lobby, listener);
        var (secondToHost, secondAtHost) = await Connect(second, lobby, listener);

        firstToHost.Send("from the first");
        secondToHost.Send("from the second");
        Assert.Equal("from the first", await Receive(firstAtHost));
        Assert.Equal("from the second", await Receive(secondAtHost));
        secondAtHost.Send("to the second");
        Assert.Equal("to the second", await Receive(secondToHost));
        Assert.False(firstToHost.TryReceive(out _));
    }

    [Fact]
    public async Task AHostTakingDirectConnections_IsReachedWithoutTheRelay()
    {
        await using var server = await RelayServer.StartAsync();
        using var tcp = new TcpConnectionListener(0);
        using var host = Services(server, "host");
        using var player = Services(server, "player");
        using var relayed = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing(port: tcp.Port));

        var toHost = await player.Network.ConnectAsync(await Find(player));
        Assert.IsType<TcpConnection>(toHost);
        var atHost = await Accept(tcp);
        toHost.Send("direct");
        Assert.Equal("direct", await Receive(atHost));
        Assert.False(relayed.TryAccept(out _));
    }

    [Fact]
    public async Task WhenTheDirectConnectionFails_TheRelayCarriesIt()
    {
        await using var server = await RelayServer.StartAsync();
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var host = Services(server, "host");
        using var player = Services(server, "player");
        using var listener = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing(port: closedPort));

        var (toHost, atHost) = await Connect(player, await Find(player), listener);
        Assert.IsNotType<TcpConnection>(toHost);
        toHost.Send("relayed");
        Assert.Equal("relayed", await Receive(atHost));
    }

    [Fact]
    public async Task JoiningAGoneLobby_IsRefusedWithTheServersReason()
    {
        await using var server = await RelayServer.StartAsync();
        using var player = Services(server, "player");
        var refused = await Assert.ThrowsAsync<WebSocketRefusedException>(() => player.Network.ConnectAsync(Listing("gone")));
        Assert.Equal(404, refused.Status);
    }

    [Fact]
    public async Task WorksCombinedWithAnotherService_ThroughTheProviderItWasFoundOn()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = new CombinedOnlineServices(new InMemoryOnlineServices(new InMemoryOnlineServices.Backend()), Services(server, "host"));
        using var player = new CombinedOnlineServices(new InMemoryOnlineServices(new InMemoryOnlineServices.Backend()), Services(server, "player"));
        await host.SignInAsync("Host");
        await player.SignInAsync("Player");
        using var listener = host.Network.Listen("lobby-1");
        await host.Lobbies.PublishAsync(Listing());

        // The player's own in-process service doesn't have it; the relay does.
        var lobby = await Until(() => player.Lobbies.SearchAsync(new LobbyQuery("1.0.0", "content")).Result.SingleOrDefault());
        Assert.Equal("Internet relay", lobby.Provider);
        var (toHost, atHost) = await Connect(player, lobby, listener);
        toHost.Send("combined");
        Assert.Equal("combined", await Receive(atHost));
    }
}
