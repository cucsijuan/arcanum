// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Which combat damage step is running when first or double strike splits it in two (rule 510.4).</summary>
    private enum DamagePass { Only, FirstStrike, Regular }

    private DamagePass _damagePass = DamagePass.Only;

    /// <summary>Creatures that had first or double strike when the first-strike damage step began.</summary>
    private readonly HashSet<CardId> _firstStrikers = new();

    private async Task DeclareAttackersAsync()
    {
        var combat = State.Combat ??= new CombatState();
        var active = State.ActivePlayer;
        var possible = State.PermanentsControlledBy(active)
            .Where(CanAttack)
            .Select(c => c.Id)
            .ToList();
        var defenders = State.OpponentsOf(active).ToList();

        if (possible.Count > 0 && defenders.Count > 0)
        {
            var declared = await ControllerOf(active).DeclareAttackersAsync(ViewFor(active), possible, defenders);
            Require(declared.Select(d => d.Attacker).Distinct().Count() == declared.Count, "A creature can attack only once.");
            Require(declared.All(d => possible.Contains(d.Attacker) && defenders.Contains(d.Defender)), "Illegal attacker or defender.");
            Require(declared.All(d => d.Planeswalker is not { } pw
                                      || State.GetCard(pw) is { Zone: Zone.Battlefield } w && w.Is(CardType.Planeswalker) && w.Controller == d.Defender),
                "A planeswalker can only be attacked through its controller.");
            // "Attacks each combat if able" (508.1d): such creatures left out attack anyway.
            var mustAttack = possible.Where(id => State.GetCard(id).Definition.AttacksEachCombat && declared.All(d => d.Attacker != id)).ToList();
            if (mustAttack.Count > 0)
            {
                var defender = declared.Count > 0 ? declared[0].Defender : defenders[0];
                declared = declared.Concat(mustAttack.Select(id => new AttackDeclaration(id, defender))).ToList();
            }

            foreach (var d in declared)
            {
                combat.Attacks.Add(new AttackInfo { Attacker = d.Attacker, Defender = d.Defender, Planeswalker = d.Planeswalker });
                var attacker = State.GetCard(d.Attacker);
                if (!attacker.Has(Keyword.Vigilance))
                {
                    attacker.Tapped = true;
                    Emit(new PermanentTapped(d.Attacker));
                }
                Emit(new AttackerDeclared(d.Attacker, d.Defender));
            }
            if (declared.Count > 0) Emit(new AttacksDeclared(active, declared.Count));
        }

        _skipCombatDamageSteps = combat.Attacks.Count == 0; // rule 508.8
    }

    private static bool CanAttack(Card c) =>
        c.IsCreature && !c.Tapped && !c.IsSummoningSick && !c.Has(Keyword.Defender) && !c.Has(Keyword.CantAttack);

    /// <summary>Evasion: flying can only be blocked by flying or reach (702.9b); "can't be blocked by ..." restrictions.</summary>
    private bool CanBlock(Card blocker, Card attacker) =>
        !blocker.Has(Keyword.CantBlock) && !attacker.Has(Keyword.CantBeBlocked) && !attacker.Has(Keyword.ProtectionFromEverything)
        && (!attacker.Has(Keyword.Flying) || blocker.Has(Keyword.Flying) || blocker.Has(Keyword.Reach))
        && !(attacker.Definition.CantBeBlockedBy is { } restriction
             && Matches(restriction with { Controller = Abilities.ControllerFilter.Any }, blocker, blocker.Controller, attacker, attacker.Controller));

    private async Task DeclareBlockersAsync()
    {
        var combat = State.Combat!;
        foreach (var defender in State.ApnapOrder().ToList())
        {
            var attackers = combat.Attacks.Where(a => a.Defender == defender).Select(a => State.GetCard(a.Attacker)).ToList();
            if (attackers.Count == 0) continue;
            var blockers = State.PermanentsControlledBy(defender).Where(c => c.IsCreature && !c.Tapped).ToList();
            var canBlock = blockers.ToDictionary(
                b => b.Id,
                b => (IReadOnlyList<CardId>)attackers.Where(a => CanBlock(b, a)).Select(a => a.Id).ToList());
            var possible = blockers.Where(b => canBlock[b.Id].Count > 0).Select(b => b.Id).ToList();
            if (possible.Count == 0) continue;

            var request = new BlockRequest(
                attackers.Select(a => a.Id).ToList(),
                possible,
                canBlock.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
                attackers.Where(a => a.Has(Keyword.Menace)).ToDictionary(a => a.Id, _ => 2)) // 702.111b
            {
                MustBeBlocked = attackers.Where(a => a.Has(Keyword.MustBeBlocked)).Select(a => a.Id).ToList(),
            };

            var declared = await ControllerOf(defender).DeclareBlockersAsync(ViewFor(defender), request);
            Require(request.IsLegal(declared, out var reason), reason ?? "Illegal blocks.");

            foreach (var b in declared)
            {
                var attack = combat.FindAttack(b.Attacker)!;
                attack.Blockers.Add(b.Blocker);
                attack.IsBlocked = true;
                Emit(new BlockerDeclared(b.Blocker, b.Attacker));
            }
        }
    }

    private bool CombatHasFirstStrike() =>
        State.Combat!.Attacks.SelectMany(a => a.Blockers.Prepend(a.Attacker))
            .Select(State.GetCard)
            .Any(c => c.Zone == Zone.Battlefield && (c.Has(Keyword.FirstStrike) || c.Has(Keyword.DoubleStrike)));

    /// <summary>Whether a creature deals damage in the current combat damage pass (rule 510.4).</summary>
    private bool DealsDamageNow(Card c) => _damagePass switch
    {
        DamagePass.FirstStrike => c.Has(Keyword.FirstStrike) || c.Has(Keyword.DoubleStrike),
        DamagePass.Regular => !_firstStrikers.Contains(c.Id) || c.Has(Keyword.DoubleStrike),
        _ => true,
    };

    /// <summary>
    /// Combat damage (rule 510). Unblocked attackers hit the player they attack; blocked attackers divide damage
    /// among blockers (asking their controller when there is a real choice); everything is dealt at once.
    /// </summary>
    private async Task DealCombatDamageAsync()
    {
        var combat = State.Combat!;
        if (_damagePass == DamagePass.FirstStrike)
        {
            _firstStrikers.Clear();
            foreach (var c in combat.Attacks.SelectMany(a => a.Blockers.Prepend(a.Attacker)).Select(State.GetCard))
                if (c.Has(Keyword.FirstStrike) || c.Has(Keyword.DoubleStrike)) _firstStrikers.Add(c.Id);
        }

        var toCards = new List<(Card Source, Card Target, int Amount)>();
        var toPlayers = new List<(Card Source, PlayerId Target, int Amount)>();
        // Damage to the attacked player, or to the attacked planeswalker if there is one (still there, rule 506.4c).
        void ToDefender(Card attacker, AttackInfo attack, int amount)
        {
            if (attack.Planeswalker is not { } pw) toPlayers.Add((attacker, attack.Defender, amount));
            else if (State.GetCard(pw) is { Zone: Zone.Battlefield } walker && walker.Is(CardType.Planeswalker)) toCards.Add((attacker, walker, amount));
        }

        // 510.1: attackers assign first (the attacking player's choices), then blockers.
        foreach (var attack in combat.Attacks)
        {
            var attacker = State.GetCard(attack.Attacker);
            if (attacker.Zone != Zone.Battlefield || !DealsDamageNow(attacker)) continue;
            int power = attacker.Power;
            if (power <= 0) continue;

            if (!attack.IsBlocked)
            {
                ToDefender(attacker, attack, power);
                continue;
            }

            var blockers = attack.Blockers.Select(State.GetCard).Where(b => b.Zone == Zone.Battlefield).ToList();
            bool trample = attacker.Has(Keyword.Trample);
            if (blockers.Count == 0)
            {
                // Blocked but every blocker is gone: only trample still deals damage (702.19e).
                if (trample) ToDefender(attacker, attack, power);
                continue;
            }

            var assignment = await AssignDamageAsync(attacker, attack.Defender, blockers, power, trample);
            foreach (var (blockerId, amount) in assignment.ToBlockers)
                if (amount > 0) toCards.Add((attacker, State.GetCard(blockerId), amount));
            if (assignment.ToPlayer > 0) ToDefender(attacker, attack, assignment.ToPlayer);
        }

        foreach (var attack in combat.Attacks)
        {
            var attacker = State.GetCard(attack.Attacker);
            if (attacker.Zone != Zone.Battlefield) continue;
            foreach (var blocker in attack.Blockers.Select(State.GetCard))
                if (blocker.Zone == Zone.Battlefield && blocker.Power > 0 && DealsDamageNow(blocker))
                    toCards.Add((blocker, attacker, blocker.Power));
        }

        // All combat damage is dealt simultaneously (rule 510.2).
        BeginCombatDamage();
        var lifeGained = new Dictionary<PlayerId, int>();
        foreach (var (source, target, dealt) in toCards)
        {
            int amount = ModifyDamage(source, target, null, dealt, combat: true);
            if (amount <= 0) continue;
            if (!target.IsCreature)
            {
                target.Counters[Abilities.CounterKind.Loyalty] = Math.Max(0, target.CounterCount(Abilities.CounterKind.Loyalty) - amount);
                Emit(new DamageDealt(source.Id, target.Id, null, amount, IsCombat: true));
                if (source.Has(Keyword.Lifelink)) lifeGained[source.Controller] = lifeGained.GetValueOrDefault(source.Controller) + amount;
                continue;
            }
            target.Damage += amount;
            if (source.Has(Keyword.Deathtouch)) target.DamagedByDeathtouch = true;
            Emit(new DamageDealt(source.Id, target.Id, null, amount, IsCombat: true));
            if (source.Has(Keyword.Lifelink)) lifeGained[source.Controller] = lifeGained.GetValueOrDefault(source.Controller) + amount;
        }
        foreach (var (source, target, dealt) in toPlayers)
        {
            int amount = ModifyDamage(source, null, target, dealt, combat: true);
            if (amount <= 0) continue;
            Emit(new DamageDealt(source.Id, null, target, amount, IsCombat: true));
            ChangeLife(target, -amount);
            RecordCommanderDamage(source, target, amount);
            if (source.Has(Keyword.Lifelink)) lifeGained[source.Controller] = lifeGained.GetValueOrDefault(source.Controller) + amount;
        }
        foreach (var (player, amount) in lifeGained) GainLifeFor(player, amount); // lifelink (702.15b)
        EndCombatDamage();
    }

    private async Task<DamageAssignment> AssignDamageAsync(Card attacker, PlayerId defender, List<Card> blockers, int power, bool trample)
    {
        var lethal = blockers.ToDictionary(b => b.Id, b => LethalDamage(b, attacker));
        var suggested = SuggestDamage(power, blockers, lethal, trample);

        bool hasChoice = blockers.Count > 1 || (trample && power > lethal.Values.Sum());
        if (!hasChoice) return suggested;

        var request = new DamageAssignmentRequest(attacker.Id, power, blockers.Select(b => b.Id).ToList(), lethal, trample, defender, suggested);
        var assignment = await ControllerOf(attacker.Controller).AssignCombatDamageAsync(ViewFor(attacker.Controller), request);
        Require(assignment.ToBlockers.Keys.All(lethal.ContainsKey), "Damage can only be assigned to blocking creatures.");
        Require(assignment.ToBlockers.Values.All(v => v >= 0) && assignment.ToPlayer >= 0, "Damage amounts cannot be negative.");
        Require(assignment.Total == power, $"Must assign exactly {power} damage.");
        Require(assignment.ToPlayer == 0 || trample, "Only trample can assign damage to the player.");
        Require(assignment.ToPlayer == 0 || lethal.All(kv => assignment.ToBlockers.GetValueOrDefault(kv.Key) >= kv.Value),
            "Trample: every blocker must be assigned lethal damage before the player.");
        return assignment;
    }

    /// <summary>Lethal damage for a blocker: what's left of its toughness, or 1 against deathtouch (702.2c).</summary>
    private static int LethalDamage(Card blocker, Card source)
    {
        int remaining = Math.Max(0, blocker.Toughness - blocker.Damage);
        return source.Has(Keyword.Deathtouch) ? Math.Min(1, remaining) : remaining;
    }

    /// <summary>Lethal damage to each blocker in order, the rest to the player (trample) or the last blocker.</summary>
    private static DamageAssignment SuggestDamage(int power, IReadOnlyList<Card> blockers, IReadOnlyDictionary<CardId, int> lethal, bool trample)
    {
        var split = new Dictionary<CardId, int>();
        foreach (var blocker in blockers)
        {
            int amount = Math.Min(power, lethal[blocker.Id]);
            split[blocker.Id] = amount;
            power -= amount;
        }
        if (power > 0 && trample) return new DamageAssignment(split, power);
        if (power > 0) split[blockers[^1].Id] += power;
        return new DamageAssignment(split);
    }

    private void ChangeLife(PlayerId playerId, int delta)
    {
        var player = State.GetPlayer(playerId);
        int old = player.Life;
        player.Life += delta;
        Emit(new LifeChanged(playerId, old, player.Life));
    }
}
