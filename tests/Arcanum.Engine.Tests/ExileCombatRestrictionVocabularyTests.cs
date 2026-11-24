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
/// Vocabulary for a delayed trigger at the beginning of the next combat this turn (gain control, untap and haste), a permanent
/// that stops fliers from blocking its controller's creatures and from attacking it, a cast trigger that gives flying to the
/// creatures a spell targets, airbend (a card exiled for its owner, who may cast it for {2}), a restriction that lets opponents
/// cast only from hand until the controller's next turn, a card that exiles cards until one gains an ability usable from exile, and
/// a mill of each target player's own graveyard count.
/// </summary>
public class ExileCombatRestrictionVocabularyTests
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

    /// <summary>A scenario in which the attacker never attacks; each test sets how it acts.</summary>
    private static Scenario Quiet()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    // ------------------------------------------------------------------ "at the beginning of the next combat this turn"

    /// <summary>Deals 1 damage to an opponent's creature, then gains control of it, untaps it and gives it haste at the next combat this turn.</summary>
    private const string SendoffScript = """
        { "spell": { "targets": [{ "kind": "creature", "controller": "opponent" }], "effects": [
            { "damage": 1, "to": "target" },
            { "atNextCombatThisTurn": "target", "effects": [
                { "gainControl": "triggered", "untilEndOfTurn": true },
                { "untap": "triggered" },
                { "pump": [0, 0], "what": "triggered", "keywords": ["Haste"] }
            ] }
        ] } }
        """;

    [Fact]
    public async Task AtTheNextCombatTheTargetIsGainedUntappedWithHasteAndGoesBackAtTheEndOfTheTurn()
    {
        var sendoff = Scripted("Test Sendoff", "Sorcery", "{R}", SendoffScript, "Invented.");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.InHand(P0, sendoff);
        var charger = s.Add(P1, Creature("Test Charger", 2, 4));
        s.Card(charger).Tapped = true;
        (bool Listed, PlayerId Controller, bool Tapped, bool Haste) seen = default;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(charger) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, attackers, defenders) =>
        {
            // The attack step comes after the beginning of combat: the creature has been gained, untapped and given haste.
            seen = (attackers.Contains(charger), s.Card(charger).Controller, s.Card(charger).Tapped, s.Card(charger).Has(Keyword.Haste));
            return attackers.Where(a => a == charger).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
        Assert.True(seen.Listed); // it could attack: haste
        Assert.Equal(P0, seen.Controller);
        Assert.False(seen.Tapped);
        Assert.True(seen.Haste);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // the 2 power attacked the opponent
        Assert.Equal(P1, s.Card(charger).Controller); // "until end of turn": control went back
    }

    [Fact]
    public async Task AnAtNextCombatEffectDoesNothingWhenTheTargetDiedFromTheSpellsDamage()
    {
        var sendoff = Scripted("Test Sendoff", "Sorcery", "{R}", SendoffScript, "Invented.");
        var s = Quiet();
        s.Lands(P0, 1);
        var card = s.InHand(P0, sendoff);
        var frail = s.Add(P1, Creature("Test Frail", 2, 1));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(frail) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Graveyard, s.Card(frail).Zone); // 1 damage on a 2/1
        Assert.DoesNotContain(frail, s.Game.State.Battlefield); // the beginning of combat gained nothing: the card is not on the battlefield
        Assert.Equal(P1, s.Card(frail).Controller);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task AnAtNextCombatEffectCastAfterCombatDoesNotFireAtAnotherTurnsCombat()
    {
        var sendoff = Scripted("Test Sendoff", "Sorcery", "{R}", SendoffScript, "Invented.");
        var s = new Scenario();
        s.Lands(P0, 1);
        var card = s.InHand(P0, sendoff);
        var charger = s.Add(P1, Creature("Test Charger", 2, 4));
        s.Add(P0, Creature("Test Ally", 1, 1)); // a possible attacker, so each declare attackers step asks
        var controllers = new List<(int Turn, PlayerId Controller)>();
        s.Attacker.Targets = (_, _) => new[] { Target.Of(charger) };
        // Cast in the second main phase of the first turn: this turn's combat is already over.
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == card && s.Game.State.Step == Step.PostcombatMain) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => { controllers.Add((s.Game.State.TurnNumber, s.Card(charger).Controller)); return Array.Empty<AttackDeclaration>(); };
        s.Defender.Attack = (_, _, _) => { controllers.Add((s.Game.State.TurnNumber, s.Card(charger).Controller)); return Array.Empty<AttackDeclaration>(); };
        await s.RunUntilTurn(4);
        Assert.Equal(Zone.Graveyard, s.Card(card).Zone);
        Assert.Contains(controllers, c => c.Turn == 3); // the next turn of the caster had its declare attackers step
        Assert.All(controllers.Where(c => c.Turn >= 2), c => Assert.Equal(P1, c.Controller)); // never gained, in turns 2 and 3
        Assert.Equal(P1, s.Card(charger).Controller);
    }

    // ------------------------------------------------------------------ "creatures with flying can't block creatures you control" / "can't attack you"

    /// <summary>A permanent whose controller's creatures can't be blocked by fliers, and fliers can't attack its controller.</summary>
    private const string WardenScript = """
        { "abilities": [{ "static": { "affects": "creatures", "filter": { "keyword": "Flying" }, "cantBlockYourCreatures": true, "cantAttackYou": true } }] }
        """;

    [Fact]
    public async Task AFlierCannotBlockTheWardensControllersCreatureWhileAGroundCreatureCan()
    {
        var warden = Scripted("Test Warden", "Enchantment", "{1}", WardenScript, "Invented.");
        var s = Quiet();
        s.Add(P0, warden);
        var bear = s.Add(P0, Creature("Test Bear", 2, 2));
        var flier = s.Add(P1, Creature("Test Flier", 1, 1, Keyword.Flying));
        var grunt = s.Add(P1, Creature("Test Grunt", 1, 1));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Where(a => a == bear).Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        await s.RunUntilTurn(2);
        var request = s.Defender.LastBlockRequest;
        Assert.NotNull(request);
        Assert.Contains(bear, request!.Attackers);
        Assert.DoesNotContain(flier, request.Blockers); // the flier can't block it, so it isn't a possible blocker at all
        Assert.Contains(grunt, request.Blockers);
        Assert.Contains(bear, request.CanBlock[grunt]); // the ground creature can
    }

    [Fact]
    public async Task AFlierCannotAttackTheWardensControllerButAGroundCreatureCan()
    {
        var warden = Scripted("Test Warden", "Enchantment", "{1}", WardenScript, "Invented.");
        var s = Quiet();
        s.Add(P0, warden);
        s.Add(P1, Creature("Test Flier", 1, 1, Keyword.Flying));
        s.Add(P1, Creature("Test Grunt", 1, 1));
        IReadOnlyList<CardId> offered = Array.Empty<CardId>();
        s.Defender.Attack = (_, attackers, defenders) =>
        {
            offered = attackers;
            return attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        };
        await s.RunUntilTurn(3);
        Assert.Single(offered); // the flier isn't a legal attacker against its controller
        Assert.Equal(19, s.Game.State.GetPlayer(P0).Life); // only the grunt attacked: 1 damage
    }

    // ------------------------------------------------------------------ cast trigger aimed at a spell's targets: the batch of creatures it targets

    /// <summary>Whenever you cast a spell that targets an opponent's creature, those creatures gain flying until end of turn.</summary>
    private const string FlyingCasterScript = """
        { "abilities": [{ "trigger": "castSpell", "spellTargets": { "types": ["creature"], "controller": "opponent" }, "effects": [
            { "pump": [0, 0], "what": "batch", "keywords": ["Flying"] }
        ] }] }
        """;

    [Fact]
    public async Task ACreatureTargetedByYourSpellGainsFlyingUntilEndOfTurnAndYourOtherCreaturesDoNot()
    {
        var caster = Scripted("Test Caster", "Enchantment", "{1}", FlyingCasterScript, "Invented.");
        var ping = Spell("Test Ping", "{R}", new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), new DealDamage(1, Subject.TargetAt(0))) with { Types = CardType.Instant };
        var s = Quiet();
        s.Lands(P0, 1);
        s.Add(P0, caster);
        var ally = s.Add(P0, Creature("Test Ally", 1, 1));
        var wall = s.Add(P1, Creature("Test Wall", 1, 5));
        var bolt = s.InHand(P0, ping);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(wall) };
        (bool WallFlies, bool AllyFlies)? seen = null;
        s.Attacker.Act = (_, legal) =>
        {
            if (s.Card(bolt).Zone == Zone.Graveyard) seen ??= (s.Card(wall).Has(Keyword.Flying), s.Card(ally).Has(Keyword.Flying)); // after the trigger and the spell resolved
            return legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bolt) as PlayerAction ?? PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.NotNull(seen);
        Assert.True(seen!.Value.WallFlies);
        Assert.False(seen.Value.AllyFlies);
        Assert.Equal(Zone.Battlefield, s.Card(wall).Zone); // 1 damage on a 1/5
        Assert.False(s.Card(wall).Has(Keyword.Flying)); // the flying ended with the turn
    }

    // ------------------------------------------------------------------ airbend: exiled for its owner, castable for {2} from exile

    /// <summary>Exiles each creature face up for its owner to cast for {2}.</summary>
    private const string SweepScript = """
        { "spell": { "effects": [{ "exile": { "each": { "types": ["creature"] } }, "airbend": true }] } }
        """;

    [Fact]
    public async Task AnAirbentCreatureCardIsExiledAndItsOwnerCastsItFromExileForTwoOnTheirTurn()
    {
        var sweep = Scripted("Test Sweep", "Sorcery", "{R}", SweepScript, "Invented.");
        var s = Quiet();
        s.Lands(P0, 1);
        s.Lands(P1, 2);
        var titan = s.Add(P1, Creature("Test Titan", 4, 4) with { ManaCost = ManaCost.Parse("{3}{G}{G}") });
        var sweepCard = s.InHand(P0, sweep);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sweepCard) as PlayerAction ?? PassPriority.Instance;
        int? castOnTurn = null;
        s.Defender.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == titan) is { } cast)
            {
                castOnTurn ??= s.Game.State.TurnNumber;
                return cast;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(3);
        Assert.Equal(2, castOnTurn); // cast on its owner's own turn, from exile
        Assert.Equal(Zone.Battlefield, s.Card(titan).Zone);
        Assert.Equal(2, s.Game.State.Battlefield.Select(s.Card).Count(c => c.Controller == P1 && c.Is(CardType.Land) && c.Tapped)); // paid {2}
    }

    [Fact]
    public async Task AnAirbentTokenCeasesToExistInsteadOfBeingExiled()
    {
        var sweep = Scripted("Test Sweep", "Sorcery", "{R}", SweepScript, "Invented.");
        var s = Quiet();
        s.Lands(P0, 1);
        var sweepCard = s.InHand(P0, sweep);
        var spirit = s.Add(P1, Creature("Test Spirit", 1, 1) with { IsToken = true });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sweepCard) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn(2);
        Assert.DoesNotContain(spirit, s.Game.State.GetPlayer(P1).Exile);
        Assert.True(s.Card(spirit).CeasedToExist);
    }

    // ------------------------------------------------------------------ "your opponents can cast spells only from their hand until your next turn"

    /// <summary>Exiles each creature face up for its owner to cast for {2}; opponents cast only from hand until the controller's next turn.</summary>
    private const string SweepRestrictedScript = """
        { "spell": { "effects": [
            { "exile": { "each": { "types": ["creature"] } }, "airbend": true },
            { "opponentsCastOnlyFromHandUntilYourNextTurn": true }
        ] } }
        """;

    [Fact]
    public async Task OpponentsCastOnlyFromHandUntilYourNextTurnKeepsAnAirbentCardFromBeingCastUntilThen()
    {
        var sweep = Scripted("Test Sweep", "Sorcery", "{R}", SweepRestrictedScript, "Invented.");
        var s = new Scenario();
        s.Lands(P0, 1);
        s.Lands(P1, 2);
        var titan = s.Add(P1, Creature("Test Titan", 4, 4) with { ManaCost = ManaCost.Parse("{3}{G}{G}") });
        var bear = s.InHand(P1, Creature("Test Hand Bear", 2, 2));
        var sweepCard = s.InHand(P0, sweep);
        var offeredOn = new List<int>();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == sweepCard) as PlayerAction ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Defender.Act = (_, legal) =>
        {
            if (legal.OfType<CastSpell>().Any(c => c.Card == titan)) offeredOn.Add(s.Game.State.TurnNumber);
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bear) is { } fromHand) return fromHand; // a card in hand is still castable
            if (legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == titan) is { } fromExile) return fromExile;
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(5);
        Assert.Equal(Zone.Battlefield, s.Card(bear).Zone); // cast from hand on the opponent's turn
        Assert.DoesNotContain(2, offeredOn); // the exiled card was not castable on its owner's turn 2
        Assert.Contains(4, offeredOn); // it is castable again after the controller's turn 3
        Assert.Equal(Zone.Battlefield, s.Card(titan).Zone);
    }

    // ------------------------------------------------------------------ exile until a card gains an ability usable from exile

    /// <summary>
    /// Exiles cards from the top of the library until a nonland card is exiled, which gains an ability: exile a card from your hand,
    /// then cast a copy of this card from exile (paying its mana cost). Usable only on the controller's turn.
    /// </summary>
    private const string GhostScoutScript = """
        { "abilities": [{ "trigger": "enters", "effects": [{ "exileUntil": { "not": ["land"] }, "gains": [{
            "cost": "exileCardFromHand", "fromExile": true, "activateIf": "yourTurn",
            "effects": [{ "castCopy": "self", "payManaCost": true }],
            "text": "Exile a card from your hand: You may cast a copy of this card from exile by paying its mana cost."
        }] }] }] }
        """;

    /// <summary>{R}: 2 damage to any target (an instant).</summary>
    private const string SparkScript = """
        { "spell": { "targets": ["any"], "effects": [{ "damage": 2, "to": "target" }] } }
        """;

    [Fact]
    public async Task ExileUntilGainsExilesThroughTheFirstNonlandAndTheFoundCardCopiesFromExileOnItsControllersTurn()
    {
        var spark = Scripted("Test Spark", "Instant", "{R}", SparkScript, "Invented.");
        var scout = Scripted("Test Ghost Scout", "Creature — Spirit", "{1}", GhostScoutScript, "Invented.", "2", "2");
        var s = Quiet();
        s.Lands(P0, 2);
        var scoutCard = s.InHand(P0, scout);
        var land1 = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var land2 = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var sparkCard = s.Game.SetupInLibrary(P0, spark);
        bool restacked = false, activated = false;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P1) };
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList(); // the hand card to exile
        s.Attacker.YesNo = (_, _) => true; // cast the copy
        s.Attacker.Act = (_, legal) =>
        {
            // The opening hand is drawn when the game starts and may have taken these cards: put them back on top of the library.
            if (!restacked) { restacked = true; s.Restack(P0, land1, land2, sparkCard); }
            if (s.Card(scoutCard).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == scoutCard) is { } cast) return cast;
            if (!activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == sparkCard) is { } ability)
            {
                activated = true;
                return ability;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Battlefield, s.Card(scoutCard).Zone);
        Assert.Equal(Zone.Exile, s.Card(land1).Zone);
        Assert.Equal(Zone.Exile, s.Card(land2).Zone);
        Assert.Equal(Zone.Exile, s.Card(sparkCard).Zone); // the first nonland card: it gains the ability and stays in exile
        Assert.True(activated);
        var handExiled = s.Game.State.GetPlayer(P0).Exile.Where(id => id != land1 && id != land2 && id != sparkCard).ToList();
        Assert.Single(handExiled); // the cost exiled one card from the hand
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // the copy was cast and dealt 2 damage
    }

    [Fact]
    public async Task TheExiledCardsAbilityIsNotOfferedOnAnOpponentsTurnButIsOfferedAgainOnALaterTurnOfItsController()
    {
        var spark = Scripted("Test Spark", "Instant", "{R}", SparkScript, "Invented.");
        var scout = Scripted("Test Ghost Scout", "Creature — Spirit", "{1}", GhostScoutScript, "Invented.", "2", "2");
        var s = Quiet();
        s.Lands(P0, 2);
        var scoutCard = s.InHand(P0, scout);
        s.InHand(P0, Creature("Test Bear", 2, 2));
        var land1 = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var land2 = s.Game.SetupInLibrary(P0, GenericCards.Mountain);
        var sparkCard = s.Game.SetupInLibrary(P0, spark);
        bool restacked = false, sawOpponentsTurn = false, offeredOnOpponentsTurn = false, activated = false;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P1) };
        s.Attacker.Choose = (_, request) => request.Options.Take(request.Max).Select(o => o.Id).ToList();
        s.Attacker.YesNo = (_, _) => true;
        s.Attacker.Act = (_, legal) =>
        {
            if (!restacked) { restacked = true; s.Restack(P0, land1, land2, sparkCard); }
            int turn = s.Game.State.TurnNumber;
            if (turn == 1 && s.Card(scoutCard).Zone == Zone.Hand && legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == scoutCard) is { } cast) return cast;
            if (turn == 2)
            {
                sawOpponentsTurn = true;
                offeredOnOpponentsTurn |= legal.OfType<ActivateAbility>().Any(a => a.Source == sparkCard);
                return PassPriority.Instance;
            }
            if (turn == 3 && !activated && legal.OfType<ActivateAbility>().FirstOrDefault(a => a.Source == sparkCard) is { } ability)
            {
                activated = true;
                return ability;
            }
            return PassPriority.Instance;
        };
        await s.RunUntilTurn(4);
        Assert.True(sawOpponentsTurn); // the controller had priority on the opponent's turn
        Assert.False(offeredOnOpponentsTurn); // "activate only if it's your turn"
        Assert.True(activated);
        Assert.Equal(Zone.Exile, s.Card(sparkCard).Zone); // still exiled, and it stays there after the copy
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life); // the copy dealt 2 damage on the controller's later turn
    }

    // ------------------------------------------------------------------ mill, a quantity of the affected player's graveyard

    /// <summary>Each target player mills as many cards as are in that player's graveyard.</summary>
    private const string CensusScript = """
        { "spell": { "targets": [{ "kind": "player", "anyNumber": true }], "effects": [
            { "mill": { "graveyard": {}, "of": "affected" }, "who": "eachTarget" }
        ] } }
        """;

    [Fact]
    public async Task EachTargetPlayerMillsTheNumberOfCardsInTheirOwnGraveyard()
    {
        var census = Scripted("Test Census", "Sorcery", "{R}", CensusScript, "Invented.");
        var s = Quiet();
        s.Lands(P0, 1);
        var spell = s.InHand(P0, census);
        PutInGraveyard(s, P0, Creature("Test Old Bear", 2, 2));
        for (int i = 0; i < 3; i++) PutInGraveyard(s, P1, Creature("Test Old Ape", 3, 3));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(P0), Target.Of(P1) };
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == spell) as PlayerAction ?? PassPriority.Instance;
        await s.RunUntilTurn(2);
        Assert.Equal(Zone.Graveyard, s.Card(spell).Zone);
        Assert.Equal(6, s.Game.State.GetPlayer(P1).Graveyard.Count); // 3 in it, 3 milled
        Assert.Equal(3, s.Game.State.GetPlayer(P0).Graveyard.Count); // 1 in it, 1 milled, and this spell
    }
}
