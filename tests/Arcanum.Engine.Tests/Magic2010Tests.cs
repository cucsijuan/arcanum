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
    public async Task TelepathyRevealsOpponentsHandsOnlyToItsController()
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
}
