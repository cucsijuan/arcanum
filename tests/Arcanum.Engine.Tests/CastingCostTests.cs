// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// Paying for spells (rule 601.2b, 601.2f-h): a spell is offered exactly when one of the ways to pay for it can be paid,
/// and casting it pays the same total cost; announcements come before targets.
/// </summary>
public class CastingCostTests
{
    /// <summary>Player 0 tries to cast <paramref name="name"/> once, at the first priority in <paramref name="step"/> where it's offered.</summary>
    private static (Scenario Scenario, Func<bool> Offered) CastOnceIn(Step step, string name)
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        bool offered = false, tried = false;
        s.Attacker.Act = (view, legal) =>
        {
            var cast = legal.OfType<CastSpell>().FirstOrDefault(c => view.FindCard(c.Card)?.Name == name);
            if (cast is not null) offered = true;
            if (tried || view.Step != step || cast is null) return PassPriority.Instance;
            tried = true;
            return cast;
        };
        return (s, () => offered);
    }

    private static CardDefinition Permanent(string name, params AbilityDefinition[] abilities) => new()
    {
        Name = name, ManaCost = ManaCost.Parse("{1}"), Types = CardType.Enchantment, Abilities = abilities,
    };

    private static int TappedLands(Scenario s, PlayerId p) =>
        s.Game.State.PermanentsControlledBy(p).Count(c => c.Is(CardType.Land) && c.Tapped);

    [Fact]
    public async Task DashingAtInstantSpeedStillPaysTheExtraCostForFlash()
    {
        var (s, _) = CastOnceIn(Step.Upkeep, "Raider");
        s.Lands(P0, 3);
        var raider = s.InHand(P0, Creature("Raider", 3, 3) with
        {
            ManaCost = ManaCost.Parse("{5}"), Dash = ManaCost.Parse("{R}"), FlashExtraCost = ManaCost.Parse("{2}"),
        });
        s.Attacker.Discard = (view, count) => view.Self.Hand.Where(c => c.Id != raider).Take(count).Select(c => c.Id).ToList();
        bool tappedThree = false;
        s.Game.EventRaised += e => { if (e is Events.SpellResolved r && r.Card == raider) tappedThree = TappedLands(s, P0) == 3; };
        await s.RunUntilTurn();
        Assert.Empty(s.Game.FailedActions);
        Assert.True(tappedThree); // {R} for dash plus {2} for flash
        Assert.Equal(Zone.Hand, s.Card(raider).Zone); // dashed: back to its owner's hand at the end step
    }

    [Fact]
    public async Task DashingWithoutTheManaForTheFlashExtraCostIsNotOffered()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 2);
        var raider = s.InHand(P0, Creature("Raider", 3, 3) with
        {
            ManaCost = ManaCost.Parse("{5}"), Dash = ManaCost.Parse("{R}"), FlashExtraCost = ManaCost.Parse("{2}"),
        });
        // Only the upkeep matters: in the main phase it's castable for its dash cost alone.
        s.Attacker.Act = (view, legal) =>
        {
            if (view.Step == Step.Upkeep && legal.OfType<CastSpell>().Any(c => c.Card == raider)) Assert.Fail("Offered without the mana for flash.");
            return PassPriority.Instance;
        };
        await s.RunUntilTurn();
        Assert.Equal(Zone.Hand, s.Card(raider).Zone);
    }

    [Fact]
    public async Task AnAlternativeCostAtInstantSpeedStillPaysTheExtraCostForFlash()
    {
        var (s, _) = CastOnceIn(Step.Upkeep, "Cheap Bolt");
        s.Lands(P0, 3);
        s.InHand(P0, new CardDefinition
        {
            Name = "Cheap Bolt", ManaCost = ManaCost.Parse("{6}"), Types = CardType.Sorcery, FlashExtraCost = ManaCost.Parse("{2}"),
            AlternativeCost = new AlternativeCost(ManaCost.Parse("{R}")),
            Spell = new SpellAbility { Effects = new Effect[] { new DealDamage(2, Subject.EachOpponent) } },
        });
        await s.RunUntilTurn();
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(3, TappedLands(s, P0));
    }

    [Fact]
    public async Task ASpellThatMayBeCastAsThoughItHadFlashDoesNotPayTheExtraCostForFlash()
    {
        var (s, offered) = CastOnceIn(Step.Upkeep, "Ambusher");
        s.Lands(P0, 1);
        s.Add(P0, Permanent("Herald", new SpellCostReduction(new ObjectFilter(CardType.Creature), 0) { GrantsFlash = true }));
        var ambusher = s.InHand(P0, Creature("Ambusher", 2, 2) with { FlashExtraCost = ManaCost.Parse("{2}") });
        await s.RunUntilTurn();
        Assert.True(offered());
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(Zone.Battlefield, s.Card(ambusher).Zone);
    }

    [Fact]
    public async Task AnXSpellThatMayBeCastFromHandForFreeIsOfferedWithoutMana()
    {
        var (s, offered) = CastOnceIn(Step.PrecombatMain, "Blast");
        s.Add(P0, Permanent("Open Mind") with { Replaces = Replacements.CastFromHandFree });
        var blast = s.InHand(P0, new CardDefinition
        {
            Name = "Blast", ManaCost = ManaCost.Parse("{X}{R}{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new DealDamage(Quantity.X, Subject.EachOpponent), new GainLife(1, Subject.You) } },
        });
        await s.RunUntilTurn();
        Assert.True(offered());
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(Zone.Graveyard, s.Card(blast).Zone);
        Assert.Equal(21, s.Game.State.GetPlayer(P0).Life);
    }

    [Fact]
    public async Task ACostIncreaseAppliesToADashCost()
    {
        var (s, offered) = CastOnceIn(Step.PrecombatMain, "Raider");
        s.Lands(P0, 1);
        s.Add(P1, Permanent("Toll", new SpellCostIncrease(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any), 1)));
        var raider = s.InHand(P0, Creature("Raider", 3, 3) with { ManaCost = ManaCost.Parse("{5}"), Dash = ManaCost.Parse("{R}") });
        await s.RunUntilTurn();
        Assert.False(offered()); // {1}{R} with one land
        Assert.Equal(Zone.Hand, s.Card(raider).Zone);
    }

    [Fact]
    public async Task ACostIncreaseAppliesToASpellCastWithoutPayingItsManaCost()
    {
        var (s, _) = CastOnceIn(Step.PrecombatMain, "Bolt");
        s.Lands(P0, 1);
        s.Add(P0, Permanent("Open Mind") with { Replaces = Replacements.CastFromHandFree });
        s.Add(P1, Permanent("Toll", new SpellCostIncrease(new ObjectFilter(CardType.Sorcery, Controller: ControllerFilter.Any), 1)));
        s.InHand(P0, new CardDefinition
        {
            Name = "Bolt", ManaCost = ManaCost.Parse("{3}{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new DealDamage(2, Subject.EachOpponent) } },
        });
        await s.RunUntilTurn();
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(18, s.Game.State.GetPlayer(P1).Life);
        Assert.Equal(1, TappedLands(s, P0)); // the {1} increase
    }

    [Fact]
    public async Task ACostReductionReducesTheGenericManaChosenForX()
    {
        var (s, _) = CastOnceIn(Step.PrecombatMain, "Blast");
        s.Lands(P0, 2);
        s.Add(P0, Permanent("Focus", new SpellCostReduction(new ObjectFilter(CardType.Sorcery), 2)));
        s.InHand(P0, new CardDefinition
        {
            Name = "Blast", ManaCost = ManaCost.Parse("{X}{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new DealDamage(Quantity.X, Subject.EachOpponent) } },
        });
        await s.RunUntilTurn();
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(17, s.Game.State.GetPlayer(P1).Life); // X = 3: {3}{R} minus {2} is {1}{R}
    }

    [Fact]
    public async Task AnnouncementsComeBeforeTargetsAndPayment()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 7);
        var bear = s.Add(P1, Creature("Bear", 2, 2));
        var order = s.InHand(P0, new CardDefinition
        {
            Name = "Raid Order", ManaCost = ManaCost.Parse("{4}{R}"), Types = CardType.Sorcery,
            Dash = ManaCost.Parse("{R}"), Kicker = ManaCost.Parse("{1}"),
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        });
        var requests = new List<string>();
        bool cast = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (cast || legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == order) is not { } spell) return PassPriority.Instance;
            cast = true;
            return spell;
        };
        s.Attacker.YesNo = (_, request) =>
        {
            requests.Add(request.Prompt.Contains("dash") ? "dash" : request.Prompt.Contains("kicker") ? "kicker" : request.Prompt);
            return true;
        };
        s.Attacker.Targets = (view, request) => { requests.Add("targets"); return TestController.FirstAllowed(view, request); };
        s.Attacker.Pay = (_, request) => { requests.Add("payment"); return request.SuggestedTaps; };
        await s.RunUntilTurn();
        Assert.Equal(new[] { "dash", "kicker", "targets", "payment" }, requests);
        Assert.Equal(Zone.Graveyard, s.Card(bear).Zone);
        Assert.Equal(2, TappedLands(s, P0)); // dash {R} and kicker {1}
    }

    [Fact]
    public async Task AChosenActionThatCannotBeCarriedOutIsReported()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        var bolt = s.InHand(P0, new CardDefinition
        {
            Name = "Bolt", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Effects = new Effect[] { new DealDamage(2, Subject.EachOpponent) } },
        });
        bool tried = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (tried || legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == bolt) is not { } spell) return PassPriority.Instance;
            tried = true;
            // The position changes behind the engine's back after it listed the action: the cast can't be paid.
            foreach (var land in s.Game.State.PermanentsControlledBy(P0).Where(c => c.Is(CardType.Land))) land.Tapped = true;
            return spell;
        };
        await s.RunUntilTurn();
        Assert.Single(s.Game.FailedActions);
        Assert.Equal(Zone.Hand, s.Card(bolt).Zone);
    }

    [Fact]
    public async Task BackingOutOfTargetsIsNotReported()
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        s.Lands(P0, 1);
        s.Add(P1, Creature("Bear", 2, 2));
        var kill = s.InHand(P0, new CardDefinition
        {
            Name = "Kill", ManaCost = ManaCost.Parse("{R}"), Types = CardType.Sorcery,
            Spell = new SpellAbility { Targets = new[] { new TargetSpec(TargetKind.Creature) }, Effects = new Effect[] { new Destroy(Subject.TargetAt(0)) } },
        });
        bool tried = false;
        s.Attacker.Act = (view, legal) =>
        {
            if (tried || legal.OfType<CastSpell>().FirstOrDefault(c => c.Card == kill) is not { } spell) return PassPriority.Instance;
            tried = true;
            return spell;
        };
        s.Attacker.Targets = (_, _) => null;
        await s.RunUntilTurn();
        Assert.Empty(s.Game.FailedActions);
        Assert.Equal(Zone.Hand, s.Card(kill).Zone);
    }
}
