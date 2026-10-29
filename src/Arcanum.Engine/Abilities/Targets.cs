// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Abilities;

/// <summary>What kind of object or player a target must be.</summary>
public enum TargetKind
{
    /// <summary>"Any target": a creature, player or planeswalker (rule 115.4).</summary>
    Any,
    Creature,
    Player,
    Permanent,
    Artifact,
    Enchantment,
    Land,
    /// <summary>A spell on the stack.</summary>
    Spell,
    Planeswalker,
    /// <summary>"Target creature or planeswalker".</summary>
    CreatureOrPlaneswalker,
    /// <summary>"Target player or planeswalker".</summary>
    PlayerOrPlaneswalker,
    /// <summary>A spell or an activated/triggered ability on the stack (a filter applies to the spells).</summary>
    SpellOrAbility,
    /// <summary>An activated or triggered ability on the stack ("target activated or triggered ability you control").</summary>
    Ability,
    /// <summary>"Target spell or nonland permanent": a spell on the stack or a permanent.</summary>
    SpellOrPermanent,
    /// <summary>A spell or a creature permanent ("target spell or creature"; a filter applies to both).</summary>
    SpellOrCreature,
    /// <summary>"Target spell, nonland permanent, or card in a graveyard" (the filter applies to the spells and permanents; any graveyard card).</summary>
    SpellPermanentOrGraveyardCard,
    /// <summary>A card in a graveyard ("target creature card from your graveyard": controller filter = owner).</summary>
    GraveyardCard,
    /// <summary>A card in exile ("target creature card exiled with [this]": controller filter = owner).</summary>
    ExiledCard,
    /// <summary>A card in a graveyard or in exile ("target instant or sorcery card from your graveyard or exiled card with flashback you own").</summary>
    GraveyardOrExiledCard,
}

/// <summary>Restriction on who controls the target ("target creature you control", "target opponent"...).</summary>
public enum ControllerFilter { Any, You, Opponent }

/// <summary>One target requirement of a spell or ability.</summary>
/// <param name="Filter">Further requirements ("artifact or enchantment", "with flying", "tapped"...); its controller is ignored.</param>
/// <param name="Optional">"Up to one target ...": the player may choose no object (<see cref="Target.None"/>).</param>
public sealed record TargetSpec(TargetKind Kind, ControllerFilter Controller = ControllerFilter.Any, ObjectFilter? Filter = null, bool Optional = false)
{
    /// <summary>"Any number of target ..." (only as the last requirement): the player picks as many different ones as they like (at least one unless optional).</summary>
    public bool AnyNumber { get; init; }

    /// <summary>The rule between this mode's targets (a mode's own "targetRule"), from <see cref="ModeStart"/> on.</summary>
    public TargetRule ModeRule { get; init; }
    public int ModeStart { get; init; }

    /// <summary>The chosen object must be attached to the target chosen for requirement N ("Equipment attached to that creature").</summary>
    public int? AttachedToTarget { get; init; }

    /// <summary>For stack objects: only ones with a single target.</summary>
    public bool SingleTargetOnly { get; init; }

    /// <summary>For abilities on the stack: only triggered ones ("counter target triggered ability or colorless spell").</summary>
    public bool TriggeredAbilitiesOnly { get; init; }

    /// <summary>The controller must be the player the trigger event was about ("that player controls").</summary>
    public bool ControlledByTriggeredPlayer { get; init; }

    /// <summary>"Target creature that's attacking that player": attacking the player the trigger is about (not a planeswalker).</summary>
    public bool AttackingTriggeredPlayer { get; init; }

    /// <summary>The controller must be the player the creature a trigger is about is attacking ("defending player controls").</summary>
    public bool ControlledByDefendingPlayer { get; init; }

    /// <summary>
    /// "For each player, choose target [permanent] that player controls": the requirement becomes one for each player who has a legal
    /// choice (<see cref="OnlyControlledBy"/>), worked out as the ability is put on the stack.
    /// </summary>
    public bool PerPlayer { get; init; }

    /// <summary>A target the spell has only if a gift was promised as it was cast ("if the gift was promised, return target …").</summary>
    public bool OnlyIfGiftPromised { get; init; }

    /// <summary>A target the spell has only when the gift wasn't promised ("Counter target creature spell. If the gift was promised, instead counter target spell.").</summary>
    public bool OnlyIfGiftNotPromised { get; init; }

    /// <summary>The controller must be this player (set when a <see cref="PerPlayer"/> requirement is split up).</summary>
    public PlayerId? OnlyControlledBy { get; init; }

    /// <summary>Text shown when choosing; set by card scripts so the player sees the printed wording.</summary>
    public string? Text { get; init; }

    /// <summary>"Up to X target …": this requirement is repeated X times (worked out as the ability is put on the stack), all optional and different.</summary>
    public Quantity? RepeatFrom { get; init; }

    /// <summary>"Each of X target …": with <see cref="RepeatFrom"/>, exactly that many (not "up to").</summary>
    public bool RepeatExactly { get; init; }

    /// <summary>"Target cards from their graveyard": cards owned by the player chosen for this earlier target (its index).</summary>
    public int? OwnedByTarget { get; init; }

    public string Describe()
    {
        if (Text is not null) return Text;
        if (Kind == TargetKind.GraveyardCard) return Controller == ControllerFilter.You ? "target card in your graveyard" : "target card in a graveyard";
        if (Kind == TargetKind.Any) return "any target";
        if (Kind == TargetKind.CreatureOrPlaneswalker) return Controller == ControllerFilter.Opponent ? "target creature or planeswalker an opponent controls" : "target creature or planeswalker";
        if (Kind == TargetKind.PlayerOrPlaneswalker) return "target player or planeswalker";
        if (Kind == TargetKind.Player && Controller == ControllerFilter.Opponent) return "target opponent";
        var noun = Kind.ToString().ToLowerInvariant();
        return Controller switch
        {
            ControllerFilter.You => $"target {noun} you control",
            ControllerFilter.Opponent => $"target {noun} an opponent controls",
            _ => $"target {noun}",
        };
    }
}

/// <summary>A chosen target: a card (permanent or spell) or a player.</summary>
public readonly record struct Target(CardId? Card, PlayerId? Player, int? StackObject = null)
{
    public static Target Of(CardId card) => new(card, null);
    public static Target Of(PlayerId player) => new(null, player);

    /// <summary>An ability on the stack (by its stack object id).</summary>
    public static Target OfStack(int stackObject) => new(null, null, stackObject);

    /// <summary>No object chosen for an optional ("up to one") target.</summary>
    public static readonly Target None = new(null, null);

    public bool IsNone => Card is null && Player is null && StackObject is null;

    public override string ToString() => Card is { } c ? c.ToString() : Player is { } p ? p.ToString() : StackObject is { } s ? $"stack#{s}" : "none";
}

/// <summary>Who or what an effect applies to.</summary>
public enum SubjectKind
{
    /// <summary>The Nth chosen target.</summary>
    Target,
    /// <summary>The controller of the spell or ability ("you").</summary>
    You,
    EachOpponent,
    EachPlayer,
    /// <summary>The source object itself ("this creature").</summary>
    Self,
    /// <summary>The controller of the Nth target ("its controller").</summary>
    TargetController,
    /// <summary>Every permanent matching <see cref="Subject.Filter"/> ("each creature", "creatures you control").</summary>
    Each,
    /// <summary>The owner of the Nth target.</summary>
    TargetOwner,
    /// <summary>The object the trigger event was about ("that creature", "that spell", "that card").</summary>
    Triggered,
    /// <summary>The player the trigger event was about ("that player").</summary>
    TriggeredPlayer,
    /// <summary>The permanent the source Aura/Equipment is attached to ("enchanted creature", "equipped creature").</summary>
    Attached,
    /// <summary>Every chosen target (from <see cref="Subject.Index"/> on): "each of those creatures".</summary>
    EachTarget,
    /// <summary>A specific player fixed when an ability was granted ("you" of the spell that granted it).</summary>
    FixedPlayer,
    /// <summary>In a granted ability: the controller of the spell or ability that granted it (bound when granted).</summary>
    Granter,
    /// <summary>The opponents who discarded their hand this way ("each opponent who discarded their hand this way").</summary>
    OpponentsWhoDiscardedHand,
    /// <summary>Every object a "one or more" trigger is about, as the objects they are now ("one of them").</summary>
    Batch,
    /// <summary>The controller of the source ("your graveyard" inside "each opponent …", where "you" is that opponent).</summary>
    SourceController,
    /// <summary>In a granted ability: the permanent that grants it.</summary>
    GranterPermanent,
    /// <summary>The Army this spell or ability amassed ("the amassed Army").</summary>
    Amassed,
    /// <summary>Tokens this spell or ability created ("attach this Equipment to it").</summary>
    Created,
    /// <summary>Cards discarded by this spell or ability, matching <see cref="Subject.Filter"/> ("if you discard a land card this way, put it …").</summary>
    Discarded,
    /// <summary>Cards this spell or ability found in a library ("untap that land").</summary>
    Found,
    /// <summary>One permanent matching <see cref="Subject.Filter"/>, chosen (not targeted) as the effect happens.</summary>
    ChooseOne,
    /// <summary>The creatures attacking the player (or planeswalker) a trigger is about ("those creatures").</summary>
    AttackersOfTriggered,
    /// <summary>Every attacking creature.</summary>
    Attackers,
    /// <summary>The creatures that convoked this spell (rule 702.51c), each as the object it was when tapped.</summary>
    Convokers,
    /// <summary>The player chosen by the last "choose an opponent" of this spell or ability; and that player with you.</summary>
    ChosenPlayer,
    YouAndChosenPlayer,
    /// <summary>Permanents this spell or ability gained control of ("those creatures").</summary>
    ControlGainedThisWay,
    /// <summary>Opponents dealt combat damage this game by a creature with the source's name.</summary>
    OpponentsDamagedBySameName,
    /// <summary>Cards this spell or ability exiled ("the exiled card").</summary>
    ExiledThisWay,
    /// <summary>Cards in exile exiled with the source object ("an instant or sorcery card exiled with this Saga", rule 607.2a).</summary>
    ExiledWithSource,
    /// <summary>The objects chosen by the last "chooses" of this spell or ability.</summary>
    Chosen,
    /// <summary>"The player to your right": the previous living player in turn order (turn order goes to the left).</summary>
    PlayerToYourRight,
    /// <summary>The controller's Ring-bearer, if they have one.</summary>
    RingBearer,
    /// <summary>Opponents who voted for a choice the controller voted for (from the votes a trigger is about).</summary>
    OpponentsWhoVotedWithYou,
    /// <summary>The controller and the opponents who voted for a choice the controller voted for.</summary>
    YouAndOpponentsWhoVotedWithYou,
    /// <summary>Permanents this spell or ability dealt damage to, matching <see cref="Subject.Filter"/> ("if a Dragon is dealt damage this way").</summary>
    DamagedThisWay,
    /// <summary>The object the effect being applied picked at random.</summary>
    ChosenAtRandom,
    /// <summary>The players who sacrificed a permanent as this spell or ability resolved ("each player who sacrificed a permanent this way"), in turn order.</summary>
    Sacrificers,
    /// <summary>The players dealt damage by this spell or ability so far ("each player dealt damage this way"), in turn order.</summary>
    PlayersDamagedThisWay,
    /// <summary>"Each player who drew a card this way".</summary>
    PlayersWhoDrewThisWay,
    /// <summary>The player the source Aura enchants.</summary>
    EnchantedPlayer,
    /// <summary>Each opponent other than the player the trigger is about ("each other opponent").</summary>
    OtherOpponents,
    /// <summary>The player or planeswalker the source is attacking ("the player or planeswalker it's attacking").</summary>
    AttackedBySource,
    /// <summary>The defending player of the attack the source (or the trigger's creature) is in ("defending player").</summary>
    DefendingPlayer,
    /// <summary>Cards milled by this effect, matching <see cref="Subject.Filter"/> ("each creature card milled this way").</summary>
    Milled,
    /// <summary>Cards this effect put onto the battlefield ("they gain haste", "return those creatures").</summary>
    EnteredThisWay,
    /// <summary>The controller of the object the trigger is about ("its controller loses 1 life"), as it last was if it left.</summary>
    TriggeredController,
    /// <summary>The owner of the source ("its owner draws that many cards").</summary>
    SourceOwner,
    /// <summary>Each player other than the source's owner ("each other player", after "its owner").</summary>
    PlayersOtherThanSourceOwner,
}

public sealed record Subject(SubjectKind Kind, int Index = 0, ObjectFilter? Filter = null)
{
    /// <summary>For <see cref="SubjectKind.Each"/>: only permanents the target player at <see cref="Index"/> controls ("creatures target player controls").</summary>
    public bool ControlledByTarget { get; init; }

    /// <summary>With <see cref="SubjectKind.Each"/>: only permanents the chosen player ("choose an opponent") controls.</summary>
    public bool ControlledByChosenPlayer { get; init; }

    /// <summary>For <see cref="SubjectKind.Each"/>: leaves out the chosen targets ("each other creature you control").</summary>
    public bool ExceptTargets { get; init; }

    /// <summary>For <see cref="SubjectKind.Each"/>: not the objects chosen earlier in the effect ("creatures other than … and the chosen creature").</summary>
    public bool ExceptChosen { get; init; }

    /// <summary>For <see cref="SubjectKind.Each"/>: only permanents attached to the target at <see cref="Index"/> ("all Equipment attached to that creature").</summary>
    public bool AttachedToTarget { get; init; }

    /// <summary>For <see cref="SubjectKind.FixedPlayer"/>.</summary>
    public PlayerId? Player { get; init; }

    public static Subject Each(ObjectFilter filter) => new(SubjectKind.Each, Filter: filter);
    public static readonly Subject Triggered = new(SubjectKind.Triggered);
    public static readonly Subject TriggeredPlayer = new(SubjectKind.TriggeredPlayer);
    public static readonly Subject Attached = new(SubjectKind.Attached);

    public static readonly Subject You = new(SubjectKind.You);
    public static readonly Subject Self = new(SubjectKind.Self);
    public static readonly Subject EachOpponent = new(SubjectKind.EachOpponent);
    public static Subject TargetAt(int index) => new(SubjectKind.Target, index);
}
