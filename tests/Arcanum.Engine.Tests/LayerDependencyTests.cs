// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using static Arcanum.Engine.Tests.Scenario;

namespace Arcanum.Engine.Tests;

/// <summary>
/// The order of continuous effects inside one layer (rule 613.8): an effect waits for the effects that change whether it
/// exists, what it applies to or what it does; the rest, and dependency loops, follow timestamps. Permanents get their
/// timestamps in the order they are set up.
/// </summary>
public class LayerDependencyTests
{
    private static CardDefinition Enchantment(string name, params AbilityDefinition[] abilities) => new()
    {
        Name = name, Types = CardType.Enchantment, Abilities = abilities,
    };

    private static readonly AffectedFilter EveryCreature = new(AffectedScope.AllCreatures);

    private static async Task<Scenario> Settled(Action<Scenario> setup)
    {
        var s = new Scenario();
        s.Attacker.Attack = (_, _, _) => Array.Empty<AttackDeclaration>();
        setup(s);
        await s.RunUntilTurn();
        return s;
    }

    // "All creatures lose all abilities."
    private static CardDefinition Hush() => Enchantment("Hush", new StaticAbility(EveryCreature) { LosesAllAbilities = true });

    // A creature with "Permanents you control have hexproof."
    private static CardDefinition Warden() => Creature("Warden", 2, 2) with
    {
        Abilities = new AbilityDefinition[] { new StaticAbility(new AffectedFilter(AffectedScope.YourPermanents), 0, 0, new[] { Keyword.Hexproof }) },
    };

    private static CardDefinition Relic() => new() { Name = "Relic", Types = CardType.Artifact, Power = 2, Toughness = 2 };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGrantFromACreatureThatLosesItsAbilitiesDoesntExist(bool removalIsOlder)
    {
        CardId relic = default, warden = default;
        var s = await Settled(s =>
        {
            if (removalIsOlder) s.Add(P1, Hush());
            warden = s.Add(P0, Warden());
            relic = s.Add(P0, Relic());
            if (!removalIsOlder) s.Add(P1, Hush());
        });
        // The grant depends on the removal, whichever came first: the Warden has no ability to grant anything with.
        Assert.False(s.Card(relic).Has(Keyword.Hexproof));
        Assert.False(s.Card(warden).Has(Keyword.Hexproof));
    }

    [Fact]
    public async Task TheGrantIsBackOnceTheRemovalIsGone()
    {
        CardId relic = default;
        var s = await Settled(s =>
        {
            s.Add(P0, Warden());
            relic = s.Add(P0, Relic());
        });
        Assert.True(s.Card(relic).Has(Keyword.Hexproof));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEffectOnAllCreaturesSeesThePermanentsAnotherMakesCreatures(bool creatureEffectIsOlder)
    {
        // "Each noncreature artifact is a creature in addition to its other types" and "All creatures are Angels in addition to their other types".
        var animator = Enchantment("Animator", new StaticAbility(new AffectedFilter(AffectedScope.AllPermanents))
        {
            Filter = new ObjectFilter(CardType.Artifact, ExcludedTypes: CardType.Creature), AddTypes = CardType.Creature,
        });
        var choir = Enchantment("Choir", new StaticAbility(EveryCreature) { AddSubtypes = new[] { "Angel" } });
        CardId relic = default;
        var s = await Settled(s =>
        {
            if (creatureEffectIsOlder) s.Add(P0, choir);
            s.Add(P0, animator);
            relic = s.Add(P1, Relic());
            if (!creatureEffectIsOlder) s.Add(P0, choir);
        });
        Assert.True(s.Card(relic).IsCreature);
        Assert.True(s.Card(relic).HasSubtype("Angel")); // the Choir waits for the Animator even when it is the older one
    }

    [Theory]
    [InlineData(true, 2, 5, 1, 3)]
    [InlineData(false, 4, 7, 3, 5)]
    public async Task ADependencyLoopFollowsTimestamps(bool growthIsOlder, int smallPower, int smallToughness, int bigPower, int bigToughness)
    {
        // "Creatures with power 2 or less get +2/+2" and "Creatures with power 3 or greater get -2/-2": each changes what the other
        // applies to (the first moves a creature into the second's range, the second one into the first's).
        var growth = Enchantment("Growth", new StaticAbility(EveryCreature, 2, 2) { Filter = new ObjectFilter(MaxPower: 2) });
        var decay = Enchantment("Decay", new StaticAbility(EveryCreature, -2, -2) { Filter = new ObjectFilter(MinPower: 3) });
        CardId small = default, big = default;
        var s = await Settled(s =>
        {
            s.Add(P0, growthIsOlder ? growth : decay);
            s.Add(P0, growthIsOlder ? decay : growth);
            small = s.Add(P0, Creature("Small", 2, 5));
            big = s.Add(P0, Creature("Big", 3, 5));
        });
        Assert.Equal((smallPower, smallToughness), (s.Card(small).Power, s.Card(small).Toughness));
        Assert.Equal((bigPower, bigToughness), (s.Card(big).Power, s.Card(big).Toughness));
    }

    [Fact]
    public async Task TheOrderIsWorkedOutAgainAfterEachEffect()
    {
        // Two "all creatures get +1/+0" around "creatures with power 4 or greater get +1/+1", and a 2/2. Neither bonus alone brings
        // the creature to 4, so nothing depends on anything at first; once the older bonus is applied, the newer one would, and the
        // effect that looks at power now waits for it.
        var first = Enchantment("First Drum", new StaticAbility(EveryCreature, 1, 0));
        var mighty = Enchantment("Mighty", new StaticAbility(EveryCreature, 1, 1) { Filter = new ObjectFilter(MinPower: 4) });
        var second = Enchantment("Second Drum", new StaticAbility(EveryCreature, 1, 0));
        CardId bear = default;
        var s = await Settled(s =>
        {
            s.Add(P0, first);
            s.Add(P0, mighty);
            s.Add(P0, second);
            bear = s.Add(P0, Creature("Bear", 2, 2));
        });
        Assert.Equal((5, 3), (s.Card(bear).Power, s.Card(bear).Toughness));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADependencyBeatsTimestamps(bool dependentIsOlder)
    {
        // "Creatures with power 3 or greater have base power and toughness 1/1" and "All creatures have base power and toughness 5/5".
        // The first applies to the 2/2 only once the second has, so it comes second even when it is the older one.
        var shrink = Enchantment("Shrink", new StaticAbility(EveryCreature) { Filter = new ObjectFilter(MinPower: 3), SetPower = 1, SetToughness = 1 });
        var giants = Enchantment("Giants", new StaticAbility(EveryCreature) { SetPower = 5, SetToughness = 5 });
        CardId bear = default;
        var s = await Settled(s =>
        {
            s.Add(P0, dependentIsOlder ? shrink : giants);
            s.Add(P0, dependentIsOlder ? giants : shrink);
            bear = s.Add(P0, Creature("Bear", 2, 2));
        });
        Assert.Equal((1, 1), (s.Card(bear).Power, s.Card(bear).Toughness));
    }

    [Fact]
    public async Task IndependentEffectsStillFollowTimestamps()
    {
        // Two effects that set base power and toughness outright: the newer one wins.
        CardId bear = default;
        var s = await Settled(s =>
        {
            s.Add(P0, Enchantment("Giants", new StaticAbility(EveryCreature) { SetPower = 5, SetToughness = 5 }));
            s.Add(P0, Enchantment("Dwarves", new StaticAbility(EveryCreature) { SetPower = 1, SetToughness = 1 }));
            bear = s.Add(P0, Creature("Bear", 2, 2));
        });
        Assert.Equal((1, 1), (s.Card(bear).Power, s.Card(bear).Toughness));
    }

    // ------------------------------------------------------------------ rule 613.6: an effect keeps its objects in later layers

    [Fact]
    public async Task AnEffectThatMakesCreaturesSetsThePowerOfThoseItMade()
    {
        // "Each noncreature artifact is a 2/2 artifact creature": after layer 4 the artifact is no longer a noncreature artifact,
        // but the same effect still sets its power and toughness in layer 7b.
        var animator = Enchantment("Animator", new StaticAbility(new AffectedFilter(AffectedScope.AllPermanents))
        {
            Filter = new ObjectFilter(CardType.Artifact, ExcludedTypes: CardType.Creature), AddTypes = CardType.Creature, SetPower = 2, SetToughness = 2,
        });
        CardId idol = default;
        var s = await Settled(s =>
        {
            s.Add(P0, animator);
            idol = s.Add(P1, new CardDefinition { Name = "Idol", Types = CardType.Artifact });
        });
        Assert.True(s.Card(idol).IsCreature);
        Assert.Equal((2, 2), (s.Card(idol).Power, s.Card(idol).Toughness));
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 4, 1)]
    public async Task AnEffectKeepsApplyingAfterItsAbilityIsLost(bool meeknessIsOlder, int meeknessPower, int bearPower)
    {
        // "Each other non-Aura enchantment is a creature with base power and toughness 4/4" makes Meekness a creature; Meekness's
        // "All creatures lose all abilities and have base power and toughness 1/1" then takes its own ability away in layer 6, but
        // having started to apply, it still sets base power and toughness in layer 7b, in timestamp order with the 4/4.
        var mirror = Enchantment("Mirror", new StaticAbility(new AffectedFilter(AffectedScope.AllPermanents))
        {
            Filter = new ObjectFilter(CardType.Enchantment, ExcludedSubtype: "Aura") { Other = true }, AddTypes = CardType.Creature, SetPower = 4, SetToughness = 4,
        });
        var meekness = Enchantment("Meekness", new StaticAbility(EveryCreature) { LosesAllAbilities = true, SetPower = 1, SetToughness = 1 });
        CardId meek = default, bear = default;
        var s = await Settled(s =>
        {
            if (meeknessIsOlder) meek = s.Add(P0, meekness);
            s.Add(P0, mirror);
            if (!meeknessIsOlder) meek = s.Add(P0, meekness);
            bear = s.Add(P1, Creature("Bear", 3, 3));
        });
        Assert.True(s.Card(meek).IsCreature);
        Assert.Equal((meeknessPower, meeknessPower), (s.Card(meek).Power, s.Card(meek).Toughness));
        Assert.Equal((bearPower, bearPower), (s.Card(bear).Power, s.Card(bear).Toughness));
    }

    [Fact]
    public async Task AnEffectGrantingAbilitiesKeepsItsCreaturesForItsBonus()
    {
        // "Creatures with power 2 or less have flying and get +2/+0" starts to apply in layer 6 to the 2/2; "All creatures have base
        // power and toughness 5/5" makes it a 5/5 in layer 7b, and the bonus still goes to the same creature in layer 7c.
        CardId bear = default;
        var s = await Settled(s =>
        {
            s.Add(P0, Enchantment("Wings", new StaticAbility(EveryCreature, 2, 0, new[] { Keyword.Flying }) { Filter = new ObjectFilter(MaxPower: 2) }));
            s.Add(P0, Enchantment("Giants", new StaticAbility(EveryCreature) { SetPower = 5, SetToughness = 5 }));
            bear = s.Add(P0, Creature("Bear", 2, 2));
        });
        Assert.True(s.Card(bear).Has(Keyword.Flying));
        Assert.Equal((7, 5), (s.Card(bear).Power, s.Card(bear).Toughness));
    }
}
