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

    /// <summary>Crew N: tap untapped creatures you control with total power N or greater (rule 702.122).</summary>
    public int CrewPower { get; init; }

    /// <summary>Remove N counters from among creatures you control.</summary>
    public int RemoveCountersFromYourCreatures { get; init; }

    /// <summary>The discarded cards must match this ("discard a legendary card with the same name as …").</summary>
    public ObjectFilter? DiscardFilter { get; init; }

    /// <summary>"Exile three cards from your graveyard".</summary>
    public int ExileFromGraveyard { get; init; }

    /// <summary>"Put a [filter] card exiled with [this] into its owner's graveyard".</summary>
    public ObjectFilter? ReturnExiledWithSource { get; init; }
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
public sealed record AlternativeCost(Mana.ManaCost Cost, Condition? If = null);

/// <summary>Riders on mana from a source (rule 106.6): what happens when it's spent on matching spells.</summary>
public enum ManaRider { None, HasteForDragonCreatureSpells, CopyRedInstantOrSorcery, LegendaryUncounterable, Uncounterable, InstantOrSorceryUncounterable, ScryIfSharesTypeWithCommander }
