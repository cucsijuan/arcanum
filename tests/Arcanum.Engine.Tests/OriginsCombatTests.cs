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
/// Magic Origins cards about combat, costs, filters and targeting, played with their module scripts: block taxes, "can block
/// only creatures with flying", "except by creatures with flying or reach", tapping artifacts as a cost, graveyard exile costs,
/// targeting restrictions, conditional uncounterability, copying a countered spell and changing a target to a creature.
/// </summary>
public class OriginsCombatTests
{
    private static CardDefinition Card(string name) => OriginsCards.Get(name);

    private delegate PlayerAction? Step(IReadOnlyList<PlayerAction> legal);

    /// <summary>The player takes these actions in order as they become legal, passing otherwise.</summary>
    private static void Plays(TestController player, Action? setup, params Step[] steps)
    {
        var q = new Queue<Step>(steps);
        bool first = true;
        player.Act = (_, legal) =>
        {
            if (first) { first = false; setup?.Invoke(); }
            if (q.Count > 0 && q.Peek()(legal) is { } a) { q.Dequeue(); return a; }
            return PassPriority.Instance;
        };
    }

    private static Step Cast(Scenario s, string name) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);
    private static Step Activate(Scenario s, string name) => legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == name);

    private static void Lands(Scenario s, PlayerId owner, CardDefinition land, int n) { for (int i = 0; i < n; i++) s.Add(owner, land); }

    /// <summary>Moves a card to its owner's graveyard while the game runs.</summary>
    private static void ToGraveyard(Scenario s, CardId id)
    {
        var card = s.Card(id);
        var owner = s.Game.State.GetPlayer(card.Owner);
        owner.Library.Remove(id);
        owner.Hand.Remove(id);
        s.Game.State.Battlefield.Remove(id);
        card.Zone = Zone.Graveyard;
        owner.Graveyard.Add(id);
    }

    private static CardDefinition Scripted(string name, CardType types, string cost, string script, int? power = null, int? toughness = null)
    {
        var parsed = CardScriptParser.Parse(script);
        var card = new CardDefinition
        {
            Name = name, Types = types, ManaCost = ManaCost.Parse(cost), Power = power, Toughness = toughness,
            Spell = parsed.Spell, Abilities = parsed.Abilities, EnchantTarget = parsed.Aura,
        };
        return parsed.ApplyTo(card);
    }

    private static CardDefinition Instant(string name) => new() { Name = name, Types = CardType.Instant, ManaCost = ManaCost.Parse("{U}") };

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    // ------------------------------------------------------------------ Archangel of Tithes

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttackingArchangelMakesEachBlockingCreatureCostOne(bool canPay)
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var angel = s.Add(P0, Card("Archangel of Tithes"));
        var spiderA = s.Add(P1, Creature("Spider A", 1, 4, Keyword.Reach));
        var spiderB = s.Add(P1, Creature("Spider B", 1, 4, Keyword.Reach));
        var land = canPay ? s.Add(P1, GenericCards.Mountain) : (CardId?)null;
        var declarations = new List<int>();
        s.Defender.Block = (_, blockers, attackers) =>
        {
            // First both block (two to pay for, one land), then only one.
            var wanted = (declarations.Count == 0 ? blockers : blockers.Take(1)).Select(b => new BlockDeclaration(b, attackers[0])).ToList();
            declarations.Add(wanted.Count);
            return wanted;
        };
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Equal("{1}", s.Defender.LastBlockRequest?.TaxPerBlocker);
        var blocks = s.Game.Log.OfType<BlockerDeclared>().ToList();
        if (canPay)
        {
            Assert.Equal(new[] { 2, 1 }, declarations);
            Assert.Single(blocks);
            Assert.Contains(s.Game.Log, e => e is PermanentTapped t && t.Card == land!.Value);
            Assert.Equal(life, Life(s, P1));
        }
        else
        {
            Assert.Empty(blocks); // never paid for: no blocks
            Assert.Equal(life - 3, Life(s, P1));
        }
        Assert.All(new[] { spiderA, spiderB }, id => Assert.Equal(Zone.Battlefield, s.Card(id).Zone));
        Assert.Equal(Zone.Battlefield, s.Card(angel).Zone);
    }

    [Fact]
    public async Task ABotBlocksOnlyWithAsManyCreaturesAsItCanPayFor()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Add(P0, Card("Archangel of Tithes") with { Power = 9 }); // lethal unless blocked: the bot wants every blocker
        s.Add(P1, Creature("Spider A", 1, 4, Keyword.Reach));
        s.Add(P1, Creature("Spider B", 1, 4, Keyword.Reach));
        s.Add(P1, GenericCards.Mountain);
        s.Game.State.GetPlayer(P1).Life = 5;
        var bot = new Bots.BotController(P1);
        int calls = 0;
        s.Defender.Block = (view, _, _) => { calls++; return bot.DeclareBlockersAsync(view, s.Defender.LastBlockRequest!).GetAwaiter().GetResult(); };
        await s.RunUntilTurn();
        Assert.Equal(1, s.Defender.LastBlockRequest!.AffordableBlockers);
        Assert.Equal(1, calls); // paid at the first declaration
        Assert.Single(s.Game.Log.OfType<BlockerDeclared>());
    }

    [Fact]
    public async Task BlockingIsFreeWhenTheArchangelIsntAttacking()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var angel = s.Add(P0, Card("Archangel of Tithes"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.Add(P1, Creature("Wall", 0, 4));
        s.Attacker.Attack = (_, _, d) => new[] { new AttackDeclaration(bear, d[0]) };
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Null(s.Defender.LastBlockRequest?.TaxPerBlocker);
        Assert.Single(s.Game.Log.OfType<BlockerDeclared>());
        Assert.False(s.Card(angel).Tapped);
    }

    [Fact]
    public async Task ABlockTaxLiftsBlockingRequirements()
    {
        // "All creatures able to block it do so" asks for a block, but every block costs {1}: a requirement is never paid for (509.1c).
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Add(P0, Card("Archangel of Tithes"));
        s.Add(P0, Creature("Bait", 1, 1, Keyword.Lure));
        s.Add(P1, GenericCards.Mountain);
        s.Add(P1, Creature("Wall", 0, 4));
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Empty(s.Defender.LastBlockRequest!.Lures);
        Assert.Equal(life - 4, Life(s, P1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntappedArchangelTaxesAttacks(bool tapped)
    {
        var s = new Scenario();
        bool first = true;
        var angel = s.Add(P1, Card("Archangel of Tithes"));
        s.Attacker.Act = (_, _) =>
        {
            if (first) { first = false; s.Card(angel).Tapped = tapped; }
            return PassPriority.Instance;
        };
        var raider = s.Add(P0, Creature("Raider", 3, 3));
        await s.RunUntilTurn();
        // No mana to pay: it attacks only while the Archangel is tapped.
        Assert.Equal(tapped, s.Game.Log.Any(e => e is AttackerDeclared a && a.Attacker == raider));
    }

    // ------------------------------------------------------------------ blocking restrictions

    [Fact]
    public async Task ScrapskinDrakeAndStratusWalkBlockOnlyCreaturesWithFlying()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var ground = s.Add(P0, Creature("Ground", 2, 2));
        var flyer = s.Add(P0, Creature("Flyer", 2, 2, Keyword.Flying));
        var drake = s.Add(P1, Card("Scrapskin Drake"));
        var walker = s.Add(P1, Creature("Walker", 2, 2));
        s.Game.SetupPermanent(P1, Card("Stratus Walk"), walker);
        var plain = s.Add(P1, Creature("Plain", 2, 2));
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.Equal(new[] { flyer }, request.CanBlock[drake]);
        Assert.Equal(new[] { flyer }, request.CanBlock[walker]);
        Assert.True(s.Card(walker).Has(Keyword.Flying));
        Assert.Equal(new[] { ground }, request.CanBlock[plain]);
    }

    [Fact]
    public async Task CanBlockOnlyFlyersIsAlsoAnUnmetRequirementNotDemanded()
    {
        // A lure without flying: the drake can't block it, so it has no requirement to do so.
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Add(P0, Creature("Bait", 1, 1, Keyword.Lure));
        var drake = s.Add(P1, Card("Scrapskin Drake"));
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Equal(life - 1, Life(s, P1));
        Assert.False(s.Defender.LastBlockRequest?.CanBlock.ContainsKey(drake) == true);
    }

    [Fact]
    public async Task StratusWalkDrawsACardAsItEnters()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Island, 2);
        s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, Card("Stratus Walk"));
        bool playing = false;
        int draws = 0;
        s.Game.EventRaised += e => { if (playing && e is CardDrawn d && d.Player == P0) draws++; };
        Plays(s.Attacker, () => playing = true, Cast(s, "Stratus Walk"));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        var walk = s.Game.State.Cards.Values.Single(c => c.Name == "Stratus Walk");
        Assert.Equal(Zone.Battlefield, walk.Zone);
        Assert.Equal(1, draws); // the starting player skips the first draw step: only the Aura's draw
    }

    [Fact]
    public async Task OrchardSpiritCanBeBlockedOnlyByCreaturesWithFlyingOrReach()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var spirit = s.Add(P0, Card("Orchard Spirit"));
        var ground = s.Add(P1, Creature("Ground", 2, 2));
        var flyer = s.Add(P1, Creature("Flyer", 1, 1, Keyword.Flying));
        var reach = s.Add(P1, Creature("Spider", 1, 3, Keyword.Reach));
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest!;
        Assert.Contains(flyer, request.Blockers);
        Assert.Contains(reach, request.Blockers);
        Assert.DoesNotContain(ground, request.Blockers);
        Assert.Equal(new[] { spirit }, request.CanBlock[reach]);
    }

    // ------------------------------------------------------------------ costs

    [Fact]
    public async Task AetherGridTapsTwoArtifactsEvenSummoningSickOnes()
    {
        var s = new Scenario();
        s.Add(P0, Card("Ghirapur Aether Grid"));
        var golem = s.Add(P0, Creature("Golem", 1, 1) with { Types = CardType.Artifact | CardType.Creature });
        var trinket = s.Add(P0, new CardDefinition { Name = "Trinket", Types = CardType.Artifact });
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Targets = (_, req) => new[] { Target.Of(P1) };
        Plays(s.Attacker, () => s.Card(golem).ControlledSinceTurnStart = false, Activate(s, "Ghirapur Aether Grid"));
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Equal(life - 1, Life(s, P1));
        Assert.True(s.Card(golem).Tapped);
        Assert.True(s.Card(trinket).Tapped);
    }

    [Fact]
    public async Task AetherGridNeedsTwoUntappedArtifacts()
    {
        var s = new Scenario();
        s.Add(P0, Card("Ghirapur Aether Grid"));
        s.Add(P0, new CardDefinition { Name = "Trinket", Types = CardType.Artifact });
        var tapped = s.Add(P0, new CardDefinition { Name = "Tapped Trinket", Types = CardType.Artifact });
        bool offered = false;
        bool first = true;
        s.Attacker.Act = (_, legal) =>
        {
            if (first) { first = false; s.Card(tapped).Tapped = true; return PassPriority.Instance; }
            offered |= legal.OfType<ActivateAbility>().Any(a => s.Card(a.Source).Name == "Ghirapur Aether Grid");
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    [Fact]
    public async Task WhirlerRogueMakesThoptersThatPayForItsAbility()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Island, 4);
        var raider = s.Add(P0, Creature("Raider", 3, 3));
        s.Add(P1, Creature("Wall", 0, 5));
        s.InHand(P0, Card("Whirler Rogue"));
        s.Attacker.Targets = (_, req) => req.LegalAt(0).Contains(Target.Of(raider)) ? new[] { Target.Of(raider) } : TestController.FirstAllowed(null!, req);
        Plays(s.Attacker, null, Cast(s, "Whirler Rogue"), Activate(s, "Whirler Rogue"));
        s.Attacker.Attack = (_, _, d) => new[] { new AttackDeclaration(raider, d[0]) };
        s.Defender.Block = (_, blockers, attackers) => attackers.Count > 0 && blockers.Count > 0 ? new[] { new BlockDeclaration(blockers[0], attackers[0]) } : Array.Empty<BlockDeclaration>();
        int life = Life(s, P1);
        await s.RunUntilTurn();
        var thopters = s.Game.State.Cards.Values.Where(c => c.Name == "Thopter" && c.Zone == Zone.Battlefield).ToList();
        Assert.Equal(2, thopters.Count);
        Assert.All(thopters, t => Assert.True(t.Is(CardType.Artifact) && t.IsCreature && t.Has(Keyword.Flying) && t.Colors.Count == 0 && t.Tapped));
        Assert.Equal(life - 3, Life(s, P1)); // the Raider couldn't be blocked
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscipleOfTheRingExilesAnInstantOrSorceryCard(bool hasOne)
    {
        var s = new Scenario();
        var disciple = s.Add(P0, Card("Disciple of the Ring"));
        s.Add(P0, GenericCards.Island);
        var spell = s.InHand(P0, hasOne ? GenericCards.EmberBolt with { Types = CardType.Sorcery } : Creature("Dead Bear", 2, 2));
        var corpse = s.InHand(P0, Creature("Corpse", 1, 1));
        s.Attacker.Modes = (_, req) => new[] { 1 }; // "This creature gets +1/+1 until end of turn"
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool offered = false;
        var steps = new Queue<int>();
        Plays(s.Attacker, () => { ToGraveyard(s, spell); ToGraveyard(s, corpse); }, legal =>
        {
            var a = legal.OfType<ActivateAbility>().FirstOrDefault(x => x.Source == disciple);
            offered |= a is not null;
            return a;
        });
        var power = new List<int>();
        s.Game.EventRaised += e => { if (e is AbilityResolved r && r.Source == disciple) power.Add(s.Card(disciple).Power); };
        await s.RunUntilTurn();
        Assert.Equal(hasOne, offered);
        if (hasOne)
        {
            Assert.Equal(Zone.Exile, s.Card(spell).Zone);
            Assert.Equal(Zone.Graveyard, s.Card(corpse).Zone);
            Assert.Equal(new[] { 4 }, power);
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task SkaabGoliathExilesTwoCreatureCardsFromTheGraveyard(int creatures, bool castable)
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Island, 6);
        var goliath = s.InHand(P0, Card("Skaab Goliath"));
        var dead = Enumerable.Range(0, creatures).Select(i => s.InHand(P0, Creature($"Dead {i}", 1, 1))).ToList();
        var bolt = s.InHand(P0, GenericCards.EmberBolt);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool offered = false;
        Plays(s.Attacker, () => { foreach (var d in dead) ToGraveyard(s, d); ToGraveyard(s, bolt); }, legal =>
        {
            var c = legal.OfType<CastSpell>().FirstOrDefault(x => x.Card == goliath);
            offered |= c is not null;
            return c;
        });
        await s.RunUntilTurn();
        Assert.Equal(castable, offered);
        if (castable)
        {
            Assert.Equal(Zone.Battlefield, s.Card(goliath).Zone);
            Assert.All(dead, d => Assert.Equal(Zone.Exile, s.Card(d).Zone));
            Assert.Equal(Zone.Graveyard, s.Card(bolt).Zone);
        }
    }

    // ------------------------------------------------------------------ filters

    [Fact]
    public async Task GiltLeafWinnowerDestroysOnlyANonElfWhosePowerAndToughnessDiffer()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Swamp, 5);
        s.InHand(P0, Card("Gilt-Leaf Winnower"));
        var square = s.Add(P1, Creature("Square", 2, 2));
        var elf = s.Add(P1, Creature("Elf", 3, 1) with { Subtypes = new[] { "Elf" } });
        var lanky = s.Add(P1, Creature("Lanky", 3, 1));
        TargetRequest? seen = null;
        s.Attacker.Targets = (_, req) => { seen ??= req; return TestController.FirstAllowed(null!, req); };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Gilt-Leaf Winnower"));
        await s.RunUntilTurn();
        Assert.Equal(new[] { Target.Of(lanky) }, seen!.LegalAt(0));
        Assert.Equal(Zone.Graveyard, s.Card(lanky).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(square).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(elf).Zone);
    }

    [Fact]
    public async Task KothophedDrawsForEachPermanentAnotherPlayerOwnsGoingToAGraveyard()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Swamp, 1);
        s.Add(P0, Card("Kothophed, Soul Hoarder"));
        s.Add(P0, Creature("Mine", 1, 1));
        var borrowed = s.Add(P1, Creature("Borrowed", 1, 1));
        s.Add(P1, Creature("Theirs", 2, 2));
        s.Add(P1, Creature("Their Token", 1, 1) with { IsToken = true });
        s.InHand(P0, Scripted("Small Purge", CardType.Sorcery, "{B}",
            """{ "spell": { "effects": [{ "destroy": { "each": { "types": ["creature"], "controller": "any", "maxPower": 3 } } }] } }"""));
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool playing = false;
        int draws = 0;
        s.Game.EventRaised += e => { if (playing && e is CardDrawn d && d.Player == P0) draws++; };
        Plays(s.Attacker, () => { playing = true; s.Card(borrowed).BaseController = P0; s.Card(borrowed).Controller = P0; }, Cast(s, "Small Purge"));
        int life = Life(s, P0);
        await s.RunUntilTurn();
        // Borrowed, Theirs and the token: three; its controller's own creature doesn't count.
        Assert.Equal(life - 3, Life(s, P0));
        Assert.Equal(3, draws);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SigilOfValorCountsTheOtherCreaturesWhenItsCreatureAttacksAlone(bool alone)
    {
        var s = new Scenario();
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        var hero = s.Add(P0, Creature("Hero", 2, 2));
        var friend = s.Add(P0, Creature("Friend", 1, 1));
        s.Add(P0, Creature("Other", 1, 1));
        s.Game.SetupPermanent(P0, Card("Sigil of Valor"), hero);
        s.Add(P1, Creature("Enemy", 5, 5)); // not counted: not controlled by the Sigil's controller
        s.Defender.Block = (_, _, _) => Array.Empty<BlockDeclaration>();
        s.Attacker.Attack = (_, _, d) => (alone ? new[] { hero } : new[] { hero, friend }).Select(a => new AttackDeclaration(a, d[0])).ToList();
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Equal(alone ? life - 4 : life - 3, Life(s, P1));
    }

    [Fact]
    public async Task DisplacementWaveReturnsNonlandPermanentsWithManaValueXOrLess()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Island, 4); // X = 2
        s.InHand(P0, Card("Displacement Wave"));
        var two = s.Add(P1, Creature("Two", 2, 2) with { ManaCost = ManaCost.Parse("{1}{G}") });
        var three = s.Add(P1, Creature("Three", 3, 3) with { ManaCost = ManaCost.Parse("{2}{G}") });
        var token = s.Add(P1, Creature("Token", 1, 1) with { IsToken = true, ManaCost = ManaCost.Zero });
        var mine = s.Add(P0, Creature("Mine", 1, 1));
        var forest = s.Add(P1, GenericCards.Forest);
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Displacement Wave"));
        await s.RunUntilTurn();
        bool Returned(CardId id) => s.Game.Log.Any(e => e is CardMoved { From: Zone.Battlefield, To: Zone.Hand } m && m.Card == id);
        Assert.True(Returned(two));
        Assert.True(Returned(mine)); // (then discarded to hand size in the cleanup step)
        Assert.NotEqual(Zone.Battlefield, s.Card(token).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(three).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(forest).Zone);
    }

    // ------------------------------------------------------------------ targeting

    [Fact]
    public async Task GaeasRevengeCantBeTargetedByNongreenSpellsOrAbilitiesOfAnyPlayer()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P0, GenericCards.Forest, 1);
        var revenge = s.Add(P0, Card("Gaea's Revenge"));
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, GenericCards.EmberBolt);
        s.InHand(P0, Scripted("Green Bolt", CardType.Instant, "{G}", """{ "spell": { "targets": ["creature"], "effects": [{ "damage": 1, "to": "target" }] } }""") with { ColorIdentity = new[] { "G" } });
        s.Add(P0, Scripted("Rod", CardType.Artifact, "{1}", """{ "abilities": [{ "cost": "{T}", "targets": ["creature"], "effects": [{ "damage": 1, "to": "target" }] }] }"""));
        var legal = new Dictionary<string, IReadOnlyList<Target>>();
        s.Attacker.Targets = (_, req) => { legal[s.Card(req.Source).Name] = req.LegalAt(0); return TestController.FirstAllowed(null!, req); };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Ember Bolt"), Cast(s, "Green Bolt"), Activate(s, "Rod"));
        await s.RunUntilTurn();
        Assert.True(s.Card(revenge).Definition.CantBeCountered);
        Assert.DoesNotContain(Target.Of(revenge), legal["Ember Bolt"]);
        Assert.Contains(Target.Of(bear), legal["Ember Bolt"]);
        Assert.DoesNotContain(Target.Of(revenge), legal["Rod"]); // colorless counts as nongreen
        Assert.Contains(Target.Of(revenge), legal["Green Bolt"]);
    }

    /// <summary>Player 1 answers player 0's first spell with <paramref name="response"/> from their hand.</summary>
    private static void Responds(Scenario s, string response)
    {
        bool done = false;
        s.Defender.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == response) is { } cast) { done = true; return cast; }
            return PassPriority.Instance;
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PsychicRebuttalCountersASpellThatTargetsYouAndWithSpellMasteryCopiesIt(bool mastery)
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Island, 2);
        var bolt = s.InHand(P0, GenericCards.EmberBolt);
        s.InHand(P1, Card("Psychic Rebuttal"));
        var old = Enumerable.Range(0, mastery ? 2 : 1).Select(i => s.InHand(P1, Instant($"Old {i}"))).ToList();
        s.Attacker.Targets = (_, req) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, () => { foreach (var o in old) ToGraveyard(s, o); }, Cast(s, "Ember Bolt"));
        Responds(s, "Psychic Rebuttal");
        s.Defender.Targets = (_, req) => req.LegalAt(0).Contains(Target.Of(P0)) ? new[] { Target.Of(P0) } : TestController.FirstAllowed(null!, req);
        int life0 = Life(s, P0), life1 = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellCountered c && c.Card == bolt);
        Assert.Equal(life1, Life(s, P1));
        Assert.Equal(mastery ? life0 - 3 : life0, Life(s, P0));
    }

    [Fact]
    public async Task PsychicRebuttalCantTargetASpellThatDoesntTargetYou()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Island, 2);
        var victim = s.Add(P1, Creature("Victim", 2, 2));
        s.InHand(P0, GenericCards.EmberBolt);
        s.InHand(P1, Card("Psychic Rebuttal"));
        s.Attacker.Targets = (_, req) => new[] { Target.Of(victim) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Ember Bolt"));
        bool offered = false;
        s.Defender.Act = (_, legal) =>
        {
            offered |= legal.OfType<CastSpell>().Any(c => s.Card(c.Card).Name == "Psychic Rebuttal");
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
        Assert.Equal(Zone.Graveyard, s.Card(victim).Zone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExquisiteFirecraftCantBeCounteredWithSpellMastery(bool mastery)
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 3);
        Lands(s, P1, GenericCards.Island, 2);
        var firecraft = s.InHand(P0, Card("Exquisite Firecraft"));
        s.InHand(P1, Card("Psychic Rebuttal"));
        var old = Enumerable.Range(0, mastery ? 2 : 1).Select(i => s.InHand(P0, Instant($"Old {i}"))).ToList();
        var theirOld = Enumerable.Range(0, 2).Select(i => s.InHand(P1, Instant($"Their Old {i}"))).ToList();
        s.Attacker.Targets = (_, req) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, () => { foreach (var o in old.Concat(theirOld)) ToGraveyard(s, o); }, Cast(s, "Exquisite Firecraft"));
        Responds(s, "Psychic Rebuttal");
        s.Defender.Targets = (_, req) => req.LegalAt(0).Contains(Target.Of(P0)) ? new[] { Target.Of(P0) } : TestController.FirstAllowed(null!, req);
        int life0 = Life(s, P0), life1 = Life(s, P1);
        await s.RunUntilTurn();
        Assert.False(s.Card(firecraft).Definition.CantBeCountered); // the importer doesn't make it unconditional
        if (mastery)
        {
            Assert.DoesNotContain(s.Game.Log, e => e is SpellCountered c && c.Card == firecraft);
            Assert.Equal(life1 - 4, Life(s, P1));
            Assert.Equal(life0, Life(s, P0)); // nothing was countered, so nothing is copied
        }
        else
        {
            Assert.Contains(s.Game.Log, e => e is SpellCountered c && c.Card == firecraft);
            Assert.Equal(life1, Life(s, P1));
            Assert.Equal(life0 - 4, Life(s, P0)); // the opponent's spell mastery copies it back
        }
    }

    [Fact]
    public async Task MizziumMeddlerTakesTheBoltMeantForAnotherCreature()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Island, 3);
        var victim = s.Add(P1, Creature("Victim", 2, 2));
        s.InHand(P0, GenericCards.EmberBolt);
        var meddler = s.InHand(P1, Card("Mizzium Meddler"));
        s.Attacker.Targets = (_, req) => new[] { Target.Of(victim) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Ember Bolt"));
        Responds(s, "Mizzium Meddler");
        OptionRequest? asked = null;
        s.Defender.Option = (_, req) => { asked = req; return 0; };
        await s.RunUntilTurn();
        Assert.Equal(2, asked!.Options.Count); // change the Victim, or don't
        Assert.Equal(Zone.Battlefield, s.Card(victim).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(meddler).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 3 } d && d.TargetCard == meddler);
    }

    [Fact]
    public async Task MizziumMeddlerChangesOnlyATargetItCouldLegallyBe()
    {
        // "Deals 1 damage to target creature and 1 damage to target player": only the creature target can become the Meddler.
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Island, 3);
        var victim = s.Add(P1, Creature("Victim", 1, 1));
        s.InHand(P0, Scripted("Twin Shot", CardType.Instant, "{R}",
            """{ "spell": { "targets": ["creature", "player"], "effects": [{ "damage": 1, "to": "target" }, { "damage": 1, "to": "target2" }] } }"""));
        var meddler = s.InHand(P1, Card("Mizzium Meddler"));
        s.Attacker.Targets = (_, req) => new[] { Target.Of(victim), Target.Of(P1) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Twin Shot"));
        Responds(s, "Mizzium Meddler");
        OptionRequest? asked = null;
        s.Defender.Option = (_, req) => { asked = req; return 0; };
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.Equal(2, asked!.Options.Count);
        Assert.Equal(Zone.Battlefield, s.Card(victim).Zone);
        Assert.Contains(s.Game.Log, e => e is DamageDealt { Amount: 1 } d && d.TargetCard == meddler);
        Assert.Equal(life - 1, Life(s, P1));
    }

    [Fact]
    public async Task MizziumMeddlerCantTakeAPlayerTarget()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        Lands(s, P1, GenericCards.Island, 3);
        s.InHand(P0, Scripted("Mind Jab", CardType.Instant, "{R}", """{ "spell": { "targets": ["player"], "effects": [{ "loseLife": 3, "who": "target" }] } }"""));
        s.InHand(P1, Card("Mizzium Meddler"));
        s.Attacker.Targets = (_, req) => new[] { Target.Of(P1) };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        Plays(s.Attacker, null, Cast(s, "Mind Jab"));
        Responds(s, "Mizzium Meddler");
        bool asked = false;
        s.Defender.Option = (_, _) => { asked = true; return 0; };
        int life = Life(s, P1);
        await s.RunUntilTurn();
        Assert.False(asked);
        Assert.Equal(life - 3, Life(s, P1));
    }
}
