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
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Magic Origins replacement and prevention effects, played with their scripts: prevention shields on one object, damage
/// "plus 1", "prevent 1" from creatures, life gain doubled or turned into life loss (ordered by rule 616.1), exile instead of
/// dying, tokens exiled instead of entering, Kytheon's end-of-combat transformation and Gideon's abilities.
/// </summary>
public class OriginsReplacementTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", string[]? keywords = null, string[]? colors = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"ori-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords ?? Array.Empty<string>(),
            ["colors"] = colors ?? Array.Empty<string>(),
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private delegate PlayerAction? Step(IReadOnlyList<PlayerAction> legal);

    private static void Script(Scenario s, params Step[] steps)
    {
        var q = new Queue<Step>(steps);
        s.Attacker.Act = (_, legal) =>
        {
            if (q.Count > 0 && s.Game.State.Stack.Count == 0 && q.Peek()(legal) is { } a) { q.Dequeue(); return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Step Cast(Scenario s, string name) => legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);

    private static Step Activate(Scenario s, CardId source, string textStart) => legal => legal.OfType<ActivateAbility>()
        .FirstOrDefault(a => a.Source == source && s.Card(a.Source).Abilities[a.Index].Text?.StartsWith(textStart) == true);

    private static void Aim(Scenario s, Func<Target, bool> pick) => s.Attacker.Targets = (_, req) =>
    {
        var chosen = new List<Target>();
        for (int i = 0; i < req.Specs.Count; i++)
            chosen.Add(req.LegalAt(i).FirstOrDefault(t => req.IsAllowed(i, t, chosen) && !t.IsNone && pick(t)));
        return chosen;
    };

    private static Func<Target, bool> Named(Scenario s, string name) => t => t.Card is { } c && s.Card(c).Name == name;

    private static int Life(Scenario s, PlayerId p) => s.Game.State.GetPlayer(p).Life;

    private static void Lands(Scenario s, PlayerId p, CardDefinition land, int n) { for (int i = 0; i < n; i++) s.Add(p, land); }

    // ------------------------------------------------------------------ the cards (scripts as in the module)

    private const string EnshroudingMistScript = """
        { "spell": { "targets": ["creature"], "effects": [
            { "pump": [1, 1], "what": "target" },
            { "preventDamage": true, "to": "target" },
            { "if": { "target": "target", "is": { "renowned": true } }, "then": [{ "untap": "target" }] } ] } }
        """;

    private static CardDefinition EnshroudingMist() => Make("Enshrouding Mist", "Instant", EnshroudingMistScript, "{W}");

    private static CardDefinition Shock(string name = "Shock", int amount = 3, string cost = "{R}", string[]? colors = null) =>
        Make(name, "Instant", $$"""{ "spell": { "targets": ["any"], "effects": [{ "damage": {{amount}}, "to": "target" }] } }""", cost, colors: colors ?? new[] { "R" });

    private static CardDefinition Sweep(int amount) =>
        Make("Sweep", "Sorcery", $$"""{ "spell": { "effects": [{ "damage": {{amount}}, "to": { "each": { "types": ["creature"], "controller": "any" } } }] } }""", "", colors: new[] { "R" });

    private static CardDefinition Bear(string name = "Bear", string pt = "2/2", string[]? keywords = null, string[]? colors = null) =>
        Make(name, "Creature — Bear", "{}", "{1}", pt, keywords: keywords, colors: colors);

    private static CardDefinition EmbermawHellion() => Make("Embermaw Hellion", "Creature — Hellion",
        """{ "damageBonus": { "sources": { "colors": ["R"], "other": true }, "amount": 1 } }""", "{3}{R}{R}", "4/5", "Trample", new[] { "Trample" }, new[] { "R" });

    private static CardDefinition OrbsOfWarding() => Make("Orbs of Warding", "Artifact",
        """{ "givesHexproof": true, "preventDamageToYou": { "sources": { "types": ["creature"] }, "amount": 1 } }""", "{5}");

    private static CardDefinition TaintedRemedy() => Make("Tainted Remedy", "Enchantment", """{ "replaces": ["OpponentsLifeGainBecomesLoss"] }""", "{2}{B}");

    private static CardDefinition Archive() => Make("Alhammarret's Archive", "Legendary Artifact", """{ "replaces": ["DoubleLifeGain", "DrawTwoExceptFirstInDrawStep"] }""", "{5}");

    private static CardDefinition Heal(int amount = 3) => Make("Heal", "Instant", $$"""{ "spell": { "effects": [{ "gainLife": {{amount}} }] } }""");

    private static CardDefinition PossessedSkaab() => Make("Possessed Skaab", "Creature — Zombie", """
        { "replaces": ["ExileInsteadOfDying"], "abilities": [{ "trigger": "enters",
            "targets": [{ "kind": "graveyardCard", "controller": "you", "filter": { "types": ["instant", "sorcery", "creature"] } }],
            "effects": [{ "bounce": "target" }] }] }
        """, "", "3/2");

    private static CardDefinition HallowedMoonlight(bool nontokenOnly = false) => Make(nontokenOnly ? "Mistcall" : "Hallowed Moonlight", "Instant",
        nontokenOnly
            ? """{ "spell": { "effects": [{ "exileUncastEntering": { "types": ["creature"], "token": false } }] } }"""
            : """{ "spell": { "effects": [{ "exileUncastEntering": { "types": ["creature"] } }, { "draw": 1 }] } }""");

    private static CardDefinition Soldiers() => Make("Raise the Alarm", "Instant",
        """{ "spell": { "effects": [{ "tokens": 2, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "colors": ["W"] } }] } }""");

    private const string KytheonScript = """
        {
          "abilities": [
            { "trigger": "endOfCombat", "if": { "attackedThisCombatWithOthers": 2 }, "effects": [{ "blink": "self", "transformed": true }],
              "text": "At end of combat, if Kytheon and at least two other creatures attacked this combat, exile Kytheon, then return him to the battlefield transformed under his owner's control." },
            { "cost": "{2}{W}", "effects": [{ "pump": [0, 0], "what": "self", "keywords": ["Indestructible"] }], "text": "{2}{W}: Kytheon gains indestructible until end of turn." }
          ],
          "back": { "abilities": [
            { "cost": "+2", "targets": [{ "kind": "creature", "controller": "opponent", "optional": true }], "effects": [{ "attacksSourceNextTurn": "target" }],
              "text": "+2: Up to one target creature an opponent controls attacks Gideon during its controller's next turn if able." },
            { "cost": "+1", "targets": ["creature"], "effects": [{ "pump": [0, 0], "what": "target", "keywords": ["Indestructible"], "untilYourNextTurn": true }, { "untap": "target" }],
              "text": "+1: Until your next turn, target creature gains indestructible. Untap that creature." },
            { "cost": "0", "effects": [
                { "become": "self", "power": 4, "toughness": 4, "addTypes": ["creature"], "addSubtypes": ["Human", "Soldier"], "keywords": ["Indestructible"] },
                { "preventDamage": true, "to": "self" } ],
              "text": "0: Until end of turn, Gideon becomes a 4/4 Human Soldier creature with indestructible that's still a planeswalker. Prevent all damage that would be dealt to him this turn." }
          ] }
        }
        """;

    private static CardDefinition Kytheon()
    {
        const string json = """
            {"oracle_id":"ori-kytheon","name":"Kytheon, Hero of Akros // Gideon, Battle-Forged","layout":"transform","mana_cost":"","type_line":"Legendary Creature — Human Soldier // Legendary Planeswalker — Gideon","oracle_text":"","colors":["W"],"keywords":["Transform"],"games":["paper"],
             "card_faces":[{"name":"Kytheon, Hero of Akros","mana_cost":"{W}","type_line":"Legendary Creature — Human Soldier","oracle_text":"At end of combat, if Kytheon and at least two other creatures attacked this combat, exile Kytheon, then return him to the battlefield transformed under his owner's control.\n{2}{W}: Kytheon gains indestructible until end of turn.","power":"2","toughness":"1","colors":["W"]},
                           {"name":"Gideon, Battle-Forged","mana_cost":"","type_line":"Legendary Planeswalker — Gideon","oracle_text":"+2: Up to one target creature an opponent controls attacks Gideon during its controller's next turn if able.\n+1: Until your next turn, target creature gains indestructible. Untap that creature.\n0: Until end of turn, Gideon becomes a 4/4 Human Soldier creature with indestructible that's still a planeswalker. Prevent all damage that would be dealt to him this turn.","loyalty":"3","colors":["W"]}]}
            """;
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json.ReplaceLineEndings(" ")))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(KytheonScript)).Definition;
    }

    /// <summary>Gideon, Battle-Forged on the battlefield (Kytheon turned over), under player 0's control since the turn began.</summary>
    private static CardId Gideon(Scenario s)
    {
        var id = s.Add(P0, Kytheon().BackFace!);
        var card = s.Card(id);
        card.Counters[CounterKind.Loyalty] = 3;
        card.ControlledSinceTurnStart = true;
        return id;
    }

    // ------------------------------------------------------------------ Enshrouding Mist: a shield on one object

    [Fact]
    public async Task EnshroudingMistPreventsDamageToThatCreatureOnlyAndUntapsItIfRenowned()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Plains, 1);
        Lands(s, P0, GenericCards.Mountain, 1);
        var hero = s.Add(P0, Bear("Hero"));
        s.Card(hero).Renowned = true;
        s.Card(hero).Tapped = true;
        var other = s.Add(P0, Bear("Other"));
        s.InHand(P0, EnshroudingMist());
        s.InHand(P0, Shock());
        s.InHand(P0, Sweep(2));
        Script(s, Cast(s, "Enshrouding Mist"), Cast(s, "Shock"), Cast(s, "Sweep"));
        Aim(s, Named(s, "Hero"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(hero).Zone);
        Assert.Empty(s.Game.Log.OfType<DamageDealt>().Where(d => d.TargetCard == hero));
        Assert.False(s.Card(hero).Tapped);
        Assert.Equal(Zone.Graveyard, s.Card(other).Zone); // the shield is only on the target
    }

    [Fact]
    public async Task EnshroudingMistLeavesANonrenownedCreatureTappedAndDoesntFollowItToANewObject()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Plains, 1);
        Lands(s, P0, GenericCards.Mountain, 1);
        var hero = s.Add(P0, Bear("Hero"));
        s.Card(hero).Tapped = true;
        s.InHand(P0, EnshroudingMist());
        s.InHand(P0, Make("Flicker", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "blink": "target" }] } }"""));
        s.InHand(P0, Shock());
        Script(s, Cast(s, "Enshrouding Mist"), Cast(s, "Flicker"), Cast(s, "Shock"));
        Aim(s, Named(s, "Hero"));
        await s.RunUntilTurn();
        // After the flicker it's a new object: the shield doesn't apply to it, so Shock kills it.
        Assert.Equal(Zone.Graveyard, s.Card(hero).Zone);
    }

    // ------------------------------------------------------------------ Embermaw Hellion and Orbs of Warding

    [Fact]
    public async Task EmbermawHellionAddsOneToOtherRedSourcesOnly()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 2);
        s.Add(P0, EmbermawHellion());
        s.InHand(P0, Shock());
        s.InHand(P0, Shock("Blue Bolt", 3, "{1}", new[] { "U" }));
        Script(s, Cast(s, "Shock"), Cast(s, "Blue Bolt"));
        Aim(s, t => t.Player == P1);
        await s.RunUntilTurn();
        Assert.Equal(20 - 4 - 3, Life(s, P1));
    }

    [Fact]
    public async Task EmbermawHellionsOwnCombatDamageIsntIncreasedButTwoHellionsEachAddOne()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        var first = s.Add(P0, EmbermawHellion());
        s.Add(P0, EmbermawHellion());
        s.Card(first).ControlledSinceTurnStart = true;
        s.InHand(P0, Shock());
        Script(s, Cast(s, "Shock"));
        Aim(s, t => t.Player == P1);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == first).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        // Shock 3 + 1 + 1; the attacking Hellion 4 + 1 (only the other Hellion adds to it).
        Assert.Equal(20 - 5 - 5, Life(s, P1));
    }

    [Theory]
    [InlineData(0, 8)] // plus 1 first, then doubled: (3 + 1) × 2
    [InlineData(1, 7)] // doubled first, then plus 1: 3 × 2 + 1
    public async Task TheDamagedPlayerOrdersEmbermawWithADoubler(int choice, int expected)
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        s.Add(P0, EmbermawHellion());
        s.Add(P0, Make("Fire Servant", "Enchantment", """{ "replaces": ["DoubleDamageToOpponents"] }"""));
        s.InHand(P0, Shock());
        Script(s, Cast(s, "Shock"));
        Aim(s, t => t.Player == P1);
        s.Defender.Option = (_, request) => request.Options[0].StartsWith("That much plus") ? choice : 1 - choice;
        await s.RunUntilTurn();
        Assert.Equal(20 - expected, Life(s, P1));
    }

    [Fact]
    public async Task OrbsOfWardingPreventsOneFromEachCreatureButNotFromOtherSources()
    {
        var s = new Scenario();
        s.Add(P1, OrbsOfWarding());
        var a = s.Add(P0, Bear("A"));
        var b = s.Add(P0, Bear("B", "3/3"));
        s.Card(a).ControlledSinceTurnStart = true;
        s.Card(b).ControlledSinceTurnStart = true;
        s.InHand(P0, Make("Lava Wave", "Sorcery", """{ "spell": { "effects": [{ "damage": 3, "to": "opponents" }] } }""", colors: new[] { "R" }));
        Script(s, Cast(s, "Lava Wave"));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(20 - 3 - 1 - 2, Life(s, P1));
        Assert.True(s.Game.State.PermanentsControlledBy(P1).Single(c => c.Name == "Orbs of Warding").Definition.GivesControllerHexproof);
    }

    // ------------------------------------------------------------------ life gain: Alhammarret's Archive and Tainted Remedy

    [Fact]
    public async Task TaintedRemedyTurnsAnOpponentsLifeGainAndLifelinkIntoLifeLoss()
    {
        var s = new Scenario();
        s.Add(P1, TaintedRemedy());
        var linker = s.Add(P0, Bear("Linker", "2/2", new[] { "Lifelink" }));
        s.Card(linker).ControlledSinceTurnStart = true;
        s.InHand(P0, Heal(3));
        Script(s, Cast(s, "Heal"));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(20 - 3 - 2, Life(s, P0));
        Assert.Equal(18, Life(s, P1));
    }

    [Fact]
    public async Task TaintedRemedyDoesntAffectItsControllerNorAPlayerWhoCantGainLife()
    {
        var s = new Scenario();
        s.Add(P0, TaintedRemedy());
        s.Add(P1, TaintedRemedy());
        s.Add(P1, Make("Sulfuric Haze", "Enchantment", """{ "playersCantGainLife": true }"""));
        s.InHand(P0, Heal(3));
        Script(s, Cast(s, "Heal"));
        await s.RunUntilTurn();
        Assert.Equal(20, Life(s, P0)); // can't gain life: nothing to replace
    }

    [Fact]
    public async Task AlhammarretsArchiveDoublesLifeGain()
    {
        var s = new Scenario();
        s.Add(P0, Archive());
        s.Add(P0, Make("Archive Copy", "Artifact", """{ "replaces": ["DoubleLifeGain"] }""")); // not legendary
        s.InHand(P0, Heal(3));
        Script(s, Cast(s, "Heal"));
        await s.RunUntilTurn();
        Assert.Equal(20 + 12, Life(s, P0)); // each applies once
    }

    [Theory]
    [InlineData(0, 20 - 6)] // doubled, then turned into loss
    [InlineData(1, 20 - 3)] // turned into loss first: the Archive no longer applies
    public async Task ThePlayerGainingLifeOrdersArchiveAndRemedy(int choice, int expected)
    {
        var s = new Scenario();
        s.Add(P0, Archive());
        s.Add(P1, TaintedRemedy());
        s.InHand(P0, Heal(3));
        Script(s, Cast(s, "Heal"));
        s.Attacker.Option = (_, request) => request.Options[0].StartsWith("Gain twice") ? choice : 1 - choice;
        await s.RunUntilTurn();
        Assert.Equal(expected, Life(s, P0));
    }

    // ------------------------------------------------------------------ Possessed Skaab

    [Fact]
    public async Task PossessedSkaabReturnsACardAndIsExiledInsteadOfDying()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        s.InHand(P0, Make("Ponder", "Sorcery", """{ "spell": { "effects": [{ "scry": 1 }] } }"""));
        var skaab = s.InHand(P0, PossessedSkaab());
        s.InHand(P0, Shock());
        Script(s, Cast(s, "Ponder"), Cast(s, "Possessed Skaab"), Cast(s, "Shock"));
        Aim(s, t => t.Card is { } c && (s.Card(c).Name is "Ponder" or "Possessed Skaab"));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log.OfType<CardMoved>(), m => s.Card(m.Card).Name == "Ponder" && m.From == Zone.Graveyard && m.To == Zone.Hand);
        Assert.Equal(Zone.Exile, s.Card(skaab).Zone);
    }

    // ------------------------------------------------------------------ Hallowed Moonlight

    [Fact]
    public async Task HallowedMoonlightExilesCreatureTokensToo()
    {
        var s = new Scenario();
        s.InHand(P0, HallowedMoonlight());
        s.InHand(P0, Soldiers());
        Script(s, Cast(s, "Hallowed Moonlight"), Cast(s, "Raise the Alarm"));
        await s.RunUntilTurn();
        Assert.DoesNotContain(s.Game.State.Battlefield, id => s.Card(id).Name == "Soldier");
        Assert.Empty(s.Game.Log.OfType<TokenCreated>());
    }

    [Fact]
    public async Task HallowedMoonlightLetsCastCreaturesEnterButExilesOthersAndCopiesOfCreatureSpells()
    {
        var s = new Scenario();
        s.InHand(P0, HallowedMoonlight());
        var cast = s.InHand(P0, Bear("Cast Bear") with { ManaCost = Mana.ManaCost.Zero });
        var copied = s.InHand(P0, Bear("Copied Bear") with { ManaCost = Mana.ManaCost.Zero });
        s.InHand(P0, Make("Twincast", "Instant", """{ "spell": { "targets": ["spell"], "effects": [{ "copySpell": "target" }] } }"""));
        var buried = s.InHand(P0, Bear("Buried Bear") with { ManaCost = ManaCostOf("{9}") });
        s.InHand(P0, Make("Exhume", "Sorcery", """{ "spell": { "targets": ["graveyardCard:you"], "effects": [{ "reanimate": "target" }] } }"""));
        Script(s); // no attacks
        // Copied Bear on the stack, then Twincast on it (both resolve with Moonlight in effect); then Exhume a discarded Bear.
        int phase = 0;
        s.Attacker.Act = (_, legal) =>
        {
            var stack = s.Game.State.Stack;
            PlayerAction? next = phase switch
            {
                0 when stack.Count == 0 => Cast(s, "Hallowed Moonlight")(legal),
                1 when stack.Count == 0 => Cast(s, "Cast Bear")(legal),
                2 when stack.Count == 0 => Cast(s, "Copied Bear")(legal),
                3 when stack.Count == 1 => Cast(s, "Twincast")(legal),
                4 when stack.Count == 0 => Cast(s, "Exhume")(legal),
                _ => null,
            };
            if (next is null) return PassPriority.Instance;
            phase++;
            return next;
        };
        s.Attacker.Targets = (_, req) => new[] { req.LegalAt(0).First(t => !t.IsNone && (t.Card is not { } c || s.Card(c).Name is "Copied Bear" or "Buried Bear")) };
        bool moved = false;
        s.Game.EventRaised += e =>
        {
            if (moved || e is not StepBegan) return;
            moved = true;
            var player = s.Game.State.GetPlayer(P0);
            player.Hand.Remove(buried);
            player.Graveyard.Add(buried);
            s.Card(buried).Zone = Zone.Graveyard;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(cast).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(copied).Zone);
        Assert.Single(s.Game.Log.OfType<SpellCopied>());
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name is "Cast Bear" or "Copied Bear")); // the copy was exiled
        Assert.Equal(Zone.Exile, s.Card(buried).Zone);
    }

    private static Mana.ManaCost ManaCostOf(string cost) => Mana.ManaCost.Parse(cost);

    [Fact]
    public async Task ANontokenWordingStillLetsTokensEnter()
    {
        var s = new Scenario();
        s.InHand(P0, HallowedMoonlight(nontokenOnly: true));
        s.InHand(P0, Soldiers());
        Script(s, Cast(s, "Mistcall"), Cast(s, "Raise the Alarm"));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Soldier"));
    }

    // ------------------------------------------------------------------ Kytheon and Gideon

    [Fact]
    public async Task KytheonTransformsAfterAttackingWithTwoOthersEvenIfOneDied()
    {
        var s = new Scenario();
        var kytheon = s.Add(P0, Kytheon());
        var a = s.Add(P0, Bear("A"));
        var b = s.Add(P0, Bear("B"));
        foreach (var id in new[] { kytheon, a, b }) s.Card(id).ControlledSinceTurnStart = true;
        var wall = s.Add(P1, Bear("Wall", "0/5"));
        var ogre = s.Add(P1, Bear("Ogre", "3/3"));
        Script(s);
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(x => new AttackDeclaration(x, defenders[0])).ToList();
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(ogre, a), new BlockDeclaration(wall, kytheon) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(a).Zone);
        var gideon = s.Card(kytheon);
        Assert.Equal(Zone.Battlefield, gideon.Zone);
        Assert.True(gideon.Transformed);
        Assert.True(gideon.Is(CardType.Planeswalker));
        Assert.Equal(3, gideon.CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task KytheonStaysWithOnlyOneOtherAttacker()
    {
        var s = new Scenario();
        var kytheon = s.Add(P0, Kytheon());
        var a = s.Add(P0, Bear("A"));
        foreach (var id in new[] { kytheon, a }) s.Card(id).ControlledSinceTurnStart = true;
        s.Add(P0, Bear("Home")); // doesn't attack
        Script(s);
        s.Attacker.Attack = (_, attackers, defenders) => new[] { new AttackDeclaration(kytheon, defenders[0]), new AttackDeclaration(a, defenders[0]) };
        await s.RunUntilTurn();
        Assert.False(s.Card(kytheon).Transformed);
    }

    [Fact]
    public async Task GideonsZeroMakesHimAnIndestructibleCreatureWhoseDamageIsPrevented()
    {
        var s = new Scenario();
        var gideon = Gideon(s);
        var giant = s.Add(P1, Bear("Giant", "6/6"));
        Script(s, Activate(s, gideon, "0:"));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(x => x == gideon).Select(x => new AttackDeclaration(x, defenders[0])).ToList();
        s.Defender.Block = (_, _, _) => new[] { new BlockDeclaration(giant, gideon) };
        await s.RunUntilTurn();
        var g = s.Card(gideon);
        Assert.Equal(Zone.Battlefield, g.Zone);
        Assert.Equal(3, g.CounterCount(CounterKind.Loyalty));
        Assert.Contains(s.Game.Log.OfType<DamageDealt>(), d => d.Source == gideon && d.TargetCard == giant && d.Amount == 4);
        Assert.DoesNotContain(s.Game.Log.OfType<DamageDealt>(), d => d.TargetCard == gideon);
        Assert.False(g.IsCreature); // until end of turn
    }

    [Fact]
    public async Task AnAnimatedPlaneswalkerLosesLoyaltyAndHasDamageMarked()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        var walker = s.Add(P0, Make("Sage", "Legendary Planeswalker — Sage",
            """{ "abilities": [{ "cost": "0", "effects": [{ "become": "self", "power": 4, "toughness": 4, "addTypes": ["creature"] }], "text": "0: animate" }] }"""));
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        s.Card(walker).ControlledSinceTurnStart = true;
        s.InHand(P0, Shock("Shock", 2));
        int damage = -1;
        Script(s, Activate(s, walker, "0:"), legal =>
        {
            var cast = Cast(s, "Shock")(legal);
            return cast;
        });
        Aim(s, Named(s, "Sage"));
        s.Game.EventRaised += e => { if (e is DamageDealt d && d.TargetCard == walker) damage = s.Card(walker).Damage; };
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(walker).CounterCount(CounterKind.Loyalty));
        Assert.Equal(2, damage);
    }

    [Fact]
    public async Task GideonsPlusTwoMakesTheCreatureAttackHimDuringItsControllersNextTurnOnly()
    {
        var s = new Scenario();
        var gideon = Gideon(s);
        var bear = s.Add(P1, Bear("Bear"));
        s.Card(bear).ControlledSinceTurnStart = true;
        Script(s, Activate(s, gideon, "+2"));
        Aim(s, Named(s, "Bear"));
        var requests = new Dictionary<int, AttackRequest>();
        s.Defender.Attack = (view, attackers, defenders) =>
        {
            var request = view.AttackRequest!;
            requests[s.Game.State.TurnNumber] = request;
            return request.Complete(Array.Empty<AttackDeclaration>());
        };
        await s.RunUntilTurn(5);
        var next = requests[2];
        Assert.False(next.IsLegal(Array.Empty<AttackDeclaration>(), out _));
        Assert.False(next.IsLegal(new[] { new AttackDeclaration(bear, P0) }, out _));
        Assert.True(next.IsLegal(new[] { new AttackDeclaration(bear, P0, gideon) }, out _));
        Assert.True(requests[4].IsLegal(Array.Empty<AttackDeclaration>(), out _)); // only that turn
    }

    [Fact]
    public async Task GideonsPlusOneGivesIndestructibleUntilYourNextTurnAndUntaps()
    {
        var s = new Scenario();
        Lands(s, P0, GenericCards.Mountain, 1);
        var gideon = Gideon(s);
        var bear = s.Add(P0, Bear("Bear"));
        s.Card(bear).Tapped = true;
        s.InHand(P0, Make("Murder", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}"));
        Script(s, Activate(s, gideon, "+1"), Cast(s, "Murder"));
        Aim(s, Named(s, "Bear"));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.False(s.Card(bear).Tapped);
        Assert.True(s.Card(bear).Has(Keyword.Indestructible)); // still during the opponent's turn
    }
}
