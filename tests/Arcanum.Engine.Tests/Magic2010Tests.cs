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

/// <summary>Magic 2010 cards with copies, libraries, continuous effects and hidden information, played from their scripts.</summary>
public class Magic2010Tests
{
    private static CardDefinition M(string name) => Magic2010Cards.Get(name);

    /// <summary>Player 0 casts or activates whatever it can (no land drop) and never attacks; player 1 only passes.</summary>
    private static Scenario Casting(int seed = 1)
    {
        var s = new Scenario(seed);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, _) => PassPriority.Instance;
        return s;
    }

    private static void Lands(Scenario s, PlayerId who, CardDefinition land, int count)
    {
        for (int i = 0; i < count; i++) s.Add(who, land);
    }

    private static Player P(Scenario s, PlayerId id) => s.Game.State.GetPlayer(id);

    private static CardDefinition Black(string name, int power, int toughness) => Creature(name, power, toughness) with { ManaCost = ManaCost.Parse("{B}") };

    /// <summary>Runs <paramref name="setup"/> once, as player 0 first gets to act (after opening hands are drawn).</summary>
    private static void OnFirstAction(Scenario s, Action setup)
    {
        bool done = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (v, legal) =>
        {
            if (!done) { done = true; setup(); return PassPriority.Instance; }
            return act(s.Game.ViewFor(P0), s.Game.GetLegalActions(P0));
        };
    }

    private static void MoveTo(Scenario s, CardId id, Zone zone)
    {
        var card = s.Card(id);
        var owner = P(s, card.Owner);
        owner.Library.Remove(id);
        owner.Hand.Remove(id);
        owner.Graveyard.Remove(id);
        owner.Exile.Remove(id);
        card.Zone = zone;
        owner.GetZone(zone).Add(id);
    }

    // ------------------------------------------------------------------ Doom Blade, Dread Warlock, Silence, Soul Bleed

    [Fact]
    public async Task DoomBladeCantTargetABlackCreature()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Swamp, 2);
        var black = s.Add(P1, Black("Shade", 2, 2));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, M("Doom Blade"));
        IReadOnlyList<Target>? legal = null;
        s.Attacker.Targets = (v, r) => { legal = r.LegalAt(0); return TestController.FirstAllowed(v, r); };
        await s.RunUntilTurn();
        Assert.NotNull(legal);
        Assert.DoesNotContain(Target.Of(black), legal!);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(black).Zone);
    }

    [Fact]
    public async Task DreadWarlockCanBeBlockedOnlyByBlackCreatures()
    {
        var s = new Scenario();
        var warlock = s.Add(P0, M("Dread Warlock"));
        var black = s.Add(P1, Black("Shade", 1, 1));
        var bear = s.Add(P1, Creature("Bear", 1, 1));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn(4);
        var request = s.Defender.LastBlockRequest;
        Assert.NotNull(request);
        Assert.Contains(warlock, request!.CanBlock[black]);
        Assert.False(request.CanBlock.TryGetValue(bear, out var bearCan) && bearCan.Contains(warlock));
    }

    [Fact]
    public async Task SilenceStopsOnlyOpponentsFromCasting()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Plains, 1);
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Mountain, 1);
        var silence = s.InHand(P0, M("Silence"));
        var bolt = s.InHand(P0, GenericCards.EmberBolt);
        s.InHand(P1, GenericCards.EmberBolt);
        bool opponentCouldCast = false;
        s.Defender.Act = (_, legal) =>
        {
            if (s.Card(silence).Zone == Zone.Graveyard && s.Game.State.TurnNumber == 1 && legal.OfType<CastSpell>().Any()) opponentCouldCast = true;
            return PassPriority.Instance;
        };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == silence)
                                       ?? (s.Card(silence).Zone == Zone.Graveyard ? legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bolt) : null)
                                       ?? (PlayerAction)PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.False(opponentCouldCast);
        Assert.Equal(Zone.Graveyard, s.Card(bolt).Zone); // its controller still casts spells
    }

    [Fact]
    public async Task SoulBleedDrainsTheEnchantedCreaturesControllerInTheirUpkeep()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, M("Soul Bleed"), bear);
        await s.RunUntilTurn(4);
        Assert.Equal(19, P(s, P1).Life); // turn 2's upkeep (theirs); not in player 0's upkeeps
        Assert.Equal(20, P(s, P0).Life);
    }

    // ------------------------------------------------------------------ Traumatize, Ponder, Lurking Predators, Polymorph

    [Fact]
    public async Task TraumatizeMillsHalfTheLibraryRoundedDown()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 5);
        s.InHand(P0, M("Traumatize"));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        await s.RunUntilTurn();
        Assert.Equal(6, P(s, P1).Graveyard.Count); // 13 cards: 6 milled
        Assert.Equal(7, P(s, P1).Library.Count);
    }

    [Fact]
    public async Task PonderOrdersTheTopThreeMayShuffleThenDraws()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 1);
        var cards = new[] { s.Game.SetupInLibrary(P0, Creature("Alpha", 1, 1)), s.Game.SetupInLibrary(P0, Creature("Beta", 1, 1)), s.Game.SetupInLibrary(P0, Creature("Gamma", 1, 1)) };
        s.InHand(P0, M("Ponder"));
        OnFirstAction(s, () => s.Restack(P0, cards));
        var purposes = new List<CardChoicePurpose>();
        // Order: Gamma on top, then Alpha, then Beta.
        s.Attacker.Choose = (_, r) =>
        {
            purposes.Add(r.Purpose);
            var pick = r.Options.FirstOrDefault(o => o.Id == cards[2]) ?? r.Options.First(o => o.Id == cards[0]);
            return new[] { pick.Id };
        };
        s.Attacker.YesNo = (_, r) => false; // don't shuffle
        await s.RunUntilTurn();
        Assert.All(purposes, p => Assert.Equal(CardChoicePurpose.Order, p)); // only ordering: no "choose up to 0" prompt
        Assert.Equal(2, purposes.Count);
        Assert.Contains(cards[2], P(s, P0).Hand.Concat(P(s, P0).Graveyard)); // drew Gamma
        Assert.Equal(new[] { cards[0], cards[1] }, P(s, P0).Library.Take(2));
    }

    [Fact]
    public async Task LurkingPredatorsPutsARevealedCreatureOntoTheBattlefield()
    {
        var s = Casting();
        s.Add(P0, M("Lurking Predators"));
        var wurm = s.Game.SetupInLibrary(P0, GenericCards.GreatWurm);
        Lands(s, P1, GenericCards.Mountain, 1);
        s.InHand(P1, GenericCards.EmberBolt);
        OnFirstAction(s, () => s.Restack(P0, wurm));
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(wurm).Zone);
        Assert.Equal(P0, s.Card(wurm).Controller);
    }

    [Fact]
    public async Task LurkingPredatorsMayPutANoncreatureCardOnTheBottom()
    {
        var s = Casting();
        s.Add(P0, M("Lurking Predators"));
        var bolt = s.Game.SetupInLibrary(P0, GenericCards.EmberBolt);
        Lands(s, P1, GenericCards.Mountain, 1);
        s.InHand(P1, GenericCards.EmberBolt);
        OnFirstAction(s, () => s.Restack(P0, bolt));
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        bool asked = false;
        s.Attacker.YesNo = (_, r) => { asked |= r.Prompt.Contains("bottom"); return true; };
        await s.RunUntilTurn();
        Assert.True(asked);
        Assert.Equal(bolt, P(s, P0).Library[^1]);
    }

    [Fact]
    public async Task PolymorphOnAnIndestructibleCreatureStillRevealsForItsController()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 4);
        var golem = s.Add(P1, Creature("Golem", 3, 3, Keyword.Indestructible));
        var land = s.Game.SetupInLibrary(P1, GenericCards.Mountain);
        var wurm = s.Game.SetupInLibrary(P1, GenericCards.GreatWurm);
        s.InHand(P0, M("Polymorph"));
        OnFirstAction(s, () => s.Restack(P1, land, wurm));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(golem) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone); // not destroyed
        Assert.Equal(Zone.Battlefield, s.Card(wurm).Zone);
        Assert.Equal(P1, s.Card(wurm).Controller); // its controller puts it onto the battlefield
        Assert.Equal(Zone.Library, s.Card(land).Zone); // shuffled back
        Assert.Contains(s.Game.Log, e => e is LibraryShuffled { Player.Value: 1 });
    }

    [Fact]
    public async Task PolymorphDestroysWithoutRegeneration()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 4);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var wurm = s.Game.SetupInLibrary(P1, GenericCards.GreatWurm);
        s.InHand(P0, M("Polymorph"));
        OnFirstAction(s, () => { s.Restack(P1, wurm); s.Card(bear).RegenerationShields = 1; });
        s.Attacker.Targets = (_, r) => new[] { Target.Of(bear) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(wurm).Zone);
    }

    // ------------------------------------------------------------------ Rise from the Grave, Awakener Druid, Ajani, CDAs

    [Fact]
    public async Task RiseFromTheGraveMakesItABlackZombieInAddition()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Swamp, 5);
        var bear = s.Game.SetupInLibrary(P1, GenericCards.GladeCub);
        s.InHand(P0, M("Rise from the Grave"));
        OnFirstAction(s, () => MoveTo(s, bear, Zone.Graveyard));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(bear) };
        await s.RunUntilTurn();
        var card = s.Card(bear);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal(P0, card.Controller);
        Assert.True(card.HasSubtype("Zombie") && card.HasSubtype("Bear"));
        Assert.Equal(new[] { "B", "G" }, card.Colors.OrderBy(c => c));
    }

    [Fact]
    public async Task AwakenerDruidAnimatesAForestOnlyWhileItRemains()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Mountain, 3);
        var forest = s.Add(P0, GenericCards.Forest);
        var druid = s.InHand(P0, M("Awakener Druid"));
        var kill = s.InHand(P0, new CardDefinition
        {
            Name = "Kill", Types = CardType.Sorcery, ManaCost = ManaCost.Zero,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        });
        (int Power, int Toughness, bool Creature, bool Land, string Colors, bool Treefolk, bool Forest)? animated = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(druid).Zone == Zone.Hand) return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == druid) ?? (PlayerAction)PassPriority.Instance;
            if (s.Card(druid).Zone == Zone.Battlefield && s.Card(forest).IsCreature && animated is null)
            {
                var f = s.Card(forest);
                animated = (f.Power, f.Toughness, f.IsCreature, f.Is(CardType.Land), string.Join("", f.Colors), f.HasSubtype("Treefolk"), f.HasSubtype("Forest"));
            }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == kill) ?? (PlayerAction)PassPriority.Instance;
        };
        s.Attacker.Targets = (_, r) => r.LegalAt(0).Contains(Target.Of(forest)) && !r.LegalAt(0).Contains(Target.Of(druid)) ? new[] { Target.Of(forest) } : new[] { Target.Of(druid) };
        await s.RunUntilTurn();
        Assert.Equal((4, 5, true, true, "G", true, true), animated);
        Assert.Equal(Zone.Graveyard, s.Card(druid).Zone);
        Assert.False(s.Card(forest).IsCreature);
        Assert.Empty(s.Card(forest).Colors);
    }

    [Fact]
    public async Task AjanisAvatarHasPowerAndToughnessEqualToYourLife()
    {
        var s = Casting();
        var ajani = s.Add(P0, M("Ajani Goldmane"));
        s.Card(ajani).Counters[CounterKind.Loyalty] = 6;
        s.Attacker.Act = (_, legal) =>
        {
            return legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ajani && a.Index == 2) ?? (PlayerAction)PassPriority.Instance;
        };
        await s.RunUntilTurn();
        var avatar = s.Game.State.Battlefield.Select(s.Card).Single(c => c.Name == "Avatar");
        Assert.Equal((20, 20), (avatar.Power, avatar.Toughness));
        Assert.Equal(new[] { "W" }, avatar.Colors);
        Assert.Equal(20, s.Game.ViewFor(P1).FindCard(avatar.Id)!.Power);
        Assert.Equal(Zone.Graveyard, s.Card(ajani).Zone); // no loyalty left
    }

    [Fact]
    public async Task AjaniPutsCountersOnCreaturesAndGivesThemVigilance()
    {
        var s = Casting();
        var ajani = s.Add(P0, M("Ajani Goldmane"));
        s.Card(ajani).Counters[CounterKind.Loyalty] = 4;
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var foe = s.Add(P1, Creature("Foe", 2, 2));
        bool vigilant = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne) > 0) vigilant = s.Card(bear).Has(Keyword.Vigilance);
            return legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == ajani && a.Index == 1) ?? (PlayerAction)PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(1, s.Card(bear).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(0, s.Card(foe).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.True(vigilant);
        Assert.False(s.Card(bear).Has(Keyword.Vigilance)); // until end of turn
        Assert.Equal(3, s.Card(ajani).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task CharacteristicDefiningPowerAndToughnessWorkInEveryZone()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Swamp, 2);
        var swampCount = new Quantity(0, QuantityKind.PermanentCount, new ObjectFilter(CardType.Land, Subtype: "Swamp"));
        var nightmare = Creature("Nightmare", 0, 0) with { Power = null, Toughness = null, PowerFrom = swampCount, ToughnessFrom = swampCount, ManaCost = ManaCost.Parse("{5}{B}") };
        var inHand = s.InHand(P0, nightmare);
        var inLibrary = s.Game.SetupInLibrary(P0, nightmare);
        var inGraveyard = s.Game.SetupInLibrary(P1, nightmare);
        OnFirstAction(s, () => MoveTo(s, inGraveyard, Zone.Graveyard));
        await s.RunUntilTurn();
        Assert.Equal((2, 2), (s.Card(inHand).Power, s.Card(inHand).Toughness)); // counts its owner's Swamps
        Assert.Equal(2, s.Card(inLibrary).Power);
        Assert.Equal(0, s.Card(inGraveyard).Power); // its owner (player 1) controls no Swamp
        Assert.Equal(2, s.Game.ViewFor(P0).Self.Hand.First(c => c.Id == inHand).Power);
        Assert.Equal(0, s.Game.ViewFor(P0).Players[1].Graveyard.First(c => c.Id == inGraveyard).Power);
    }

    // ------------------------------------------------------------------ Coat of Arms, Telepathy, Vampire Nocturnus, Convincing Mirage

    [Fact]
    public async Task CoatOfArmsCountsOtherCreaturesSharingATypeWithEachCreature()
    {
        var s = new Scenario();
        s.Add(P0, M("Coat of Arms"));
        var warrior = s.Add(P0, Creature("Goblin Warrior", 1, 1) with { Subtypes = new[] { "Goblin", "Warrior" } });
        var shaman = s.Add(P1, Creature("Goblin Shaman", 1, 1) with { Subtypes = new[] { "Goblin", "Shaman" } });
        var elf = s.Add(P0, Creature("Elf", 1, 1) with { Subtypes = new[] { "Elf" } });
        var changeling = s.Add(P1, Creature("Shapeshifter", 1, 1, Keyword.Changeling) with { Subtypes = new[] { "Shapeshifter" } });
        var wall = s.Add(P0, Creature("Wall", 0, 4));
        await s.RunUntilTurn(1);
        Assert.Equal(3, s.Card(warrior).Power); // the shaman and the changeling
        Assert.Equal(3, s.Card(shaman).Power);
        Assert.Equal(2, s.Card(elf).Power); // the changeling
        Assert.Equal(4, s.Card(changeling).Power); // every other creature with a creature type
        Assert.Equal(0, s.Card(wall).Power); // no creature type
    }

    [Fact]
    public async Task TelepathyRevealsOpponentsHandsButNotItsControllers()
    {
        var s = new Scenario();
        s.Add(P0, M("Telepathy"));
        var secret = s.InHand(P1, GenericCards.GreatWurm);
        var mine = s.InHand(P0, GenericCards.GladeCub);
        await s.RunUntilTurn(1);
        Assert.All(s.Game.ViewFor(P0).Players[1].Hand, c => Assert.False(c.IsHidden));
        Assert.Contains(s.Game.ViewFor(P0).Players[1].Hand, c => c.Id == secret && c.Name == "Great Wurm");
        Assert.All(s.Game.ViewFor(P1).Players[0].Hand, c => Assert.True(c.IsHidden));
        Assert.Contains(s.Game.ViewFor(P1).Players[0].Hand, c => c.Id == mine);
    }

    [Fact]
    public async Task TelepathyRevealsOpponentsHandsToEveryPlayer()
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players)
        {
            p.Act = (_, _) => PassPriority.Instance;
            p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        }
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        var p2 = new PlayerId(2);
        game.SetupPermanent(P0, M("Telepathy"));
        var wurm = game.SetupInHand(P1, GenericCards.GreatWurm);
        var cub = game.SetupInHand(p2, GenericCards.GladeCub);
        var mine = game.SetupInHand(P0, GenericCards.StoneElemental);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 1 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
        // Revealed means to all players (rule 701.20a): player 2 sees player 1's hand and player 1 sees player 2's.
        foreach (var (viewer, owner, card) in new[] { (P0, P1, wurm), (p2, P1, wurm), (P0, p2, cub), (P1, p2, cub) })
        {
            var hand = game.ViewFor(viewer).Players[owner.Value].Hand;
            Assert.All(hand, c => Assert.False(c.IsHidden));
            Assert.Contains(hand, c => c.Id == card && !c.IsHidden);
            Assert.Contains(viewer, game.State.GetCard(card).KnownTo);
        }
        // Telepathy's controller has no opponent controlling one: their hand stays hidden.
        foreach (var viewer in new[] { P1, p2 })
        {
            Assert.All(game.ViewFor(viewer).Players[0].Hand, c => Assert.True(c.IsHidden));
            Assert.DoesNotContain(viewer, game.State.GetCard(mine).KnownTo);
        }
    }

    [Fact]
    public async Task VampireNocturnusPlaysWithTheTopCardRevealedAndPumpsVampiresWhileItIsBlack()
    {
        var s = new Scenario();
        var nocturnus = s.Add(P0, M("Vampire Nocturnus"));
        var vampire = s.Add(P0, Creature("Vampire", 2, 2) with { Subtypes = new[] { "Vampire" } });
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var enemyVampire = s.Add(P1, Creature("Vampire", 2, 2) with { Subtypes = new[] { "Vampire" } });
        var shade = s.Game.SetupInLibrary(P0, Black("Shade", 1, 1));
        (int Noc, int Vamp, int Bear, int Enemy, bool Flying, bool SeenByOpponent)? withBlackTop = null;
        bool done = false;
        s.Attacker.Act = (_, _) =>
        {
            if (!done)
            {
                done = true;
                s.Restack(P0, shade);
                s.Game.State.Battlefield.Remove(bear); s.Game.State.Battlefield.Add(bear); // any event recomputes
            }
            else if (withBlackTop is null)
                withBlackTop = (s.Card(nocturnus).Power, s.Card(vampire).Power, s.Card(bear).Power, s.Card(enemyVampire).Power, s.Card(vampire).Has(Keyword.Flying),
                    s.Game.ViewFor(P1).Players[0].LibraryTop?.Id == shade);
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(2);
        Assert.Equal((5, 4, 2, 2, true, true), withBlackTop);
    }

    [Fact]
    public async Task VampireNocturnusDoesNothingWhileTheTopCardIsntBlack()
    {
        var s = new Scenario();
        var nocturnus = s.Add(P0, M("Vampire Nocturnus"));
        await s.RunUntilTurn(1); // the library is all Forests
        Assert.Equal(3, s.Card(nocturnus).Power);
        Assert.False(s.Card(nocturnus).Has(Keyword.Flying));
        var top = P(s, P0).Library[0];
        Assert.Equal(top, s.Game.ViewFor(P1).Players[0].LibraryTop?.Id);
        Assert.Null(s.Game.ViewFor(P0).Players[1].LibraryTop); // the opponent's isn't revealed
    }

    [Fact]
    public async Task ConvincingMirageSetsTheLandTypeAndReplacesItsManaAbility()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 2);
        var forest = s.Add(P1, GenericCards.Forest);
        var dual = s.Add(P1, new CardDefinition { Name = "Rootbound Crag", Types = CardType.Land, TapForMana = new[] { ManaType.Red, ManaType.Green }, EntersTapped = true });
        s.InHand(P0, M("Convincing Mirage"));
        s.InHand(P0, M("Convincing Mirage"));
        int cast = 0;
        s.Attacker.Targets = (_, r) => new[] { Target.Of(cast++ == 0 ? forest : dual) };
        s.Attacker.Option = (_, r) => Array.IndexOf(new[] { "Plains", "Island", "Swamp", "Mountain", "Forest" }, "Island");
        s.Add(P0, GenericCards.Island);
        s.Add(P0, GenericCards.Island);
        await s.RunUntilTurn();
        foreach (var land in new[] { forest, dual })
        {
            var card = s.Card(land);
            Assert.True(card.HasSubtype("Island"));
            Assert.False(card.HasSubtype("Forest"));
            Assert.Equal(new[] { ManaType.Blue }, card.ManaOptions.SelectMany(o => o.Types).Distinct());
        }
    }

    /// <summary>A land whose text sets every land's type, like Urborg, Tomb of Yawgmoth.</summary>
    private static CardDefinition UrborgLike() => Arcanum.Data.CardData.CardFactory.Create(new Arcanum.Data.CardData.CardRecord
    {
        OracleId = "db6174d7-211d-4817-b8e4-8384594c83f9", Name = "Urborg, Tomb of Yawgmoth", Layout = "normal", ManaCost = "", TypeLine = "Legendary Land",
        OracleText = "Each land is a Swamp in addition to its other land types.", Keywords = Array.Empty<string>(),
    }, Arcanum.Data.Scripts.CardScriptParser.Parse(
        "{\"name\": \"Urborg, Tomb of Yawgmoth\", \"abilities\": [{\"static\": {\"affects\": \"permanents\", \"filter\": {\"types\": [\"land\"]}, "
        + "\"addSubtypes\": [\"Swamp\"]}, \"text\": \"Each land is a Swamp in addition to its other land types.\"}]}")).Definition;

    [Fact]
    public async Task ConvincingMirageOnUrborgMeansUrborgsEffectDoesntExist()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 2);
        var urborg = s.Add(P1, UrborgLike());
        var forest = s.Add(P1, GenericCards.Forest);
        var mirage = s.InHand(P0, M("Convincing Mirage"));
        s.Attacker.Targets = (_, r) => new[] { Target.Of(urborg) };
        s.Attacker.Option = (_, r) => Array.IndexOf(new[] { "Plains", "Island", "Swamp", "Mountain", "Forest" }, "Island");
        bool forestWasSwampBefore = false;
        (bool Urborg, bool Forest)? enchanted = null, afterMirageLeft = null;
        s.Defender.Act = (_, _) =>
        {
            if (enchanted is null && s.Card(mirage).Zone == Zone.Battlefield)
            {
                enchanted = (s.Card(urborg).HasSubtype("Swamp") || !s.Card(urborg).HasSubtype("Island"), s.Card(forest).HasSubtype("Swamp"));
                s.Game.State.Battlefield.Remove(mirage);
                MoveTo(s, mirage, Zone.Graveyard);
            }
            else if (enchanted is not null && afterMirageLeft is null)
                afterMirageLeft = (s.Card(urborg).HasSubtype("Swamp"), s.Card(forest).HasSubtype("Swamp"));
            return PassPriority.Instance;
        };
        OnFirstAction(s, () => forestWasSwampBefore = s.Card(forest).HasSubtype("Swamp"));
        await s.RunUntilTurn(3);
        Assert.True(forestWasSwampBefore);
        Assert.Equal((false, false), enchanted); // Urborg is just an Island: its "each land is a Swamp" doesn't exist (rules 305.7, 613.8a)
        Assert.Equal((true, true), afterMirageLeft); // with the Mirage gone, Urborg's effect is back
    }

    // ------------------------------------------------------------------ card names

    [Fact]
    public async Task PlainCardNameChoiceLooksAtNothingAndOffersOnlyKnownNames()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Mountain, 1);
        s.InHand(P0, Creature("Needle Golem", 1, 1) with { ChooseOnEnter = EnterChoice.CardName });
        s.Game.SetupInLibrary(P1, GenericCards.GreatWurm); // hidden in the opponent's library
        s.InHand(P1, GenericCards.StoneElemental); // hidden in the opponent's hand
        s.Add(P1, GenericCards.HillBrute); // visible
        IReadOnlyList<string>? offered = null;
        OptionKind? kind = null;
        s.Attacker.Option = (_, r) => { offered = r.Options; kind = r.Kind; return 0; };
        await s.RunUntilTurn();
        Assert.Empty(s.Game.Log.OfType<HandLookedAt>());
        Assert.Equal(OptionKind.CardName, kind);
        Assert.NotNull(offered);
        Assert.Contains("Hill Brute", offered!);
        Assert.Contains("Needle Golem", offered!);
        Assert.DoesNotContain("Great Wurm", offered!);
        Assert.DoesNotContain("Stone Elemental", offered!);
    }

    [Fact]
    public async Task CardNameChoiceOffersTheGamesCardNamesWhenGiven()
    {
        var lands = Decks.Of((GenericCards.Forest, 20));
        var a = new TestController();
        var b = new TestController { Act = (_, _) => PassPriority.Instance };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0, CardNames = new[] { "Alpha", "Omega" } },
            new[] { new PlayerSetup("A", a, lands), new PlayerSetup("B", b, lands) });
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupInHand(P0, Creature("Needle Golem", 1, 1) with { ChooseOnEnter = EnterChoice.CardName });
        a.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        IReadOnlyList<string>? offered = null;
        a.Option = (_, r) => { offered = r.Options; return 1; };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(new[] { "Alpha", "Omega" }, offered);
        Assert.Equal("Omega", game.State.Battlefield.Select(game.State.GetCard).Single(c => c.Name == "Needle Golem").ChosenName);
    }

    [Fact]
    public async Task ChoosingANonbasicLandCardNameOffersOnlyThoseNames()
    {
        var lands = Decks.Of((GenericCards.Forest, 20));
        var a = new TestController();
        var b = new TestController { Act = (_, _) => PassPriority.Instance };
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0, CardNames = new[] { "Alpha", "Omega", "Shifting Dunes" }, NonbasicLandNames = new[] { "Shifting Dunes" } },
            new[] { new PlayerSetup("A", a, lands), new PlayerSetup("B", b, lands) });
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupInHand(P0, Creature("Moon Golem", 1, 1) with { ChooseOnEnter = EnterChoice.NonbasicLandCardName });
        a.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        IReadOnlyList<string>? offered = null;
        a.Option = (_, r) => { offered = r.Options; return 0; };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Assert.Equal(new[] { "Shifting Dunes" }, offered); // rule 201.3: not Alpha, Omega nor a basic land's name
        Assert.Equal("Shifting Dunes", game.State.Battlefield.Select(game.State.GetCard).Single(c => c.Name == "Moon Golem").ChosenName);
    }

    // ------------------------------------------------------------------ Clone

    private static CardDefinition Token(string name, int power, int toughness) => Creature(name, power, toughness) with { IsToken = true, Subtypes = new[] { "Soldier" } };

    [Fact]
    public async Task CloneEntersAsACopyWithTheCopiedReplacementEffects()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 4);
        var djinn = s.Add(P1, M("Djinn of Wishes"));
        var clone = s.InHand(P0, M("Clone"));
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == djinn).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        var card = s.Card(clone);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.Equal("Djinn of Wishes", card.Name);
        Assert.Equal((4, 4), (card.Power, card.Toughness));
        Assert.True(card.Has(Keyword.Flying));
        Assert.Equal(3, card.CounterCount(CounterKind.Wish)); // "enters with three wish counters" is part of what it copies
        Assert.Equal(P0, card.Controller);
        Assert.False(card.Definition.IsToken);
    }

    [Fact]
    public async Task CloneCopyingATokenOrACopyGetsTheOriginalsValuesAndStaysACard()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 8);
        var soldier = s.Add(P1, Token("Soldier", 1, 1));
        var first = s.InHand(P0, M("Clone"));
        var second = s.InHand(P0, M("Clone"));
        s.Attacker.Choose = (_, r) => s.Card(first).Zone != Zone.Battlefield
            ? r.Options.Where(o => o.Id == soldier).Select(o => o.Id).ToList()
            : r.Options.Where(o => o.Id == first).Select(o => o.Id).ToList();
        await s.RunUntilTurn();
        foreach (var id in new[] { first, second })
        {
            var card = s.Card(id);
            Assert.Equal(Zone.Battlefield, card.Zone);
            Assert.Equal("Soldier", card.Name);
            Assert.Equal(1, card.Power);
            Assert.False(card.Definition.IsToken);
        }
        Assert.Equal("Clone", s.Card(second).CopyOfName);
    }

    [Fact]
    public async Task CloneCopyingNothingIsAZeroZeroAndDies()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 4);
        s.Add(P1, Creature("Bear", 2, 2));
        var clone = s.InHand(P0, M("Clone"));
        s.Attacker.Choose = (_, r) => Array.Empty<CardId>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(clone).Zone);
        Assert.Equal("Clone", s.Card(clone).Name);
    }

    // ------------------------------------------------------------------ Hive Mind

    [Fact]
    public async Task HiveMindGivesEachOtherPlayerACopyTheyMayRetarget()
    {
        var s = Casting();
        s.Add(P0, M("Hive Mind"));
        Lands(s, P0, GenericCards.Mountain, 1);
        var mine = s.Add(P0, Creature("Mine", 3, 3));
        var theirs = s.Add(P1, Creature("Theirs", 3, 3));
        s.InHand(P0, GenericCards.EmberBolt);
        s.Attacker.Targets = (_, r) => new[] { Target.Of(theirs) };
        s.Defender.YesNo = (_, r) => true;
        s.Defender.Targets = (_, r) => new[] { Target.Of(mine) };
        await s.RunUntilTurn();
        var copied = Assert.Single(s.Game.Log.OfType<SpellCopied>());
        Assert.Equal(P1, copied.Controller);
        Assert.Equal(Zone.Graveyard, s.Card(theirs).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(mine).Zone);
    }

    [Fact]
    public async Task HiveMindCopiesInApnapOrder()
    {
        var a = new TestController();
        var b = new TestController { Act = (_, _) => PassPriority.Instance };
        var c = new TestController { Act = (_, _) => PassPriority.Instance };
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 },
            new[] { new PlayerSetup("A", a, lands), new PlayerSetup("B", b, lands), new PlayerSetup("C", c, lands) });
        game.SetupPermanent(P1, M("Hive Mind"));
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupInHand(P0, GenericCards.EmberBolt with { Spell = new SpellAbility { Effects = new Effect[] { new GainLife(1, Subject.You) } } });
        a.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        a.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        var copies = game.Log.OfType<SpellCopied>().Select(x => x.Controller).ToList();
        Assert.Equal(new[] { new PlayerId(1), new PlayerId(2) }, copies); // APNAP order from the active player who cast it
        Assert.Equal(21, game.State.GetPlayer(new PlayerId(1)).Life);
        Assert.Equal(21, game.State.GetPlayer(new PlayerId(2)).Life);
        Assert.Equal(21, game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ Warp World, Open the Vaults

    private static CardDefinition Might => new()
    {
        Name = "Might", Types = CardType.Enchantment, Subtypes = new[] { "Aura" }, ManaCost = ManaCost.Parse("{G}"),
        EnchantTarget = new TargetSpec(TargetKind.Creature),
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Enchanted), 1, 1) },
    };

    private static CardDefinition Relic => new() { Name = "Relic", Types = CardType.Artifact, ManaCost = ManaCost.Parse("{1}") };

    [Fact]
    public async Task WarpWorldReshufflesPermanentsAndPutsRevealedPermanentCardsOntoTheBattlefield()
    {
        var s = Casting(seed: 3);
        Lands(s, P0, GenericCards.Mountain, 8);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var aura = s.Game.SetupPermanent(P0, Might, bear);
        var token = s.Add(P0, Token("Soldier", 1, 1));
        s.Add(P1, Creature("Theirs", 2, 2));
        var warp = s.InHand(P0, M("Warp World"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(warp).Zone);
        Assert.DoesNotContain(token, s.Game.State.Battlefield);
        Assert.DoesNotContain(token, P(s, P0).Library); // tokens cease to exist
        var revealed = s.Game.Log.OfType<CardsRevealed>().Last(r => r.Player == P0).Cards;
        Assert.Equal(11, revealed.Count); // 8 lands, the Bear, the Aura and the token were shuffled in
        var theirs = s.Game.Log.OfType<CardsRevealed>().Last(r => r.Player == P1).Cards;
        Assert.Single(theirs);
        foreach (var id in revealed.Where(id => s.Card(id).Zone != Zone.Battlefield))
        {
            Assert.True(s.Card(id).HasSubtype("Aura")); // every land and creature card revealed entered
            Assert.Contains(id, P(s, P0).Library.TakeLast(revealed.Count)); // the rest on the bottom
        }
        Assert.All(revealed.Where(id => s.Card(id).Zone == Zone.Battlefield), id => Assert.Equal(P0, s.Card(id).Controller));
        if (s.Card(aura).Zone == Zone.Battlefield) Assert.True(s.Card(s.Card(aura).AttachedTo!.Value).IsCreature);
        Assert.Equal(revealed.Concat(theirs).Count(id => s.Card(id).Zone == Zone.Battlefield), s.Game.State.Battlefield.Count);
    }

    [Fact]
    public async Task OpenTheVaultsReturnsArtifactsAndEnchantmentsAndAnAuraNeedsSomethingAlreadyThere()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Plains, 6);
        var relic = s.Game.SetupInLibrary(P0, Relic);
        var aura = s.Game.SetupInLibrary(P0, Might);
        var golem = s.Game.SetupInLibrary(P1, Creature("Golem", 3, 3) with { Types = CardType.Artifact | CardType.Creature });
        s.InHand(P0, M("Open the Vaults"));
        OnFirstAction(s, () => { MoveTo(s, relic, Zone.Graveyard); MoveTo(s, aura, Zone.Graveyard); MoveTo(s, golem, Zone.Graveyard); });
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(relic).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(golem).Zone);
        Assert.Equal(P1, s.Card(golem).Controller); // under its owner's control
        Assert.Equal(Zone.Graveyard, s.Card(aura).Zone); // the Golem entered at the same time: nothing to enchant
    }

    [Fact]
    public async Task AnAuraReturnedByOpenTheVaultsEnchantsWhatItsOwnerChooses()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Plains, 6);
        var mine = s.Add(P0, Creature("Mine", 2, 2));
        s.Add(P1, Creature("Theirs", 2, 2));
        var aura = s.Game.SetupInLibrary(P1, Might);
        s.InHand(P0, M("Open the Vaults"));
        OnFirstAction(s, () => MoveTo(s, aura, Zone.Graveyard));
        TargetRequest? asked = null;
        s.Defender.Targets = (_, r) => { asked = r; return new[] { Target.Of(mine) }; };
        await s.RunUntilTurn();
        Assert.NotNull(asked);
        Assert.Equal(Zone.Battlefield, s.Card(aura).Zone);
        Assert.Equal(P1, s.Card(aura).Controller);
        Assert.Equal(mine, s.Card(aura).AttachedTo);
        Assert.Equal(3, s.Card(mine).Power);
    }

    // ------------------------------------------------------------------ Mirror of Fate, Haunting Echoes, Sphinx Ambassador

    [Fact]
    public async Task MirrorOfFateRebuildsTheLibraryFromChosenExiledCards()
    {
        var s = Casting();
        var mirror = s.Add(P0, M("Mirror of Fate"));
        var a = s.Game.SetupInLibrary(P0, Creature("Alpha", 1, 1));
        var b = s.Game.SetupInLibrary(P0, Creature("Beta", 1, 1));
        var c = s.Game.SetupInLibrary(P0, Creature("Gamma", 1, 1));
        var theirs = s.Game.SetupInLibrary(P1, Creature("Theirs", 1, 1));
        OnFirstAction(s, () => { MoveTo(s, a, Zone.Exile); MoveTo(s, b, Zone.Exile); MoveTo(s, c, Zone.Exile); MoveTo(s, theirs, Zone.Exile); });
        CardChoiceRequest? offer = null;
        s.Attacker.Choose = (_, r) =>
        {
            if (r.Purpose == CardChoicePurpose.Order) return new[] { r.Options.FirstOrDefault(o => o.Id == c)?.Id ?? r.Options[0].Id };
            offer = r;
            return new[] { a, c };
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(mirror).Zone);
        Assert.NotNull(offer);
        Assert.Equal(new[] { a, b, c }.OrderBy(x => x.Value), offer!.Options.Select(o => o.Id).OrderBy(x => x.Value)); // only cards you own
        Assert.Equal(new[] { c, a }, P(s, P0).Library);
        Assert.Equal(Zone.Exile, s.Card(b).Zone);
        Assert.True(P(s, P0).Exile.Count >= 13);
    }

    [Fact]
    public async Task HauntingEchoesExilesTheGraveyardAndTheNamesakesFound()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Swamp, 5);
        var dead = s.Game.SetupInLibrary(P1, GenericCards.GladeCub);
        var forest = s.Game.SetupInLibrary(P1, GenericCards.Forest);
        var copies = new[] { s.Game.SetupInLibrary(P1, GenericCards.GladeCub), s.Game.SetupInLibrary(P1, GenericCards.GladeCub) };
        s.InHand(P0, M("Haunting Echoes"));
        OnFirstAction(s, () => { MoveTo(s, dead, Zone.Graveyard); MoveTo(s, forest, Zone.Graveyard); s.Restack(P1, copies); });
        s.Attacker.Targets = (_, r) => new[] { Target.Of(P1) };
        CardChoiceRequest? search = null;
        s.Attacker.Choose = (_, r) => { search = r; return new[] { copies[0] }; }; // may find fewer than all (rule 701.19b)
        await s.RunUntilTurn();
        Assert.Equal(new[] { forest }, P(s, P1).Graveyard); // basic land cards stay
        Assert.Equal(Zone.Exile, s.Card(dead).Zone);
        Assert.Equal(0, search!.Min);
        Assert.Equal(2, search.Options.Count);
        Assert.Equal(Zone.Exile, s.Card(copies[0]).Zone);
        Assert.Equal(Zone.Library, s.Card(copies[1]).Zone);
        Assert.Contains(s.Game.Log, e => e is LibraryShuffled { Player.Value: 1 });
    }

    private static Scenario SphinxAttacks(out CardId wurm)
    {
        var s = new Scenario();
        s.Add(P0, M("Sphinx Ambassador"));
        var found = wurm = s.Game.SetupInLibrary(P1, GenericCards.GreatWurm);
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == found).Select(o => o.Id).ToList();
        OnFirstAction(s, () => s.Restack(P1, found));
        return s;
    }

    [Fact]
    public async Task SphinxAmbassadorTakesTheFoundCreatureIfItsOwnerNamesAnotherCard()
    {
        var s = SphinxAttacks(out var wurm);
        bool sawIt = true;
        OptionKind? kind = null;
        s.Defender.Option = (view, r) =>
        {
            kind = r.Kind;
            sawIt = view.Self.KnownLibrary.Any(x => x.Card.Id == wurm);
            return r.Options.ToList().IndexOf("Forest");
        };
        await s.RunUntilTurn(2);
        Assert.Equal(OptionKind.CardName, kind);
        Assert.False(sawIt); // the namer doesn't know which card was found
        Assert.Equal(Zone.Battlefield, s.Card(wurm).Zone);
        Assert.Equal(P0, s.Card(wurm).Controller);
        Assert.Equal(15, P(s, P1).Life);
    }

    [Fact]
    public async Task SphinxAmbassadorGetsNothingWhenTheNameIsRight()
    {
        var s = SphinxAttacks(out var wurm);
        bool askedToPut = false;
        s.Attacker.YesNo = (_, r) => { askedToPut |= r.Prompt.Contains("onto the battlefield"); return true; };
        s.Defender.Option = (_, r) => r.Options.ToList().IndexOf("Great Wurm");
        await s.RunUntilTurn(2);
        Assert.False(askedToPut);
        Assert.Equal(Zone.Library, s.Card(wurm).Zone);
        Assert.Contains(s.Game.Log, e => e is LibraryShuffled { Player.Value: 1 });
    }

    // ------------------------------------------------------------------ Djinn of Wishes

    [Fact]
    public async Task DjinnOfWishesPlaysALandAsTheLandDropThenExilesWhatItCantPlay()
    {
        var s = Casting();
        Lands(s, P0, GenericCards.Island, 17);
        var djinn = s.InHand(P0, M("Djinn of Wishes"));
        var first = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var second = s.Game.SetupInLibrary(P0, GenericCards.Plains);
        var third = s.Game.SetupInLibrary(P0, GenericCards.GladeCub);
        bool stacked = false;
        int activations = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, first, second, third); return PassPriority.Instance; }
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == djinn) is { } cast) return cast;
            if (activations < 3 && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == djinn) is { } activate) { activations++; return activate; }
            return PassPriority.Instance;
        };
        var onEnter = new List<int>();
        s.Game.EventRaised += e => { if (e is CardMoved { To: Zone.Battlefield } m && m.Card == djinn) onEnter.Add(s.Card(djinn).CounterCount(CounterKind.Wish)); };
        s.Attacker.YesNo = (_, _) => true;
        await s.RunUntilTurn();
        Assert.Equal(new[] { 3 }, onEnter);
        Assert.Equal(Zone.Battlefield, s.Card(first).Zone); // played as the land drop
        Assert.Single(s.Game.Log.OfType<LandPlayed>());
        Assert.Equal(Zone.Exile, s.Card(second).Zone); // no land play left: exiled
        Assert.Equal(Zone.Battlefield, s.Card(third).Zone); // cast without paying its mana cost
        Assert.Equal(0, s.Card(djinn).CounterCount(CounterKind.Wish));
    }
}
