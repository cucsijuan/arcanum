// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Arcanum.Cards;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Blight N (put N -1/-1 counters on a creature you control) as a cost, an optional additional cost and an effect,
/// "if this spell's additional cost was paid", wither, evoke, encore, compleated, clash, manifest, companion, persist and undying. The cards are invented.
/// </summary>
public class BlightTests
{
    private static int _next;

    private static CardDefinition Make(string name, string typeLine, string script, string cost = "", string? pt = null, string oracle = "", params string[] keywords)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oracle_id"] = $"blight-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = cost, ["type_line"] = typeLine, ["oracle_text"] = oracle,
            ["power"] = pt?.Split('/')[0], ["toughness"] = pt?.Split('/')[1], ["keywords"] = keywords,
            ["loyalty"] = typeLine.Contains("Planeswalker") ? "5" : null,
        });
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(json))).First();
        return CardFactory.Create(record, CardScriptParser.Parse(script)).Definition;
    }

    private static void Play(Scenario s, Func<IReadOnlyList<PlayerAction>, PlayerAction?> step)
    {
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && step(legal) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    private static Func<IReadOnlyList<PlayerAction>, PlayerAction?> Cast(Scenario s, string name) =>
        legal => legal.OfType<CastSpell>().FirstOrDefault(c => s.Card(c.Card).Name == name);

    private static Func<GameView, CardChoiceRequest, IReadOnlyList<CardId>> Pick(CardId card) =>
        (_, request) => request.Options.Any(o => o.Id == card) ? new[] { card } : request.Options.Take(request.Min).Select(o => o.Id).ToList();

    [Fact]
    public async Task ABlightCostPutsTheCountersOnTheCreatureItsPayerChooses()
    {
        var idol = Make("Grim Idol", "Artifact", """{ "abilities": [{ "cost": "{1}, blight:2", "effects": [{ "gainLife": 3 }] }] }""", "{2}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, idol);
        var mouse = s.Add(P0, Creature("Mouse", 1, 1));
        var ox = s.Add(P0, Creature("Ox", 3, 3));
        s.Attacker.Choose = Pick(ox);
        Play(s, legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == "Grim Idol"));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(ox).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(0, s.Card(mouse).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task WithoutACreatureABlightCostCantBePaid()
    {
        var idol = Make("Grim Idol", "Artifact", """{ "abilities": [{ "cost": "{1}, blight:1", "effects": [{ "gainLife": 3 }] }] }""", "{2}");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, idol);
        s.Add(P1, Creature("Their Ox", 3, 3)); // an opponent's creature can't pay it
        bool offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            offered |= legal.OfType<ActivateAbility>().Any(a => s.Card(a.Source).Name == "Grim Idol");
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.False(offered);
    }

    private const string EmberStrike = """
        { "optionalAdditionalCost": { "blight": 1 },
          "spell": { "targets": ["creature"], "effects": [
            { "if": "additionalCostPaid", "then": [{ "damage": 4, "to": "target" }], "else": [{ "damage": 2, "to": "target" }] } ] } }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOptionalBlightIsAskedAtCastAndTheSpellKnowsWhetherItWasPaid(bool pay)
    {
        var strike = Make("Ember Strike", "Sorcery", EmberStrike, "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        var wall = s.Add(P1, Creature("Wall", 0, 3));
        s.InHand(P0, strike);
        s.Attacker.YesNo = (_, _) => pay;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(wall) };
        Play(s, Cast(s, "Ember Strike"));
        await s.RunUntilTurn();
        Assert.Equal(pay ? 1 : 0, s.Card(bear).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(pay ? Zone.Graveyard : Zone.Battlefield, s.Card(wall).Zone);
    }

    [Fact]
    public async Task EachOpponentBlightsAndAPlayerWithoutCreaturesDoesNothing()
    {
        var word = Make("Withering Word", "Sorcery", """{ "spell": { "effects": [{ "blight": 1, "who": "opponents" }, { "blight": 2 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var first = s.Add(P1, Creature("First", 2, 2));
        var second = s.Add(P1, Creature("Second", 2, 2));
        var cast = s.InHand(P0, word);
        s.Defender.Choose = Pick(second);
        Play(s, Cast(s, "Withering Word"));
        await s.RunUntilTurn();
        Assert.Equal(0, s.Card(first).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(1, s.Card(second).CounterCount(CounterKind.MinusOneMinusOne)); // its controller chose it
        Assert.Equal(Zone.Graveyard, s.Card(cast).Zone); // P0 controls no creature: its own blight 2 did nothing
    }

    [Fact]
    public async Task BlightXIsAnAdditionalCostAndXIsCappedByTheGreatestToughness()
    {
        var pyre = Make("Soul Pyre", "Sorcery", """{ "additionalCost": { "blight": "X" }, "maxX": "greatestToughness", "spell": { "effects": [{ "gainLife": "X" }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var tall = s.Add(P0, Creature("Tall One", 1, 3));
        s.Add(P0, Creature("Mouse", 1, 1));
        s.InHand(P0, pyre);
        int offeredMax = -1;
        s.Attacker.Number = (_, request) => { offeredMax = request.Max; return request.Max; };
        s.Attacker.Choose = Pick(tall);
        Play(s, Cast(s, "Soul Pyre"));
        await s.RunUntilTurn();
        Assert.Equal(3, offeredMax);
        Assert.Equal(Zone.Graveyard, s.Card(tall).Zone); // three -1/-1 counters on a 1/3
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task WitherDamageIsDealtAsMinusCountersThatStay()
    {
        var thorn = Make("Thorn Sprite", "Creature — Faerie", """{ "abilities": [{ "cost": "{1}", "targets": ["creature"], "effects": [{ "damage": 2, "to": "target" }] }] }""",
            "{1}{B}", "1/1", "Wither", "Wither");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Add(P0, thorn);
        var ox = s.Add(P1, Creature("Ox", 3, 3));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ox) };
        Play(s, legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => s.Card(a.Source).Name == "Thorn Sprite"));
        await s.RunUntilTurn(2);
        Assert.Equal(2, s.Card(ox).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(0, s.Card(ox).Damage);
        Assert.Equal(1, s.Card(ox).Toughness); // still 1/1 after the turn ends: counters aren't removed like damage
    }

    // ------------------------------------------------------------------ evoke

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEvokedCreatureIsSacrificedAfterItsEntersTriggerCanStillResolve(bool evoke)
    {
        var drifter = Make("Cloud Drifter", "Creature — Elemental", """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 2 }] }] }""",
            "{4}{R}", "3/3", "Flying\nWhen this creature enters, you gain 2 life.\nEvoke {1}{R} (You may cast this spell for its evoke cost. If you do, it's sacrificed when it enters.)",
            "Flying", "Evoke");
        var s = new Scenario();
        s.Lands(P0, 5);
        var cast = s.InHand(P0, drifter);
        s.Attacker.YesNo = (_, request) => !request.Prompt.Contains("evoke cost") || evoke;
        Play(s, Cast(s, "Cloud Drifter"));
        await s.RunUntilTurn();
        Assert.Equal(evoke ? Zone.Graveyard : Zone.Battlefield, s.Card(cast).Zone);
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(evoke ? 3 : 0, s.Game.State.Battlefield.Count(id => !s.Card(id).Tapped && s.Card(id).Name == "Mountain")); // {1}{R} or all five paid
    }

    [Fact]
    public async Task AnEvokeCostCanExileACardOfItsColorFromTheHand()
    {
        var spirit = Make("Ember Spirit", "Creature — Elemental Incarnation", """{ "abilities": [{ "trigger": "enters", "effects": [{ "gainLife": 4 }] }] }""",
            "{3}{R}{R}", "3/2", "When this creature enters, you gain 4 life.\nEvoke—Exile a red card from your hand.", "Evoke");
        var s = new Scenario(); // no lands: evoking is the only way
        var cast = s.InHand(P0, spirit);
        var green = s.InHand(P0, Creature("Green Bear", 2, 2) with { ManaCost = Mana.ManaCost.Parse("{1}{G}") });
        var red = s.InHand(P0, Creature("Red Bear", 2, 2) with { ManaCost = Mana.ManaCost.Parse("{1}{R}") });
        IReadOnlyList<CardId>? offered = null;
        s.Attacker.Choose = (_, request) => { offered = request.Options.Select(o => o.Id).ToList(); return new[] { red }; };
        Play(s, Cast(s, "Ember Spirit"));
        await s.RunUntilTurn();
        Assert.Equal(new[] { red }, offered);
        Assert.Equal(Zone.Exile, s.Card(red).Zone);
        Assert.Equal(Zone.Hand, s.Card(green).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(cast).Zone);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ encore

    [Fact]
    public async Task EncoreMakesAHastyCopyThatHasToAttackAndIsSacrificedAtTheEndStep()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var raider = s.InHand(P0, Make("Grave Raider", "Creature — Pirate", "{}", "{3}{R}", "3/1", "Encore {2}", "Encore"));
        var player = s.Game.State.GetPlayer(P0);
        player.Hand.Remove(raider);
        player.Graveyard.Add(raider);
        s.Card(raider).Zone = Zone.Graveyard;
        Play(s, legal => legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == raider));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(raider).Zone); // exiled to pay the cost
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // one copy for the one opponent, with haste
        Assert.DoesNotContain(s.Game.State.Battlefield, id => s.Card(id).Name == "Grave Raider"); // sacrificed at the end step
    }

    // ------------------------------------------------------------------ compleated

    [Theory]
    [InlineData(1, 3, 18)] // only a Mountain: the {B/P} is paid with 2 life, two fewer loyalty counters
    [InlineData(0, 5, 20)] // a Swamp: paid with mana, all five
    public async Task ACompleatedPlaneswalkerEntersWithTwoFewerLoyaltyForEachPhyrexianSymbolPaidWithLife(int mountainOnly, int loyalty, int life)
    {
        var walker = Make("Venom Seer", "Legendary Planeswalker — Seer", """{ "abilities": [{ "cost": "+1", "effects": [{ "gainLife": 1 }] }] }""",
            "{1}{B/P}", null, "Compleated ({B/P} can be paid with {B} or 2 life. If life was paid, this planeswalker enters with two fewer loyalty counters.)\n+1: You gain 1 life.",
            "Compleated");
        var s = new Scenario();
        s.Lands(P0, 2);
        if (mountainOnly == 0) s.Add(P0, GenericCards.Swamp);
        var cast = s.InHand(P0, walker);
        // The default answer to "how to pay" is the first way: all of it with mana when that's possible.
        bool done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == cast) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(cast).Zone);
        Assert.Equal(loyalty, s.Card(cast).CounterCount(CounterKind.Loyalty));
        Assert.Equal(life, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ clash

    [Fact]
    public async Task WinningAClashRepeatsTheProcessUntilAClashIsNotWon()
    {
        var greed = Make("Greedy Hoard", "Sorcery", """{ "spell": { "effects": [{ "loseLife": 2 }, { "draw": 2 }, { "clash": true, "ifWin": [{ "repeatProcess": true }] }] } }""", "{1}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var first = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var second = s.Game.SetupInLibrary(P0, GenericCards.Forest);
        var big = s.Game.SetupInLibrary(P0, Creature("Big One", 5, 5) with { ManaCost = Mana.ManaCost.Parse("{5}") });
        var cast = s.InHand(P0, greed);
        bool stacked = false, done = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, first, second, big); }
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == cast) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        // The first clash reveals the 5-drop against a land: won, so it all happens again; the second is land against land: not won.
        Assert.Equal(16, s.Game.State.GetPlayer(P0).Life);
        Assert.NotEqual(Zone.Library, s.Card(big).Zone); // kept on top, then drawn by the repeat (and maybe discarded down to seven)
    }

    // ------------------------------------------------------------------ manifest

    [Fact]
    public async Task TheExiledCreaturesControllerManifestsTheTopCardOfTheirLibrary()
    {
        var shift = Make("Shape Swap", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "exile": "target" }, { "manifest": 1, "who": "targetController" }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 1);
        var ox = s.Add(P1, Creature("Ox", 3, 3));
        var hidden = s.Game.SetupInLibrary(P1, Creature("Hidden Wolf", 4, 4) with { ManaCost = Mana.ManaCost.Parse("{3}{G}") });
        var cast = s.InHand(P0, shift);
        bool stacked = false, done = false;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(ox) };
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P1, hidden); }
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == cast) is { } a) { done = true; return a; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Exile, s.Card(ox).Zone);
        var wolf = s.Card(hidden);
        Assert.Equal(Zone.Battlefield, wolf.Zone);
        Assert.Equal(P1, wolf.Controller);
        Assert.True(wolf.TurnedFaceDown && wolf.Manifested);
        Assert.Equal(2, wolf.Power);
        Assert.False(wolf.IsVisibleTo(P0)); // only its controller may look at it
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AManifestedCreatureCardTurnsFaceUpForItsManaCostAndOtherCardsDont(bool creature)
    {
        var peek = Make("Odd Peek", "Sorcery", """{ "spell": { "effects": [{ "manifest": 1 }] } }""", "{R}");
        var top = creature ? Creature("Ember Cub", 3, 3) with { ManaCost = Mana.ManaCost.Parse("{R}") }
            : Make("Spark Burst", "Sorcery", """{ "spell": { "effects": [{ "gainLife": 1 }] } }""", "{R}");
        var s = new Scenario();
        s.Lands(P0, 2);
        var hidden = s.Game.SetupInLibrary(P0, top);
        var cast = s.InHand(P0, peek);
        bool stacked = false, done = false, offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!stacked) { stacked = true; s.Restack(P0, hidden); }
            if (!done && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == cast) is { } a) { done = true; return a; }
            if (legal.OfType<TurnFaceUp>().FirstOrDefault(t => t.Card == hidden && t.ForManaCost) is { } up && s.Card(hidden).TurnedFaceDown) { offered = true; return up; }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(creature, offered);
        Assert.Equal(!creature, s.Card(hidden).TurnedFaceDown);
        Assert.Equal(creature ? 3 : 2, s.Card(hidden).Power);
    }

    // ------------------------------------------------------------------ companion

    private static CardDefinition Elk => Make("Wellspring Elk", "Legendary Creature — Elk", """{ "companion": "noRepeatedManaSymbols" }""", "{4}{R/G}", "5/5",
        "Companion — No card in your starting deck has more than one of the same mana symbol in its mana cost.", "Companion");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARevealedCompanionIsPutIntoTheHandForThreeOnceItsConditionHolds(bool repeatedSymbol)
    {
        var s = new Scenario(sideboard: new[] { Elk });
        s.Lands(P0, 3);
        if (repeatedSymbol) s.Game.SetupInLibrary(P0, Creature("Twin Flame", 2, 2) with { ManaCost = Mana.ManaCost.Parse("{R}{R}") });
        bool asked = false, taken = false;
        s.Attacker.Choose = (_, request) =>
        {
            if (!request.Prompt.StartsWith("Reveal a companion")) return request.Options.Take(request.Min).Select(o => o.Id).ToList();
            asked = true;
            return new[] { request.Options[0].Id };
        };
        s.Attacker.Act = (_, legal) =>
        {
            if (!taken && legal.OfType<PutCompanionIntoHand>().FirstOrDefault() is { } put) { taken = true; return put; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        var player = s.Game.State.GetPlayer(P0);
        Assert.Equal(!repeatedSymbol, asked); // a {R}{R} card in the starting deck: it can't be revealed
        Assert.Equal(!repeatedSymbol, taken);
        Assert.Equal(repeatedSymbol ? 1 : 0, player.Outside.Count);
        if (!repeatedSymbol) // into the hand (where the cleanup step may have had it discarded down to seven)
            Assert.Contains(player.Hand.Concat(player.Graveyard), id => s.Card(id).Name == "Wellspring Elk");
        if (!repeatedSymbol) Assert.Equal(0, s.Game.State.Battlefield.Count(id => s.Card(id).Name == "Mountain" && !s.Card(id).Tapped)); // {3} paid
    }

    // ------------------------------------------------------------------ persist and undying

    [Theory]
    [InlineData("Persist", CounterKind.MinusOneMinusOne)]
    [InlineData("Undying", CounterKind.PlusOnePlusOne)]
    public async Task APermanentWithPersistOrUndyingReturnsOnceWithItsCounter(string keyword, CounterKind kind)
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var shade = s.Add(P1, Make("Stubborn Shade", "Creature — Shade", "{}", "{2}{B}", "3/3", keyword, keyword));
        var dooms = new[] { s.InHand(P0, Make("Doom", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}")),
                            s.InHand(P0, Make("Doom Again", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}")) };
        s.Attacker.Targets = (_, _) => new[] { Target.Of(shade) };
        int cast = 0;
        s.Attacker.Act = (view, legal) =>
        {
            bool ready = view.Stack.Count == 0 && s.Card(shade).Zone == Zone.Battlefield && s.Card(shade).CounterCount(kind) == cast;
            if (cast < 2 && ready && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == dooms[cast]) is { } a) { cast++; return a; }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, cast); // the second kill found it back with one counter
        Assert.Equal(Zone.Graveyard, s.Card(shade).Zone); // that counter stopped it the second time
    }

    [Fact]
    public async Task APersistCreatureComesBackWithAMinusCounter()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var shade = s.Add(P1, Make("Stubborn Shade", "Creature — Shade", "{}", "{2}{B}", "3/3", "Persist", "Persist"));
        var doom = s.InHand(P0, Make("Doom", "Instant", """{ "spell": { "targets": ["creature"], "effects": [{ "destroy": "target" }] } }""", "{R}"));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(shade) };
        Play(s, legal => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == doom));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(shade).Zone);
        Assert.Equal(1, s.Card(shade).CounterCount(CounterKind.MinusOneMinusOne));
        Assert.Equal(2, s.Card(shade).Power);
    }
}
