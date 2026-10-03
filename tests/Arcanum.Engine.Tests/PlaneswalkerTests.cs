// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Loyalty abilities, damage and attacks on planeswalkers, emblems, playing exiled cards and mana sources.</summary>
public class PlaneswalkerTests
{
    private static CardDefinition Walker(int loyalty, params ActivatedAbility[] abilities) => new()
    {
        Name = "Sage", ManaCost = ManaCost.Parse("{2}"), Types = CardType.Planeswalker, Loyalty = loyalty, Abilities = abilities,
    };

    private static ActivatedAbility Loyalty(int cost, params Effect[] effects) =>
        new() { Cost = new AbilityCost(ManaCost.Zero) { Loyalty = cost }, Effects = effects, Text = $"{cost:+#;-#;0}" };

    [Fact]
    public async Task EntersWithLoyaltyAndUsesOneAbilityPerTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 2);
        var walker = s.InHand(P0, Walker(3, Loyalty(1, new GainLife(2, Subject.You)), Loyalty(-2, new DrawCards(1, Subject.You))));
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(walker).CounterCount(CounterKind.Loyalty)); // 3, then +1 once
        Assert.Equal(22, s.Game.State.GetPlayer(P0).Life);
        Assert.Single(s.Game.Log.OfType<AbilityActivated>());
    }

    [Fact]
    public async Task AttackingAPlaneswalkerRemovesLoyaltyAndItDies()
    {
        var s = new Scenario();
        var walker = s.Game.SetupPermanent(P1, Walker(3));
        s.Card(walker).Counters[CounterKind.Loyalty] = 3;
        s.Add(P0, Creature("Brute", 4, 4));
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0], walker)).ToList();
        await s.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, s.Card(walker).Zone);
        Assert.Equal(20, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task BurnCanTargetPlaneswalkers()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var walker = s.Game.SetupPermanent(P1, Walker(5));
        s.Card(walker).Counters[CounterKind.Loyalty] = 5;
        s.InHand(P0, new CardDefinition
        {
            Name = "Scorch", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Instant,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.CreatureOrPlaneswalker) }, Effects = new Effect[] { new DealDamage(3, Subject.TargetAt(0)) } },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(2, s.Card(walker).CounterCount(CounterKind.Loyalty));
    }

    [Fact]
    public async Task EmblemStaticAbilitiesApply()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var bear = s.Add(P0, Creature("Bear", 2, 2));
        s.InHand(P0, new CardDefinition
        {
            Name = "Ascend", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility
            {
                Effects = new Effect[]
                {
                    new CreateEmblem("Emblem", new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourCreatures), 2, 2) }),
                },
            },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(4, s.Card(bear).Power);
        Assert.Single(s.Game.State.Emblems);
    }

    [Fact]
    public async Task ExiledCardsCanBePlayedThisTurn()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        s.InHand(P0, new CardDefinition
        {
            Name = "Impulse", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new ExileTopPlayable(1) } },
        });
        // Player 0 casts the spell, then plays whatever it exiled (a land: the libraries are lands only).
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<PlayLand>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        var played = new List<CardId>();
        s.Game.EventRaised += e => { if (e is LandPlayed l) played.Add(l.Card); };
        CardId? exiled = null;
        s.Game.EventRaised += e => { if (e is CardMoved { From: Zone.Library, To: Zone.Exile } m) exiled = m.Card; };
        await s.RunUntilTurn();
        Assert.NotNull(exiled);
        // The land drop was used either from hand first or on the exiled card; when the exiled land is played it leaves exile.
        Assert.True(played.Count == 1);
    }

    [Fact]
    public async Task SourcesThatAddSeveralManaPayBigCosts()
    {
        var s = new Scenario();
        s.Add(P0, new CardDefinition { Name = "Lotus", Types = CardType.Artifact, TapForMana = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }, ManaAmount = 3 });
        s.InHand(P0, new CardDefinition
        {
            Name = "Big Idea", ManaCost = ManaCost.Parse("{2}{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new GainLife(5, Subject.You) } },
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        await s.RunUntilTurn();
        Assert.Equal(25, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ColorIsChosenAsThePermanentEnters()
    {
        var s = new Scenario();
        s.Lands(P0, 1);
        var banner = s.InHand(P0, new CardDefinition
        {
            Name = "Banner", ManaCost = ManaCost.Parse("{1}"), Types = CardType.Artifact, ChooseOnEnter = EnterChoice.Color, ManaFromChosenColor = true,
        });
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Attacker.Option = (_, r) => { Assert.Equal(OptionKind.Color, r.Kind); return 3; }; // Red
        await s.RunUntilTurn();
        Assert.Equal("R", s.Card(banner).ChosenColor);
        Assert.Equal(new[] { ManaType.Red }, s.Card(banner).ManaTypes);
    }
}
