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

    public Card(CardId id, CardDefinition definition, PlayerId owner)
    {
        Id = id;
        Definition = definition;
        Owner = owner;
        Controller = owner;
        Zone = Zone.Library;
    }

    public string Name => Definition.Name;

    // Characteristics are read through these so continuous effects (layers, M4) can hook in later.
    public CardType Types => Definition.Types;
    public int Power => (Definition.Power ?? 0) + PowerBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);
    public int Toughness => (Definition.Toughness ?? 0) + ToughnessBonus + CounterCount(CounterKind.PlusOnePlusOne) - CounterCount(CounterKind.MinusOneMinusOne);

    public int CounterCount(CounterKind kind) => Counters.GetValueOrDefault(kind);

    public bool Is(CardType type) => (Types & type) != 0;

    public bool IsCreature => Is(CardType.Creature);

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
        Version++;
        Controller = Owner;
    }

    public override string ToString() => $"{Name} {Id}";
}
