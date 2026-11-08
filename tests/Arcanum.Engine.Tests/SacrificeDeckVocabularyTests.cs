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
/// Vocabulary of sacrifice decks: extort, committing a crime, extra mana from Swamps, an opponent's low life, mana from devotion,
/// sacrifices that share a card type, and dying triggers that trigger an additional time.
/// </summary>
public class SacrificeDeckVocabularyTests
{
    private static CardDefinition Scripted(CardDefinition card, string json)
    {
        var script = CardScriptParser.Parse(json);
        return script.ApplyTo(card with { Abilities = script.Abilities, Spell = script.Spell ?? card.Spell });
    }

    private static void NoAttacks(Scenario s) => s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();

    private static void ToGraveyard(Scenario s, CardId id)
    {
        var owner = s.Game.State.GetPlayer(s.Card(id).Owner);
        owner.Library.Remove(id);
        owner.Hand.Remove(id);
        s.Card(id).Zone = Zone.Graveyard;
        owner.Graveyard.Add(id);
    }

    private static CardDefinition Instant(string name, string cost) => new() { Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Instant };

    [Fact]
    public async Task ExtortDrainsEachOpponentWhenItsControllerCastsASpell()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P0, Creature("Toll Collector", 1, 1, Keyword.Extort));
        s.Add(P0, GenericCards.Swamp);
        s.Add(P0, GenericCards.Swamp);
        s.InHand(P0, Creature("Bear", 2, 2));
        await s.RunUntilTurn();
        Assert.Equal(19, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    private static CardDefinition TunnelRat() => Scripted(Creature("Tunnel Rat", 1, 1) with { ManaCost = ManaCost.Parse("{B}") },
        """{ "abilities": [ { "trigger": "youCommitCrime", "fromGraveyard": true, "effects": [ { "mayPay": "Pay {B}?", "mana": "{B}", "effects": [ { "reanimate": "self" } ] } ], "text": "Whenever you commit a crime, you may pay {B}. If you do, return this card from your graveyard to the battlefield." } ] }""");

    private static CardDefinition Jab() => Scripted(Instant("Jab", "{R}"),
        """{ "spell": { "targets": ["player"], "effects": [ { "loseLife": 1, "who": "target" } ], "text": "Target player loses 1 life." } }""");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetingAnOpponentIsACrimeTargetingYourselfIsNot(bool opponent)
    {
        var s = new Scenario();
        NoAttacks(s);
        var rat = s.InHand(P0, TunnelRat());
        s.Add(P0, GenericCards.Swamp);
        s.Add(P0, GenericCards.Mountain);
        s.InHand(P0, Jab());
        s.Attacker.Targets = (_, _) => new[] { Target.Of(opponent ? P1 : P0) };
        bool moved = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (v, legal) =>
        {
            if (!moved) { moved = true; ToGraveyard(s, rat); return PassPriority.Instance; }
            return legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == "Jab") is { } jab ? jab : PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(opponent ? Zone.Battlefield : Zone.Graveyard, s.Card(rat).Zone);
    }

    [Fact]
    public async Task ASwampTappedForManaAddsAnAdditionalBlack()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P0, Scripted(Creature("Mire Ghost", 2, 2), """{ "yourSwampsExtraBlack": true }"""));
        s.Add(P0, GenericCards.Swamp);
        var knight = s.InHand(P0, Creature("Dark Knight", 2, 2) with { ManaCost = ManaCost.Parse("{B}{B}") });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == knight) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(knight).Zone); // one Swamp paid {B}{B}
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task HasteAsLongAsAnOpponentHasTenOrLessLife(int life, bool haste)
    {
        var s = new Scenario();
        NoAttacks(s);
        var ghast = s.Add(P0, Scripted(Creature("Grave Runner", 2, 1),
            """{ "abilities": [ { "static": { "affects": "self", "keywords": ["Haste"], "while": { "opponentLifeAtMost": 10 } }, "text": "This creature has haste as long as an opponent has 10 or less life." } ] }"""));
        s.Game.State.GetPlayer(P1).Life = life;
        await s.RunUntilTurn();
        Assert.Equal(haste, s.Card(ghast).Has(Keyword.Haste));
    }

    [Fact]
    public async Task ManaOfAChosenColorEqualToDevotion()
    {
        var s = new Scenario();
        NoAttacks(s);
        var shrine = s.Add(P0, Scripted(new CardDefinition { Name = "Shrine", Types = CardType.Land },
            """{ "abilities": [ { "cost": "{2}, {T}", "effects": [ { "addManaAnyColor": 1, "devotionToThatColor": true } ], "text": "{2}, {T}: Choose a color. Add an amount of mana of that color equal to your devotion to that color." } ] }"""));
        s.Add(P0, Creature("Night Stalker", 2, 2) with { ManaCost = ManaCost.Parse("{B}{B}{B}") });
        s.Add(P0, GenericCards.Mountain);
        s.Add(P0, GenericCards.Mountain);
        s.Attacker.Choose = (_, r) => r.Options.Take(r.Min).Select(c => c.Id).ToList();
        int black = 0;
        s.Game.EventRaised += e => { if (e is ManaAdded { Source: var from, Type: ManaType.Black } && from == shrine) black++; };
        bool activated = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == shrine) is { } use) { activated = true; return use; }
            return PassPriority.Instance;
        };
        s.Attacker.Option = (_, r) => r.Options.ToList().IndexOf("Black");
        await s.RunUntilTurn();
        Assert.Equal(3, black); // devotion to black: {B}{B}{B}
    }

    [Fact]
    public async Task AnOpponentWithNothingSharingATypeLosesLifeAndYouDraw()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P0, Scripted(Creature("Dread Weaver", 1, 1), """
            { "abilities": [ { "trigger": "endStep", "effects": [ { "may": "Sacrifice?", "effects": [
              { "sacrifice": 1, "filter": { "types": ["artifact", "creature", "enchantment", "land", "planeswalker"] } },
              { "if": "youSacrificed", "then": [ { "opponentsMaySacrificeSharingType": { "loseLife": 2, "draw": 1 } } ] } ] } ],
              "text": "At the beginning of your end step, you may sacrifice an artifact, creature, enchantment, land, or planeswalker. If you do, each opponent may sacrifice a permanent of their choice that shares a card type with it. For each opponent who doesn't, that player loses 2 life and you draw a card." } ] }
            """));
        var charm = s.Add(P0, new CardDefinition { Name = "Old Charm", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment });
        var theirs = s.Add(P1, GenericCards.Mountain);
        s.Attacker.Choose = (_, r) => r.Options.Any(o => o.Id == charm) ? new[] { charm } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        int drawn = 0;
        s.Game.EventRaised += e => { if (e is CardDrawn { Player: var p } && p == P0 && s.Game.State.Step == Step.End) drawn++; };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(charm).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(theirs).Zone); // a land shares no type with an enchantment
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(1, drawn);
    }

    [Fact]
    public async Task AnOpponentMaySacrificeAPermanentSharingATypeInstead()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P0, Scripted(Creature("Dread Weaver", 1, 1), """
            { "abilities": [ { "trigger": "endStep", "effects": [ { "may": "Sacrifice?", "effects": [
              { "sacrifice": 1, "filter": { "types": ["enchantment"] } },
              { "if": "youSacrificed", "then": [ { "opponentsMaySacrificeSharingType": { "loseLife": 2, "draw": 1 } } ] } ] } ], "text": "…" } ] }
            """));
        s.Add(P0, new CardDefinition { Name = "Old Charm", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment });
        var theirs = s.Add(P1, new CardDefinition { Name = "Their Charm", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment });
        s.Defender.Choose = (_, r) => r.Options.Any(o => o.Id == theirs) ? new[] { theirs } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(theirs).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task ACreatureDyingTriggersAbilitiesOfYourPermanentsAnAdditionalTime()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P0, Scripted(Creature("Carnage Lord", 3, 3), """{ "yourDyingTriggersExtra": true }"""));
        s.Add(P0, Scripted(Creature("Blood Painter", 0, 1), """
            { "abilities": [ { "trigger": "creatureDies", "filter": { "types": ["creature"], "controller": "any", "other": true },
              "effects": [ { "loseLife": 1, "who": "opponents" } ], "text": "Whenever another creature dies, each opponent loses 1 life." },
              { "cost": "sacrificeAny:creature", "effects": [], "text": "Sacrifice a creature: nothing." } ] }
            """));
        var fodder = s.Add(P0, Creature("Fodder", 1, 1));
        s.Attacker.Choose = (_, r) => r.Options.Any(o => o.Id == fodder) ? new[] { fodder } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<ActivateAbility>().FirstOrDefault() is { } sac) { done = true; return sac; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(fodder).Zone);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // triggered twice
    }
}
