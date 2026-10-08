// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>
/// A continuous effect from a resolved spell or ability on one specific object (card + version): P/T changes, keyword
/// and ability grants, type changes. Kept until end of turn, or until the object leaves the battlefield
/// (<see cref="GameState.LastingEffects"/>). Applied in its layers in timestamp order (rule 613.7).
/// </summary>
public sealed record UntilEndOfTurnEffect(CardId Card, int Version, int Power, int Toughness, IReadOnlyList<Cards.Keyword> Keywords)
{
    /// <summary>"Becomes a 3/3 creature": card types added and base power/toughness set (layers 4, 7b).</summary>
    public Cards.CardType AddTypes { get; init; }
    public int? SetPower { get; init; }
    public int? SetToughness { get; init; }
    public IReadOnlyList<string>? AddSubtypes { get; init; }

    /// <summary>Replaces its subtypes ("becomes a Human Faerie Detective").</summary>
    public IReadOnlyList<string>? SetSubtypes { get; init; }

    /// <summary>Colors it has from now on ("becomes a green creature") / colors added ("is black in addition to its other colors"), layer 5.</summary>
    public IReadOnlyList<string>? SetColors { get; init; }
    public IReadOnlyList<string>? AddColors { get; init; }

    /// <summary>Abilities gained ("gains 'When this creature dies, ...'").</summary>
    public IReadOnlyList<Abilities.AbilityDefinition>? Abilities { get; init; }

    /// <summary>It loses all abilities (those it gained from earlier effects too).</summary>
    public bool LosesAbilities { get; init; }

    /// <summary>When the effect began (rule 613.7a).</summary>
    public long Timestamp { get; init; }

    /// <summary>"Can't be blocked by creatures that player controls this turn."</summary>
    public PlayerId? UnblockableBy { get; init; }

    /// <summary>Keywords it loses (layer 6).</summary>
    public IReadOnlyList<Cards.Keyword>? LoseKeywords { get; init; }

    /// <summary>Its card types from now on (layer 4).</summary>
    public Cards.CardType? SetTypes { get; init; }

    /// <summary>Base power/toughness worked out continuously ("equal to the number of lands you control").</summary>
    public Abilities.Quantity? SetPowerFrom { get; init; }
    public Abilities.Quantity? SetToughnessFrom { get; init; }

    /// <summary>Ends when this permanent (id, version) leaves the battlefield ("for as long as this Saga remains").</summary>
    public (CardId Card, int Version)? WhileSource { get; init; }

    /// <summary>Also ends once this player no longer controls <see cref="WhileSource"/> ("for as long as you control").</summary>
    public PlayerId? WhileControlledBy { get; init; }

    /// <summary>Ends as this player's next turn begins ("until your next turn").</summary>
    public PlayerId? UntilTurnOf { get; init; }

    /// <summary>Protection from these players (layer 6).</summary>
    public IReadOnlyList<PlayerId>? ProtectionFromPlayers { get; init; }

    /// <summary>It has "This creature can't attack its owner" (layer 6).</summary>
    public bool CantAttackOwner { get; init; }

    /// <summary>Protection from these card types (layer 6).</summary>
    public Cards.CardType ProtectionFromTypes { get; init; }

    /// <summary>"Can't be blocked by [filter] creatures" (layer 6): blockers matching it can't block the creature.</summary>
    public Abilities.ObjectFilter? CantBeBlockedBy { get; init; }
}

/// <summary>
/// A control-changing effect from a resolved spell or ability (layer 2), applied with the control-changing static abilities
/// in timestamp order (rule 613.7). It ends with the turn, at the end of a player's next turn, once its source leaves or
/// changes controller, or never; and always when its object leaves the battlefield.
/// </summary>
public sealed record ControlEffect(CardId Card, int Version, PlayerId NewController, long Timestamp)
{
    public bool UntilEndOfTurn { get; init; }

    /// <summary>"Until the end of your next turn": ends at the cleanup of that player's first turn after the effect began.</summary>
    public PlayerId? UntilEndOfNextTurnOf { get; init; }
    public int MadeOnTurn { get; init; }

    /// <summary>"For as long as you control [source]".</summary>
    public (CardId Card, int Version)? WhileSource { get; init; }
}

/// <summary>A card exiled until a source leaves the battlefield.</summary>
public sealed record LinkedExile(CardId Source, int SourceVersion, CardId Exiled, int ExiledVersion);

/// <summary>A card in exile <paramref name="Player"/> may play until turn <paramref name="UntilTurn"/> ends (free when <paramref name="WithoutPaying"/>).</summary>
public sealed record PlayableFromExile(CardId Card, int Version, PlayerId Player, int UntilTurn, bool WithoutPaying = false)
{
    /// <summary>Cast by paying life equal to its mana value rather than its mana cost.</summary>
    public bool PayLife { get; init; }

    /// <summary>Playable only while this holds for the player.</summary>
    public Abilities.Condition? While { get; init; }

    /// <summary>Mana of any type can be spent to cast it.</summary>
    public bool AnyManaType { get; init; }

    /// <summary>"Until the end of your next turn": lasts through that player's first turn after <see cref="MadeOnTurn"/>.</summary>
    public PlayerId? UntilEndOfNextTurnOf { get; init; }
    public int MadeOnTurn { get; init; }

    /// <summary>"You may cast" (not play): a land can't be played this way.</summary>
    public bool CastOnly { get; init; }

    /// <summary>"You may play a card exiled with [this]": playing one of the group ends the others' permission.</summary>
    public int Group { get; init; }

    /// <summary>"When you play a card this way": the source and its ability that triggers then.</summary>
    public (CardId Source, Abilities.TriggeredAbility Ability)? WhenPlayed { get; init; }
}

/// <summary>
/// "Prevent all [combat] damage that would be dealt [by a creature / by matching sources / to a player] this turn" (rule 615).
/// </summary>
public sealed record PreventionShield(int Turn, bool CombatOnly)
{
    public (CardId Card, int Version)? DealtBy { get; init; }
    public Abilities.ObjectFilter? SourceFilter { get; init; }
    public PlayerId FilterController { get; init; }
    public PlayerId? ToPlayer { get; init; }

    /// <summary>"… and creatures you control": also damage to creatures this player controls when it would be dealt.</summary>
    public PlayerId? ToCreaturesOf { get; init; }
}

/// <summary>
/// "The next N damage that a source of your choice would deal to you and/or permanents you control this turn is dealt to
/// [the target] instead" (a redirection effect, rule 614.9): <see cref="Remaining"/> goes down as damage is redirected.
/// </summary>
public sealed record RedirectShield(int Id, PlayerId Controller, CardId Card, CardId Source, int SourceVersion, ChosenTarget To, int Turn)
{
    public int Remaining { get; set; }
}

/// <summary>A delayed triggered ability waiting for its moment ("at the beginning of the next upkeep").</summary>
public sealed record DelayedTrigger(CardId Source, Abilities.TriggeredAbility Ability, PlayerId Controller, int Amount)
{
    /// <summary>Only at this player's upkeep ("your next upkeep").</summary>
    public PlayerId? OnlyAtUpkeepOf { get; init; }

    /// <summary>"That player" for the delayed ability.</summary>
    public PlayerId? About { get; init; }

    /// <summary>How many times its source had transformed when it was created (rule 701.27f).</summary>
    public int? SourceTransforms { get; init; }
}

/// <summary>"Prevent all damage that would be dealt by" a permanent, while a source stays on the battlefield.</summary>
public sealed record DamagePrevention(CardId Card, int Version, CardId Source, int SourceVersion);

/// <summary>A delayed action: return a card to the battlefield, or sacrifice a permanent.</summary>
public sealed record DelayedAction(CardId Card, int Version, bool Return, PlayerId Controller)
{
    /// <summary>Counters it returns with.</summary>
    public (Abilities.CounterKind Kind, int Count)? Counters { get; init; }
}

/// <summary>Complete, authoritative state of a game. Only the engine mutates it.</summary>
public sealed class GameState
{
    public IReadOnlyList<Player> Players { get; }
    public Dictionary<CardId, Card> Cards { get; } = new();
    public List<CardId> Battlefield { get; } = new();

    /// <summary>Last element is the top of the stack.</summary>
    public List<StackItem> Stack { get; } = new();

    /// <summary>Next stack object id.</summary>
    public int NextStackId { get; set; } = 1;

    /// <summary>"Until end of turn" modifications, removed in the cleanup step (rule 514.2).</summary>
    public List<UntilEndOfTurnEffect> UntilEndOfTurn { get; } = new();

    /// <summary>Effects that last as long as their object stays on the battlefield ("it's a Demon in addition to its other types").</summary>
    public List<UntilEndOfTurnEffect> LastingEffects { get; } = new();

    /// <summary>Players who will take extra turns, in the order they were created (the last one is taken first).</summary>
    public List<PlayerId> ExtraTurns { get; } = new();

    /// <summary>The latest timestamp handed out (rule 613.7).</summary>
    public long LastTimestamp { get; set; }

    /// <summary>Control-changing effects of resolved spells and abilities (layer 2).</summary>
    public List<ControlEffect> ControlEffects { get; } = new();

    /// <summary>The turn number each player's current or latest turn has (for "until the end of your next turn").</summary>
    public Dictionary<PlayerId, int> LastTurnOf { get; } = new();

    /// <summary>Cards exiled "until [source] leaves the battlefield".</summary>
    public List<LinkedExile> LinkedExiles { get; } = new();

    /// <summary>Things to do at the beginning of the next end step (delayed triggered abilities, rule 603.7).</summary>
    public List<DelayedAction> AtNextEndStep { get; } = new();

    /// <summary>Cards in exile their controller may play for a while (until <c>UntilTurn</c> ends).</summary>
    public List<PlayableFromExile> PlayableFromExile { get; } = new();

    /// <summary>Cards in graveyards that can be cast this turn (flashback granted, or cast from the graveyard).</summary>
    public List<PlayableFromExile> PlayableFromGraveyard { get; } = new();
    public List<PlayableFromExile> FlashbackGranted { get; } = new();

    /// <summary>"When you next cast an instant or sorcery spell this turn, copy that spell" (player, turn).</summary>
    public List<(PlayerId Player, int Turn)> CopyNextInstantOrSorcery { get; } = new();

    /// <summary>Emblems in the command zone (rule 114).</summary>
    public List<CardId> Emblems { get; } = new();

    /// <summary>Emblems that end with the turn ("whenever you attack this turn, …").</summary>
    public List<CardId> EmblemsUntilEndOfTurn { get; } = new();

    /// <summary>Permanents that entered and still need their "as this enters, choose..." choice.</summary>
    public List<(CardId Card, int Version)> PendingEnterChoices { get; } = new();

    /// <summary>Objects that are exiled instead if they would die this turn.</summary>
    public HashSet<(CardId Card, int Version)> ExileIfDies { get; } = new();

    /// <summary>Permanents (card, version) that are exiled instead if they would leave the battlefield, for as long as they stay.</summary>
    public HashSet<(CardId Card, int Version)> ExileIfLeaves { get; } = new();

    /// <summary>"Until end of turn, you may cast [filter] spells from your graveyard": the player, the turn and what they may cast.</summary>
    public List<(PlayerId Player, int Turn, Abilities.ObjectFilter Filter)> GraveyardCastRights { get; } = new();

    /// <summary>"Creatures without flying can't block this turn": filters (with the player whose effect it is) for this turn.</summary>
    public List<(Abilities.ObjectFilter Filter, PlayerId Controller, int Turn)> CantBlockThisTurn { get; } = new();

    /// <summary>
    /// "It can't have counters put on it for as long as this creature remains on the battlefield" / "that player can't get counters":
    /// the object or player (card and version, or player) and the permanent (card and version) whose presence keeps the ban.
    /// </summary>
    public List<(CardId? Card, int CardVersion, PlayerId? Player, CardId Source, int SourceVersion)> CounterBans { get; } = new();

    /// <summary>"Until end of turn, if a [creature] would enter the battlefield and it wasn't cast, exile it instead": the filter, the effect's controller and the turn.</summary>
    public List<(Abilities.ObjectFilter Filter, PlayerId Controller, int Turn)> ExileUncastEntering { get; } = new();

    /// <summary>Phased-out permanents (rule 702.26), with the player whose untap step phases them in.</summary>
    public List<(CardId Card, PlayerId Controller)> PhasedOut { get; } = new();

    /// <summary>Creatures to be sacrificed by their controllers at end of combat (the Ring's third ability).</summary>
    public List<(CardId Card, int Version)> SacrificeAtEndOfCombat { get; } = new();

    /// <summary>Permanents that can't be sacrificed this turn ("you can't sacrifice those creatures this turn"), with the player that applies to.</summary>
    public List<(CardId Card, int Version, PlayerId Player, int Turn)> CantSacrificeThisTurn { get; } = new();

    /// <summary>Objects (card, version) that are exiled instead if they would be put into a graveyard ("if that spell would be put into a graveyard, exile it instead").</summary>
    public HashSet<(CardId Card, int Version)> ExileInsteadOfGraveyard { get; } = new();

    /// <summary>Spells (card, version) exiled with time counters instead of going to the graveyard as they resolve, gaining suspend.</summary>
    public Dictionary<(CardId Card, int Version), int> SuspendOnResolution { get; } = new();

    /// <summary>Permanents exiled at end of combat ("exile the token at end of combat").</summary>
    public List<(CardId Card, int Version)> ExileAtEndOfCombat { get; } = new();

    /// <summary>Turn in which players can't cast spells (-1: none).</summary>
    public int SpellsForbiddenTurn { get; set; } = -1;

    /// <summary>Players who can't cast spells during a given turn ("your opponents can't cast spells this turn").</summary>
    public List<(PlayerId Player, int Turn)> SpellsForbiddenFor { get; } = new();

    /// <summary>Delayed abilities that trigger at the beginning of the next upkeep.</summary>
    public List<DelayedTrigger> AtNextUpkeep { get; } = new();

    /// <summary>"At the beginning of the next end step, exile that token unless …": per token made.</summary>
    public List<(CardId Source, PlayerId Controller, CardId Token, int TokenVersion, IReadOnlyList<Abilities.Effect> Effects, Abilities.Condition? Unless, int? SourceTransforms)> AtNextEndStepEffects { get; } = new();

    /// <summary>"When you next cast a creature spell of that type this turn, that creature enters with an additional +1/+1 counter."</summary>
    public List<(PlayerId Player, string Type, int Turn)> NextCreatureSpellBonus { get; } = new();

    /// <summary>Cast creature spells (card, version on the stack) that enter with additional +1/+1 counters.</summary>
    public Dictionary<(CardId Card, int Version), int> ExtraCountersOnEnter { get; } = new();

    /// <summary>Delayed abilities waiting for a given player's next end step (and the turn they were made, so it's a later one).</summary>
    public List<(DelayedTrigger Trigger, PlayerId Whose, int MadeOnTurn, bool MadeDuringEndStep)> AtPlayersNextEndStep { get; } = new();

    /// <summary>Permanents whose damage is prevented while a source stays.</summary>
    public List<DamagePrevention> DamagePreventions { get; } = new();

    /// <summary>Objects combat damage to which is prevented this turn.</summary>
    public HashSet<(CardId Card, int Version)> CombatDamagePrevented { get; } = new();

    public int TurnNumber { get; set; }
    public PlayerId ActivePlayer { get; set; }
    public PlayerId? PriorityPlayer { get; set; }
    public Step Step { get; set; }
    public CombatState? Combat { get; set; }

    /// <summary>Creatures that died this turn (morbid).</summary>
    public int CreaturesDiedThisTurn { get; set; }

    /// <summary>Permanents sacrificed this turn by all players.</summary>
    public int PermanentsSacrificedThisTurn { get; set; }

    /// <summary>Blocks declared this turn: (blocker, its version, attacker, its version).</summary>
    public List<(CardId Blocker, int BlockerVersion, CardId Attacker, int AttackerVersion)> BlocksThisTurn { get; } = new();

    /// <summary>The turn during which damage can't be prevented.</summary>
    public int DamageCantBePreventedTurn { get; set; } = -1;

    /// <summary>"[Creature] attacks [player] this turn if able": the creature (card, version), the player and the turn.</summary>
    public List<(CardId Card, int Version, PlayerId Player, int Turn)> AttackPlayerRequirements { get; } = new();

    /// <summary>
    /// "Doesn't untap during its controller's next untap step" (no player) or "during [player]'s next untap step": the
    /// permanent (card, version) and the player whose next untap step it is.
    /// </summary>
    public List<(CardId Card, int Version, PlayerId? Player)> SkipNextUntap { get; } = new();

    /// <summary>Goaded creatures (card, version) and who goaded them, until that player's next turn (rule 701.15).</summary>
    public List<(CardId Card, int Version, PlayerId Goader)> Goads { get; } = new();


    /// <summary>Damage prevention shields that last this turn.</summary>
    public List<PreventionShield> PreventionShields { get; } = new();

    /// <summary>Damage redirection shields that last this turn.</summary>
    public List<RedirectShield> RedirectShields { get; } = new();
    public int NextRedirectShieldId { get; set; } = 1;

    /// <summary>"If a source you control would deal damage this turn to an opponent or a permanent an opponent controls, it deals triple that damage instead."</summary>
    public List<(PlayerId Player, int Turn)> DamageTripled { get; } = new();

    /// <summary>The monarch (rule 724), if there is one, and the object standing for the monarch's inherent triggered abilities.</summary>
    public PlayerId? Monarch { get; set; }
    public CardId? MonarchDesignation { get; set; }

    /// <summary>Cards exiled "until an opponent becomes the monarch", with the player whose ability exiled them.</summary>
    public List<(CardId Card, int Version, PlayerId Controller)> ExiledUntilOpponentIsMonarch { get; } = new();

    /// <summary>Spells cast this turn by all players (storm).</summary>
    public int SpellsCastThisTurnCount { get; set; }

    /// <summary>Combats begun this turn (for "this combat").</summary>
    public int CombatsThisTurn { get; set; }

    /// <summary>"[Attacking player] can't attack [protected player] this combat" (turn, combat number).</summary>
    public List<(PlayerId Attacker, PlayerId Protected, int Turn, int Combat)> CantAttackThisCombat { get; } = new();

    public bool IsGameOver { get; set; }
    public PlayerId? Winner { get; set; }

    public GameState(IReadOnlyList<Player> players)
    {
        Players = players;
    }

    public Player GetPlayer(PlayerId id) => Players[id.Value];

    public Card GetCard(CardId id) => Cards[id];

    public IEnumerable<Player> LivingPlayers => Players.Where(p => !p.HasLost);

    public IEnumerable<Card> PermanentsControlledBy(PlayerId player) =>
        Battlefield.Select(GetCard).Where(c => c.Controller == player);

    /// <summary>Next living player after <paramref name="from"/> in turn order.</summary>
    public PlayerId NextLivingPlayer(PlayerId from)
    {
        for (int i = 1; i <= Players.Count; i++)
        {
            var candidate = Players[(from.Value + i) % Players.Count];
            if (!candidate.HasLost) return candidate.Id;
        }
        return from;
    }

    /// <summary>Living players in APNAP order starting with the active player (rule 101.4).</summary>
    public IEnumerable<PlayerId> ApnapOrder()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[(ActivePlayer.Value + i) % Players.Count];
            if (!p.HasLost) yield return p.Id;
        }
    }

    public IEnumerable<PlayerId> OpponentsOf(PlayerId player) =>
        LivingPlayers.Where(p => p.Id != player).Select(p => p.Id);
}
