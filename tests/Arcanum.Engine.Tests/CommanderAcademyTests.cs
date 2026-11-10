// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
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
/// Commander academy vocabulary: removing counters from opponents, Auras with protection and graveyard casting, damage
/// by power, a trigger on attacks against an opponent, attack taxes per counter, and the greatest mana value cast. The
/// cards are invented.
/// </summary>
public class CommanderAcademyTests
{
    private static readonly PlayerId P2 = new(2);

    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"commander-academy-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, script.Length == 0 ? null : CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    private static void CastFirst(Scenario s, CardId card)
    {
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static async Task Run(Game game, int turn = 2)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        game.EventRaised += e => { if (e is TurnBegan t && t.TurnNumber == turn) cts.Cancel(); };
        try { await game.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }
    }

    private static CardDefinition Snap() => Make("Snap Note", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}", null,
        "Destroy target creature.");

    // ------------------------------------------------------------------ removing counters from opponents

    [Fact]
    public async Task RemoveAllCountersOnOpponentsClearsTheirPoisonAndExperience()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Game.State.GetPlayer(P1).Poison = 3;
        s.Game.State.GetPlayer(P1).Experience = 2;
        CastFirst(s, s.InHand(P0, Make("Purge Note", "Sorcery", """{ "spell": { "effects": [{ "removeAllCounters": "opponents" }] } }""", "{R}", null,
            "Remove all counters from each opponent.")));
        await s.RunUntilTurn();
        Assert.Equal(0, s.Game.State.GetPlayer(P1).Poison);
        Assert.Equal(0, s.Game.State.GetPlayer(P1).Experience);
    }

    // ------------------------------------------------------------------ an Aura with protection from creatures

    private static CardDefinition Veil() => Make("Shield Bond", "Enchantment — Aura",
        """{ "abilities": [{ "static": { "affects": "enchanted", "pump": [1, 1], "protectionFromTypes": ["creature"] }, "text": "Enchanted creature gets +1/+1 and has protection from creatures." }] }""",
        "{1}", null, "Enchant creature\nEnchanted creature gets +1/+1 and has protection from creatures.", "Enchant");

    [Fact]
    public async Task AnEnchantedCreatureWithProtectionFromCreaturesCantBeBlockedByCreatures()
    {
        var s = new Scenario();
        var bait = s.Add(P0, Creature("Bait", 2, 2));
        s.Game.SetupPermanent(P0, Veil(), bait);
        s.Add(P1, Creature("Guard", 1, 4));
        await s.RunUntilTurn();
        Assert.Null(s.Defender.LastBlockRequest); // nothing could block
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // 3 damage: the enchanted creature is 3/3
    }

    [Fact]
    public async Task CombatDamageFromCreaturesToAnEnchantedCreatureWithProtectionFromCreaturesIsPrevented()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var bait = s.Add(P0, Creature("Bait", 2, 2));
        s.Game.SetupPermanent(P0, Veil(), bait);
        s.Attacker.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        var raider = s.Add(P1, Creature("Raider", 4, 4));
        s.Defender.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn(3);
        Assert.DoesNotContain(s.Game.Log, e => e is DamageDealt d && d.TargetCard == bait);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 3 } d && d.TargetCard == raider);
        Assert.Equal(Zone.Battlefield, s.Card(bait).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ each creature damages itself by its power

    [Fact]
    public async Task EachCreatureDealsDamageEqualToItsPowerToItself()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var big = s.Add(P0, Creature("Big", 3, 3));
        var tough = s.Add(P0, Creature("Tough", 2, 5));
        CastFirst(s, s.InHand(P0, Make("Quake Note", "Sorcery", """{ "spell": { "effects": [{ "eachDealsDamageEqualToPower": { "each": { "types": ["creature"] } }, "to": "itself" }] } }""", "{R}", null,
            "Each creature deals damage to itself equal to its power.")));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(big).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(tough).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 2 } d && d.TargetCard == tough);
    }

    // ------------------------------------------------------------------ an Aura cast from the graveyard for another cost

    private static CardDefinition GraveyardVeil() => Make("Ashen Veil", "Enchantment — Aura", """{ "graveyardAlternativeCost": "{2}{W}" }""", "{W}", null,
        "Enchant creature\nYou may cast this card from your graveyard for {2}{W}.", "Enchant");

    [Theory]
    [InlineData(1, Zone.Graveyard, false)]
    [InlineData(3, Zone.Battlefield, true)]
    public async Task AnAuraInTheGraveyardIsCastOnlyForTheAlternativeCost(int mountains, Zone expected, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, mountains); // the first Mountain pays for the spell that puts the Aura into the graveyard
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P0, Creature("Bait", 2, 2));
        var other = s.Add(P0, Creature("Other", 1, 1));
        var aura = s.Game.SetupPermanent(P0, GraveyardVeil(), bait);
        var snap = s.InHand(P0, Snap());
        bool offered = false;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(s.Card(bait).Zone == Zone.Battlefield ? bait : other) };
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(snap).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == snap) is { } first) return first;
            if (s.Card(aura).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == aura) is { } cast)
            {
                offered = true;
                return cast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(castable, offered);
        Assert.Equal(expected, s.Card(aura).Zone);
        Assert.NotEqual(Zone.Exile, s.Card(aura).Zone);
        if (castable) Assert.Equal(other, s.Card(aura).AttachedTo);
    }

    // ------------------------------------------------------------------ an Aura that returns with its creature

    private static CardDefinition Tether() => Make("Grave Tether", "Enchantment — Aura",
        """{ "abilities": [{ "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any", "attachedToSource": true }, "effects": [{ "reanimate": "triggered", "ownerControl": true }, { "atNextEndStepAbout": "enteredThisWay", "effects": [{ "reanimate": "self", "attachTo": "triggered" }] }] }] }""",
        "{2}{B}", null, "Enchant creature\nWhen enchanted creature dies, return that card to the battlefield under its owner's control. At the beginning of the next end step, return this Aura to the battlefield attached to it.", "Enchant");

    [Fact]
    public async Task WhenTheEnchantedCreatureDiesItReturnsAndTheAuraReturnsAttachedToItAtTheNextEndStep()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bait = s.Add(P0, Creature("Bait", 2, 2));
        var aura = s.Game.SetupPermanent(P0, Tether(), bait);
        var snap = s.InHand(P0, Snap());
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bait) };
        CastFirst(s, snap);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is CardMoved m && m.Card == bait && m.From == Zone.Graveyard && m.To == Zone.Battlefield);
        Assert.Equal(Zone.Battlefield, s.Card(bait).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(aura).Zone);
        Assert.Equal(bait, s.Card(aura).AttachedTo);
    }

    // ------------------------------------------------------------------ hexproof lost by an opponent's creatures

    private const string LoseHexproof = """{ "spell": { "effects": [{ "pump": [0, 0], "what": { "each": { "types": ["creature"], "controller": "opponent" } }, "loseKeywords": ["Hexproof"], "cantHave": true }] } }""";
    private const string LoseHexproofWithoutCantHave = """{ "spell": { "effects": [{ "pump": [0, 0], "what": { "each": { "types": ["creature"], "controller": "opponent" } }, "loseKeywords": ["Hexproof"] }] } }""";
    private const string GrantHexproof = """{ "spell": { "effects": [{ "pump": [0, 0], "what": { "each": { "types": ["creature"], "controller": "opponent" } }, "keywords": ["Hexproof"] }] } }""";

    /// <summary>Casts the losing spell, then the granting spell, in one turn; reports whether the creature has hexproof after each resolves.</summary>
    private static async Task<(bool? AfterLoss, bool? AfterGrant)> HexproofAfterEachSpell(string losing)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var shadow = s.Add(P1, Creature("Shadow", 2, 2, Keyword.Hexproof));
        var first = s.InHand(P0, Make("Quell Note", "Sorcery", losing, "{R}", null, "Opponents' creatures lose hexproof until end of turn."));
        var second = s.InHand(P0, Make("Grant Note", "Sorcery", GrantHexproof, "{R}", null, "Opponents' creatures gain hexproof until end of turn."));
        var order = new[] { first, second };
        int next = 0;
        bool? afterLoss = null, afterGrant = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (next == 1 && afterLoss is null && s.Card(first).Zone == Zone.Graveyard) afterLoss = s.Card(shadow).Has(Keyword.Hexproof);
            if (next == 2 && afterGrant is null && s.Card(second).Zone == Zone.Graveyard) afterGrant = s.Card(shadow).Has(Keyword.Hexproof);
            if (next < order.Length && s.Card(order[next]).Zone == Zone.Hand && (next == 0 || s.Card(first).Zone == Zone.Graveyard)
                && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == order[next]) is { } cast)
            {
                next++;
                return cast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        return (afterLoss, afterGrant);
    }

    [Fact]
    public async Task AnOpponentsCreatureLosesHexproofAndALaterGrantDoesNotGiveItBack()
    {
        var (afterLoss, afterGrant) = await HexproofAfterEachSpell(LoseHexproof);
        Assert.False(afterLoss);
        Assert.False(afterGrant);
    }

    [Fact]
    public async Task WithoutCantHaveALaterGrantOfHexproofGivesItBack()
    {
        var (afterLoss, afterGrant) = await HexproofAfterEachSpell(LoseHexproofWithoutCantHave);
        Assert.False(afterLoss);
        Assert.True(afterGrant);
    }

    // ------------------------------------------------------------------ an Aura that protects from the color chosen as it enters

    [Fact]
    public async Task AnAuraWithProtectionFromTheChosenColorCantBeBlockedByThatColorAndStaysAttached()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P0, Creature("Bait", 2, 2));
        var aura = s.InHand(P0, Make("Hue Veil", "Enchantment — Aura",
            """{ "chooseOnEnter": "color", "abilities": [{ "static": { "affects": "enchanted", "pump": [0, 0], "protectionFromChosenColor": true }, "text": "Enchanted creature has protection from the chosen color." }] }""",
            "{W}", null, "Enchant creature\nAs this Aura enters, choose a color.\nEnchanted creature has protection from the chosen color.", "Enchant"));
        s.Add(P1, Make("Pale Guard", "Creature — Soldier", "{}", "{W}", "1/1"));
        s.Attacker.Option = (_, r) => r.Options.ToList().IndexOf("White");
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bait) };
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == aura) is { } cast) return cast;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Null(s.Defender.LastBlockRequest); // the only creature that could block is white
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(Zone.Battlefield, s.Card(aura).Zone);
        Assert.Equal(bait, s.Card(aura).AttachedTo);
    }

    // ------------------------------------------------------------------ a target filter on the source's X

    [Fact]
    public async Task AnEnterTriggerTargetsOnlyArtifactsWithManaValueUpToTheSourcesX()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        var two = s.Add(P0, Make("Small Idol", "Artifact", "{}", "{2}"));
        var three = s.Add(P0, Make("Large Idol", "Artifact", "{}", "{3}"));
        var sprout = s.InHand(P0, Make("Sprout Fiend", "Creature — Plant",
            """{ "abilities": [{ "trigger": "enters", "targets": [{ "kind": "permanent", "filter": { "types": ["artifact"], "maxManaValueSourceX": true } }], "effects": [{ "destroy": "target" }] }] }""",
            "{X}{G}", "2/2", "When this creature enters, destroy target artifact with mana value X or less."));
        var targetable = new List<CardId>();
        s.Attacker.Number = (_, _) => 2;
        s.Attacker.Targets = (view, request) =>
        {
            targetable.AddRange(request.LegalAt(0).Where(t => t.Card is not null).Select(t => t.Card!.Value));
            return TestController.FirstAllowed(view, request);
        };
        CastFirst(s, sprout);
        await s.RunUntilTurn();
        Assert.Contains(two, targetable);
        Assert.DoesNotContain(three, targetable);
    }

    // ------------------------------------------------------------------ an observer of attacks on an opponent

    private static CardDefinition Watcher() => Make("Watch Post", "Enchantment",
        """{ "abilities": [{ "trigger": "playerAttacksYourOpponent", "effects": [{ "draw": 1, "who": "triggeredPlayer" }] }] }""", "{1}", null,
        "Whenever a player attacks an opponent of you, that player draws a card.");

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public async Task AnObserverMakesTheAttackerDrawWhenItAttacksAnOpponentOfTheObserversController(int defender, int draws)
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        foreach (var p in players) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var target = new PlayerId(defender);
        players[0].Attack = (_, attackers, _) => attackers.Select(a => new AttackDeclaration(a, target)).ToList();
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        game.SetupPermanent(P0, Creature("Bear", 2, 2));
        game.SetupPermanent(P2, Watcher());
        await Run(game);
        // Only the draws after the attack count: the opening hands are drawn before the game starts.
        int attack = game.Log.ToList().FindIndex(e => e is AttackerDeclared);
        Assert.True(attack >= 0);
        Assert.Equal(draws, game.Log.Skip(attack).OfType<CardDrawn>().Count(d => d.Player == P0));
    }

    // ------------------------------------------------------------------ attack tax per counter

    private static CardDefinition TollPost() => Make("Toll Post", "Artifact", """{ "attackTaxPerCounter": true }""", "{1}", null,
        "Creatures with counters on them cost their controller {2} per counter to attack you.");

    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public async Task AnAttackerWithCountersPaysTheTaxPerCounterToAttackThatPlayer(int lands, bool attacks)
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance; // no land drop: only the lands given here can pay
        s.Lands(P0, lands);
        var raider = s.Add(P0, Creature("Raider", 1, 1));
        s.Card(raider).Counters[CounterKind.PlusOnePlusOne] = 2;
        s.Add(P1, TollPost());
        await s.RunUntilTurn();
        Assert.Equal(attacks, s.Game.Log.Any(e => e is AttackerDeclared a && a.Attacker == raider));
        Assert.Equal(attacks ? 17 : 20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ACreatureWithoutCountersAttacksThatPlayerForFree()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var bare = s.Add(P0, Creature("Bare", 1, 1));
        s.Add(P1, TollPost());
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is AttackerDeclared a && a.Attacker == bare);
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ greatest mana value cast this turn

    [Fact]
    public async Task GreatestManaValueCastKeepsTheHighestValueAfterTheSpellsResolve()
    {
        var s = new Scenario();
        s.Lands(P0, 7); // 4 for the sorcery with X = 3, 2 for the instant, 1 for the creature that reads the number
        s.Attacker.Number = (_, _) => 3; // X
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var sorcery = s.InHand(P0, Make("Heavy Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{X}{R}", null, "You gain 1 life."));
        var instant = s.InHand(P0, Make("Quick Note", "Instant", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{1}{R}", null, "You gain 1 life."));
        var probe = s.InHand(P0, Make("Probe Creature", "Creature — Probe",
            """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": { "greatestManaValueCast": { "types": ["instant", "sorcery"] } } }] }] }""",
            "{1}", "1/1", "When this creature enters, you gain life equal to the greatest mana value of an instant or sorcery you cast this turn."));
        var order = new[] { sorcery, instant, probe };
        int next = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (next < order.Length && s.Card(order[next]).Zone == Zone.Hand && (next == 0 || s.Card(order[next - 1]).Zone == Zone.Graveyard)
                && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == order[next]) is { } cast)
            {
                next++;
                return cast;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        // 20 + 1 (sorcery) + 1 (instant) + 4 (the sorcery's mana value on the stack, X = 3 and {R}) from the creature's trigger
        Assert.Equal(26, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ an emblem with a mana ability

    private const string ChannelSpell = """{ "spell": { "effects": [{ "emblem": "Channel", "untilEndOfTurn": true, "abilities": [{ "cost": "life:1", "effects": [{ "addMana": "{C}" }], "text": "Pay 1 life: Add {C}." }] }] } }""";

    [Fact]
    public async Task AnEmblemsManaAbilityCanBeActivatedTwiceAndTheEmblemEndsWithTheTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var channel = s.InHand(P0, Make("Channel Note", "Sorcery", ChannelSpell, "{R}", null, "Create an emblem with \"Pay 1 life: Add {C}.\""));
        bool cast = false;
        CardId? emblem = null;
        var before = new List<(int Life, int Colorless)>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == channel) is { } spell) { cast = true; return spell; }
            var emblems = s.Game.State.EmblemsUntilEndOfTurn;
            if (emblems.Count > 0) emblem = emblems[0];
            if (before.Count < 2 && legal.OfType<ActivateAbility>().FirstOrDefault(a => emblems.Contains(a.Source)) is { } ability)
            {
                var player = s.Game.State.GetPlayer(P0);
                before.Add((player.Life, player.ManaPool[ManaType.Colorless]));
                return ability;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal((20, 0), before[0]);
        Assert.Equal((19, 1), before[1]);
        Assert.Equal(18, s.Game.State.GetPlayer(P0).Life);
        Assert.NotNull(emblem);
        Assert.DoesNotContain(emblem!.Value, s.Game.State.Emblems);
    }

    // ------------------------------------------------------------------ becoming prepared after combat damage

    /// <summary>A preparation card from card data and a script (copied from the prepared tests).</summary>
    private static CardDefinition Preparation(string script)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"commander-academy-{_next++}", ["name"] = "Lookout Owl // Owl Notes", ["layout"] = "prepare", ["mana_cost"] = "{R} // {R}",
            ["type_line"] = "Creature — Bird // Sorcery", ["keywords"] = new[] { "Prepared" },
            ["card_faces"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "Lookout Owl", ["mana_cost"] = "{R}", ["type_line"] = "Creature — Bird", ["oracle_text"] = "", ["power"] = "1", ["toughness"] = "1" },
                new Dictionary<string, object?> { ["name"] = "Owl Notes", ["mana_cost"] = "{R}", ["type_line"] = "Sorcery", ["oracle_text"] = "Draw a card." },
            },
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        var made = CardFactory.Create(record, CardScriptParser.Parse(script));
        Assert.Equal(CardSupport.Full, made.Support);
        return made.Definition;
    }

    private const string LookoutScript = """{ "abilities": [{ "trigger": "youAttack", "targets": ["creature"], "effects": [{ "whenDealsCombatDamageThisCombat": "target", "effects": [{ "becomePrepared": "self" }] }] }], "prepare": { "spell": { "effects": [{ "draw": 1 }] } } }""";

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task TheSourceBecomesPreparedOnlyWhenTheTargetDealsCombatDamage(bool targetDeals, bool prepared)
    {
        var s = new Scenario();
        var source = s.Game.SetupPermanent(P0, Preparation(LookoutScript));
        var target = s.Add(P0, Creature("Target", 2, 2));
        var other = s.Add(P0, Creature("Other", 1, 1));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(target) };
        var dealer = targetDeals ? target : other;
        s.Attacker.Attack = (_, _, defenders) => new[] { new AttackDeclaration(dealer, defenders[0]) };
        await s.RunUntilTurn();
        Assert.Equal(prepared, s.Card(source).PreparedCopy is not null);
    }

    // ------------------------------------------------------------------ any player's hand size on each upkeep

    private static CardDefinition HandWatch() => Make("Hand Watch", "Enchantment",
        """{ "abilities": [{ "trigger": "eachUpkeep", "if": { "anyPlayerHandAtMost": 1 }, "effects": [{ "gainLife": 1 }] }] }""", "{1}", null,
        "At the beginning of each upkeep, if a player has one or fewer cards in hand, you gain 1 life.");

    /// <summary>Moves the cards in hand back to the library, then puts <paramref name="count"/> library cards into hand.</summary>
    private static void SetHand(Scenario s, PlayerId owner, int count)
    {
        var player = s.Game.State.GetPlayer(owner);
        foreach (var id in player.Hand.ToList())
        {
            player.Hand.Remove(id);
            player.Library.Add(id);
            s.Card(id).Zone = Zone.Library;
        }
        for (int i = 0; i < count; i++)
        {
            var id = player.Library[0];
            player.Library.RemoveAt(0);
            player.Hand.Add(id);
            s.Card(id).Zone = Zone.Hand;
        }
    }

    [Theory]
    [InlineData(1, 3, 21)]
    [InlineData(2, 2, 20)]
    public async Task AnUpkeepTriggerWaitsForAnyPlayerToHaveAtMostOneCardInHand(int opponentHand, int ownHand, int life)
    {
        var s = new Scenario();
        s.Add(P0, HandWatch());
        bool set = false;
        s.Attacker.Act = (_, _) =>
        {
            if (!set)
            {
                set = true;
                SetHand(s, P0, ownHand);
                SetHand(s, P1, opponentHand);
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(3); // the upkeep of player 1's turn is the one that counts
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ mana for X costs only

    [Theory]
    [InlineData("{X}{R}", 1, true)]
    [InlineData("{2}{R}", 0, false)]
    public async Task AnArtifactsManaIsSpentOnlyOnXCosts(string cost, int x, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var engine = s.Add(P0, Make("Charge Engine", "Artifact",
            """{ "tapForMana": "{C}", "manaAmountFrom": { "counters": "charge" }, "manaOnlyForXCosts": true }""", "{1}", null,
            "Tap this artifact for {C} for each charge counter on it. Spend this mana only to pay costs that have {X} in them."));
        s.Card(engine).Counters[CounterKind.Charge] = 2;
        var sorcery = s.InHand(P0, Make("Ember Sum", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", cost, null, "You gain 1 life."));
        s.Attacker.Number = (_, _) => x;
        CastFirst(s, sorcery);
        await s.RunUntilTurn();
        Assert.Equal(castable ? Zone.Graveyard : Zone.Hand, s.Card(sorcery).Zone);
    }

    // ------------------------------------------------------------------ X doubled on a spell that has X

    [Fact]
    public async Task ACreatureCastWithXDoublesItsXWhenAnotherPermanentSaysSo()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, Make("Echo Lens", "Artifact",
            """{ "abilities": [{ "trigger": "castSpell", "filter": { "hasX": true }, "effects": [{ "doubleTriggeredSpellX": true }] }] }""", "{1}"));
        var sprout = s.InHand(P0, Make("Growing Sprout", "Creature — Plant", """{ "entersWithCounters": "X" }""", "{X}{G}", "0/0"));
        s.Attacker.Number = (_, _) => 2;
        CastFirst(s, sprout);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(sprout).Zone);
        Assert.Equal(4, s.Card(sprout).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ copying an X ability

    [Theory]
    [InlineData(0, 5)] // {X} with X = 2: +2/+0, copied for another +2/+0
    [InlineData(1, 2)] // {1}: +1/+0, not copied
    public async Task AnActivatedAbilityWithXIsCopiedAndOneWithoutXIsNot(int index, int power)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var sprite = s.Add(P0, Make("Swelling Sprite", "Creature — Sprite",
            """{ "abilities": [{ "cost": "{X}", "effects": [{ "pump": ["X", 0], "what": "self" }], "text": "{X}: This creature gets +X/+0 until end of turn." }, { "cost": "{1}", "effects": [{ "pump": [1, 0], "what": "self" }], "text": "{1}: This creature gets +1/+0 until end of turn." }] }""",
            "{1}", "1/1"));
        s.Add(P0, Make("Echo Mirror", "Artifact",
            """{ "abilities": [{ "trigger": "youActivateNonManaAbility", "if": "triggeredAbilityCostHasX", "effects": [{ "copyTriggeredAbility": true }] }] }""", "{1}"));
        s.Attacker.Number = (_, _) => 2;
        bool activated = false;
        int? seen = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == sprite && a.Index == index) is { } ability)
            {
                activated = true;
                return ability;
            }
            if (activated && seen is null && s.Game.State.Stack.Count == 0) seen = s.Card(sprite).Power;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(power, seen);
    }

    // ------------------------------------------------------------------ extra mana for the next spell with X

    private const string ReliefSpell = """{ "extraMana": [{ "types": "{C}", "amount": 2, "whenNextCast": { "spells": { "hasX": true }, "effects": [{ "draw": 1 }, { "gainLife": { "halfDown": "triggeredSpellX" } }], "text": "When you next cast a spell with {X} in its cost, draw a card and gain half X life, rounded down." } }] }""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnArtifactsExtraManaDrawsAndGainsLifeOnTheNextSpellWithX(bool plainSpellFirst)
    {
        var s = new Scenario();
        s.Lands(P0, plainSpellFirst ? 3 : 2);
        s.Add(P0, Make("Relief Relic", "Artifact", ReliefSpell, "{1}"));
        var plain = s.InHand(P0, Make("Plain Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 0 }] } }""", "{R}", null, "You gain 0 life."));
        var sum = s.InHand(P0, Make("Sum Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 0 }] } }""", "{X}{R}", null, "You gain 0 life."));
        var order = plainSpellFirst ? new[] { plain, sum } : new[] { sum };
        int next = 0;
        s.Attacker.Number = (_, _) => 3;
        s.Attacker.Act = (_, legal) =>
        {
            if (next < order.Length && s.Game.State.Stack.Count == 0 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == order[next]) is { } cast)
            {
                next++;
                return cast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        int castAt = s.Game.Log.ToList().FindIndex(e => e is SpellCast c && c.Card == sum);
        Assert.True(castAt >= 0);
        Assert.Equal(1, s.Game.Log.Skip(castAt).OfType<CardDrawn>().Count(d => d.Player == P0));
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // half of X = 3, rounded down
    }

    // ------------------------------------------------------------------ +1/+1 counters doubled on other creatures

    private static CardDefinition CounterLens() => Make("Counter Lens", "Creature — Construct",
        """{ "plusOneCountersPlusOneOn": { "types": ["creature"], "other": true } }""", "{1}", "1/1",
        "If you would put a +1/+1 counter on a creature you control other than this one, put two instead.");

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task APlusOneCounterOnAnotherCreatureIsDoubledButNotOnTheSource(bool onOther, int expected)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var lens = s.Add(P0, CounterLens());
        var other = s.Add(P0, Creature("Ally", 1, 1));
        var grow = s.InHand(P0, Make("Grow Note", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "counters": 1, "what": "target" }] } }""", "{R}", null,
            "Put a +1/+1 counter on target creature."));
        var target = onOther ? other : lens;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(target) };
        CastFirst(s, grow);
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(target).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ creatures that entered this turn, even if gone

    [Fact]
    public async Task EnteredThisTurnCountsACreatureThatDiedAsWellAsOneOnTheBattlefield()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var early = s.InHand(P0, Make("Early Bird", "Creature — Bird", "{}", "{1}", "1/1"));
        var kill = s.InHand(P0, Make("Kill Note", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}", null, "Destroy target creature."));
        var late = s.InHand(P0, Make("Late Bird", "Creature — Bird", "{}", "{1}", "1/1"));
        var probe = s.InHand(P0, Make("Count Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": { "enteredThisTurn": { "types": ["creature"], "token": false }, "evenIfGone": true } }] } }""", "{R}", null,
            "You gain life equal to the number of nontoken creatures that entered this turn."));
        var order = new[] { early, kill, late, probe };
        int next = 0;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(early) };
        s.Attacker.Act = (_, legal) =>
        {
            if (next < order.Length && s.Game.State.Stack.Count == 0 && s.Card(order[next]).Zone == Zone.Hand
                && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == order[next]) is { } cast)
            {
                next++;
                return cast;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(early).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(late).Zone);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // the creature that died and the one still there
    }

    // ------------------------------------------------------------------ a token for each opponent that must attack it

    [Fact]
    public async Task ATokenIsCreatedForEachOpponentAndMustAttackThatOpponent()
    {
        var players = new[] { new TestController(), new TestController(), new TestController() };
        AttackRequest? request = null;
        players[0].Attack = (view, _, _) =>
        {
            request = view.AttackRequest;
            return request!.Complete(Array.Empty<AttackDeclaration>());
        };
        foreach (var p in players.Skip(1)) p.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        players[0].Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault() is { } cast) return cast;
            return PassPriority.Instance;
        };
        var lands = Decks.Of((GenericCards.Forest, 20));
        var game = new Game(new GameConfig { Seed = 1, StartingPlayer = P0 }, players.Select((c, i) => new PlayerSetup($"P{i}", c, lands)).ToArray());
        game.SetupPermanent(P0, GenericCards.Mountain);
        game.SetupInHand(P0, Make("Rally Note", "Sorcery", """{ "spell": { "effects": [{ "tokens": 1, "forEachOpponentMustAttack": true, "token": { "name": "Raid Scout", "types": "Creature — Scout", "power": 1, "toughness": 1, "keywords": ["Haste"] } }] } }""", "{R}", null,
            "Create a 1/1 red Scout creature token with haste for each opponent. Each of those tokens attacks its opponent this combat if able."));
        await Run(game);
        var tokens = game.State.Battlefield.Where(id => game.State.GetCard(id).Definition.IsToken).ToList();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(request);
        var requirements = request!.Requirements.Where(r => r.Kind == AttackRequirementKind.AttacksPlayer).ToList();
        Assert.Equal(2, requirements.Select(r => r.Attacker).Distinct().Count());
        Assert.Contains(requirements, r => tokens.Contains(r.Attacker) && r.Player == P1);
        Assert.Contains(requirements, r => tokens.Contains(r.Attacker) && r.Player == P2);
    }

    // ------------------------------------------------------------------ a spell cost reduction when it targets a creature

    [Theory]
    [InlineData(true, 1, true)]   // targets a creature: costs {R}
    [InlineData(false, 1, false)] // targets a player: costs {2}{R}, one Mountain is not enough
    [InlineData(false, 3, true)]  // targets a player: castable with {2}{R}
    public async Task ASpellCostReductionAppliesOnlyWhenTheSpellTargetsACreature(bool targetCreature, int mountains, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, mountains);
        s.Add(P0, Make("Cost Warden", "Creature — Human",
            """{ "abilities": [{ "spellCost": { "spells": {}, "amount": 2, "ifTargets": { "types": ["creature"] } } }] }""", "{1}", "1/1"));
        var bait = s.Add(P1, Creature("Bait", 2, 2));
        var burn = s.InHand(P0, Make("Burn Note", "Sorcery", """{ "spell": { "targets": ["any"], "effects": [{ "damage": 2, "to": "target" }] } }""", "{2}{R}", null,
            "Burn Note deals 2 damage to target creature or player."));
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => targetCreature ? t.Card == bait : t.Player == P1) };
        CastFirst(s, burn);
        await s.RunUntilTurn();
        Assert.Equal(castable ? Zone.Graveyard : Zone.Hand, s.Card(burn).Zone);
    }

    // ------------------------------------------------------------------ batch 3: helpers

    private static void ToGraveyard(Scenario s, PlayerId owner, CardId id)
    {
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Library.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
    }

    private static void ToExile(Scenario s, PlayerId owner, CardId id)
    {
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Library.Remove(id);
        player.Exile.Add(id);
        s.Card(id).Zone = Zone.Exile;
    }

    private static CardDefinition Aura(string name, string script = "{}", string cost = "{1}") =>
        Make(name, "Enchantment — Aura", script, cost, null, "Enchant creature", "Enchant");

    // ------------------------------------------------------------------ a reanimated artifact that is a creature

    [Fact]
    public async Task AReanimatedArtifactIsAFlyingSpiritCreatureWithAFinalityCounter()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var relic = s.InHand(P0, Make("Dusty Relic", "Artifact", "{}", "{2}"));
        ToGraveyard(s, P0, relic);
        var raise = s.InHand(P0, Make("Raise Note", "Sorcery", """
            { "spell": { "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["artifact"] } }],
              "effects": [{ "reanimate": "target", "addTypes": ["creature"], "addSubtypes": ["Spirit"], "setPower": 1, "setToughness": 1,
                            "keywords": ["Flying"], "counters": 1, "counterKind": "finality" }] } }
            """, "{R}", null, "Return target artifact card from your graveyard to the battlefield. It's a 1/1 Spirit creature with flying."));
        CastFirst(s, raise);
        await s.RunUntilTurn();
        var card = s.Card(relic);
        Assert.Equal(Zone.Battlefield, card.Zone);
        Assert.True(card.IsCreature);
        Assert.True(card.HasSubtype("Spirit"));
        Assert.Equal(1, card.Power);
        Assert.True(card.Has(Keyword.Flying));
        Assert.Equal(1, card.CounterCount(CounterKind.Finality));
    }

    // ------------------------------------------------------------------ Auras attached to creatures in a count

    [Fact]
    public async Task ACountOfAurasAttachedToCreaturesIgnoresTheOneOnALand()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var forest = s.Add(P0, GenericCards.Forest);
        s.Game.SetupPermanent(P0, Aura("Ward Aura"), bear);
        s.Game.SetupPermanent(P0, Aura("Ward Aura"), forest);
        var tally = s.InHand(P0, Make("Tally Note", "Sorcery", """{ "spell": { "effects": [{ "gainLife": { "count": { "subtype": "Aura", "attachedToCreature": true } } }] } }""", "{R}", null,
            "You gain life equal to the number of Auras you control attached to creatures."));
        CastFirst(s, tally);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ revealing the top eight and casting an Aura free

    [Fact]
    public async Task RevealTopCastFreeExilesEightCastsTheAuraAndPutsTheRestOnTheBottom()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var aura = s.Game.SetupInLibrary(P0, Aura("Hidden Aura"));
        var dig = s.InHand(P0, Make("Dig Note", "Sorcery", """{ "spell": { "effects": [{ "revealTopCastFree": 8, "filter": { "subtype": "Aura" }, "exileRestToBottom": true }] } }""", "{R}", null,
            "Reveal the top eight cards of your library. You may cast an Aura among them without paying its mana cost. Put the rest on the bottom."));
        int before = -1;
        bool started = false, cast = false;
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == aura).Select(o => o.Id).ToList();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        s.Attacker.Act = (_, legal) =>
        {
            if (!started)
            {
                started = true;
                s.Restack(P0, aura);
                before = s.Game.State.GetPlayer(P0).Library.Count;
            }
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == dig) is { } spell) { cast = true; return spell; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(aura).Zone);
        Assert.Equal(before - 1, s.Game.State.GetPlayer(P0).Library.Count);
    }

    // ------------------------------------------------------------------ a creature leaving your graveyard

    [Fact]
    public async Task ACreatureCardLeavingYourGraveyardLetsTheSourceBecomeACopyUntilEndOfTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var mimic = s.Add(P0, Make("Mimic Pool", "Creature — Shapeshifter", """
            { "abilities": [{ "trigger": "leavesGraveyard", "batched": true, "filter": { "controller": "you" },
              "effects": [{ "becomeCopy": "self", "target": "batch", "mayChooseAmong": { "types": ["creature"] } }] }] }
            """, "{1}", "1/1"));
        var bear = s.InHand(P0, Creature("Old Bear", 3, 3));
        ToGraveyard(s, P0, bear);
        var bounce = s.InHand(P0, Make("Return Note", "Sorcery", """
            { "spell": { "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"] } }],
              "effects": [{ "returnFromGraveyard": { "types": ["creature"], "controller": "you" }, "to": "hand" }] } }
            """, "{R}", null, "Return target creature card from your graveyard to your hand."));
        string? seen = null;
        s.Attacker.Choose = (_, r) => r.Options.Take(1).Select(o => o.Id).ToList();
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(bounce).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bounce) is { } cast) return cast;
            if (s.Card(bounce).Zone == Zone.Graveyard && s.Game.State.Stack.Count == 0) seen ??= s.Card(mimic).Name;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal("Old Bear", seen);
    }

    // ------------------------------------------------------------------ exiling several cards from the library at once

    [Fact]
    public async Task ExilingThreeCardsFromYourLibraryAtOnceTriggersOnce()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Watch Owl", "Creature — Owl", """{ "abilities": [{ "trigger": "exiledFromLibraryOrGraveyard", "batched": true, "filter": { "controller": "you" }, "effects": [{ "gainLife": 1 }] }] }""", "{1}", "1/1"));
        var ransack = s.InHand(P0, Make("Ransack Library", "Sorcery", """{ "spell": { "effects": [{ "exileTop": 3 }] } }""", "{R}", null, "Exile the top three cards of your library."));
        CastFirst(s, ransack);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ an Equipment found on the top and attached

    [Fact]
    public async Task AnEquipmentFoundOnTheTopEntersAndAttachesToTheChosenCreature()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var blade = s.Game.SetupInLibrary(P0, Make("Brass Blade", "Artifact — Equipment", "{}", "{1}"));
        var quest = s.InHand(P0, Make("Quest Note", "Sorcery", """
            { "spell": { "effects": [{ "lookAtTop": 6, "filter": { "subtype": "Equipment" }, "to": "battlefield" },
                { "if": { "foundHas": { "subtype": "Equipment" } }, "then": [{ "attach": "found", "to": { "choose": { "types": ["creature"], "controller": "you" } } }] }] } }
            """, "{R}", null, "Look at the top six cards. Put an Equipment onto the battlefield and attach it to a creature you control."));
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == bear || o.Id == blade).Select(o => o.Id).ToList();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        bool started = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, blade); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == quest) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(blade).Zone);
        Assert.Equal(bear, s.Card(blade).AttachedTo);
    }

    // ------------------------------------------------------------------ an Aura's attack trigger

    [Fact]
    public async Task AnEnchantedAttacksTriggerFiresWhenTheEnchantedCreatureAttacks()
    {
        var s = new Scenario();
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, Make("Battle Veil", "Enchantment — Aura",
            """{ "abilities": [{ "trigger": "enchantedAttacks", "effects": [{ "gainLife": 1 }] }] }""", "{1}", null,
            "Enchant creature\nWhenever enchanted creature attacks, you gain 1 life.", "Enchant"), bear);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ revealing until an Aura, to the battlefield or hand

    [Theory]
    [InlineData(true, Zone.Battlefield)]
    [InlineData(false, Zone.Hand)]
    public async Task RevealUntilAnAuraPutsItOntoTheBattlefieldIfYouChooseAndIntoHandOtherwise(bool yes, Zone expected)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Bear", 2, 2));
        var aura = s.Game.SetupInLibrary(P0, Aura("Found Aura"));
        var search = s.InHand(P0, Make("Search Note", "Sorcery", """{ "spell": { "effects": [{ "revealUntil": { "subtype": "Aura" }, "to": "hand", "mayBattlefield": true }] } }""", "{R}", null,
            "Reveal cards until you reveal an Aura. Put it into your hand, or onto the battlefield if you choose."));
        s.Attacker.YesNo = (_, _) => yes;
        bool started = false;
        Zone? seen = null; // where it went as the spell resolved (a full hand discards in the cleanup step)
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, aura); }
            if (s.Card(search).Zone == Zone.Graveyard && s.Game.State.Stack.Count == 0) seen ??= s.Card(aura).Zone;
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == search) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(expected, seen);
    }

    // ------------------------------------------------------------------ an animate Aura on an opponent's creature card

    [Fact]
    public async Task AnAnimateAuraReturnsAnOpponentsCreatureUnderYourControlAndDestroyingItSacrificesIt()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var ghost = s.InHand(P1, Creature("Ghost", 2, 2));
        ToGraveyard(s, P1, ghost);
        var animate = s.InHand(P0, Make("Animate Veil", "Enchantment — Aura", """
            { "aura": { "kind": "graveyardCard", "controller": "any", "filter": { "types": ["creature"] } },
              "abilities": [{ "trigger": "enters", "effects": [{ "animateEnchantedCard": true }] },
                            { "trigger": "leaves", "effects": [{ "sacrificeAnimated": true }] },
                            { "static": { "affects": "enchanted", "pump": [-1, 0] } }] }
            """, "{1}", null, "Enchant creature card in a graveyard. When this Aura enters, return enchanted card to the battlefield under your control. It gets -1/-0.", "Enchant"));
        var destroy = s.InHand(P0, Make("Break Note", "Sorcery", """{ "spell": { "targets": [{ "kind": "permanent", "filter": { "types": ["enchantment"] } }], "effects": [{ "destroy": "target" }] } }""", "{R}", null, "Destroy target enchantment."));
        int? powerUnderControl = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(animate).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == animate) is { } aura) return aura;
            if (s.Card(animate).Zone == Zone.Battlefield && s.Card(destroy).Zone == Zone.Hand && s.Game.State.Stack.Count == 0)
            {
                powerUnderControl = s.Card(ghost).Power;
                if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == destroy) is { } breaker) return breaker;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(1, powerUnderControl);
        Assert.Equal(Zone.Graveyard, s.Card(ghost).Zone);
        Assert.Contains(ghost, s.Game.State.GetPlayer(P1).Graveyard);
    }

    // ------------------------------------------------------------------ an opponent's creature enchanted by your Aura attacks

    [Fact]
    public async Task AnOpponentsCreatureEnchantedByYourAuraTriggersTheObserverWhenItAttacks()
    {
        var s = new Scenario();
        s.Add(P0, Make("Herald Owl", "Creature — Owl", """{ "abilities": [{ "trigger": "creaturesAttackMatching", "filter": { "types": ["creature"], "enchantedByYours": true }, "effects": [{ "gainLife": 1 }] }] }""", "{1}", "1/1"));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, Aura("Yours Veil"), bear);
        s.Defender.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn(3);
        Assert.Equal(19, s.Game.State.GetPlayer(P0).Life); // 2 combat damage from the bear, 1 life from the observer
    }

    // ------------------------------------------------------------------ casting at a modified creature

    [Theory]
    [InlineData(true, 21)]
    [InlineData(false, 20)]
    public async Task CastingAnAuraAtYourModifiedCreatureTriggersTheObserver(bool modified, int life)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Keen Watcher", "Creature — Scout", """{ "abilities": [{ "trigger": "castSpell", "filter": { "targets": { "modified": true, "controller": "you" } }, "effects": [{ "gainLife": 1 }] }] }""", "{1}", "1/1"));
        var counted = s.Add(P0, Creature("Counted", 2, 2));
        s.Card(counted).Counters[CounterKind.PlusOnePlusOne] = 1;
        var plain = s.Add(P0, Creature("Plain", 2, 2));
        var veil = s.InHand(P0, Aura("Keen Veil", "{}", "{R}"));
        var target = modified ? counted : plain;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(target) };
        CastFirst(s, veil);
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ the number of your Auras when a creature dies

    [Fact]
    public async Task ACreatureWithTwoOfYourAurasDyingGainsLifeForEachAura()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Grim Vigil", "Creature — Spirit", """{ "abilities": [{ "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any", "enchanted": true }, "effects": [{ "gainLife": "yourAurasOnTriggeredAtDeath" }] }] }""", "{1}", "1/1"));
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.Game.SetupPermanent(P0, Aura("Veil One"), bear);
        s.Game.SetupPermanent(P0, Aura("Veil Two"), bear);
        var snap = s.InHand(P0, Snap());
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        CastFirst(s, snap);
        await s.RunUntilTurn();
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ a condition on a modified creature dying this turn

    [Theory]
    [InlineData(true, 21)]
    [InlineData(false, 20)]
    public async Task AModifiedCreatureDyingThisTurnIsAConditionOfTheTrigger(bool modified, int life)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Grief Watch", "Creature — Spirit", """{ "abilities": [{ "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any" }, "if": "modifiedCreatureDiedThisTurn", "effects": [{ "gainLife": 1 }] }] }""", "{1}", "1/1"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        if (modified) s.Card(bear).Counters[CounterKind.PlusOnePlusOne] = 1;
        var snap = s.InHand(P0, Snap());
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bear) };
        CastFirst(s, snap);
        await s.RunUntilTurn();
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ revealing the top card of both libraries

    [Fact]
    public async Task RevealTopDuelMakesEachPlayerLoseTheOtherCardsManaValueAndBothCardsGoToHand()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var mine = s.Game.SetupInLibrary(P0, Make("Duel Bird", "Creature — Bird", "{}", "{3}", "1/1"));
        var theirs = s.Game.SetupInLibrary(P1, Make("Duel Beast", "Creature — Beast", "{}", "{2}", "1/1"));
        var duel = s.InHand(P0, Make("Duel Note", "Sorcery", """{ "spell": { "targets": ["opponent"], "effects": [{ "revealTopDuel": "target" }] } }""", "{R}", null,
            "Each player reveals the top card of their library. Each player loses life equal to the other card's mana value. Put those cards into their owners' hands."));
        bool started = false, cast = false;
        (Zone Mine, Zone Theirs)? seen = null; // as the spell resolved (a full hand discards in the cleanup step)
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, mine); s.Restack(P1, theirs); }
            if (s.Card(duel).Zone == Zone.Graveyard && s.Game.State.Stack.Count == 0) seen ??= (s.Card(mine).Zone, s.Card(theirs).Zone);
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == duel) is { } spell) { cast = true; return spell; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(18, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal((Zone.Hand, Zone.Hand), seen);
    }

    // ------------------------------------------------------------------ an extra cost for each target after the first

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(2, 5, true)]
    public async Task AnExtraTargetCostIsPaidForEachTargetAfterTheFirst(int targets, int mountains, bool castable)
    {
        var s = new Scenario();
        s.Lands(P0, mountains);
        s.Add(P1, Creature("First", 2, 2));
        s.Add(P1, Creature("Second", 2, 2));
        var many = s.InHand(P0, Make("Many Note", "Sorcery", """{ "extraTargetCost": "{2}{R}", "spell": { "targets": [{ "kind": "creature", "anyNumber": true }], "effects": [{ "gainLife": 1 }] } }""", "{1}{R}", null,
            "Choose any number of target creatures. This spell costs {2}{R} more for each target after the first."));
        s.Attacker.Targets = (_, request) => request.LegalAt(0).Take(targets).ToList();
        CastFirst(s, many);
        var error = await Record.ExceptionAsync(() => s.RunUntilTurn());
        if (castable)
        {
            Assert.Null(error);
            Assert.Equal(Zone.Graveyard, s.Card(many).Zone);
        }
        else
        {
            Assert.IsType<InvalidDecisionException>(error); // the second target can't be paid for
            Assert.Equal(Zone.Hand, s.Card(many).Zone);
        }
    }

    // ------------------------------------------------------------------ a total mana value limit on the creatures put onto the battlefield

    [Fact]
    public async Task CreaturesPutOntoTheBattlefieldTotalNoMoreThanTheManaValueLimit()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var three = s.Game.SetupInLibrary(P0, Make("Three Bird", "Creature — Bird", "{}", "{3}", "2/2"));
        var two = s.Game.SetupInLibrary(P0, Make("Two Bird", "Creature — Bird", "{}", "{2}", "1/1"));
        var sift = s.InHand(P0, Make("Sift Note", "Sorcery", """{ "spell": { "effects": [{ "lookAtTop": 7, "filter": { "types": ["creature"] }, "to": "battlefield", "maxTotalManaValue": 4 }] } }""", "{R}", null,
            "Look at the top seven cards. Put any number of creatures with total mana value 4 or less onto the battlefield."));
        bool started = false;
        s.Attacker.Choose = (_, r) => r.Options.Select(o => o.Id).ToList(); // 3 + 2 is more than 4: refused
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, three, two); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sift) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var error = await Record.ExceptionAsync(() => s.RunUntilTurn());
        Assert.IsType<InvalidDecisionException>(error);
        Assert.Equal(Zone.Library, s.Card(three).Zone);
        Assert.Equal(Zone.Library, s.Card(two).Zone);
    }

    // ------------------------------------------------------------------ X counters distributed among targets

    [Fact]
    public async Task XCountersAreDistributedAmongTheTargets()
    {
        var s = new Scenario();
        for (int i = 0; i < 4; i++) s.Add(P0, GenericCards.Forest);
        var a = s.Add(P0, Creature("Ally One", 1, 1));
        var b = s.Add(P0, Creature("Ally Two", 1, 1));
        var spread = s.InHand(P0, Make("Spread Note", "Sorcery", """{ "spell": { "targets": [{ "kind": "creature", "controller": "you", "anyNumber": true }], "effects": [{ "distributeCounters": "X" }] } }""", "{X}{G}", null,
            "Distribute X +1/+1 counters among any number of target creatures you control."));
        s.Attacker.Number = (_, r) => r.Prompt.Contains("how much") ? 1 : 3; // X, then one target's share
        s.Attacker.Targets = (_, request) => request.LegalAt(0).Take(2).ToList();
        CastFirst(s, spread);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(a).CounterCount(CounterKind.PlusOnePlusOne) + s.Card(b).CounterCount(CounterKind.PlusOnePlusOne));
    }

    // ------------------------------------------------------------------ two nonland cards discarded at once, one exiled and cast

    [Fact]
    public async Task AfterDiscardingTwoNonlandCardsAtOnceOneCanBeExiledAndCast()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Make("Salvage Owl", "Creature — Owl", """{ "abilities": [{ "trigger": "playerDiscards", "batched": true, "filter": { "controller": "you" }, "effects": [{ "choose": { "not": ["land"], "zone": "graveyard" }, "among": "batch", "optional": true }, { "exile": "chosen", "mayPlayThisTurn": true }] }] }""", "{1}", "1/1"));
        var shrivel = s.InHand(P0, Make("Shrivel Note", "Sorcery", """{ "spell": { "effects": [{ "discard": 2 }] } }""", "{R}", null, "Discard two cards."));
        var x = s.InHand(P0, Make("Pick One", "Creature — Bird", "{}", "{1}", "1/1"));
        var y = s.InHand(P0, Make("Pick Two", "Creature — Bird", "{}", "{1}", "1/1"));
        s.Attacker.Discard = (_, _) => new[] { x, y };
        s.Attacker.Choose = (_, r) => r.Options.Where(o => o.Id == x).Select(o => o.Id).ToList();
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(shrivel).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == shrivel) is { } first) return first;
            if (s.Card(x).Zone == Zone.Exile && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == x) is { } cast) return cast;
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(x).Zone);
    }

    // ------------------------------------------------------------------ red mana equal to the target's hand size

    [Fact]
    public async Task RedManaEqualToTheTargetsHandSizeLastsIntoTheNextStep()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var ration = s.InHand(P0, Make("Ember Ration", "Sorcery", """{ "spell": { "targets": ["opponent"], "effects": [{ "addManaUntilEndOfTurn": "{R}", "times": "targetHandSize" }] } }""", "{R}", null,
            "Target opponent's hand size: add that much {R}. The mana stays until end of turn."));
        int? redAtEndCombat = null;
        bool started = false;
        s.Game.EventRaised += e =>
        {
            if (e is StepBegan { ActivePlayer: var p, Step: Step.EndCombat } && p == P0) redAtEndCombat = s.Game.State.GetPlayer(P0).ManaPool[ManaType.Red];
        };
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; SetHand(s, P1, 3); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == ration) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(3, redAtEndCombat);
    }

    // ------------------------------------------------------------------ reveal until nonland: lands to the bottom, the hit cast or exiled

    [Theory]
    [InlineData(true, Zone.Battlefield)]
    [InlineData(false, Zone.Exile)]
    public async Task RevealUntilNonlandCastsTheHitFreeAndLandsGoToTheBottom(bool cast, Zone hit)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var forest1 = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var forest2 = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var bird = s.Game.SetupInLibrary(P0, Make("Hit Bird", "Creature — Bird", "{}", "{4}", "2/2"));
        var dig = s.InHand(P0, Make("Dig Note", "Sorcery", """{ "spell": { "effects": [{ "revealUntil": { "not": ["land"] }, "castFree": true, "hitStaysExiled": true }] } }""", "{R}", null,
            "Reveal cards until you reveal a nonland card. You may cast it without paying its mana cost; if you don't, it stays exiled. Put the lands on the bottom."));
        bool started = false;
        s.Attacker.YesNo = (_, _) => cast;
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, forest1, forest2, bird); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == dig) is { } spell ? spell : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(hit, s.Card(bird).Zone);
        Assert.Equal(Zone.Library, s.Card(forest1).Zone);
        Assert.Equal(Zone.Library, s.Card(forest2).Zone);
    }

    // ------------------------------------------------------------------ an exiled sorcery cast free this turn, then bottomed

    [Fact]
    public async Task AnExiledSorceryCanBeCastFreeThisTurnAndIsThenOnTheBottomOfTheLibrary()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var scroll = s.InHand(P0, Make("Old Scroll", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}{R}", null, "You gain 1 life."));
        ToExile(s, P0, scroll);
        var unseal = s.InHand(P0, Make("Unseal Note", "Sorcery", """{ "spell": { "targets": [{ "kind": "exiledCard", "controller": "you" }], "effects": [{ "castExiledFreeThisTurnBottomInstead": "target" }] } }""", "{R}", null,
            "Target exiled card you own: you may cast it free this turn. If you don't, it goes to the bottom of your library."));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(scroll) };
        bool unsealed = false, cast = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!unsealed && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == unseal) is { } first) { unsealed = true; return first; }
            if (unsealed && !cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == scroll) is { } second) { cast = true; return second; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(Zone.Library, s.Card(scroll).Zone);
        Assert.Equal(scroll, s.Game.State.GetPlayer(P0).Library.Last());
    }

    // ------------------------------------------------------------------ playing from the graveyard once per turn

    [Fact]
    public async Task ACreatureCastFromYourGraveyardOncePerTurnIsExiledWhenItDiesAndYouGainTwo()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Make("Yard Permit", "Enchantment", """
            { "graveyardCastOncePerTurn": { "filter": { "anyOf": [{ "types": ["land"] }, { "types": ["creature"], "maxManaValue": 3 }] }, "lands": true,
              "grants": [{ "trigger": "putIntoGraveyard", "effects": [{ "exile": "self" }, { "gainLife": 2 }] }] } }
            """, "{1}"));
        var first = s.InHand(P0, Make("Rot Bird", "Creature — Bird", "{}", "{1}", "0/0"));
        var second = s.InHand(P0, Make("Rot Owl", "Creature — Owl", "{}", "{1}", "0/0"));
        ToGraveyard(s, P0, first);
        ToGraveyard(s, P0, second);
        bool cast = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!cast && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == first) is { } spell) { cast = true; return spell; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(first).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(second).Zone); // only once this turn
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ combat damage lets you cast a copy of an exiled sorcery

    [Fact]
    public async Task CombatDamageToAPlayerLetsYouCastACopyOfTheExiledSorcery()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Creature("Striker", 2, 2));
        var scroll = s.InHand(P0, Make("Old Scroll", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{2}{R}", null, "You gain 1 life."));
        ToGraveyard(s, P0, scroll);
        var ransack = s.InHand(P0, Make("Ransack Note", "Sorcery", """
            { "spell": { "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["sorcery"] } }],
              "effects": [{ "exile": "target" }, { "whenYourCreatureDealsCombatDamageThisTurn": "exiled", "effects": [{ "castCopy": "triggered" }] }] } }
            """, "{R}", null, "Exile target sorcery card from your graveyard. Whenever a creature you control deals combat damage to a player this turn, you may cast a copy of it."));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(scroll) };
        CastFirst(s, ransack);
        s.Attacker.Attack = (view, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life); // the copy of the exiled sorcery resolved
    }

    // ------------------------------------------------------------------ copies for each other creature it could target

    [Fact]
    public async Task ACastOnASourceCreatureCopiesTheSpellForEachOtherCreatureItCouldTarget()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var source = s.Add(P0, Make("Mirror Source", "Creature — Mirror", """
            { "abilities": [{ "trigger": "anyPlayerCastsSpell", "targetsSource": true, "filter": { "singleTarget": true }, "effects": [{ "copyForEachOtherCreatureItCouldTarget": true }] }] }
            """, "{1}", "1/1"));
        s.Add(P0, Creature("Other One", 1, 1));
        s.Add(P0, Creature("Other Two", 1, 1));
        var pump = s.InHand(P0, Make("Pump Note", "Sorcery", """{ "spell": { "targets": ["creature"], "effects": [{ "pump": [1, 1], "what": "target" }] } }""", "{R}", null,
            "Target creature gets +1/+1 until end of turn."));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(source) };
        CastFirst(s, pump);
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.OfType<SpellCopied>().Count());
    }

    // ------------------------------------------------------------------ an Aura token attached to the chosen creature

    [Fact]
    public async Task AnAuraTokenIsAttachedToTheChosenOpponentsCreature()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Plains);
        var bait = s.Add(P1, Creature("Bait", 2, 2));
        var pact = s.InHand(P0, Make("Pact Note", "Sorcery", """
            { "spell": { "targets": ["creature:opponent"], "effects": [{ "tokens": 1, "attachTo": "target",
                "token": { "name": "Pact", "types": "Enchantment — Aura", "aura": "creature", "colors": ["W"],
                           "abilities": [{ "static": { "affects": "enchanted", "pump": [-1, -1] } }] } }] } }
            """, "{W}", null, "Create a 1/1 Aura token attached to target creature an opponent controls."));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(bait) };
        CastFirst(s, pact);
        await s.RunUntilTurn();
        var token = s.Game.State.Battlefield.Single(id => s.Card(id).Definition.IsToken);
        Assert.Equal(bait, s.Card(token).AttachedTo);
        Assert.Equal(1, s.Card(bait).Power);
    }

    // ------------------------------------------------------------------ piles, a limit on total cost, and the nonland pick

    [Fact]
    public async Task ExiledPilesExilesTheTopFourCardsOfTheLibrary()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var pile = s.InHand(P0, Make("Pile Note", "Sorcery", """{ "spell": { "effects": [{ "exiledPilesOpponentChooses": 4 }] } }""", "{R}", null,
            "Exile the top four cards of your library. An opponent separates them into two piles."));
        int before = -1;
        bool started = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; before = s.Game.State.GetPlayer(P0).Library.Count; }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == pile) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(before - 8, s.Game.State.GetPlayer(P0).Library.Count); // four face down, then four face up
    }

    [Fact]
    public async Task ExileTopAsYouChooseMovesCardsFromTheTopOfTheLibrary()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var dig = s.InHand(P0, Make("Dig Pile", "Sorcery", """{ "spell": { "effects": [{ "exileTopAsYouChooseCastIfTotalAtMost": 13 }] } }""", "{R}", null,
            "Exile cards from the top of your library one at a time. You may cast any of them with total mana value 13 or less."));
        int before = -1;
        bool started = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; before = s.Game.State.GetPlayer(P0).Library.Count; }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == dig) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.True(s.Game.State.GetPlayer(P0).Library.Count < before);
    }

    [Fact]
    public async Task EachPlayerExilesUntilANonlandAndTheOpponentPicks()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var mineForest = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var mineBird = s.Game.SetupInLibrary(P0, Make("Mine Bird", "Creature — Bird", "{}", "{2}", "1/1"));
        var theirForest = s.Game.SetupInLibrary(P1, GenericCards.Forest);
        var theirBird = s.Game.SetupInLibrary(P1, Make("Their Bird", "Creature — Bird", "{}", "{2}", "1/1"));
        var dig = s.InHand(P0, Make("Dig Each", "Sorcery", """{ "spell": { "effects": [{ "eachExilesUntilNonlandOpponentPicks": 2 }] } }""", "{R}", null,
            "Each player exiles cards from the top of their library until they exile a nonland card. An opponent chooses two of the exiled nonland cards."));
        bool started = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!started) { started = true; s.Restack(P0, mineForest, mineBird); s.Restack(P1, theirForest, theirBird); }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == dig) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.NotEqual(Zone.Library, s.Card(mineBird).Zone);
        Assert.NotEqual(Zone.Library, s.Card(theirBird).Zone);
        Assert.NotEqual(Zone.Library, s.Card(mineForest).Zone);
        Assert.NotEqual(Zone.Library, s.Card(theirForest).Zone);
    }
}
