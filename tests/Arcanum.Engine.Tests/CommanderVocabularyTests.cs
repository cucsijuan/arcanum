// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Vocabulary for lasting restrictions, mana and attacks: a condition on opponents' lands, no -1/-1 counters on your
/// creatures, token mana costs, a life lock, unspent mana becoming colorless, attack triggers, casting from a graveyard
/// once per player, cost reductions from the command zone, planeswalker attack bans, goads for the rest of the game,
/// and mana among the colors of your permanents.
/// </summary>
public class CommanderVocabularyTests
{
    private static CardDefinition TargetedSorcery(string name, TargetSpec target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery, Spell = new SpellAbility { Targets = new[] { target }, Effects = effects },
    };

    /// <summary>Player 0 casts whatever it can, the first copy of each spell in hand; never attacks.</summary>
    private static Scenario Casting()
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public void EntersTappedUnlessCountsTheLandsOpponentsControl()
    {
        var script = CardScriptParser.Parse("""{ "entersTappedUnless": { "opponentsControlTotal": { "types": ["land"] }, "count": 3 } }""");
        var condition = Assert.IsType<OpponentsControlTotal>(script.EntersTappedUnless);
        Assert.Equal(3, condition.AtLeast);
        Assert.Equal(CardType.Land, condition.Filter.Types);
    }

    [Fact]
    public void YourCreaturesCantHaveMinusCountersIsACardFlag()
    {
        var script = CardScriptParser.Parse("""{ "yourCreaturesCantHaveMinusCounters": true }""");
        Assert.True(script.YourCreaturesCantHaveMinusCounters);
    }

    [Theory]
    [InlineData(true, 0)]  // your creatures can't have -1/-1 counters put on them: the bear gets none
    [InlineData(false, 1)] // without it: the counter is put on your bear
    public async Task YourCreaturesCantHaveMinusCountersStopsThemOnYourCreatures(bool ban, int expected)
    {
        var s = Casting();
        s.Lands(P0, 1);
        if (ban) s.Add(P0, new CardDefinition { Name = "Stone Warden", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment, YourCreaturesCantHaveMinusCounters = true });
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, TargetedSorcery("Withering Note", new TargetSpec(TargetKind.Creature, ControllerFilter.You), new AddCounters(1, Subject.TargetAt(0), CounterKind.MinusOneMinusOne)));
        await s.RunUntilTurn();
        Assert.Equal(expected, s.Card(bear).CounterCount(CounterKind.MinusOneMinusOne));
    }

    [Fact]
    public void TokenManaCostIsParsedFromTheToken()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [{ "tokens": 1, "token": { "name": "Biscuit Golem", "types": "Artifact Creature — Golem", "power": 1, "toughness": 1, "manaCost": "{1}{R}" } }] } }
            """);
        var token = Assert.IsType<CreateTokens>(script.Spell!.Effects[0]).Token;
        Assert.Equal(2, token.ManaCost.ManaValue);
        Assert.Equal(new[] { ManaType.Red }, token.ManaCost.Pips);
    }

    [Fact]
    public void ProtectionAndLifeLockParsesToTheSubjectItLocks()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "protectionAndLifeLock": "you" }] } }""");
        var effect = Assert.IsType<ProtectionAndLifeLockUntilTheirTurn>(script.Spell!.Effects[0]);
        Assert.Equal(Subject.You, effect.Who);
    }

    [Fact]
    public async Task ProtectionAndLifeLockStopsYourLifeTotalChangingUntilYourNextTurn()
    {
        var s = Casting();
        s.Lands(P0, 1);
        s.InHand(P0, new CardDefinition
        {
            Name = "Lifeward Vow", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new ProtectionAndLifeLockUntilTheirTurn(Subject.You) } },
        });
        await s.RunUntilTurn(2);
        Assert.True(s.Game.State.GetPlayer(P0).LifeCantChange);
        Assert.False(s.Game.State.GetPlayer(P1).LifeCantChange);
    }

    [Fact]
    public void UnspentManaIsAQuantity()
    {
        using var doc = JsonDocument.Parse("\"unspentMana\"");
        var quantity = CardScriptParser.ParseQuantity(doc.RootElement);
        Assert.Equal(QuantityKind.UnspentMana, quantity.Kind);
    }

    [Fact]
    public void UnspentManaBecomesColorlessIsACardFlag()
    {
        var script = CardScriptParser.Parse("""{ "unspentManaBecomesColorless": true }""");
        Assert.True(script.UnspentManaBecomesColorless);
    }

    [Fact]
    public void YouAttackWithParsesToATriggerWithItsFilter()
    {
        var script = CardScriptParser.Parse("""{ "abilities": [{ "trigger": "youAttackWith", "filter": { "types": ["creature"], "minPower": 3 }, "effects": [{ "gainLife": 1 }], "text": "x" }] }""");
        var ability = Assert.IsType<TriggeredAbility>(script.Abilities[0]);
        Assert.Equal(TriggerEvent.YouAttackWithMatching, ability.Trigger);
        Assert.Equal(CardType.Creature, ability.Filter!.Types);
        Assert.Equal(3, ability.Filter.MinPower);
    }

    [Fact]
    public void CastFromGraveyardPerPlayerAndNoExileParseAsFlags()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "castFromGraveyard": { "types": ["instant"] }, "perPlayer": true, "noExile": true }] } }""");
        var cast = Assert.IsType<CastFromGraveyardNow>(script.Spell!.Effects[0]);
        Assert.True(cast.PerPlayer);
        Assert.True(cast.NoExile);
    }

    [Fact]
    public void CastFromGraveyardFlagsAreOffWhenAbsent()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "castFromGraveyard": { "types": ["instant"] } }] } }""");
        var cast = Assert.IsType<CastFromGraveyardNow>(script.Spell!.Effects[0]);
        Assert.False(cast.PerPlayer);
        Assert.False(cast.NoExile);
    }

    [Fact]
    public void SpellCostFromTheCommandZoneParsesAsAFlag()
    {
        var script = CardScriptParser.Parse("""{ "abilities": [{ "spellCost": { "spells": { "types": ["creature"] }, "amount": 1, "fromCommandZone": true }, "text": "x" }] }""");
        var reduction = Assert.IsType<SpellCostReduction>(script.Abilities[0]);
        Assert.True(reduction.FromCommandZone);
        Assert.Equal(1, reduction.Amount);
        Assert.Equal(CardType.Creature, reduction.Spells.Types);
    }

    [Fact]
    public void CantAttackYourPlaneswalkersParsesTheSubtypeAndWhoCantAttack()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "cantAttackYourPlaneswalkers": "opponents", "subtype": "Vexmaster" }] } }""");
        var effect = Assert.IsType<CantAttackYourPlaneswalkersThisTurn>(script.Spell!.Effects[0]);
        Assert.Equal(Subject.EachOpponent, effect.Who);
        Assert.Equal("Vexmaster", effect.Subtype);
    }

    [Fact]
    public void GoadForRestOfGameParsesAsAFlag()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "goad": "target", "forRestOfGame": true }] } }""");
        var goad = Assert.IsType<Goad>(script.Spell!.Effects[0]);
        Assert.True(goad.ForRestOfGame);
        Assert.Equal(Subject.TargetAt(0), goad.What);
    }

    [Fact]
    public void EachToADifferentOpponentParses()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "effects": [{ "eachToADifferentOpponent": "target" }] } }""");
        var effect = Assert.IsType<EachToADifferentOpponent>(script.Spell!.Effects[0]);
        Assert.Equal(Subject.TargetAt(0), effect.What);
    }

    [Theory]
    [InlineData(true, true)]   // a goad for the rest of the game is still on the creature after the goader's next turn
    [InlineData(false, false)] // one until the goader's next turn is gone by then
    public async Task AGoadForTheRestOfTheGameOutlastsTheGoadersNextTurn(bool forRestOfGame, bool stillGoaded)
    {
        var s = Casting();
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        s.InHand(P0, TargetedSorcery("Endless Taunt", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), new Goad(Subject.TargetAt(0)) { ForRestOfGame = forRestOfGame }));
        // The goaded bear must attack on its controller's turn: the declaration has it attack the goader.
        s.Defender.Attack = (view, _, _) => view.AttackRequest!.Complete(new[] { new AttackDeclaration(bear, P0) });
        await s.RunUntilTurn(4);
        Assert.Equal(stillGoaded, s.Game.State.GoadsForever.Any(g => g.Card == bear && g.Goader == P0));
        Assert.DoesNotContain(s.Game.State.Goads, g => g.Card == bear);
    }

    [Fact]
    public void ExtraManaColorsAmongMatchingPermanentsParses()
    {
        var script = CardScriptParser.Parse("""{ "extraMana": [{ "colorsAmong": { "types": ["creature", "planeswalker"], "supertype": "legendary" } }] }""");
        var option = Assert.Single(script.ExtraMana!);
        Assert.True(option.ColorsAmongYourPermanents);
        Assert.NotNull(option.ColorsAmongMatching);
        Assert.Equal(CardType.Creature | CardType.Planeswalker, option.ColorsAmongMatching!.Types);
        Assert.Equal(Supertype.Legendary, option.ColorsAmongMatching.Supertype);
    }
}
