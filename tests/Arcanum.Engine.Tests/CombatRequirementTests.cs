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
/// Combat requirements and restrictions (rules 506.5, 508.1c-d, 509.1a-c, 510.1d), protection from creature types, block
/// triggers per creature, "doesn't untap during its next untap step" and enchant restrictions (Magic 2010 cards).
/// </summary>
public class CombatRequirementTests
{
    private static readonly PlayerId P2 = new(2);

    private static CardDefinition Jackal => Creature("Jackal", 2, 2, Keyword.CantAttackAlone, Keyword.CantBlockAlone);
    private static CardDefinition Bear => Creature("Bear", 2, 2);
    private static CardDefinition Lure => Creature("Unicorn", 2, 2, Keyword.Lure);
    private static CardDefinition Guard => Creature("Palace Guard", 1, 4, Keyword.CanBlockAnyNumber);

    private static List<AttackDeclaration> All(IReadOnlyList<CardId> attackers, PlayerId defender) =>
        attackers.Select(a => new AttackDeclaration(a, defender)).ToList();

    private static (Game Game, TestController[] Players) ThreePlayers()
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        return (game, players);
    }

    private static async Task Run(Game game, int turn = 2)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == turn) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ can't attack or block alone (506.5)

    [Fact]
    public async Task ACreatureThatCantAttackAloneStaysHomeAlone()
    {
        var s = new Scenario();
        var jackal = s.Add(P0, Jackal);
        AttackRequest? request = null;
        s.Attacker.Attack = (view, attackers, defenders) => { request = view.AttackRequest; return All(attackers, defenders[0]); };
        await s.RunUntilTurn();
        Assert.NotNull(request);
        Assert.False(request!.IsLegal(new[] { new AttackDeclaration(jackal, P1) }, out var reason));
        Assert.Contains("alone", reason);
        Assert.True(request.IsLegal(Array.Empty<AttackDeclaration>(), out _));
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ACreatureThatCantAttackAloneAttacksWithAnother()
    {
        var s = new Scenario();
        s.Add(P0, Jackal);
        s.Add(P0, Bear);
        await s.RunUntilTurn();
        Assert.Equal(16, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ACreatureThatCantBlockAloneBlocksOnlyWithAnother()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Bear);
        var jackal = s.Add(P1, Jackal);
        var wall = s.Add(P1, Creature("Wall", 0, 4));
        BlockRequest? request = null;
        s.Defender.Block = (_, _, _) =>
        {
            request = s.Defender.LastBlockRequest;
            return new[] { new BlockDeclaration(jackal, bear), new BlockDeclaration(wall, bear) };
        };
        await s.RunUntilTurn();
        Assert.False(request!.IsLegal(new[] { new BlockDeclaration(jackal, bear) }, out var reason));
        Assert.Contains("alone", reason);
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(wall, bear) }, out _));
        // The closest legal declaration to a lone block leaves it out rather than adding a blocker nobody asked for.
        Assert.Empty(request.Complete(new[] { new BlockDeclaration(jackal, bear) }));
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone); // blocked by both: 2 damage from the jackal
    }

    // ------------------------------------------------------------------ lure (509.1c)

    [Fact]
    public async Task EveryCreatureAbleToBlockALureMustBlockIt()
    {
        var s = new Scenario();
        var unicorn = s.Add(P0, Lure);
        var a = s.Add(P1, Bear);
        var b = s.Add(P1, Creature("Ox", 1, 3));
        s.Defender.Block = (_, _, _) => s.Defender.LastBlockRequest!.Complete(Array.Empty<BlockDeclaration>());
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.Equal(2, request.MaxRequirements);
        Assert.False(request.IsLegal(Array.Empty<BlockDeclaration>(), out _));
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(a, unicorn) }, out _));
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(a, unicorn), new BlockDeclaration(b, unicorn) }, out _));
        Assert.Equal(Zone.Graveyard, s.Card(unicorn).Zone);
    }

    [Fact]
    public async Task ALureWithMenaceNeedsTwoAbleBlockersToRequireAnything()
    {
        var s = new Scenario();
        var unicorn = s.Add(P0, Creature("Menacing Unicorn", 2, 2, Keyword.Lure, Keyword.Menace));
        s.Add(P1, Bear);
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.Equal(0, request.MaxRequirements); // blocking it alone would break menace
        Assert.True(request.IsLegal(Array.Empty<BlockDeclaration>(), out _));

        var t = new Scenario();
        var lure = t.Add(P0, Creature("Menacing Unicorn", 2, 2, Keyword.Lure, Keyword.Menace));
        var ids = new[] { t.Add(P1, Bear), t.Add(P1, Bear), t.Add(P1, Bear) };
        t.Defender.Block = (_, _, _) => t.Defender.LastBlockRequest!.Complete(Array.Empty<BlockDeclaration>());
        await t.RunUntilTurn();
        var three = t.Defender.LastBlockRequest!;
        Assert.Equal(3, three.MaxRequirements);
        Assert.False(three.IsLegal(ids.Take(2).Select(id => new BlockDeclaration(id, lure)).ToList(), out _));
        Assert.True(three.IsLegal(ids.Select(id => new BlockDeclaration(id, lure)).ToList(), out _));
        Assert.Equal(Zone.Graveyard, t.Card(lure).Zone);
    }

    [Fact]
    public async Task ALureThatCantBeBlockedByMoreThanOneTakesOneBlocker()
    {
        var s = new Scenario();
        var unicorn = s.Add(P0, Creature("Lonely Unicorn", 2, 2, Keyword.Lure, Keyword.CantBeBlockedByMoreThanOne));
        var a = s.Add(P1, Bear);
        var b = s.Add(P1, Bear);
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(b, unicorn) };
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.Equal(1, request.MaxRequirements);
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(a, unicorn) }, out _));
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(a, unicorn), new BlockDeclaration(b, unicorn) }, out _));
    }

    [Fact]
    public async Task ALureAndACreatureThatCantBlockAlone()
    {
        // Alone, the jackal can't block: the lure requires nothing of it.
        var s = new Scenario();
        s.Add(P0, Lure);
        s.Add(P1, Jackal);
        await s.RunUntilTurn();
        Assert.Equal(0, s.Defender.LastBlockRequest!.MaxRequirements);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);

        // With another creature able to block, both must block it.
        var t = new Scenario();
        var unicorn = t.Add(P0, Lure);
        var jackal = t.Add(P1, Jackal);
        var bear = t.Add(P1, Bear);
        t.Defender.Block = (_, _, _) => t.Defender.LastBlockRequest!.Complete(Array.Empty<BlockDeclaration>());
        await t.RunUntilTurn();
        var request = t.Defender.LastBlockRequest!;
        Assert.Equal(2, request.MaxRequirements);
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(bear, unicorn) }, out _));
        Assert.Equal(2, t.Game.Log.OfType<BlockerDeclared>().Count());
    }

    [Fact]
    public async Task TwoLuresAndACreatureThatBlocksAnyNumber()
    {
        var s = new Scenario();
        var first = s.Add(P0, Lure);
        var second = s.Add(P0, Lure);
        var guard = s.Add(P1, Guard);
        var bear = s.Add(P1, Bear);
        DamageAssignmentRequest? split = null;
        s.Defender.Block = (_, _, _) => s.Defender.LastBlockRequest!.Complete(Array.Empty<BlockDeclaration>());
        s.Defender.AssignDamage = (_, r) => { split = r; return r.Suggested; };
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        // The guard blocks both lures, the bear one of them.
        Assert.Equal(3, request.MaxRequirements);
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(guard, first), new BlockDeclaration(guard, second), new BlockDeclaration(bear, second) }, out _));
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(guard, first), new BlockDeclaration(bear, second) }, out _));
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(bear, first), new BlockDeclaration(bear, second) }, out _)); // one attacker per bear
        Assert.Equal(3, s.Game.Log.OfType<BlockerDeclared>().Count());
        Assert.NotNull(split);
        Assert.True(split!.ByBlocker);
        Assert.Equal(guard, split.Attacker);
        Assert.Equal(new[] { first, second }.OrderBy(x => x.Value), split.Blockers.OrderBy(x => x.Value));
    }

    [Fact]
    public async Task TheComputerMakesLegalDeclarationsAroundLuresAndLoneCreatures()
    {
        var s = new Scenario();
        s.Add(P0, Jackal);
        s.Add(P0, Lure);
        s.Add(P0, Lure);
        s.Add(P1, Guard);
        s.Add(P1, Jackal);
        s.Add(P1, Bear);
        var attackerBot = new Bots.BotController(P0);
        var defenderBot = new Bots.BotController(P1);
        IReadOnlyList<AttackDeclaration>? attacks = null;
        AttackRequest? attackRules = null;
        IReadOnlyList<BlockDeclaration>? blocks = null;
        s.Attacker.Attack = (v, a, d) => { attackRules = v.AttackRequest; return attacks = attackerBot.DeclareAttackersAsync(v, a, d).Result; };
        s.Defender.Block = (v, _, _) => blocks = defenderBot.DeclareBlockersAsync(v, s.Defender.LastBlockRequest!).Result;
        s.Defender.AssignDamage = (v, r) => defenderBot.AssignCombatDamageAsync(v, r).Result;
        await s.RunUntilTurn();
        Assert.True(attackRules!.IsLegal(attacks!, out var attackReason), attackReason);
        if (s.Defender.LastBlockRequest is { } request) Assert.True(request.IsLegal(blocks!, out var reason), reason);
    }

    // ------------------------------------------------------------------ can block any number (509.1a, 510.1d)

    [Fact]
    public async Task ACreatureBlockingSeveralAttackersDividesItsDamageAndTriggersBlocksOnce()
    {
        var s = new Scenario();
        var a = s.Add(P0, Bear);
        var b = s.Add(P0, Bear);
        var guard = s.Add(P1, Creature("Big Guard", 3, 6, Keyword.CanBlockAnyNumber) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility { Trigger = TriggerEvent.Blocks, Text = "Whenever this creature blocks, you gain 1 life.", Effects = new Effect[] { new GainLife(1, Subject.You) } },
                new TriggeredAbility { Trigger = TriggerEvent.BlocksCreature, Text = "Whenever this creature blocks a creature, you gain 10 life.", Effects = new Effect[] { new GainLife(10, Subject.You) } },
            },
        });
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(guard, a), new BlockDeclaration(guard, b) };
        s.Defender.AssignDamage = (_, r) => new DamageAssignment(new Dictionary<CardId, int> { [a] = 2, [b] = 1 });
        await s.RunUntilTurn();
        Assert.Equal(20 + 1 + 20, s.Game.State.GetPlayer(P1).Life); // "blocks" once, "blocks a creature" twice
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(b).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 1 } d && d.Source == guard && d.TargetCard == b);
        Assert.Equal(Zone.Battlefield, s.Card(guard).Zone); // 4 damage to a 3/6
    }

    [Fact]
    public async Task OnlyCreaturesThatCanBlockAnyNumberBlockSeveral()
    {
        var s = new Scenario();
        var a = s.Add(P0, Bear);
        var b = s.Add(P0, Bear);
        var bear = s.Add(P1, Bear);
        var guard = s.Add(P1, Guard);
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.False(request.IsLegal(new[] { new BlockDeclaration(bear, a), new BlockDeclaration(bear, b) }, out _));
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(guard, a), new BlockDeclaration(guard, b), new BlockDeclaration(bear, a) }, out _));
    }

    // ------------------------------------------------------------------ attacks you this turn if able (508.1d)

    private static CardDefinition Siren => Creature("Siren", 1, 1) with
    {
        Abilities = new AbilityDefinition[]
        {
            new ActivatedAbility
            {
                Cost = AbilityCost.TapOnly, Text = "{T}: Target creature an opponent controls attacks you this turn if able.",
                Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent) },
                Effects = new Effect[] { new AttacksYouThisTurn(Subject.TargetAt(0)) },
            },
        },
    };

    [Fact]
    public async Task ASirenMakesTheCreatureAttackItsControllerInMultiplayer()
    {
        var (game, players) = ThreePlayers();
        var bear = game.SetupPermanent(P0, Bear);
        var siren = game.SetupPermanent(P2, Siren);
        game.State.GetCard(siren).ControlledSinceTurnStart = true;
        players[2].Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        AttackRequest? request = null;
        players[0].Attack = (view, attackers, _) => { request = view.AttackRequest; return All(attackers, P1); }; // wants to attack P1
        await Run(game);
        Assert.NotNull(request);
        Assert.False(request!.IsLegal(new[] { new AttackDeclaration(bear, P1) }, out _));
        Assert.False(request.IsLegal(Array.Empty<AttackDeclaration>(), out _));
        Assert.True(request.IsLegal(new[] { new AttackDeclaration(bear, P2) }, out _));
        Assert.Equal(20, game.State.GetPlayer(P1).Life);
        Assert.Equal(18, game.State.GetPlayer(P2).Life);
    }

    [Fact]
    public async Task ASirenNeverBreaksARestriction()
    {
        var (game, players) = ThreePlayers();
        var jackal = game.SetupPermanent(P0, Jackal);
        var siren = game.SetupPermanent(P2, Siren);
        game.State.GetCard(siren).ControlledSinceTurnStart = true;
        players[2].Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        AttackRequest? request = null;
        players[0].Attack = (view, _, _) => { request = view.AttackRequest; return Array.Empty<AttackDeclaration>(); };
        await Run(game);
        Assert.True(game.State.GetCard(siren).Tapped);
        Assert.Equal(0, request!.MaxRequirements); // it can't attack alone
        Assert.True(request.IsLegal(Array.Empty<AttackDeclaration>(), out _));
        Assert.False(request.IsLegal(new[] { new AttackDeclaration(jackal, P2) }, out _));
    }

    // ------------------------------------------------------------------ attack restrictions about the defending player

    private static CardDefinition Serpent => Creature("Serpent", 5, 5) with
    {
        CantAttackUnlessDefenderControls = new ObjectFilter(CardType.Land, Subtype: "Island", Controller: ControllerFilter.Any),
    };

    [Fact]
    public async Task ASerpentAttacksOnlyPlayersWhoControlAnIsland()
    {
        var (game, players) = ThreePlayers();
        var serpent = game.SetupPermanent(P0, Serpent);
        game.SetupPermanent(P1, GenericCards.Island);
        var walker = game.SetupPermanent(P2, new CardDefinition { Name = "Walker", ManaCost = ManaCost.Parse("{3}"), Types = CardType.Planeswalker, Loyalty = 3 });
        AttackRequest? request = null;
        players[0].Attack = (view, attackers, _) => { request = view.AttackRequest; return All(attackers, P2); };
        await Run(game);
        Assert.True(request!.MayAttack(serpent, P1));
        Assert.False(request.MayAttack(serpent, P2));
        Assert.False(request.MayAttack(serpent, P2, walker)); // the defending player would be P2
        Assert.False(request.IsLegal(new[] { new AttackDeclaration(serpent, P2) }, out _));
        Assert.Equal(20, game.State.GetPlayer(P2).Life);
    }

    [Fact]
    public async Task ASerpentCantAttackWithoutIslands()
    {
        var s = new Scenario();
        s.Add(P0, Serpent);
        IReadOnlyList<CardId>? offered = null;
        s.Attacker.Attack = (_, attackers, defenders) => { offered = attackers; return All(attackers, defenders[0]); };
        await s.RunUntilTurn();
        Assert.Null(offered); // no creature could attack: nobody was asked
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ block triggers per creature

    [Fact]
    public async Task InfernoElementalDamagesEachCreatureBlockingIt()
    {
        var s = new Scenario();
        var inferno = s.Add(P0, Creature("Inferno", 4, 4) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.BlocksOrBecomesBlockedByCreature, Text = "Whenever this creature blocks or becomes blocked by a creature, this creature deals 3 damage to that creature.",
                    Effects = new Effect[] { new DealDamage(3, Subject.Triggered) },
                },
            },
        });
        var a = s.Add(P1, Creature("Ox", 1, 3));
        var b = s.Add(P1, Creature("Ox", 1, 3));
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(a, inferno), new BlockDeclaration(b, inferno) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(b).Zone);
        Assert.DoesNotContain(s.Game.Log, e => e is DamageDealt d && d.TargetCard == inferno); // they died before combat damage
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life); // still blocked
    }

    // ------------------------------------------------------------------ protection from creature types (702.16)

    private static CardDefinition Baneslayer => Creature("Baneslayer", 5, 5, Keyword.Flying, Keyword.FirstStrike, Keyword.Lifelink) with
    {
        ProtectionFromSubtypes = new[] { "Demon", "Dragon" },
    };

    private static CardDefinition Dragon(int power) => Creature("Dragon", power, power, Keyword.Flying) with { Subtypes = new[] { "Dragon" } };

    [Fact]
    public async Task ADragonCantBlockACreatureWithProtectionFromDragons()
    {
        var s = new Scenario();
        s.Add(P0, Baneslayer);
        s.Add(P1, Dragon(5));
        s.Defender.Block = (_, blockers, attackers) => blockers.Select(b => new BlockDeclaration(b, attackers[0])).ToList();
        await s.RunUntilTurn();
        Assert.Null(s.Defender.LastBlockRequest); // nothing could block
        Assert.Equal(15, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life); // lifelink
    }

    [Fact]
    public async Task DamageFromADragonToACreatureWithProtectionFromDragonsIsPrevented()
    {
        var s = new Scenario();
        var dragon = s.Add(P0, Dragon(6));
        var angel = s.Add(P1, Baneslayer);
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(angel, dragon) };
        await s.RunUntilTurn();
        Assert.DoesNotContain(s.Game.Log, e => e is DamageDealt d && d.TargetCard == angel);
        Assert.Equal(Zone.Battlefield, s.Card(angel).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 5 } d && d.TargetCard == dragon);
    }

    [Fact]
    public async Task ADemonsAbilityCantTargetACreatureWithProtectionFromDemons()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Add(P0, Creature("Demon", 1, 1) with
        {
            Subtypes = new[] { "Demon" },
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: deal 1 damage to any target.",
                    Targets = new[] { new TargetSpec(TargetKind.Any) }, Effects = new Effect[] { new DealDamage(1, Subject.TargetAt(0)) },
                },
            },
        });
        var angel = s.Add(P1, Baneslayer);
        var bear = s.Add(P1, Bear);
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        TargetRequest? asked = null;
        s.Attacker.Targets = (_, r) => { asked = r; return new[] { Target.Of(bear) }; };
        await s.RunUntilTurn();
        Assert.NotNull(asked);
        Assert.DoesNotContain(Target.Of(angel), asked!.LegalAt(0));
        Assert.Contains(Target.Of(bear), asked.LegalAt(0));
    }

    [Fact]
    public async Task AnAuraFromADragonFallsOffACreatureWithProtectionFromDragons()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var angel = s.Add(P1, Baneslayer);
        var aura = s.Game.SetupPermanent(P0, GenericCards.StoneSkin with { Subtypes = new[] { "Aura", "Dragon" } }, angel);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(aura).Zone);
    }

    // ------------------------------------------------------------------ Stone Giant

    [Fact]
    public async Task StoneGiantTargetsOnlyCreaturesWithLessToughnessThanItsPowerAndDestroysThemAtTheEndStep()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var giant = s.Add(P0, Creature("Giant", 3, 4) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: Target creature you control with toughness less than this creature's power gains flying until end of turn. Destroy that creature at the beginning of the next end step.",
                    Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You, new ObjectFilter(Controller: ControllerFilter.Any) { ToughnessLessThanSourcePower = true }) },
                    Effects = new Effect[]
                    {
                        new PumpUntilEndOfTurn(0, 0, Subject.TargetAt(0), new[] { Keyword.Flying }),
                        new AtNextEndStepAbout(Subject.TargetAt(0), new Effect[] { new Destroy(Subject.Triggered) }),
                    },
                },
            },
        });
        var bear = s.Add(P0, Bear);
        var ox = s.Add(P0, Creature("Ox", 2, 3));
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        TargetRequest? asked = null;
        s.Attacker.Targets = (_, r) => { asked = r; return new[] { Target.Of(bear) }; };
        bool flew = false;
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.End } && s.Card(bear).Has(Keyword.Flying)) flew = true; };
        await s.RunUntilTurn();
        Assert.DoesNotContain(Target.Of(ox), asked!.LegalAt(0));
        Assert.DoesNotContain(Target.Of(giant), asked.LegalAt(0));
        Assert.True(flew);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(ox).Zone);
    }

    [Fact]
    public async Task StoneGiantUsesItsLastKnownPowerWhenItCameBackAsANewObject()
    {
        // A 5/6 Giant (a 3/4 with two +1/+1 counters) targets a 2/4; blinked in response, it comes back as a new 3/4 object.
        // "This creature" is the object that activated the ability, as it last existed (rules 400.7, 608.2h): still legal.
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var giant = s.Add(P0, Creature("Giant", 3, 4) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: Target creature you control with toughness less than this creature's power gains flying until end of turn. Destroy that creature at the beginning of the next end step.",
                    Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You, new ObjectFilter(Controller: ControllerFilter.Any) { ToughnessLessThanSourcePower = true }) },
                    Effects = new Effect[]
                    {
                        new PumpUntilEndOfTurn(0, 0, Subject.TargetAt(0), new[] { Keyword.Flying }),
                        new AtNextEndStepAbout(Subject.TargetAt(0), new Effect[] { new Destroy(Subject.Triggered) }),
                    },
                },
            },
        });
        s.Card(giant).Counters[CounterKind.PlusOnePlusOne] = 2;
        var blinker = s.Add(P0, new CardDefinition
        {
            Name = "Blinker", Types = CardType.Artifact,
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: Exile target creature you control, then return it to the battlefield.",
                    Targets = new[] { new TargetSpec(TargetKind.Creature, ControllerFilter.You) },
                    Effects = new Effect[] { new ExileIt(Subject.TargetAt(0)) { Linked = true }, new ReturnLinkedExiled() },
                },
            },
        });
        var bear = s.Add(P0, Creature("Ox", 2, 4));
        int step = 0;
        s.Attacker.Act = (_, legal) => step++ switch
        {
            0 => (PlayerAction)legal.OfType<ActivateAbility>().First(a => a.Source == giant),
            1 => legal.OfType<ActivateAbility>().First(a => a.Source == blinker),
            _ => PassPriority.Instance,
        };
        s.Attacker.Targets = (_, r) => new[] { Target.Of(step == 1 ? bear : giant) };
        int giantVersion = s.Card(giant).Version;
        bool flew = false;
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.End } && s.Card(bear).Has(Keyword.Flying)) flew = true; };
        await s.RunUntilTurn();
        Assert.NotEqual(giantVersion, s.Card(giant).Version);
        Assert.Equal(Zone.Battlefield, s.Card(giant).Zone);
        Assert.Equal(3, s.Card(giant).Power);
        Assert.True(flew);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
    }

    // ------------------------------------------------------------------ Master of the Wild Hunt

    [Fact]
    public async Task TappedWolvesDamageTheTargetWhichDividesItsDamageAmongThem()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var master = s.Add(P0, Creature("Master", 3, 3) with
        {
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = AbilityCost.TapOnly, Text = "{T}: Tap all untapped Wolf creatures you control. …",
                    Targets = new[] { new TargetSpec(TargetKind.Creature) },
                    Effects = new Effect[] { new TapAllToDamage(new ObjectFilter(CardType.Creature, Subtype: "Wolf"), Subject.TargetAt(0)) },
                },
            },
        });
        var wolf = Creature("Wolf", 2, 2) with { Subtypes = new[] { "Wolf" } };
        var w1 = s.Add(P0, wolf);
        var w2 = s.Add(P0, wolf);
        var bear = s.Add(P1, Creature("Bear", 3, 3));
        s.Attacker.Act = (v, legal) => v.Step == Step.PrecombatMain ? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance : PassPriority.Instance;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        DamageAssignmentRequest? asked = null;
        s.Defender.AssignDamage = (_, r) => { asked = r; return new DamageAssignment(new Dictionary<CardId, int> { [w1] = 2, [w2] = 1 }); };
        bool wolvesTapped = false;
        s.Game.EventRaised += e => { if (e is DamageDealt && s.Card(w1).Tapped && s.Card(w2).Tapped) wolvesTapped = true; };
        await s.RunUntilTurn();
        Assert.NotNull(asked); // the target's controller divides
        Assert.Equal(3, asked!.Power);
        Assert.True(wolvesTapped);
        Assert.True(s.Card(master).Tapped);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(w1).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(w2).Zone);
    }

    // ------------------------------------------------------------------ doesn't untap during the next untap step

    [Fact]
    public async Task SleepKeepsThePlayersCreaturesTappedThroughTheirNextUntapStepOnly()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Bear);
        var mine = s.Add(P0, Bear);
        var each = new Subject(SubjectKind.Each, 0, new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any)) { ControlledByTarget = true };
        s.InHand(P0, new CardDefinition
        {
            Name = "Sleep", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Player) }, Effects = new Effect[] { new TapIt(each), new SkipNextUntap(each, Subject.TargetAt(0)) } },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P1) };
        var tappedAtUpkeep = new Dictionary<int, bool>();
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.Upkeep }) tappedAtUpkeep[s.Game.State.TurnNumber] = s.Card(bear).Tapped; };
        await s.RunUntilTurn(5);
        Assert.True(tappedAtUpkeep[2]);  // P1's next untap step: stays tapped
        Assert.False(tappedAtUpkeep[4]); // the one after: untaps
        Assert.False(s.Card(mine).Tapped);
    }

    [Fact]
    public async Task WallOfFrostKeepsTheCreatureItBlockedTappedThroughItsNextUntapStep()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Bear);
        var wall = s.Add(P1, Creature("Wall of Frost", 0, 7, Keyword.Defender) with
        {
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.BlocksCreature, Text = "Whenever this creature blocks a creature, that creature doesn't untap during its controller's next untap step.",
                    Effects = new Effect[] { new SkipNextUntap(Subject.Triggered) },
                },
            },
        });
        s.Attacker.Attack = (v, attackers, defenders) => v.TurnNumber == 1 ? All(attackers, defenders[0]) : Array.Empty<AttackDeclaration>();
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(wall, bear) };
        var tappedAtUpkeep = new Dictionary<int, bool>();
        s.Game.EventRaised += e => { if (e is StepBegan { Step: Step.Upkeep }) tappedAtUpkeep[s.Game.State.TurnNumber] = s.Card(bear).Tapped; };
        await s.RunUntilTurn(6);
        Assert.True(tappedAtUpkeep[3]);
        Assert.False(tappedAtUpkeep[5]);
    }

    // ------------------------------------------------------------------ enchant tapped creature (303.4d, 704.5m)

    private static CardDefinition Vines => new()
    {
        Name = "Entangling Vines", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Enchantment, Subtypes = new[] { "Aura" },
        EnchantTarget = new TargetSpec(TargetKind.Creature, Filter: new ObjectFilter(Tapped: true, Controller: ControllerFilter.Any)),
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.Enchanted), Keywords: new[] { Keyword.DoesntUntap }) },
    };

    [Fact]
    public async Task AnAuraThatEnchantsATappedCreatureTargetsOnlyTappedCreaturesAndKeepsItTapped()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var tapped = s.Add(P1, Bear);
        var untapped = s.Add(P1, Bear);
        s.Card(tapped).Tapped = true;
        var vines = s.InHand(P0, Vines);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        TargetRequest? asked = null;
        s.Attacker.Targets = (_, r) => { asked = r; return new[] { Target.Of(tapped) }; };
        await s.RunUntilTurn(3);
        Assert.Contains(Target.Of(tapped), asked!.LegalAt(0));
        Assert.DoesNotContain(Target.Of(untapped), asked.LegalAt(0));
        Assert.Equal(tapped, s.Card(vines).AttachedTo);
        Assert.True(s.Card(tapped).Tapped); // P1's untap step didn't untap it
    }

    [Fact]
    public async Task AnAuraThatEnchantsATappedCreatureFallsOffWhenItUntaps()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Bear);
        s.Card(bear).Tapped = true;
        var vines = s.Game.SetupPermanent(P0, Vines, bear);
        s.InHand(P0, new CardDefinition
        {
            Name = "Awaken", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new UntapIt(Subject.TargetAt(0)) } },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        await s.RunUntilTurn();
        Assert.False(s.Card(bear).Tapped);
        Assert.Equal(Zone.Graveyard, s.Card(vines).Zone);
    }
}
