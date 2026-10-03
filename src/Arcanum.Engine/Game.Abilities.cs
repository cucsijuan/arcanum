// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

public sealed partial class Game
{
    private sealed record PendingTrigger(CardId Source, TriggeredAbility Ability, PlayerId Controller);

    private readonly List<PendingTrigger> _pendingTriggers = new();

    // ------------------------------------------------------------------ targeting (rule 115)

    private bool HasLegalTargets(AbilityDefinition? ability, PlayerId controller, CardId source) =>
        ability is null || ability.Targets.All(spec => LegalTargets(spec, controller, source).Any());

    private IEnumerable<Target> LegalTargets(TargetSpec spec, PlayerId controller, CardId source)
    {
        bool ControllerOk(PlayerId owner) => spec.Controller switch
        {
            ControllerFilter.You => owner == controller,
            ControllerFilter.Opponent => owner != controller,
            _ => true,
        };

        if (spec.Kind is TargetKind.Any or TargetKind.Player)
            foreach (var p in State.LivingPlayers.Where(p => ControllerOk(p.Id)))
                yield return Target.Of(p.Id);

        if (spec.Kind == TargetKind.Spell)
        {
            foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Card != source && ControllerOk(s.Controller)))
                yield return Target.Of(spell.Card);
            yield break;
        }
        if (spec.Kind == TargetKind.Player) yield break;

        foreach (var card in State.Battlefield.Select(State.GetCard))
        {
            if (!ControllerOk(card.Controller) || !MatchesKind(card, spec.Kind)) continue;
            if (card.Has(Keyword.Shroud) || (card.Has(Keyword.Hexproof) && card.Controller != controller)) continue; // 702.18, 702.11
            yield return Target.Of(card.Id);
        }
    }

    private static bool MatchesKind(Card card, TargetKind kind) => kind switch
    {
        TargetKind.Any or TargetKind.Creature => card.IsCreature || (kind == TargetKind.Any && card.Is(CardType.Planeswalker)),
        TargetKind.Permanent => true,
        TargetKind.Artifact => card.Is(CardType.Artifact),
        TargetKind.Enchantment => card.Is(CardType.Enchantment),
        TargetKind.Land => card.Is(CardType.Land),
        _ => false,
    };

    /// <summary>Asks for targets (601.2c / 603.3d). Returns an empty list for untargeted abilities, null if cancelled.</summary>
    private async Task<IReadOnlyList<ChosenTarget>?> ChooseTargetsAsync(
        PlayerId player, AbilityDefinition? ability, CardId source, string text, bool canCancel)
    {
        if (ability is null || ability.Targets.Count == 0) return Array.Empty<ChosenTarget>();
        var legal = ability.Targets.Select(spec => (IReadOnlyList<Target>)LegalTargets(spec, player, source).ToList()).ToList();
        var request = new TargetRequest(source, text, ability.Targets, legal, canCancel);

        var chosen = await ControllerOf(player).ChooseTargetsAsync(ViewFor(player), request);
        if (chosen is null)
        {
            Require(canCancel, "These targets must be chosen.");
            return null;
        }
        Require(chosen.Count == legal.Count, $"Choose exactly {legal.Count} target(s).");
        for (int i = 0; i < chosen.Count; i++) Require(legal[i].Contains(chosen[i]), $"Illegal target {chosen[i]}.");
        return chosen.Select(t => new ChosenTarget(t, VersionOf(t))).ToList();
    }

    private int VersionOf(Target target) => target.Card is { } c ? State.GetCard(c).Version : 0;

    private bool IsStillLegal(ChosenTarget chosen, TargetSpec spec, PlayerId controller, CardId source)
    {
        if (chosen.Target.Card is { } card && State.GetCard(card).Version != chosen.Version) return false; // new object
        return LegalTargets(spec, controller, source).Contains(chosen.Target);
    }

    // ------------------------------------------------------------------ resolution (rule 608)

    /// <summary>Carries out a resolving spell's or ability's effects. Returns false if every target became illegal.</summary>
    private async Task<bool> ApplyResolutionAsync(StackItem item, AbilityDefinition ability, Card source)
    {
        var legal = new bool[item.Targets.Count];
        for (int i = 0; i < item.Targets.Count; i++)
            legal[i] = IsStillLegal(item.Targets[i], ability.Targets[i], item.Controller, source.Id);
        if (item.Targets.Count > 0 && !legal.Any(l => l)) return false; // 608.2b

        // An intervening "if" clause is checked again on resolution (rule 603.4).
        if (ability is TriggeredAbility { Condition: { } condition } && !Holds(condition, item.Controller, source)) return true;

        var context = new EffectContext(item.Controller, source, item.Targets, legal);
        await ApplyAllAsync(ability.Effects, context);
        RecomputeContinuousEffects();
        return true;
    }

    private async Task ApplyAllAsync(IEnumerable<Effect> effects, EffectContext ctx)
    {
        foreach (var effect in effects)
        {
            if (State.IsGameOver) return;
            await ApplyAsync(effect, ctx);
        }
    }

    private sealed record EffectContext(PlayerId Controller, Card Source, IReadOnlyList<ChosenTarget> Targets, bool[] TargetLegal);

    private IEnumerable<Card> CardsFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.Target when subject.Index < ctx.Targets.Count && ctx.TargetLegal[subject.Index]
                                && ctx.Targets[subject.Index].Target.Card is { } id => new[] { State.GetCard(id) },
        SubjectKind.Self when ctx.Source.Zone == Zone.Battlefield => new[] { ctx.Source },
        _ => Array.Empty<Card>(),
    };

    private IEnumerable<PlayerId> PlayersFor(Subject subject, EffectContext ctx) => subject.Kind switch
    {
        SubjectKind.You => new[] { ctx.Controller },
        SubjectKind.EachOpponent => State.OpponentsOf(ctx.Controller).ToList(),
        SubjectKind.EachPlayer => State.LivingPlayers.Select(p => p.Id).ToList(),
        SubjectKind.Target when subject.Index < ctx.Targets.Count && ctx.TargetLegal[subject.Index]
                                && ctx.Targets[subject.Index].Target.Player is { } p => new[] { p },
        SubjectKind.TargetController when subject.Index < ctx.Targets.Count && ctx.TargetLegal[subject.Index]
                                          && ctx.Targets[subject.Index].Target.Card is { } c => new[] { State.GetCard(c).Controller },
        _ => Array.Empty<PlayerId>(),
    };

    private async Task ApplyAsync(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case Scry sc:
                await LookAtTopAsync(ctx.Controller, sc.Count, CardChoicePurpose.ScryToBottom, ctx.Source);
                break;
            case Surveil sv:
                await LookAtTopAsync(ctx.Controller, sv.Count, CardChoicePurpose.SurveilToGraveyard, ctx.Source);
                break;
            case Fight f:
            {
                var first = CardsFor(f.First, ctx).FirstOrDefault();
                var second = CardsFor(f.Second, ctx).FirstOrDefault();
                // If either creature is gone (or no longer a creature), no damage is dealt (rule 701.14b).
                if (first is null || second is null || !first.IsCreature || !second.IsCreature) break;
                int firstPower = first.Power, secondPower = second.Power;
                DamageCreature(first, second, firstPower);
                if (first.Id != second.Id) DamageCreature(second, first, secondPower);
                break;
            }
            case Discard d:
                foreach (var player in PlayersFor(d.Who, ctx)) await DiscardAsync(player, d.Count);
                break;
            case IfThen c:
                await ApplyAllAsync(Holds(c.Condition, ctx.Controller, ctx.Source) ? c.Then : c.Else ?? Array.Empty<Effect>(), ctx);
                break;
            case MayDo m:
                if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(m.Prompt, ctx.Source.Id)))
                    await ApplyAllAsync(m.Effects, ctx);
                break;
            case DealDamage d:
                foreach (var card in CardsFor(d.To, ctx)) DamageCreature(ctx.Source, card, d.Amount);
                foreach (var player in PlayersFor(d.To, ctx)) DamagePlayer(ctx.Source, player, d.Amount);
                break;
            case DrawCards d:
                foreach (var player in PlayersFor(d.Who, ctx)) Draw(player, d.Count);
                break;
            case GainLife g:
                foreach (var player in PlayersFor(g.Who, ctx)) ChangeLife(player, g.Amount);
                break;
            case LoseLife l:
                foreach (var player in PlayersFor(l.Who, ctx)) ChangeLife(player, -l.Amount);
                break;
            case Destroy d:
                foreach (var card in CardsFor(d.What, ctx).Where(c => !c.Has(Keyword.Indestructible)))
                {
                    bool creature = card.IsCreature;
                    MoveCard(card.Id, Zone.Graveyard);
                    Emit(new PermanentDestroyed(card.Id));
                    if (creature) Emit(new CreatureDied(card.Id));
                }
                break;
            case ExileIt x:
                foreach (var card in CardsFor(x.What, ctx)) MoveCard(card.Id, Zone.Exile);
                break;
            case ReturnToHand r:
                foreach (var card in CardsFor(r.What, ctx)) MoveCard(card.Id, Zone.Hand);
                break;
            case TapIt t:
                foreach (var card in CardsFor(t.What, ctx).Where(c => !c.Tapped)) { card.Tapped = true; Emit(new PermanentTapped(card.Id)); }
                break;
            case UntapIt u:
                foreach (var card in CardsFor(u.What, ctx).Where(c => c.Tapped)) { card.Tapped = false; Emit(new PermanentUntapped(card.Id)); }
                break;
            case Mill m:
                foreach (var player in PlayersFor(m.Who, ctx))
                    foreach (var id in State.GetPlayer(player).Library.Take(m.Count).ToList()) MoveCard(id, Zone.Graveyard);
                break;
            case CounterSpell c:
                if (c.What.Kind == SubjectKind.Target && c.What.Index < ctx.Targets.Count && ctx.TargetLegal[c.What.Index]
                    && ctx.Targets[c.What.Index].Target.Card is { } spellCard)
                {
                    State.Stack.RemoveAll(s => s is SpellOnStack sp && sp.Card == spellCard);
                    MoveCard(spellCard, Zone.Graveyard);
                    Emit(new SpellCountered(spellCard));
                }
                break;
            case PumpUntilEndOfTurn p:
                foreach (var card in CardsFor(p.What, ctx).Where(c => c.IsCreature))
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, p.Power, p.Toughness,
                        p.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>()));
                break;
            case AddCounters a:
                foreach (var card in CardsFor(a.What, ctx))
                {
                    card.Counters[a.Kind] = card.CounterCount(a.Kind) + a.Count;
                    Emit(new CountersPlaced(card.Id, a.Kind, a.Count));
                }
                break;
            case AttachSelf a:
                if (ctx.Source.Zone == Zone.Battlefield)
                    foreach (var card in CardsFor(a.To, ctx)) ctx.Source.AttachedTo = card.Id;
                break;
            case CreateTokens t:
                foreach (var player in PlayersFor(t.Controller, ctx))
                    for (int i = 0; i < t.Count; i++) CreateToken(t.Token, player);
                break;
            default:
                throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
        }
    }

    /// <summary>Non-combat damage from a spell or ability (rule 120), with deathtouch and lifelink.</summary>
    private void DamageCreature(Card source, Card target, int amount)
    {
        if (amount <= 0 || target.Zone != Zone.Battlefield || !target.IsCreature) return;
        target.Damage += amount;
        if (source.Has(Keyword.Deathtouch)) target.DamagedByDeathtouch = true;
        Emit(new DamageDealt(source.Id, target.Id, null, amount));
        if (source.Has(Keyword.Lifelink)) ChangeLife(source.Controller, amount);
    }

    private void DamagePlayer(Card source, PlayerId player, int amount)
    {
        if (amount <= 0) return;
        Emit(new DamageDealt(source.Id, null, player, amount));
        ChangeLife(player, -amount);
        if (source.Has(Keyword.Lifelink)) ChangeLife(source.Controller, amount);
    }

    private void CreateToken(CardDefinition definition, PlayerId controller)
    {
        var token = definition with { IsToken = true };
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        var card = new Card(id, token, controller) { Zone = Zone.Battlefield };
        State.Cards.Add(id, card);
        State.Battlefield.Add(id);
        Emit(new TokenCreated(id, controller));
        Emit(new CardMoved(id, controller, Zone.Exile, Zone.Battlefield, controller));
    }

    /// <summary>Scry or surveil: the player looks at the top cards and picks which ones leave the top.</summary>
    private async Task LookAtTopAsync(PlayerId who, int count, CardChoicePurpose purpose, Card source)
    {
        var player = State.GetPlayer(who);
        var top = player.Library.Take(count).ToList();
        if (top.Count == 0) return;
        bool scry = purpose == CardChoicePurpose.ScryToBottom;
        var prompt = scry
            ? $"Scry {count}: choose cards to put on the bottom of your library"
            : $"Surveil {count}: choose cards to put into your graveyard";
        var options = top.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
        var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(prompt, source.Id, options, 0, top.Count, purpose));
        Require(chosen.Distinct().Count() == chosen.Count && chosen.All(top.Contains), "Choose among the cards looked at.");

        foreach (var id in chosen)
        {
            if (scry)
            {
                player.Library.Remove(id);
                player.Library.Add(id);
            }
            else MoveCard(id, Zone.Graveyard);
        }
        Emit(new LookedAtTop(who, top.Count, chosen.Count, scry));
    }

    private async Task DiscardAsync(PlayerId who, int count)
    {
        var player = State.GetPlayer(who);
        count = Math.Min(count, player.Hand.Count);
        if (count == 0) return;
        var chosen = await ControllerOf(who).ChooseDiscardAsync(ViewFor(who), count);
        Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(player.Hand.Contains),
            $"Must discard exactly {count} distinct cards from hand.");
        foreach (var card in chosen)
        {
            MoveCard(card, Zone.Graveyard);
            Emit(new CardDiscarded(who, card));
        }
    }

    // ------------------------------------------------------------------ conditions and filters

    private bool Holds(Condition condition, PlayerId controller, Card? source)
    {
        var player = State.GetPlayer(controller);
        return condition switch
        {
            AttackedThisTurn => player.AttackedThisTurn,
            CreatureDiedThisTurn => State.CreaturesDiedThisTurn > 0,
            GainedLifeThisTurn g => player.LifeGainedThisTurn >= g.AtLeast,
            CardsInGraveyard g => player.Graveyard.Count >= g.AtLeast,
            YouControl y => State.Battlefield.Select(State.GetCard).Count(c => Matches(y.Filter, c, c.Controller, source, controller)) >= y.AtLeast,
            LifeAtLeast l => player.Life >= l.Amount,
            Not n => !Holds(n.Inner, controller, source),
            _ => throw new NotSupportedException($"Condition {condition.GetType().Name} is not implemented."),
        };
    }

    /// <summary>Whether <paramref name="obj"/> (controlled by <paramref name="objController"/>) fits the filter, seen from the ability's side.</summary>
    private static bool Matches(ObjectFilter filter, Card obj, PlayerId objController, Card? source, PlayerId sourceController)
    {
        if (filter.Types != 0 && (obj.Types & filter.Types) == 0) return false;
        if ((obj.Types & filter.ExcludedTypes) != 0) return false;
        if (filter.Subtype is { } subtype && !obj.Definition.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase)) return false;
        if (filter.Controller == ControllerFilter.You && objController != sourceController) return false;
        if (filter.Controller == ControllerFilter.Opponent && objController == sourceController) return false;
        if (filter.Other && source is not null && obj.Id == source.Id) return false;
        if (filter.MinPower is { } min && obj.Power < min) return false;
        if (filter.Token is { } token && obj.Definition.IsToken != token) return false;
        return true;
    }

    // ------------------------------------------------------------------ continuous effects (rule 611, 613)

    /// <summary>
    /// Recomputes characteristics changed by continuous effects: static abilities of permanents on the battlefield,
    /// then "until end of turn" effects. Only additive P/T changes (layer 7c) and keyword grants (layer 6) exist so
    /// far, so the order within a layer doesn't change the result.
    /// </summary>
    private void RecomputeContinuousEffects()
    {
        var battlefield = State.Battlefield.Select(State.GetCard).ToList();
        foreach (var card in battlefield)
        {
            card.PowerBonus = 0;
            card.ToughnessBonus = 0;
            card.GrantedKeywords.Clear();
        }
        foreach (var source in battlefield)
        {
            foreach (var ability in source.Definition.Abilities.OfType<StaticAbility>())
            {
                foreach (var affected in Affected(source, ability.Affects, battlefield))
                {
                    affected.PowerBonus += ability.Power;
                    affected.ToughnessBonus += ability.Toughness;
                    affected.GrantedKeywords.UnionWith(ability.GrantedKeywords);
                }
            }
        }
        State.UntilEndOfTurn.RemoveAll(e => State.GetCard(e.Card).Version != e.Version);
        foreach (var effect in State.UntilEndOfTurn)
        {
            var card = State.GetCard(effect.Card);
            if (card.Zone != Zone.Battlefield) continue;
            card.PowerBonus += effect.Power;
            card.ToughnessBonus += effect.Toughness;
            card.GrantedKeywords.UnionWith(effect.Keywords);
        }
    }

    private IEnumerable<Card> Affected(Card source, AffectedFilter filter, IReadOnlyList<Card> battlefield)
    {
        IEnumerable<Card> candidates = filter.Scope switch
        {
            AffectedScope.Self => new[] { source },
            AffectedScope.YourCreatures => battlefield.Where(c => c.IsCreature && c.Controller == source.Controller),
            AffectedScope.OpponentsCreatures => battlefield.Where(c => c.IsCreature && c.Controller != source.Controller),
            AffectedScope.AllCreatures => battlefield.Where(c => c.IsCreature),
            AffectedScope.Enchanted or AffectedScope.Equipped when source.AttachedTo is { } host
                => battlefield.Where(c => c.Id == host),
            _ => Array.Empty<Card>(),
        };
        if (filter.Other) candidates = candidates.Where(c => c.Id != source.Id);
        if (filter.Subtype is { } subtype) candidates = candidates.Where(c => c.Definition.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase));
        return candidates;
    }

    // ------------------------------------------------------------------ triggers (rule 603)

    /// <summary>Notes triggered abilities that trigger on <paramref name="e"/>; they go on the stack before the next priority.</summary>
    private void CollectTriggers(GameEvent e)
    {
        switch (e)
        {
            case CardMoved { To: Zone.Battlefield } m:
            {
                var entering = State.GetCard(m.Card);
                Queue(m.Card, TriggerEvent.EntersBattlefield, entering.Controller);
                if (entering.IsCreature) QueueObservers(TriggerEvent.CreatureEnters, entering, entering.Controller);
                if (entering.Is(CardType.Land)) QueueObservers(TriggerEvent.LandEnters, entering, entering.Controller);
                break;
            }
            case CardMoved { From: Zone.Battlefield, To: Zone.Graveyard } m when State.GetCard(m.Card).IsCreature:
                Queue(m.Card, TriggerEvent.Dies, m.LastController);
                QueueObservers(TriggerEvent.CreatureDies, State.GetCard(m.Card), m.LastController);
                break;
            case AttackerDeclared a:
                Queue(a.Attacker, TriggerEvent.Attacks, State.GetCard(a.Attacker).Controller);
                Queue(a.Attacker, TriggerEvent.AttacksOrBlocks, State.GetCard(a.Attacker).Controller);
                break;
            case AttacksDeclared a:
                foreach (var card in State.PermanentsControlledBy(a.Player).ToList()) Queue(card.Id, TriggerEvent.YouAttack, a.Player);
                break;
            case BlockerDeclared b:
                Queue(b.Blocker, TriggerEvent.Blocks, State.GetCard(b.Blocker).Controller);
                Queue(b.Blocker, TriggerEvent.AttacksOrBlocks, State.GetCard(b.Blocker).Controller);
                break;
            case LifeChanged l when l.NewLife > l.OldLife:
                foreach (var card in State.PermanentsControlledBy(l.Player).ToList()) Queue(card.Id, TriggerEvent.YouGainLife, l.Player);
                break;
            case SpellCast c:
            {
                var spell = State.GetCard(c.Card);
                foreach (var observer in State.PermanentsControlledBy(c.Player).ToList())
                {
                    if (!spell.IsCreature && observer.Has(Keyword.Prowess)) _pendingTriggers.Add(new PendingTrigger(observer.Id, ProwessTrigger, c.Player));
                    foreach (var ability in observer.Definition.Abilities.OfType<TriggeredAbility>())
                        if (ability.Trigger == TriggerEvent.YouCastSpell && Matches(ability.Filter ?? new ObjectFilter(), spell, c.Player, observer, c.Player))
                            AddPending(observer.Id, ability, c.Player);
                }
                break;
            }
            case StepBegan { Step: Step.BeginCombat } s:
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourBeginCombat, s.ActivePlayer);
                break;
            case DamageDealt { IsCombat: true, TargetPlayer: not null } d:
                Queue(d.Source, TriggerEvent.DealsCombatDamageToPlayer, State.GetCard(d.Source).Controller);
                break;
            case StepBegan { Step: Step.Upkeep } s:
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourUpkeep, s.ActivePlayer);
                break;
            case StepBegan { Step: Step.End } s:
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.YourEndStep, s.ActivePlayer);
                break;
        }
    }

    private void Queue(CardId source, TriggerEvent trigger, PlayerId controller)
    {
        foreach (var ability in State.GetCard(source).Definition.Abilities.OfType<TriggeredAbility>())
            if (ability.Trigger == trigger) AddPending(source, ability, controller);
    }

    /// <summary>Triggers of permanents watching for an event that happened to <paramref name="subject"/> ("whenever another creature you control enters").</summary>
    private void QueueObservers(TriggerEvent trigger, Card subject, PlayerId subjectController)
    {
        foreach (var observer in State.Battlefield.Select(State.GetCard).ToList())
            foreach (var ability in observer.Definition.Abilities.OfType<TriggeredAbility>())
                if (ability.Trigger == trigger && Matches(ability.Filter ?? ObjectFilter.YourCreatures, subject, subjectController, observer, observer.Controller))
                    AddPending(observer.Id, ability, observer.Controller);
    }

    /// <summary>An ability with an intervening "if" clause triggers only if the condition holds now (rule 603.4).</summary>
    private void AddPending(CardId source, TriggeredAbility ability, PlayerId controller)
    {
        if (ability.Condition is { } condition && !Holds(condition, controller, State.GetCard(source))) return;
        _pendingTriggers.Add(new PendingTrigger(source, ability, controller));
    }

    private static readonly TriggeredAbility ProwessTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new PumpUntilEndOfTurn(1, 1, Subject.Self) },
        Text = "Prowess",
    };

    /// <summary>
    /// Puts waiting triggered abilities on the stack in APNAP order (603.3b), choosing their targets. A trigger
    /// with no legal targets is removed (603.3d). Returns true if anything was put on the stack.
    /// </summary>
    private async Task<bool> PutPendingTriggersOnStackAsync()
    {
        if (_pendingTriggers.Count == 0) return false;
        var pending = _pendingTriggers.ToList();
        _pendingTriggers.Clear();
        bool any = false;
        foreach (var player in State.ApnapOrder().ToList())
        {
            foreach (var trigger in pending.Where(t => t.Controller == player))
            {
                if (!HasLegalTargets(trigger.Ability, player, trigger.Source)) continue;
                var targets = await ChooseTargetsAsync(player, trigger.Ability, trigger.Source, trigger.Ability.Text, canCancel: false);
                State.Stack.Add(new AbilityOnStack(trigger.Source, trigger.Ability, player, targets!));
                Emit(new AbilityTriggered(player, trigger.Source, trigger.Ability.Text));
                any = true;
            }
        }
        return any;
    }
}
