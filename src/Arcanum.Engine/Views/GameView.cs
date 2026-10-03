// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Views;

/// <summary>
/// What a specific player is allowed to know about a card. Hidden cards only expose their id,
/// owner and zone, so a client (or a cheating remote peer) never receives hidden information.
/// </summary>
public sealed record CardView
{
    public required CardId Id { get; init; }
    public required PlayerId Owner { get; init; }
    public required PlayerId Controller { get; init; }
    public required Zone Zone { get; init; }
    public required bool IsHidden { get; init; }

    public string? Name { get; init; }
    public string? ManaCost { get; init; }
    public CardType Types { get; init; }
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public bool Tapped { get; init; }
    public int Damage { get; init; }
    public bool SummoningSick { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>Rules text of each of the card's abilities, indexed like <c>ActivateAbility.Index</c>.</summary>
    public IReadOnlyList<string> AbilityTexts { get; init; } = Array.Empty<string>();
    public CardId? AttachedTo { get; init; }
    public bool IsCommander { get; init; }
    /// <summary>For a commander in the command zone: extra generic mana it costs to cast now.</summary>
    public int CommanderTax { get; init; }
    /// <summary>Printed power/toughness, to show when continuous effects or counters change them.</summary>
    public int? BasePower { get; init; }
    public int? BaseToughness { get; init; }
    public int PlusOneCounters { get; init; }
    public int MinusOneCounters { get; init; }
    public bool IsToken { get; init; }
    public string OracleText { get; init; } = "";
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    /// <summary>Exact image identifier when the name is ambiguous (tokens); see <c>CardDefinition.ImageKey</c>.</summary>
    public string? ImageKey { get; init; }
}

public sealed record PlayerView
{
    public required PlayerId Id { get; init; }
    public required string Name { get; init; }
    public required int Life { get; init; }
    public required bool HasLost { get; init; }
    public required int LibraryCount { get; init; }
    public required IReadOnlyList<CardView> Hand { get; init; }
    public required IReadOnlyList<CardView> Graveyard { get; init; }
    public required IReadOnlyList<CardView> Exile { get; init; }
    public required IReadOnlyList<CardView> Command { get; init; }
    public required int ManaPoolTotal { get; init; }

    /// <summary>Combat damage taken from each commander (commander games).</summary>
    public IReadOnlyDictionary<CardId, int> CommanderDamage { get; init; } = new Dictionary<CardId, int>();

    /// <summary>Floating mana by type (only types with a non-zero amount).</summary>
    public required IReadOnlyDictionary<Mana.ManaType, int> ManaPool { get; init; }
}

/// <param name="Card">The spell, or the source of the ability.</param>
/// <param name="AbilityText">Rules text of the ability, or null for a spell.</param>
public sealed record StackItemView(CardView Card, PlayerId Controller, string? AbilityText, IReadOnlyList<Abilities.Target> Targets);

public sealed record AttackView(CardId Attacker, PlayerId Defender, IReadOnlyList<CardId> Blockers, bool IsBlocked);

/// <summary>Snapshot of the game from one player's perspective.</summary>
public sealed record GameView
{
    public required PlayerId Viewer { get; init; }
    public required int TurnNumber { get; init; }
    public required PlayerId ActivePlayer { get; init; }
    public required PlayerId? PriorityPlayer { get; init; }
    public required Step Step { get; init; }
    public required IReadOnlyList<PlayerView> Players { get; init; }
    public required IReadOnlyList<CardView> Battlefield { get; init; }
    public required IReadOnlyList<StackItemView> Stack { get; init; }
    public required IReadOnlyList<AttackView> Attacks { get; init; }
    public required bool IsGameOver { get; init; }
    public required PlayerId? Winner { get; init; }

    public PlayerView Self => Players[Viewer.Value];

    public CardView? FindCard(CardId id) =>
        Battlefield.FirstOrDefault(c => c.Id == id)
        ?? Players.SelectMany(p => p.Hand.Concat(p.Graveyard).Concat(p.Exile).Concat(p.Command))
            .FirstOrDefault(c => c.Id == id)
        ?? Stack.Select(s => s.Card).FirstOrDefault(c => c.Id == id);
}
