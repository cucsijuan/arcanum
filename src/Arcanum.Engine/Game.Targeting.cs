// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine;

public sealed partial class Game
{
    // ------------------------------------------------------------------ targeting (rule 115)

    /// <summary>
    /// Whether the ability can be put on the stack: every required target has a legal choice (optional ones always
    /// do), and for a modal ability enough modes are possible.
    /// </summary>
    private bool HasLegalTargets(AbilityDefinition? ability, PlayerId controller, CardId source)
    {
        if (ability is null) return true;
        if (ability.Modes is { } modes)
            return modes.Count(m => m.TargetRule == TargetRule.None
                    ? m.Targets.All(spec => IsUpTo(spec) || LegalTargets(spec, controller, source).Any())
                    : HasLegalTargets(m.AsAbility() with { TargetRule = m.TargetRule }, controller, source)) // a whole choice that follows the mode's rule
                >= (ability.UpToModes || ability.ModesMayRepeat ? 1 : ability.ModeCount);
        if (!ability.Targets.All(spec => IsUpTo(spec) || LegalTargets(spec, controller, source).Any())) return false;
        if (ability.TargetRule == TargetRule.None && ability.TargetsTotalPowerAtMost is null && ability.Targets.All(t => t.AttachedToTarget is null && t.OwnedByTarget is null && t.ModeRule == TargetRule.None)) return true;
        // Rules between targets ("two target lands" must be different ones, "attached to that creature"...): there must be
        // a whole choice of the required targets that follows them (rule 601.2c), not just a legal choice for each alone.
        var legal = ability.Targets.Select(spec => LegalTargets(spec, controller, source).ToList()).ToList();
        bool Choose(int index, List<Target> before)
        {
            if (index == legal.Count) return true;
            if (IsUpTo(ability.Targets[index]))
            {
                before.Add(Target.None); // keeps later "attached to target N" indexes in place
                bool skipped = Choose(index + 1, before);
                before.RemoveAt(before.Count - 1);
                if (skipped) return true;
            }
            foreach (var candidate in legal[index])
            {
                if (candidate.IsNone || !TargetAllowed(ability, index, candidate, before)) continue;
                before.Add(candidate);
                bool found = Choose(index + 1, before);
                before.RemoveAt(before.Count - 1);
                if (found) return true;
            }
            return false;
        }
        return Choose(0, new List<Target>());
    }

    /// <summary>The cards a reflexive trigger carries ("target creature card exiled this way"), while its targets are chosen and checked.</summary>
    private IReadOnlyList<CardId>? _triggeredFound;

    /// <summary>The player a pending trigger is about, while its targets are chosen and checked ("that player controls").</summary>
    private PlayerId? _triggeredPlayer;

    /// <summary>The player the triggering attacker attacked when it was declared (<see cref="TriggerInfo.Defender"/>).</summary>
    private PlayerId? _triggeredDefender;

    /// <summary>
    /// "Defending player" for an ability about an attacking creature (rule 508.5): the player that creature is attacking, or,
    /// once it is no longer attacking (removed from combat, or gone from the battlefield), the player it was attacking.
    /// </summary>
    private PlayerId? DefendingPlayerOfTriggered()
    {
        if (_triggeredSubject is not { } attacker) return null;
        if (State.GetCard(attacker) is { Zone: Zone.Battlefield } card && card.Version == _triggeredSubjectVersion
            && State.Combat?.FindAttack(attacker) is { } attack) return attack.Defender;
        return _triggeredDefender;
    }

    private IEnumerable<Target> LegalTargets(TargetSpec spec, PlayerId controller, CardId source)
    {
        bool ControllerOk(PlayerId owner) => spec.Controller switch
        {
            ControllerFilter.You => owner == controller,
            ControllerFilter.Opponent => owner != controller,
            _ => true,
        } && (spec.OnlyControlledBy is null || owner == spec.OnlyControlledBy)
          && (!spec.ControlledByTriggeredPlayer || owner == _triggeredPlayer)
          && (!spec.ControlledByDefendingPlayer || DefendingPlayerOfTriggered() == owner);
        var sourceCard = State.GetCard(source);
        bool FilterOk(Card card) => (spec.Filter is not { } f || Matches(f with { Controller = ControllerFilter.Any }, card, card.Controller, sourceCard, controller))
            && (!spec.AttackingTriggeredPlayer || State.Combat?.FindAttack(card.Id) is { Planeswalker: null } onPlayer && onPlayer.Defender == _triggeredPlayer);

        if (spec.Optional) yield return Target.None;

        if (spec.Kind is TargetKind.Any or TargetKind.Player or TargetKind.PlayerOrPlaneswalker)
            foreach (var p in State.LivingPlayers.Where(p => ControllerOk(p.Id) && !(spec.NotTriggeredPlayer && p.Id == _triggeredPlayer)))
                if (!PlayerHasHexproof(p.Id, controller) && !PlayerProtectedFromSource(p.Id, sourceCard)
                    && !(p.Id != controller && p.HexproofFromColorsThisTurn.Turn == State.TurnNumber && State.GetPlayer(controller).IgnoresHexproofTurn != State.TurnNumber
                         && ColorsOf(sourceCard).Any(p.HexproofFromColorsThisTurn.Colors.Contains)))
                    yield return Target.Of(p.Id);

        switch (spec.Kind)
        {
            case TargetKind.Spell:
                foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Card != source && ControllerOk(s.Controller)))
                    if (FilterOk(State.GetCard(spell.Card)) && (!spec.SingleTargetOnly || spell.Targets.Count == 1)) yield return Target.Of(spell.Card);
                yield break;
            case TargetKind.SpellOrPermanent:
            case TargetKind.SpellOrCreature:
                foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Card != source && ControllerOk(s.Controller)))
                    if (FilterOk(State.GetCard(spell.Card))) yield return Target.Of(spell.Card);
                break; // and permanents below
            case TargetKind.SpellPermanentOrGraveyardCard:
                foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Card != source && ControllerOk(s.Controller)))
                    if (FilterOk(State.GetCard(spell.Card))) yield return Target.Of(spell.Card);
                // Any card in a graveyard ("nonland" is about the permanents).
                foreach (var player in State.Players)
                    foreach (var id in player.Graveyard)
                        yield return Target.Of(id);
                break; // and permanents below
            case TargetKind.SpellOrAbility:
                foreach (var item in State.Stack.Where(s => ControllerOk(s.Controller) && (!spec.SingleTargetOnly || s.Targets.Count == 1)))
                {
                    if (item is SpellOnStack sp && sp.Card != source && FilterOk(State.GetCard(sp.Card))) yield return Target.Of(sp.Card);
                    else if (item is AbilityOnStack ab && (!spec.TriggeredAbilitiesOnly || ab.Ability is TriggeredAbility)) yield return Target.OfStack(item.Id);
                }
                yield break;
            case TargetKind.Ability:
                foreach (var item in State.Stack.OfType<AbilityOnStack>().Where(s => ControllerOk(s.Controller) && (!spec.TriggeredAbilitiesOnly || s.Ability is TriggeredAbility)))
                    yield return Target.OfStack(item.Id);
                yield break;
            case TargetKind.Player:
                yield break;
            case TargetKind.GraveyardCard:
                foreach (var player in State.Players.Where(p => ControllerOk(p.Id)))
                    foreach (var id in player.Graveyard)
                        if (FilterOk(State.GetCard(id))) yield return Target.Of(id);
                yield break;
            case TargetKind.ExiledCard:
                foreach (var player in State.Players.Where(p => ControllerOk(p.Id)))
                    foreach (var id in player.Exile)
                        if (FilterOk(State.GetCard(id))) yield return Target.Of(id);
                yield break;
            case TargetKind.GraveyardOrExiledCard:
                foreach (var player in State.Players.Where(p => ControllerOk(p.Id)))
                    foreach (var id in player.Graveyard.Concat(player.Exile))
                        if (FilterOk(State.GetCard(id))) yield return Target.Of(id);
                yield break;
        }

        foreach (var card in State.Battlefield.Select(State.GetCard))
        {
            if (!ControllerOk(card.Controller) || !MatchesKind(card, spec.Kind) || !FilterOk(card)) continue;
            // "As though they didn't have hexproof": a hexproof opponent's creature can be targeted (the abilities still stop other things).
            bool ignoresHexproof = card.Controller != controller && (State.GetPlayer(controller).IgnoresHexproofTurn == State.TurnNumber
                || (card.IsCreature && State.OpponentsOf(controller).Contains(card.Controller) && HasRule(controller, ExtraRules.OpponentsCreaturesTargetableAndWardless)));
            if (card.Has(Keyword.Shroud) || (card.Has(Keyword.Hexproof) && card.Controller != controller && !ignoresHexproof)) continue; // 702.18, 702.11
            if (card.Controller != controller && !ignoresHexproof && HexproofFrom(card, sourceCard)) continue;
            if (card.Controller != controller && !ignoresHexproof && card.HexproofFromColorsThisTurn.Turn == State.TurnNumber
                && ColorsOf(sourceCard).Any(card.HexproofFromColorsThisTurn.Colors.Contains)) continue;
            if (ProtectedFrom(card, sourceCard)) continue; // 702.16b
            if (CantBeTargetedBy(card, sourceCard)) continue; // "can't be the target of nongreen spells or abilities from nongreen sources"
            if (card.Controller != controller && !ignoresHexproof && card.HasTextAbilities && (card.Definition.HexproofFromTypes & sourceCard.Types) != 0) continue;
            yield return Target.Of(card.Id);
        }
    }

    /// <summary>
    /// Protection (rule 702.16): from everything, or from a color of <paramref name="source"/> (a source that left the
    /// battlefield has the colors it last had there).
    /// </summary>
    internal static bool ProtectedFrom(Card card, Card? source)
    {
        if (card.Has(Keyword.ProtectionFromEverything)) return true;
        if (source is null) return false;
        var sourceController = source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lkc ? lkc.Controller : source.Controller;
        if (card.ProtectedFromPlayers.Contains(sourceController)) return true;
        if (card.ProtectedFromRingBearers && source.IsRingBearerNow && source.Zone == Zone.Battlefield) return true;
        if ((card.ProtectionFromTypes & (source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lkt ? lkt.Types : source.Types)) != 0) return true;
        // Protection from a creature type ("from Demons and from Dragons"): sources with that subtype, as they last existed.
        if (card.HasTextAbilities && card.Definition.ProtectionFromSubtypes.Any(t =>
                source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lks ? lks.HasSubtype(t) : source.HasSubtype(t))) return true;
        var colors = source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lki ? lki.Colors : source.Colors;
        if (colors.Count >= 2 && card.Has(Keyword.ProtectionFromMulticolored)) return true;
        if (card.AuraColorProtections.Any(p => p.Aura != source.Id && colors.Contains(p.Color))) return true;
        return colors.Any(c => Keywords.ProtectionFrom(c) is { } protection && card.Has(protection));
    }

    /// <summary>The card types a player can choose ("choose a card type"), those of traditional cards (rule 205.2a).</summary>
    private static readonly CardType[] ChoosableCardTypes =
        { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred, CardType.Land, CardType.Planeswalker, CardType.Sorcery };

    /// <summary>The card types a player has protection from: "you and creatures you control have protection from the chosen card type".</summary>
    private CardType PlayerProtectionFromTypes(PlayerId player) =>
        State.PermanentsControlledBy(player).Where(c => c.Definition.ProtectionFromChosenCardType && c.HasTextAbilities && c.ChosenType is not null)
            .Aggregate(CardType.None, (all, c) => all | Enum.Parse<CardType>(c.ChosenType!));

    /// <summary>Whether a player's protection from card types covers this source (as it last existed if it left).</summary>
    private bool PlayerProtectedFromSource(PlayerId player, Card? source) =>
        source is not null && (PlayerProtectionFromTypes(player) & (source.Zone is not (Zone.Battlefield or Zone.Stack) && source.LastKnownInfo is { } lk ? lk.Types : source.Types)) != 0;

    /// <summary>"You have hexproof" (a permanent with that static ability) protects a player from opponents' targeting.</summary>
    private bool PlayerHasHexproof(PlayerId player, PlayerId targetingPlayer) =>
        State.GetPlayer(player).Protected // protection from everything: nothing can target them
        || PlayerProtectedFrom(player, targetingPlayer)
        || (player != targetingPlayer && State.GetPlayer(targetingPlayer).IgnoresHexproofTurn != State.TurnNumber
            && (State.GetPlayer(player).HexproofTurn == State.TurnNumber
                || State.PermanentsControlledBy(player).Any(c => c.Definition.GivesControllerHexproof && c.HasTextAbilities)));

    /// <summary>"Hexproof from [color]": opponents' sources of that color can't target it.</summary>
    private static bool HexproofFrom(Card card, Card source) =>
        (card.Definition.HexproofFromColors.Count > 0 && card.HasTextAbilities && ColorsOf(source).Any(card.Definition.HexproofFromColors.Contains))
        // "Hexproof from each of its colors": the colors it has now.
        || (card.Has(Keyword.HexproofFromItsColors) && ColorsOf(source).Any(card.Colors.Contains))
        || (card.Has(Keyword.HexproofFromMonocolored) && ColorsOf(source).Distinct().Count() == 1);

    /// <summary>
    /// Whether an Aura can be attached to that permanent: everything its enchant ability says ("enchant tapped creature",
    /// "enchant creature you control") and no protection from it (rule 303.4d, 702.16c).
    /// </summary>
    private bool CanEnchant(Card aura, TargetSpec enchant, Card host) =>
        MatchesKind(host, enchant.Kind)
        && enchant.Controller switch { ControllerFilter.You => host.Controller == aura.Controller, ControllerFilter.Opponent => host.Controller != aura.Controller, _ => true }
        && (enchant.Filter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, host, host.Controller, aura, aura.Controller))
        && !ProtectedFrom(host, aura);

    private static bool MatchesKind(Card card, TargetKind kind) => kind switch
    {
        TargetKind.Any or TargetKind.Creature => card.IsCreature || (kind == TargetKind.Any && card.Is(CardType.Planeswalker)),
        TargetKind.Permanent or TargetKind.SpellOrPermanent or TargetKind.SpellPermanentOrGraveyardCard => true,
        TargetKind.SpellOrCreature => card.IsCreature,
        TargetKind.Artifact => card.Is(CardType.Artifact),
        TargetKind.Enchantment => card.Is(CardType.Enchantment),
        TargetKind.Land => card.Is(CardType.Land),
        TargetKind.Planeswalker or TargetKind.PlayerOrPlaneswalker => card.Is(CardType.Planeswalker),
        TargetKind.CreatureOrPlaneswalker => card.IsCreature || card.Is(CardType.Planeswalker),
        _ => false,
    };

    /// <summary>Asks for targets (601.2c / 603.3d). Returns an empty list for untargeted abilities, null if cancelled.</summary>
    /// <param name="affordable">For a single-target spell or ability whose cost depends on its target: the targets it can still be paid with.</param>
    private async Task<IReadOnlyList<ChosenTarget>?> ChooseTargetsAsync(
        PlayerId player, AbilityDefinition? ability, CardId source, string text, bool canCancel, Func<Target, bool>? affordable = null,
        int? maxCount = null, int? exactCount = null)
    {
        if (ability is null || ability.Targets.Count == 0) return Array.Empty<ChosenTarget>();
        var legal = ability.Targets.Select(spec => (IReadOnlyList<Target>)LegalTargets(spec, player, source)
            .Where(t => affordable is null || t.IsNone || affordable(t)).ToList()).ToList();
        // Each target of a division gets at least 1 (rule 601.2d); a copy keeps its number of targets.
        int? max = new[] { maxCount, MaxTargetsForDivision(ability, player, source), exactCount }.Where(m => m is not null).Select(m => m!.Value).DefaultIfEmpty(-1).Min() is var m0 && m0 >= 0 ? m0 : null;
        var request = new TargetRequest(source, text, ability.Targets, legal, canCancel)
        {
            MaxCount = max,
            Allowed = ability.TargetRule == TargetRule.None && ability.TargetsTotalPowerAtMost is null && ability.Targets.All(t => t.AttachedToTarget is null && t.OwnedByTarget is null && t.ModeRule == TargetRule.None)
                ? null
                : (index, candidate, before) => TargetAllowed(ability, index, candidate, before),
        };

        var chosen = await ControllerOf(player).ChooseTargetsAsync(ViewFor(player), request);
        if (chosen is null)
        {
            Require(canCancel, "These targets must be chosen.");
            _playerCancels++;
            return null;
        }
        Require(request.IsComplete(chosen.Count) && (request.LastIsAnyNumber || chosen.Count == legal.Count),
            $"Choose {legal.Count} target(s) for {State.GetCard(source).Name} (got {chosen.Count}).");
        Require(exactCount is not { } exact || chosen.Count == exact, $"Choose {exactCount} target(s).");
        // Each choice must be legal, different from earlier ones for the same "target" word (115.3), and follow the rules between targets.
        for (int i = 0; i < chosen.Count; i++)
            Require(request.IsAllowed(i, chosen[i], chosen.Take(i).ToList()), $"Illegal target {chosen[i]}.");
        return chosen.Select(t => new ChosenTarget(t, VersionOf(t))).ToList();
    }

    /// <summary>Rules between targets (different objects, different controllers, same graveyard, attached to an earlier target).</summary>
    private bool TargetAllowed(AbilityDefinition ability, int index, Target candidate, IReadOnlyList<Target> before)
    {
        if (candidate.IsNone) return true;
        var spec = ability.Targets[Math.Min(index, ability.Targets.Count - 1)];
        if (spec.AttachedToTarget is { } host)
        {
            if (host >= before.Count || before[host].Card is not { } hostCard || candidate.Card is not { } att || State.GetCard(att).AttachedTo != hostCard) return false;
        }
        if (spec.OwnedByTarget is { } ownerIndex
            && (ownerIndex >= before.Count || before[ownerIndex].Player is not { } owner || candidate.Card is not { } owned || State.GetCard(owned).Owner != owner)) return false;
        var earlier = before.Where(t => !t.IsNone).ToList();
        if (ability.TargetsTotalPowerAtMost is { } powerMax && candidate.Card is { } powered
            && earlier.Where(t => t.Card is not null).Sum(t => State.GetCard(t.Card!.Value).Power) + State.GetCard(powered).Power > powerMax)
            return false;
        if (ability.TargetsTotalManaValueAtMost is { } totalMax && candidate.Card is { } counted
            && earlier.Where(t => t.Card is not null).Sum(t => ManaValueOf(State.GetCard(t.Card!.Value))) + ManaValueOf(State.GetCard(counted)) > totalMax)
            return false;
        if (spec.ModeRule != TargetRule.None && !RuleHolds(spec.ModeRule, candidate, before.Skip(spec.ModeStart).Where(t => !t.IsNone).ToList())) return false;
        return RuleHolds(ability.TargetRule, candidate, earlier);
    }

    /// <summary>Whether a candidate target follows a rule against the targets chosen before it.</summary>
    private bool RuleHolds(TargetRule rule, Target candidate, IReadOnlyList<Target> earlier)
    {
        switch (rule)
        {
            case TargetRule.AllDifferent:
                return !earlier.Contains(candidate);
            case TargetRule.SameController:
                return !earlier.Contains(candidate) && (candidate.Card is not { } sc || earlier.All(t => t.Card is not { } e || State.GetCard(e).Controller == State.GetCard(sc).Controller));
            case TargetRule.DifferentControllers:
                return candidate.Card is not { } c || earlier.All(t => t.Card is not { } e || State.GetCard(e).Controller != State.GetCard(c).Controller);
            case TargetRule.SameGraveyard:
                return candidate.Card is not { } g || earlier.All(t => t.Card is not { } e || State.GetCard(e).Owner == State.GetCard(g).Owner);
            case TargetRule.ShareCreatureType:
                return !earlier.Contains(candidate) && (candidate.Card is not { } sct || earlier.All(t => t.Card is not { } e || SharesCreatureType(State.GetCard(e), State.GetCard(sct))));
            case TargetRule.ShareCardType: // different objects that share a card type
                return !earlier.Contains(candidate) && (candidate.Card is not { } s || earlier.All(t => t.Card is not { } e || (State.GetCard(e).Types & State.GetCard(s).Types
                    & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle)) != 0));
        }
        return true;
    }

    private static TargetSpec SpecAt(AbilityDefinition ability, int index) => ability.Targets[Math.Min(index, ability.Targets.Count - 1)];

    /// <summary>Every spell put on the stack, by card and object version, as it last was there (for copying a spell that has left it).</summary>
    private readonly Dictionary<(CardId Card, int Version), SpellOnStack> _spellsOnStack = new();

    private void PushStack(StackItem item)
    {
        item = item with { Id = State.NextStackId++ };
        State.Stack.Add(item);
        if (item is SpellOnStack pushed) _spellsOnStack[(pushed.Card, State.GetCard(pushed.Card).Version)] = pushed;
        QueueBecameTarget(item, item.Targets.Select(t => t.Target.Card).OfType<CardId>());
        QueueCrime(item);
        if (item is SpellOnStack)
            foreach (var aimed in item.Targets.Select(t => t.Target.Card).Where(c => c is not null).Distinct().Select(c => State.GetCard(c!.Value)).Where(c => c.Zone == Zone.Battlefield).ToList())
            {
                QueueObservers(TriggerEvent.BecomesTargetOfSpell, aimed, aimed.Controller);
                // "Whenever this becomes the target of a spell an opponent controls."
                if (State.OpponentsOf(aimed.Controller).Contains(item.Controller))
                    Queue(aimed.Id, TriggerEvent.BecomesTargetOfOpponentSpell, aimed.Controller, new TriggerInfo(aimed.Id, aimed.Version, item.Controller));
            }
        // "Whenever you become the target of a spell or ability an opponent controls" (rule 603.2): a player targeted, once however often.
        foreach (var targetedPlayer in item.Targets.Select(t => t.Target.Player).OfType<PlayerId>().Distinct().ToList())
        {
            if (!State.OpponentsOf(targetedPlayer).Contains(item.Controller)) continue;
            foreach (var (watcher, abilities) in Observers().Where(o => o.Card.Controller == targetedPlayer).ToList())
                foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouBecomeTargetOfOpponent))
                    AddPending(watcher.Id, ability, watcher.Controller, new TriggerInfo(Player: item.Controller, Amount: item.Id));
        }
        // Ward (702.21): becoming the target of an opponent's spell or ability triggers "counter it unless that player pays".
        foreach (var target in item.Targets.Select(t => t.Target.Card).Where(c => c is not null).Distinct())
        {
            var warded = State.GetCard(target!.Value);
            if (warded.Zone != Zone.Battlefield || warded.Controller == item.Controller) continue;
            Queue(warded.Id, TriggerEvent.BecomesTargetOfOpponent, warded.Controller, new TriggerInfo(Player: item.Controller));
            // Each instance of ward triggers separately (rule 702.21b): printed, then granted by other permanents.
            var wards = new List<(ManaCost Mana, int Life)>();
            if (warded.HasTextAbilities && (warded.Definition.WardMana is not null || warded.Definition.WardLife > 0)) wards.Add((warded.Definition.WardMana ?? ManaCost.Zero, warded.Definition.WardLife));
            wards.AddRange(warded.GrantedWards.Select(w => (w, 0)));
            wards.AddRange(warded.GrantedWardLives.Select(l => (ManaCost.Zero, l)));
            var wardCosts = wards.Select(w => (w.Mana, w.Life, Extra: (ExtraCost?)null)).ToList();
            if (warded.HasTextAbilities && warded.Definition.WardCost is { } wardCost) wardCosts.Add((ManaCost.Zero, 0, wardCost));
            // "Ward—Pay life equal to [its] power": the life is worked out as the ward ability resolves.
            if (warded.HasTextAbilities && warded.Definition.WardLifeFromPower) wardCosts.Add((ManaCost.Zero, -1, null));
            // "Ward abilities of those creatures don't trigger": a creature of an opponent of a player with that rule.
            if (warded.IsCreature && State.OpponentsOf(warded.Controller).Any(p => HasRule(p, ExtraRules.OpponentsCreaturesTargetableAndWardless))) wardCosts.Clear();
            foreach (var (mana, life, extra) in wardCosts)
            {
                var ward = new TriggeredAbility
                {
                    Trigger = TriggerEvent.EntersBattlefield,
                    Effects = new Effect[] { new CounterUnlessPays(item.Id, mana, Math.Max(0, life)) { Extra = extra, LifeFromPowerOf = life < 0 ? warded.Id : null } },
                    Text = life < 0 ? $"Ward — counter it unless its controller pays life equal to {warded.Name}'s power."
                        : $"Ward — counter it unless its controller pays {(extra is not null ? DescribeCost(extra).ToLowerInvariant() : DescribeWard(mana, life))}.",
                };
                for (int copies = 1 + ExtraTriggersFor(warded); copies > 0; copies--) _pendingTriggers.Add(new PendingTrigger(warded.Id, ward, warded.Controller));
            }
        }
    }

    /// <summary>
    /// Committing a crime (rule 700.13): a spell cast, or an ability activated or put on the stack, that targets an opponent of its
    /// controller, a permanent or spell an opponent controls, or a card in an opponent's graveyard. A copy isn't cast.
    /// </summary>
    private void QueueCrime(StackItem item)
    {
        if (item is SpellOnStack spell && State.GetCard(spell.Card).Definition.IsToken) return;
        var opponents = State.OpponentsOf(item.Controller).ToHashSet();
        bool crime = item.Targets.Any(t => t.Target.Player is { } p ? opponents.Contains(p)
            : t.Target.Card is { } c && State.GetCard(c) is var aimed
              && (aimed.Zone is Zone.Battlefield or Zone.Stack ? opponents.Contains(aimed.Controller) : aimed.Zone == Zone.Graveyard && opponents.Contains(aimed.Owner)));
        if (!crime) return;
        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == item.Controller).ToList())
            foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouCommitCrime))
                AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(Player: item.Controller));
    }

    /// <summary>
    /// "When this becomes the target of a spell or ability" / "when enchanted creature becomes the target …": permanents a spell
    /// or ability (anyone's) now targets, each once however many times it's targeted (rule 603.2).
    /// </summary>
    private void QueueBecameTarget(StackItem item, IEnumerable<CardId> targeted)
    {
        // "Whenever one or more creatures you control become the target of an activated ability": once for each such ability and each player whose creatures it targets.
        if (item is AbilityOnStack { AbilityIndex: not null })
            foreach (var group in targeted.Distinct().Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield && c.IsCreature).GroupBy(c => c.Controller).ToList())
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == group.Key).ToList())
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.CreaturesYouControlTargetedByActivatedAbility))
                        AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(group.First().Id, group.First().Version, item.Controller));
        // "A creature spell you control becomes the target of a spell or ability an opponent controls."
        foreach (var aimedSpell in targeted.Distinct().Select(State.GetCard).Where(c => c.Zone == Zone.Stack).ToList())
            foreach (var (observer, abilities) in Observers())
                foreach (var ability in abilities.Where(a => a.SpellsToo && a.Trigger == TriggerEvent.PermanentBecomesTargetOfOpponent))
                    if (State.OpponentsOf(observer.Controller).Contains(item.Controller)
                        && Matches(ability.Filter ?? ObjectFilter.YourCreatures, aimedSpell, aimedSpell.Controller, observer, observer.Controller))
                        AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(aimedSpell.Id, aimedSpell.Version, item.Controller));
        foreach (var aimed in targeted.Distinct().Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList())
        {
            Queue(aimed.Id, TriggerEvent.BecomesTarget, aimed.Controller, new TriggerInfo(aimed.Id, aimed.Version, item.Controller));
            State.TargetedThisTurn.RemoveAll(t => t.Turn != State.TurnNumber);
            State.TargetedThisTurn.Add((aimed.Id, aimed.Version, item.Controller, State.TurnNumber));
            QueueTargetedByController(item, aimed);
            foreach (var attached in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedTo == aimed.Id).ToList())
                Queue(attached.Id, TriggerEvent.AttachedBecomesTarget, attached.Controller, new TriggerInfo(aimed.Id, aimed.Version, item.Controller));
        }
    }

    private static string DescribeWard(ManaCost mana, int life) =>
        string.Join(" and ", new[] { mana.ManaValue > 0 ? mana.ToString() : null, life > 0 ? $"{life} life" : null }.Where(x => x is not null));

    private int VersionOf(Target target) => target.Card is { } c ? State.GetCard(c).Version : 0;

    private bool IsStillLegal(ChosenTarget chosen, TargetSpec spec, PlayerId controller, CardId source)
    {
        if (chosen.Target.IsNone) return false;
        if (chosen.Target.StackObject is { } so) return State.Stack.Any(s => s.Id == so);
        if (chosen.Target.Card is { } card && State.GetCard(card).Version != chosen.Version) return false; // new object
        return LegalTargets(spec, controller, source).Contains(chosen.Target);
    }

    // ------------------------------------------------------------------ modes (rule 700.2)

    /// <summary>
    /// For a modal ability, asks which modes to use and returns the ability narrowed to them (their targets in
    /// order, each mode's effects reading its own targets). Non-modal abilities come back unchanged; null if cancelled.
    /// </summary>
    private async Task<T?> ChooseModesAsync<T>(PlayerId player, T ability, CardId source, bool canCancel) where T : AbilityDefinition
    {
        if (ability.Modes is not { } modes) return ability;
        var sourceCard = State.GetCard(source);
        if (ability.ModesOncePerTurn && sourceCard.ModesTurn != State.TurnNumber)
        {
            sourceCard.ChosenModes.Clear();
            sourceCard.ModesTurn = State.TurnNumber;
        }
        if (ability.ExtraModeIf is { } extraMode && Holds(extraMode, player, sourceCard)) ability = ability with { ModeCount = ability.ModeCount + 1, UpToModes = true, ExtraModeIf = null };
        if (ability.AllModesIf is { } allModes && Holds(allModes, player, sourceCard)) ability = ability with { ModeCount = ability.Modes.Count, UpToModes = true, AllModesIf = null };
        var possible = Enumerable.Range(0, modes.Count)
            .Where(i => modes[i].TargetRule == TargetRule.None
                ? modes[i].Targets.All(spec => IsUpTo(spec) || LegalTargets(spec, player, source).Any())
                : HasLegalTargets(modes[i].AsAbility() with { TargetRule = modes[i].TargetRule }, player, source))
            .Where(i => !(ability.ModesOncePerObject || ability.ModesOncePerTurn) || !sourceCard.ChosenModes.Contains(i))
            .ToList();
        int min = ability.UpToModes ? 1 : Math.Min(ability.ModeCount, possible.Count);
        int max = Math.Min(ability.ModeCount, possible.Count);
        if (possible.Count == 0) return null;
        // "You may choose two": choosing none removes the ability from the stack.
        if (ability.MayChooseModes && (possible.Count < ability.ModeCount
            || !await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"{sourceCard.Name}: choose {ability.ModeCount} modes?", source))))
            return null;
        IReadOnlyList<int> chosen;
        if (ability.RandomMode)
        {
            int random = possible[Rng.Next(possible.Count)];
            Emit(new ChoiceMade(source, $"chose at random: {modes[random].Text}"));
            return ability.WithModes(new[] { random });
        }
        if (ability.PawprintBudget > 0)
        {
            // "Choose up to five {P} worth of modes. You may choose the same mode more than once": one mode at a time, at least one,
            // until the pawprints are used up or the player stops.
            var picks = new List<int>();
            for (int left = ability.PawprintBudget; ;)
            {
                var affordable = possible.Where(i => modes[i].Pawprints <= left).ToList();
                if (affordable.Count == 0) break;
                var request = new ModeRequest(source, $"{ability.Text} ({left} {{P}} left)", modes.Select(m => m.Text).ToList(), affordable, picks.Count == 0 ? 1 : 0, 1, canCancel && picks.Count == 0);
                var answer = await ControllerOf(player).ChooseModesAsync(ViewFor(player), request);
                if (answer is null)
                {
                    Require(canCancel && picks.Count == 0, "Modes must be chosen.");
                    _playerCancels++;
                    return null;
                }
                if (answer.Count == 0) { Require(picks.Count > 0, "Choose at least one mode."); break; }
                Require(answer.Count == 1 && affordable.Contains(answer[0]), "Choose one of the possible modes.");
                picks.Add(answer[0]);
                left -= modes[answer[0]].Pawprints;
            }
            return ability.WithModes(picks.OrderBy(i => i).ToList());
        }
        if (ability.ModesMayRepeat)
        {
            // "Choose three. You may choose the same mode more than once": each choice is one mode.
            var picks = new List<int>();
            for (int n = 0; n < ability.ModeCount; n++)
            {
                var request = new ModeRequest(source, $"{ability.Text} (choice {n + 1} of {ability.ModeCount})", modes.Select(m => m.Text).ToList(), possible, 1, 1, canCancel && n == 0);
                var answer = await ControllerOf(player).ChooseModesAsync(ViewFor(player), request);
                if (answer is null)
                {
                    Require(canCancel && n == 0, "Modes must be chosen.");
                    _playerCancels++;
                    return null;
                }
                Require(answer.Count == 1 && possible.Contains(answer[0]), "Choose one of the possible modes.");
                picks.Add(answer[0]);
            }
            return ability.WithModes(picks.OrderBy(i => i).ToList());
        }
        if (possible.Count == 1 && min == 1) chosen = possible;
        else
        {
            var request = new ModeRequest(source, ability.Text, modes.Select(m => m.Text).ToList(), possible, min, max, canCancel);
            var answer = await ControllerOf(player).ChooseModesAsync(ViewFor(player), request);
            if (answer is null)
            {
                Require(canCancel, "Modes must be chosen.");
                _playerCancels++;
                return null;
            }
            Require(answer.Count >= min && answer.Count <= max && answer.Distinct().Count() == answer.Count && answer.All(possible.Contains),
                $"Choose {min}–{max} of the possible modes.");
            chosen = answer.OrderBy(i => i).ToList();
        }
        if (ability.ModesOncePerObject || ability.ModesOncePerTurn) sourceCard.ChosenModes.UnionWith(chosen);
        return ability.WithModes(chosen);
    }
}
