// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Net.Client;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;

namespace Arcanum.Net.Tests;

public class OnlineServicesTests
{
    private static LobbyListing Listing(string host, string code, int open = 1, string version = "1") =>
        new(LobbyIds.New(), host, "Casual", false, 2, open, version, "c", code);

    [Fact]
    public void InviteCodesAreShortReadableAndForgiving()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => InviteCode.New()).ToList();
        Assert.All(codes, c => Assert.Equal(InviteCode.Length, c.Length));
        Assert.All(codes, c => Assert.DoesNotContain(c, ch => "01OIL".Contains(ch)));
        Assert.True(codes.Distinct().Count() > 190);
        Assert.Equal("ABC-DEF", InviteCode.Display("ABCDEF"));
        Assert.True(InviteCode.TryParse(" abc-def ", out var parsed));
        Assert.Equal("ABCDEF", parsed);
        Assert.False(InviteCode.TryParse("ABC-DE", out _));
        Assert.False(InviteCode.TryParse("ABC-D0F", out _)); // 0 is never part of a code
    }

    [Fact]
    public async Task TheDirectoryListsMatchingLobbiesAndFindsCodes()
    {
        var backend = new InMemoryOnlineServices.Backend();
        using var host = new InMemoryOnlineServices(backend, "host");
        using var player = new InMemoryOnlineServices(backend, "player");
        var open = await host.Lobbies.PublishAsync(Listing("Ann", "AAAAAA"));
        await host.Lobbies.PublishAsync(Listing("Full", "BBBBBB", open: 0));
        await host.Lobbies.PublishAsync(Listing("Old", "CCCCCC", version: "0"));
        await host.Lobbies.PublishAsync(Listing("Private", "DDDDDD") with { Listed = false });

        var found = await player.Lobbies.SearchAsync(new LobbyQuery("1", "c"));
        Assert.Equal(new[] { "Ann" }, found.Select(l => l.HostName).ToArray());
        Assert.Equal("Private", (await player.Lobbies.FindByCodeAsync("ddd-ddd"))?.HostName);

        await host.Lobbies.UpdateAsync(open with { OpenSeats = 0 });
        Assert.Empty(await player.Lobbies.SearchAsync(new LobbyQuery("1", "c")));
        Assert.Equal(2, (await player.Lobbies.SearchAsync(new LobbyQuery("1", "c", OnlyWithOpenSeats: false))).Count); // Ann and Full
        await host.Lobbies.RemoveAsync(open.LobbyId);
        Assert.Null(await player.Lobbies.FindByCodeAsync("AAAAAA"));
    }

    [Fact]
    public async Task APlayerJoinsALobbyFoundByItsInviteCodeThroughTheServiceNetwork()
    {
        var backend = new InMemoryOnlineServices.Backend();
        using var hostServices = new InMemoryOnlineServices(backend, "host");
        using var playerServices = new InMemoryOnlineServices(backend, "player");
        var lobby = new LobbyHost(new LobbySettings("Casual", false, 20, 2, "1", "c"), list => new DeckCheck(Array.Empty<CardDefinition>(), null, null));
        var listing = Listing("Host", InviteCode.New());
        lobby.AddListener(hostServices.Network.Listen(listing.LobbyId));
        listing = await hostServices.Lobbies.PublishAsync(listing);

        var found = await playerServices.Lobbies.FindByCodeAsync(InviteCode.Display(listing.InviteCode));
        Assert.NotNull(found);
        var guest = new LobbyClient(await playerServices.Network.ConnectAsync(found!), new ClientIdentity("Guest", "", "1", "c"));
        for (int i = 0; i < 10 && guest.Seat < 0; i++)
        {
            lobby.Poll();
            guest.Poll();
        }
        Assert.Equal(1, guest.Seat);
    }

    [Fact]
    public async Task OnTheLocalNetworkLobbiesAreAnnouncedFoundAndJoined()
    {
        int port = 47090 + Random.Shared.Next(9);
        using var hostServices = new LanOnlineServices(port);
        using var playerServices = new LanOnlineServices(port);
        var lobby = new LobbyHost(new LobbySettings("Casual", false, 20, 2, "1", "c"), list => new DeckCheck(Array.Empty<CardDefinition>(), null, null));
        var listing = Listing("Host", InviteCode.New());
        lobby.AddListener(hostServices.Network.Listen(listing.LobbyId));
        listing = await hostServices.Lobbies.PublishAsync(listing);
        Assert.True(listing.Port > 0);

        var found = await playerServices.Lobbies.SearchAsync(new LobbyQuery("1", "c"));
        var mine = Assert.Single(found, l => l.LobbyId == listing.LobbyId);
        Assert.NotNull(mine.Address);
        Assert.Equal(listing.LobbyId, (await playerServices.Lobbies.FindByCodeAsync(listing.InviteCode))?.LobbyId);

        var guest = new LobbyClient(await playerServices.Network.ConnectAsync(mine), new ClientIdentity("Guest", "", "1", "c"));
        for (int i = 0; i < 200 && guest.Seat < 0; i++)
        {
            lobby.Poll();
            guest.Poll();
            await Task.Delay(10);
        }
        Assert.Equal(1, guest.Seat);
    }
}
