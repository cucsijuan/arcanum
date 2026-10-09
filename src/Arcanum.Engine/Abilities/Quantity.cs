// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

public enum QuantityKind
{
    /// <summary>A printed number.</summary>
    Fixed,
    /// <summary>The value chosen for X when the spell was cast (or the ability activated).</summary>
    X,
    /// <summary>The number of permanents matching <see cref="Quantity.Filter"/> ("for each Goblin you control").</summary>
    PermanentCount,
    /// <summary>The number of cards in your graveyard matching <see cref="Quantity.Filter"/>.</summary>
    GraveyardCount,
    /// <summary>The source's power.</summary>
    SourcePower,
    /// <summary>The power of the target at <see cref="Quantity.Index"/>.</summary>
    TargetPower,
    /// <summary>The toughness of the target at <see cref="Quantity.Index"/>.</summary>
    TargetToughness,
    /// <summary>The mana value of the target at <see cref="Quantity.Index"/>.</summary>
    TargetManaValue,
    /// <summary>The life the controller gained this turn.</summary>
    LifeGainedThisTurn,
    /// <summary>The controller's life total.</summary>
    YourLife,
    /// <summary>The number of cards in the controller's hand.</summary>
    HandSize,
    /// <summary>The amount the trigger event was about ("that much life", "that many counters").</summary>
    TriggerAmount,
    /// <summary>The power of the object the trigger event was about.</summary>
    TriggeredPower,
    /// <summary>The number of matching permanents among the attacking creatures ("for each attacking creature").</summary>
    AttackingCount,
    /// <summary>The number of <see cref="Quantity.Counter"/> counters on the source.</summary>
    SourceCounters,
    /// <summary>The number of cards in opponents' graveyards.</summary>
    OpponentsGraveyardCount,
    /// <summary>The greatest power among other creatures you control.</summary>
    GreatestOtherPower,
    /// <summary>Toughness / power of the permanent sacrificed by this spell or ability (cost or effect).</summary>
    SacrificedToughness,
    SacrificedPower,
    /// <summary>Life lost by players through this effect ("life lost this way").</summary>
    LifeLostThisWay,
    /// <summary>Cards put into graveyards from libraries by this effect that match the filter.</summary>
    MilledThisWay,
    /// <summary>Permanents destroyed by this effect.</summary>
    DestroyedThisWay,
    /// <summary>Damage dealt beyond lethal to the creature by this effect.</summary>
    ExcessDamage,
    /// <summary>Cards exiled by this effect that match the filter.</summary>
    ExiledThisWay,
    /// <summary>The number of different mana values among matching permanents you control.</summary>
    DistinctManaValues,
    /// <summary>The greatest mana value among the permanents you control matching the filter ("the greatest mana value among artifacts you control").</summary>
    GreatestManaValue,
    /// <summary>Cards this effect found and moved ("when a creature is put onto the battlefield this way"): those taken from the top of the library.</summary>
    FoundThisWay,
    /// <summary>Colors of the spell or object the trigger was about.</summary>
    TriggeredColors,
    /// <summary>Spells matching the filter the controller cast this turn.</summary>
    SpellsCastThisTurn,

    /// <summary>Spells matching the filter you cast this turn before the spell that triggered the ability ("each other … you've cast before it this turn").</summary>
    SpellsCastBeforeTriggered,
    /// <summary>Cards discarded by this effect that match the filter.</summary>
    DiscardedThisWay,
    /// <summary>Graveyards with at least <see cref="Quantity.Value"/> cards in them.</summary>
    GraveyardsWithAtLeast,
    /// <summary>Mana spent to cast the spell the trigger was about.</summary>
    ManaSpent,
    /// <summary>Permanents returned to their owners' hands by this effect.</summary>
    ReturnedThisWay,
    /// <summary>The power of the creature the source is attached to.</summary>
    AttachedPower,
    /// <summary>The source's toughness.</summary>
    SourceToughness,
    /// <summary>The greatest power / toughness among creatures you control.</summary>
    GreatestPower,
    GreatestToughness,
    /// <summary>The greatest number of permanents matching the filter one opponent controls.</summary>
    GreatestAmongOpponents,
    /// <summary>Counters of <see cref="Quantity.Counter"/> kind among permanents matching the filter.</summary>
    CountersAmong,
    /// <summary>Total mana value of the other spells the controller cast this turn.</summary>
    ManaValueOfOtherSpellsThisTurn,
    /// <summary>Total mana value of the cards milled by this effect.</summary>
    MilledManaValue,
    /// <summary>Permanents tapped by this effect.</summary>
    TappedThisWay,
    /// <summary>Times the Ring has tempted the controller.</summary>
    RingLevel,
    /// <summary>Auras and Equipment this effect attached.</summary>
    AttachedThisWay,
    /// <summary>The power of the controller's Ring-bearer.</summary>
    RingBearerPower,
    /// <summary>The sum of <see cref="Quantity.Parts"/> ("the number of creatures you control plus the number of Foods you control").</summary>
    Sum,
    /// <summary>The number of opponents the controller has.</summary>
    OpponentCount,
    /// <summary>Total power of your attacking creatures matching the filter ("their total power").</summary>
    AttackingPower,
    /// <summary>Cards in all players' hands.</summary>
    CardsInAllHands,
    /// <summary>Damage dealt to the target player at <see cref="Quantity.Index"/> this turn.</summary>
    DamageTakenThisTurn,
    /// <summary>The greatest power among creatures the target player at <see cref="Quantity.Index"/> controls.</summary>
    TargetPlayersGreatestPower,
    /// <summary>The greatest mana value of a commander the controller owns on the battlefield or in the command zone.</summary>
    GreatestCommanderManaValue,
    /// <summary>Other attacking creatures that share a creature type with the creature a trigger is about.</summary>
    OtherAttackersSharingTypeWithTriggered,
    /// <summary>Times the source's multikicker / squad cost was paid.</summary>
    TimesKicked,
    SquadPaid,
    /// <summary>Votes for the option numbered <see cref="Quantity.Value"/>.</summary>
    VotesFor,
    /// <summary>Votes the player being affected received.</summary>
    VotesReceived,
    /// <summary>Opponents who voted for a choice the controller didn't vote for (from the votes a trigger is about).</summary>
    OpponentsVotedOtherwise,
    /// <summary>Cards in the hand of the player an effect is being applied to ("the number of cards in their hand").</summary>
    AffectedHandSize,
    /// <summary>Permanents sacrificed this turn by all players.</summary>
    PermanentsSacrificedThisTurn,
    /// <summary>Permanents sacrificed by this effect ("for each creature sacrificed this way").</summary>
    SacrificedThisWay,
    /// <summary>The power of the Army amassed by this effect.</summary>
    AmassedPower,
    /// <summary>Other creatures on the battlefield that share a creature type with the object a static ability is affecting ("for each other creature … that shares at least one creature type with it").</summary>
    OtherCreaturesSharingTypeWithAffected,
    /// <summary>Half the cards in the library of the player being affected (or else the controller's), rounded down.</summary>
    HalfLibrary,
    /// <summary>Half of <see cref="Quantity.Parts"/>[0], rounded up (rule 107.1a: "round up each time").</summary>
    HalfRoundedUp,
    /// <summary>The mana value of the object a static ability is being applied to ("equal to its mana value").</summary>
    AffectedManaValue,
    /// <summary>The power / toughness of the card a reveal found, as it was when revealed ("equal to its power").</summary>
    FoundPower,
    FoundToughness,
}

/// <summary>
/// A number an effect uses: printed ("3"), or worked out on resolution ("X", "for each creature you control",
/// "equal to its power"), times <see cref="Multiplier"/> (use -1 for "-X/-X").
/// </summary>
public sealed record Quantity(int Value, QuantityKind Kind = QuantityKind.Fixed, ObjectFilter? Filter = null, int Multiplier = 1, int Index = 0,
    CounterKind Counter = CounterKind.PlusOnePlusOne, int Offset = 0)
{
    /// <summary>For <see cref="QuantityKind.PermanentCount"/>: count only what the target player at <see cref="Index"/> controls.</summary>
    public bool ControlledByTarget { get; init; }

    /// <summary>For <see cref="QuantityKind.PermanentCount"/>: count only what the player the trigger was about controls.</summary>
    public bool ControlledByTriggeredPlayer { get; init; }

    /// <summary>For <see cref="QuantityKind.GraveyardCount"/>: the graveyard of the player the effect is affecting ("in that player's graveyard").</summary>
    public bool OfAffectedPlayer { get; init; }

    /// <summary>For <see cref="QuantityKind.Sum"/>: the quantities added together.</summary>
    public IReadOnlyList<Quantity>? Parts { get; init; }

    public static implicit operator Quantity(int value) => new(value);

    public static readonly Quantity X = new(0, QuantityKind.X);

    public bool IsFixed => Kind == QuantityKind.Fixed;

    /// <summary>A rough value for planning (computer players): exact when printed.</summary>
    public int Estimate => (IsFixed ? Value : 2) * Multiplier;

    public override string ToString() => IsFixed ? (Value * Multiplier).ToString() : $"{(Multiplier == -1 ? "-" : Multiplier != 1 ? Multiplier + "×" : "")}{Kind}";
}
