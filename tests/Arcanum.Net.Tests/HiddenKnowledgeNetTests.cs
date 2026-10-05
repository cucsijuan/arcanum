// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Net.Host;
using Arcanum.Net.Protocol;

namespace Arcanum.Net.Tests;

/// <summary>
/// Over the hosted protocol, each player is sent the hidden cards they know (looked at, revealed) and nothing else: a card
/// they don't know has no name, and no id that would let them follow it into a library or through a shuffle.
/// </summary>
public class HiddenKnowledgeNetTests
{
    /// <summary>"As this enters, look at an opponent's hand, then choose any card name."</summary>
    private static readonly CardDefinition PeekingGlade = GenericCards.Forest with { Name = "Peeking Glade", Supertypes = 0, ChooseOnEnter = EnterChoice.CardName };

    /// <summary>"When this enters, reveal the top card of your library" (it stays there).</summary>
    private static readonly CardDefinition SeeingGlade = GenericCards.Forest with
    {
        Name = "Seeing Glade", Supertypes = 0,
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield, Text = "reveal the top card of your library",
                Effects = new Effect[] { new RevealTop(new ObjectFilter(CardType.Artifact), false, Array.Empty<Effect>()) } },
        },
    };

    /// <summary>
    /// "When this enters, reveal the top card of your library, then search your library for an artifact card and shuffle,
    /// then reveal cards from the top until you reveal an artifact card" (there is none: the whole library is revealed,
    /// then put on the bottom in a random order).
    /// </summary>
    private static readonly CardDefinition StirringGlade = GenericCards.Forest with
    {
        Name = "Stirring Glade", Supertypes = 0,
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield, Text = "reveal the top card, search and shuffle, then reveal the whole library",
                Effects = new Effect[]
                {
                    new RevealTop(new ObjectFilter(CardType.Artifact), false, Array.Empty<Effect>()),
                    new SearchLibrary(new ObjectFilter(CardType.Artifact), 1, Zone.Hand),
                    new RevealUntil(new ObjectFilter(CardType.Artifact), Zone.Hand),
                } },
        },
    };

    private static IReadOnlyList<CardDefinition> Deck() =>
        NetTable.RedGreen().Concat(NetTable.Deck((PeekingGlade, 6), (SeeingGlade, 8), (StirringGlade, 6))).ToList();

    /// <summary>Every card id inside a message, and every card as the player was shown it.</summary>
    private static void Walk(object? value, List<CardId> ids, List<CardView> cards)
    {
        switch (value)
        {
            case null or string or JsonElement or Enum: return;
            case CardId id: ids.Add(id); return;
            case CardView card:
                cards.Add(card);
                ids.Add(card.Id);
                if (card.AttachedTo is { } host) ids.Add(host);
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary) { Walk(entry.Key, ids, cards); Walk(entry.Value, ids, cards); }
                return;
            case IEnumerable list:
                foreach (var item in list) Walk(item, ids, cards);
                return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.Namespace?.StartsWith("System") == true && !type.IsGenericType) return;
        foreach (var property in type.GetProperties().Where(p => p.GetIndexParameters().Length == 0))
            Walk(property.GetValue(value), ids, cards);
    }

    [Fact]
    public void PlayersReceiveTheHiddenCardsTheyKnowAndCantFollowTheOnesTheyDont()
    {
        var seats = new[] { new HostSeat("Player 1", Deck()), new HostSeat("Player 2", Deck()) };
        var table = new NetTable(seed: 11, seats: seats);
        table.Host.Start();
        Assert.True(table.RunUntil(() => table.GameOver), "game did not finish");
        table.Step();
        var log = table.Host.Game.Log;
        Assert.Contains(log, e => e is HandLookedAt);
        Assert.Contains(log, e => e is CardsRevealed);
        Assert.Contains(log, e => e is LibraryShuffled);

        int followedThroughShuffles = 0;
        for (int seat = 0; seat < 2; seat++)
        {
            var me = new PlayerId(seat);
            var format = new WireFormat();
            var lastSeen = new Dictionary<CardId, CardView>();
            var lost = new HashSet<CardId>(); // ids that must never come back
            bool sawOpponentsHand = false, sawOpponentsLibrary = false;
            foreach (var message in table.Wires[seat]!.Received.Select(format.Read))
            {
                var ids = new List<CardId>();
                var cards = new List<CardView>();
                Walk(message, ids, cards);
                Assert.DoesNotContain(ids, lost.Contains);
                foreach (var card in cards)
                {
                    if (card.IsHidden) Assert.True(card.Name is null && card.OracleText == "" && card.AbilityTexts.Count == 0);
                    lastSeen[card.Id] = card;
                }
                switch (message)
                {
                    case ViewUpdate { View: var view }:
                        // A library card known before but no longer shown is unknown now (put back unseen): its id is gone.
                        var known = view.Players.SelectMany(p => p.KnownLibrary).Select(k => k.Card.Id).ToHashSet();
                        foreach (var (id, _) in lastSeen.Where(kv => kv.Value is { Zone: Zone.Library, IsHidden: false } && !known.Contains(kv.Key)).ToList())
                        {
                            lost.Add(id);
                            lastSeen.Remove(id);
                        }
                        var opponent = view.Players.Single(p => p.Id != me);
                        sawOpponentsHand |= opponent.Hand.Any(c => !c.IsHidden);
                        sawOpponentsLibrary |= opponent.KnownLibrary.Count > 0;
                        Assert.All(view.Players.SelectMany(p => p.KnownLibrary), k => Assert.False(k.Card.IsHidden));
                        if (opponent.LibraryTop is { } top) Assert.Contains(opponent.KnownLibrary, k => k.Position == 0 && k.Card.Id == top.Id);
                        break;
                    case Happened { Event: { Event: CardMoved { To: Zone.Library } moved } ev } when ev.Card(moved.Card) is { IsHidden: true }:
                        lost.Add(moved.Card); // went in unseen
                        break;
                    case Happened { Event: { Event: LibraryShuffled shuffled } }:
                        foreach (var (id, card) in lastSeen.Where(kv => kv.Value.Zone == Zone.Library && kv.Value.Owner == shuffled.Player).ToList())
                        {
                            if (!card.IsHidden) followedThroughShuffles++;
                            lost.Add(id);
                            lastSeen.Remove(id);
                        }
                        break;
                }
            }
            Assert.True(sawOpponentsHand, $"seat {seat} never saw a card of the opponent's hand it knew");
            Assert.True(sawOpponentsLibrary, $"seat {seat} never saw a card of the opponent's library it knew");
        }
        Assert.True(followedThroughShuffles > 0, "no known library card was shuffled away");
    }
}
