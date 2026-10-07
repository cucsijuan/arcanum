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

/// <summary>Nonmodal double-faced cards (rule 712) and transforming (rule 701.27).</summary>
public class TransformTests
{
    private static ActivatedAbility TransformSelf(string cost, bool tap = false) => new()
    {
        Cost = new AbilityCost(ManaCost.Parse(cost), Tap: tap), Effects = new Effect[] { new Transform(Subject.Self) }, Text = $"{cost}: Transform this creature.",
    };

    private static readonly CardDefinition Brute = new()
    {
        Name = "Howling Brute", Types = CardType.Creature, Subtypes = new[] { "Wolf", "Horror" }, Power = 4, Toughness = 4, Colors = new[] { "R" },
        Keywords = new[] { "Trample" },
    };

    /// <summary>A {1}{G} 2/2 whose back face is a red 4/4 with trample; "{1}: Transform" on both faces unless given other abilities.</summary>
    private static CardDefinition Pup(CardDefinition? back = null, params AbilityDefinition[] front) => new()
    {
        Name = "Moonlit Pup", ManaCost = ManaCost.Parse("{1}{G}"), Types = CardType.Creature, Subtypes = new[] { "Wolf" }, Power = 2, Toughness = 2,
        Abilities = front.Length > 0 ? front : new AbilityDefinition[] { TransformSelf("{1}") },
        BackFace = back ?? Brute with { Abilities = new AbilityDefinition[] { TransformSelf("{1}") } },
    };

    private static CardDefinition Spell(string name, string cost, TargetSpec? target, params Effect[] effects) => new()
    {
        Name = name, ManaCost = ManaCost.Parse(cost), Types = CardType.Sorcery,
        Spell = new SpellAbility { Targets = target is null ? Array.Empty<TargetSpec>() : new[] { target }, Effects = effects },
    };

    /// <summary>Activates abilities (up to <paramref name="activations"/>) before casting spells; never attacks.</summary>
    private static Scenario Acting(int activations = 1)
    {
        var s = new Scenario();
        int used = 0;
        s.Attacker.Act = (_, legal) =>
        {
            if (used < activations && legal.OfType<ActivateAbility>().FirstOrDefault() is { } activate) { used++; return activate; }
            return legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        };
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        return s;
    }

    [Fact]
    public async Task ADoubleFacedCardIsCastWithItsFrontFaceAndEntersFrontFaceUp()
    {
        var s = Acting(activations: 0);
        s.Add(P0, GenericCards.Forest);
        s.Add(P0, GenericCards.Forest);
        var pup = s.InHand(P0, Pup());
        Assert.Equal("Moonlit Pup", s.Card(pup).Name);
        Assert.Equal(2, s.Card(pup).ManaValue);
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellCast c && c.Card == pup);
        Assert.Equal(Zone.Battlefield, s.Card(pup).Zone);
        Assert.False(s.Card(pup).Transformed);
        Assert.Equal("Moonlit Pup", s.Card(pup).Name);
        Assert.Equal(2, s.Card(pup).Power);
        Assert.Equal(new[] { "G" }, s.Card(pup).Colors);
    }

    [Fact]
    public async Task TheBackFaceCantBeCast()
    {
        // A front face too expensive to cast, and a back face without a mana cost: only the front face is ever offered (712.11).
        var s = Acting(activations: 0);
        s.Lands(P0, 2);
        var card = s.InHand(P0, Pup() with { ManaCost = ManaCost.Parse("{5}{G}{G}") });
        bool offered = false;
        var act = s.Attacker.Act;
        s.Attacker.Act = (view, legal) => { offered |= legal.OfType<CastSpell>().Any(c => c.Card == card); return act(view, legal); };
        await s.RunUntilTurn();
        Assert.False(offered);
        Assert.Equal(Zone.Hand, s.Card(card).Zone);
    }

    [Fact]
    public async Task ATransformedPermanentHasItsBackFacesCharacteristicsAndItsFrontFacesManaValue()
    {
        var s = Acting();
        s.Lands(P0, 1);
        var pup = s.Add(P0, Pup());
        await s.RunUntilTurn();
        var card = s.Card(pup);
        Assert.True(card.Transformed);
        Assert.Equal("Howling Brute", card.Name);
        Assert.Equal(4, card.Power);
        Assert.Equal(4, card.Toughness);
        Assert.Equal(new[] { "R" }, card.Colors);
        Assert.True(card.Has(Keyword.Trample));
        Assert.True(card.HasSubtype("Horror"));
        Assert.Equal(ManaCost.Zero.ToString(), card.Definition.ManaCost.ToString());
        Assert.Equal(2, card.ManaValue); // 712.8e: the front face's mana cost
        var view = s.Game.ViewFor(P0).FindCard(pup)!;
        Assert.True(view.IsBackFace);
        Assert.Equal("Howling Brute", view.Name);
        Assert.Equal("Moonlit Pup", view.OtherFace?.Name);
        Assert.False(view.OtherFace!.IsBackFace);
    }

    [Fact]
    public void ADoubleFacedCardInAHandShowsItsBackFaceOnlyAsItsOtherFace()
    {
        var s = new Scenario();
        var pup = s.InHand(P0, Pup());
        var view = Views.ViewBuilder.Card(s.Game.State, pup, P0);
        Assert.Equal("Moonlit Pup", view.Name);
        Assert.False(view.IsBackFace);
        Assert.Equal("Howling Brute", view.OtherFace?.Name);
        Assert.True(view.OtherFace!.IsBackFace);
        Assert.Equal(4, view.OtherFace.Power);
    }

    [Fact]
    public async Task TransformingKeepsTheSameObjectWithItsCountersDamageAttachmentsAndTappedStatus()
    {
        var gainOnEnter = new TriggeredAbility { Trigger = TriggerEvent.EntersBattlefield, Effects = new Effect[] { new GainLife(5, Subject.You) }, Text = "When this enters, gain 5 life." };
        var gainOnTransform = new TriggeredAbility { Trigger = TriggerEvent.Transforms, Effects = new Effect[] { new GainLife(3, Subject.You) }, Text = "When this transforms, gain 3 life." };
        var s = Acting();
        var pup = s.Add(P0, Pup(Brute with { Abilities = new AbilityDefinition[] { gainOnEnter, gainOnTransform } }, TransformSelf("{0}", tap: true)));
        var veil = s.Game.SetupPermanent(P0, new CardDefinition
        {
            Name = "Thin Veil", Types = CardType.Enchantment, Subtypes = new[] { "Aura" }, EnchantTarget = new TargetSpec(TargetKind.Creature),
        }, pup);
        // "Whenever a creature you control transforms, gain 1 life."
        s.Add(P0, new CardDefinition
        {
            Name = "Moon Watcher", Types = CardType.Enchantment,
            Abilities = new AbilityDefinition[]
            {
                new TriggeredAbility
                {
                    Trigger = TriggerEvent.Transforms, Filter = new ObjectFilter(CardType.Creature), Effects = new Effect[] { new GainLife(1, Subject.You) },
                    Text = "Whenever a creature you control transforms, gain 1 life.",
                },
            },
        });
        s.Card(pup).Counters[CounterKind.PlusOnePlusOne] = 1;
        s.Card(pup).Damage = 1;
        int version = s.Card(pup).Version;
        (int Version, bool Tapped, int Counters, int Damage, CardId? Veil, bool Controlled)? seen = null;
        s.Game.EventRaised += e =>
        {
            if (e is PermanentTransformed t && t.Card == pup)
            {
                var c = s.Card(pup);
                seen = (c.Version, c.Tapped, c.CounterCount(CounterKind.PlusOnePlusOne), c.Damage, s.Card(veil).AttachedTo, c.ControlledSinceTurnStart);
            }
        };
        await s.RunUntilTurn();
        Assert.NotNull(seen);
        Assert.Equal((version, true, 1, 1, (CardId?)pup, true), seen!.Value);
        Assert.Equal(5, s.Card(pup).Power); // the back face's 4/4 with its +1/+1 counter
        Assert.DoesNotContain(s.Game.Log, e => e is CardMoved m && m.Card == pup);
        Assert.Equal(24, s.Game.State.GetPlayer(P0).Life); // transform triggers (3 + 1); no enters trigger
    }

    [Fact]
    public async Task AnAbilityOfAPermanentDoesntTransformItAgainAfterItTransformedSinceTheAbilityWasPutOnTheStack()
    {
        // Two "{1}: Transform" activations on the stack at once: the first transforms it, the second does nothing (701.27f).
        var s = Acting(activations: 2);
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup());
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.Count(e => e is AbilityActivated a && a.Source == pup));
        Assert.Single(s.Game.Log, e => e is PermanentTransformed t && t.Card == pup);
        Assert.True(s.Card(pup).Transformed);
        Assert.Equal(1, s.Card(pup).TransformCount);
    }

    [Fact]
    public async Task ASpellCanTransformAPermanentEachTimeItSaysTo()
    {
        var s = Acting(activations: 0);
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup());
        s.InHand(P0, Spell("Moon Flicker", "{R}", new TargetSpec(TargetKind.Creature), new Transform(Subject.TargetAt(0))));
        s.InHand(P0, Spell("Moon Flicker", "{R}", new TargetSpec(TargetKind.Creature), new Transform(Subject.TargetAt(0))));
        await s.RunUntilTurn();
        Assert.Equal(2, s.Game.Log.Count(e => e is PermanentTransformed t && t.Card == pup));
        Assert.False(s.Card(pup).Transformed);
        Assert.Equal("Moonlit Pup", s.Card(pup).Name);
    }

    [Fact]
    public async Task APermanentThatIsntDoubleFacedDoesntTransform()
    {
        var s = Acting(activations: 0);
        s.Lands(P0, 1);
        var bear = s.Add(P1, Creature("Plain Bear", 2, 2));
        s.InHand(P0, Spell("Moon Flicker", "{R}", new TargetSpec(TargetKind.Creature), new Transform(Subject.TargetAt(0))));
        await s.RunUntilTurn();
        Assert.Contains(s.Game.Log, e => e is SpellResolved);
        Assert.DoesNotContain(s.Game.Log, e => e is PermanentTransformed);
        Assert.False(s.Card(bear).Transformed);
        Assert.Equal("Plain Bear", s.Card(bear).Name);
    }

    [Fact]
    public async Task LeavingTheBattlefieldTurnsItFrontFaceUpAgain()
    {
        var s = Acting();
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup());
        s.InHand(P0, Spell("Moon Doom", "{R}", new TargetSpec(TargetKind.Creature), new Destroy(Subject.TargetAt(0))));
        await s.RunUntilTurn();
        var card = s.Card(pup);
        Assert.Equal(Zone.Graveyard, card.Zone);
        Assert.False(card.Transformed);
        Assert.Equal("Moonlit Pup", card.Name);
        Assert.Equal(2, card.Power);
        Assert.Equal(new[] { "G" }, card.Colors);
        Assert.Equal(2, card.ManaValue);
    }

    [Fact]
    public async Task ACardPutOntoTheBattlefieldTransformedEntersBackFaceUpAndOtherCardsStayWhereTheyAre()
    {
        static TriggeredAbility ReturnTransformed() => new()
        {
            Trigger = TriggerEvent.Dies, Effects = new Effect[] { new PutOntoBattlefield(Subject.Self) { Transformed = true } },
            Text = "When this dies, return it to the battlefield transformed.",
        };
        var s = Acting(activations: 0);
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup(Brute, ReturnTransformed()));
        var bear = s.Add(P0, Creature("Plain Bear", 2, 2) with { Abilities = new AbilityDefinition[] { ReturnTransformed() } });
        s.InHand(P0, Spell("Moonfall", "{1}{R}", null, new Destroy(Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any)))));
        await s.RunUntilTurn();
        Assert.Equal(Zone.Battlefield, s.Card(pup).Zone);
        Assert.True(s.Card(pup).Transformed);
        Assert.Equal("Howling Brute", s.Card(pup).Name);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone); // 712.14a
    }

    [Theory]
    [InlineData(1UL)] [InlineData(2UL)] [InlineData(3UL)] [InlineData(4UL)]
    public async Task BotsCastDoubleFacedCardsAndChooseWhetherToTransformThem(ulong seed)
    {
        // "At the beginning of your upkeep, you may transform this creature" on both faces, and spells that transform creatures.
        var upkeepFlip = new TriggeredAbility
        {
            Trigger = TriggerEvent.YourUpkeep, Effects = new Effect[] { new MayDo("Transform this creature?", new Effect[] { new Transform(Subject.Self) }) },
            Text = "At the beginning of your upkeep, you may transform this creature.",
        };
        var pup = Pup(Brute with { Abilities = new AbilityDefinition[] { upkeepFlip } }, upkeepFlip) with { ManaCost = ManaCost.Parse("{1}{R}") };
        var flicker = Spell("Moon Flicker", "{R}", new TargetSpec(TargetKind.Creature), new Transform(Subject.TargetAt(0))) with { Types = CardType.Instant };
        IReadOnlyList<CardDefinition> Deck() => Decks.Of((GenericCards.Mountain, 17), (pup, 15), (flicker, 8));
        var a = new Bots.BotController(P0);
        var b = new Bots.BotController(P1);
        var game = new Game(new GameConfig { Seed = seed }, new[] { new PlayerSetup("Bot A", a, Deck()), new PlayerSetup("Bot B", b, Deck()) });
        Func<CardId, CardDefinition?> rules = id => game.State.Cards.TryGetValue(id, out var c) ? c.Definition : null;
        a.UseCardRules(rules);
        b.UseCardRules(rules);
        await game.RunWithTimeout(20);
        Assert.True(game.State.IsGameOver);
        Assert.Contains(game.Log, e => e is SpellCast c && game.State.GetCard(c.Card).PrintedDefinition.Name == "Moonlit Pup");
        Assert.Contains(game.Log, e => e is PermanentTransformed);
    }

    [Fact]
    public async Task ACopyOfATransformedPermanentCopiesTheBackFaceWithManaValueZero()
    {
        var s = Acting();
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup());
        var mimic = s.InHand(P0, Creature("Mimic", 0, 0) with { EntersAsCopyOf = new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any) });
        s.Attacker.Choose = (_, request) => request.Options.Take(Math.Max(1, request.Min)).Select(c => c.Id).ToList();
        await s.RunUntilTurn();
        Assert.True(s.Card(pup).Transformed);
        var copy = s.Card(mimic);
        Assert.Equal(Zone.Battlefield, copy.Zone);
        Assert.Equal("Howling Brute", copy.Name);
        Assert.Equal(4, copy.Power);
        Assert.Equal(0, copy.ManaValue); // 712.8e
        Assert.False(copy.IsDoubleFaced); // a copy of a double-faced permanent can't transform (701.27c)
        Assert.Null(s.Game.ViewFor(P0).FindCard(mimic)!.OtherFace);
    }

    [Fact]
    public async Task ATokenCopyOfATransformedPermanentIsDoubleFacedAndEntersBackFaceUp()
    {
        var s = Acting();
        s.Lands(P0, 2);
        var pup = s.Add(P0, Pup());
        s.InHand(P0, Spell("Moon Echo", "{R}", new TargetSpec(TargetKind.Creature), new CreateTokenCopy(Subject.TargetAt(0), 1)));
        await s.RunUntilTurn();
        var token = s.Game.State.PermanentsControlledBy(P0).Single(c => c.PrintedDefinition.IsToken);
        Assert.True(token.IsDoubleFaced); // 707.8a
        Assert.True(token.Transformed);
        Assert.Equal("Howling Brute", token.Name);
        Assert.True(token.Definition.IsToken);
        Assert.Equal(0, token.ManaValue);
        Assert.Equal("Moonlit Pup", token.PrintedDefinition.Name);
        Assert.True(s.Card(pup).Transformed);
    }
}
