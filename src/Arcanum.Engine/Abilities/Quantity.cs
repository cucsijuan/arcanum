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
    /// <summary>The toughness of the object the trigger event was about ("life equal to that creature's toughness").</summary>
    TriggeredToughness,
    /// <summary>Half of <see cref="Quantity.Parts"/>' single quantity, rounded down ("half the number of Zombies you control, rounded down").</summary>
    HalfRoundedDown,
    /// <summary>Devotion to the color in <see cref="Quantity.Filter"/>: the mana symbols of that color among the mana costs of permanents you control (rule 700.5).</summary>
    Devotion,
    /// <summary>Players who drew a card in an earlier draw effect of this spell or ability (opponents only with an opponent <see cref="Quantity.Filter"/>).</summary>
    PlayersWhoDrewThisWay,
    /// <summary>Opponents with more of <see cref="Quantity.Measure"/> (life, creatures, lands, cards in hand) than the controller.</summary>
    OpponentsWithMore,
    /// <summary>The greatest mana value among the commanders the controller owns, wherever they are ("among your commanders").</summary>
    GreatestCommanderManaValueAnywhere,
    /// <summary>The creatures attacking this combat that the player the trigger is about controls ("attacks with two or more creatures").</summary>
    TriggeredPlayerAttackers,
    /// <summary>Opponents the controller attacked with a creature this combat (melee, rule 702.121a).</summary>
    OpponentsAttackedThisCombat,
    /// <summary><see cref="Quantity.Parts"/>[0] minus [1] ("the difference"; an effect does nothing with a result below one).</summary>
    Difference,
    /// <summary><see cref="Quantity.Parts"/>[0] divided by <see cref="Quantity.Value"/>, rounded down ("for every seven cards").</summary>
    Per,
    /// <summary>Card types among cards in all graveyards (artifact, battle, creature, enchantment, instant, kindred, land, planeswalker, sorcery).</summary>
    CardTypesInAllGraveyards,
    /// <summary>Loyalty counters on the first target ("deals damage equal to its loyalty").</summary>
    TargetLoyalty,
    /// <summary>Different powers among creatures the controller controls.</summary>
    DistinctPowersAmongYourCreatures,
    /// <summary>The greatest mana value among cards matching <see cref="Quantity.Filter"/> in the controller's graveyard.</summary>
    GreatestManaValueInYourGraveyard,
    /// <summary>Cards put into the first target player's graveyard from their library this turn.</summary>
    TargetPlayerMilledThisTurn,
    /// <summary>Cards drawn by an earlier effect of this spell or ability ("the number of cards drawn this way").</summary>
    DrawnThisWay,
    /// <summary>Basic land types among lands the controller controls (domain).</summary>
    Domain,
    /// <summary>Cards the controller discarded this turn.</summary>
    DiscardedThisTurn,
    /// <summary>Planeswalker types among planeswalkers the controller controls.</summary>
    PlaneswalkerTypesAmongYours,
    /// <summary>Colors among permanents the controller controls that match <see cref="Quantity.Filter"/> ("each color among other artifacts you control").</summary>
    ColorsAmongYourMatching,
    /// <summary>The unspent mana in the controller's mana pool.</summary>
    UnspentMana,
    /// <summary>Opponents who discarded fewer than <see cref="Quantity.Value"/> cards matching the filter this way.</summary>
    OpponentsWhoDiscardedFewer,
    /// <summary>Colors among permanents the controller controls (W, U, B, R, G: 0 to 5).</summary>
    ColorsAmongYourPermanents,
    /// <summary>Different kinds of counters on the source ("for each kind of counter on it").</summary>
    CounterKindsOnSource,
    /// <summary>The total mana value of the cards this spell or ability exiled ("that card's mana value").</summary>
    ExiledManaValue,
    /// <summary>Players who lost life this turn.</summary>
    PlayersWhoLostLifeThisTurn,
    /// <summary>"The greatest power among creatures you controlled as you cast this spell".</summary>
    GreatestPowerAsCast,
    /// <summary>The number of counters of every kind on the object the trigger is about, as it last existed on the battlefield ("that number").</summary>
    TriggeredCounterCount,
    /// <summary>The total mana value of the permanents sacrificed by this spell or ability (as they last existed).</summary>
    SacrificedManaValue,
    /// <summary>Permanents matching the filter that entered the battlefield under your control this turn (and are still those objects there).</summary>
    EnteredThisTurnCount,
    /// <summary>Opponents who control a permanent matching the filter ("opponents who control a creature with power 4 or greater").</summary>
    OpponentsControlling,
    /// <summary>Opponents dealt combat damage this turn by the source (as this object) or by a Dragon.</summary>
    OpponentsCombatDamagedBySourceOrDragon,
    /// <summary>How far the discovered card's mana value is below the N it was discovered for (none without a discovered card).</summary>
    DiscoverShortfall,
    /// <summary>The power / toughness of the last creature card exiled with the source ("imprint").</summary>
    LastExiledCreaturePower,
    LastExiledCreatureToughness,
    /// <summary>The total mana value of the permanents matching the filter ("the total mana value of other permanents you control").</summary>
    ManaValueSum,
    /// <summary>Permanents whose control this effect gave (or took) that are still on the battlefield ("if they do").</summary>
    ControlGainedThisWay,
    /// <summary>The mana value of the last card a reveal or look found ("you lose life equal to its mana value").</summary>
    FoundManaValue,
    /// <summary>Card types among the cards this effect discarded ("a card for each card type among cards discarded this way").</summary>
    CardTypesAmongDiscarded,
    /// <summary>Creatures that died this turn ("for each creature that died this turn").</summary>
    CreaturesDiedThisTurn,
    /// <summary>The result of this effect's die roll.</summary>
    Rolled,
    /// <summary>Opponents that were dealt combat damage this turn.</summary>
    OpponentsDealtCombatDamage,
    /// <summary>Counters this effect removed ("for each lore counter removed this way").</summary>
    CountersRemovedThisWay,
    /// <summary>Cards this effect found (put into a hand, revealed …) that match the filter ("if you put a Town card into your hand this way").</summary>
    FoundMatching,
    /// <summary>The total mana value of the cards this effect discarded ("loses life equal to the discarded card's mana value").</summary>
    DiscardedManaValue,
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

    /// <summary>For <see cref="QuantityKind.GraveyardCount"/>: every player's graveyard ("in all graveyards").</summary>
    public bool OfEveryone { get; init; }

    /// <summary>For <see cref="QuantityKind.GraveyardCount"/>: the graveyards of the controller's opponents ("in your opponents' graveyards").</summary>
    public bool OfOpponents { get; init; }

    /// <summary>With <see cref="QuantityKind.GraveyardCount"/>: the graveyard of the source's controller ("your graveyard" inside "each opponent …").</summary>
    public bool OfSourceController { get; init; }

    /// <summary>With <see cref="QuantityKind.CountersAmong"/>: counters of every kind ("the number of counters among creatures you control").</summary>
    public bool AnyCounterKind { get; init; }

    /// <summary>With <see cref="QuantityKind.PermanentCount"/>: permanents controlled by the controller of the permanent the source is attached to.</summary>
    public bool ControlledByAttachedController { get; init; }

    /// <summary>For <see cref="QuantityKind.OpponentsWithMore"/>: what is compared ("life", "creatures", "lands", "cards").</summary>
    public string? Measure { get; init; }

    /// <summary>For <see cref="QuantityKind.Sum"/>: the quantities added together.</summary>
    public IReadOnlyList<Quantity>? Parts { get; init; }

    public static implicit operator Quantity(int value) => new(value);

    public static readonly Quantity X = new(0, QuantityKind.X);

    public bool IsFixed => Kind == QuantityKind.Fixed;

    /// <summary>A rough value for planning (computer players): exact when printed.</summary>
    public int Estimate => (IsFixed ? Value : 2) * Multiplier;

    public override string ToString() => IsFixed ? (Value * Multiplier).ToString() : $"{(Multiplier == -1 ? "-" : Multiplier != 1 ? Multiplier + "×" : "")}{Kind}";
}
