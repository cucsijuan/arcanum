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
    /// <summary>"Attacks each combat if able."</summary>
    public bool AttacksEachCombat { get; init; }
    /// <summary>Loyalty counters (planeswalkers).</summary>
    public int Loyalty { get; init; }
    public bool IsToken { get; init; }
    public string OracleText { get; init; } = "";
    public IReadOnlyList<string> Colors { get; init; } = Array.Empty<string>();
    /// <summary>Exact image identifier when the name is ambiguous (tokens); see <c>CardDefinition.ImageKey</c>.</summary>
    public string? ImageKey { get; init; }

    // What effects changed compared with the printed card (shown beside the card preview).
    public Supertype Supertypes { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = Array.Empty<string>();
    public CardType PrintedTypes { get; init; }
    public IReadOnlyList<string> PrintedSubtypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PrintedColors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PrintedKeywords { get; init; } = Array.Empty<string>();
    public bool LostAllAbilities { get; init; }
    /// <summary>Rules text of abilities it gained from effects.</summary>
    public IReadOnlyList<string> GainedAbilityTexts { get; init; } = Array.Empty<string>();
    /// <summary>Counters other than +1/+1, -1/-1 and loyalty, by kind.</summary>
    public IReadOnlyDictionary<string, int> OtherCounters { get; init; } = new Dictionary<string, int>();
    public string? ChosenColor { get; init; }
    public string? ChosenType { get; init; }

    /// <summary>An adventurer card's Adventure: its name, mana cost and rules text.</summary>
    public string? AdventureName { get; init; }
    public string? AdventureCost { get; init; }
    public string? AdventureText { get; init; }

    /// <summary>In exile after its Adventure: its owner may cast it from there.</summary>
    public bool OnAdventure { get; init; }

    /// <summary>A split card's halves: names and mana costs.</summary>
    public IReadOnlyList<string>? SplitHalves { get; init; }
}

public sealed record PlayerView
{
    public required PlayerId Id { get; init; }
    public required string Name { get; init; }
    public required int Life { get; init; }
    public required bool HasLost { get; init; }
    public required int LibraryCount { get; init; }

    /// <summary>The top card of the library when the viewer may look at it ("You may look at the top card of your library any time").</summary>
    public CardView? LibraryTop { get; init; }
    public required IReadOnlyList<CardView> Hand { get; init; }
    public required IReadOnlyList<CardView> Graveyard { get; init; }
    public required IReadOnlyList<CardView> Exile { get; init; }
    public required IReadOnlyList<CardView> Command { get; init; }
    public required int ManaPoolTotal { get; init; }

    /// <summary>Combat damage taken from each commander (commander games).</summary>
    public IReadOnlyDictionary<CardId, int> CommanderDamage { get; init; } = new Dictionary<CardId, int>();

    /// <summary>Has an enduring story (storied) for the rest of the game.</summary>
    public bool EnduringStory { get; init; }

    /// <summary>Has the city's blessing (ascend).</summary>
    public bool CitysBlessing { get; init; }

    /// <summary>Times the Ring has tempted this player, and their Ring-bearer.</summary>
    public int RingLevel { get; init; }
    public CardId? RingBearer { get; init; }

    /// <summary>Has protection from everything until their next turn.</summary>
    public bool Protected { get; init; }

    /// <summary>Poison counters.</summary>
    public int Poison { get; init; }

    /// <summary>Has no maximum hand size for the rest of the game.</summary>
    public bool NoMaximumHandSize { get; init; }

    /// <summary>Emblems the player owns: name and rules text.</summary>
    public IReadOnlyList<EmblemView> Emblems { get; init; } = Array.Empty<EmblemView>();

    /// <summary>Floating mana by type (only types with a non-zero amount).</summary>
    public required IReadOnlyDictionary<Mana.ManaType, int> ManaPool { get; init; }
}

/// <summary>An emblem in the command zone (rule 114).</summary>
public sealed record EmblemView(string Name, string Text, bool UntilEndOfTurn);

/// <param name="Card">The spell, or the source of the ability.</param>
/// <param name="AbilityText">Rules text of the ability, or null for a spell.</param>
public sealed record StackItemView(CardView Card, PlayerId Controller, string? AbilityText, IReadOnlyList<Abilities.Target> Targets, int Id = 0);

/// <summary>"Creatures can't attack [Defender] unless their controller pays [CostPerCreature] for each": and how many the viewer can pay for now.</summary>
public sealed record AttackTaxView(PlayerId Defender, string CostPerCreature, int Affordable);

/// <summary>A creature that can't attack a given player ("can't attack you", "can't attack you this combat").</summary>
public sealed record AttackRestrictionView(CardId Attacker, PlayerId Defender);

public sealed record AttackView(CardId Attacker, PlayerId Defender, IReadOnlyList<CardId> Blockers, bool IsBlocked, CardId? Planeswalker = null);

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

    /// <summary>Attack taxes the viewer would have to pay, by defending player.</summary>
    public IReadOnlyList<AttackTaxView> AttackTaxes { get; init; } = Array.Empty<AttackTaxView>();

    /// <summary>Players the viewer's creatures can't attack, by creature.</summary>
    public IReadOnlyList<AttackRestrictionView> AttackRestrictions { get; init; } = Array.Empty<AttackRestrictionView>();

    /// <summary>The monarch (rule 724), if there is one.</summary>
    public PlayerId? Monarch { get; init; }

    /// <summary>Whether the creature may attack that player, as far as restrictions go.</summary>
    public bool MayAttack(CardId attacker, PlayerId defender) => !AttackRestrictions.Any(r => r.Attacker == attacker && r.Defender == defender);
    public required PlayerId? Winner { get; init; }

    public PlayerView Self => Players[Viewer.Value];

    public CardView? FindCard(CardId id) =>
        Battlefield.FirstOrDefault(c => c.Id == id)
        ?? Players.SelectMany(p => p.Hand.Concat(p.Graveyard).Concat(p.Exile).Concat(p.Command))
            .FirstOrDefault(c => c.Id == id)
        ?? Stack.Select(s => s.Card).FirstOrDefault(c => c.Id == id);
}
