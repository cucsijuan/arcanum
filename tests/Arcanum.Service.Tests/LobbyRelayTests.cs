// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Arcanum.CareerService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arcanum.Service.Tests;

/// <summary>The relay on a loopback port, with the player named by an <c>X-Player</c> header instead of a token.</summary>
public sealed class RelayServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayServer(WebApplication app, Uri http)
    {
        _app = app;
        Http = http;
        Ws = new UriBuilder(http) { Scheme = "ws" }.Uri;
    }

    public Uri Http { get; }
    public Uri Ws { get; }

    public static async Task<RelayServer> StartAsync(LobbyRelayOptions? options = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options ?? new LobbyRelayOptions());
        builder.Services.AddSingleton<LobbyRelay>();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapLobbyRelay(http => Task.FromResult(http.Request.Headers["X-Player"].FirstOrDefault()));
        await app.StartAsync();
        return new RelayServer(app, new Uri(app.Urls.First()));
    }

    public async Task<ClientWebSocket> ConnectAsync(string path, string? player)
    {
        var socket = new ClientWebSocket();
        if (player is not null) socket.Options.SetRequestHeader("X-Player", player);
        await socket.ConnectAsync(new Uri(Ws, path), Timeout());
        return socket;
    }

    public HttpClient Client(string player)
    {
        var client = new HttpClient { BaseAddress = Http };
        client.DefaultRequestHeaders.Add("X-Player", player);
        return client;
    }

    public static CancellationToken Timeout(int seconds = 5) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public class LobbyRelayTests
{
    private static LobbyInfo Lobby(string id = "lobby-1", string code = "ABC123", int port = 0) =>
        new(id, "Host", "Standard", false, 2, 1, "1.0.0", "content", code) { Port = port };

    private static Task SendText(WebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            WebSocketMessageType.Text, true, RelayServer.Timeout());

    private static Task SendBinary(WebSocket socket, byte[] data) =>
        socket.SendAsync(data, WebSocketMessageType.Binary, true, RelayServer.Timeout());

    private static async Task<(WebSocketMessageType Type, byte[] Data)> Receive(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        int length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), RelayServer.Timeout());
            length += result.Count;
            if (result.EndOfMessage) return (result.MessageType, buffer[..length]);
        }
    }

    private static async Task<JsonElement> ReceiveJson(WebSocket socket)
    {
        var (type, data) = await Receive(socket);
        Assert.Equal(WebSocketMessageType.Text, type);
        return JsonDocument.Parse(data).RootElement;
    }

    private static byte[] WithPeer(uint peer, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var data = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, peer);
        payload.CopyTo(data, 4);
        return data;
    }

    private static async Task<ClientWebSocket> Host(RelayServer server, LobbyInfo lobby, string player = "host-player")
    {
        var host = await server.ConnectAsync("/play/host", player);
        await SendText(host, new { type = "publish", lobby });
        // Published once the search finds it.
        for (int i = 0; i < 50; i++)
        {
            var found = await server.Client("someone").GetFromJsonAsync<List<LobbyInfo>>($"/lobbies?version={lobby.Version}&content={lobby.Content}&open=false");
            if (found!.Any(l => l.LobbyId == lobby.LobbyId)) return host;
            await Task.Delay(20);
        }
        throw new TimeoutException("The lobby was never listed.");
    }

    [Fact]
    public async Task PublishedLobbyIsFound_WithTheHostsAddress()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby(port: 47013));
        var client = server.Client("browser");

        var found = await client.GetFromJsonAsync<List<LobbyInfo>>("/lobbies?version=1.0.0&content=content");
        var lobby = Assert.Single(found!);
        Assert.Equal("lobby-1", lobby.LobbyId);
        Assert.Equal("127.0.0.1", lobby.Address);
        Assert.Equal(47013, lobby.Port);

        Assert.Empty((await client.GetFromJsonAsync<List<LobbyInfo>>("/lobbies?version=2.0.0&content=content"))!);
        Assert.Equal("lobby-1", (await client.GetFromJsonAsync<LobbyInfo>("/lobbies/code/abc123"))!.LobbyId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/lobbies/code/NOPE")).StatusCode);
    }

    [Fact]
    public async Task FullLobbiesAreHiddenUnlessAsked()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby() with { OpenSeats = 0 });
        var client = server.Client("browser");
        Assert.Empty((await client.GetFromJsonAsync<List<LobbyInfo>>("/lobbies?version=1.0.0&content=content"))!);
        Assert.Single((await client.GetFromJsonAsync<List<LobbyInfo>>("/lobbies?version=1.0.0&content=content&open=false"))!);
    }

    [Fact]
    public async Task PacketsGoBothWays_WithThePlayersNumberOnTheHostsSide()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby());
        using var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");

        var open = await ReceiveJson(host);
        Assert.Equal("open", open.GetProperty("type").GetString());
        uint peer = open.GetProperty("peer").GetUInt32();

        await SendBinary(player, Encoding.UTF8.GetBytes("hello host"));
        var (type, data) = await Receive(host);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(peer, BinaryPrimitives.ReadUInt32BigEndian(data));
        Assert.Equal("hello host", Encoding.UTF8.GetString(data, 4, data.Length - 4));

        await SendBinary(host, WithPeer(peer, "hello player"));
        var (back, reply) = await Receive(player);
        Assert.Equal(WebSocketMessageType.Binary, back);
        Assert.Equal("hello player", Encoding.UTF8.GetString(reply));
    }

    [Fact]
    public async Task EachPlayerGetsItsOwnNumber_AndOnlyItsOwnPackets()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby());
        using var first = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        uint firstPeer = (await ReceiveJson(host)).GetProperty("peer").GetUInt32();
        using var second = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-2");
        uint secondPeer = (await ReceiveJson(host)).GetProperty("peer").GetUInt32();
        Assert.NotEqual(firstPeer, secondPeer);

        await SendBinary(host, WithPeer(secondPeer, "for the second"));
        Assert.Equal("for the second", Encoding.UTF8.GetString((await Receive(second)).Data));
        await SendBinary(host, WithPeer(firstPeer, "for the first"));
        Assert.Equal("for the first", Encoding.UTF8.GetString((await Receive(first)).Data));
    }

    [Fact]
    public async Task APlayerLeaving_IsToldToTheHost()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby());
        var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        uint peer = (await ReceiveJson(host)).GetProperty("peer").GetUInt32();

        await player.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", RelayServer.Timeout());
        var closed = await ReceiveJson(host);
        Assert.Equal("closed", closed.GetProperty("type").GetString());
        Assert.Equal(peer, closed.GetProperty("peer").GetUInt32());
    }

    [Fact]
    public async Task TheHostLeaving_EndsTheLobbyAndClosesItsPlayers()
    {
        await using var server = await RelayServer.StartAsync();
        var host = await Host(server, Lobby());
        using var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        await ReceiveJson(host);

        await host.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", RelayServer.Timeout());
        var (type, _) = await Receive(player);
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, player.CloseStatus);
        Assert.Empty((await server.Client("browser").GetFromJsonAsync<List<LobbyInfo>>("/lobbies?version=1.0.0&content=content&open=false"))!);
    }

    [Fact]
    public async Task TheHostCanClosePlayer()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await Host(server, Lobby());
        using var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        uint peer = (await ReceiveJson(host)).GetProperty("peer").GetUInt32();

        await SendText(host, new { type = "close", peer });
        Assert.Equal(WebSocketMessageType.Close, (await Receive(player)).Type);
    }

    [Fact]
    public async Task AHostMustDescribeItsLobbyFirst()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await server.ConnectAsync("/play/host", "host-player");
        await SendBinary(host, WithPeer(1, "not a lobby"));
        Assert.Equal(WebSocketMessageType.Close, (await Receive(host)).Type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, host.CloseStatus);
    }

    [Fact]
    public async Task AnUnreasonableLobbyIsRefused()
    {
        await using var server = await RelayServer.StartAsync();
        using var host = await server.ConnectAsync("/play/host", "host-player");
        await SendText(host, new { type = "publish", lobby = Lobby() with { HostName = new string('x', 500) } });
        Assert.Equal(WebSocketMessageType.Close, (await Receive(host)).Type);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, host.CloseStatus);
    }

    [Fact]
    public async Task ALobbyIdTakenByAnotherHostIsRefused()
    {
        await using var server = await RelayServer.StartAsync();
        using var first = await Host(server, Lobby());
        using var second = await server.ConnectAsync("/play/host", "other-player");
        await SendText(second, new { type = "publish", lobby = Lobby() });
        Assert.Equal(WebSocketMessageType.Close, (await Receive(second)).Type);
    }

    [Fact]
    public async Task WithoutAnIdentity_NothingIsAllowed()
    {
        await using var server = await RelayServer.StartAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync("/play/host", null));
        var anonymous = new HttpClient { BaseAddress = server.Http };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/lobbies?version=1.0.0&content=content")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/lobbies/code/ABC123")).StatusCode);
    }

    [Fact]
    public async Task JoiningAMissingLobbyIsRefused()
    {
        await using var server = await RelayServer.StartAsync();
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync("/play/join?lobby=nothing", "player-1"));
    }

    [Fact]
    public async Task APlayerHostsOnlySoManyLobbies()
    {
        await using var server = await RelayServer.StartAsync(new LobbyRelayOptions { MaxLobbiesPerPlayer = 1 });
        using var first = await Host(server, Lobby());
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync("/play/host", "host-player"));
        using var other = await Host(server, Lobby("lobby-2", "XYZ789"), "other-player");
    }

    [Fact]
    public async Task ALobbyTakesOnlySoManyPlayers()
    {
        await using var server = await RelayServer.StartAsync(new LobbyRelayOptions { MaxGuestsPerLobby = 1 });
        using var host = await Host(server, Lobby());
        using var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        await ReceiveJson(host);
        await Assert.ThrowsAsync<WebSocketException>(() => server.ConnectAsync("/play/join?lobby=lobby-1", "player-2"));
    }

    [Fact]
    public async Task TooLargeAPacketClosesTheSocket()
    {
        await using var server = await RelayServer.StartAsync(new LobbyRelayOptions { MaxPacket = 1024 });
        using var host = await Host(server, Lobby());
        using var player = await server.ConnectAsync("/play/join?lobby=lobby-1", "player-1");
        await ReceiveJson(host);
        await SendBinary(player, new byte[4096]);
        Assert.Equal(WebSocketMessageType.Close, (await Receive(player)).Type);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, player.CloseStatus);
    }

    [Fact]
    public async Task TheBudgetWaits_OnceTheBurstIsSpent()
    {
        var budget = new ByteBudget(perSecond: 1000, burst: 1000);
        var clock = Stopwatch.StartNew();
        await budget.SpendAsync(1000, RelayServer.Timeout());
        Assert.True(clock.ElapsedMilliseconds < 100);
        await budget.SpendAsync(500, RelayServer.Timeout());
        Assert.True(clock.ElapsedMilliseconds >= 400, $"waited only {clock.ElapsedMilliseconds} ms");
    }
}
