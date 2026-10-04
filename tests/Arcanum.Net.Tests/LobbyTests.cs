// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Bots;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Net.Client;
using Arcanum.Net.Host;
using Arcanum.Net.Lobby;
using Arcanum.Net.Protocol;
using Arcanum.Net.Transport;

namespace Arcanum.Net.Tests;

public class LobbyTests
{
    private static readonly Dictionary<string, CardDefinition> Cards = NetTable.RedGreen().DistinctBy(c => c.Name).ToDictionary(c => c.Name);

    /// <summary>"count name" lines; an unknown card makes the deck unplayable.</summary>
    private static DeckCheck Check(string list)
    {
        var deck = new List<CardDefinition>();
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2);
            if (!Cards.TryGetValue(parts[1], out var card)) return new DeckCheck(deck, null, $"Unknown card: {parts[1]}");
            deck.AddRange(Enumerable.Repeat(card, int.Parse(parts[0])));
        }
        return new DeckCheck(deck, null, deck.Count < 40 ? "Fewer than 40 cards." : null);
    }

    private static string List(IReadOnlyList<CardDefinition> deck) =>
        string.Join("\n", deck.GroupBy(c => c.Name).Select(g => $"{g.Count()} {g.Key}"));

    private static ClientIdentity Identity(string name, string token = "") => new(name, token, "1", "c");

    [Fact]
    public void PlayersJoinChooseDecksAndPlayWithTheComputer()
    {
        var clock = new FakeClock();
        var lobby = new LobbyHost(new LobbySettings("Casual", false, 20, 3, "1", "c"), Check, clock.Func);
        var listener = new InMemoryListener();
        lobby.AddListener(listener);

        var host = new LobbyClient(listener.Connect(), Identity("Host", lobby.HostToken), clock.Func);
        host.SubmitDeck("Red-green", List(NetTable.RedGreen())); // right behind the greeting
        var guest = new LobbyClient(listener.Connect(), Identity("Guest"), clock.Func);
        void Step()
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            lobby.Poll();
            host.Poll();
            guest.Poll();
        }
        for (int i = 0; i < 5; i++) Step();
        Assert.Equal(0, host.Seat);
        Assert.Equal(1, guest.Seat);
        Assert.NotEmpty(guest.Identity.Token);
        Assert.Contains("Guest hasn't chosen a deck", lobby.StartProblem);

        // A third player finds the game full once the computer takes the last seat.
        lobby.SetComputer(2, "Computer", "Red-green", List(NetTable.RedGreen()));
        var late = new LobbyClient(listener.Connect(), Identity("Late"), clock.Func);
        for (int i = 0; i < 3; i++) { Step(); late.Poll(); }
        Assert.Equal("The game is full.", late.RejectedReason);

        guest.SubmitDeck("Broken", "40 Mystery Card");
        for (int i = 0; i < 3; i++) Step();
        Assert.Contains("Unknown card", lobby.StartProblem);
        Assert.Contains("Unknown card", guest.State!.Seats[1].Problem);

        guest.SubmitDeck("Red-green", List(NetTable.RedGreen()));
        for (int i = 0; i < 3; i++) Step();
        Assert.Null(lobby.StartProblem);

        var game = lobby.Start(5, new HostOptions { Clock = clock.Func });
        for (int i = 0; i < 3; i++) Step();
        Assert.True(host.Started && guest.Started);

        var clients = new[] { host.JoinGame(), guest.JoinGame() };
        for (int seat = 0; seat < 2; seat++)
        {
            var client = clients[seat];
            var bot = new BotController(new PlayerId(seat)) { AlwaysKeep = true };
            bot.UseCardRules(id => client.View?.FindCard(id) is { IsHidden: false, Name: { } n } ? Cards.GetValueOrDefault(n) : null);
            client.Controller = bot;
        }
        for (int i = 0; i < 200_000 && !game.Game.State.IsGameOver; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            lobby.Poll();
            foreach (var c in clients) c.Poll();
        }
        Assert.True(game.Game.State.IsGameOver);
        Assert.Equal(SeatState.Computer, game.StateOf(new PlayerId(2)));
        Assert.Equal(new[] { "Host", "Guest", "Computer" }, clients[1].Players);
    }

    [Fact]
    public void AGuestWhoDropsInTheLobbyCanTakeTheirSeatBack()
    {
        var clock = new FakeClock();
        var lobby = new LobbyHost(new LobbySettings("Casual", false, 20, 2, "1", "c"), Check, clock.Func);
        var listener = new InMemoryListener();
        lobby.AddListener(listener);
        var guest = new LobbyClient(listener.Connect(), Identity("Guest"), clock.Func);
        for (int i = 0; i < 3; i++) { lobby.Poll(); guest.Poll(); }
        guest.SubmitDeck("Red-green", List(NetTable.RedGreen()));
        var token = guest.Identity.Token;
        for (int i = 0; i < 3; i++) { lobby.Poll(); guest.Poll(); }
        guest.Close();
        lobby.Poll();
        Assert.False(lobby.State.Seats[1].Connected);

        var back = new LobbyClient(listener.Connect(), Identity("Guest", token), clock.Func);
        for (int i = 0; i < 3; i++) { lobby.Poll(); back.Poll(); }
        Assert.Equal(1, back.Seat);
        Assert.True(lobby.State.Seats[1].Connected);
        Assert.Equal("Red-green", lobby.State.Seats[1].Deck); // the deck stays with the seat
    }
}
