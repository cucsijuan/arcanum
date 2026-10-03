// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private async Task DeclareAttackersAsync()
    {
        var combat = State.Combat ??= new CombatState();
        var active = State.ActivePlayer;
        var possible = State.PermanentsControlledBy(active)
            .Where(c => c.IsCreature && !c.Tapped && c.ControlledSinceTurnStart)
            .Select(c => c.Id)
            .ToList();
        var defenders = State.OpponentsOf(active).ToList();

        if (possible.Count > 0 && defenders.Count > 0)
        {
            var declared = await ControllerOf(active).DeclareAttackersAsync(ViewFor(active), possible, defenders);
            Require(declared.Select(d => d.Attacker).Distinct().Count() == declared.Count, "A creature can attack only once.");
            Require(declared.All(d => possible.Contains(d.Attacker) && defenders.Contains(d.Defender)), "Illegal attacker or defender.");

            foreach (var d in declared)
            {
                combat.Attacks.Add(new AttackInfo { Attacker = d.Attacker, Defender = d.Defender });
                State.GetCard(d.Attacker).Tapped = true; // vigilance arrives with keywords (M4)
                Emit(new PermanentTapped(d.Attacker));
                Emit(new AttackerDeclared(d.Attacker, d.Defender));
            }
        }

        _skipCombatDamageSteps = combat.Attacks.Count == 0; // rule 508.8
    }

    private async Task DeclareBlockersAsync()
    {
        var combat = State.Combat!;
        foreach (var defender in State.ApnapOrder().ToList())
        {
            var attackers = combat.Attacks.Where(a => a.Defender == defender).Select(a => a.Attacker).ToList();
            if (attackers.Count == 0) continue;
            var possible = State.PermanentsControlledBy(defender)
                .Where(c => c.IsCreature && !c.Tapped)
                .Select(c => c.Id)
                .ToList();
            if (possible.Count == 0) continue;

            var declared = await ControllerOf(defender).DeclareBlockersAsync(ViewFor(defender), possible, attackers);
            Require(declared.Select(b => b.Blocker).Distinct().Count() == declared.Count, "A creature can block only one attacker.");
            Require(declared.All(b => possible.Contains(b.Blocker) && attackers.Contains(b.Attacker)), "Illegal blocker or attacker.");

            foreach (var b in declared)
            {
                var attack = combat.FindAttack(b.Attacker)!;
                attack.Blockers.Add(b.Blocker);
                attack.IsBlocked = true;
                Emit(new BlockerDeclared(b.Blocker, b.Attacker));
            }
        }
    }

    /// <summary>
    /// Combat damage (rule 510). Unblocked attackers hit the player they attack; an attacker with several
    /// blockers asks its controller how to divide the damage. First strike and trample come with keywords (M4).
    /// </summary>
    private async Task DealCombatDamageAsync()
    {
        var combat = State.Combat!;
        var toCards = new List<(CardId Source, CardId Target, int Amount)>();
        var toPlayers = new List<(CardId Source, PlayerId Target, int Amount)>();

        // 510.1: damage is assigned first (attacking player's choices before defending players'), then dealt at once.
        foreach (var attack in combat.Attacks)
        {
            var attacker = State.GetCard(attack.Attacker);
            int power = attacker.Power;
            if (power <= 0) continue;

            if (!attack.IsBlocked)
            {
                toPlayers.Add((attacker.Id, attack.Defender, power));
                continue;
            }

            var blockers = attack.Blockers.Select(State.GetCard).ToList();
            if (blockers.Count == 0) continue; // blocked, but every blocker left combat (rule 509.1h)

            IReadOnlyDictionary<CardId, int> split;
            var suggested = SuggestDamageSplit(power, blockers);
            if (blockers.Count == 1)
            {
                split = suggested;
            }
            else
            {
                var request = new DamageAssignmentRequest(attacker.Id, power, blockers.Select(b => b.Id).ToList(), suggested);
                split = await ControllerOf(attacker.Controller).AssignCombatDamageAsync(ViewFor(attacker.Controller), request);
                Require(split.Keys.All(request.Blockers.Contains), "Damage can only be assigned to blocking creatures.");
                Require(split.Values.All(v => v >= 0), "Damage amounts cannot be negative.");
                Require(split.Values.Sum() == power, $"Must assign exactly {power} damage.");
            }
            foreach (var (blocker, amount) in split)
                if (amount > 0) toCards.Add((attacker.Id, blocker, amount));
        }

        foreach (var attack in combat.Attacks)
        {
            foreach (var blocker in attack.Blockers.Select(State.GetCard).Where(b => b.Power > 0))
                toCards.Add((blocker.Id, attack.Attacker, blocker.Power));
        }

        // All combat damage is dealt simultaneously (rule 510.2).
        foreach (var (source, target, amount) in toCards)
        {
            State.GetCard(target).Damage += amount;
            Emit(new DamageDealt(source, target, null, amount));
        }
        foreach (var (source, target, amount) in toPlayers)
        {
            Emit(new DamageDealt(source, null, target, amount));
            ChangeLife(target, -amount);
        }
    }

    /// <summary>Lethal damage to each blocker in order, everything left over to the last one.</summary>
    private static IReadOnlyDictionary<CardId, int> SuggestDamageSplit(int power, IReadOnlyList<Card> blockers)
    {
        var split = new Dictionary<CardId, int>();
        for (int i = 0; i < blockers.Count; i++)
        {
            int lethal = Math.Max(0, blockers[i].Toughness - blockers[i].Damage);
            int amount = i == blockers.Count - 1 ? power : Math.Min(power, lethal);
            split[blockers[i].Id] = amount;
            power -= amount;
        }
        return split;
    }

    private void ChangeLife(PlayerId playerId, int delta)
    {
        var player = State.GetPlayer(playerId);
        int old = player.Life;
        player.Life += delta;
        Emit(new LifeChanged(playerId, old, player.Life));
    }
}
