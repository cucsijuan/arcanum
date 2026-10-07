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

public partial class ScriptedVocabularyTests
{
    // ------------------------------------------------------------------ graveyards, exile, entering

    [Fact]
    public async Task ASpellOfAKindMayBeCastFromTheGraveyardForTheTurnAndGoesToTheGraveyardAfterwards()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.Add(P0, Scripted("Grave Whistle", CardType.Artifact, "{2}", """{ "abilities": [{ "cost": "{1}", "effects": [{ "mayCastFromGraveyard": { "subtype": "Ghoul" } }] }] }"""));
        var ghoul = s.InHand(P0, Creature("Crypt Ghoul", "{}", 2, 2, "{R}", "Ghoul"));
        var other = s.InHand(P0, Creature("Tomb Mouse", "{}", 1, 1, "{R}", "Rat"));
        var offered = new List<CardId>();
        Acting(s, () => { ToGraveyard(s, ghoul); ToGraveyard(s, other); });
        var act = s.Attacker.Act;
        s.Attacker.Act = (v, legal) => { offered.AddRange(legal.OfType<CastSpell>().Select(c => c.Card)); return act(v, legal); };
        await s.RunUntilTurn();
        Assert.Contains(ghoul, offered);
        Assert.DoesNotContain(other, offered);
        Assert.Equal(Zone.Battlefield, s.Card(ghoul).Zone);
    }

    [Fact]
    public async Task ANoTurnLimitedPermissionToPlayLandsFromTheGraveyard()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Dusty Urn", CardType.Artifact, "{3}", """{ "replaces": ["LandsFromGraveyard"] }"""));
        var land = s.InHand(P0, GenericCards.Forest);
        var creature = s.InHand(P0, Creature("Tomb Mouse", "{}", 1, 1, "{R}"));
        bool landOffered = false, creatureOffered = false;
        Acting(s, () => { ToGraveyard(s, land); ToGraveyard(s, creature); }, activations: 0);
        var act = s.Attacker.Act;
        s.Attacker.Act = (v, legal) =>
        {
            landOffered |= legal.OfType<PlayLand>().Any(p => p.Card == land);
            creatureOffered |= legal.OfType<CastSpell>().Any(c => c.Card == creature);
            return legal.OfType<PlayLand>().FirstOrDefault(p => p.Card == land) is { } play ? play : act(v, legal);
        };
        await s.RunUntilTurn();
        Assert.True(landOffered);
        Assert.False(creatureOffered);
        Assert.Equal(Zone.Battlefield, s.Card(land).Zone);
    }

    [Fact]
    public async Task SpellsFromAmongExiledCardsCanBeCastButLandsCantBePlayed()
    {
        var s = new Scenario();
        s.Lands(P0, 6);
        var land = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var creature = s.Game.SetupInLibrary(P0, Creature("Tomb Mouse", "{}", 1, 1, "{R}"));
        s.InHand(P0, Scripted("Plunder Run", CardType.Sorcery, "{R}", """{ "spell": { "effects": [{ "exileTopPlayable": 2, "chooseOne": false, "castOnly": true }] } }"""));
        bool restacked = false, landOffered = false, creatureOffered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, land, creature); }
            if (s.Card(land).Zone == Zone.Exile)
            {
                landOffered |= legal.OfType<PlayLand>().Any(p => p.Card == land);
                creatureOffered |= legal.OfType<CastSpell>().Any(c => c.Card == creature);
            }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card != creature) is { } cast ? cast : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(land).Zone);
        Assert.False(landOffered);
        Assert.True(creatureOffered);
    }

    [Fact]
    public async Task APermanentReturnedWithAnExileClauseIsExiledInsteadOfAnyOtherZone()
    {
        var s = new Scenario();
        s.Lands(P0, 4);
        var fallen = s.InHand(P0, Creature("Fallen Knight", "{}", 2, 2, "{2}"));
        s.InHand(P0, Scripted("Dark Rite", CardType.Sorcery, "{R}",
            """{ "spell": { "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"] } }], "effects": [{ "reanimate": "target", "counters": 1, "counterKind": "corpse", "exileIfLeaves": true }] } }"""));
        s.InHand(P0, Scripted("Sudden Retreat", CardType.Instant, "{R}", """{ "spell": { "targets": ["creature"], "effects": [{ "bounce": "target" }] } }"""));
        int casts = 0;
        bool placed = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (v, legal) =>
        {
            if (!placed) { placed = true; ToGraveyard(s, fallen); return PassPriority.Instance; }
            var spells = legal.OfType<CastSpell>().Where(c => c.Card != fallen).ToList();
            if (casts < 2 && spells.Count > 0)
            {
                // The reanimation first, the bounce once the creature is back.
                var next = spells.FirstOrDefault(c => s.Card(c.Card).Name == (casts == 0 ? "Dark Rite" : "Sudden Retreat"));
                if (next is not null) { casts++; return next; }
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(fallen).Zone);
    }

    [Fact]
    public async Task ThePaidXOfAReflexiveAbilityPicksTheCardWithThatManaValue()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var small = s.InHand(P0, Creature("Squire", "{}", 1, 1, "{1}"));
        var big = s.InHand(P0, Creature("Champion", "{}", 3, 3, "{3}"));
        s.Add(P0, Creature("Grave Caller", """
            { "abilities": [{ "trigger": "attacks", "effects": [{ "mayPayX": "Pay X?", "effects": [{ "whenYouDo": {
                "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["creature"], "manaValueIsTriggerAmount": true } }],
                "amount": "X", "effects": [{ "reanimate": "target", "counters": 1, "counterKind": "corpse", "exileIfLeaves": true }] } }] }] }] }
            """, 2, 2));
        IReadOnlyList<Abilities.Target>? legal = null;
        s.Attacker.Number = (_, request) => 3;
        s.Attacker.Targets = (_, request) => { legal = request.LegalAt(0).ToList(); return new[] { legal.First() }; };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        bool first = true;
        s.Attacker.Act = (_, _) =>
        {
            if (first) { first = false; ToGraveyard(s, small); ToGraveyard(s, big); }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.NotNull(legal);
        Assert.Equal(new[] { big }, legal!.Select(t => t.Card!.Value));
        Assert.Equal(Zone.Battlefield, s.Card(big).Zone);
        Assert.Equal(1, s.Card(big).CounterCount(CounterKind.Corpse));
        Assert.Equal(Zone.Graveyard, s.Card(small).Zone);
    }

    // ------------------------------------------------------------------ combat

    [Fact]
    public async Task ADefenderGrantedPermissionAttacksAsThoughItHadNone()
    {
        var s = new Scenario();
        var wall = s.Add(P0, Scripted("Stout Wall", CardType.Creature, "{1}", "{}", 0, 5, null, null, "Defender"));
        var other = s.Add(P0, Scripted("Stout Wall B", CardType.Creature, "{1}", "{}", 0, 5, null, null, "Defender"));
        s.Add(P0, Scripted("Wall Marshal", CardType.Creature, "{2}",
            """{ "abilities": [{ "static": { "affects": "creatures:you", "filter": { "types": ["creature"], "keyword": "Defender", "other": true }, "keywords": ["Can attack as though it didn't have defender"] } }] }""", 1, 1));
        IReadOnlyList<CardId>? possible = null;
        s.Attacker.Attack = (_, attackers, _) => { possible = attackers; return Array.Empty<AttackDeclaration>(); };
        await s.RunUntilTurn();
        Assert.NotNull(possible);
        Assert.Contains(wall, possible!);
        Assert.Contains(other, possible!);
    }

    [Fact]
    public void ACreatureThatCanBlockAnAdditionalCreatureBlocksTwoAttackersAndNotThree()
    {
        var blocker = new CardId(1);
        var a = new[] { new CardId(10), new CardId(11), new CardId(12) };
        var request = new BlockRequest(a, new[] { blocker },
            new Dictionary<CardId, IReadOnlyList<CardId>> { [blocker] = a }, new Dictionary<CardId, int>())
        {
            CanBlockAdditional = new[] { blocker },
        };
        Assert.True(request.CanBlockSeveral(blocker));
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(blocker, a[0]) }, out _));
        Assert.True(request.IsLegal(new[] { new BlockDeclaration(blocker, a[0]), new BlockDeclaration(blocker, a[1]) }, out _));
        Assert.False(request.IsLegal(a.Select(x => new BlockDeclaration(blocker, x)).ToList(), out var reason));
        Assert.Contains("only one attacker", reason);
        // Repairing a wanted declaration keeps two of the three blocks.
        Assert.Equal(2, request.Complete(a.Select(x => new BlockDeclaration(blocker, x)).ToList()).Count);
    }

    [Fact]
    public async Task AnEffectCanForbidBlockersMatchingAFilterForTheTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var raider = s.Add(P0, Creature("Sly Raider", """{ "abilities": [{ "cost": "{1}", "effects": [{ "pump": [0, 0], "what": "self", "cantBeBlockedBy": { "maxPower": 2 } }] }] }""", 3, 3));
        var small = s.Add(P1, Creature("Little Guard", "{}", 2, 2));
        var big = s.Add(P1, Creature("Big Guard", "{}", 4, 4));
        Acting(s);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest;
        Assert.NotNull(request);
        Assert.Equal(new[] { raider }, request!.CanBlock[big]);
        Assert.False(request.CanBlock.ContainsKey(small));
    }

    [Fact]
    public async Task ExceptByTheirKindIsAFilterOfEverythingElse()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var raider = s.Add(P0, Creature("Sly Raider", """{ "abilities": [{ "cost": "{1}", "effects": [{ "pump": [0, 0], "what": "self", "cantBeBlockedBy": { "notSubtype": "Ghost" } }] }] }""", 3, 3));
        var ghost = s.Add(P1, Creature("Pale Ghost", "{}", 1, 1, "{1}", "Ghost"));
        var brute = s.Add(P1, Creature("Big Guard", "{}", 4, 4));
        Acting(s);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        var request = s.Defender.LastBlockRequest;
        Assert.NotNull(request);
        Assert.Contains(raider, request!.CanBlock[ghost]);
        Assert.False(request.CanBlock.ContainsKey(brute));
    }

    [Fact]
    public async Task ACreatureThatAttackedOrBlockedIsShuffledIntoItsOwnersLibraryAtEndStep()
    {
        var s = new Scenario();
        const string Script = """{ "abilities": [{ "trigger": "eachEndStep", "if": "attackedOrBlockedThisTurn", "effects": [{ "shuffleIntoLibrary": "self" }] }] }""";
        var attacker = s.Add(P0, Creature("Fickle Drake", Script, 1, 1));
        var idle = s.Add(P0, Creature("Idle Drake", Script, 1, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == attacker).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(attacker).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(idle).Zone);
    }

    [Fact]
    public async Task ABlockerCountsAsHavingBlockedForTheSameCondition()
    {
        var s = new Scenario();
        const string Script = """{ "abilities": [{ "trigger": "eachEndStep", "if": "attackedOrBlockedThisTurn", "effects": [{ "shuffleIntoLibrary": "self" }] }] }""";
        var blocker = s.Add(P1, Creature("Fickle Drake", Script, 1, 5));
        s.Add(P0, Creature("Charging Raider", "{}", 2, 2));
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Library, s.Card(blocker).Zone);
    }

    [Fact]
    public async Task AnEmblemWithAnEndStepTriggerMakesItsTokensEveryTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.InHand(P0, Scripted("Rallying Oath", CardType.Sorcery, "{R}",
            """{ "spell": { "effects": [{ "emblem": "Oath emblem", "abilities": [{ "trigger": "endStep", "effects": [{ "tokens": 3, "token": { "name": "Cat", "types": "Creature — Cat", "power": 1, "toughness": 1, "keywords": ["Lifelink"], "colors": ["W"] } }] }] }] } }"""));
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(3, s.Game.State.PermanentsControlledBy(P0).Count(c => c.Name == "Cat"));
    }

    [Fact]
    public async Task ACardCanHaveLandsPlayedFromTheGraveyardCountedAsALandPlay()
    {
        var s = new Scenario();
        s.Add(P0, Scripted("Dusty Urn", CardType.Artifact, "{3}", """{ "replaces": ["LandsFromGraveyard"] }"""));
        var first = s.InHand(P0, GenericCards.Forest);
        var second = s.InHand(P0, GenericCards.Forest);
        var offered = new List<CardId>();
        bool placed = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!placed) { placed = true; ToGraveyard(s, first); ToGraveyard(s, second); }
            offered.AddRange(legal.OfType<PlayLand>().Select(p => p.Card).Where(c => c == first || c == second));
            return legal.OfType<PlayLand>().FirstOrDefault(p => p.Card == first || p.Card == second) is { } play ? play : PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        // One land play a turn: after the first the second is no longer offered.
        Assert.Equal(1, new[] { first, second }.Count(c => s.Card(c).Zone == Zone.Battlefield));
    }
}
