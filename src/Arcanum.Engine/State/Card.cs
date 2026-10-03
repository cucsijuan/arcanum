// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>A card object in a game: its definition plus mutable in-game status.</summary>
public sealed class Card
{
    public CardId Id { get; }
    public CardDefinition Definition { get; }
    public PlayerId Owner { get; }
    public PlayerId Controller { get; set; }
    public Zone Zone { get; set; }

    public bool Tapped { get; set; }
    public int Damage { get; set; }

    /// <summary>
    /// True once the controller has controlled this permanent continuously since their most recent
    /// turn began. Creatures without it are "summoning sick" (rule 302.6).
    /// </summary>
    public bool ControlledSinceTurnStart { get; set; }

    /// <summary>Dealt damage by a source with deathtouch since the last state-based action check (rule 704.5h).</summary>
    public bool DamagedByDeathtouch { get; set; }

    /// <summary>
    /// Incremented every time the card changes zones: after that it is a new object (rule 400.7), so targets and
    /// effects that referred to the old one no longer apply.
    /// </summary>
    public int Version { get; internal set; }

    public Dictionary<CounterKind, int> Counters { get; } = new();

    /// <summary>This card is its owner's commander (the designation follows it across zones, rule 903.3).</summary>
    public bool IsCommander { get; init; }

    /// <summary>The permanent this Aura or Equipment is attached to (rule 701.3).</summary>
    public CardId? AttachedTo { get; set; }

    // Continuous-effect modifications, recomputed by the engine (layer 7c and keyword grants in layer 6).
    internal int PowerBonus { get; set; }
    internal int ToughnessBonus { get; set; }
    internal HashSet<Keyword> GrantedKeywords { get; } = new();
    internal HashSet<string> GrantedSubtypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Base power/toughness from a characteristic-defining ability (rule 604.3).</summary>
    internal int? BasePowerOverride { get; set; }
    internal int? BaseToughnessOverride { get; set; }

    public Card(CardId id, CardDefinition definition, PlayerId owner)
    {
        Id = id;
        Definition = definition;
        Owner = owner;
        Controller = owner;
        Zone = Zone.Library;
    }

    public string Name => Definition.Name;

    // Characteristics are read through these so continuous effects (layers) apply everywhere.
    public CardType Types => Definition.Types;
    public int Power => (BasePowerOverride ?? Definition.Power ?? 0) + PowerBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);
    public int Toughness => (BaseToughnessOverride ?? Definition.Toughness ?? 0) + ToughnessBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);

    public int CounterCount(CounterKind kind) => Counters.GetValueOrDefault(kind);

    public bool Is(CardType type) => (Types & type) != 0;

    public bool IsCreature => Is(CardType.Creature);

    /// <summary>Has this subtype (changelings have every creature type).</summary>
    public bool HasSubtype(string subtype) =>
        Definition.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase) || GrantedSubtypes.Contains(subtype) || (Has(Keyword.Changeling) && subtype is not ("Equipment" or "Aura" or "Treasure" or "Food" or "Clue"));

    /// <summary>Color chosen as it entered ("As this enters, choose a color"): W, U, B, R or G.</summary>
    public string? ChosenColor { get; set; }

    /// <summary>Creature type chosen as it entered.</summary>
    public string? ChosenType { get; set; }

    /// <summary>Types of mana its mana ability can add (the chosen color for "Add one mana of the chosen color").</summary>
    public IReadOnlyList<Mana.ManaType> ManaTypes =>
        Definition.ManaFromChosenColor && ChosenColor is { } color && Mana.ManaTypeExtensions.TryParse(color[0], out var type)
            ? new[] { type }
            : Definition.TapForMana;

    /// <summary>Mana its mana ability adds per activation (recomputed for "Add {G} for each Elf you control").</summary>
    public int ManaAmount { get; internal set; } = 1;

    /// <summary>Cast with its kicker cost paid (kept as the spell becomes a permanent, rule 702.33).</summary>
    public bool Kicked { get; set; }

    /// <summary>Indices of "activate only once each turn" abilities already activated this turn.</summary>
    public HashSet<int> ActivatedThisTurn { get; } = new();

    /// <summary>A loyalty ability of this planeswalker was activated this turn (one per turn, rule 606.3).</summary>
    public bool LoyaltyActivatedThisTurn { get; set; }

    /// <summary>"Triggers only once each turn" abilities that already triggered this turn.</summary>
    public HashSet<AbilityDefinition> TriggeredThisTurn { get; } = new(ReferenceEqualityComparer.Instance);

    public bool Has(Keyword keyword) => Definition.KeywordAbilities.Contains(keyword) || GrantedKeywords.Contains(keyword);

    /// <summary>A creature that can't attack or use {T} abilities yet (rule 302.6); haste removes the restriction.</summary>
    public bool IsSummoningSick => IsCreature && !ControlledSinceTurnStart && !Has(Keyword.Haste);

    /// <summary>Clears per-object status when the card changes zones (rule 400.7: it becomes a new object).</summary>
    internal void ResetStatus()
    {
        Tapped = false;
        Damage = 0;
        DamagedByDeathtouch = false;
        ControlledSinceTurnStart = false;
        Counters.Clear();
        AttachedTo = null;
        PowerBonus = 0;
        ToughnessBonus = 0;
        GrantedKeywords.Clear();
        GrantedSubtypes.Clear();
        BasePowerOverride = null;
        BaseToughnessOverride = null;
        Kicked = false;
        ChosenColor = null;
        ChosenType = null;
        ActivatedThisTurn.Clear();
        TriggeredThisTurn.Clear();
        LoyaltyActivatedThisTurn = false;
        Version++;
        Controller = Owner;
    }

    public override string ToString() => $"{Name} {Id}";
}
