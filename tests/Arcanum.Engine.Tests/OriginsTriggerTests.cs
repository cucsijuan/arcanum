// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Data.Scripts;
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
/// Magic Origins triggers and turn tracking: becoming renowned, becoming blocked, becoming the target of your spells and abilities,
/// spells cast last turn, damage an object dealt this turn, players dealt damage this way and "one or more … to a player".
/// The card scripts are the module's.
/// </summary>
public partial class ScriptedVocabularyTests
{
    private const string RenownOne = """
        { "trigger": "combatDamageToPlayer", "if": { "not": "renowned" },
          "effects": [{ "counters": 1, "what": "self" }, { "becomeRenowned": "self" }], "text": "Renown 1" }
        """;

    private const string RelicSeekerScript = $$"""
        { "abilities": [ {{RenownOne}},
          { "trigger": "becomesRenowned", "effects": [{ "may": "Search?", "effects": [
              { "search": { "types": ["artifact"], "subtype": "Equipment" }, "count": 1, "to": "hand" } ] }],
            "text": "When this creature becomes renowned, you may search your library for an Equipment card, reveal it, put it into your hand, then shuffle." } ] }
        """;

    private const string ValeronWardensScript = """
        { "abilities": [
          { "trigger": "combatDamageToPlayer", "if": { "not": "renowned" },
            "effects": [{ "counters": 2, "what": "self" }, { "becomeRenowned": "self" }], "text": "Renown 2" },
          { "trigger": "creatureBecomesRenowned", "filter": { "types": ["creature"] }, "effects": [{ "draw": 1 }],
            "text": "Whenever a creature you control becomes renowned, draw a card." } ] }
        """;

    private const string SomberwaldAlphaScript = """
        { "abilities": [
          { "trigger": "creatureBecomesBlocked", "filter": { "types": ["creature"] }, "effects": [{ "pump": [1, 1], "what": "triggered" }],
            "text": "Whenever a creature you control becomes blocked, it gets +1/+1 until end of turn." },
          { "cost": "{1}{G}", "targets": ["creature:you"], "effects": [{ "pump": [0, 0], "what": "target", "keywords": ["Trample"] }],
            "text": "{1}{G}: Target creature you control gains trample until end of turn." } ] }
        """;

    private const string CallOfTheFullMoonScript = """
        { "aura": "creature", "abilities": [
          { "static": { "affects": "enchanted", "pump": [3, 2], "keywords": ["Trample"] }, "text": "Enchanted creature gets +3/+2 and has trample." },
          { "trigger": "eachUpkeep", "if": { "spellsCastLastTurn": 2 }, "effects": [{ "sacrificeIt": "self" }],
            "text": "At the beginning of each upkeep, if a player cast two or more spells last turn, sacrifice this Aura." } ] }
        """;

    private const string WillbreakerScript = """
        { "abilities": [
          { "trigger": "becomesTargetOfYours", "filter": { "types": ["creature"], "controller": "opponent" },
            "effects": [{ "gainControl": "triggered", "whileYouControl": true }],
            "text": "Whenever a creature an opponent controls becomes the target of a spell or ability you control, gain control of that creature for as long as you control this creature." } ] }
        """;

    private const string ThopterSpyNetworkScript = """
        { "abilities": [
          { "trigger": "upkeep", "if": { "control": { "types": ["artifact"] } },
            "effects": [{ "tokens": 1, "token": { "name": "Thopter", "types": "Artifact Creature — Thopter", "power": 1, "toughness": 1, "keywords": ["Flying"] } }],
            "text": "At the beginning of your upkeep, if you control an artifact, create a 1/1 colorless Thopter artifact creature token with flying." },
          { "trigger": "creatureCombatDamageToPlayer", "filter": { "types": ["artifact"] }, "batchedPerPlayer": true, "effects": [{ "draw": 1 }],
            "text": "Whenever one or more artifact creatures you control deal combat damage to a player, draw a card." } ] }
        """;

    private const string TouchOfMoongloveScript = """
        { "spell": { "targets": ["creature:you"], "effects": [
          { "pump": [1, 0], "what": "target", "keywords": ["Deathtouch"] },
          { "emblem": "Touch of Moonglove", "untilEndOfTurn": true, "about": "target", "abilities": [
            { "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any", "damagedThisTurnByThat": true },
              "effects": [{ "loseLife": 2, "who": "triggeredPlayer" }],
              "text": "Whenever a creature dealt damage by that creature this turn dies, its controller loses 2 life." } ] } ] } }
        """;

    private const string ChandraFrontScript = """
        { "abilities": [
          { "trigger": "castSpell", "filter": { "colors": ["R"] }, "effects": [{ "untap": "self" }], "text": "Whenever you cast a red spell, untap Chandra." },
          { "cost": "{T}", "targets": ["playerOrPlaneswalker"], "effects": [
              { "damage": 1, "to": "target" },
              { "if": { "dealtDamageThisTurn": 3 }, "then": [{ "blink": "self", "transformed": true }] } ],
            "text": "{T}: Chandra deals 1 damage to target player or planeswalker. If Chandra has dealt 3 or more damage this turn, exile her, then return her to the battlefield transformed under her owner's control." } ] }
        """;

    private const string ChandraBackScript = """
        { "abilities": [
          { "cost": "+1", "targets": ["playerOrPlaneswalker"], "effects": [{ "damage": 2, "to": "target" }], "text": "+1" },
          { "cost": "-2", "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }], "text": "-2" },
          { "cost": "-7", "effects": [
              { "damage": 6, "to": "opponents" },
              { "emblem": "Chandra, Roaring Flame", "for": "playersDamagedThisWay", "abilities": [
                { "trigger": "upkeep", "effects": [{ "damage": 3, "to": "you" }], "text": "At the beginning of your upkeep, this emblem deals 3 damage to you." } ] } ],
            "text": "-7" } ] }
        """;

    private static CardDefinition ChandraRoaringFlame() => Scripted("Chandra, Roaring Flame", CardType.Planeswalker, "{0}", ChandraBackScript, colors: new[] { "R" }) with
    {
        Loyalty = 4, Supertypes = Supertype.Legendary, Subtypes = new[] { "Chandra" },
    };

    private static CardDefinition ChandraFireOfKaladesh() =>
        Scripted("Chandra, Fire of Kaladesh", CardType.Creature, "{1}{R}{R}", ChandraFrontScript, 2, 2, new[] { "Human", "Shaman" }, new[] { "R" }) with
        {
            Supertypes = Supertype.Legendary, BackFace = ChandraRoaringFlame(),
        };

    private static CardDefinition RedSpark(string name = "Spark") =>
        Scripted(name, CardType.Instant, "{R}", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", colors: new[] { "R" });

    /// <summary>Plays the given actions in order whenever they are legal (passing to let each resolve), then passes; never attacks.</summary>
    private static void Sequence(Scenario s, params Func<IReadOnlyList<PlayerAction>, PlayerAction?>[] steps)
    {
        int next = 0;
        bool settling = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (settling) { settling = false; return PassPriority.Instance; }
            if (next < steps.Length && steps[next](legal) is { } action) { next++; settling = true; return action; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    /// <summary>Cards a player drew once the first turn began (not the opening hand), until the scenario stopped.</summary>
    private static int DrawnDuringTheGame(Game game, PlayerId player) =>
        game.Log.SkipWhile(e => e is not TurnBegan).OfType<CardDrawn>().Count(d => d.Player == player);

    private static Func<IReadOnlyList<PlayerAction>, PlayerAction?> Cast(CardId card) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card);

    private static Func<IReadOnlyList<PlayerAction>, PlayerAction?> Activate(CardId source) => legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == source);

    // ------------------------------------------------------------------ becoming renowned (rule 702.112b)

    [Fact]
    public async Task ValeronWardensDrawsWhenItBecomesRenownedAndRenownTriggersOnlyOnce()
    {
        var s = new Scenario();
        var wardens = s.Add(P0, Creature("Valeron Wardens", ValeronWardensScript, 1, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.True(s.Card(wardens).Renowned);
        Assert.Equal(2, s.Card(wardens).CounterCount(CounterKind.PlusOnePlusOne));
        Assert.Equal(1, DrawnDuringTheGame(s.Game, P0)); // the first player skips the draw: only the trigger drew
    }

    [Fact]
    public async Task AnotherCreatureBecomingRenownedTriggersTheWatcherButAnOpponentsDoesNot()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Valeron Wardens", ValeronWardensScript, 1, 3));
        s.Add(P0, Creature("Topan Freeblade", $$"""{ "abilities": [ {{RenownOne}} ] }""", 2, 2));
        s.Add(P1, Creature("Watching Wardens", ValeronWardensScript, 1, 3));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(2, DrawnDuringTheGame(s.Game, P0)); // both of P0's creatures became renowned
        Assert.Equal(0, DrawnDuringTheGame(s.Game, P1));
    }

    [Fact]
    public async Task BecomingRenownedAgainWhileRenownedDoesNotTrigger()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Herald", """
            { "abilities": [
              { "cost": "{0}", "effects": [{ "becomeRenowned": "self" }] },
              { "trigger": "becomesRenowned", "effects": [{ "gainLife": 1 }] } ] }
            """));
        Acting(s, activations: 2);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task RelicSeekerFindsAnEquipmentWhenItBecomesRenowned()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Relic Seeker", RelicSeekerScript, 2, 2));
        var sword = s.Game.SetupInLibrary(P0, new CardDefinition { Name = "Short Sword", Types = CardType.Artifact, Subtypes = new[] { "Equipment" }, ManaCost = ManaCost.Parse("{1}") });
        bool first = true;
        s.Attacker.Act = (_, _) =>
        {
            if (first) { first = false; s.Restack(P0, sword); }
            return PassPriority.Instance;
        };
        s.Attacker.Choose = (_, request) => request.Options.Take(Math.Max(1, request.Min)).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved { From: Zone.Library, To: Zone.Hand } m && m.Card == sword); // later discarded to hand size
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(1, s.Game.Log.OfType<LibraryShuffled>().Count(l => l.Player == P0) - 1); // searched, then shuffled
    }

    // ------------------------------------------------------------------ becoming blocked (rule 509.3c)

    [Fact]
    public async Task SomberwaldAlphaPumpsEachBlockedAttackerOnceHoweverManyBlockIt()
    {
        var s = new Scenario();
        var alpha = s.Add(P0, Creature("Somberwald Alpha", SomberwaldAlphaScript, 3, 2));
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        var wall1 = s.Add(P1, Scenario.Creature("Wall", 0, 4));
        var wall2 = s.Add(P1, Scenario.Creature("Wall", 0, 4));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(alpha, defenders[0]), new AttackDeclaration(bear, defenders[0]) };
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(wall1, bear), new BlockDeclaration(wall2, bear) };
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // the unblocked Alpha isn't pumped
        Assert.Equal(3, s.Game.Log.OfType<DamageDealt>().Where(d => d.Source == bear).Sum(d => d.Amount)); // 2 + 1, once
    }

    // ------------------------------------------------------------------ spells cast last turn

    [Theory]
    [InlineData(2, Zone.Graveyard)]
    [InlineData(1, Zone.Battlefield)]
    public async Task CallOfTheFullMoonIsSacrificedAfterAPlayerCastTwoSpellsLastTurn(int spells, Zone expected)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        var call = s.Game.SetupPermanent(P0, Scripted("Call of the Full Moon", CardType.Enchantment, "{1}{R}", CallOfTheFullMoonScript, subtypes: new[] { "Aura" }), bear);
        for (int i = 0; i < spells; i++) s.InHand(P0, RedSpark($"Spark {i}"));
        Acting(s, activations: 0);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(3); // P1's upkeep on turn 2 looks back at turn 1
        Assert.Equal(expected, s.Card(call).Zone);
        if (expected == Zone.Battlefield) Assert.Equal(5, s.Card(bear).Power);
    }

    [Fact]
    public async Task OnlyTheTurnJustBeforeCountsAsLastTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        // Turn 1: player 0 casts two spells. Turn 2's upkeep sees it; turn 3's upkeep looks at turn 2, when nobody cast anything.
        s.Add(P1, Scripted("Moon Watcher", CardType.Enchantment, "{0}",
            """{ "abilities": [{ "trigger": "eachUpkeep", "if": { "spellsCastLastTurn": 2 }, "effects": [{ "gainLife": 1 }] }] }"""));
        s.InHand(P0, RedSpark("Spark 1"));
        s.InHand(P0, RedSpark("Spark 2"));
        Acting(s, activations: 0);
        await s.RunUntilTurn(4);
        Assert.Equal(21, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // the two Sparks
    }

    // ------------------------------------------------------------------ damage this object dealt this turn

    [Fact]
    public async Task ChandraTransformsOnceSheHasDealtThreeDamageThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var chandra = s.Add(P0, ChandraFireOfKaladesh());
        var spark1 = s.InHand(P0, RedSpark("Spark 1"));
        var spark2 = s.InHand(P0, RedSpark("Spark 2"));
        Sequence(s, Activate(chandra), Cast(spark1), Activate(chandra), Cast(spark2), Activate(chandra));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Player == P1) };
        int versionBefore = s.Card(chandra).Version;
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Battlefield, s.Card(chandra).Zone);
        Assert.True(s.Card(chandra).Transformed);
        Assert.Equal("Chandra, Roaring Flame", s.Card(chandra).Name);
        Assert.NotEqual(versionBefore, s.Card(chandra).Version); // exiled and returned: a new object
        Assert.Equal(4, s.Card(chandra).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task ChandraDoesNotTransformBeforeThreeDamage()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var chandra = s.Add(P0, ChandraFireOfKaladesh());
        var spark = s.InHand(P0, RedSpark());
        Sequence(s, Activate(chandra), Cast(spark), Activate(chandra));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Player == P1) };
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.False(s.Card(chandra).Transformed);
    }

    [Fact]
    public async Task CombatDamageCountsTowardChandrasDamageThisTurn()
    {
        var s = new Scenario();
        var chandra = s.Add(P0, ChandraFireOfKaladesh());
        bool attacked = false;
        // Attack for 2, then after combat use her ability (she has vigilance-free tapping: give her an untap via a red spell).
        var spark = s.InHand(P0, RedSpark());
        s.Lands(P0, 1);
        s.Attacker.Attack = (_, _, defenders) => attacked ? Array.Empty<AttackDeclaration>() : new[] { new AttackDeclaration(chandra, defenders[0]) };
        bool settling = false;
        int step = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (settling) { settling = false; return PassPriority.Instance; }
            if (s.Game.State.Step != Step.PostcombatMain) return PassPriority.Instance;
            attacked = true;
            PlayerAction? action = step switch
            {
                0 => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spark),
                1 => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == chandra),
                _ => null,
            };
            if (action is null) return PassPriority.Instance;
            step++;
            settling = true;
            return action;
        };
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Player == P1) };
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.True(s.Card(chandra).Transformed);
    }

    [Fact]
    public async Task ChandrasUltimateGivesAnEmblemToEachPlayerDealtDamage()
    {
        var s = new Scenario();
        var chandra = s.Add(P0, ChandraRoaringFlame());
        s.Card(chandra).Counters[CounterKind.Loyalty] = 7;
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().LastOrDefault(a => a.Source == chandra) is { } ultimate && s.Card(chandra).Zone == Zone.Battlefield
                                       && s.Game.State.Stack.Count == 0 && !s.Game.Log.OfType<AbilityActivated>().Any() ? ultimate : PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(3);
        var emblem = Assert.Single(s.Game.State.Emblems);
        Assert.Equal(P1, s.Card(emblem).Owner);
        Assert.Equal(11, s.Game.State.GetPlayer(P1).Life); // 6, then 3 at their upkeep
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task APlayerWhoseDamageWasPreventedGetsNoEmblem()
    {
        var s = new Scenario();
        var chandra = s.Add(P0, ChandraRoaringFlame());
        s.Card(chandra).Counters[CounterKind.Loyalty] = 7;
        s.Game.State.PreventionShields.Add(new PreventionShield(1, false) { ToPlayer = P1 });
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().LastOrDefault(a => a.Source == chandra) is { } ultimate
                                       && s.Game.State.Stack.Count == 0 && !s.Game.Log.OfType<AbilityActivated>().Any() ? ultimate : PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Empty(s.Game.State.Emblems);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ becoming the target of your spells and abilities

    private static CardDefinition Prod() => Scripted("Prod", CardType.Artifact, "{0}",
        """{ "abilities": [{ "cost": "{0}", "targets": ["creature"], "effects": [{ "pump": [0, 0], "what": "target" }] }] }""");

    [Fact]
    public async Task WillbreakerTakesACreatureYourAbilityTargets()
    {
        var s = new Scenario();
        s.Add(P0, Creature("Willbreaker", WillbreakerScript, 2, 3));
        s.Add(P0, Prod());
        var bear = s.Add(P1, Scenario.Creature("Bear", 2, 2));
        Acting(s, activations: 1);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Card(bear).Controller);
    }

    [Fact]
    public async Task ATargetChangedToAnOpponentsCreatureTriggersWillbreaker()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Willbreaker", WillbreakerScript, 2, 3));
        var prod = s.Add(P0, Prod());
        var mine = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        var theirs = s.Add(P1, Scenario.Creature("Goblin", 1, 1));
        var bend = s.InHand(P0, Scripted("Bend", CardType.Instant, "{R}",
            """{ "spell": { "targets": [{ "kind": "spellOrAbility" }], "effects": [{ "changeTarget": "target" }] } }"""));
        int step = 0;
        s.Attacker.Act = (_, legal) => step switch
        {
            0 when legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == prod) is { } activate => Next(activate),
            1 when legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bend) is { } cast => Next(cast),
            _ => PassPriority.Instance,
        };
        PlayerAction Next(PlayerAction action) { step++; return action; }
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, request) => new[]
        {
            request.Source == prod ? request.LegalAt(0).First(t => t.Card == mine)
            : request.Text.StartsWith("Choose the new target") ? request.LegalAt(0).First(t => t.Card == theirs)
            : request.LegalAt(0).First(),
        };
        await s.RunUntilTurn();
        Assert.Equal(2, step);
        Assert.Equal(P0, s.Card(theirs).Controller);
    }

    [Fact]
    public async Task WillbreakersControlEndsWhenItLeaves()
    {
        var s = new Scenario();
        var breaker = s.Add(P0, Creature("Willbreaker", WillbreakerScript, 2, 3));
        s.Add(P0, Prod());
        var bear = s.Add(P1, Scenario.Creature("Bear", 2, 2));
        s.Lands(P0, 1);
        s.InHand(P0, Scripted("Unsummon", CardType.Instant, "{R}", """{ "spell": { "targets": ["creature:you"], "effects": [{ "bounce": "target" }] } }"""));
        Acting(s, activations: 1);
        s.Attacker.Targets = (_, request) => request.LegalAt(0).Any(t => t.Card == bear) && s.Card(bear).Controller == P1
            ? new[] { request.LegalAt(0).First(t => t.Card == bear) }
            : new[] { request.LegalAt(0).First(t => t.Card == breaker) };
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved { From: Zone.Battlefield, To: Zone.Hand } m && m.Card == breaker);
        Assert.Equal(P1, s.Card(bear).Controller);
    }

    [Fact]
    public async Task AnOpponentsSpellTargetingTheirOwnCreatureDoesNotTriggerWillbreaker()
    {
        var s = new Scenario();
        s.Add(P1, Creature("Willbreaker", WillbreakerScript, 2, 3));
        s.Add(P0, Prod());
        var bear = s.Add(P0, Scenario.Creature("Bear", 2, 2));
        Acting(s, activations: 1);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await s.RunUntilTurn();
        Assert.Equal(P0, s.Card(bear).Controller);
    }

    /// <summary>Three players; player 0 acts with <paramref name="act"/>, the others pass and never attack.</summary>
    private static (Game Game, TestController[] Players) ThreePlayers()
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        foreach (var p in players.Skip(1)) p.Act = (_, _) => PassPriority.Instance;
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        return (game, players);
    }

    private static async Task RunFirstTurn(Game game)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan { TurnNumber: 2 }) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    [Theory]
    [InlineData(1, false)] // P1 (another opponent) targets P2's creature: not P0's spell or ability
    [InlineData(0, true)]  // P0 targets P2's creature
    public async Task InMultiplayerOnlyYourOwnSpellsAndAbilitiesCountForWillbreaker(int prodOwner, bool stolen)
    {
        var p2 = new PlayerId(2);
        var (game, players) = ThreePlayers();
        game.SetupPermanent(P0, Creature("Willbreaker", WillbreakerScript, 2, 3));
        var bear = game.SetupPermanent(p2, Scenario.Creature("Bear", 2, 2));
        game.SetupPermanent(new PlayerId(prodOwner), Prod());
        // The prodding player activates once (on their own turn only P0 acts, so P1 acts in response to nothing: let P1 act on P0's turn).
        bool used = false;
        players[prodOwner].Act = (_, legal) =>
        {
            if (used || legal.OfType<ActivateAbility>().FirstOrDefault() is not { } activate) return PassPriority.Instance;
            used = true;
            return activate;
        };
        if (prodOwner != 0) players[0].Act = (_, _) => PassPriority.Instance;
        players[prodOwner].Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await RunFirstTurn(game);
        Assert.True(used);
        Assert.Equal(stolen ? P0 : p2, game.State.GetCard(bear).Controller);
    }

    [Fact]
    public async Task InMultiplayerAnOpponentsAbilityTargetingAnotherOpponentsCreatureTriggersOnlyForThoseItOpposes()
    {
        // "Whenever a creature you control becomes the target of a spell or ability an opponent controls": P2's creature targeted by P1.
        var p2 = new PlayerId(2);
        var (game, players) = ThreePlayers();
        var watcher = """{ "abilities": [{ "trigger": "permanentBecomesTarget", "filter": { "types": ["creature"], "controller": "any" }, "effects": [{ "gainLife": 1 }] }] }""";
        game.SetupPermanent(P0, Scripted("Watcher", CardType.Enchantment, "{0}", watcher));
        game.SetupPermanent(p2, Scripted("Watcher", CardType.Enchantment, "{0}", watcher));
        var bear = game.SetupPermanent(p2, Scenario.Creature("Bear", 2, 2));
        game.SetupPermanent(P0, Prod());
        bool used = false;
        players[0].Act = (_, legal) =>
        {
            if (used || legal.OfType<ActivateAbility>().FirstOrDefault() is not { } activate) return PassPriority.Instance;
            used = true;
            return activate;
        };
        players[0].Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await RunFirstTurn(game);
        Assert.Equal(20, game.State.GetPlayer(P0).Life); // P0 controls the ability: not an opponent of P0
        Assert.Equal(21, game.State.GetPlayer(p2).Life);
    }

    // ------------------------------------------------------------------ "one or more … deal combat damage to a player"

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ThopterSpyNetworkMakesAThopterOnlyWhileYouControlAnArtifact(bool artifact, int thopters)
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Thopter Spy Network", CardType.Enchantment, "{2}{U}{U}", ThopterSpyNetworkScript));
        if (artifact) s.Add(P0, Scripted("Bauble", CardType.Artifact, "{0}", "{}"));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(thopters, s.Game.State.Battlefield.Select(s.Card).Count(c => c.Name == "Thopter" && c.Is(CardType.Artifact) && c.IsCreature));
    }

    [Fact]
    public async Task ThopterSpyNetworkDrawsOnceForSeveralArtifactCreaturesHittingOnePlayer()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Thopter Spy Network", CardType.Enchantment, "{2}{U}{U}", ThopterSpyNetworkScript));
        s.Add(P0, Scripted("Construct", CardType.Artifact | CardType.Creature, "{2}", "{}", 2, 2));
        s.Add(P0, Scripted("Construct", CardType.Artifact | CardType.Creature, "{2}", "{}", 2, 2));
        s.Add(P0, Scenario.Creature("Bear", 2, 2));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(14, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(1, DrawnDuringTheGame(s.Game, P0));
    }

    [Fact]
    public async Task ThopterSpyNetworkDrawsOnceForEachPlayerDealtCombatDamage()
    {
        var p2 = new PlayerId(2);
        var (game, players) = ThreePlayers();
        game.SetupPermanent(P0, Scripted("Thopter Spy Network", CardType.Enchantment, "{2}{U}{U}", ThopterSpyNetworkScript));
        var a = game.SetupPermanent(P0, Scripted("Construct", CardType.Artifact | CardType.Creature, "{2}", "{}", 2, 2));
        var b = game.SetupPermanent(P0, Scripted("Construct", CardType.Artifact | CardType.Creature, "{2}", "{}", 2, 2));
        players[0].Act = (_, _) => PassPriority.Instance;
        players[0].Attack = (_, _, _) => new[] { new AttackDeclaration(a, P1), new AttackDeclaration(b, p2) };
        await RunFirstTurn(game);
        Assert.Equal(18, game.State.GetPlayer(P1).Life);
        Assert.Equal(18, game.State.GetPlayer(p2).Life);
        Assert.Equal(1 + 2, DrawnDuringTheGame(game, P0)); // the draw step (multiplayer: the first player draws, rule 103.8c), then once per player
    }

    // ------------------------------------------------------------------ "a creature dealt damage by that creature this turn dies"

    private static CardDefinition Pinger() => Creature("Pinger", """
        { "abilities": [{ "cost": "{T}", "targets": ["creature"], "effects": [{ "damage": 1, "to": "target" }] }] }
        """, 1, 1);

    private static CardDefinition Shock() => Scripted("Shock", CardType.Instant, "{R}", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }] } }""", colors: new[] { "R" });

    [Theory]
    [InlineData(true, 18)]  // the pinger damaged it earlier this turn, before Touch of Moonglove resolved
    [InlineData(false, 20)] // never damaged by the pinger
    public async Task TouchOfMoongloveCountsDamageDealtBeforeItResolved(bool pinged, int expectedLife)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, GenericCards.Swamp);
        var pinger = s.Add(P0, Pinger());
        var bear = s.Add(P1, Scenario.Creature("Bear", 2, 2));
        var touch = s.InHand(P0, Scripted("Touch of Moonglove", CardType.Instant, "{B}", TouchOfMoongloveScript));
        var shock = s.InHand(P0, Shock());
        var steps = new List<Func<IReadOnlyList<PlayerAction>, PlayerAction?>>();
        if (pinged) steps.Add(Activate(pinger));
        steps.Add(Cast(touch));
        steps.Add(Cast(shock));
        Sequence(s, steps.ToArray());
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == (request.Source == touch ? pinger : bear)) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(expectedLife, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task TouchOfMoongloveForgetsTheCreatureOnceItBecameANewObject()
    {
        var s = new Scenario();
        var pinger = s.Add(P0, Pinger());
        var bear = s.Add(P1, Scenario.Creature("Bear", 2, 2));
        s.Game.EventRaised += e =>
        {
            // Damage by the pinger as an earlier object (it left and came back): not "that creature".
            if (e is TurnBegan { TurnNumber: 1 })
                s.Game.State.DamageLog.Add(new DamageRecord(1, pinger, s.Card(pinger).Version - 1, bear, s.Card(bear).Version, null, 1));
        };
        s.Lands(P0, 1);
        s.Add(P0, GenericCards.Swamp);
        var touch = s.InHand(P0, Scripted("Touch of Moonglove", CardType.Instant, "{B}", TouchOfMoongloveScript));
        var shock = s.InHand(P0, Shock());
        Sequence(s, Cast(touch), Cast(shock));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == (request.Source == touch ? pinger : bear)) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }
}
