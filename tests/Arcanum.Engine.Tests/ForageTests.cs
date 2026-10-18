// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Forage (rule 701.61a): "To forage, a player exiles three cards from their graveyard or sacrifices a Food." Whenever a
/// player forages, "youForage" triggers fire for each permanent that player controls.
/// </summary>
public class ForageTests
{
    /// <summary>An artifact with "{T}, Forage: Draw a card.", read from its script.</summary>
    private static CardDefinition Forager()
    {
        var parsed = CardScriptParser.Parse("""
            { "abilities": [ { "cost": "{T}, forage", "effects": [ { "draw": 1 } ], "text": "{T}, Forage: Draw a card." } ] }
            """);
        return new CardDefinition { Name = "Thrift Lantern", Types = CardType.Artifact, Abilities = parsed.Abilities };
    }

    /// <summary>A creature that may forage as it enters, to draw a card.</summary>
    private static CardDefinition Gatherer()
    {
        var parsed = CardScriptParser.Parse("""
            { "abilities": [ { "trigger": "enters", "effects": [ { "mayPay": "Forage?", "cost": { "forage": true }, "effects": [ { "draw": 1 } ] } ],
              "text": "When this creature enters, you may forage. If you do, draw a card." } ] }
            """);
        return new CardDefinition
        {
            Name = "Hollow Gatherer", Types = CardType.Creature, ManaCost = ManaCost.Parse("{1}"), Power = 1, Toughness = 1,
            Abilities = parsed.Abilities,
        };
    }

    /// <summary>A permanent with "Whenever you forage, you gain 1 life."</summary>
    private static CardDefinition Idol()
    {
        var parsed = CardScriptParser.Parse("""
            { "abilities": [ { "trigger": "youForage", "effects": [ { "gainLife": 1 } ], "text": "Whenever you forage, you gain 1 life." } ] }
            """);
        return new CardDefinition { Name = "Thankful Idol", Types = CardType.Enchantment, Abilities = parsed.Abilities };
    }

    private static CardDefinition Scout() => Creature("Dusty Scout", 1, 1);

    /// <summary>Puts a card straight into its owner's graveyard before the game starts.</summary>
    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    /// <summary>The cards a player draws after foraging (the opening hand is drawn before any forage).</summary>
    private static List<CardId> DrawnAfterForaging(Scenario s, PlayerId player)
    {
        var drawn = new List<CardId>();
        var foraged = false;
        s.Game.EventRaised += e =>
        {
            if (e is Foraged f && f.Player == player) foraged = true;
            else if (foraged && e is CardDrawn d && d.Player == player) drawn.Add(d.Card);
        };
        return drawn;
    }

    /// <summary>How many Dusty Scouts (the cards forage exiles) are in a player's graveyard.</summary>
    private static int Scouts(Scenario s, PlayerId owner) =>
        s.Game.State.GetPlayer(owner).Graveyard.Count(id => s.Card(id).Name == "Dusty Scout");

    /// <summary>Player 0 casts what it can and never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    /// <summary>Player 0 activates the given source's ability as soon as it's offered, and never attacks.</summary>
    private static void ActivateFrom(Scenario s, CardId source)
    {
        s.Attacker.Act = (_, legal) => legal.OfType<ActivateAbility>().Where(a => a.Source == source).Cast<PlayerAction>().FirstOrDefault()
            ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
    }

    [Fact]
    public async Task ActivatedForageExilesThreeGraveyardCards()
    {
        var s = new Scenario();
        var source = s.Add(P0, Forager());
        var grave = Enumerable.Range(0, 3).Select(_ => PutInGraveyard(s, P0, Scout())).ToList();
        ActivateFrom(s, source);
        var drawn = DrawnAfterForaging(s, P0);
        await s.RunUntilTurn();
        foreach (var id in grave) Assert.Equal(Zone.Exile, s.Card(id).Zone);
        Assert.Equal(0, Scouts(s, P0));
        Assert.Single(drawn);
        Assert.True(s.Card(source).Tapped);
    }

    [Fact]
    public async Task ActivatedForageCantBeActivatedWithoutGraveyardOrFood()
    {
        var s = new Scenario();
        var source = s.Add(P0, Forager());
        PutInGraveyard(s, P0, Scout());
        PutInGraveyard(s, P0, Scout());
        var offered = new List<ActivateAbility>();
        s.Attacker.Act = (_, legal) => { offered.AddRange(legal.OfType<ActivateAbility>()); return PassPriority.Instance; };
        await s.RunUntilTurn();
        Assert.DoesNotContain(offered, a => a.Source == source);
        Assert.Equal(2, s.Game.State.GetPlayer(P0).Graveyard.Count);
    }

    [Fact]
    public async Task ForageBySacrificingAFood()
    {
        var s = new Scenario();
        var source = s.Add(P0, Forager());
        var food = s.Add(P0, PredefinedTokens.Food);
        ActivateFrom(s, source);
        var drawn = DrawnAfterForaging(s, P0);
        await s.RunUntilTurn();
        Assert.DoesNotContain(food, s.Game.State.Battlefield);
        Assert.Single(drawn);
    }

    [Fact]
    public async Task ChoosingTheFoodWhenBothArePossible()
    {
        var s = new Scenario();
        var source = s.Add(P0, Forager());
        var grave = Enumerable.Range(0, 3).Select(_ => PutInGraveyard(s, P0, Scout())).ToList();
        var food = s.Add(P0, PredefinedTokens.Food);
        ActivateFrom(s, source);
        var labels = new List<string>();
        s.Attacker.Option = (_, request) => { labels.AddRange(request.Options); return 1; };
        await s.RunUntilTurn();
        Assert.Equal(new[] { "Exile three cards from your graveyard", "Sacrifice a Food" }, labels);
        Assert.DoesNotContain(food, s.Game.State.Battlefield);
        Assert.Equal(3, Scouts(s, P0));
        foreach (var id in grave) Assert.Equal(Zone.Graveyard, s.Card(id).Zone);
    }

    [Fact]
    public async Task MayForageOnEnterIsOptional()
    {
        async Task<(Scenario Game, List<CardId> Drawn)> CastGatherer(bool pay)
        {
            var s = Casting();
            s.Attacker.YesNo = (_, _) => pay;
            s.Lands(P0, 1);
            for (int i = 0; i < 3; i++) PutInGraveyard(s, P0, Scout());
            s.InHand(P0, Gatherer());
            var drawn = DrawnAfterForaging(s, P0);
            await s.RunUntilTurn();
            return (s, drawn);
        }

        var (declined, declinedDraws) = await CastGatherer(false);
        Assert.Equal(3, Scouts(declined, P0));
        Assert.Empty(declined.Game.State.GetPlayer(P0).Exile);
        Assert.Empty(declinedDraws);

        var (accepted, acceptedDraws) = await CastGatherer(true);
        Assert.Equal(0, Scouts(accepted, P0));
        Assert.Equal(3, accepted.Game.State.GetPlayer(P0).Exile.Count);
        Assert.Single(acceptedDraws);
    }

    [Fact]
    public async Task WheneverYouForageTriggers()
    {
        var s = new Scenario();
        var source = s.Add(P0, Forager());
        s.Add(P0, Idol());
        s.Add(P1, Idol());
        for (int i = 0; i < 3; i++) PutInGraveyard(s, P0, Scout());
        ActivateFrom(s, source);
        await s.RunUntilTurn();
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }
}
