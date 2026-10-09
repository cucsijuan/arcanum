// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

public sealed class AttackInfo
{
    public required CardId Attacker { get; init; }
    public required PlayerId Defender { get; init; }

    /// <summary>The planeswalker being attacked, if it isn't the player.</summary>
    public CardId? Planeswalker { get; init; }

    /// <summary>Blockers in damage assignment order.</summary>
    public List<CardId> Blockers { get; } = new();

    /// <summary>Stays true even if every blocker later leaves combat (rule 509.1h).</summary>
    public bool IsBlocked { get; set; }
}

public sealed class CombatState
{
    public List<AttackInfo> Attacks { get; } = new();

    /// <summary>Every creature declared as an attacker this combat, as the object it was (it "attacked" even if it left combat since).</summary>
    public List<(CardId Card, int Version)> Declared { get; } = new();

    public AttackInfo? FindAttack(CardId attacker) => Attacks.Find(a => a.Attacker == attacker);

    public bool IsBlocking(CardId blocker) => Attacks.Exists(a => a.Blockers.Contains(blocker));

    public void Remove(CardId card)
    {
        Attacks.RemoveAll(a => a.Attacker == card);
        foreach (var attack in Attacks) attack.Blockers.Remove(card);
    }
}
