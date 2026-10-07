// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

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
        var defenders = State.OpponentsOf(active).ToList();
        var possible = State.PermanentsControlledBy(active)
            // "Can't attack you" leaves that player's planeswalkers attackable.
            .Where(c => CanAttack(c) && defenders.Any(d => !AttackForbiddenWithPlaneswalkers(c, d)
                                                          && (!AttackForbidden(c, d) || State.PermanentsControlledBy(d).Any(p => p.Is(CardType.Planeswalker)))))
            .Select(c => c.Id)
            .ToList();

        if (possible.Count > 0 && defenders.Count > 0)
        {
            var request = AttackRequestFor(active, possible, defenders);
            _attackRequest = request;
            IReadOnlyList<AttackDeclaration> declared;
            try { declared = await ControllerOf(active).DeclareAttackersAsync(ViewFor(active), possible, defenders); }
            finally { _attackRequest = null; }
            Require(declared.Select(d => d.Attacker).Distinct().Count() == declared.Count, "A creature can attack only once.");
            Require(declared.All(d => possible.Contains(d.Attacker) && defenders.Contains(d.Defender)), "Illegal attacker or defender.");
            Require(declared.All(d => d.Planeswalker is not { } pw
                                      || State.GetCard(pw) is { Zone: Zone.Battlefield } w && w.Is(CardType.Planeswalker) && w.Controller == d.Defender),
                "A planeswalker can only be attacked through its controller.");
            // Restrictions are never broken and requirements are obeyed as far as possible (508.1c-d): a declaration that
            // doesn't is illegal, like an illegal block (the request's Complete makes one legal, keeping as much of it as
            // possible).
            Require(request.IsLegal(declared, out var reason), reason ?? "Illegal attack.");

            // "Creatures can't attack you unless their controller pays {1} for each of those creatures" (rule 508.1g–h): the
            // costs are paid as attackers are declared; a declaration whose costs aren't paid is illegal and is made again.
            var total = Mana.ManaCost.Zero;
            foreach (var d in declared)
                if (AttackTax(d.Defender) is { } tax) total = total.Plus(tax);
            if (total.ManaValue > 0
                && !(Payable(active, total, null) && await PayManaAsync(State.GetPlayer(active), declared[0].Attacker, total, null)))
            {
                if (++_attackRetries < 5)
                {
                    await DeclareAttackersAsync();
                    return;
                }
                // A controller that keeps declaring attacks it won't pay for gets no taxed attacks.
                declared = declared.Where(d => AttackTax(d.Defender) is null).ToList();
                var untaxed = request.WithoutTaxedDefenders();
                if (!untaxed.IsLegal(declared, out _)) declared = untaxed.Complete(declared);
            }
            _attackRetries = 0;

            foreach (var d in declared)
            {
                combat.Attacks.Add(new AttackInfo { Attacker = d.Attacker, Defender = d.Defender, Planeswalker = d.Planeswalker });
                var attacker = State.GetCard(d.Attacker);
                if (!attacker.Has(Keyword.Vigilance))
                {
                    Tap(attacker);
                }
                Emit(new AttackerDeclared(d.Attacker, d.Defender));
                // The Ring, level 2: "Whenever your Ring-bearer attacks, draw a card, then discard a card."
                if (IsRingBearer(attacker, 2)) _pendingTriggers.Add(new PendingTrigger(attacker.Id, RingLoot, active));
            }
            // Exert (701.39): "you may exert this creature as it attacks".
            foreach (var d in declared)
            {
                var attacker = State.GetCard(d.Attacker);
                if (!attacker.Definition.Exert || attacker.LosesAbilities || (attacker.Definition.ExertIf is { } exertIf && !Holds(exertIf, active, attacker))) continue;
                if (!await ControllerOf(active).ChooseYesNoAsync(ViewFor(active), new YesNoRequest($"Exert {attacker.Name}? (It won't untap during your next untap step.)", attacker.Id))) continue;
                attacker.ExertedTurn = State.TurnNumber;
                attacker.SkipsNextUntap = true;
                Emit(new ChoiceMade(attacker.Id, "exerted"));
                var about = new TriggerInfo(attacker.Id, attacker.Version, active);
                foreach (var ability in TriggerAbilitiesOf(attacker).Where(a => a.Trigger == Abilities.TriggerEvent.Exerted && a.Filter is null))
                    AddPending(attacker.Id, ability, active, about);
                foreach (var (observer, abilities) in Observers())
                    foreach (var ability in abilities.Where(a => a.Trigger == Abilities.TriggerEvent.Exerted && a.Filter is not null))
                        if (Matches(ability.Filter!, attacker, active, observer, observer.Controller)) AddPending(observer.Id, ability, observer.Controller, about);
            }
            var activePlayer = State.GetPlayer(active);
            activePlayer.AttackersThisTurn = Math.Max(activePlayer.AttackersThisTurn, declared.Count);
            if (declared.Count > 0) Emit(new AttacksDeclared(active, declared.Count));
        }

        _skipCombatDamageSteps = combat.Attacks.Count == 0; // rule 508.8
    }

    private int _attackRetries;

    /// <summary>The attack being declared, shown to the active player while they declare it.</summary>
    private AttackRequest? _attackRequest;

    /// <summary>Who may attack whom, and the restrictions and requirements of this declaration (rule 508.1c-d).</summary>
    private AttackRequest AttackRequestFor(PlayerId active, List<CardId> possible, List<PlayerId> defenders)
    {
        var requirements = new List<AttackRequirement>();
        foreach (var id in possible)
        {
            var card = State.GetCard(id);
            if (card.Definition.AttacksEachCombat || card.Has(Keyword.AttacksEachCombat)) requirements.Add(new AttackRequirement(id, AttackRequirementKind.Attacks));
            // Each goad: "attacks each combat if able and attacks a player other than [the goader] if able" (701.15b-c).
            foreach (var goader in State.Goads.Where(g => g.Card == id && g.Version == card.Version).Select(g => g.Goader).Concat(card.StaticGoaders).Distinct())
            {
                requirements.Add(new AttackRequirement(id, AttackRequirementKind.Attacks));
                requirements.Add(new AttackRequirement(id, AttackRequirementKind.AttacksPlayerOtherThan, goader));
            }
            // "Attacks [player] this turn if able."
            foreach (var r in State.AttackPlayerRequirements.Where(r => r.Card == id && r.Version == card.Version && r.Turn == State.TurnNumber && defenders.Contains(r.Player)))
                requirements.Add(new AttackRequirement(id, AttackRequirementKind.AttacksPlayer, r.Player));
        }
        return new AttackRequest(possible, defenders)
        {
            Planeswalkers = defenders.SelectMany(d => State.PermanentsControlledBy(d).Where(p => p.Is(CardType.Planeswalker)).Select(p => new AttackablePlaneswalker(p.Id, d))).ToList(),
            Forbidden = possible.SelectMany(id => defenders
                    .Where(d => AttackForbidden(State.GetCard(id), d) || AttackForbiddenWithPlaneswalkers(State.GetCard(id), d))
                    .Select(d => new AttackForbidden(id, d, AttackForbiddenWithPlaneswalkers(State.GetCard(id), d))))
                .ToList(),
            CantAttackAlone = possible.Where(id => State.GetCard(id).Has(Keyword.CantAttackAlone)).ToList(),
            Requirements = requirements,
            Taxed = defenders.Where(d => AttackTax(d) is not null).ToList(),
        };
    }

    /// <summary>
    /// Restrictions that keep a creature from attacking a player and their planeswalkers alike, being about the defending
    /// player: "can't attack unless defending player controls an Island".
    /// </summary>
    private bool AttackForbiddenWithPlaneswalkers(Card attacker, PlayerId defender) =>
        attacker.Definition.CantAttackUnlessDefenderControls is { } needed && !attacker.LosesAbilities
        && !State.PermanentsControlledBy(defender).Any(p => Matches(needed with { Controller = Abilities.ControllerFilter.Any }, p, defender, attacker, attacker.Controller));

    /// <summary>The total cost to attack <paramref name="defender"/> with one creature, or null when attacking them is free.</summary>
    private Mana.ManaCost? AttackTax(PlayerId defender)
    {
        var total = Mana.ManaCost.Zero;
        foreach (var c in State.PermanentsControlledBy(defender))
            if (c.Definition.AttackTax is { } tax && (c.Definition.AttackTaxIf is not { } cond || Holds(cond, defender, c))) total = total.Plus(tax);
        return total.ManaValue > 0 ? total : null;
    }

    /// <summary>What attacking each opponent costs the player, and how many creatures they can pay for now.</summary>
    private IReadOnlyList<AttackTaxView> AttackTaxesFor(PlayerId player)
    {
        var result = new List<AttackTaxView>();
        foreach (var defender in State.OpponentsOf(player))
        {
            if (AttackTax(defender) is not { } tax) continue;
            int n = 0;
            var cost = Mana.ManaCost.Zero;
            while (n < 30 && Payable(player, cost.Plus(tax), null)) { cost = cost.Plus(tax); n++; }
            result.Add(new AttackTaxView(defender, tax.ToString(), n));
        }
        return result;
    }

    private bool CanAttack(Card c) =>
        c.IsCreature && !c.Tapped && !c.IsSummoningSick && (!c.Has(Keyword.Defender) || c.Has(Keyword.CanAttackWithDefender)) && !c.Has(Keyword.CantAttack)
        // "Creatures with power greater than the number of cards in your hand can't attack" (the permanent's controller's hand).
        && !State.Battlefield.Select(State.GetCard).Any(b => b.Definition.CantAttackIfPowerAboveHandSize && !b.LosesAbilities && c.Power > State.GetPlayer(b.Controller).Hand.Count);

    /// <summary>Evasion: flying can only be blocked by flying or reach (702.9b); "can't be blocked by ..." restrictions.</summary>
    private bool CanBlock(Card blocker, Card attacker) =>
        !blocker.Has(Keyword.CantBlock) && !attacker.Has(Keyword.CantBeBlocked) && !ProtectedFrom(attacker, blocker)
        && (!attacker.Has(Keyword.Flying) || blocker.Has(Keyword.Flying) || blocker.Has(Keyword.Reach))
        && attacker.Has(Keyword.Shadow) == blocker.Has(Keyword.Shadow) // shadow (702.28b)
        && !attacker.UnblockableBy.Contains(blocker.Controller)
        && !State.CantBlockThisTurn.Any(r => r.Turn == State.TurnNumber && Matches(r.Filter with { Controller = Abilities.ControllerFilter.Any }, blocker, blocker.Controller, null, r.Controller))
        && !Landwalks.Any(w => attacker.Has(w.Keyword) && State.PermanentsControlledBy(blocker.Controller).Any(c => c.Is(CardType.Land) && c.HasSubtype(w.Land)))
        && !(attacker.Has(Keyword.NonbasicLandwalk) && State.PermanentsControlledBy(blocker.Controller).Any(c => c.Is(CardType.Land) && (c.Supertypes & Supertype.Basic) == 0))
        && !(IsRingBearer(attacker, 1) && blocker.Power > attacker.Power) // the Ring, level 1
        && !(attacker.Has(Keyword.Skulk) && blocker.Power > attacker.Power) // skulk (702.118)
        && !(attacker.Definition.CantBeBlockedBy is { } restriction
             && Matches(restriction with { Controller = Abilities.ControllerFilter.Any }, blocker, blocker.Controller, attacker, attacker.Controller))
        // "Can't be blocked by creatures with power 2 or less" / "can't be blocked except by Spirits" from an effect.
        && !attacker.BlockRestrictions.Any(r => Matches(r with { Controller = Abilities.ControllerFilter.Any }, blocker, blocker.Controller, attacker, attacker.Controller));

    private static readonly (Keyword Keyword, string Land)[] Landwalks =
    {
        (Keyword.Islandwalk, "Island"), (Keyword.Swampwalk, "Swamp"), (Keyword.Forestwalk, "Forest"), (Keyword.Mountainwalk, "Mountain"), (Keyword.Plainswalk, "Plains"),
    };

    /// <summary>
    /// The life a player loses from damage: "If you control a creature, damage that would reduce your life total to less than 1
    /// reduces it to 1 instead" (a replacement on the result of the damage, which is still dealt in full).
    /// </summary>
    private int LifeLostToDamage(PlayerId playerId, int amount)
    {
        var player = State.GetPlayer(playerId);
        if (player.Life - amount < 1 && Has(playerId, Cards.Replacements.DamageCantReduceYourLifeBelowOne)
            && State.PermanentsControlledBy(playerId).Any(c => c.IsCreature))
            return Math.Max(0, player.Life - 1);
        return amount;
    }

    /// <summary>Whether a player can't lose the game now (and so whether an opponent can't win against them).</summary>
    private bool CantLose(PlayerId player) =>
        Has(player, Cards.Replacements.YouCantLose) || State.GetPlayer(player).CantLoseGameTurn == State.TurnNumber
        || State.OpponentsOf(player).Any(o => Has(o, Cards.Replacements.OpponentsCantLoseYouCantWin));

    /// <summary>"You can't win the game": a permanent saying so, or an opponent's "your opponents can't win the game (this turn)".</summary>
    private bool CantWin(PlayerId player) =>
        Has(player, Cards.Replacements.OpponentsCantLoseYouCantWin)
        // "Your opponents can't win the game" (Platinum Angel, Angel's Grace): any opponent saying so stops the win.
        || State.OpponentsOf(player).Any(o => Has(o, Cards.Replacements.YouCantLose) || State.GetPlayer(o).CantLoseGameTurn == State.TurnNumber);

    /// <summary>Whether the creature is its controller's Ring-bearer and the Ring has tempted them at least <paramref name="level"/> times.</summary>
    private bool IsRingBearer(Card card, int level) =>
        State.GetPlayer(card.Controller) is { RingBearer: { } bearer } p && bearer.Card == card.Id && bearer.Version == card.Version && p.RingLevel >= level;

    /// <summary>The combat damage a creature assigns: its power, or its toughness when it says so (rule 510.1c).</summary>
    private static int CombatDamageOf(Card creature) => creature.Has(Keyword.AssignsDamageByToughness) ? creature.Toughness : creature.Power;

    /// <summary>Fewest creatures that can block it: two with menace, or more ("except by three or more creatures").</summary>
    private static int MinimumBlockersOf(Card attacker) => Math.Max(attacker.Has(Keyword.Menace) ? 2 : 1, attacker.Definition.MinimumBlockers);

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
                attackers.Where(a => MinimumBlockersOf(a) > 1).ToDictionary(a => a.Id, MinimumBlockersOf)) // menace, 702.111b
            {
                MustBeBlocked = attackers.Where(a => a.Has(Keyword.MustBeBlocked)).Select(a => a.Id).ToList(),
                MaximumBlockers = attackers.Where(a => a.Has(Keyword.CantBeBlockedByMoreThanOne)).ToDictionary(a => a.Id, _ => 1),
                Lures = attackers.Where(a => a.Has(Keyword.Lure)).Select(a => a.Id).ToList(),                          // 509.1c
                CanBlockAny = possible.Where(id => State.GetCard(id).Has(Keyword.CanBlockAnyNumber)).ToList(),         // 509.1a
                CanBlockAdditional = possible.Where(id => State.GetCard(id).Has(Keyword.CanBlockAdditional)).ToList(), // 509.1a
                CantBlockAlone = possible.Where(id => State.GetCard(id).Has(Keyword.CantBlockAlone)).ToList(),         // 506.5
            };

            var declared = await ControllerOf(defender).DeclareBlockersAsync(ViewFor(defender), request);
            Require(request.IsLegal(declared, out var reason), reason ?? "Illegal blocks.");

            var newlyBlocked = new List<CardId>();
            foreach (var b in declared)
            {
                var attack = combat.FindAttack(b.Attacker)!;
                attack.Blockers.Add(b.Blocker);
                if (!attack.IsBlocked) newlyBlocked.Add(b.Attacker);
                attack.IsBlocked = true;
                State.BlocksThisTurn.Add((b.Blocker, State.GetCard(b.Blocker).Version, b.Attacker, State.GetCard(b.Attacker).Version));
                Emit(new BlockerDeclared(b.Blocker, b.Attacker));
                // "Whenever equipped creature blocks or becomes blocked by a creature": once for each creature on the other side.
                foreach (var (equippedOne, other) in new[] { (b.Blocker, b.Attacker), (b.Attacker, b.Blocker) })
                    foreach (var equipment in State.Battlefield.Select(State.GetCard).Where(e => e.AttachedTo == equippedOne).ToList())
                        Queue(equipment.Id, Abilities.TriggerEvent.EquippedBlocksOrBecomesBlocked, equipment.Controller,
                            new TriggerInfo(other, State.GetCard(other).Version, State.GetCard(other).Controller));
                // "Whenever this creature blocks or becomes blocked by a creature" / "blocks a creature": once per creature on the
                // other side, about that creature (rule 509.3d, 509.3f).
                foreach (var (self, other) in new[] { (b.Blocker, b.Attacker), (b.Attacker, b.Blocker) })
                    Queue(self, Abilities.TriggerEvent.BlocksOrBecomesBlockedByCreature, State.GetCard(self).Controller,
                        new TriggerInfo(other, State.GetCard(other).Version, State.GetCard(other).Controller));
                Queue(b.Blocker, Abilities.TriggerEvent.BlocksCreature, State.GetCard(b.Blocker).Controller,
                    new TriggerInfo(b.Attacker, State.GetCard(b.Attacker).Version, State.GetCard(b.Attacker).Controller));
                // The Ring, level 3: the blocker's controller sacrifices it at end of combat.
                if (IsRingBearer(State.GetCard(b.Attacker), 3)) State.SacrificeAtEndOfCombat.Add((b.Blocker, State.GetCard(b.Blocker).Version));
            }
            foreach (var attacker in newlyBlocked) Queue(attacker, Abilities.TriggerEvent.BecomesBlocked, State.GetCard(attacker).Controller);
            // "When enchanted creature blocks": once however many creatures it blocks.
            foreach (var blocker in declared.Select(b => b.Blocker).Distinct())
                foreach (var aura in State.Battlefield.Select(State.GetCard).Where(e => e.AttachedTo == blocker).ToList())
                    Queue(aura.Id, Abilities.TriggerEvent.AttachedBlocks, aura.Controller, new TriggerInfo(blocker, State.GetCard(blocker).Version, State.GetCard(blocker).Controller));
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
            int power = CombatDamageOf(attacker);
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

        // Blockers assign next (510.1d): all of it to the attacker it blocks, or divided as its controller chooses among the
        // attackers it blocks when it blocks several ("can block any number of creatures").
        foreach (var blockerId in combat.Attacks.SelectMany(a => a.Blockers).Distinct().ToList())
        {
            var blocker = State.GetCard(blockerId);
            int damage = CombatDamageOf(blocker);
            if (blocker.Zone != Zone.Battlefield || damage <= 0 || !DealsDamageNow(blocker)) continue;
            var blocked = combat.Attacks.Where(a => a.Blockers.Contains(blockerId)).Select(a => State.GetCard(a.Attacker))
                .Where(a => a.Zone == Zone.Battlefield).ToList();
            if (blocked.Count == 0) continue;
            if (blocked.Count == 1)
            {
                toCards.Add((blocker, blocked[0], damage));
                continue;
            }
            var split = await AssignBlockerDamageAsync(blocker, blocked, damage);
            foreach (var (attackerId, amount) in split.ToBlockers)
                if (amount > 0) toCards.Add((blocker, State.GetCard(attackerId), amount));
        }

        // All combat damage is dealt simultaneously (rule 510.2).
        BeginCombatDamage();
        BeginSimultaneous(); // all combat damage is one event: "one or more" triggers see it once (rule 510.2)
        var lifeGained = new Dictionary<PlayerId, int>();
        await DealDamageEventAsync(toCards.Select(d => new DamagePart(d.Source, d.Target, null, d.Amount, true))
            .Concat(toPlayers.Select(d => new DamagePart(d.Source, null, d.Target, d.Amount, true))).ToList(), lifeGained);
        foreach (var (player, amount) in lifeGained) GainLifeFor(player, amount); // lifelink (702.15b)
        EndCombatDamage();
        EndSimultaneous();
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

    /// <summary>A creature blocking several attackers divides its combat damage among them as its controller chooses (510.1d).</summary>
    private async Task<DamageAssignment> AssignBlockerDamageAsync(Card blocker, List<Card> attackers, int power)
    {
        var lethal = attackers.ToDictionary(a => a.Id, a => LethalDamage(a, blocker));
        var suggested = SuggestDamage(power, attackers, lethal, trample: false);
        var request = new DamageAssignmentRequest(blocker.Id, power, attackers.Select(a => a.Id).ToList(), lethal, false, blocker.Controller, suggested)
        {
            ByBlocker = true,
        };
        var assignment = await ControllerOf(blocker.Controller).AssignCombatDamageAsync(ViewFor(blocker.Controller), request);
        Require(assignment.ToBlockers.Keys.All(lethal.ContainsKey), "Damage can only be assigned to creatures it blocks.");
        Require(assignment.ToBlockers.Values.All(v => v >= 0) && assignment.ToPlayer == 0, "Damage amounts cannot be negative.");
        Require(assignment.Total == power, $"Must assign exactly {power} damage.");
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
        if (delta < 0 && player.CantLoseLifeTurn == State.TurnNumber) return; // "you can't lose life this turn"
        int old = player.Life;
        player.Life += delta;
        Emit(new LifeChanged(playerId, old, player.Life));
    }
}
