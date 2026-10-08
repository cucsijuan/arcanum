// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Dealing damage (rule 120): one damage event can have several parts dealt at the same time (combat damage, "deals 1 damage
/// to each creature and each player", divided damage). Before it's dealt, every part goes through the replacement and prevention
/// effects that apply to it (rules 614, 615): the affected player (or the affected permanent's controller) chooses their order
/// (rule 616.1), each applies once to the event (rule 614.5), and damage redirected to another recipient meets that recipient's
/// effects too.
/// </summary>
public sealed partial class Game
{
    /// <summary>Damage a source would deal to one creature, planeswalker or player, as part of a damage event.</summary>
    private sealed record DamagePart(Card Source, Card? ToCard, PlayerId? ToPlayer, int Amount, bool Combat)
    {
        /// <summary>Replacement and prevention effects that already modified this damage (each applies once, rule 614.5).</summary>
        public IReadOnlySet<string> Applied { get; init; } = new HashSet<string>();

        /// <summary>
        /// The resolving effect's results this damage counts toward ("each creature dealt damage this way", "excess damage"),
        /// noted when it's actually dealt (inside "simultaneously" that's after the whole event is put together). Damage
        /// redirected elsewhere doesn't keep it.
        /// </summary>
        public EffectResults? Report { get; init; }

        /// <summary>Whether damage beyond lethal counts as excess damage for <see cref="Report"/>.</summary>
        public bool CountExcess { get; init; } = true;

        /// <summary>"This damage can't be prevented" (rule 615.12): prevention effects don't apply to it.</summary>
        public bool Unpreventable { get; init; }
    }

    /// <summary>One replacement or prevention effect that could modify a part of a damage event.</summary>
    private sealed record DamageModifier(string Key, string Id, string Label, Func<int, int> Apply)
    {
        /// <summary>A plain prevention effect (its order among other plain ones changes nothing).</summary>
        public bool PlainPrevention { get; init; }
    }

    /// <summary>Damage parts of the event being put together by a "simultaneously" effect.</summary>
    private List<DamagePart>? _damageBatch;

    /// <summary>Deals damage from one source to a creature, planeswalker or player (a damage event with one part).</summary>
    private Task DealDamageAsync(Card source, Card? toCard, PlayerId? toPlayer, int amount, bool combat = false) =>
        DealDamageEventAsync(new[] { new DamagePart(source, toCard, toPlayer, amount, combat) });

    /// <summary>
    /// Deals a damage event: all its parts at the same time, after replacement and prevention effects. With
    /// <paramref name="lifelink"/>, life gained through lifelink is added up there (combat damage) instead of gained at once.
    /// </summary>
    private async Task DealDamageEventAsync(IReadOnlyList<DamagePart> parts, Dictionary<PlayerId, int>? lifelink = null)
    {
        // Inside "simultaneously": the parts join the event being put together.
        if (_damageBatch is not null && lifelink is null)
        {
            _damageBatch.AddRange(parts);
            return;
        }
        var final = await ReplaceDamageAsync(parts.Where(p => p.Amount > 0 && StillDamageable(p)).ToList());
        bool many = final.Count > 1;
        if (many) BeginSimultaneous();
        foreach (var part in final) ApplyDamage(part, lifelink);
        if (many) EndSimultaneous();
    }

    /// <summary>A creature or planeswalker still on the battlefield, or a player still in the game.</summary>
    private bool StillDamageable(DamagePart part) => part.ToCard is { } card
        ? card.Zone == Zone.Battlefield && (card.IsCreature || card.Is(CardType.Planeswalker))
        : part.ToPlayer is { } player && !State.GetPlayer(player).HasLost;

    /// <summary>Applies replacement and prevention effects to every part of a damage event; returns the damage actually dealt.</summary>
    private async Task<List<DamagePart>> ReplaceDamageAsync(List<DamagePart> parts)
    {
        var dealt = new List<DamagePart>();
        while (parts.Count > 0)
        {
            var allocations = await AllocateRedirectionsAsync(parts);
            var redirected = new List<DamagePart>();
            for (int i = 0; i < parts.Count; i++)
            {
                var (left, moved) = await ModifyDamagePartAsync(parts[i], allocations.GetValueOrDefault(i));
                if (left is { Amount: > 0 }) dealt.Add(left);
                redirected.AddRange(moved.Where(m => m.Amount > 0 && StillDamageable(m)));
            }
            parts = redirected;
        }
        return dealt;
    }

    /// <summary>
    /// "The next N damage that a source of your choice would deal to you and/or permanents you control": when one damage event
    /// would have more of it than a shield has left, the shield's controller chooses which of it is redirected. Returns, per
    /// part, how much each shield may take from it.
    /// </summary>
    private async Task<Dictionary<int, Dictionary<int, int>>> AllocateRedirectionsAsync(List<DamagePart> parts)
    {
        var result = new Dictionary<int, Dictionary<int, int>>();
        foreach (var shield in State.RedirectShields.Where(s => s.Turn == State.TurnNumber && s.Remaining > 0).ToList())
        {
            if (!RedirectTargetValid(shield)) continue;
            var eligible = Enumerable.Range(0, parts.Count).Where(i => RedirectApplies(shield, parts[i])).ToList();
            if (eligible.Count == 0) continue;
            int total = eligible.Sum(i => parts[i].Amount);
            if (total <= shield.Remaining || eligible.Count == 1)
            {
                foreach (var i in eligible) Allocate(i, shield.Remaining);
                continue;
            }
            // More of it than the shield takes: its controller chooses which damage is redirected.
            int need = shield.Remaining;
            for (int n = 0; n < eligible.Count; n++)
            {
                var part = parts[eligible[n]];
                int later = eligible.Skip(n + 1).Sum(i => parts[i].Amount);
                int min = Math.Max(0, need - later), max = Math.Min(part.Amount, need);
                int take = min;
                if (max > min)
                {
                    take = await ControllerOf(shield.Controller).ChooseNumberAsync(ViewFor(shield.Controller), new NumberRequest(
                        $"{State.GetCard(shield.Card).Name}: how much of {part.Source.Name}'s damage to {RecipientName(part)} is redirected? ({need} left)", shield.Card, min, max));
                    Require(take >= min && take <= max, $"Choose from {min} to {max}.");
                }
                Allocate(eligible[n], take);
                need -= take;
            }

            void Allocate(int part, int amount)
            {
                if (amount <= 0) return;
                if (!result.TryGetValue(part, out var byShield)) result[part] = byShield = new();
                byShield[shield.Id] = amount;
            }
        }
        return result;
    }

    private string RecipientName(DamagePart part) => part.ToCard is { } card ? card.Name : State.GetPlayer(part.ToPlayer!.Value).Name;

    /// <summary>Whether a redirection shield applies to this damage: from its source, to its controller or a permanent they control.</summary>
    private bool RedirectApplies(RedirectShield shield, DamagePart part) =>
        part.Amount > 0 && !part.Applied.Contains($"redirect:{shield.Id}") && IsSameSource(part.Source, shield.Source, shield.SourceVersion)
        && (part.ToPlayer == shield.Controller || part.ToCard?.Controller == shield.Controller);

    /// <summary>The chosen source object: the same object, or the permanent it was if it has just left the battlefield (last known information).</summary>
    private static bool IsSameSource(Card source, CardId id, int version) =>
        source.Id == id && (source.Version == version || (source.Zone != Zone.Battlefield && source.LastKnownInfo?.Version == version));

    /// <summary>Damage can be redirected only to a player still in the game or a creature or planeswalker still on the battlefield.</summary>
    private bool RedirectTargetValid(RedirectShield shield) => shield.To.Target switch
    {
        { Player: { } player } => !State.GetPlayer(player).HasLost,
        { Card: { } card } => State.GetCard(card) is { Zone: Zone.Battlefield } c && c.Version == shield.To.Version && (c.IsCreature || c.Is(CardType.Planeswalker)),
        _ => false,
    };

    /// <summary>
    /// Applies the replacement and prevention effects to one part of a damage event, in the order the affected player chooses
    /// (rule 616.1). Returns what is left to deal to the original recipient and the damage redirected elsewhere.
    /// </summary>
    private async Task<(DamagePart? Left, List<DamagePart> Redirected)> ModifyDamagePartAsync(DamagePart part, Dictionary<int, int>? redirectAllowance)
    {
        var redirected = new List<DamagePart>();
        int amount = part.Amount;
        if (amount <= 0) return (null, redirected);
        var applied = new HashSet<string>(part.Applied);
        var chooser = part.ToPlayer ?? part.ToCard!.Controller;
        int prevented = 0;
        var modifiers = DamageModifiers(part, redirectAllowance, redirected, applied, n => prevented += n);
        while (amount > 0 && modifiers.Count > 0)
        {
            var kinds = modifiers.Select(m => m.Key).Distinct().ToList();
            DamageModifier next;
            if (kinds.Count > 1 && !modifiers.All(m => m.PlainPrevention))
            {
                int pick = await ControllerOf(chooser).ChooseOptionAsync(ViewFor(chooser), new OptionRequest(
                    $"{part.Source.Name} would deal {amount} damage to {RecipientName(part)}: which effect applies next?", part.Source.Id,
                    kinds.Select(k => modifiers.First(m => m.Key == k).Label).ToList(), OptionKind.Other));
                Require(pick >= 0 && pick < kinds.Count, "Choose one of the effects.");
                next = modifiers.First(m => m.Key == kinds[pick]);
            }
            else next = modifiers.FirstOrDefault(m => m.Key == "preventAll") ?? modifiers[0];
            modifiers.Remove(next);
            applied.Add(next.Id);
            amount = next.Apply(amount);
        }
        // "Whenever damage that would be dealt to you is prevented, …" (amount: the damage prevented).
        if (prevented > 0 && part.ToPlayer is { } spared)
            foreach (var card in State.PermanentsControlledBy(spared).ToList())
                Queue(card.Id, TriggerEvent.DamageToYouPrevented, spared, new TriggerInfo(Player: spared, Amount: prevented));
        return (amount > 0 ? part with { Amount = amount, Applied = applied } : null, redirected);
    }

    /// <summary>The replacement and prevention effects that apply to this part of a damage event.</summary>
    private List<DamageModifier> DamageModifiers(DamagePart part, Dictionary<int, int>? redirectAllowance, List<DamagePart> redirected,
        HashSet<string> applied, Action<int> notePrevented)
    {
        var list = new List<DamageModifier>();
        void Add(DamageModifier m)
        {
            if (!part.Applied.Contains(m.Id)) list.Add(m);
        }
        var source = part.Source;
        bool preventable = State.DamageCantBePreventedTurn != State.TurnNumber && !part.Unpreventable; // "damage can't be prevented (this turn)"
        if (preventable)
        {
            if (PreventsAll(source, part.ToCard, part.ToPlayer, part.Combat) is { } reason)
                Add(new DamageModifier("preventAll", $"preventAll:{RecipientKey(part)}", $"Prevent all of it ({reason})", n => { notePrevented(n); return 0; }) { PlainPrevention = true });
            // "If a source an opponent controls would deal damage to you, prevent 1 of that damage."
            if (part.ToPlayer is { } player && State.OpponentsOf(player).Contains(SourceController(source)))
                foreach (var seraph in State.PermanentsControlledBy(player).Where(c => (c.Definition.Replaces & Replacements.PreventOneDamageFromOpponentsSources) != 0))
                    Add(new DamageModifier("prevent1", $"prevent1:{seraph.Id}", $"Prevent 1 of it ({seraph.Name})", n => { notePrevented(Math.Min(1, n)); return n - 1; }) { PlainPrevention = true });
            // "If damage would be dealt to this creature, prevent that damage and remove that many +1/+1 counters from it."
            if (part.ToCard is { } hydra && (hydra.Definition.Replaces & Replacements.PreventDamageRemoveCounters) != 0)
                Add(new DamageModifier("removeCounters", $"removeCounters:{hydra.Id}", $"Prevent it and remove that many +1/+1 counters ({hydra.Name})", n =>
                {
                    notePrevented(n);
                    RemoveCountersFrom(hydra, CounterKind.PlusOnePlusOne, n);
                    return 0;
                }));
        }
        // "Is dealt to any target instead" (redirection, rule 614.9).
        foreach (var (shieldId, allowance) in redirectAllowance ?? new Dictionary<int, int>())
        {
            var shield = State.RedirectShields.First(s => s.Id == shieldId);
            var to = shield.To.Target;
            string toName = to.Card is { } tc ? State.GetCard(tc).Name : State.GetPlayer(to.Player!.Value).Name;
            Add(new DamageModifier($"redirect:{shield.Id}", $"redirect:{shield.Id}", $"Deal {Math.Min(allowance, shield.Remaining)} of it to {toName} instead ({State.GetCard(shield.Card).Name})", n =>
            {
                if (!RedirectTargetValid(shield)) return n;
                int moved = Math.Min(Math.Min(allowance, shield.Remaining), n);
                if (moved <= 0) return n;
                shield.Remaining -= moved;
                var newCard = to.Card is { } id ? State.GetCard(id) : null;
                redirected.Add(new DamagePart(part.Source, newCard, to.Player, moved, part.Combat) { Applied = new HashSet<string>(applied) { $"redirect:{shield.Id}" }, Unpreventable = part.Unpreventable });
                return n - moved;
            }));
        }
        // Replacement effects that double or triple it.
        foreach (var (id, label, factor) in DamageMultipliers(source, part.ToCard, part.ToPlayer))
            Add(new DamageModifier($"x{factor}", id, label, n => n * factor));
        return list;
    }

    private static string RecipientKey(DamagePart part) => part.ToCard is { } c ? $"c{c.Id.Value}" : $"p{part.ToPlayer!.Value.Value}";

    /// <summary>The controller of a damage source (as it last existed on the battlefield if it just left).</summary>
    private PlayerId SourceController(Card source) =>
        source.Zone is not (Zone.Battlefield or Zone.Stack) && source.ZoneChangedTurn == State.TurnNumber && source.LastKnownInfo is { } lki ? lki.Controller : source.Controller;

    /// <summary>A prevention effect that prevents all of this damage, described for players; null if none applies (rule 615).</summary>
    private string? PreventsAll(Card source, Card? targetCard, PlayerId? targetPlayer, bool combat)
    {
        foreach (var shield in State.PreventionShields.Where(s => s.Turn == State.TurnNumber && (combat || !s.CombatOnly)))
            if (ShieldPrevents(shield, source, targetCard, targetPlayer)) return "a prevention effect";
        if (State.DamagePreventions.Any(d => d.Card == source.Id && d.Version == source.Version)) return "its damage is prevented";
        if (targetPlayer is { } protectedPlayer && State.GetPlayer(protectedPlayer).Protected) return "protection"; // protection from everything
        if (targetCard is not null && ProtectedFrom(targetCard, source)) return "protection"; // 702.16e
        if (targetCard is not null && (targetCard.StaticDamagePrevention == StaticDamagePrevention.All
                                       || (!combat && targetCard.StaticDamagePrevention == StaticDamagePrevention.Noncombat)))
            return "a static ability";
        if (combat)
        {
            if ((source.Definition.Replaces & Replacements.PreventCombatDamageToAndBySelf) != 0) return source.Name;
            if (targetCard is not null && ((targetCard.Definition.Replaces & Replacements.PreventCombatDamageToAndBySelf) != 0
                                           || State.CombatDamagePrevented.Contains((targetCard.Id, targetCard.Version)))) return targetCard.Name;
        }
        else if (targetCard is not null && targetCard.IsCreature
                 && State.PermanentsControlledBy(targetCard.Controller).FirstOrDefault(c => c.Id != targetCard.Id && (c.Definition.Replaces & Replacements.PreventNoncombatDamageToYourOtherCreatures) != 0) is { } guard)
            return guard.Name;
        // "During your turn, prevent all damage that would be dealt to [this]."
        if (targetCard is not null && (targetCard.Definition.Replaces & Replacements.PreventDamageToSelfDuringYourTurn) != 0 && State.ActivePlayer == targetCard.Controller)
            return targetCard.Name;
        return null;
    }

    /// <summary>
    /// A "prevent all damage this turn" shield: each qualifier it has must hold (dealt by that creature, by a matching source,
    /// to that player or to creatures that player controls); a shield with none prevents all (combat) damage.
    /// </summary>
    private bool ShieldPrevents(PreventionShield shield, Card source, Card? targetCard, PlayerId? targetPlayer)
    {
        if (shield.DealtBy is { } by && !(by.Card == source.Id && by.Version == source.Version)) return false;
        if (shield.SourceFilter is { } f && !Matches(f with { Controller = ControllerFilter.Any }, source, source.Controller, null, shield.FilterController)) return false;
        if (shield.ToPlayer is not null || shield.ToCreaturesOf is not null)
        {
            bool toPlayer = shield.ToPlayer is { } shielded && targetPlayer == shielded;
            bool toCreature = shield.ToCreaturesOf is { } owner && targetCard is { IsCreature: true } && targetCard.Controller == owner;
            if (!toPlayer && !toCreature) return false;
        }
        return true;
    }

    /// <summary>Effects that double or triple damage (one entry per instance): an id, a label and the factor (rule 614).</summary>
    private List<(string Id, string Label, int Factor)> DamageMultipliers(Card source, Card? targetCard, PlayerId? targetPlayer)
    {
        var result = new List<(string, string, int)>();
        var victim = targetPlayer ?? targetCard!.Controller;
        // A source that just left the battlefield (sacrificed to pay for its ability) is used as it last existed there.
        var lki = source.Zone is not (Zone.Battlefield or Zone.Stack) && source.ZoneChangedTurn == State.TurnNumber ? source.LastKnownInfo : null;
        var controller = lki?.Controller ?? source.Controller;
        bool creature = lki is not null ? (lki.Types & CardType.Creature) != 0 : source.IsCreature && source.Zone == Zone.Battlefield;
        if (victim != controller)
            foreach (var c in State.PermanentsControlledBy(controller).Where(c => (c.Definition.Replaces & Replacements.DoubleDamageToOpponents) != 0))
                result.Add(($"double:{c.Id}", $"Double it ({c.Name})", 2));
        if (creature)
            foreach (var c in State.PermanentsControlledBy(controller).Where(c => (c.Definition.Replaces & Replacements.DoubleCreatureDamage) != 0))
                result.Add(($"double:{c.Id}", $"Double it ({c.Name})", 2));
        if (victim != controller)
        {
            int i = 0;
            foreach (var _ in State.DamageTripled.Where(t => t.Player == controller && t.Turn == State.TurnNumber))
                result.Add(($"triple:{controller.Value}:{i++}", "Triple it", 3));
        }
        return result;
    }

    /// <summary>Deals one part of a damage event (after replacement effects): rules 120.3 and 120.4.</summary>
    private void ApplyDamage(DamagePart part, Dictionary<PlayerId, int>? lifelink)
    {
        var source = part.Source;
        int amount = part.Amount;
        if (amount <= 0 || !StillDamageable(part)) return;
        source.HasDealtDamage = true;
        void Lifelink()
        {
            if (!source.Has(Keyword.Lifelink)) return;
            if (lifelink is not null) lifelink[source.Controller] = lifelink.GetValueOrDefault(source.Controller) + amount;
            else GainLifeFor(source.Controller, amount);
        }
        if (part.ToPlayer is { } player)
        {
            Emit(new DamageDealt(source.Id, null, player, amount, part.Combat));
            ChangeLife(player, -LifeLostToDamage(player, amount));
            if (part.Combat) RecordCommanderDamage(source, player, amount);
            Lifelink();
            return;
        }
        var target = part.ToCard!;
        if (!target.IsCreature)
        {
            // Damage to a planeswalker removes that many loyalty counters (rule 120.3c).
            target.Counters[CounterKind.Loyalty] = Math.Max(0, target.CounterCount(CounterKind.Loyalty) - amount);
            part.Report?.Damaged.Add(target.Id);
            Emit(new DamageDealt(source.Id, target.Id, null, amount, part.Combat));
            Lifelink();
            return;
        }
        // Lethal damage is 1 from a deathtouch source (rule 702.2c).
        int lethalBefore = target.DamagedByDeathtouch ? 0 : Math.Max(0, target.Toughness - target.Damage);
        if (source.Has(Keyword.Deathtouch) && lethalBefore > 0) lethalBefore = 1;
        target.Damage += amount;
        if (source.Has(Keyword.Deathtouch)) target.DamagedByDeathtouch = true;
        if (part.Report is { } report)
        {
            report.Damaged.Add(target.Id);
            if (part.CountExcess) report.ExcessDamage += Math.Max(0, amount - lethalBefore); // rule 120.4a
        }
        Emit(new DamageDealt(source.Id, target.Id, null, amount, part.Combat));
        Queue(target.Id, TriggerEvent.DealtDamage, target.Controller, new TriggerInfo(target.Id, target.Version, Amount: amount)); // once for each source
        if (!part.Combat)
        {
            // "Is dealt excess noncombat damage": more than lethal damage (rule 120.4a).
            if (amount > lethalBefore) QueueObservers(TriggerEvent.ExcessNoncombatDamage, target, target.Controller, new TriggerInfo(target.Id, target.Version, target.Controller, amount - lethalBefore));
            Queue(target.Id, TriggerEvent.DealtNoncombatDamage, target.Controller, new TriggerInfo(target.Id, target.Version, Amount: amount));
        }
        Lifelink();
    }

    /// <summary>
    /// Removes counters from a permanent; each one removed triggers "whenever a [kind] counter is removed from this" once
    /// (so removing three triggers it three times).
    /// </summary>
    private void RemoveCountersFrom(Card card, CounterKind kind, int count)
    {
        int removed = Math.Min(count, card.CounterCount(kind));
        if (removed <= 0) return;
        card.Counters[kind] = card.CounterCount(kind) - removed;
        Emit(new CountersRemoved(card.Id, kind, removed));
    }

    /// <summary>
    /// "The next N damage that a source of your choice would deal to you and/or permanents you control this turn is dealt to
    /// [the target] instead": the source is chosen as the effect happens (rule 609.7a: a permanent, a spell on the stack, or an
    /// object referred to by an object on the stack).
    /// </summary>
    private async Task CreateRedirectShieldAsync(RedirectNextDamage effect, EffectContext ctx)
    {
        if (ctx.TargetAt(effect.To.Index) is null) return;
        var candidates = State.Battlefield
            .Concat(State.Stack.Select(s => s.SourceCard))
            .Concat(State.Stack.SelectMany(s => s.Targets.Select(t => t.Target.Card).OfType<CardId>()))
            .Distinct().Select(State.GetCard).ToList();
        if (candidates.Count == 0) return;
        var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: c.Zone is Zone.Battlefield or Zone.Stack)).ToList();
        var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
            new CardChoiceRequest($"{ctx.Source.Name}: choose a source of damage", ctx.Source.Id, options, 1, 1, CardChoicePurpose.DamageSource));
        Require(pick.Count == 1 && candidates.Any(c => c.Id == pick[0]), "Choose one of the sources.");
        var source = State.GetCard(pick[0]);
        Emit(new ChoiceMade(ctx.Source.Id, source.Name));
        var chosen = ctx.Targets[ctx.TargetOffset + effect.To.Index];
        State.RedirectShields.Add(new RedirectShield(State.NextRedirectShieldId++, ctx.Controller, ctx.Source.Id, source.Id, source.Version, chosen, State.TurnNumber)
        {
            Remaining = Eval(effect.Amount, ctx),
        });
    }

    // ------------------------------------------------------------------ dividing damage or counters (rule 601.2d)

    /// <summary>
    /// What the ability divides among its targets ("5 damage divided as you choose", "distribute three +1/+1 counters"): the
    /// amount and the range of target indices it's divided among; null if it divides nothing.
    /// </summary>
    private static (int Total, int From, int To)? DividedAmong(AbilityDefinition? ability, int targetCount)
    {
        if (ability is null) return null;
        static int? TotalOf(IEnumerable<Effect> effects) => effects.Select(e => e switch
        {
            DealDamageDivided dd => dd.Total,
            DistributeCounters dc => dc.Total,
            _ => (int?)null,
        }).FirstOrDefault(t => t is not null);
        if (TotalOf(ability.Effects.Where(e => e is not ModeEffects)) is { } total) return (total, 0, targetCount);
        var modes = ability.Effects.OfType<ModeEffects>().ToList();
        for (int i = 0; i < modes.Count; i++)
            if (TotalOf(EffectTree.All(new SpellAbility { Effects = modes[i].Effects })) is { } modeTotal)
                return (modeTotal, modes[i].TargetOffset, i + 1 < modes.Count ? modes[i + 1].TargetOffset : targetCount);
        return null;
    }

    /// <summary>The most targets a dividing ability can have: each target gets at least 1 (rule 601.2d).</summary>
    private static int? MaxTargetsForDivision(AbilityDefinition? ability) =>
        DividedAmong(ability, 1000) is { } d ? d.Total + d.From : null;

    /// <summary>
    /// The division of damage or counters among the chosen targets, announced as the spell is cast or the ability is put on the
    /// stack (rules 601.2d, 602.2b, 603.3d): at least 1 to each. Null when the ability divides nothing.
    /// </summary>
    private async Task<IReadOnlyList<int>?> ChooseDivisionAsync(PlayerId player, AbilityDefinition? ability, CardId source, IReadOnlyList<ChosenTarget> targets)
    {
        if (DividedAmong(ability, targets.Count) is not var (total, from, to)) return null;
        var division = new int[targets.Count];
        var chosen = Enumerable.Range(from, Math.Max(0, Math.Min(to, targets.Count) - from)).Where(i => !targets[i].Target.IsNone).ToList();
        int remaining = total;
        for (int n = 0; n < chosen.Count; n++)
        {
            int index = chosen[n], left = chosen.Count - n - 1;
            int amount = remaining;
            if (left > 0)
            {
                var target = targets[index].Target;
                var name = target.Card is { } c ? State.GetCard(c).Name : target.Player is { } p ? State.GetPlayer(p).Name : "it";
                amount = await ControllerOf(player).ChooseNumberAsync(ViewFor(player),
                    new NumberRequest($"{State.GetCard(source).Name}: how much to {name}? ({remaining} left to divide)", source, 1, remaining - left));
                Require(amount >= 1 && amount <= remaining - left, "Each target gets at least 1.");
            }
            division[index] = amount;
            remaining -= amount;
        }
        return division;
    }

    /// <summary>
    /// The announced division, by target index relative to the effect's targets. Without one (a spell or ability put on the
    /// stack some other way), it's divided now among the targets still legal.
    /// </summary>
    private async Task<List<(int Index, int Amount)>> DivisionAtResolutionAsync(EffectContext ctx, int total, string what)
    {
        int count = ctx.Targets.Count - ctx.TargetOffset;
        if (ctx.Division is { } division)
            return Enumerable.Range(0, count).Select(i => (i, ctx.TargetOffset + i < division.Count ? division[ctx.TargetOffset + i] : 0)).ToList();
        var legal = Enumerable.Range(0, count).Where(i => ctx.TargetAt(i) is not null).ToList();
        var result = new List<(int, int)>();
        int remaining = total;
        for (int n = 0; n < legal.Count && remaining > 0; n++)
        {
            int left = legal.Count - n - 1;
            int max = Math.Max(1, remaining - left);
            int amount = left == 0 ? remaining : await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                new NumberRequest($"{what}: how much to target {legal[n] + 1}? ({remaining} left)", ctx.Source.Id, 1, max));
            Require(amount >= 1 && amount <= max, "Each target gets at least 1.");
            result.Add((legal[n], amount));
            remaining -= amount;
        }
        return result;
    }
}
