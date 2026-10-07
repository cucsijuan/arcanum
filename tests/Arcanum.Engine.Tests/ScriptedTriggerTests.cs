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
    private const string Bolt3 = """{ "spell": { "targets": ["any"], "effects": [{ "damage": 3, "to": "target" }] } }""";

    private static CardDefinition BoltAt(string name = "Lightning Jab") => Scripted(name, CardType.Instant, "{R}", Bolt3);

    /// <summary>Targets the given player with the first target requirement.</summary>
    private static void AimAt(Scenario s, PlayerId player) =>
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Player == player) };

    // ------------------------------------------------------------------ becoming a target, discarding, damage, dying

    [Theory]
    [InlineData(0, 20)] // nothing to pay with: the spell is countered
    [InlineData(1, 17)] // the tax is paid, the spell resolves
    public async Task YouBecomingTheTargetOfAnOpponentsSpellCountersItUnlessItsControllerPays(int spareLands, int expectedLife)
    {
        var s = new Scenario();
        s.Lands(P0, 1 + spareLands);
        s.Add(P1, Scripted("Warding Charm", CardType.Artifact, "{2}",
            """{ "abilities": [{ "trigger": "youBecomeTargetOfOpponent", "effects": [{ "counterTriggeringUnlessPays": "{1}" }] }] }"""));
        s.InHand(P0, BoltAt());
        AimAt(s, P1);
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(expectedLife, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ATriggerAboutYouBeingTargetedIgnoresYourOwnSpells()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, Scripted("Warding Charm", CardType.Artifact, "{2}",
            """{ "abilities": [{ "trigger": "youBecomeTargetOfOpponent", "effects": [{ "gainLife": 5 }] }] }"""));
        s.InHand(P0, Scripted("Soothing Rite", CardType.Instant, "{R}", """{ "spell": { "targets": ["player"], "effects": [{ "gainLife": 1 }] } }"""));
        AimAt(s, P0);
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task DiscardingACardBecauseOfAnOpponentTriggersItsOwnAbilityEvenInTheGraveyard()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        s.InHand(P1, Scripted("Spiteful Idol", CardType.Enchantment, "{3}",
            """{ "abilities": [{ "trigger": "discardedByOpponent", "if": { "life": 1 }, "effects": [{ "gainLife": 3 }] }] }"""));
        s.InHand(P0, Scripted("Mind Gnaw", CardType.Sorcery, "{R}", """{ "spell": { "targets": ["opponent"], "effects": [{ "discard": 1, "who": "target" }] } }"""));
        s.Defender.Discard = (view, count) => view.Self.Hand.Where(c => c.Name == "Spiteful Idol").Select(c => c.Id).Take(count).ToList();
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(23, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task DiscardingToHandSizeIsNotAnOpponentsDoing()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var idol = s.InHand(P0, Scripted("Spiteful Idol", CardType.Enchantment, "{9}",
            """{ "abilities": [{ "trigger": "discardedByOpponent", "effects": [{ "gainLife": 3 }] }] }"""));
        s.Attacker.Act = (_, _) => PassPriority.Instance;
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id == idol).Select(c => c.Id).Take(count).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(idol).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task WheneverThisCreatureIsDealtDamageTriggersOncePerSourceAndCountsWhatWasDealt()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var hydra = s.Add(P1, Creature("Gorging Hydra", """{ "abilities": [{ "trigger": "dealtDamage", "effects": [{ "counters": "triggerAmount", "what": "self" }] }] }""", 1, 6));
        s.InHand(P0, BoltAt());
        Acting(s, activations: 0);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == hydra) };
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ACreatureThatDiesFromTheDamageGetsNoCounters()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var hydra = s.Add(P1, Creature("Gorging Hydra", """{ "abilities": [{ "trigger": "dealtDamage", "effects": [{ "counters": "triggerAmount", "what": "self" }] }] }""", 1, 3));
        s.InHand(P0, BoltAt());
        Acting(s, activations: 0);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == hydra) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(hydra).Zone);
        Assert.Equal(0, s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task ABlockerThatIsDealtCombatDamageGetsTheTriggerToo()
    {
        var s = new Scenario();
        var hydra = s.Add(P1, Creature("Gorging Hydra", """{ "abilities": [{ "trigger": "dealtDamage", "effects": [{ "counters": "triggerAmount", "what": "self" }] }] }""", 1, 6));
        s.Add(P0, Creature("Lunging Brute", "{}", 3, 3));
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(3, s.Card(hydra).CounterCount(CounterKind.PlusOnePlusOne));
    }

    [Fact]
    public async Task APermanentDiesTriggerSeesPlaneswalkersAndCreaturesButNotOtherPermanents()
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P0, Scripted("Mourning Veil", CardType.Enchantment, "{2}",
            """{ "abilities": [{ "trigger": "permanentDies", "filter": { "anyOf": [{ "types": ["creature"] }, { "types": ["planeswalker"] }], "controller": "you" }, "effects": [{ "gainLife": 1 }] }] }"""));
        var walker = s.Add(P0, new CardDefinition { Name = "Wandering Sage", Types = CardType.Planeswalker, Loyalty = 3 });
        var trinket = s.Add(P0, new CardDefinition { Name = "Shiny Trinket", Types = CardType.Artifact });
        var soldier = s.Add(P0, Creature("Veil Soldier", "{}"));
        s.InHand(P0, Scripted("Wide Smite", CardType.Sorcery, "{R}",
            """{ "spell": { "effects": [{ "destroy": { "each": { "controller": "you", "types": ["planeswalker", "artifact", "creature"] } } }] } }"""));
        Acting(s, activations: 0);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(walker).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(trinket).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(soldier).Zone);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life); // the planeswalker and the creature, not the artifact
    }

    [Fact]
    public async Task WhenEnchantedCreatureBlocksTheAttackerStaysBlockedAndTheBlockerIsDestroyed()
    {
        var s = new Scenario();
        var blocker = s.Add(P1, Creature("Sluggish Ox", "{}", 3, 3));
        s.Game.SetupPermanent(P0, Scripted("Fatal Burden", CardType.Enchantment, "{2}",
            """{ "abilities": [{ "static": { "affects": "enchanted", "pump": [-6, 0] } }, { "trigger": "attachedBlocks", "effects": [{ "destroy": "triggered" }] }] }"""), blocker);
        s.Add(P0, Creature("Charging Raider", "{}", 2, 2));
        s.Defender.Block = (_, blockers, attackers) => new[] { new BlockDeclaration(blockers[0], attackers[0]) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(blocker).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life); // blocked, so no damage reaches the player
    }

    [Fact]
    public async Task AFilterForTheSourceItselfIgnoresOtherCreaturesBeingTargeted()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var skittish = s.Add(P1, Creature("Skittish Eel", """{ "abilities": [{ "trigger": "becomesTargetOfSpell", "filter": { "types": ["creature"], "self": true }, "effects": [{ "sacrificeIt": "self" }] }] }"""));
        var calm = s.Add(P1, Creature("Calm Eel", """{ "abilities": [{ "trigger": "becomesTargetOfSpell", "filter": { "types": ["creature"], "self": true }, "effects": [{ "sacrificeIt": "self" }] }] }""", 1, 9));
        s.InHand(P0, BoltAt());
        Acting(s, activations: 0);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == calm) };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(calm).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(skittish).Zone);
    }

    // ------------------------------------------------------------------ damage that can't be prevented, uncounterable by X

    [Theory]
    [InlineData("""{ "damage": 2, "to": "target" }""", Zone.Battlefield)]
    [InlineData("""{ "damage": 2, "to": "target", "cantBePrevented": true }""", Zone.Graveyard)]
    public async Task DamageThatCantBePreventedGetsPastPrevention(string effect, Zone expected)
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P1, Scripted("Guardian Totem", CardType.Artifact, "{2}", """{ "replaces": ["PreventNoncombatDamageToYourOtherCreatures"] }"""));
        var bear = s.Add(P1, Creature("Pale Bear", "{}", 2, 2));
        s.InHand(P0, Scripted("Sure Strike", CardType.Instant, "{R}", $$"""{ "spell": { "targets": ["creature"], "effects": [{{effect}}] } }"""));
        Acting(s, activations: 0);
        s.Attacker.Targets = (_, request) => new[] { request.LegalAt(0).First(t => t.Card == bear) };
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(bear).Zone);
    }

    [Theory]
    [InlineData(2, 18)]  // X is 2, which is enough: it can't be countered
    [InlineData(3, 20)]  // X is 2, not enough: the counterspell works
    public async Task ASpellCanBeUncounterableFromACertainXOn(int xNeeded, int expectedLife)
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        s.Add(P1, GenericCards.Island);
        s.InHand(P0, Scripted("Rising Blast", CardType.Sorcery, "{X}{R}",
            $$"""{ "uncounterableIfXAtLeast": {{xNeeded}}, "spell": { "targets": ["player"], "effects": [{ "loseLife": "X", "who": "target" }] } }"""));
        s.InHand(P1, Scripted("Hush", CardType.Instant, "{U}", """{ "spell": { "targets": ["spell"], "effects": [{ "counter": "target" }] } }"""));
        AimAt(s, P1);
        Acting(s, activations: 0);
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(expectedLife, s.Game.State.GetPlayer(P1).Life);
    }

    // ------------------------------------------------------------------ hexproof

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASourceMayIgnoreHexproofOfOpponentsForATurn(bool ignore)
    {
        var s = new Scenario();
        s.Lands(P0, 3);
        var shielded = s.Add(P1, Scripted("Veiled Stag", CardType.Creature, "{1}", "{}", 1, 1, null, null, "Hexproof"));
        s.Add(P1, Scripted("Warding Veil", CardType.Enchantment, "{2}", """{ "givesHexproof": true }"""));
        s.Add(P0, Scripted("Prying Tower", CardType.Artifact, "{2}", """{ "abilities": [{ "cost": "{1}", "effects": [{ "ignoreHexproof": true }] }] }"""));
        s.InHand(P0, BoltAt());
        IReadOnlyList<Abilities.Target>? legal = null;
        s.Attacker.Targets = (_, request) => { legal = request.LegalAt(0).ToList(); return new[] { legal.First(t => !t.IsNone) }; };
        Acting(s, activations: ignore ? 1 : 0);
        await s.RunUntilTurn();
        Assert.NotNull(legal);
        // Without it only the caster and their own permanents are legal; with it the hexproof opponent is too.
        Assert.Equal(ignore, legal!.Any(t => t.Player == P1));
        Assert.Equal(ignore, legal!.Any(t => t.Card == shielded || (t.Card is { } c && s.Card(c).Controller == P1 && s.Card(c).IsCreature)));
    }
}
