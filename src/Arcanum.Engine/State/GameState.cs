// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A temporary P/T change and/or keyword grant on one specific object (card + version).</summary>
public sealed record UntilEndOfTurnEffect(CardId Card, int Version, int Power, int Toughness, IReadOnlyList<Cards.Keyword> Keywords)
{
    /// <summary>"Becomes a 3/3 creature": card types added and base power/toughness set (layers 4, 7b).</summary>
    public Cards.CardType AddTypes { get; init; }
    public int? SetPower { get; init; }
    public int? SetToughness { get; init; }
    public IReadOnlyList<string>? AddSubtypes { get; init; }

    /// <summary>Abilities gained until end of turn ("gains 'When this creature dies, ...'").</summary>
    public IReadOnlyList<Abilities.AbilityDefinition>? Abilities { get; init; }
}

/// <summary>Control gained "until end of turn": returned to <paramref name="Original"/> at cleanup.</summary>
public sealed record TemporaryControlEffect(CardId Card, int Version, PlayerId Original);

/// <summary>A card exiled until a source leaves the battlefield.</summary>
public sealed record LinkedExile(CardId Source, int SourceVersion, CardId Exiled, int ExiledVersion);

/// <summary>A card in exile <paramref name="Player"/> may play until turn <paramref name="UntilTurn"/> ends (free when <paramref name="WithoutPaying"/>).</summary>
public sealed record PlayableFromExile(CardId Card, int Version, PlayerId Player, int UntilTurn, bool WithoutPaying = false);

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

    /// <summary>"Until end of turn" modifications, removed in the cleanup step (rule 514.2).</summary>
    public List<UntilEndOfTurnEffect> UntilEndOfTurn { get; } = new();

    public List<TemporaryControlEffect> TemporaryControl { get; } = new();

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

    /// <summary>Permanents that entered and still need their "as this enters, choose..." choice.</summary>
    public List<(CardId Card, int Version)> PendingEnterChoices { get; } = new();

    /// <summary>Objects that are exiled instead if they would die this turn.</summary>
    public HashSet<(CardId Card, int Version)> ExileIfDies { get; } = new();

    /// <summary>Objects combat damage to which is prevented this turn.</summary>
    public HashSet<(CardId Card, int Version)> CombatDamagePrevented { get; } = new();

    public int TurnNumber { get; set; }
    public PlayerId ActivePlayer { get; set; }
    public PlayerId? PriorityPlayer { get; set; }
    public Step Step { get; set; }
    public CombatState? Combat { get; set; }

    /// <summary>Creatures that died this turn (morbid).</summary>
    public int CreaturesDiedThisTurn { get; set; }

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
