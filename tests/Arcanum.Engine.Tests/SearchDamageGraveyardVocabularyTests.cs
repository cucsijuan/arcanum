// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Vocabulary for a permanent that triggers when an opponent searches their library, damage that heals the other damage marked
/// on a creature, creatures that enter without triggering while a permanent is on the battlefield, cards that are exiled instead of
/// going to a graveyard, descend, a name chosen from a graveyard card and exiled from every zone of its owner, and a searched
/// artifact that enters with counters.
/// </summary>
public class SearchDamageGraveyardVocabularyTests
{
    private static readonly ObjectFilter BasicLand = new(CardType.Land, Supertype: Supertype.Basic);

    private static CardDefinition Spell(string name, string cost, TargetSpec? target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = target is null ? Array.Empty<TargetSpec>() : new[] { target }, Effects = effects },
    };

    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    /// <summary>A scenario in which the attacker never attacks; each test sets how it acts.</summary>
    private static Scenario Quiet()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    // ------------------------------------------------------------------ "whenever an opponent searches their library" (701.23)

    /// <summary>A creature whose controller draws a card whenever an opponent searches their library.</summary>
    private static CardDefinition Watcher() => Creature("Test Watcher", 1, 1) with
    {
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility { Trigger = TriggerEvent.OpponentSearchesLibrary, Effects = new Effect[] { new DrawCards(1, Subject.You) } },
        },
    };

    [Fact]
    public async Task AnOpponentsSearchOfTheirLibraryTriggersThePermanentsController()
    {
        var s = Quiet();
        s.Lands(P0, 1);
        s.Add(P1, Watcher());
        var seek = s.InHand(P0, Spell("Test Seek", "{R}", null, new SearchLibrary(BasicLand, 1, Zone.Hand)));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == seek) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList(); // a basic land is found
        await s.RunUntilTurn();
        int castAt = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == seek);
        Assert.True(castAt >= 0);
        Assert.Equal(Zone.Graveyard, s.Card(seek).Zone);
        Assert.Single(s.Game.Log.Skip(castAt).OfType<CardMoved>().Where(m => m.Owner == P0 && m.From == Zone.Library && m.To == Zone.Hand));
        var draw = Assert.Single(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P1));
        Assert.Equal(Zone.Hand, s.Card(draw.Card).Zone);
        Assert.Empty(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P0));
    }

    [Fact]
    public async Task ASearchOfYourOwnLibraryDoesNotTriggerYourOwnPermanent()
    {
        var s = Quiet();
        s.Add(P1, GenericCards.Mountain);
        s.Add(P1, Watcher());
        var seek = s.InHand(P1, Spell("Test Seek", "{R}", null, new SearchLibrary(BasicLand, 1, Zone.Hand)) with { Types = CardType.Instant });
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == seek) as PlayerAction ?? PassPriority.Instance;
        s.Defender.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList(); // a basic land is found
        await s.RunUntilTurn();
        int castAt = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == seek);
        Assert.True(castAt >= 0); // the instant was cast on the other player's turn
        Assert.Equal(Zone.Graveyard, s.Card(seek).Zone);
        Assert.Single(s.Game.Log.Skip(castAt).OfType<CardMoved>().Where(m => m.Owner == P1 && m.From == Zone.Library && m.To == Zone.Hand));
        Assert.Empty(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P1)); // no trigger for their own search
    }

    // ------------------------------------------------------------------ damage that heals other damage

    /// <summary>
    /// Deals 2 damage, then 3 more, to a creature of the opponent's. Returns the creature and the damage it had after the second spell
    /// resolved (-1 when it never got there); a creature that dies has no damage left to read, so it is checked by its zone.
    /// </summary>
    private static async Task<(Scenario S, CardId Creature, int Damage)> TwoBolts(CardDefinition creature)
    {
        var s = Quiet();
        s.Lands(P0, 2);
        var first = s.InHand(P0, Spell("Test Bolt Two", "{R}", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), new DealDamage(2, Subject.TargetAt(0))));
        var second = s.InHand(P0, Spell("Test Bolt Three", "{R}", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), new DealDamage(3, Subject.TargetAt(0))));
        var target = s.Add(P1, creature);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(target) };
        int? damage = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(second).Zone == Zone.Graveyard) damage ??= s.Card(target).Damage; // read before cleanup removes damage
            if (s.Card(first).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == first) is { } a) return a;
            if (s.Card(first).Zone == Zone.Graveyard && s.Card(second).Zone == Zone.Hand
                && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == second) is { } b) return b;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        return (s, target, damage ?? -1);
    }

    [Fact]
    public async Task DamageToACreatureWithDamageHealsOtherDamageLeavesOnlyTheNewDamage()
    {
        var (s, creature, damage) = await TwoBolts(Creature("Test Healer", 3, 5) with { DamageHealsOtherDamage = true });
        Assert.Equal(Zone.Battlefield, s.Card(creature).Zone); // 3 damage on a 3/5 does not kill it
        Assert.Equal(3, damage); // the 2 already dealt was healed, not added to
    }

    [Fact]
    public async Task WithoutDamageHealsOtherDamageTheDamageAddsUpAndTheCreatureDies()
    {
        var (s, creature, _) = await TwoBolts(Creature("Test Sturdy", 3, 5));
        Assert.Equal(Zone.Graveyard, s.Card(creature).Zone); // 2 + 3 = 5 damage on a 3/5
    }

    // ------------------------------------------------------------------ creatures entering without triggering

    /// <summary>A card that gains 3 life when it enters the battlefield.</summary>
    private static CardDefinition LifeOnEnter(CardDefinition card) => card with
    {
        Abilities = new AbilityDefinition[]
        {
            new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield, Effects = new Effect[] { new GainLife(3, Subject.You) } },
        },
    };

    /// <summary>A permanent that stops creatures entering the battlefield from triggering abilities.</summary>
    private static CardDefinition Hush() => new()
    {
        Name = "Test Hush", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Artifact, EnteringCreaturesDontTrigger = true,
    };

    [Fact]
    public async Task ACreatureEnteringDoesNotTriggerWhileAPermanentWithEnteringCreaturesDontTriggerIsOnTheBattlefield()
    {
        var s = Quiet();
        s.Add(P1, Hush());
        s.Lands(P0, 1);
        var visitor = s.InHand(P0, LifeOnEnter(Creature("Test Visitor", 2, 2)));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == visitor) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(visitor).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life); // no life gained: the enters trigger did not happen
    }

    [Fact]
    public async Task AnArtifactEnteringStillTriggersWhileAPermanentWithEnteringCreaturesDontTriggerIsOnTheBattlefield()
    {
        var s = Quiet();
        s.Add(P1, Hush());
        s.Lands(P0, 1);
        var gadget = s.InHand(P0, LifeOnEnter(new CardDefinition { Name = "Test Gadget", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Artifact }));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == gadget) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(gadget).Zone);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // the artifact's enters trigger still happens
    }

    // ------------------------------------------------------------------ cards exiled instead of going to a graveyard

    /// <summary>A permanent that exiles every card and token that would be put into a graveyard.</summary>
    private static CardDefinition Undertow() => new()
    {
        Name = "Test Undertow", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment, AllExiledInsteadOfGraveyard = true,
    };

    [Fact]
    public async Task ACreatureThatIsDestroyedIsExiledAndItsDiesTriggerDoesNotTrigger()
    {
        var s = Quiet();
        s.Add(P1, Undertow());
        s.Lands(P0, 1);
        var victim = s.Add(P1, Creature("Test Victim", 2, 2) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.Dies, Effects = new Effect[] { new GainLife(3, Subject.You) } },
            },
        });
        var wrath = s.InHand(P0, Spell("Test Wrath", "{R}", new TargetSpec(TargetKind.Creature), new Destroy(Subject.TargetAt(0))));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(victim) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == wrath) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(victim).Zone);
        Assert.Contains(victim, s.Game.State.GetPlayer(P1).Exile);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life); // the dies trigger never happened
    }

    [Fact]
    public async Task ACardDiscardedIsExiledInsteadOfGoingToTheGraveyard()
    {
        var s = Quiet();
        s.Add(P1, Undertow());
        s.Lands(P0, 1);
        var bear = s.InHand(P0, Creature("Test Bear", 2, 2));
        var sift = s.InHand(P0, Spell("Test Sift", "{R}", null, new Discard(1, Subject.You)));
        s.Attacker.Discard = (_, _) => new[] { bear };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sift) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(bear).Zone);
        Assert.Contains(bear, s.Game.State.GetPlayer(P0).Exile);
    }

    [Fact]
    public async Task ATokenThatDiesIsExiled()
    {
        var s = Quiet();
        s.Add(P1, Undertow());
        s.Lands(P0, 1);
        var token = s.Add(P0, Creature("Test Token", 1, 1) with { IsToken = true });
        var bolt = s.InHand(P0, Spell("Test Bolt", "{R}", new TargetSpec(TargetKind.Creature), new DealDamage(1, Subject.TargetAt(0))));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(token) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bolt) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.DoesNotContain(token, s.Game.State.GetPlayer(P0).Graveyard);
        Assert.Equal(Zone.Exile, s.Card(token).Zone);
    }

    // ------------------------------------------------------------------ descend ("if you descended this turn")

    /// <summary>Gains 5 life if its controller descended this turn, otherwise 1.</summary>
    private static CardDefinition Descent() => Spell("Test Descent", "{R}", null,
        new IfThen(new TurnHappened(TurnEvent.YouDescended), new Effect[] { new GainLife(5, Subject.You) }, new Effect[] { new GainLife(1, Subject.You) }));

    [Fact]
    public async Task AnIfDescendedSpellGainsOneLifeWhenNothingWasPutIntoYourGraveyardThisTurn()
    {
        var s = Quiet();
        s.Lands(P0, 1);
        var descent = s.InHand(P0, Descent());
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == descent) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(descent).Zone);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AnIfDescendedSpellGainsFiveLifeAfterACreatureCardWasDiscardedThisTurn()
    {
        var s = Quiet();
        s.Lands(P0, 2);
        var bear = s.InHand(P0, Creature("Test Bear", 2, 2));
        var sift = s.InHand(P0, Spell("Test Sift", "{R}", null, new Discard(1, Subject.You)));
        var descent = s.InHand(P0, Descent());
        s.Attacker.Discard = (_, _) => new[] { bear };
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(sift).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sift) is { } first) return first;
            if (s.Card(sift).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == descent) is { } second) return second;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task AnInstantCardPutIntoYourGraveyardDoesNotCountAsDescending()
    {
        var s = Quiet();
        s.Lands(P0, 2);
        var charm = s.InHand(P0, Spell("Test Charm", "{R}", null, new GainLife(1, Subject.You)) with { Types = CardType.Instant });
        var sift = s.InHand(P0, Spell("Test Sift", "{R}", null, new Discard(1, Subject.You)));
        var descent = s.InHand(P0, Descent());
        s.Attacker.Discard = (_, _) => new[] { charm };
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(sift).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sift) is { } first) return first;
            if (s.Card(sift).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == descent) is { } second) return second;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(charm).Zone);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // the instant is not a permanent card: still 1 life
    }

    // ------------------------------------------------------------------ name a card from an opponent's graveyard and exile it everywhere

    [Fact]
    public async Task NameThenExileOffersAtMostFourCardsWithTheChosenNameAndExilesAtMostFour()
    {
        var s = Quiet();
        s.Lands(P0, 1);
        var twin = Creature("Test Twin", 2, 2);
        var cards = new List<CardId> { PutInGraveyard(s, P1, twin), s.InHand(P1, twin), s.InHand(P1, twin) };
        for (int i = 0; i < 3; i++) cards.Add(s.Game.SetupInLibrary(P1, twin));
        var purge = s.InHand(P0, Spell("Test Purge", "{R}", null,
            new NameThenExileFromAllZones(Subject.EachOpponent, new ObjectFilter(CardType.Creature)) { MaxCount = 4 }));
        CardChoiceRequest? offered = null;
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == purge) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Option = (_, request) => request.Options.ToList().IndexOf("Test Twin");
        s.Attacker.Choose = (_, request) =>
        {
            offered = request;
            return request.Options.Take(request.Max).Select(o => o.Id).ToList(); // the most the card allows: four
        };
        await s.RunUntilTurn();
        Assert.NotNull(offered);
        Assert.Equal(6, offered!.Options.Count); // every card with the name in the opponent's graveyard, hand and library
        Assert.Equal(4, offered.Max);
        Assert.Equal(4, cards.Count(id => s.Card(id).Zone == Zone.Exile));
        Assert.Equal(2, cards.Count(id => s.Card(id).Zone != Zone.Exile));
    }

    [Fact]
    public async Task ExilingAGraveyardCardExilesEveryCardWithItsNameAndTheOwnerDrawsForEachExiledFromHand()
    {
        var s = Quiet();
        s.Lands(P0, 1);
        var beast = Creature("Test Twin Beast", 2, 2);
        var graveyardCopy = PutInGraveyard(s, P1, beast);
        var handCopy = s.InHand(P1, beast);
        var libraryCopy = s.Game.SetupInLibrary(P1, beast);
        var purge = s.InHand(P0, Spell("Test Purge", "{R}", null,
            new NameThenExileFromAllZones(Subject.Self, ObjectFilter.Anything) { FromOpponentsGraveyardCard = true }));
        bool restacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            // The opening hand is drawn when the game starts and may have taken this card: put it back on top of the library.
            if (!restacked) { restacked = true; s.Restack(P1, libraryCopy); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == purge) as PlayerAction ?? PassPriority.Instance;
        };
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList(); // scripted: every offered card is picked
        await s.RunUntilTurn();
        int castAt = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == purge);
        Assert.True(castAt >= 0);
        Assert.Equal(Zone.Exile, s.Card(graveyardCopy).Zone);
        Assert.Equal(Zone.Exile, s.Card(handCopy).Zone);
        Assert.Equal(Zone.Exile, s.Card(libraryCopy).Zone);
        Assert.Contains(s.Game.Log.Skip(castAt), e => e is LibraryShuffled l && l.Player == P1);
        var draw = Assert.Single(s.Game.Log.Skip(castAt).OfType<CardDrawn>().Where(d => d.Player == P1));
        Assert.Equal(Zone.Hand, s.Card(draw.Card).Zone); // one card exiled from their hand, so one drawn
    }

    // ------------------------------------------------------------------ searched artifact enters with counters

    [Fact]
    public async Task AFoundArtifactEntersWithSevenChargeCounters()
    {
        var s = Quiet();
        s.Lands(P0, 1);
        var gadget = s.Game.SetupInLibrary(P0, new CardDefinition { Name = "Test Gadget", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Artifact });
        var seek = s.InHand(P0, Spell("Test Seek", "{R}", null, new SearchLibrary(new ObjectFilter(CardType.Artifact), 1, Zone.Battlefield)
        {
            EntersWithCounter = CounterKind.Charge, EntersWithCounterCount = 7,
        }));
        bool restacked = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, gadget); } // the opening hand may have taken it
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == seek) as PlayerAction ?? PassPriority.Instance;
        };
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList(); // the artifact is found
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(gadget).Zone);
        Assert.Equal(7, s.Card(gadget).CounterCount(CounterKind.Charge));
    }
}
