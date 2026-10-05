// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Arcanum.Engine.Cards;
using Arcanum.Net.Client;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Services;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Tests;

public class PacketRelayTests
{
    /// <summary>Peers on one in-process packet service with tiny packets, so messages are always split.</summary>
    private sealed class Hub
    {
        public readonly ConcurrentDictionary<string, Link> Peers = new();
        public Link Join(string name) => Peers[name] = new Link(this, name);

        public sealed class Link(Hub hub, string name) : IPacketLink
        {
            public readonly ConcurrentQueue<(string, byte[])> Inbox = new();
            public readonly ConcurrentQueue<string> Lost = new();
            public int MaxPacketSize => 16;

            public void Send(string peer, byte[] packet)
            {
                Assert.True(packet.Length <= MaxPacketSize);
                if (hub.Peers.TryGetValue(peer, out var other)) other.Inbox.Enqueue((name, packet));
            }

            public bool TryReceive([NotNullWhen(true)] out string? peer, [NotNullWhen(true)] out byte[]? packet)
            {
                if (Inbox.TryDequeue(out var item)) { (peer, packet) = item; return true; }
                peer = null; packet = null;
                return false;
            }

            public bool TryTakeClosedPeer([NotNullWhen(true)] out string? peer) => Lost.TryDequeue(out peer);
        }
    }

    /// <summary>Keeps the host answering in the background, as the lobby's polling does.</summary>
    private static CancellationTokenSource KeepPumping(PacketRelay relay)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) { relay.Pump(); await Task.Delay(5); }
        });
        return stop;
    }

    private static string Receive(IConnection connection)
    {
        for (int i = 0; i < 100; i++)
            if (connection.TryReceive(out var message)) return message;
        throw new Xunit.Sdk.XunitException("nothing received");
    }

    [Fact]
    public async Task LongMessagesArriveWholeAndInOrderOnSeveralConnections()
    {
        var hub = new Hub();
        var host = new PacketRelay(hub.Join("host"));
        var ann = new PacketRelay(hub.Join("ann"));
        using var listener = host.Listen();
        using var pumping = KeepPumping(host);
        var first = await ann.ConnectAsync("host", TimeSpan.FromSeconds(2));
        var second = await ann.ConnectAsync("host", TimeSpan.FromSeconds(2));
        Assert.True(listener.TryAccept(out var hostFirst));
        Assert.True(listener.TryAccept(out var hostSecond));

        string big = string.Concat(Enumerable.Range(0, 300).Select(i => $"{i}é,"));
        first.Send(big);
        first.Send("");
        second.Send("other");
        Assert.Equal(big, Receive(hostFirst!));
        Assert.Equal("", Receive(hostFirst!));
        Assert.Equal("other", Receive(hostSecond!));
        hostFirst!.Send("back");
        Assert.Equal("back", Receive(first));
    }

    [Fact]
    public async Task ClosingAndLosingAPeerCloseTheConnections()
    {
        var hub = new Hub();
        var host = new PacketRelay(hub.Join("host"));
        var ann = new PacketRelay(hub.Join("ann"));
        using var listener = host.Listen();
        using var pumping = KeepPumping(host);
        var a = await ann.ConnectAsync("host", TimeSpan.FromSeconds(2));
        var b = await ann.ConnectAsync("host", TimeSpan.FromSeconds(2));
        listener.TryAccept(out var hostA);
        listener.TryAccept(out var hostB);

        a.Send("last words");
        a.Close();
        Assert.Equal("last words", Receive(hostA!)); // received before the close is still read
        Assert.False(hostA!.IsOpen);
        Assert.True(hostB!.IsOpen);

        pumping.Cancel();
        hub.Peers["host"].Lost.Enqueue("ann");
        Assert.False(hostB.IsOpen);
    }

    [Fact]
    public async Task NobodyListeningRefusesTheConnection()
    {
        var hub = new Hub();
        _ = new PacketRelay(hub.Join("host"));
        var ann = new PacketRelay(hub.Join("ann"));
        var pump = Task.Run(async () =>
        {
            // The host side has to process packets to answer.
            var host = new PacketRelay(hub.Peers["host"]);
            for (int i = 0; i < 100; i++) { host.Pump(); await Task.Delay(10); }
        });
        await Assert.ThrowsAsync<IOException>(() => ann.ConnectAsync("host", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CombinedServicesPublishEverywhereListEachLobbyOnceAndJoinThroughTheRightOne()
    {
        var lan = new InMemoryOnlineServices.Backend();
        var internet = new InMemoryOnlineServices.Backend();
        using var host = new CombinedOnlineServices(new InMemoryOnlineServices(lan, "host"), new Named(new InMemoryOnlineServices(internet, "host"), "Internet"));
        using var nearby = new CombinedOnlineServices(new InMemoryOnlineServices(lan, "near"), new Named(new InMemoryOnlineServices(internet, "near"), "Internet"));
        using var faraway = new CombinedOnlineServices(new InMemoryOnlineServices(new InMemoryOnlineServices.Backend(), "far"), new Named(new InMemoryOnlineServices(internet, "far"), "Internet"));

        var lobby = new LobbyHost(new LobbySettings("Casual", false, 20, 3, "1", "c"), list => new DeckCheck(Array.Empty<CardDefinition>(), null, null));
        var listing = new LobbyListing(LobbyIds.New(), "Host", "Casual", false, 3, 2, "1", "c", InviteCode.New());
        lobby.AddListener(host.Network.Listen(listing.LobbyId));
        listing = await host.Lobbies.PublishAsync(listing);

        var near = Assert.Single(await nearby.Lobbies.SearchAsync(new LobbyQuery("1", "c")));
        Assert.Equal("In-process", near.Provider); // the local service comes first
        var far = await faraway.Lobbies.FindByCodeAsync(listing.InviteCode);
        Assert.Equal("Internet", far?.Provider);

        var guests = new[]
        {
            new LobbyClient(await nearby.Network.ConnectAsync(near), new ClientIdentity("Near", "", "1", "c")),
            new LobbyClient(await faraway.Network.ConnectAsync(far!), new ClientIdentity("Far", "", "1", "c")),
        };
        for (int i = 0; i < 20 && guests.Any(g => g.Seat < 0); i++)
        {
            lobby.Poll();
            foreach (var guest in guests) guest.Poll();
        }
        Assert.All(guests, g => Assert.True(g.Seat > 0));

        await host.Lobbies.UpdateAsync(listing with { OpenSeats = 0 });
        Assert.Empty(await faraway.Lobbies.SearchAsync(new LobbyQuery("1", "c")));
        await host.Lobbies.RemoveAsync(listing.LobbyId);
        Assert.Null(await nearby.Lobbies.FindByCodeAsync(listing.InviteCode));
    }

    /// <summary>A service under another name (two in-memory services standing for two kinds of service).</summary>
    private sealed class Named(IOnlineServices inner, string name) : IOnlineServices
    {
        public string Name => name;
        public bool IsAvailable => inner.IsAvailable;
        public Task<OnlineIdentity> SignInAsync(string displayName, CancellationToken cancel = default) => inner.SignInAsync(displayName, cancel);
        public ILobbyDirectory Lobbies => inner.Lobbies;
        public IRelayNetwork Network => inner.Network;
        public void Dispose() => inner.Dispose();
    }
}
