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
    /// <summary>Effects handled by the group-4 part of the effect switch; false when the effect belongs to another group (the switch order is kept).</summary>
    private async Task<bool> ApplyGroup4Async(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case PreventDamageBy pd:
                if (!SourceOnBattlefield(ctx)) break;
                foreach (var card in CardsFor(pd.What, ctx))
                    State.DamagePreventions.Add(new DamagePrevention(card.Id, card.Version, ctx.Source.Id, ctx.Source.Version));
                break;
            case AtPlayersNextEndStep an:
            {
                var whose = an.Whose.Kind == SubjectKind.EachOpponent
                    ? await ChooseOpponentAsync(ctx.Controller, ctx.Source, $"{ctx.Source.Name}: choose an opponent")
                    : PlayersFor(an.Whose, ctx).Cast<PlayerId?>().FirstOrDefault();
                if (whose is not { } watched) break;
                Emit(new ChoiceMade(ctx.Source.Id, State.GetPlayer(watched).Name));
                var aboutCard = an.About is { } endStepAbout && CardsFor(endStepAbout, ctx).FirstOrDefault() is { } aboutIt ? (aboutIt.Id, aboutIt.Version) : ((CardId, int)?)null;
                State.AtPlayersNextEndStep.Add((new DelayedTrigger(ctx.Source.Id, an.Ability, ctx.Controller, 0)
                    { SourceTransforms = ctx.Source.TransformCount, SourceVersion = ExpectedSourceVersion(ctx), AboutCard = aboutCard }, watched, State.TurnNumber, State.Step == Step.End));
                break;
            }
            case MoveCounterOfEachMissingKind mc:
            {
                var from = CardsFor(mc.From, ctx).FirstOrDefault();
                var to = mc.To.Kind == SubjectKind.Self ? (SourceOnBattlefield(ctx) ? ctx.Source : null) : CardsFor(mc.To, ctx).FirstOrDefault();
                if (from is null || to is null) break;
                foreach (var kind in from.Counters.Where(kv => kv.Value > 0 && to.CounterCount(kv.Key) == 0).Select(kv => kv.Key).ToList())
                {
                    RemoveCountersFrom(from, kind, 1);
                    PutCounters(to, kind, 1, ctx.Controller);
                }
                break;
            }
            case MoveChosenCounters mcc:
            {
                var from = mcc.From.Kind == SubjectKind.Self ? (SourceOnBattlefield(ctx) ? ctx.Source : null) : CardsFor(mcc.From, ctx).FirstOrDefault();
                var to = CardsFor(mcc.To, ctx).FirstOrDefault();
                if (from is null || to is null) break;
                int moved = 0;
                if (mcc.Single)
                {
                    var kinds = from.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    if (kinds.Count == 0) break;
                    int pick = kinds.Count == 1 ? 0 : await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(
                        $"Move which counter from {from.Name} onto {to.Name}?", ctx.Source.Id, kinds.Select(k => $"A {CounterName(k)} counter").ToList(), OptionKind.Other));
                    Require(pick >= 0 && pick < kinds.Count, "Choose a kind of counter.");
                    RemoveCountersFrom(from, kinds[pick], 1);
                    PutCounters(to, kinds[pick], 1, ctx.Controller);
                    await ApplyAllAsync(mcc.Then, ctx);
                    break;
                }
                foreach (var kind in from.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
                {
                    int n = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                        new NumberRequest($"Move how many {CounterName(kind)} counters from {from.Name} onto {to.Name}?", ctx.Source.Id, 0, from.CounterCount(kind)));
                    Require(n >= 0 && n <= from.CounterCount(kind), "Choose how many counters to move.");
                    if (n == 0) continue;
                    RemoveCountersFrom(from, kind, n);
                    PutCounters(to, kind, n, ctx.Controller);
                    moved += n;
                }
                if (moved > 0) await ApplyAllAsync(mcc.Then, ctx); // "one or more … If you do"
                break;
            }
            case Simultaneously sim:
            {
                BeginSimultaneous();
                // Damage dealt by its effects is one damage event (rule 120): replacement effects see it whole.
                bool collects = _damageBatch is null;
                if (collects) _damageBatch = new();
                try { await ApplyAllAsync(sim.Effects, ctx); }
                finally
                {
                    if (collects)
                    {
                        var batch = _damageBatch!;
                        _damageBatch = null;
                        if (batch.Count > 0) await DealDamageEventAsync(batch);
                    }
                }
                EndSimultaneous();
                break;
            }
            case RevealTop rt:
            {
                var player = State.GetPlayer(ctx.Controller);
                if (player.Library.Count == 0) break;
                if (rt.Optional && !await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest("Reveal the top card of your library?", ctx.Source.Id))) break;
                var top = State.GetCard(player.Library[0]);
                Emit(new CardsRevealed(ctx.Controller, new[] { top.Id }));
                bool hit = Matches(rt.Filter with { Controller = ControllerFilter.Any }, top, ctx.Controller, ctx.Source, ctx.Controller);
                if (!hit && rt.Else is null) break;
                ctx.Results.Found.Add(top.Id);
                ctx.Results.FoundStats[top.Id] = (top.Power, top.Toughness);
                await ApplyAllAsync(hit ? rt.Effects : rt.Else!, ctx);
                break;
            }
            case DamageCantBePreventedThisTurn:
                State.DamageCantBePreventedTurn = State.TurnNumber;
                break;
            case Goad gd:
                foreach (var card in CardsFor(gd.What, ctx).ToList())
                {
                    (gd.ForRestOfGame ? State.GoadsForever : State.Goads).Add((card.Id, card.Version, ctx.Controller));
                    Emit(new ChoiceMade(card.Id, "goaded"));
                }
                break;
            case EachToADifferentOpponent eo:
            {
                var left = State.OpponentsOf(ctx.Controller).ToList();
                long stamp = NewTimestamp();
                foreach (var card in CardsFor(eo.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    if (left.Count == 0) break;
                    var chosen = await ChooseOpponentAsync(ctx.Controller, ctx.Source, $"Choose the opponent who gains control of {card.Name}", left) ?? left[0];
                    left.Remove(chosen);
                    State.ControlEffects.Add(new ControlEffect(card.Id, card.Version, chosen, stamp) { MadeOnTurn = State.TurnNumber });
                    Emit(new ChoiceMade(card.Id, State.GetPlayer(chosen).Name));
                }
                RecomputeContinuousEffects();
            }
                break;
            case ProtectionFromChosenType pt:
            {
                var types = ChoosableCardTypes;
                foreach (var card in CardsFor(pt.What, ctx).ToList())
                {
                    int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"{card.Name} gains protection from which card type?", ctx.Source.Id, types.Select(t => t.ToString().ToLowerInvariant()).ToList(), OptionKind.Other));
                    Require(pick >= 0 && pick < types.Length, "Choose a card type.");
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>()) { ProtectionFromTypes = types[pick], Timestamp = NewTimestamp() });
                }
                break;
            }
            case ProtectionFromColorsOf pc:
            {
                var of = pc.Of.Kind == SubjectKind.Target && ctx.ChosenAt(pc.Of.Index)?.Card is { } chosenId ? State.GetCard(chosenId) : CardsFor(pc.Of, ctx).FirstOrDefault();
                if (of is null) break;
                var colors = (of.Zone == Zone.Battlefield || of.LastKnownInfo is null ? of.Colors : of.LastKnownInfo.Colors);
                var keywords = colors.Select(Keywords.ProtectionFrom).OfType<Keyword>().ToList();
                if (keywords.Count == 0) break;
                foreach (var card in CardsFor(pc.What, ctx).ToList())
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, keywords) { Timestamp = NewTimestamp() });
                break;
            }
            case ExileHandDownTo eh:
                foreach (var who in PlayersFor(eh.Who, ctx).ToList())
                {
                    var hand = State.GetPlayer(who).Hand;
                    int excess = hand.Count - eh.Keep;
                    if (excess <= 0) continue;
                    var options = hand.Select(id => ViewBuilder.Card(State, id, who)).ToList();
                    var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Exile {excess} card{(excess == 1 ? "" : "s")} from your hand", ctx.Source.Id, options, excess, excess, CardChoicePurpose.Discard));
                    Require(chosen.Count == excess && chosen.Distinct().Count() == excess && chosen.All(hand.Contains), $"Exile exactly {excess} cards from your hand.");
                    foreach (var id in chosen) await MoveCardAsync(id, Zone.Exile);
                }
                break;
            case AddChosenCounter acc:
                foreach (var card in CardsFor(acc.What, ctx).ToList())
                {
                    var names = acc.Kinds.Select(CounterName).ToList();
                    int pick = acc.Kinds.Count == 1 ? 0 : await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"{ctx.Source.Name}: choose a counter to put on {card.Name}", ctx.Source.Id, names, OptionKind.Other));
                    Require(pick >= 0 && pick < acc.Kinds.Count, "Choose one of the listed counters.");
                    PutCounters(card, acc.Kinds[pick], 1, ctx.Controller);
                }
                break;
            case AddCountersOfTriggeredKinds act:
                BeginSimultaneous();
                foreach (var card in CardsFor(act.What, ctx).ToList())
                    foreach (var kind in ctx.Trigger?.CounterKinds ?? new List<CounterKind>())
                        PutCounters(card, kind, 1, ctx.Controller);
                EndSimultaneous();
                break;
            case ChooseOneEffect co:
            {
                int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                    new OptionRequest($"{ctx.Source.Name}: choose one", ctx.Source.Id, co.Choices.Select(c => c.Text).ToList(), OptionKind.Other));
                Require(pick >= 0 && pick < co.Choices.Count, "Choose one of the listed options.");
                await ApplyAllAsync(co.Choices[pick].Effects, ctx);
                break;
            }
            case Amass am:
                foreach (var player in PlayersFor(am.Who, ctx).ToList())
                    await AmassAsync(player, am, Eval(am.Count, ctx), ctx);
                break;
            case CreateTokens t:
            {
                int count = Eval(t.Count, ctx);
                if (t.PowerAndToughness is { } size)
                {
                    int n = Eval(size, ctx);
                    t = t with { Token = t.Token with { Power = n, Toughness = n } };
                }
                if (t.ForEachOpponentMustAttack)
                {
                    foreach (var opponent in State.ApnapOrder().Where(o => State.OpponentsOf(ctx.Controller).Contains(o)).ToList())
                        foreach (var token in (await CreateTokenEventAsync(ctx.Controller, t.Token, count, t.Tapped, ctx.Source)).Originals)
                        {
                            ctx.Results.Created.Add(token);
                            var made = State.GetCard(token);
                            State.AttackPlayerRequirements.Add((token, made.Version, opponent, State.TurnNumber));
                            if (t.HasteUntilEndOfTurn)
                                State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(token, made.Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                        }
                    break;
                }
                if (t.AttackingEachOpponent)
                {
                    foreach (var opponent in State.ApnapOrder().Where(o => State.OpponentsOf(ctx.Controller).Contains(o)).ToList())
                        foreach (var token in (await CreateTokenEventAsync(ctx.Controller, t.Token, count, true, ctx.Source)).Originals)
                        {
                            ctx.Results.Created.Add(token);
                            if (State.Combat is not { } eachCombat) continue;
                            var walkers = State.PermanentsControlledBy(opponent).Where(c => c.Is(CardType.Planeswalker)).ToList();
                            CardId? walker = null;
                            if (walkers.Count > 0)
                            {
                                var labels = walkers.Select(w => w.Name).Prepend(State.GetPlayer(opponent).Name).ToList();
                                int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(
                                    "The token attacks that player or a planeswalker they control", token, labels, OptionKind.Other));
                                Require(pick >= 0 && pick < labels.Count, "Choose what the token attacks.");
                                walker = pick == 0 ? null : walkers[pick - 1].Id;
                            }
                            eachCombat.Attacks.Add(new AttackInfo { Attacker = token, Defender = opponent, Planeswalker = walker });
                        }
                    break;
                }
                foreach (var player in PlayersFor(t.Controller, ctx).ToList())
                {
                    foreach (var token in (await CreateTokenEventAsync(player, t.Token, count, t.Tapped, ctx.Source)).Originals)
                    {
                        ctx.Results.Created.Add(token);
                        if (t.AttachTo is { } tokenHost && State.GetCard(token) is { Definition.EnchantTarget: { } tokenEnchant } auraToken
                            && CardsFor(tokenHost, ctx).FirstOrDefault(h => h.Zone == Zone.Battlefield && CanEnchant(auraToken, tokenEnchant, h)) is { } host)
                        {
                            auraToken.AttachedTo = host.Id;
                            auraToken.Timestamp = 0;
                        }
                        if (t.AttackingTriggerDefender && State.Combat is { } defenderCombat && ctx.Trigger?.Defender is { } attackedPlayer)
                            defenderCombat.Attacks.Add(new AttackInfo { Attacker = token, Defender = attackedPlayer });
                        // "Tapped and attacking": attacking what the creature the trigger was about attacks.
                        else if (t.Attacking && !t.AttackingChosenDefender && State.Combat is { } combat && ctx.Trigger?.Subject is { } attackerId && combat.FindAttack(attackerId) is { } attack)
                            combat.Attacks.Add(new AttackInfo { Attacker = token, Defender = attack.Defender, Planeswalker = attack.Planeswalker });
                        else if (t.Attacking && State.Combat is { } openCombat)
                            openCombat.Attacks.Add(await AttackFor(State.GetCard(token), ctx));
                        if (t.SacrificeAtEndOfCombat) State.SacrificeAtEndOfCombat.Add((token, State.GetCard(token).Version));
                        if (t.HasteUntilEndOfTurn)
                            State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(token, State.GetCard(token).Version, 0, 0, new[] { Keyword.Haste }) { Timestamp = NewTimestamp() });
                    }
                }
            }
                break;
            case BecomeMonarch bm:
            {
                // "Its controller": the creature's controller, as it last existed if it left the battlefield.
                var who = bm.Who.Kind == SubjectKind.Triggered
                    ? (ctx.Trigger?.Subject is { } monarchMaker ? new[] { ControllerOrLastKnown(State.GetCard(monarchMaker), ctx.Trigger.SubjectVersion) } : Array.Empty<PlayerId>())
                    : PlayersFor(bm.Who, ctx).ToArray();
                foreach (var player in who.Take(1)) BecomeMonarch(player);
                break;
            }
            case ExileUntilOpponentIsMonarch eum:
                foreach (var card in CardsFor(eum.What, ctx).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    if (card.Zone == Zone.Exile && !card.Definition.IsToken) State.ExiledUntilOpponentIsMonarch.Add((card.Id, card.Version, ctx.Controller));
                }
                break;
            case PlayerMayPay pmp:
                foreach (var player in PlayersFor(pmp.Who, ctx).ToList())
                {
                    int amount = Math.Max(0, Eval(pmp.Generic, ctx with { AffectedPlayer = player }));
                    var cost = new ManaCost(amount, Array.Empty<ManaType>());
                    bool paid = amount == 0
                        ? await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"{ctx.Source.Name}: pay {{0}}?", ctx.Source.Id))
                        : Payable(player, cost, null)
                          && await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"{ctx.Source.Name}: pay {cost}?", ctx.Source.Id))
                          && await PayManaAsync(State.GetPlayer(player), ctx.Source.Id, cost, null);
                    if (!paid) await ApplyAllAsync(pmp.IfNot, ctx with { Trigger = new TriggerInfo(Player: player) });
                }
                break;
            case PreventDamageThisTurn { To: { } shielded } pdt:
                foreach (var card in CardsFor(shielded, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.PreventionShields.Add(new PreventionShield(State.TurnNumber, pdt.CombatOnly)
                        { ToObject = (card.Id, card.Version), CountersInstead = pdt.CountersInstead, Controller = ctx.Controller, SourceCard = ctx.Source.Id, Id = NewTimestamp() });
                break;
            case AttacksSourceNextTurn asn:
                AddAttackSourceRequirements(asn, ctx);
                break;
            case PreventDamageThisTurn pdt:
                if (pdt.DealtBy is { } by)
                    foreach (var card in CardsFor(by, ctx).ToList())
                        State.PreventionShields.Add(new PreventionShield(State.TurnNumber, pdt.CombatOnly) { DealtBy = (card.Id, card.Version) });
                else State.PreventionShields.Add(new PreventionShield(State.TurnNumber, pdt.CombatOnly)
                {
                    UntilTurnOf = pdt.UntilYourNextTurn ? ctx.Controller : null,
                    SourceFilter = pdt.Sources, FilterController = ctx.Controller, ToPlayer = pdt.ToYou ? ctx.Controller : null, ToCreaturesOf = pdt.ToYourCreatures ? ctx.Controller : null,
                    SourceOpponentsOf = pdt.SourcesOfOpponents ? ctx.Controller : null, Controller = ctx.Controller, SourceCard = ctx.Source.Id, Id = NewTimestamp(),
                    WhenCreatureDamagePrevented = pdt.WhenCreatureDamagePrevented is { } prevented
                        ? new TriggeredAbility { Trigger = TriggerEvent.Reflexive, Effects = prevented, Text = pdt.WhenPreventedText ?? "Whenever damage from a creature is prevented this way" }
                        : null,
                    // "For each 1 damage prevented this way, …": the reflexive ability's amount is the damage prevented.
                    WhenPrevented = pdt.WhenPrevented is { } anyPrevented
                        ? new TriggeredAbility { Trigger = TriggerEvent.Reflexive, Effects = anyPrevented, Text = pdt.WhenPreventedText ?? "For each 1 damage prevented this way" }
                        : null,
                });
                break;
            case TripleDamageThisTurn:
                State.DamageTripled.Add((ctx.Controller, State.TurnNumber));
                break;
            case CantLoseThisTurn:
                State.GetPlayer(ctx.Controller).CantLoseLifeTurn = State.TurnNumber;
                State.GetPlayer(ctx.Controller).CantLoseGameTurn = State.TurnNumber;
                break;
            case OwnersGainControl ogc:
            {
                long stamp = NewTimestamp();
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => Matches(ogc.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList())
                    State.ControlEffects.Add(new ControlEffect(card.Id, card.Version, card.Owner, stamp) { MadeOnTurn = State.TurnNumber });
                RecomputeContinuousEffects();
                break;
            }
            case Repeat rp:
                for (int n = Eval(rp.Times, ctx); n > 0 && !State.IsGameOver; n--) await ApplyAllAsync(rp.Effects, ctx);
                break;
            case ProtectionFromOpponents po:
                foreach (var card in CardsFor(po.What, ctx).ToList())
                    (po.Permanent ? State.LastingEffects : State.UntilEndOfTurn).Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>())
                    {
                        ProtectionFromPlayers = State.OpponentsOf(ctx.Controller).ToList(), Timestamp = NewTimestamp(),
                    });
                break;
            case ExileTopFaceDown etf:
                foreach (var whose in State.ApnapOrder().Where(PlayersFor(etf.Whose, ctx).ToList().Contains).ToList())
                {
                    if (State.GetPlayer(whose).Library.Count == 0) continue;
                    var topCard = State.GetPlayer(whose).Library[0];
                    Look(ctx.Controller, new[] { topCard });
                    if (etf.Optional && !await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                            new YesNoRequest($"Exile {State.GetCard(topCard).Name} face down?", topCard))) continue;
                    await MoveCardAsync(topCard, Zone.Exile, faceDown: true);
                    var hidden = State.GetCard(topCard);
                    if (hidden.Zone != Zone.Exile) continue;
                    Look(ctx.Controller, new[] { topCard });
                    hidden.ExiledWith = (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version);
                }
                break;
            case PlayOneExiledWithThisFree:
            {
                int group = ++_playGroups;
                foreach (var hidden in State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id
                                                                     && w.Version == (ctx.SourceVersion ?? ctx.Source.Version)).ToList())
                    State.PlayableFromExile.Add(new PlayableFromExile(hidden.Id, hidden.Version, ctx.Controller, State.TurnNumber, WithoutPaying: true) { Group = group });
                break;
            }
            case TakeCountersOfTriggered take:
            {
                // "Put those counters on [this / target permanent]": the counters it had as it left.
                var onto = take.Onto is { } ontoSubject ? CardsFor(ontoSubject, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield) : SourceOnBattlefield(ctx) ? ctx.Source : null;
                // The permanent the trigger is about, or this card itself ("when this dies, put its counters on …").
                var leftId = ctx.Trigger?.Subject ?? ctx.Source.Id;
                if (onto is null || State.GetCard(leftId).LastKnownInfo is not { } left) break;
                BeginSimultaneous();
                foreach (var (kind, n) in left.Counters.Where(kv => kv.Value > 0)) PutCounters(onto, kind, n, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case MoveAllCounters mac:
            {
                var from = mac.From.Kind == SubjectKind.Self ? (SourceOnBattlefield(ctx) ? ctx.Source : null) : CardsFor(mac.From, ctx).FirstOrDefault();
                var to = CardsFor(mac.To, ctx).FirstOrDefault();
                if (from is null || to is null || from.Id == to.Id) break;
                var moving = from.Counters.Where(kv => kv.Value > 0).ToList();
                from.Counters.Clear();
                BeginSimultaneous();
                foreach (var (kind, n) in moving) PutCounters(to, kind, n, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case MayBounceAnotherSharingType:
            {
                if (ctx.Trigger?.Subject is not { } enteredId || State.GetCard(enteredId) is not { Zone: Zone.Battlefield } entered) break;
                var permanentTypes = new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Land, CardType.Planeswalker };
                var options = State.PermanentsControlledBy(ctx.Controller).Where(c => c.Id != entered.Id && permanentTypes.Any(t => c.Is(t) && entered.Is(t))).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest($"You may return another permanent that shares a type with {entered.Name}", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, 1, CardChoicePurpose.ToHand));
                foreach (var id in pick.Where(id => options.Any(c => c.Id == id))) await MoveCardAsync(id, Zone.Hand);
                break;
            }
            case CopyTriggeredAbility:
            {
                if (ctx.Trigger?.Amount is not { } stackId || State.Stack.FirstOrDefault(s => s.Id == stackId) is not AbilityOnStack original) break;
                IReadOnlyList<ChosenTarget> targets = original.Targets;
                if (original.Ability.Targets.Count > 0
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Choose new targets for the copy of {original.Ability.Text}?", ctx.Source.Id)))
                    targets = await ChooseTargetsAsync(ctx.Controller, original.Ability, original.Source, original.Ability.Text, canCancel: true, exactCount: original.Targets.Count) ?? original.Targets;
                PushStack(original with { Controller = ctx.Controller, Targets = targets, Id = 0 });
                Emit(new AbilityTriggered(ctx.Controller, original.Source, $"Copy: {original.Ability.Text}"));
                break;
            }
            case SwapGraveyardAndBattlefield swap:
            {
                var filter = swap.Filter with { Controller = ControllerFilter.Any };
                var exiled = new List<(PlayerId Player, CardId Card)>();
                foreach (var p in State.ApnapOrder().ToList())
                    foreach (var id in State.GetPlayer(p).Graveyard.Where(id => Matches(filter, State.GetCard(id), p, ctx.Source, ctx.Controller)).ToList())
                    {
                        await MoveCardAsync(id, Zone.Exile);
                        if (State.GetCard(id).Zone == Zone.Exile) exiled.Add((p, id));
                    }
                BeginSimultaneous();
                foreach (var permanent in State.Battlefield.Select(State.GetCard).Where(c => Matches(filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList())
                    await SacrificePermanentAsync(permanent.Id);
                EndSimultaneous();
                BeginEnteringTogether();
                foreach (var (p, id) in exiled)
                    if (State.GetCard(id).Zone == Zone.Exile) await MoveCardAsync(id, Zone.Battlefield, controller: p);
                EndEnteringTogether();
                break;
            }
            case Parley:
            {
                var revealed = new List<CardId>();
                foreach (var revealer in State.ApnapOrder().Where(p => State.GetPlayer(p).Library.Count > 0).ToList())
                {
                    var top = State.GetPlayer(revealer).Library[0];
                    revealed.Add(top);
                    RevealToAll(new[] { top });
                    Emit(new CardsRevealed(revealer, new[] { top }));
                }
                ctx.Results.NonlandRevealed = revealed.Count(id => !State.GetCard(id).Is(CardType.Land));
                break;
            }
            case TemptingOffer to:
            {
                var you = ctx.Controller;
                await ApplyAllAsync(to.Effects, ctx);
                var accepting = new List<PlayerId>();
                foreach (var opponent in State.ApnapOrder().Where(p => State.OpponentsOf(you).Contains(p)).ToList())
                    if (await ControllerOf(opponent).ChooseYesNoAsync(ViewFor(opponent), new YesNoRequest($"{ctx.Source.Name}: {to.Prompt} ({State.GetPlayer(you).Name} does it again if you do)", ctx.Source.Id)))
                        accepting.Add(opponent);
                foreach (var opponent in accepting) await ApplyAllAsync(to.Effects, ctx with { Controller = opponent, AffectedPlayer = opponent });
                foreach (var _ in accepting) await ApplyAllAsync(to.Effects, ctx);
                break;
            }
            case TemptingOfferSearch tos:
            {
                var you = ctx.Controller;
                var search = new SearchLibrary(tos.Filter, 1, Zone.Battlefield);
                ctx.Results.Found.AddRange(await SearchLibraryAsync(you, search, ctx.Source));
                int accepted = 0;
                foreach (var opponent in State.ApnapOrder().Where(p => p != you).ToList())
                {
                    if (!await ControllerOf(opponent).ChooseYesNoAsync(ViewFor(opponent), new YesNoRequest($"{ctx.Source.Name}: search your library for a land card and put it onto the battlefield? ({State.GetPlayer(you).Name} searches again if you do)", ctx.Source.Id))) continue;
                    accepted++;
                    await SearchLibraryAsync(opponent, search, ctx.Source);
                }
                for (int n = 0; n < accepted; n++) ctx.Results.Found.AddRange(await SearchLibraryAsync(you, search, ctx.Source));
                break;
            }
            case CopyEachYouControl ce:
            {
                var originals = State.PermanentsControlledBy(ctx.Controller).Where(c => Matches(ce.Filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                BeginEnteringTogether();
                foreach (var original in originals)
                {
                    var (copied, backFaceUp) = CopiableForToken(original);
                    ctx.Results.Created.AddRange((await CreateTokenEventAsync(ctx.Controller, copied with { IsToken = true }, 1, false, ctx.Source, backFaceUp)).Originals);
                }
                EndEnteringTogether();
                break;
            }
            case OpponentsExileGreatestPower og:
            {
                var exiledPower = new List<(PlayerId Player, int Power)>();
                foreach (var opponent in State.ApnapOrder().Where(p => p != ctx.Controller).ToList())
                {
                    var creatures = State.PermanentsControlledBy(opponent).Where(c => c.IsCreature).ToList();
                    if (creatures.Count == 0) continue;
                    int most = creatures.Max(c => c.Power);
                    var biggest = creatures.Where(c => c.Power == most).ToList();
                    var pick = biggest.Count == 1 ? new[] { biggest[0].Id } : await ControllerOf(opponent).ChooseCardsAsync(ViewFor(opponent), new CardChoiceRequest(
                        $"{ctx.Source.Name}: exile a creature with the greatest power among creatures you control", ctx.Source.Id,
                        biggest.Select(c => ViewBuilder.Card(State, c.Id, opponent)).ToList(), 1, 1, CardChoicePurpose.Sacrifice));
                    Require(pick.Count == 1 && biggest.Any(c => c.Id == pick[0]), "Choose one of the creatures with the greatest power.");
                    var exiled = State.GetCard(pick[0]);
                    int power = exiled.Power;
                    await MoveCardAsync(exiled.Id, Zone.Exile);
                    exiledPower.Add((opponent, exiled.LastKnownInfo?.Power ?? power));
                }
                if (og.DamageIf is { } spellMastery && HoldsIn(spellMastery, ctx))
                    await DealDamageEventAsync(exiledPower.Select(e => new DamagePart(ctx.Source, null, e.Item1, e.Item2, false)).ToList());
                break;
            }
            case CopyIfOpponentsTopSharesType:
            {
                if (ctx.Trigger?.Subject is not { } spellId) break;
                var spellCard = State.GetCard(spellId);
                var revealed = new List<Card>();
                foreach (var opponent in State.ApnapOrder().Where(p => p != ctx.Controller).ToList())
                    if (State.GetPlayer(opponent).Library.Count > 0)
                    {
                        var top = State.GetCard(State.GetPlayer(opponent).Library[0]);
                        Emit(new CardsRevealed(opponent, new[] { top.Id }));
                        revealed.Add(top);
                    }
                var spellTypes = spellCard.Zone == Zone.Stack ? spellCard.Types : spellCard.Definition.Types;
                if (revealed.Any(c => (c.Definition.Types & spellTypes) != 0))
                {
                    if (State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == spellId) is { } original && !spellCard.Definition.CantBeCopied)
                        await CopySpellAsync(original, ctx.Controller);
                    foreach (var opponent in State.ApnapOrder().Where(p => p != ctx.Controller).ToList()) await DrawAsync(opponent);
                }
                else await DrawAsync(ctx.Controller);
                break;
            }
            case SuspendTick:
                if (ctx.Source.Zone != Zone.Exile || !ctx.Source.Suspended || ctx.Source.CounterCount(CounterKind.Time) <= 0) break;
                ctx.Source.Counters[CounterKind.Time]--;
                if (ctx.Source.CounterCount(CounterKind.Time) == 0) _pendingTriggers.Add(new PendingTrigger(ctx.Source.Id, SuspendPlay, ctx.Source.Owner));
                break;
            case PlaySuspendedFree:
            {
                var suspended = ctx.Source;
                if (suspended.Zone != Zone.Exile) break;
                var owner = State.GetPlayer(suspended.Owner);
                if (suspended.Is(CardType.Land))
                {
                    if (owner.LandsPlayedThisTurn >= LandsAllowed(owner.Id) || State.ActivePlayer != owner.Id) break;
                    if (!await ControllerOf(owner.Id).ChooseYesNoAsync(ViewFor(owner.Id), new YesNoRequest($"Play {suspended.Name}?", suspended.Id))) break;
                    owner.LandsPlayedThisTurn++;
                    await MoveCardAsync(suspended.Id, Zone.Battlefield, controller: owner.Id);
                    break;
                }
                if (!HasLegalTargets(CastingTargets(suspended.Definition), owner.Id, suspended.Id)
                    || !await ControllerOf(owner.Id).ChooseYesNoAsync(ViewFor(owner.Id), new YesNoRequest($"Cast {suspended.Name} without paying its mana cost?", suspended.Id))) break;
                // A creature cast this way gains haste, but only if it's actually cast.
                await CastNowWithoutPayingAsync(owner, suspended.Id, hasteOnEnter: suspended.IsCreature);
                break;
            }
            case ExileWithCounterWhenResolves ew:
                if (ctx.Trigger?.Subject is { } resolvingSpell && State.GetCard(resolvingSpell) is { Zone: Zone.Stack } marked)
                    State.ExileWithCounterOnResolution[(resolvingSpell, marked.Version)] = ew.Kind;
                break;
            case SuspendWhenResolves sw:
                if (ctx.Trigger?.Subject is { } resolving && State.GetCard(resolving) is { Zone: Zone.Stack } suspending)
                    State.SuspendOnResolution[(resolving, suspending.Version)] = sw.TimeCounters;
                // The resolving spell itself ("exile this spell with three time counters on it").
                else if (ctx.Trigger is null && ctx.Source.Zone == Zone.Stack)
                    State.SuspendOnResolution[(ctx.Source.Id, ctx.Source.Version)] = sw.TimeCounters;
                break;
            case DestroyPowerAbove dpa:
            {
                var reference = dpa.What.Kind == SubjectKind.Target && ctx.ChosenAt(dpa.What.Index)?.Card is { } refId ? State.GetCard(refId) : CardsFor(dpa.What, ctx).FirstOrDefault();
                if (reference is null || ctx.TargetAt(dpa.What.Index) is null) break;
                int threshold = reference.Power;
                await ApplyAsync(new Destroy(Subject.Each(new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any, MinPower: threshold + 1))), ctx);
                break;
            }
            case ExileChosenFromGraveyard ecg:
            {
                var who = ctx.Controller;
                var options = State.GetPlayer(who).Graveyard.Select(State.GetCard).Where(c => Matches(ecg.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Exile a card from your graveyard", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 1, 1, CardChoicePurpose.Discard));
                Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the listed cards.");
                await MoveCardAsync(pick[0], Zone.Exile);
                if (State.GetCard(pick[0]).Zone == Zone.Exile) ctx.Results.Exiled.Add(pick[0]);
                break;
            }
            case ReturnAllFromGraveyardToHand ra2:
                foreach (var id in State.GetPlayer(ctx.Controller).Graveyard.Where(id => Matches(ra2.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList())
                    await MoveCardAsync(id, Zone.Hand);
                break;
            case ChooseAnOpponent:
                ctx.Results.ChosenPlayer = await ChooseOpponentAsync(ctx.Controller, ctx.Source, $"{ctx.Source.Name}: choose an opponent");
                if (ctx.Results.ChosenPlayer is { } opp) Emit(new ChoiceMade(ctx.Source.Id, State.GetPlayer(opp).Name));
                break;
            case ChooseObjects { PerOpponent: true } perOpponent:
                ctx.Results.Chosen.Clear();
                foreach (var opponent in State.ApnapOrder().Where(State.OpponentsOf(ctx.Controller).Contains).ToList())
                {
                    var theirs = State.PermanentsControlledBy(opponent).Where(c => Matches(perOpponent.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (theirs.Count == 0) continue;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose one that {State.GetPlayer(opponent).Name} controls", ctx.Source.Id,
                        theirs.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), perOpponent.Optional ? 0 : 1, 1, CardChoicePurpose.Keep));
                    Require(pick.Count <= 1 && (perOpponent.Optional || pick.Count == 1) && pick.All(id => theirs.Any(c => c.Id == id)), "Choose one of the listed permanents.");
                    ctx.Results.Chosen.AddRange(pick);
                    ctx.Results.ChosenElsewhere.AddRange(pick.Select(State.GetCard).Where(c => c.Zone != Zone.Battlefield).Select(c => (c.Id, c.Version)));
                    if (pick.Count == 1) Emit(new ChoiceMade(pick[0], "chosen"));
                }
                break;
            case ChooseObjects co:
                ctx.Results.Chosen.Clear();
                foreach (var chooser in State.ApnapOrder().Where(PlayersFor(co.Chooser, ctx).ToList().Contains).ToList())
                {
                    // Among cards elsewhere ("one of them" in your graveyard) when the filter names that zone.
                    var options = (co.Among is { } among ? CardsFor(among, ctx).Distinct().Where(c => co.Filter.InZone is not null || c.Zone == Zone.Battlefield) : State.Battlefield.Select(State.GetCard))
                        .Where(c => Matches(co.Filter, c, c.Controller, ctx.Source, chooser)).ToList();
                    if (options.Count == 0) continue;
                    int most = Math.Min(co.Count, options.Count);
                    var pick = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest(co.Count == 1 ? $"{ctx.Source.Name}: choose one" : $"{ctx.Source.Name}: choose up to {co.Count}",
                        ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, chooser)).ToList(), co.Optional ? 0 : most, most, CardChoicePurpose.Keep));
                    Require(pick.Count <= most && (co.Optional || pick.Count == most) && pick.Distinct().Count() == pick.Count && pick.All(id => options.Any(c => c.Id == id)),
                        "Choose among the listed permanents.");
                    ctx.Results.Chosen.AddRange(pick);
                    ctx.Results.ChosenElsewhere.AddRange(pick.Select(State.GetCard).Where(c => c.Zone != Zone.Battlefield).Select(c => (c.Id, c.Version)));
                    foreach (var chosenId in pick) Emit(new ChoiceMade(chosenId, $"chosen by {State.GetPlayer(chooser).Name}"));
                }
                break;
            case BounceSameManaValue bs:
            {
                var first = CardsFor(bs.What, ctx).FirstOrDefault();
                if (first is null) break;
                int value = ManaValueOf(first);
                var all = State.Battlefield.Select(State.GetCard).Where(c => !c.Is(CardType.Land) && (c.Id == first.Id || ManaValueOf(c) == value)).ToList();
                foreach (var card in all) await MoveCardAsync(card.Id, Zone.Hand);
                break;
            }
            case AttacksYouThisTurn ay:
                foreach (var card in CardsFor(ay.What, ctx).Where(c => c.IsCreature).ToList())
                    State.AttackPlayerRequirements.Add((card.Id, card.Version, ctx.Controller, State.TurnNumber));
                break;
            case SkipNextUntap sn:
            {
                PlayerId? whose = sn.Player is { } who ? PlayersFor(who, ctx).Cast<PlayerId?>().FirstOrDefault() : null;
                if (sn.Player is not null && whose is null) break;
                foreach (var card in CardsFor(sn.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.SkipNextUntap.Add((card.Id, card.Version, whose));
                break;
            }
            case TapAllToDamage tad:
            {
                if (CardsFor(tad.Target, ctx).FirstOrDefault() is not { } prey) break;
                var pack = State.PermanentsControlledBy(ctx.Controller)
                    .Where(c => !c.Tapped && Matches(tad.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                foreach (var wolf in pack) Tap(wolf);
                pack = pack.Where(w => w.Tapped && w.Zone == Zone.Battlefield).ToList(); // "each one tapped this way"
                var hits = pack.Select(w => (Source: w, Target: prey, Amount: w.Power)).ToList();
                // "That creature deals damage equal to its power divided as its controller chooses among any number of those" ones.
                int bite = prey.IsCreature ? prey.Power : 0;
                if (bite > 0 && pack.Count > 0)
                {
                    var lethal = pack.ToDictionary(w => w.Id, w => LethalDamage(w, prey));
                    var suggested = SuggestDamage(bite, pack, lethal, trample: false);
                    var chooser = prey.Controller;
                    var split = await ControllerOf(chooser).AssignCombatDamageAsync(ViewFor(chooser),
                        new DamageAssignmentRequest(prey.Id, bite, pack.Select(w => w.Id).ToList(), lethal, false, chooser, suggested)
                        {
                            ByBlocker = true, Prompt = $"{prey.Name} deals {bite} damage divided among",
                        });
                    Require(split.ToBlockers.Keys.All(lethal.ContainsKey) && split.ToBlockers.Values.All(v => v >= 0) && split.ToPlayer == 0 && split.Total == bite,
                        $"Divide exactly {bite} damage among those creatures.");
                    hits.AddRange(split.ToBlockers.Where(kv => kv.Value > 0).Select(kv => (Source: prey, Target: State.GetCard(kv.Key), Amount: kv.Value)));
                }
                // All of it is dealt at once (one damage event).
                await DealDamageEventAsync(hits.Where(h => h.Amount > 0).Select(h => new DamagePart(h.Source, h.Target, null, h.Amount, false)).ToList());
                break;
            }
            case AtNextEndStepAbout an:
                foreach (var card in CardsFor(an.About, ctx).Concat(an.About.Kind == SubjectKind.Target && ctx.ChosenAt(an.About.Index)?.Card is { } tc2 ? new[] { State.GetCard(tc2) } : Array.Empty<Card>()).Distinct().ToList())
                    State.AtNextEndStepEffects.Add((ctx.Source.Id, ctx.Controller, card.Id, card.Version, an.Effects, null, ctx.Source.TransformCount));
                break;
            case DrawUpTo du:
                foreach (var player in PlayersFor(du.Who, ctx).ToList())
                {
                    int n = await ControllerOf(player).ChooseNumberAsync(ViewFor(player), new NumberRequest($"{ctx.Source.Name}: draw up to {du.Max} cards", ctx.Source.Id, 0, du.Max));
                    Require(n >= 0 && n <= du.Max, $"Draw between 0 and {du.Max} cards.");
                    await DrawAsync(player, n);
                }
                break;
            case Hideaway hw:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(hw.Count).ToList();
                if (top.Count == 0) break;
                Look(ctx.Controller, top);
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Hideaway: choose a card to exile face down", ctx.Source.Id,
                    top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(pick.Count == 1 && top.Contains(pick[0]), "Choose one of the cards.");
                await MoveCardAsync(pick[0], Zone.Exile, faceDown: true);
                var hidden = State.GetCard(pick[0]);
                hidden.ExiledWith = (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version);
                // "Put the rest on the bottom of your library in a random order" (rule 702.75a).
                var rest = top.Where(id => id != pick[0]).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case PlayLinkedExiledFree:
            {
                var exiledWithIt = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id && w.Version == (ctx.SourceVersion ?? ctx.Source.Version)).ToList();
                if (exiledWithIt.Count == 0) break;
                var player = State.GetPlayer(ctx.Controller);
                var hidden = exiledWithIt[0];
                if (exiledWithIt.Count > 1)
                {
                    // "You may play one of them without paying its mana cost" (two hideaway abilities, or more).
                    var choice = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose a card to play without paying its mana cost", ctx.Source.Id,
                        exiledWithIt.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    Require(choice.Count <= 1 && choice.All(id => exiledWithIt.Any(c => c.Id == id)), "Choose one of the exiled cards.");
                    if (choice.Count == 0) break;
                    hidden = State.GetCard(choice[0]);
                }
                else if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Play {hidden.Name} without paying its mana cost?", hidden.Id))) break;
                if (hidden.Is(CardType.Land))
                {
                    // Playing a land this way uses a land play (rule 305.2).
                    if (player.LandsPlayedThisTurn >= LandsAllowed(ctx.Controller)) break;
                    player.LandsPlayedThisTurn++;
                    await MoveCardAsync(hidden.Id, Zone.Battlefield, controller: ctx.Controller);
                    Emit(new LandPlayed(ctx.Controller, hidden.Id));
                    break;
                }
                if (!HasLegalTargets(CastingTargets(hidden.Definition), ctx.Controller, hidden.Id)) break;
                await CastNowWithoutPayingAsync(player, hidden.Id);
                break;
            }
            case BecomePrepared bp:
                foreach (var card in CardsFor(bp.What, ctx).Where(c => c.Zone == Zone.Battlefield).Distinct().ToList())
                    if (bp.Prepared) Prepare(card);
                    else Unprepare(card, announce: true);
                break;
            case GrantManaUntilCastFromExile gm:
                if (ctx.Source.Zone != Zone.Exile) break;
                foreach (var land in CardsFor(gm.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.LastingEffects.Add(new UntilEndOfTurnEffect(land.Id, land.Version, 0, 0, Array.Empty<Keyword>())
                    {
                        GrantsMana = gm.Types, GrantsManaAmount = gm.Amount, UntilCastFromExile = (ctx.Source.Id, ctx.Source.Version), Timestamp = NewTimestamp(),
                    });
                // "You may cast this card for as long as it remains exiled."
                State.PlayableFromExile.Add(new PlayableFromExile(ctx.Source.Id, ctx.Source.Version, ctx.Source.Owner, int.MaxValue) { CastOnly = true });
                RecomputeContinuousEffects();
                break;
            case CastCopiesOfExiledFree cc:
            {
                int left = cc.MaxTotalManaValue;
                var unused = ctx.Results.Exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && !c.Is(CardType.Land)).ToList();
                while (true)
                {
                    var options = unused.Where(c => ManaValueOf(c) <= left && HasLegalTargets(CastingTargets(c.Definition), ctx.Controller, c.Id)).ToList();
                    if (options.Count == 0) break;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: cast a copy of one of these without paying its mana cost ({left} mana value left), or none", ctx.Source.Id,
                        options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    if (pick.Count != 1 || options.All(c => c.Id != pick[0])) break;
                    var original = State.GetCard(pick[0]);
                    unused.Remove(original);
                    left -= ManaValueOf(original);
                    var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                    var copy = new Card(id, original.Definition with { IsToken = true }, ctx.Controller) { Zone = Zone.Exile };
                    State.Cards.Add(id, copy);
                    State.GetPlayer(ctx.Controller).Exile.Add(id);
                    await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), id);
                    if (copy.Zone == Zone.Exile)
                    {
                        State.GetPlayer(ctx.Controller).Exile.Remove(id); // a copy not cast ceases to exist (rule 704.5e)
                        copy.CeasedToExist = true;
                    }
                }
                break;
            }
            case PayAnyLife pl:
            {
                var payer = State.GetPlayer(ctx.Controller);
                // Paying life needs a life total that high (rule 119.4) and the ability to lose life.
                int most = payer.CantLoseLifeNow(State.TurnNumber) ? 0 : Math.Max(0, payer.Life);
                if (most == 0) break;
                int paid = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller), new NumberRequest($"{ctx.Source.Name}: pay how much life?", ctx.Source.Id, 0, most));
                Require(paid >= 0 && paid <= most, $"Choose a number from 0 to {most}.");
                if (paid == 0) break;
                ChangeLife(ctx.Controller, -paid);
                await ApplyAllAsync(pl.Effects, ctx with { X = paid });
                break;
            }
            case FromOutsideOrExileToHand fo:
            {
                var you = ctx.Controller;
                bool Fits(Card c) => Matches(fo.Filter with { Controller = ControllerFilter.Any }, c, you, ctx.Source, you);
                var outside = State.GetPlayer(you).Outside.Select(State.GetCard).Where(Fits).ToList();
                var exiled = State.GetPlayer(you).Exile.Select(State.GetCard).Where(c => !c.FaceDown && Fits(c)).ToList();
                var options = outside.Concat(exiled).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(you).ChooseCardsAsync(ViewFor(you), new CardChoiceRequest($"{ctx.Source.Name}: you may put one of these into your hand", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, you, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToHand));
                Require(pick.Count <= 1 && pick.All(id => options.Any(c => c.Id == id)), "Choose one of the listed cards.");
                if (pick.Count == 0) break;
                if (outside.Any(c => c.Id == pick[0])) { RevealToAll(pick); Emit(new CardsRevealed(you, pick.ToList())); } // revealed from outside the game
                await MoveCardAsync(pick[0], Zone.Hand);
                break;
            }
            case RevealTwoFromOutsideOpponentChooses:
            {
                var you = ctx.Controller;
                var outside = State.GetPlayer(you).Outside.Select(State.GetCard).ToList();
                if (outside.Select(c => c.Name).Distinct().Count() < 2) break; // exactly two with different names, or nothing
                if (!await ControllerOf(you).ChooseYesNoAsync(ViewFor(you), new YesNoRequest($"{ctx.Source.Name}: reveal two cards with different names from outside the game?", ctx.Source.Id)))
                    break;
                var first = await ControllerOf(you).ChooseCardsAsync(ViewFor(you), new CardChoiceRequest($"{ctx.Source.Name}: the first card to reveal from outside the game", ctx.Source.Id,
                    outside.Select(c => ViewBuilder.Card(State, c.Id, you, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(first.Count == 1 && outside.Any(c => c.Id == first[0]), "Choose one of the listed cards.");
                var others = outside.Where(c => c.Name != State.GetCard(first[0]).Name).ToList();
                var second = await ControllerOf(you).ChooseCardsAsync(ViewFor(you), new CardChoiceRequest($"{ctx.Source.Name}: the second card to reveal (a different name)", ctx.Source.Id,
                    others.Select(c => ViewBuilder.Card(State, c.Id, you, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(second.Count == 1 && others.Any(c => c.Id == second[0]), "Choose one of the listed cards.");
                var shown = new List<CardId> { first[0], second[0] };
                Emit(new CardsRevealed(you, shown.ToList()));
                if (await ChooseOpponentAsync(you, ctx.Source, $"{ctx.Source.Name}: choose the opponent who picks the card") is not { } chooser) break;
                var pick = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose the card {State.GetPlayer(you).Name} puts into their hand", ctx.Source.Id,
                    shown.Select(id => ViewBuilder.Card(State, id, chooser, reveal: true)).ToList(), 1, 1, CardChoicePurpose.ToHand));
                Require(pick.Count == 1 && shown.Contains(pick[0]), "Choose one of the revealed cards.");
                Emit(new ChoiceMade(pick[0], $"chosen by {State.GetPlayer(chooser).Name}"));
                await MoveCardAsync(pick[0], Zone.Hand);
                break;
            }
            case MountainsAddExtraRedThisTurn:
                State.GetPlayer(ctx.Controller).ExtraRedFromMountainsThisTurn++;
                RecomputeContinuousEffects();
                break;
            case ProtectionAndLifeLockUntilTheirTurn pl:
                foreach (var who in PlayersFor(pl.Who, ctx).ToList())
                {
                    State.GetPlayer(who).Protected = true;
                    State.GetPlayer(who).LifeCantChange = true;
                }
                break;
            case CantAttackYourPlaneswalkersThisTurn cp:
                foreach (var who in PlayersFor(cp.Who, ctx).ToList())
                    State.PlaneswalkerAttackBans.Add((who, ctx.Controller, cp.Subtype, State.TurnNumber));
                break;
            case NoLegendRuleThisTurn:
                State.GetPlayer(ctx.Controller).NoLegendRuleThisTurn = true;
                break;
            case NextSpellUncounterable:
                State.GetPlayer(ctx.Controller).NextSpellUncounterableThisTurn = true;
                break;
            case LookAtTopMayBottom lb:
                foreach (var owner in PlayersFor(lb.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(owner).Library;
                    if (library.Count == 0) continue;
                    var top = library[0];
                    Look(ctx.Controller, new[] { top });
                    Emit(new LookedAtTop(ctx.Controller, 1, 0, Scry: false));
                    if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                            new YesNoRequest($"Put {State.GetCard(top).Name} on the bottom of {State.GetPlayer(owner).Name}'s library?", top)))
                    {
                        library.Remove(top);
                        library.Add(top);
                        Emit(new ChoiceMade(ctx.Source.Id, $"{State.GetPlayer(ctx.Controller).Name} puts the top card of {State.GetPlayer(owner).Name}'s library on the bottom"));
                    }
                }
                break;
            case CopiesOfDestroyedHalved ch:
                foreach (var died in ctx.Results.DestroyedCards.Select(State.GetCard).ToList())
                {
                    // It died: a creature put into a graveyard from the battlefield (a token there too, before it ceased to exist).
                    if (died.LastKnownInfo is not { CopiableValues: { } values } lki || !lki.Types.HasFlag(CardType.Creature)) continue;
                    int HalfUp(int n) => (int)Math.Ceiling(n / 2.0);
                    var copy = values with
                    {
                        IsToken = true, ImageByName = values.ImageByName || (!values.IsToken && values.ImageKey is null),
                        Power = HalfUp(lki.Power), Toughness = HalfUp(lki.Toughness), PowerFrom = null, ToughnessFrom = null,
                    };
                    ctx.Results.Created.AddRange((await CreateTokenEventAsync(lki.Controller, copy, ch.Count, false, ctx.Source, lki.CopiableBackFaceUp)).Originals);
                }
                break;
            case EachDrawsDamageDealtToSource:
            {
                // The object as it was on the battlefield (this ability triggered as it left).
                int version = (ctx.SourceVersion ?? ctx.Source.Version) - (ctx.Source.Zone == Zone.Battlefield ? 0 : 1);
                var dealtBy = State.DamageLog.Where(r => r.Turn == State.TurnNumber && r.TargetCard == ctx.Source.Id && r.TargetVersion == version)
                    .GroupBy(r => r.SourceController).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
                foreach (var player in State.ApnapOrder().ToList())
                    if (dealtBy.TryGetValue(player, out int amount) && amount > 0) await DrawAsync(player, amount);
                break;
            }
            case ReturnToHandAtEndOfCombat re:
                foreach (var card in CardsFor(re.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.AtEndOfCombat.Add(new DelayedTrigger(ctx.Source.Id, ReturnThatCreatureToHand, ctx.Controller, 0) { AboutCard = (card.Id, card.Version) });
                break;
            case RevealTopCastOneFree rc:
            {
                var player = State.GetPlayer(ctx.Controller);
                var revealed = player.Library.Take(Math.Max(0, Eval(rc.Count, ctx))).ToList();
                if (revealed.Count == 0) break;
                if (!rc.Look)
                {
                    RevealToAll(revealed);
                    Emit(new CardsRevealed(ctx.Controller, revealed));
                }
                else foreach (var id in revealed) State.GetCard(id).KnownTo.Add(ctx.Controller);
                int maxValue = rc.MaxManaValue is { } rcMax ? Eval(rcMax, ctx) : int.MaxValue;
                var castable = revealed.Select(State.GetCard).Where(c => !c.Is(CardType.Land) && ManaValueOf(c) <= maxValue
                    && Matches(rc.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller)
                    && HasLegalTargets(CastingTargets(c.Definition), ctx.Controller, c.Id)).ToList();
                if (castable.Count > 0)
                {
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: you may cast one of these without paying its mana cost", ctx.Source.Id,
                        castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    Require(pick.Count <= 1 && pick.All(id => castable.Any(c => c.Id == id)), "Choose one of the revealed cards.");
                    if (pick.Count == 1) await CastNowWithoutPayingAsync(player, pick[0]);
                }
                var rest = revealed.Where(id => State.GetCard(id).Zone == Zone.Library).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case AttackersAttackAgainNotYou:
            {
                if (State.Combat is not { } combat) break;
                var attackers = combat.Attacks.Select(a => State.GetCard(a.Attacker)).Where(c => c.Zone == Zone.Battlefield).ToList();
                foreach (var attacker in attackers)
                {
                    combat.Remove(attacker.Id);
                    Untap(attacker);
                    State.NextCombatRules.Add((attacker.Id, attacker.Version, ctx.Controller, State.TurnNumber));
                }
                _extraCombats++;
                break;
            }
            case ExileLibrary el:
                foreach (var owner in PlayersFor(el.Who, ctx).ToList())
                {
                    BeginSimultaneous();
                    foreach (var id in State.GetPlayer(owner).Library.ToList()) await MoveCardAsync(id, Zone.Exile);
                    EndSimultaneous();
                }
                break;
            case ShuffleHandIntoLibrary sh:
                foreach (var owner in PlayersFor(sh.Who, ctx).ToList())
                {
                    var player = State.GetPlayer(owner);
                    foreach (var id in player.Hand.ToList()) await MoveCardAsync(id, Zone.Library);
                    Shuffle(player);
                }
                break;
            case RepeatProcess when ctx.Resolving is { } repeated:
                ctx.Results.Milled.Clear(); // "if an Insect card was milled this way": each time through the process counts only its own
                await ApplyAllAsync(repeated.Effects, ctx);
                break;
            case CreatureSpellsUncounterableThisTurn:
                // It covers the creature spells already cast this turn too, not only later ones.
                State.GetPlayer(ctx.Controller).CreatureSpellsUncounterableThisTurn = true;
                foreach (var spell in State.Stack.OfType<SpellOnStack>().Where(s => s.Controller == ctx.Controller && State.GetCard(s.Card).IsCreature))
                    State.GetCard(spell.Card).Uncounterable = true;
                break;
            case RemoveUpToCounters ru:
                foreach (var card in CardsFor(ru.From, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    int left = ru.Max;
                    foreach (var kind in card.Counters.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
                    {
                        if (left == 0) break;
                        int most = Math.Min(left, card.CounterCount(kind));
                        int n = await ControllerOf(ctx.Controller).ChooseNumberAsync(ViewFor(ctx.Controller),
                            new NumberRequest($"{ctx.Source.Name}: remove how many {kind} counters from {card.Name}?", ctx.Source.Id, 0, most));
                        Require(n >= 0 && n <= most, $"Choose a number from 0 to {most}.");
                        if (n > 0)
                        {
                            int before = card.CounterCount(kind);
                            RemoveCountersFrom(card, kind, n);
                            ctx.Results.CountersRemoved += before - card.CounterCount(kind); // "the number of counters removed this way"
                        }
                        left -= n;
                    }
                }
                break;
            case LoyaltyAbilitiesAtInstantSpeed li:
                State.GetPlayer(ctx.Controller).InstantLoyaltySubtypes.Add(li.Subtype);
                break;
            case EmpowerJace ej:
                await EmpowerJaceAsync(ctx.Controller, Eval(ej.Amount, ctx), ctx.Source);
                break;
            case BecomeRenowned br:
                foreach (var card in CardsFor(br.What, ctx).ToList())
                {
                    bool already = card.Renowned;
                    card.Renowned = true;
                    if (!already && card.Zone == Zone.Battlefield) QueueBecameRenowned(card);
                }
                break;
            case Transform tf:
                foreach (var card in CardsFor(tf.What, ctx).Distinct().ToList())
                {
                    if (card.Id == ctx.Source.Id)
                    {
                        // An ability of this permanent: only the object it came from (rule 400.7), and only if that object hasn't
                        // transformed since the ability was put on the stack or, for a delayed ability, was created (701.27f).
                        if (!IsSourceObject(ctx)) continue;
                        if (ctx.SourceTransforms is { } before && before != card.TransformCount) continue;
                    }
                    if (TransformPermanent(card) && card.Transformed && card.Definition.AsTransformsInto is { } asItTransforms)
                        await ApplyAllAsync(asItTransforms, new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()));
                }
                break;
            case Regenerate rg:
                foreach (var card in CardsFor(rg.What, ctx)) card.RegenerationShields++;
                break;
            case Populate:
            {
                var tokens = State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Definition.IsToken).ToList();
                if (tokens.Count == 0) break;
                var pick = tokens.Count == 1 ? new[] { tokens[0].Id } : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest("Populate: choose a creature token to copy", ctx.Source.Id, tokens.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(pick.Count == 1 && tokens.Any(c => c.Id == pick[0]), "Choose one of your creature tokens.");
                var original = State.GetCard(pick[0]);
                // A copy of a token copies its copiable values (rule 707.2): the original definition.
                var (populated, populatedBackUp) = CopiableForToken(original);
                foreach (var made in (await CreateTokenEventAsync(ctx.Controller, populated with { IsToken = true }, 1, false, ctx.Source, populatedBackUp)).Originals) ctx.Results.Created.Add(made);
                break;
            }
            case CastForMiracle:
            {
                if (ctx.Trigger?.Subject is not { } drawn || State.GetCard(drawn) is not { Zone: Zone.Hand } card || card.Version != ctx.Trigger.SubjectVersion
                    || MiracleCost(card) is not { } miracle) break;
                _miracleCost[card.Id] = miracle;
                try
                {
                    if (!CanBeCast(card, ctx.Controller, flashExtra: false)
                        || !await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {card.Name} for its miracle cost {miracle}?", card.Id))) break;
                    await CastSpellAsync(State.GetPlayer(ctx.Controller), card.Id);
                }
                finally { _miracleCost.Remove(card.Id); }
                break;
            }
            case ThisSpellToLibraryBottom:
                if (ctx.Source.Zone == Zone.Stack) _spellToLibraryBottom = ctx.Source.Id;
                break;
            case ExileThisSpell:
                if (ctx.Source.Zone == Zone.Stack) _exileResolvingSpell = ctx.Source.Id;
                break;
            case Vote vote:
                await VoteAsync(vote, ctx);
                break;
            case StunVotedCreatures:
                await StunVotedCreaturesAsync(ctx);
                break;
            case VotersGiveCreatures vg:
                await VotersGiveCreaturesAsync(vg, ctx);
                break;
            case GainVotersPermanents gvp:
                await GainVotersPermanentsAsync(gvp, ctx);
                break;
            case ForEachPlayer fe:
            {
                var each = State.ApnapOrder().Where(PlayersFor(fe.Who, ctx).ToList().Contains)
                    .Select(p => ctx with { Controller = p, AffectedPlayer = p }).Where(c => fe.If is not { } cond || HoldsIn(cond, c)).ToList();
                // "Each player may …": everyone decides in turn order, then it's done (rule 101.4).
                if (fe.Effects is [MayDo may])
                {
                    var yes = new List<EffectContext>();
                    foreach (var c in each)
                        if (await ControllerOf(c.Controller).ChooseYesNoAsync(ViewFor(c.Controller), new YesNoRequest(may.Prompt, ctx.Source.Id))) yes.Add(c);
                    foreach (var c in yes) await ApplyAllAsync(may.Effects, c);
                    break;
                }
                foreach (var c in each) await ApplyAllAsync(fe.Effects, c);
                break;
            }
            case CantAttackPlayerThisTurn cap:
                foreach (var player in PlayersFor(cap.Player, ctx).ToList())
                    State.CantAttackThisCombat.Add((ctx.Controller, player, State.TurnNumber, -1));
                break;
            case CantSacrificeThisTurn cst:
                foreach (var card in CardsFor(cst.What, ctx).ToList())
                    State.CantSacrificeThisTurn.Add((card.Id, card.Version, ctx.Controller, State.TurnNumber));
                break;
            case CantAttackYouThisCombat cay:
                foreach (var player in PlayersFor(cay.Who, ctx).ToList())
                    State.CantAttackThisCombat.Add((player, ctx.Controller, State.TurnNumber, State.CombatsThisTurn));
                break;
            default:
                return false;
        }
        return true;
    }
}
