// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// What players know about cards in hidden zones: cards looked at or revealed stay known to those who saw them while
/// they can follow them, and nobody knows a card's place once its library is shuffled or it's put back unseen.
/// </summary>
public class HiddenKnowledgeTests
{
    private static CardDefinition Sorcery(string name, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Effects = effects },
    };

    /// <summary>"Reveal the top card of your library" (nothing happens with it: it stays on top).</summary>
    private static readonly CardDefinition TopGlancing = Sorcery("Top Glancing", new RevealTop(new ObjectFilter(CardType.Artifact), false, Array.Empty<Effect>()));

    /// <summary>A search that finds nothing, then shuffles.</summary>
    private static readonly CardDefinition DeckStirring = Sorcery("Deck Stirring", new SearchLibrary(new ObjectFilter(CardType.Artifact), 1, Zone.Hand));

    private static readonly CardDefinition TwofoldGaze = Sorcery("Twofold Gaze", new Scry(2));
    private static readonly CardDefinition OnefoldGaze = Sorcery("Onefold Gaze", new Scry(1));

    /// <summary>"As this enters, look at an opponent's hand, then choose any card name."</summary>
    private static readonly CardDefinition HandSnooper = Creature("Hand Snooper", 1, 1) with { ChooseOnEnter = EnterChoice.LookAtOpponentsHandThenCardName };

    /// <summary>Player 0 plays a land and casts whatever it can, targets the opponent when possible and never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, r) => r.Legal.Select(choices => choices.FirstOrDefault(t => t.Player == P1) is { Player: not null } p ? p : choices[0]).ToList();
        return s;
    }

    /// <summary>Player 0 casts these spells in this order (and plays no land).</summary>
    private static void CastInOrder(Scenario s, params CardId[] order) =>
        s.Attacker.Act = (_, legal) => order.Select(id => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == id)).OfType<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;

    private static PlayerView Of(GameView view, PlayerId player) => view.Players[player.Value];

    [Fact]
    public async Task AnOpponentWhoLookedAtYourHandKeepsSeeingThoseCardsButNotTheNextDraw()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, HandSnooper);
        var looked = new List<CardId>();
        var events = new List<(EventView Owner, EventView Other)>();
        s.Game.EventRaised += e =>
        {
            if (e is not HandLookedAt l) return;
            looked.AddRange(l.Cards);
            events.Add((EventViews.Build(s.Game.State, e, P1), EventViews.Build(s.Game.State, e, P0)));
        };
        List<CardView>? handAtTurnThree = null;
        s.Game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 3 }) handAtTurnThree = Of(s.Game.ViewFor(P0), P1).Hand.ToList(); };
        await s.RunUntilTurn(3);

        var look = Assert.Single(s.Game.Log.OfType<HandLookedAt>());
        Assert.Equal(P0, look.Looker);
        Assert.Equal(P1, look.Player);
        Assert.Equal(7, looked.Count);
        // The hand's owner is told whose hand is looked at, and both of them see the cards.
        Assert.All(looked, id => Assert.True(events[0].Owner.IsVisible(id) && events[0].Other.IsVisible(id)));

        Assert.NotNull(handAtTurnThree);
        var drawn = s.Game.Log.OfType<CardDrawn>().Last(d => d.Player == P1).Card; // in their turn, after the look
        Assert.All(handAtTurnThree!, c => Assert.Equal(c.Id == drawn, c.IsHidden));
        Assert.Contains(handAtTurnThree!, c => c.Id == drawn);
        Assert.True(handAtTurnThree!.Count(c => !c.IsHidden) >= 6); // the looked-at cards still in hand (one land was played)
        // The hand's owner still sees everything; nobody else learned anything about the library.
        Assert.All(Of(s.Game.ViewFor(P1), P1).Hand, c => Assert.False(c.IsHidden));
        Assert.Empty(Of(s.Game.ViewFor(P0), P1).KnownLibrary);
    }

    [Fact]
    public async Task TheOwnerSeesTheCardTheOpponentChoosesFromTheirRevealedHand()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, new CardDefinition
        {
            Name = "Hand Picking", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility
            {
                Targets = new[] { new TargetSpec(TargetKind.Player, ControllerFilter.Opponent) },
                Effects = new Effect[] { new DiscardChosenByYou(Subject.TargetAt(0), new ObjectFilter(CardType.Land)) },
            },
        });
        EventView? ownerSaw = null;
        s.Game.EventRaised += e => { if (e is ChosenFromHand) ownerSaw = EventViews.Build(s.Game.State, e, P1); };
        await s.RunUntilTurn();

        var revealed = Assert.Single(s.Game.Log.OfType<HandRevealed>());
        Assert.Equal(P0, revealed.Chooser);
        var chosen = Assert.Single(s.Game.Log.OfType<ChosenFromHand>());
        Assert.Equal((P0, P1), (chosen.Chooser, chosen.Player));
        var card = Assert.Single(chosen.Cards);
        Assert.True(ownerSaw!.IsVisible(card));
        Assert.Equal(Zone.Hand, ownerSaw.Card(card)!.Zone); // announced while still in the hand, before the discard
        Assert.Equal(card, s.Game.Log.OfType<CardDiscarded>().Single().Card);
        // The rest of the revealed hand stays known to the opponent.
        Assert.All(Of(s.Game.ViewFor(P0), P1).Hand, c => Assert.False(c.IsHidden));
    }

    [Fact]
    public async Task ARevealedCardLeftOnTopIsKnownToEveryoneUntilTheLibraryIsShuffled()
    {
        var s = Casting();
        s.Lands(P0, 2);
        var glance = s.InHand(P0, TopGlancing);
        var stir = s.InHand(P0, DeckStirring);
        CastInOrder(s, glance, stir);
        CardId? top = null;
        CardView? opponentSawTop = null, ownerSawTop = null;
        s.Game.EventRaised += e =>
        {
            if (e is not SpellResolved r || r.Card != glance) return;
            top = s.Game.State.GetPlayer(P0).Library[0];
            opponentSawTop = Of(s.Game.ViewFor(P1), P0).LibraryTop;
            ownerSawTop = Of(s.Game.ViewFor(P0), P0).LibraryTop;
            Assert.Equal(new[] { 0 }, Of(s.Game.ViewFor(P1), P0).KnownLibrary.Select(k => k.Position));
        };
        await s.RunUntilTurn();

        Assert.NotNull(top);
        Assert.Equal(top, opponentSawTop?.Id);
        Assert.False(opponentSawTop!.IsHidden);
        Assert.Equal("Forest", opponentSawTop.Name);
        Assert.Equal(top, ownerSawTop?.Id);
        Assert.Contains(s.Game.Log, e => e is LibraryShuffled { Player.Value: 0 });
        // Shuffled: no one knows any card of that library any more.
        foreach (var viewer in new[] { P0, P1 })
        {
            Assert.Null(Of(s.Game.ViewFor(viewer), P0).LibraryTop);
            Assert.Empty(Of(s.Game.ViewFor(viewer), P0).KnownLibrary);
        }
    }

    [Fact]
    public async Task AKnownCardDrawnStaysKnownToThoseWhoSawIt()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, TopGlancing);
        CardId? top = null;
        CardView? opponentSaw = null;
        s.Game.EventRaised += e =>
        {
            if (e is CardsRevealed r) top = r.Cards[0];
            if (e is CardDrawn d && d.Card == top) opponentSaw = Of(s.Game.ViewFor(P1), P0).Hand.Single(c => c.Id == d.Card);
        };
        await s.RunUntilTurn(4);
        Assert.NotNull(opponentSaw);
        Assert.False(opponentSaw!.IsHidden);
    }

    [Fact]
    public async Task ScryingTellsOnlyTheScryingPlayerWhatIsOnTopAndInWhatOrder()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, TwofoldGaze);
        s.Attacker.Choose = (_, request) => request.Purpose == CardChoicePurpose.Order
            ? new[] { request.Options[^1].Id } // reverse their order
            : Array.Empty<CardId>(); // keep both on top
        await s.RunUntilTurn();

        var library = s.Game.State.GetPlayer(P0).Library;
        var mine = Of(s.Game.ViewFor(P0), P0);
        Assert.Equal(new[] { (0, library[0]), (1, library[1]) }, mine.KnownLibrary.Select(k => (k.Position, k.Card.Id)));
        Assert.All(mine.KnownLibrary, k => Assert.False(k.Card.IsHidden));
        Assert.Equal(library[0], mine.LibraryTop?.Id);
        var theirs = Of(s.Game.ViewFor(P1), P0);
        Assert.Empty(theirs.KnownLibrary);
        Assert.Null(theirs.LibraryTop);
    }

    [Fact]
    public async Task CardsPutBackInAnOrderOthersDontSeeAreNoLongerKnownToThem()
    {
        var s = Casting();
        s.Lands(P0, 3);
        var glance = s.InHand(P0, TopGlancing);
        var squint = s.InHand(P0, OnefoldGaze);
        var peek = s.InHand(P0, TwofoldGaze);
        CastInOrder(s, glance, squint, peek);
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Min).Select(o => o.Id).ToList(); // keep everything on top
        CardId? revealed = null;
        bool knownAfterScryOne = false;
        s.Game.EventRaised += e =>
        {
            if (e is CardsRevealed r) revealed = r.Cards[0];
            // Scry 1 kept it on top: a single card can be followed, so everyone still knows it.
            if (e is SpellResolved { } done && done.Card == squint)
                knownAfterScryOne = Of(s.Game.ViewFor(P1), P0).LibraryTop?.Id == revealed;
        };
        await s.RunUntilTurn();

        Assert.True(knownAfterScryOne);
        // Scry 2: two cards were put back face down in an order only their owner saw.
        Assert.Empty(Of(s.Game.ViewFor(P1), P0).KnownLibrary);
        Assert.Contains(Of(s.Game.ViewFor(P0), P0).KnownLibrary, k => k.Card.Id == revealed);
    }

    [Fact]
    public async Task ACardExiledFaceDownIsNeverShownToThoseWhoDidNotSeeIt()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, Sorcery("Sly Exiling", new ExileTopFaceDown(Subject.EachOpponent)));
        var moves = new List<(EventView Owner, EventView Exiler, CardId Card)>();
        s.Game.EventRaised += e =>
        {
            if (e is CardMoved { From: Zone.Library, To: Zone.Exile } m)
                moves.Add((EventViews.Build(s.Game.State, e, P1), EventViews.Build(s.Game.State, e, P0), m.Card));
        };
        await s.RunUntilTurn();

        var (owner, exiler, card) = Assert.Single(moves);
        Assert.False(owner.IsVisible(card)); // only the player who looked may see it
        Assert.True(exiler.IsVisible(card));
        Assert.True(Assert.Single(Of(s.Game.ViewFor(P1), P1).Exile).IsHidden);
        Assert.False(Assert.Single(Of(s.Game.ViewFor(P0), P1).Exile).IsHidden);
    }
}
