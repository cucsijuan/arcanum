// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Cards;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>Each generic card used by the demo sandbox does what its text says.</summary>
public class GenericCardsTests
{
    private static Scenario Casting(Target? target = null)
    {
        var s = new Scenario();
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? legal.OfType<ActivateAbility>().Cast<PlayerAction>().FirstOrDefault()
                                       ?? PassPriority.Instance;
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        if (target is { } t) s.Attacker.Targets = (_, _) => new[] { t };
        return s;
    }

    [Fact]
    public async Task AuraEquipmentAndBannerChangeTheBoard()
    {
        var s = new Scenario();
        var cub = s.Add(P0, Creature("Cub", 2, 2));
        var other = s.Add(P0, Creature("Other", 1, 1));
        s.Game.SetupPermanent(P0, GenericCards.StoneSkin, attachTo: cub);
        s.Game.SetupPermanent(P0, GenericCards.IronBlade, attachTo: cub);
        s.Add(P0, GenericCards.RallyBanner);
        s.Add(P1, Creature("Rival", 2, 2));
        await s.RunUntilTurn();

        Assert.Equal(2 + 2 + 2 + 1, s.Card(cub).Power);
        Assert.Equal(2 + 2 + 1, s.Card(cub).Toughness);
        Assert.Equal(2, s.Card(other).Power);
        Assert.Equal(2, s.Card(other).Toughness);
    }

    [Fact]
    public async Task EquipAttachesTheBladeToYourCreature()
    {
        var s = Casting();
        var cub = s.Add(P0, Creature("Cub", 2, 2));
        var blade = s.Add(P0, GenericCards.IronBlade);
        s.Add(P0, GenericCards.Mountain);
        s.Attacker.Targets = (_, _) => new[] { Target.Of(cub) };
        await s.RunUntilTurn();

        Assert.Equal(cub, s.Card(blade).AttachedTo);
        Assert.Equal(4, s.Card(cub).Power);
    }

    [Fact]
    public async Task EmberBoltDealsThreeDamage()
    {
        var s = Casting(Target.Of(P1));
        s.Add(P0, GenericCards.Mountain);
        s.InHand(P0, GenericCards.EmberBolt);
        await s.RunUntilTurn();
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life);
    }

    [Fact]
    public async Task MightySurgeGivesPlusThreeUntilEndOfTurn()
    {
        var s = new Scenario();
        var cub = s.Add(P0, Creature("Cub", 2, 2));
        s.Add(P0, GenericCards.Forest);
        s.InHand(P0, GenericCards.MightySurge);
        s.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        s.Attacker.Targets = (_, _) => new[] { Target.Of(cub) };
        s.Attacker.Attack = (_, attackers, defenders) => attackers.Select(a => new AttackDeclaration(a, defenders[0])).ToList();
        int powerWhenAttacking = 0;
        s.Game.EventRaised += e => { if (e is AttackerDeclared a && a.Attacker == cub) powerWhenAttacking = s.Card(cub).Power; };
        await s.RunUntilTurn();

        Assert.Equal(5, powerWhenAttacking);
        Assert.Equal(2, s.Card(cub).Power);
    }

    [Fact]
    public async Task RendDestroysTheTarget()
    {
        var rival = new Scenario();
        var victim = rival.Add(P1, Creature("Victim", 2, 2));
        rival.Attacker.Act = (_, legal) => legal.OfType<CastSpell>().Cast<PlayerAction>().FirstOrDefault() ?? PassPriority.Instance;
        rival.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        rival.Attacker.Targets = (_, _) => new[] { Target.Of(victim) };
        rival.Add(P0, GenericCards.Swamp);
        rival.Add(P0, GenericCards.Swamp);
        rival.Add(P0, GenericCards.Swamp);
        rival.InHand(P0, GenericCards.Rend);
        await rival.RunUntilTurn();
        Assert.Equal(Zone.Graveyard, rival.Card(victim).Zone);
    }

    [Fact]
    public async Task GustAwayReturnsTheTargetToHand()
    {
        CardId victim;
        var s = Casting();
        victim = s.Add(P1, Creature("Victim", 2, 2));
        s.Attacker.Targets = (_, _) => new[] { Target.Of(victim) };
        s.Add(P0, GenericCards.Island);
        s.InHand(P0, GenericCards.GustAway);
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(victim).Zone);
    }
}
