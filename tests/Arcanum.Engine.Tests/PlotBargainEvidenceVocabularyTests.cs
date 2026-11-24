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
/// Vocabulary for a card that is plotted from hand and cast later without paying its mana cost, an optional additional cost
/// that sacrifices an artifact, enchantment or token (bargain), one that exiles cards from the graveyard with total mana value
/// (collect evidence), one that taps creatures by total power (teamwork), a land that is earthbent and comes back tapped, a
/// card cast from the graveyard the turn it was discarded (mayhem), a power-up ability, and a return from the graveyard limited
/// by total mana value.
/// </summary>
public class PlotBargainEvidenceVocabularyTests
{
    private static int _next;

    /// <summary>A card from invented oracle data and a script, built through the card factory like an import.</summary>
    private static CardDefinition Scripted(string name, string typeLine, string manaCost, string script, string oracle = "", string? power = null, string? toughness = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["oracle_id"] = $"rules-{_next++}", ["name"] = name, ["layout"] = "normal", ["mana_cost"] = manaCost,
            ["type_line"] = typeLine, ["oracle_text"] = oracle,
        };
        if (power is not null) { fields["power"] = power; fields["toughness"] = toughness; }
        var record = OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)))).First();
        var parsed = CardScriptParser.Parse(script);
        var (definition, support) = CardFactory.Create(record, parsed);
        Assert.True(support == CardSupport.Full, string.Join("; ", CardFactory.Explain(record, parsed)));
        return definition;
    }

    private static CardDefinition Spell(string name, string cost, TargetSpec? target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = target is null ? Array.Empty<TargetSpec>() : new[] { target }, Effects = effects },
    };

    private static CardId PutInGraveyard(Scenario s, PlayerId owner, CardDefinition card)
    {
        var id = s.InHand(owner, card);
        var player = s.Game.State.GetPlayer(owner);
        player.Hand.Remove(id);
        player.Graveyard.Add(id);
        s.Card(id).Zone = Zone.Graveyard;
        return id;
    }

    /// <summary>The bargain cost as the card factory builds it: sacrifice an artifact, an enchantment or a token.</summary>
    private static ExtraCost BargainCost() => new(Sacrifice: new ObjectFilter(AnyOf: new[]
    {
        new ObjectFilter(CardType.Artifact), new ObjectFilter(CardType.Enchantment), new ObjectFilter(Token: true),
    })) { Bargain = true };

    /// <summary>A scenario in which the attacker never attacks; each test sets how it acts.</summary>
    private static Scenario Quiet()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    // ------------------------------------------------------------------ plot (702.170)

    [Fact]
    public async Task APlottedCardIsCastLaterFromExileWithoutPayingItsManaCost()
    {
        var s = new Scenario();
        var mountains = new[] { s.Add(P0, GenericCards.Mountain), s.Add(P0, GenericCards.Mountain) };
        var plotter = s.InHand(P0, Scripted("Test Plotter", "Creature — Human", "{1}{R}", "{}", "Plot {1}{R}", "2", "2"));
        bool plotted = false, castOnPlottingTurn = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (!plotted && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == plotter) is { } plot) { plotted = true; return plot; }
            if (legal.OfType<CastSpell>().Any(c => c.Card == plotter) && s.Game.State.TurnNumber == 1) castOnPlottingTurn = true;
            if (s.Game.State.TurnNumber == 3 && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == plotter) is { } cast) return cast;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Exile, s.Card(plotter).Zone);
        Assert.Equal(1, s.Card(plotter).PlottedTurn);
        Assert.All(mountains, m => Assert.True(s.Card(m).Tapped)); // the plot cost was paid
        Assert.False(castOnPlottingTurn); // not castable the turn it was plotted

        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Battlefield, s.Card(plotter).Zone);
        Assert.All(mountains, m => Assert.False(s.Card(m).Tapped)); // no mana was paid on the later turn
    }

    // ------------------------------------------------------------------ bargain (702.166)

    private static async Task<(Scenario S, CardId Token, CardId Wall, int Damage)> CastBargainSpell(bool pay)
    {
        var spell = Spell("Test Bargain Bolt", "{R}", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent),
            new IfThen(new AdditionalCostWasPaid(), new Effect[] { new DealDamage(3, Subject.TargetAt(0)) }, new Effect[] { new DealDamage(2, Subject.TargetAt(0)) })) with
        {
            OptionalAdditionalCost = BargainCost(),
        };
        var s = Quiet();
        var bolt = s.InHand(P0, spell);
        var token = s.Add(P0, Creature("Test Token", 1, 1) with { IsToken = true });
        var wall = s.Add(P1, Creature("Test Wall", 1, 3));
        int? damage = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(bolt).Zone == Zone.Graveyard) damage ??= s.Card(wall).Damage; // read before cleanup removes damage
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bolt) as PlayerAction ?? PassPriority.Instance;
        };
        s.Lands(P0, 1);
        s.Attacker.YesNo = (_, _) => pay;
        await s.RunUntilTurn(2);
        return (s, token, wall, damage ?? -1);
    }

    [Fact]
    public async Task ABargainSpellSacrificesATokenForThreeDamageWhenPaid()
    {
        var (s, token, wall, _) = await CastBargainSpell(pay: true);
        Assert.DoesNotContain(token, s.Game.State.Battlefield);
        Assert.DoesNotContain(wall, s.Game.State.Battlefield); // three damage to a 1/3
    }

    [Fact]
    public async Task ABargainSpellDealsTwoDamageWhenNotPaid()
    {
        var (s, token, wall, damage) = await CastBargainSpell(pay: false);
        Assert.Contains(token, s.Game.State.Battlefield);
        Assert.Equal(Zone.Battlefield, s.Card(wall).Zone);
        Assert.Equal(2, damage);
    }

    // ------------------------------------------------------------------ collect evidence (701.59)

    [Fact]
    public async Task CollectEvidenceExilesTheGraveyardCardsAndPaysTheOptionalCost()
    {
        var spell = Spell("Test Evidence Spell", "{1}", null, new IfThen(new AdditionalCostWasPaid(), new Effect[] { new GainLife(3, Subject.You) })) with
        {
            OptionalAdditionalCost = new ExtraCost { CollectEvidence = 6 },
        };
        var s = Quiet();
        var evidence = s.InHand(P0, spell);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == evidence) as PlayerAction ?? PassPriority.Instance;
        s.Lands(P0, 1);
        var four = PutInGraveyard(s, P0, Creature("Test Four", 3, 3) with { ManaCost = ManaCost.Parse("{3}{G}") });
        var three = PutInGraveyard(s, P0, Creature("Test Three", 2, 2) with { ManaCost = ManaCost.Parse("{2}{B}") });
        s.Attacker.YesNo = (_, _) => true;
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList(); // both cards: 4 + 3 = 7 or more
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Exile, s.Card(four).Zone);
        Assert.Equal(Zone.Exile, s.Card(three).Zone);
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life); // the spell gains life only when the cost was paid
    }

    [Fact]
    public async Task CollectEvidenceIsNotOfferedWhenTheGraveyardTotalIsTooLow()
    {
        var spell = Spell("Test Evidence Spell", "{1}", null, new IfThen(new AdditionalCostWasPaid(), new Effect[] { new GainLife(3, Subject.You) })) with
        {
            OptionalAdditionalCost = new ExtraCost { CollectEvidence = 6 },
        };
        var s = Quiet();
        var evidence = s.InHand(P0, spell);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == evidence) as PlayerAction ?? PassPriority.Instance;
        s.Lands(P0, 1);
        var three = PutInGraveyard(s, P0, Creature("Test Three", 2, 2) with { ManaCost = ManaCost.Parse("{2}{B}") });
        var two = PutInGraveyard(s, P0, Creature("Test Two", 1, 1) with { ManaCost = ManaCost.Parse("{1}{B}") });
        bool asked = false;
        s.Attacker.YesNo = (_, _) => { asked = true; return true; };
        await s.RunUntilTurn(2);
        Assert.False(asked); // 3 + 2 = 5, less than 6: no yes or no question
        Assert.Equal(Zone.Graveyard, s.Card(three).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(two).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(evidence).Zone); // the spell resolved unpaid
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ earthbend (keyword action)

    /// <summary>Earthbends a land with a red spell, then removes it with <paramref name="removal"/> (a red spell); returns the state the land had when removed.</summary>
    private static async Task<(Scenario S, CardId Land, int Power, int Toughness, bool Creature, bool Haste)> EarthbendThenRemove(Effect removal)
    {
        var s = new Scenario();
        var lands = new[] { s.Add(P0, GenericCards.Mountain), s.Add(P0, GenericCards.Mountain), s.Add(P0, GenericCards.Mountain) };
        var bend = s.InHand(P0, Spell("Test Earthbend", "{R}", new TargetSpec(TargetKind.Land, ControllerFilter.You), new Earthbend(Subject.TargetAt(0), 2)));
        var remove = s.InHand(P0, Spell("Test Removal", "{R}", new TargetSpec(TargetKind.Creature, ControllerFilter.You), removal));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(lands[0]) };
        (int Power, int Toughness, bool Creature, bool Haste) seen = default;
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bend) is { } first) return first;
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == remove) is { } second && s.Card(lands[0]).IsCreature)
            {
                var land = s.Card(lands[0]);
                seen = (land.Power, land.Toughness, land.IsCreature, land.Has(Keyword.Haste));
                return second;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(2);
        return (s, lands[0], seen.Power, seen.Toughness, seen.Creature, seen.Haste);
    }

    [Fact]
    public async Task AnEarthbentLandIsACreatureWithHasteAndReturnsTappedWhenDestroyed()
    {
        var (s, land, power, toughness, creature, haste) = await EarthbendThenRemove(new Destroy(Subject.TargetAt(0)));
        Assert.True(creature);
        Assert.True(haste);
        Assert.Equal(2, power);
        Assert.Equal(2, toughness);
        Assert.Equal(Zone.Battlefield, s.Card(land).Zone); // the reflexive trigger returned it
        Assert.True(s.Card(land).Tapped);
        Assert.True(s.Card(land).Is(CardType.Land));
        Assert.False(s.Card(land).IsCreature);
    }

    [Fact]
    public async Task AnEarthbentLandReturnsTappedWhenExiled()
    {
        var (s, land, _, _, creature, _) = await EarthbendThenRemove(new ExileIt(Subject.TargetAt(0)));
        Assert.True(creature);
        Assert.Equal(Zone.Battlefield, s.Card(land).Zone);
        Assert.True(s.Card(land).Tapped);
        Assert.True(s.Card(land).Is(CardType.Land));
        Assert.False(s.Card(land).IsCreature);
    }

    // ------------------------------------------------------------------ mayhem

    [Fact]
    public async Task ACardDiscardedThisTurnIsCastFromTheGraveyardForItsMayhemCost()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var rager = s.InHand(P0, Creature("Test Rager", 2, 2) with { Mayhem = ManaCost.Parse("{1}") });
        var sift = s.InHand(P0, Spell("Test Sift", "{R}", null, new Discard(1, Subject.You)));
        s.Attacker.Discard = (_, _) => new[] { rager };
        bool castFromGraveyard = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sift) is { } first) return first;
            if (s.Card(rager).Zone == Zone.Graveyard && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == rager) is { } mayhem)
            {
                castFromGraveyard = true;
                return mayhem;
            }
            return PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(2);
        Assert.True(castFromGraveyard);
        Assert.Equal(Zone.Battlefield, s.Card(rager).Zone);
        Assert.All(s.Game.State.Battlefield.Select(s.Card).Where(c => c.Is(CardType.Land)), land => Assert.True(land.Tapped)); // {R} and {1} paid
    }

    [Fact]
    public async Task ACardPutIntoTheGraveyardAnotherWayCannotBeCastWithMayhem()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var milled = s.InHand(P0, Creature("Test Rager", 2, 2) with { Mayhem = ManaCost.Parse("{1}") });
        var mill = s.InHand(P0, Spell("Test Mill", "{R}", null, new Mill(1, Subject.You)));
        bool restacked = false, offered = false;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, milled); } // on top of the library, so the mill puts it into the graveyard
            offered |= legal.OfType<CastSpell>().Any(c => c.Card == milled);
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == mill) as PlayerAction ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Graveyard, s.Card(milled).Zone);
        Assert.Equal(-1, s.Card(milled).DiscardedTurn);
        Assert.False(offered);
    }

    // ------------------------------------------------------------------ teamwork

    [Fact]
    public async Task ATeamworkCostTapsCreaturesOfTotalPowerWithoutCrewing()
    {
        var spell = Spell("Test Teamwork Gift", "{1}", null, new IfThen(new AdditionalCostWasPaid(), new Effect[] { new GainLife(3, Subject.You) })) with
        {
            OptionalAdditionalCost = new ExtraCost { CrewPower = 2, Teamwork = true },
        };
        var s = Quiet();
        var gift = s.InHand(P0, spell);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == gift) as PlayerAction ?? PassPriority.Instance;
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Test Bear", 2, 2));
        s.Attacker.YesNo = (_, _) => true;
        s.Attacker.Choose = (_, request) => request.Options.Select(o => o.Id).ToList();
        await s.RunUntilTurn(2);
        Assert.True(s.Card(bear).Tapped);
        Assert.DoesNotContain(s.Game.State.CrewedThisTurn, c => c.Crewer == bear); // teamwork is not crewing
        Assert.Equal(23, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ATeamworkCostIgnoresACrewPowerBonus()
    {
        var spell = Spell("Test Teamwork Gift", "{1}", null, new IfThen(new AdditionalCostWasPaid(), new Effect[] { new GainLife(3, Subject.You) })) with
        {
            OptionalAdditionalCost = new ExtraCost { CrewPower = 2, Teamwork = true },
        };
        var s = Quiet();
        var gift = s.InHand(P0, spell);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == gift) as PlayerAction ?? PassPriority.Instance;
        s.Lands(P0, 1);
        var pawn = s.Add(P0, Creature("Test Pawn", 1, 1) with { CrewPowerBonus = 2 });
        bool asked = false;
        s.Attacker.YesNo = (_, _) => { asked = true; return true; };
        await s.RunUntilTurn(2);
        Assert.False(asked); // the pawn's power is 1, the bonus doesn't count
        Assert.False(s.Card(pawn).Tapped);
        Assert.Equal(20, s.Game.State.GetPlayer(P0).Life);
    }

    // ------------------------------------------------------------------ power-up

    [Fact]
    public async Task APowerUpAbilityCostsTwoOnTheTurnTheCreatureEntersAndOnlyOnce()
    {
        var powered = Creature("Test Powered", 3, 3) with
        {
            ManaCost = ManaCost.Parse("{3}{W}{W}"),
            Abilities = new AbilityDefinition[]
            {
                new ActivatedAbility
                {
                    Cost = new AbilityCost(ManaCost.Parse("{5}{W}{W}")), PowerUp = true, OnlyOnce = true,
                    Effects = Array.Empty<Effect>(), Text = "Power-up {5}{W}{W}: activate only once.",
                },
            },
        };
        var s = new Scenario();
        var lands = new List<CardId>
        {
            s.Add(P0, GenericCards.Plains), s.Add(P0, GenericCards.Plains),
        };
        for (int i = 0; i < 7; i++) lands.Add(s.Add(P0, GenericCards.Mountain));
        var creature = s.InHand(P0, powered);
        bool activated = false, offeredAgain = false;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == creature) is { } cast) return cast;
            if (legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == creature) is { } power)
            {
                if (activated) { offeredAgain = true; return PassPriority.Instance; }
                activated = true;
                return power;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Battlefield, s.Card(creature).Zone);
        Assert.True(activated);
        Assert.False(offeredAgain); // only once, even with 2 lands left to pay for it
        Assert.Equal(7, lands.Count(id => s.Card(id).Tapped)); // 5 to cast, 2 to power up
    }

    // ------------------------------------------------------------------ return from graveyard by total mana value

    [Fact]
    public async Task ReturnFromTheGraveyardStopsAtTheTotalManaValue()
    {
        var s = new Scenario();
        s.Add(P0, GenericCards.Forest);
        var three = PutInGraveyard(s, P0, Creature("Test Three", 3, 3) with { ManaCost = ManaCost.Parse("{2}{G}") });
        var two = PutInGraveyard(s, P0, Creature("Test Two", 2, 2) with { ManaCost = ManaCost.Parse("{1}{G}") });
        var one = PutInGraveyard(s, P0, Creature("Test One", 1, 1) with { ManaCost = ManaCost.Parse("{G}") });
        var recall = s.InHand(P0, Spell("Test Recall", "{G}", null,
            new ReturnFromGraveyard(new ObjectFilter(CardType.Creature), 2, Zone.Battlefield, UpTo: true) { TotalManaValueAtMost = 4 }));
        var offers = new List<IReadOnlyList<CardId>>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == recall) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Choose = (_, request) =>
        {
            offers.Add(request.Options.Select(o => o.Id).ToList());
            return offers.Count == 1 ? new[] { three } : new[] { one };
        };
        await s.RunUntilTurn(2);
        Assert.Equal(2, offers.Count);
        Assert.Contains(two, offers[0]); // the first pick offers every card that fits in the total of 4
        Assert.DoesNotContain(two, offers[1]); // after the 3 there is 1 left, so the 2 is not offered
        Assert.Contains(one, offers[1]);
        Assert.Equal(Zone.Battlefield, s.Card(three).Zone);
        Assert.Equal(Zone.Battlefield, s.Card(one).Zone);
        Assert.Equal(Zone.Graveyard, s.Card(two).Zone);
    }
}
