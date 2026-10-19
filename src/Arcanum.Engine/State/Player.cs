// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.State;

public sealed class Player
{
    public PlayerId Id { get; }
    public string Name { get; }
    public int Life { get; set; }

    /// <summary>Index 0 is the top of the library.</summary>
    public List<CardId> Library { get; } = new();
    public List<CardId> Hand { get; } = new();
    public List<CardId> Graveyard { get; } = new();
    public List<CardId> Exile { get; } = new();
    public List<CardId> Command { get; } = new();

    /// <summary>The player's cards outside the game (their sideboard, rule 400.11b): private to them.</summary>
    public List<CardId> Outside { get; } = new();

    public ManaPool ManaPool { get; } = new();
    public int LandsPlayedThisTurn { get; set; }

    /// <summary>How many turns this player has begun ("your first, second, or third turns of the game").</summary>
    public int TurnsTaken { get; set; }

    /// <summary>This player attacked with at least one creature this turn (raid).</summary>
    public bool AttackedThisTurn { get; set; }

    /// <summary>Activated a loyalty ability this turn.</summary>
    public bool ActivatedLoyaltyThisTurn { get; set; }

    /// <summary>Subtypes of planeswalkers whose loyalty abilities this player may activate at instant speed this turn.</summary>
    public HashSet<string> InstantLoyaltySubtypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total life gained this turn.</summary>
    public int LifeGainedThisTurn { get; set; }

    /// <summary>Total mana spent to cast spells this turn (for expend).</summary>
    public int ManaSpentOnSpellsThisTurn { get; set; }

    /// <summary>Permanent types already used this turn to play cards from the graveyard ("a permanent spell of each permanent type").</summary>
    public Cards.CardType GraveyardTypesUsedThisTurn { get; set; }

    /// <summary>"You have no maximum hand size for the rest of the game."</summary>
    public bool NoMaximumHandSize { get; set; }

    /// <summary>Additional lands this player may play this turn ("you may play an additional land this turn").</summary>
    public int ExtraLandsThisTurn { get; set; }

    /// <summary>Equip abilities activated this turn.</summary>
    public int EquipsThisTurn { get; set; }

    /// <summary>Abilities activated this turn (their own turn) that targeted a creature they control (for "the first … costs less").</summary>
    public int OwnCreatureAbilityActivationsThisTurn { get; set; }

    /// <summary>The first card of this turn's draw step has been drawn.</summary>
    public bool DrewInDrawStep { get; set; }

    /// <summary>Has the city's blessing for the rest of the game (ascend).</summary>
    public bool HasCitysBlessing { get; set; }

    /// <summary>Times the Ring has tempted this player, and their Ring-bearer (card, version).</summary>
    public int RingLevel { get; set; }
    public (Core.CardId Card, int Version)? RingBearer { get; set; }

    /// <summary>Protection from everything until the turn with this number begins for them (-1: none).</summary>
    public bool Protected { get; set; }

    /// <summary>Most creatures this player attacked with in one combat this turn.</summary>
    public int AttackersThisTurn { get; set; }

    /// <summary>Has an enduring story for the rest of the game (storied).</summary>
    public bool HasEnduringStory { get; set; }

    /// <summary>Poison counters (ten or more: the player loses, rule 704.5c).</summary>
    public int Poison { get; set; }

    /// <summary>Spells this player cast this turn, in order.</summary>
    public List<CardId> SpellsCastThisTurn { get; } = new();

    /// <summary>Spells this player cast during the previous turn ("if a player cast two or more spells last turn").</summary>
    public int SpellsCastLastTurn { get; set; }

    /// <summary>Cards drawn this turn.</summary>
    public int CardsDrawnThisTurn { get; set; }

    /// <summary>Separate life gain events this turn.</summary>
    public int LifeGainsThisTurn { get; set; }

    /// <summary>Total life lost this turn.</summary>
    public int LifeLostThisTurn { get; set; }

    /// <summary>Set when the player tried to draw from an empty library; checked as a state-based action.</summary>
    public bool AttemptedDrawFromEmptyLibrary { get; set; }

    /// <summary>Creatures that died under this player's control this turn.</summary>
    public int CreaturesDiedThisTurn { get; set; }

    /// <summary>Permanents this player sacrificed this turn.</summary>
    public List<Core.CardId> SacrificedThisTurn { get; } = new();

    /// <summary>Players this player attacked this turn (with creatures attacking them, not their planeswalkers).</summary>
    public HashSet<Core.PlayerId> PlayersAttackedThisTurn { get; } = new();

    /// <summary>A permanent this player controlled left the battlefield this turn.</summary>
    public bool PermanentLeftThisTurn { get; set; }

    public bool HasLost { get; set; }

    /// <summary>Damage dealt to this player this turn.</summary>
    public int DamageTakenThisTurn { get; set; }

    /// <summary>Combat damage dealt to this player this turn, and whether a legendary creature dealt some of it.</summary>
    public int CombatDamageTakenThisTurn { get; set; }
    public bool CombatDamagedByLegendaryThisTurn { get; set; }

    /// <summary>The objects that dealt combat damage to this player this turn, and whether a Dragon did.</summary>
    public HashSet<(CardId Card, int Version)> CombatDamageSourcesThisTurn { get; } = new();
    public bool CombatDamagedByDragonThisTurn { get; set; }

    /// <summary>A card left this player's graveyard this turn.</summary>
    public bool CardLeftGraveyardThisTurn { get; set; }

    /// <summary>How many cards left this player's graveyard this turn ("if three or more cards left your graveyard this turn").</summary>
    public int CardsLeftGraveyardThisTurn { get; set; }

    /// <summary>Creatures exiled from the battlefield this turn while this player controlled them.</summary>
    public int CreaturesExiledThisTurn { get; set; }

    /// <summary>Scried or surveilled this turn.</summary>
    public bool ScriedOrSurveilledThisTurn { get; set; }

    /// <summary>Cards put into this player's graveyard from their library this turn.</summary>
    public int MilledThisTurn { get; set; }

    /// <summary>"The next spell you cast this turn can't be countered": waiting for that spell.</summary>
    public bool NextSpellUncounterableThisTurn { get; set; }

    /// <summary>"Creature spells you cast this turn can't be countered."</summary>
    public bool CreatureSpellsUncounterableThisTurn { get; set; }

    /// <summary>Spells cast this turn that mana from a Treasure was spent to cast.</summary>
    public int TreasureSpellsThisTurn { get; set; }

    /// <summary>The legend rule doesn't apply to this player's permanents this turn.</summary>
    public bool NoLegendRuleThisTurn { get; set; }

    /// <summary>"Their life total can't change" until this player's next turn.</summary>
    public bool LifeCantChange { get; set; }

    /// <summary>The player can't lose life now ("you can't lose life this turn", or their life total can't change).</summary>
    public bool CantLoseLifeNow(int turn) => CantLoseLifeTurn == turn || LifeCantChange;

    /// <summary>"Until end of turn, whenever you tap a Mountain for mana, add an additional {R}": instances this turn.</summary>
    public int ExtraRedFromMountainsThisTurn { get; set; }

    /// <summary>"If you created a token this turn".</summary>
    public bool CreatedTokenThisTurn { get; set; }

    /// <summary>Cards this player discarded this turn.</summary>
    public int CardsDiscardedThisTurn { get; set; }

    /// <summary>Noncombat damage dealt to this player during the previous turn.</summary>
    public int NoncombatDamageTakenLastTurn { get; set; }

    /// <summary>This player put a counter on a creature this turn.</summary>
    public bool PutCounterOnCreatureThisTurn { get; set; }

    /// <summary>Names of creatures that dealt combat damage to this player this game.</summary>
    public HashSet<string> CombatDamagedByNames { get; } = new();

    /// <summary>Permanents that entered the battlefield under this player's control this turn (card, version).</summary>
    public List<(CardId Card, int Version)> EnteredThisTurn { get; } = new();

    /// <summary>"You can't lose life this turn" / "you can't lose the game this turn and your opponents can't win the game this turn": the turn it applies to.</summary>
    public int CantLoseLifeTurn { get; set; } = -1;

    /// <summary>The turn this player has hexproof until its end ("you gain hexproof until end of turn").</summary>
    public int HexproofTurn { get; set; } = -1;

    /// <summary>The last turn this player flipped a coin ("the first time you flip one or more coins each turn").</summary>
    public int CoinFlipTurn { get; set; } = -1;
    public int CantLoseGameTurn { get; set; } = -1;

    /// <summary>The turn this player's spells and abilities can target hexproof opponents and their hexproof creatures ("as though they didn't have hexproof").</summary>
    public int IgnoresHexproofTurn { get; set; } = -1;

    /// <summary>Times each of this player's commanders has been cast from the command zone (commander tax).</summary>
    public Dictionary<CardId, int> CommanderCasts { get; } = new();

    /// <summary>Combat damage this player has taken from each commander (rule 903.10a).</summary>
    public Dictionary<CardId, int> CommanderDamageTaken { get; } = new();

    public Player(PlayerId id, string name, int life)
    {
        Id = id;
        Name = name;
        Life = life;
    }

    public List<CardId> GetZone(Zone zone) => zone switch
    {
        Zone.Library => Library,
        Zone.Hand => Hand,
        Zone.Graveyard => Graveyard,
        Zone.Exile => Exile,
        Zone.Command => Command,
        Zone.Outside => Outside,
        _ => throw new ArgumentException($"{zone} is not a per-player zone.", nameof(zone)),
    };

    public override string ToString() => $"{Name} ({Id})";
}
