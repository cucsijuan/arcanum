// SPDX-License-Identifier: AGPL-3.0-or-later
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
    public int Power => Definition.Power ?? 0;
    public int Toughness => Definition.Toughness ?? 0;

    public bool Is(CardType type) => (Types & type) != 0;

    public bool IsCreature => Is(CardType.Creature);

    public bool Has(Keyword keyword) => Definition.KeywordAbilities.Contains(keyword);

    /// <summary>A creature that can't attack or use {T} abilities yet (rule 302.6); haste removes the restriction.</summary>
    public bool IsSummoningSick => IsCreature && !ControlledSinceTurnStart && !Has(Keyword.Haste);

    /// <summary>Clears per-object status when the card changes zones (rule 400.7: it becomes a new object).</summary>
    internal void ResetStatus()
    {
        Tapped = false;
        Damage = 0;
        DamagedByDeathtouch = false;
        ControlledSinceTurnStart = false;
        Controller = Owner;
    }

    public override string ToString() => $"{Name} {Id}";
}
