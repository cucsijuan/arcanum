// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.Abilities;

/// <summary>
/// Non-mana costs: "discard a card", "sacrifice a creature" / "sacrifice another creature", "pay 2 life".
/// </summary>
/// <param name="Sacrifice">What must be sacrificed (null: nothing); the filter's controller is ignored (you control it).</param>
public sealed record ExtraCost(int Discard = 0, ObjectFilter? Sacrifice = null, int SacrificeCount = 1, int PayLife = 0)
{
    /// <summary>"Tap N untapped [filter] you control" (e.g. ten Elves).</summary>
    public ObjectFilter? TapCreatures { get; init; }
    public int TapCount { get; init; }

    /// <summary>"Pay life equal to the number of colors in your commanders' color identity" (on top of <see cref="ExtraCost.PayLife"/>).</summary>
    public bool LifeFromCommanderColors { get; init; }

    /// <summary>"Return an artifact you control to its owner's hand": one permanent matching this (the source too).</summary>
    public ObjectFilter? ReturnToHand { get; init; }

    /// <summary>"Behold a [filter]": choose one you control or reveal one from your hand (rule 701.4).</summary>
    public ObjectFilter? Behold { get; init; }

    /// <summary>"Behold a [filter] and exile it": the beheld card is exiled, linked to the permanent the spell becomes (rule 607.2).</summary>
    public bool BeholdExile { get; init; }

    /// <summary>"Behold three Elementals": how many different objects (1 when not said).</summary>
    public int BeholdCount { get; init; } = 1;

    /// <summary>"Choose a creature type and behold N creatures of that type": the type is chosen as the cost is paid (the spell's chosen type).</summary>
    public bool BeholdChooseType { get; init; }

    /// <summary>Crew N: tap untapped creatures you control with total power N or greater (rule 702.122).</summary>
    public int CrewPower { get; init; }

    /// <summary>Remove N counters from among creatures you control (or, with <see cref="RemoveCountersFrom"/>, among those permanents).</summary>
    public int RemoveCountersFromYourCreatures { get; init; }

    /// <summary>The permanents you control the counters are removed from ("remove a counter from a nonland permanent you control").</summary>
    public ObjectFilter? RemoveCountersFrom { get; init; }

    /// <summary>"Mill N cards" as a cost (it can't be paid with fewer cards in the library, rule 701.13b).</summary>
    public int Mill { get; init; }

    /// <summary>The discarded cards must match this ("discard a legendary card with the same name as …").</summary>
    public ObjectFilter? DiscardFilter { get; init; }

    /// <summary>"Exile three cards from your graveyard".</summary>
    public int ExileFromGraveyard { get; init; }

    /// <summary>The exiled cards must match this ("exile a creature card from your graveyard").</summary>
    public ObjectFilter? ExileFromGraveyardFilter { get; init; }

    /// <summary>"Put a [filter] card exiled with [this] into its owner's graveyard".</summary>
    public ObjectFilter? ReturnExiledWithSource { get; init; }

    /// <summary>Forage (rule 701.61a): "exile three cards from your graveyard or sacrifice a Food", the player's choice.</summary>
    public bool Forage { get; init; }

    /// <summary>Blight N (a keyword action): "put N -1/-1 counters on a creature you control", the creature chosen as the cost is paid.</summary>
    public int Blight { get; init; }

    /// <summary>"Blight X": the spell's X counters (<see cref="Blight"/> once X is announced).</summary>
    public bool BlightX { get; init; }

    /// <summary>"Exile a green card from your hand": one matching card other than the spell being cast, revealed as it is exiled.</summary>
    public ObjectFilter? ExileFromHand { get; init; }

    /// <summary>"Discard your hand": every card in the hand (an empty hand pays it too).</summary>
    public bool DiscardHand { get; init; }

    /// <summary>"Return two lands you control to their owner's hand": how many permanents matching <see cref="ReturnToHand"/>.</summary>
    public int ReturnToHandCount { get; init; } = 1;

    /// <summary>"Sacrifice a Swamp and a Forest": with <see cref="Sacrifice"/> (matching any of them), one different permanent for each of these.</summary>
    public IReadOnlyList<ObjectFilter>? SacrificeEachOf { get; init; }
}

/// <summary>
/// "Costs {N} less to cast": a fixed amount when <see cref="Condition"/> holds, or that amount for each permanent
/// matching <see cref="PerPermanent"/> (or each card in your graveyard matching <see cref="PerGraveyardCard"/>).
/// Only generic mana is reduced (rule 601.2f).
/// </summary>
public sealed record CostReduction(int Amount, Condition? Condition = null, ObjectFilter? PerPermanent = null, ObjectFilter? PerGraveyardCard = null)
{
    /// <summary>"Costs {N} less to cast if it targets a [filter]."</summary>
    public ObjectFilter? IfTargets { get; init; }

    /// <summary>"Costs {X} less, where X is [quantity]."</summary>
    public Quantity? AmountFrom { get; init; }

    /// <summary>"Costs {X} less, where X is the total power of creatures you control" (those matching <see cref="PowerFilter"/>).</summary>
    public bool ByTotalPower { get; init; }
    public ObjectFilter? PowerFilter { get; init; }
}

/// <summary>One way to pay a cost when there are alternatives ("sacrifice a creature or pay {3}{B}").</summary>
public sealed record CostOption(Mana.ManaCost? Mana, ExtraCost? Extra);

/// <summary>"You may pay [cost] rather than pay this spell's mana cost if [condition]".</summary>
public sealed record AlternativeCost(Mana.ManaCost Cost, Condition? If = null)
{
    /// <summary>Non-mana parts of the alternative cost ("pay {W} and tap four untapped creatures you control with flying").</summary>
    public ExtraCost? Extra { get; init; }
}

/// <summary>Riders on mana from a source (rule 106.6): what happens when it's spent on matching spells.</summary>
public enum ManaRider { None, HasteForDragonCreatureSpells, CopyRedInstantOrSorcery, LegendaryUncounterable, Uncounterable, InstantOrSorceryUncounterable, ScryIfSharesTypeWithCommander, CommanderEntersWithCastCountCounters }
