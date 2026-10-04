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
    /// <summary>Colors of the spell or object the trigger was about.</summary>
    TriggeredColors,
    /// <summary>Spells matching the filter the controller cast this turn.</summary>
    SpellsCastThisTurn,

    /// <summary>Spells matching the filter you cast this turn before the spell that triggered the ability ("each other … you've cast before it this turn").</summary>
    SpellsCastBeforeTriggered,
}

/// <summary>
/// A number an effect uses: printed ("3"), or worked out on resolution ("X", "for each creature you control",
/// "equal to its power"), times <see cref="Multiplier"/> (use -1 for "-X/-X").
/// </summary>
public sealed record Quantity(int Value, QuantityKind Kind = QuantityKind.Fixed, ObjectFilter? Filter = null, int Multiplier = 1, int Index = 0,
    CounterKind Counter = CounterKind.PlusOnePlusOne, int Offset = 0)
{
    public static implicit operator Quantity(int value) => new(value);

    public static readonly Quantity X = new(0, QuantityKind.X);

    public bool IsFixed => Kind == QuantityKind.Fixed;

    /// <summary>A rough value for planning (computer players): exact when printed.</summary>
    public int Estimate => (IsFixed ? Value : 2) * Multiplier;

    public override string ToString() => IsFixed ? (Value * Multiplier).ToString() : $"{(Multiplier == -1 ? "-" : Multiplier != 1 ? Multiplier + "×" : "")}{Kind}";
}
