// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// A tapped creature's triggered mana ability on lands, controllers choosing to exile their own permanents, playing cards exiled from the
/// bottom of opponents' libraries, a planeswalker that is a creature off the battlefield, and repeating a process.
/// </summary>
public class HauntedCommanderTests
{
    private static CardDefinition Scripted(CardDefinition card, string json)
    {
        var script = CardScriptParser.Parse(json);
        return script.ApplyTo(card with { Abilities = script.Abilities, Spell = script.Spell ?? card.Spell });
    }

    private static void NoAttacks(Scenario s) => s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALandTappedForManaAddsOneMoreAndDealsDamageWhileTheCreatureIsTapped(bool tapped)
    {
        var s = new Scenario();
        NoAttacks(s);
        var imp = s.Add(P1, Scripted(Creature("Flare Imp", 2, 2), """{ "rules": ["LandsTappedAddOneAndDamageWhileTapped"] }"""));
        s.Add(P0, GenericCards.Forest);
        var knight = s.InHand(P0, Creature("Green Knight", 2, 2) with { ManaCost = ManaCost.Parse("{G}{G}") });
        s.Card(imp).Tapped = tapped;
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == knight) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn();
        Assert.Equal(tapped ? Zone.Battlefield : Zone.Hand, s.Card(knight).Zone); // one Forest paid {G}{G}
        Assert.Equal(tapped ? 19 : 20, s.Game.State.GetPlayer(P0).Life);
    }

    private static CardDefinition Choice() => Scripted(new CardDefinition { Name = "Lost Way", ManaCost = ManaCost.Parse("{3}{G}"), Types = CardType.Sorcery }, """
        { "spell": { "targetRule": "differentControllers",
          "targets": [ { "kind": "permanent", "controller": "opponent", "optional": true, "anyNumber": true, "filter": { "types": ["artifact", "enchantment"] } } ],
          "effects": [ { "controllersMayExile": "eachTarget" },
            { "if": { "atLeast": "targetsOnBattlefield", "value": 1 }, "then": [ { "search": { "types": ["land"] }, "count": "targetsOnBattlefield", "to": "battlefield", "tapped": true } ] } ],
          "text": "For each opponent, choose up to one target artifact or enchantment that player controls. For each permanent chosen this way, its controller may exile it. Then if one or more of the chosen permanents are still on the battlefield, you search your library for up to that many land cards, put them onto the battlefield tapped, then shuffle." } }
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItsControllerMayExileTheChosenPermanentOrYouSearchForALand(bool exile)
    {
        var s = new Scenario();
        NoAttacks(s);
        for (int i = 0; i < 4; i++) s.Add(P0, GenericCards.Forest);
        var spell = s.InHand(P0, Choice());
        var charm = s.Add(P1, new CardDefinition { Name = "Their Charm", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(charm) };
        s.Attacker.Choose = (_, r) => r.Options.Take(r.Max).Select(c => c.Id).ToList();
        s.Defender.YesNo = (_, _) => exile;
        await s.RunUntilTurn();
        Assert.Equal(exile ? Zone.Exile : Zone.Battlefield, s.Card(charm).Zone);
        Assert.Equal(exile ? 4 : 5, s.Game.State.PermanentsControlledBy(P0).Count(c => c.Is(CardType.Land)));
    }

    [Fact]
    public async Task TheBottomCardOfEachOpponentsLibraryIsExiledFaceDownAndItsPermanentSpellsCanBeCastWithAnyColor()
    {
        var s = new Scenario();
        NoAttacks(s);
        var flail = s.Add(P0, Scripted(new CardDefinition
        {
            Name = "Mind Leech", ManaCost = ManaCost.Parse("{4}{B}{B}{B}"), Types = CardType.Enchantment | CardType.Creature, Power = 5, Toughness = 5,
        }, """
            { "abilities": [
              { "static": { "affects": "self", "removeTypes": ["creature"], "while": { "not": { "control": { "notOwnedByYou": true }, "count": 3 } } },
                "text": "This isn't a creature unless you control three or more permanents you don't own." },
              { "trigger": "endStep", "effects": [ { "exileTopPlayable": 1, "of": "opponents", "fromBottom": true, "faceDown": true, "forever": true, "chooseOne": false, "permanentsOnly": true, "anyColorMana": true } ],
                "text": "At the beginning of your end step, exile the bottom card of each opponent's library face down. For as long as those cards remain exiled, you may look at them, you may cast permanent spells from among them, and you may spend mana as though it were mana of any color to cast those spells." } ] }
            """));
        var elk = s.InHand(P1, Creature("Green Elk", 2, 2) with { ManaCost = ManaCost.Parse("{G}") });
        s.Lands(P0, 1);
        bool moved = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!moved)
            {
                moved = true;
                var library = s.Game.State.GetPlayer(P1);
                library.Hand.Remove(elk);
                s.Card(elk).Zone = Zone.Library;
                library.Library.Add(elk); // the bottom card
            }
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == elk) is { } cast ? cast : PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.False(s.Card(flail).IsCreature);
        Assert.Equal(Zone.Exile, s.Card(elk).Zone);
        Assert.True(s.Card(elk).FaceDown);
        Assert.True(s.Card(elk).IsVisibleTo(P0));
        Assert.False(s.Card(elk).IsVisibleTo(P1));
        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Battlefield, s.Card(elk).Zone); // cast on the next turn with a Mountain
        Assert.Equal(P0, s.Card(elk).Controller);
    }

    private static CardDefinition HungerWalker() => Scripted(new CardDefinition
    {
        Name = "Swarm Mother", ManaCost = ManaCost.Parse("{1}{B}{G}"), Types = CardType.Planeswalker, Supertypes = Supertype.Legendary, Loyalty = 3,
    }, """
        { "creatureOffBattlefield": [1, 1, "Insect"], "abilities": [
          { "cost": "+1", "effects": [ { "tokens": 1, "token": { "name": "Insect", "types": "Creature — Insect", "colors": ["B", "G"], "power": 1, "toughness": 1 } }, { "mill": 1 },
              { "if": { "atLeast": { "milled": { "subtype": "Insect" } }, "value": 1 }, "then": [ { "counters": 1, "kind": "loyalty", "what": "self" }, { "repeatProcess": true } ] } ],
            "text": "+1: Create a 1/1 black and green Insect creature token, then mill a card. If an Insect card was milled this way, put a loyalty counter on this and repeat this process." } ] }
        """);

    [Fact]
    public void APlaneswalkerIsAnInsectCreatureOffTheBattlefieldOnly()
    {
        var s = new Scenario();
        var inHand = s.InHand(P0, HungerWalker());
        var onBattlefield = s.Add(P0, HungerWalker());
        Assert.True(s.Card(inHand).IsCreature && s.Card(inHand).HasSubtype("Insect"));
        Assert.Equal(1, s.Card(inHand).Power);
        Assert.False(s.Card(onBattlefield).IsCreature);
        Assert.True(s.Card(onBattlefield).Is(CardType.Planeswalker));
    }

    [Fact]
    public async Task TheProcessRepeatsOnlyWhileItsOwnMillFindsAnInsect()
    {
        var s = new Scenario();
        NoAttacks(s);
        var walker = s.Add(P0, HungerWalker());
        s.Card(walker).Counters[CounterKind.Loyalty] = 3; // without loyalty counters the walker dies as a state-based action
        var first = s.InHand(P0, Creature("Beetle", 1, 1) with { Subtypes = new[] { "Insect" } });
        var second = s.InHand(P0, HungerWalker()); // an Insect card in the library
        var third = s.InHand(P0, Creature("Bear", 2, 2));
        bool stacked = false, activated = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, first, second, third); }
            if (activated || legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == walker) is not { } plus) return PassPriority.Instance;
            activated = true;
            return plus;
        };
        await s.RunUntilTurn();
        Assert.Equal(new[] { Zone.Graveyard, Zone.Graveyard, Zone.Graveyard }, new[] { s.Card(first).Zone, s.Card(second).Zone, s.Card(third).Zone });
        Assert.Equal(3, s.Game.State.PermanentsControlledBy(P0).Count(c => c.Name == "Insect"));
        Assert.Equal(3 + 1 + 2, s.Card(walker).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task ACopyAlsoGetsTheAbilityTheCloneAdds()
    {
        var s = new Scenario();
        NoAttacks(s);
        s.Add(P1, Creature("Bear", 2, 2));
        var image = s.InHand(P0, Scripted(Creature("Mirror Shade", 0, 0) with { ManaCost = ManaCost.Parse("{U}"), Subtypes = new[] { "Illusion" } }, """
            { "entersAsCopy": { "types": ["creature"] }, "entersAsCopyAddSubtypes": ["Illusion"],
              "entersAsCopyAddAbilities": [ { "trigger": "becomesTargetOfAny", "effects": [ { "sacrificeIt": "self" } ], "text": "When this creature becomes the target of a spell or ability, sacrifice it." } ] }
            """));
        s.Add(P0, GenericCards.Island);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == image) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Choose = (_, r) => r.Options.Take(Math.Max(1, r.Min)).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal("Bear", s.Card(image).Name);
        Assert.True(s.Card(image).HasSubtype("Illusion"));
        Assert.Contains(s.Card(image).Abilities.OfType<TriggeredAbility>(), a => a.Trigger == TriggerEvent.BecomesTarget);
    }

    [Fact]
    public async Task ACurseOnAPlayerTriggersOnTheirNoncreatureSpellsAndStaysWithNoOtherOpponent()
    {
        var s = new Scenario();
        NoAttacks(s);
        var hex = s.Add(P0, Scripted(new CardDefinition { Name = "Fever Hex", ManaCost = ManaCost.Parse("{1}{R}{R}"), Types = CardType.Enchantment, Subtypes = new[] { "Aura", "Curse" } }, """
            { "abilities": [ { "trigger": "enchantedPlayerCastsSpell", "filter": { "not": ["creature"] },
              "effects": [ { "rollDie": 6 }, { "damage": "rolled", "to": "triggeredPlayer" }, { "attachToRandomOtherOpponent": true } ], "text": "…" } ] }
            """));
        s.Card(hex).AttachedPlayer = P1;
        var charm = s.InHand(P1, new CardDefinition { Name = "Their Charm", ManaCost = ManaCost.Parse("{G}"), Types = CardType.Enchantment });
        var bear = s.InHand(P1, Creature("Their Bear", 2, 2) with { ManaCost = ManaCost.Parse("{G}") });
        s.Add(P1, GenericCards.Forest);
        s.Add(P1, GenericCards.Forest);
        s.Defender.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == charm || c.Card == bear) is { } cast ? cast : PassPriority.Instance;
        await s.RunUntilTurn(3);
        Assert.Equal(Zone.Battlefield, s.Card(charm).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone);
        Assert.InRange(s.Game.State.GetPlayer(P1).Life, 14, 19); // one roll: only the noncreature spell
        Assert.Equal(P1, s.Card(hex).AttachedPlayer);
    }

    [Fact]
    public async Task EachMoneyVoteGainsYouAPermanentTheVoterOwnsAndEachTimeVoteAnExtraTurn()
    {
        var s = new Scenario();
        NoAttacks(s);
        var spell = s.InHand(P0, Scripted(new CardDefinition { Name = "Toll of Ages", ManaCost = ManaCost.Parse("{U}"), Types = CardType.Sorcery }, """
            { "spell": { "exileAfter": true, "effects": [ { "vote": ["time", "money"] }, { "repeat": { "votes": 0 }, "effects": [ { "extraTurn": true } ] }, { "gainVotersPermanents": 1 } ],
              "text": "Council's dilemma — Starting with you, each player votes for time or money. For each time vote, take an extra turn after this one. For each money vote, choose a permanent owned by the voter and gain control of it. Exile this." } }
            """));
        s.Add(P0, GenericCards.Island);
        var bear = s.Add(P1, Creature("Their Bear", 2, 2));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Option = (_, r) => r.Options.ToList().IndexOf("time") is var i && i >= 0 ? i : 0;
        s.Defender.Option = (_, r) => r.Options.ToList().IndexOf("money") is var i && i >= 0 ? i : 0;
        s.Attacker.Choose = (_, r) => r.Options.Any(o => o.Id == bear) ? new[] { bear } : r.Options.Take(r.Min).Select(c => c.Id).ToList();
        PlayerId? second = null;
        s.Game.EventRaised += e => { if (e is Events.TurnBegan { TurnNumber: 2 } t) second = t.ActivePlayer; };
        await s.RunUntilTurn(3);
        Assert.Equal(P0, s.Card(bear).Controller);
        Assert.Equal(Zone.Exile, s.Card(spell).Zone);
        Assert.Equal(P0, second); // the extra turn
    }

    [Fact]
    public async Task LandsATapCostNeedsStayUntappedWhileTheManaIsPaid()
    {
        var s = new Scenario();
        NoAttacks(s);
        var lands = Enumerable.Range(0, 3).Select(_ => s.Add(P0, GenericCards.Forest)).ToList();
        var fear = s.InHand(P0, Scripted(Creature("Dread Shape", 2, 2) with { ManaCost = ManaCost.Parse("{G}") }, """
            { "additionalCost": { "tap": { "anyOf": [ { "types": ["creature"] }, { "types": ["land"] } ] }, "tapCount": 2 } }
            """));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == fear) is { } cast ? cast : PassPriority.Instance;
        s.Attacker.Choose = (_, r) => r.Options.Take(Math.Max(r.Min, 0)).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(fear).Zone); // one Forest paid {G}, the other two were tapped for the cost
        Assert.All(lands, id => Assert.True(s.Card(id).Tapped));
        Assert.Empty(s.Game.FailedActions);
    }
}
