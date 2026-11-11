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
    /// <summary>Effects handled by the group-3 part of the effect switch; false when the effect belongs to another group (the switch order is kept).</summary>
    private async Task<bool> ApplyGroup3Async(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case LoseGame lg:
                foreach (var loser in (lg.Who is { } who ? PlayersFor(who, ctx) : new[] { ctx.Controller }).ToList())
                    if (!CantLose(loser)) Lose(loser, $"{ctx.Source.Name}");
                break;
            case RemoveFromCombat rc:
                foreach (var card in (rc.What.Kind == SubjectKind.Self ? (IsSourceObject(ctx) ? new[] { ctx.Source } : Array.Empty<Card>()) : CardsFor(rc.What, ctx)).ToList())
                    State.Combat?.Remove(card.Id);
                break;
            case LoseAllAbilitiesUntilEndOfTurn la:
                foreach (var card in (la.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } lid } li && State.GetCard(lid) is { Zone: Zone.Battlefield } lc && lc.Version == li.SubjectVersion
                             ? new[] { lc } : CardsFor(la.What, ctx)).ToList())
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>()) { LosesAbilities = true, Timestamp = NewTimestamp() });
                break;
            case ReturnToHand r:
                if (r.What.Kind == SubjectKind.Self && SourceIn(ctx, Zone.Graveyard)) await MoveCardAsync(ctx.Source.Id, Zone.Hand); // "return this card"
                foreach (var card in CardsFor(r.What, ctx).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Hand);
                    if (State.GetCard(card.Id).Zone == Zone.Hand) ctx.Results.Returned.Add(card.Id);
                }
                break;
            case MayPay mp:
            {
                if (mp.OncePerTurn && ctx.Source.DoneThisTurn.Contains(mp)) break; // "Do this only once each turn"
                var payer = State.GetPlayer(ctx.Controller);
                // The ways to pay: one cost, or a choice among several ("sacrifice a Food or pay {2}{W}").
                var lifeFrom = mp.LifeFrom is { } lf ? Eval(lf, ctx) : 0;
                if (mp.LifeFrom is not null && lifeFrom <= 0 && !mp.PayZero) { if (mp.Else is { } none) await ApplyAllAsync(none, ctx); break; }
                var ways = (mp.Options ?? new[] { new CostOption(mp.Mana, mp.LifeFrom is not null ? (mp.Extra ?? new ExtraCost()) with { PayLife = lifeFrom } : mp.Extra) })
                    .Where(o => (o.Mana is null || Payable(ctx.Controller, o.Mana, null)) && CanPayExtra(ctx.Controller, o.Extra, ctx.Source.Id)).ToList();
                CostOption? way = null;
                if (ways.Count > 0)
                {
                    if (mp.Options is null)
                        way = await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(mp.Prompt, ctx.Source.Id)) ? ways[0] : null;
                    else
                    {
                        var labels = ways.Select(o => string.Join(" and ", new[] { o.Mana?.ToString(), o.Extra is { } x ? DescribeCost(x) : null }.Where(t => !string.IsNullOrEmpty(t)))).Append("Don't pay").ToList();
                        int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest(mp.Prompt, ctx.Source.Id, labels, OptionKind.Other));
                        Require(pick >= 0 && pick < labels.Count, "Choose one of the listed options.");
                        way = pick < ways.Count ? ways[pick] : null;
                    }
                }
                if (way is not null && (way.Mana is not { } mana || await PayManaAsync(payer, ctx.Source.Id, mana, null)))
                {
                    if (mp.OncePerTurn) ctx.Source.DoneThisTurn.Add(mp);
                    _lastDiscardedForCost.Clear();
                    ctx.Results.Sacrificed.AddRange(await PayExtraAsync(ctx.Controller, way.Extra, ctx.Source.Id)); // "the sacrificed permanent"
                    ctx.Results.Discarded.AddRange(_lastDiscardedForCost); // "a creature card was discarded this way"
                    await ApplyAllAsync(mp.Effects, ctx);
                }
                else if (mp.Else is { } otherwise) await ApplyAllAsync(otherwise, ctx);
                break;
            }
            case ReturnFromGraveyard rg:
            {
                var who = ctx.Controller;
                var options = (rg.AnyGraveyard ? State.ApnapOrder().SelectMany(p => State.GetPlayer(p).Graveyard) : State.GetPlayer(who).Graveyard).Select(State.GetCard)
                    .Where(c => !(rg.ExcludeSacrificed && ctx.Results.Sacrificed.Contains(c.Id)))
                    .Where(c => !rg.AmongDestroyed || ctx.Results.DestroyedCards.Contains(c.Id))
                    .Where(c => rg.Filter is null || Matches(rg.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who))
                    .Select(c => ViewBuilder.Card(State, c.Id, who)).ToList();
                if (options.Count == 0) break;
                int max = Math.Min(rg.Count, options.Count);
                var purpose = rg.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand;
                IReadOnlyList<CardId> chosen;
                if (rg.DifferentNames)
                {
                    // "With different names": one at a time, each among the names not chosen yet.
                    var picked = new List<CardId>();
                    while (picked.Count < max)
                    {
                        var left = options.Where(o => picked.All(p => State.GetCard(p).Name != State.GetCard(o.Id).Name)).ToList();
                        if (left.Count == 0) break;
                        var one = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                            $"Choose a card with a different name from your graveyard ({picked.Count + 1}; choose none to stop)", ctx.Source.Id, left, 0, 1, purpose));
                        Require(one.Count <= 1 && one.All(id => left.Any(o => o.Id == id)), "Choose among the listed cards.");
                        if (one.Count == 0) break;
                        picked.Add(one[0]);
                    }
                    chosen = picked;
                }
                else chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who),
                    new CardChoiceRequest($"Choose {(rg.UpTo ? "up to " : "")}{max} from your graveyard", ctx.Source.Id, options, rg.UpTo ? 0 : max, max, purpose));
                Require(chosen.Count <= max && (rg.UpTo || chosen.Count == max) && chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(o => o.Id == id)),
                    "Choose among the listed cards.");
                // "With different mana values": a card with a mana value already chosen isn't returned.
                if (rg.DifferentManaValues) chosen = chosen.GroupBy(id => ManaValueOf(State.GetCard(id))).Select(g => g.First()).ToList();
                foreach (var id in chosen)
                {
                    await MoveCardAsync(id, rg.To, controller: who, tapped: rg.Tapped && rg.To == Zone.Battlefield,
                        withCounters: rg.EntersWithCounter is { } enterKind && rg.To == Zone.Battlefield ? new[] { (enterKind, 1) } : null);
                    if (rg.To == Zone.Battlefield && State.GetCard(id).Zone == Zone.Battlefield) ctx.Results.Entered.Add(id);
                }
                break;
            }
            case DiscardHand dh:
                foreach (var player in PlayersFor(dh.Who, ctx).ToList())
                {
                    ctx.Results.HandDiscarders.Add(player); // "each opponent who discarded their hand this way" (an empty hand too)
                    foreach (var id in State.GetPlayer(player).Hand.ToList())
                    {
                        await DiscardCardAsync(player, id, ctx.Controller, byEffect: true);
                        ctx.Results.Discarded.Add(id);
                    }
                }
                break;
            case TapIt t:
                foreach (var card in CardsFor(t.What, ctx).Where(c => !c.Tapped)) Tap(card);
                break;
            case UntapIt u:
                foreach (var card in CardsFor(u.What, ctx).Where(c => c.Tapped)) Untap(card);
                break;
            case Mill m:
                foreach (var player in PlayersFor(m.Who, ctx).ToList())
                {
                    bool again;
                    do
                    {
                        var milled = new List<Card>();
                        int count = Eval(m.Count, ctx with { AffectedPlayer = player });
                        // "If an opponent would mill one or more cards, they mill that many cards plus four instead" (each such permanent applies).
                        if (count > 0) count += 4 * State.OpponentsOf(player).Sum(o => Instances(o, Replacements.OpponentsMillFourMore));
                        foreach (var id in State.GetPlayer(player).Library.Take(count).ToList())
                        {
                            await MoveCardAsync(id, Zone.Graveyard);
                            // A card a replacement effect sent elsewhere wasn't put into the graveyard "this way".
                            if (State.GetCard(id).Zone != Zone.Graveyard) continue;
                            ctx.Results.Milled.Add(id);
                            milled.Add(State.GetCard(id));
                        }
                        var nonland = milled.Where(c => !c.Is(CardType.Land)).ToList();
                        again = m.RepeatWhileNonlandShareColor && nonland.Count >= 2
                                && nonland.SelectMany(c => c.Colors).GroupBy(c => c).Any(g => g.Count() >= 2);
                    }
                    while (again && State.GetPlayer(player).Library.Count > 0);
                }
                break;
            case RevealFromHandYouChooseExile rh:
                foreach (var revealer in PlayersFor(rh.Who, ctx).ToList())
                {
                    var hand = State.GetPlayer(revealer).Hand.ToList();
                    int n = Math.Min(Eval(rh.Count, ctx), hand.Count);
                    if (n <= 0) continue;
                    // The revealing player chooses which cards to reveal.
                    var shown = n == hand.Count ? hand : (await ControllerOf(revealer).ChooseCardsAsync(ViewFor(revealer), new CardChoiceRequest(
                        $"{ctx.Source.Name}: reveal {n} cards from your hand", ctx.Source.Id, hand.Select(id => ViewBuilder.Card(State, id, revealer)).ToList(), n, n, CardChoicePurpose.Keep))).ToList();
                    Require(shown.Count == n && shown.Distinct().Count() == n && shown.All(hand.Contains), $"Reveal {n} cards from your hand.");
                    Emit(new CardsRevealed(revealer, shown));
                    var chosenCard = shown.Count == 1 ? shown : (await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose the card that player exiles", ctx.Source.Id, shown.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Discard))).ToList();
                    Require(chosenCard.Count == 1 && shown.Contains(chosenCard[0]), "Choose one of the revealed cards.");
                    var exiledCard = State.GetCard(chosenCard[0]);
                    await MoveCardAsync(exiledCard.Id, Zone.Exile);
                    if (exiledCard.Zone != Zone.Exile) continue;
                    ctx.Results.Exiled.Add(exiledCard.Id);
                    if (exiledCard.Is(CardType.Instant) || exiledCard.Is(CardType.Sorcery))
                        State.PlayableFromExile.Add(new PlayableFromExile(exiledCard.Id, exiledCard.Version, ctx.Controller, int.MaxValue, false)
                        {
                            AnyManaType = true, CastOnly = true, MadeOnTurn = State.TurnNumber,
                            WhileControlling = (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version),
                        });
                }
                break;
            case RevealNonlandExileOnePerColor:
            {
                var player = State.GetPlayer(ctx.Controller);
                var colors = State.PermanentsControlledBy(ctx.Controller).SelectMany(c => c.Colors).Distinct()
                    .OrderBy(c => Array.IndexOf(ColorLetters, c)).ToList();
                int wanted = colors.Count, nonland = 0;
                var revealed = new List<CardId>();
                foreach (var id in player.Library.ToList())
                {
                    if (nonland >= wanted) break;
                    revealed.Add(id);
                    if (!State.GetCard(id).Is(CardType.Land)) nonland++;
                }
                if (revealed.Count > 0) Emit(new CardsRevealed(ctx.Controller, revealed));
                var toExile = new List<CardId>();
                foreach (var color in colors)
                {
                    var ofColor = revealed.Where(id => !toExile.Contains(id) && State.GetCard(id).Colors.Contains(color)).ToList();
                    if (ofColor.Count == 0) continue;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: you may exile a {ColorNames[Array.IndexOf(ColorLetters, color)].ToLowerInvariant()} card", ctx.Source.Id,
                        ofColor.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToHand));
                    Require(pick.Count <= 1 && pick.All(ofColor.Contains), "Choose one of the revealed cards of that color.");
                    toExile.AddRange(pick);
                }
                foreach (var id in toExile)
                {
                    await MoveCardAsync(id, Zone.Exile);
                    if (State.GetCard(id) is not { Zone: Zone.Exile } exiled) continue;
                    ctx.Results.Exiled.Add(id);
                    State.PlayableFromExile.Add(new PlayableFromExile(id, exiled.Version, ctx.Controller, State.TurnNumber) { CastOnly = true, MadeOnTurn = State.TurnNumber });
                }
                Shuffle(player);
                break;
            }
            case Champion ch:
            {
                // "Sacrifice it unless you exile another [filter] you control" (rule 702.72a): the player may choose one, or none.
                var options = State.PermanentsControlledBy(ctx.Controller)
                    .Where(c => c.Id != ctx.Source.Id && Matches(ch.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                var pick = options.Count == 0 ? Array.Empty<CardId>() : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"Champion: exile another one you control, or {ctx.Source.Name} is sacrificed", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, 1, CardChoicePurpose.Sacrifice));
                Require(pick.Count <= 1 && pick.All(id => options.Any(c => c.Id == id)), "Choose one of the listed permanents, or none.");
                if (pick.Count == 1)
                {
                    var championed = State.GetCard(pick[0]);
                    var championBy = (ctx.Source.Id, ExpectedSourceVersion(ctx) ?? ctx.Source.Version);
                    await MoveCardAsync(championed.Id, Zone.Exile);
                    if (championed.Zone == Zone.Exile)
                    {
                        championed.ExiledWith = championBy;
                        ctx.Results.Exiled.Add(championed.Id);
                        Queue(ctx.Source.Id, TriggerEvent.Championed, ctx.Controller, new TriggerInfo(championed.Id, championed.Version, ctx.Controller));
                    }
                }
                else if (SourceOnBattlefield(ctx) && ctx.Source.Controller == ctx.Controller)
                    await SacrificePermanentAsync(ctx.Source.Id);
                break;
            }
            case SacrificeNotSharingTypeWithChosen:
            {
                var doomed = new List<CardId>();
                foreach (var player in State.ApnapOrder().ToList())
                {
                    var creatures = State.PermanentsControlledBy(player).Where(c => c.IsCreature).ToList();
                    if (creatures.Count == 0) continue;
                    var pick = creatures.Count == 1 ? new[] { creatures[0].Id } : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose a creature {State.GetPlayer(player).Name} controls", ctx.Source.Id,
                        creatures.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(pick.Count == 1 && creatures.Any(c => c.Id == pick[0]), "Choose one of that player's creatures.");
                    var kept = State.GetCard(pick[0]);
                    Emit(new ChoiceMade(kept.Id, kept.Name));
                    doomed.AddRange(creatures.Where(c => c.Id != kept.Id && !SharesCreatureType(c, kept)).Select(c => c.Id));
                }
                BeginSimultaneous();
                foreach (var id in doomed)
                    if (State.GetCard(id) is { Zone: Zone.Battlefield } victim && CanBeSacrificedBy(victim, victim.Controller)) await SacrificePermanentAsync(id);
                EndSimultaneous();
                break;
            }
            case CounterAllOfOpponents:
                // "Counter all spells your opponents control and all abilities your opponents control" (rule 701.6).
                foreach (var item in State.Stack.Where(s => State.OpponentsOf(ctx.Controller).Contains(s.Controller)).ToList())
                {
                    if (!State.Stack.Contains(item)) continue;
                    if (item is SpellOnStack spell && State.GetCard(spell.Card) is { Zone: Zone.Stack } spellCard && CanBeCountered(spellCard))
                    {
                        ctx.Results.Countered.Add((spell.Card, spellCard.Version));
                        await CounterSpellOnStackAsync(spell.Card);
                    }
                    else if (item is AbilityOnStack ability)
                    {
                        State.Stack.Remove(ability);
                        ctx.Results.AbilitiesCountered++;
                        Emit(new AbilityCountered(ability.SourceCard));
                    }
                }
                break;
            case CounterSpell c when c.UnlessPays is not null || c.UnlessPaysGeneric is not null:
                if (c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.Card is { } taxed
                    && State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == taxed) is { } taxedItem)
                {
                    var tax = c.UnlessPays ?? ManaCost.Zero.PlusGeneric(Math.Max(0, Eval(c.UnlessPaysGeneric!, ctx)));
                    await ApplyAsync(new CounterUnlessPays(taxedItem.Id, tax, 0) { ExileIfCountered = c.ExileIfCountered, IfPaid = c.IfPaid }, ctx);
                }
                break;
            case CounterSpell c when c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.StackObject is { } abilityItem:
                // "Counter target activated or triggered ability": it's removed from the stack (rule 701.6b).
                if (State.Stack.FirstOrDefault(s => s.Id == abilityItem) is AbilityOnStack counteredAbility)
                {
                    State.Stack.Remove(counteredAbility);
                    Emit(new AbilityCountered(counteredAbility.SourceCard));
                }
                break;
            case CounterSpell c:
                // "Counter that spell": the spell the trigger was about, if it's still on the stack.
                if (c.What.Kind == SubjectKind.Triggered && ctx.Trigger?.Subject is { } triggeredSpell && State.GetCard(triggeredSpell) is { Zone: Zone.Stack } onStack
                    && onStack.Version == ctx.Trigger.SubjectVersion && CanBeCountered(onStack))
                {
                    ctx.Results.Countered.Add((triggeredSpell, onStack.Version));
                    await CounterSpellOnStackAsync(triggeredSpell);
                }
                else if (c.What.Kind == SubjectKind.Target && ctx.TargetAt(c.What.Index)?.Card is { } spellCard && CanBeCountered(State.GetCard(spellCard)))
                {
                    var countered = State.GetCard(spellCard);
                    bool permanent = countered.Types.IsPermanent() && !countered.Definition.IsToken;
                    if (countered.Zone == Zone.Stack) ctx.Results.Countered.Add((spellCard, countered.Version));
                    // "If that spell is countered this way, exile it instead of putting it into its owner's graveyard. You may cast that card without
                    // paying its mana cost for as long as it remains exiled."
                    bool exilePlayable = c.ExilePlayable && !countered.Definition.IsToken;
                    if (exilePlayable) State.ExileInsteadOfGraveyard.Add((spellCard, countered.Version));
                    await CounterSpellOnStackAsync(spellCard);
                    if (exilePlayable && countered.Zone == Zone.Exile)
                        State.PlayableFromExile.Add(new PlayableFromExile(spellCard, countered.Version, ctx.Controller, int.MaxValue, WithoutPaying: true));
                    if (c.ExilePermanentPlayable && permanent && countered.Zone != Zone.Exile)
                    {
                        // "Exile it instead …; you may cast that card without paying its mana cost for as long as it remains exiled."
                        await MoveCardAsync(spellCard, Zone.Exile);
                        if (countered.Zone == Zone.Exile)
                            State.PlayableFromExile.Add(new PlayableFromExile(spellCard, countered.Version, ctx.Controller, int.MaxValue, WithoutPaying: true));
                    }
                }
                break;
            case PumpUntilEndOfTurn p:
            {
                if (p.WhileSourceRemains && !SourceOnBattlefield(ctx)) break;
                if (p.WhileYouControlSource && ctx.Source.Controller != ctx.Controller) break;
                int power = Eval(p.Power, ctx), toughness = Eval(p.Toughness, ctx);
                foreach (var card in CardsFor(p.What, ctx).Where(c => c.IsCreature || (power == 0 && toughness == 0)))
                    (p.WhileSourceRemains || p.UntilYourNextTurn ? State.LastingEffects : State.UntilEndOfTurn).Add(new UntilEndOfTurnEffect(card.Id, card.Version, power, toughness,
                        p.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                    {
                        LoseKeywords = p.LoseKeywords,
                        Timestamp = p.CantHaveLostKeywords ? long.MaxValue : NewTimestamp(), WhileSource = p.WhileSourceRemains ? (ctx.Source.Id, ctx.Source.Version) : null,
                        WhileControlledBy = p.WhileYouControlSource ? ctx.Controller : null,
                        UntilTurnOf = p.UntilYourNextTurn ? ctx.Controller : null,
                        // "Can't be blocked by creatures your opponents control": the opponents of this effect's controller.
                        CantBeBlockedBy = p.CantBeBlockedBy is { Controller: ControllerFilter.Opponent } byOpponents
                            ? byOpponents with { OpponentOf = ctx.Controller } : p.CantBeBlockedBy,
                    });
            }
                break;
            case DoublePowerAndToughness dpt:
                // 701.10d-e: it gets +X/+X, X being its power and toughness now (a negative power doubles down).
                foreach (var card in CardsFor(dpt.What, ctx).Where(c => c.IsCreature).ToList())
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, card.Power, card.Toughness, Array.Empty<Keyword>()) { Timestamp = NewTimestamp() });
                break;
            case AddCounters a:
            {
                int count = Eval(a.Count, ctx);
                if (count <= 0) break;
                BeginSimultaneous(); // counters put on several permanents at once: "one or more" triggers see one event
                foreach (var card in CardsFor(a.What, ctx)) PutCounters(card, a.Kind, count, ctx.Controller);
                EndSimultaneous();
            }
                break;
            case Abilities.Blight blight:
                await ApplyBlightAsync(blight, ctx);
                break;
            case AttachSelf a:
                if (SourceOnBattlefield(ctx))
                    foreach (var card in CardsFor(a.To, ctx).Where(c => !ctx.Source.HasSubtype("Equipment") || c.IsCreature))
                    {
                        ctx.Source.AttachedTo = card.Id;
                        ctx.Source.Timestamp = 0; // becoming attached gives it a new timestamp (rule 613.7e)
                    }
                break;
            case LinkedExiledOwnerCreatesToken lo:
            {
                int version = (ctx.SourceVersion ?? ctx.Source.Version) - (ctx.Source.Zone == Zone.Battlefield ? 0 : 1);
                foreach (var card in State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id && w.Version == version).ToList())
                    ctx.Results.Created.AddRange((await CreateTokenEventAsync(card.Owner, lo.Token with { Power = card.ManaValue, Toughness = card.ManaValue }, 1, false, ctx.Source)).Originals);
                break;
            }
            case ReturnLinkedExiled:
            {
                // The object that exiled them, as it was on the battlefield (this ability triggered as it left).
                int version = (ctx.SourceVersion ?? ctx.Source.Version) - (ctx.Source.Zone == Zone.Battlefield ? 0 : 1);
                BeginEnteringTogether();
                foreach (var card in State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id && w.Version == version).ToList())
                    await MoveCardAsync(card.Id, Zone.Battlefield, controller: card.Owner);
                EndEnteringTogether();
                break;
            }
            case CantBlockThisTurn cb:
                State.CantBlockThisTurn.Add((cb.Filter, ctx.Controller, State.TurnNumber));
                break;
            case UnblockableByMostLifePlayer ub:
            {
                int most = State.LivingPlayers.Max(p => p.Life);
                var tied = State.ApnapOrder().Where(p => State.GetPlayer(p).Life == most).ToList();
                var chosen = tied[0];
                if (tied.Count > 1)
                {
                    int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"{ctx.Source.Name}: choose a player with the most life", ctx.Source.Id, tied.Select(p => State.GetPlayer(p).Name).ToList(), OptionKind.Other));
                    Require(pick >= 0 && pick < tied.Count, "Choose one of the players.");
                    chosen = tied[pick];
                }
                Emit(new ChoiceMade(ctx.Source.Id, State.GetPlayer(chosen).Name));
                foreach (var card in CardsFor(ub.What, ctx))
                    State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>()) { UnblockableBy = chosen, Timestamp = NewTimestamp() });
                break;
            }
            case PhaseOut po:
                foreach (var card in CardsFor(po.What, ctx).Where(c => State.Battlefield.Contains(c.Id)).ToList())
                {
                    // Already phased out with the permanent it's attached to: a permanent phases out only once.
                    if (!State.Battlefield.Contains(card.Id)) continue;
                    // Anything attached phases out with it (indirect phasing, rule 702.26g).
                    foreach (var phasing in State.Battlefield.Select(State.GetCard).Where(a => a.AttachedTo == card.Id).Append(card).ToList())
                    {
                        State.Battlefield.Remove(phasing.Id);
                        State.Combat?.Remove(phasing.Id);
                        State.PhasedOut.Add((phasing.Id, card.Controller));
                        Emit(new PhasedOut(phasing.Id));
                    }
                }
                break;
            case RingTemptsYou:
            {
                var player = State.GetPlayer(ctx.Controller);
                player.RingLevel++;
                var creatures = State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature).ToList();
                if (creatures.Count > 0)
                {
                    var pick = creatures.Count == 1 ? new[] { creatures[0].Id } : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest("The Ring tempts you: choose your Ring-bearer", ctx.Source.Id, creatures.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(pick.Count == 1 && creatures.Any(c => c.Id == pick[0]), "Choose one of your creatures.");
                    player.RingBearer = (pick[0], State.GetCard(pick[0]).Version);
                }
                Emit(new RingTempted(ctx.Controller, player.RingLevel, player.RingBearer?.Card));
                // "Whenever the Ring tempts you, if you chose a creature other than …": the trigger knows the creature chosen this time.
                var chosen = creatures.Count > 0 && RingBearerOf(ctx.Controller) is { } bearer ? new TriggerInfo(bearer.Id, bearer.Version, ctx.Controller) : new TriggerInfo(Player: ctx.Controller);
                foreach (var card in State.PermanentsControlledBy(ctx.Controller).ToList())
                {
                    Queue(card.Id, TriggerEvent.RingTemptsYou, ctx.Controller, chosen);
                    if (creatures.Count > 0) Queue(card.Id, TriggerEvent.RingBearerChosen, ctx.Controller, chosen);
                }
                foreach (var card in player.Graveyard.Select(State.GetCard).ToList())
                    foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard && a.Trigger == TriggerEvent.RingTemptsYou))
                        AddPending(card.Id, ability, ctx.Controller, chosen);
                break;
            }
            case PlayerProtection:
                State.GetPlayer(ctx.Controller).Protected = true;
                break;
            case CascadeEffect:
            {
                int manaValue = ctx.Trigger?.Amount ?? 0;
                var player = State.GetPlayer(ctx.Controller);
                var exiled = new List<CardId>();
                CardId? hit = null;
                while (player.Library.Count > 0)
                {
                    var top = player.Library[0];
                    await MoveCardAsync(top, Zone.Exile);
                    exiled.Add(top);
                    var card = State.GetCard(top);
                    if (!card.Is(CardType.Land) && card.ManaValue < manaValue) { hit = top; break; }
                }
                if (hit is { } found && HasLegalTargets(CastingTargets(State.GetCard(found).Definition), ctx.Controller, found)
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cascade: cast {State.GetCard(found).Name} without paying its mana cost?", found)))
                {
                    await CastNowWithoutPayingAsync(player, found);
                }
                var rest = exiled.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) await MoveCardAsync(id, Zone.Library, toBottom: true);
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case CastFromHandFree cf:
            {
                var who = ctx.Controller;
                int max = Eval(cf.MaxManaValue, ctx);
                // "Lesser mana value that shares a card type with it": the triggering spell as it is (or last was) on the stack.
                var spell = cf.LesserSharingTypeWithTriggered && ctx.Trigger?.Subject is { } spellId ? State.GetCard(spellId) : null;
                if (cf.LesserSharingTypeWithTriggered && spell is null) break;
                if (spell is not null)
                    max = Math.Min(max, (spell.Zone != Zone.Stack && spell.LastOnStack is { } lastSpell ? lastSpell.ManaValue : ManaValueOf(spell)) - 1);
                var options = State.GetPlayer(who).Hand.Select(State.GetCard)
                    .Where(c => !c.Is(CardType.Land) && c.ManaValue <= max && Matches(cf.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)
                                && (spell is null || (c.Definition.Types & spell.Definition.Types) != 0)
                                && HasLegalTargets(CastingTargets(c.Definition), who, c.Id)).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"You may cast a spell with mana value {max} or less from your hand for free", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                if (pick.Count != 1 || options.All(c => c.Id != pick[0])) break;
                if (await CastNowWithoutPayingAsync(State.GetPlayer(who), pick[0])) ctx.Results.CastThisWay = true;
                break;
            }
            case PutFromHand ph:
            {
                var who = ctx.Controller;
                int? mvCap = ph.MaxManaValue is { } cap ? Eval(cap, ctx) : null; // "with mana value X or less, where X is …"
                var options = State.GetPlayer(who).Hand.Concat(ph.OrGraveyard ? State.GetPlayer(who).Graveyard : Enumerable.Empty<CardId>()).Select(State.GetCard)
                    .Where(c => Matches(ph.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who) && (mvCap is not { } m || c.ManaValue <= m)).ToList();
                if (options.Count == 0) break;
                int most = ph.One ? 1 : ph.UpTo is { } upTo ? Math.Min(options.Count, Math.Max(0, Eval(upTo, ctx))) : options.Count;
                if (most == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(ph.One ? "You may put one onto the battlefield" : $"Put up to {most} onto the battlefield", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, most, CardChoicePurpose.ToBattlefield));
                Require(pick.Count <= most, $"Choose at most {most} cards.");
                BeginEnteringTogether();
                foreach (var id in pick.Where(id => options.Any(c => c.Id == id)))
                {
                    bool tappedAttacking = ph.TappedAttackingIf is { } taf && Matches(taf with { Controller = ControllerFilter.Any }, State.GetCard(id), who, ctx.Source, who);
                    _puttingOntoBattlefieldBy = ctx.Source.Id;
                    await MoveCardAsync(id, Zone.Battlefield, controller: who, tapped: ph.Tapped || tappedAttacking,
                        withCounters: ph.WithCounter is { } phKind ? new[] { (phKind, 1) } : null);
                    _puttingOntoBattlefieldBy = null;
                    var entered = State.GetCard(id);
                    if (entered.Zone != Zone.Battlefield) continue;
                    ctx.Results.Entered.Add(id); // "that creature gains haste", "sacrifice it at the next end step"
                    if ((ph.Attacking || tappedAttacking) && State.Combat is { } combat && entered.IsCreature)
                        combat.Attacks.Add(await AttackFor(entered, ctx));
                }
                EndEnteringTogether();
                break;
            }
            case DestroyAllButChosen dab:
            {
                var all = State.Battlefield.Select(State.GetCard).Where(c => Matches(dab.Filter with { Controller = ControllerFilter.Any }, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                var keep = all.Count == 0 ? Array.Empty<CardId>() : await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest($"Choose up to {dab.Keep} to keep", ctx.Source.Id, all.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, Math.Min(dab.Keep, all.Count), CardChoicePurpose.Keep));
                BeginSimultaneous();
                foreach (var card in all.Where(c => !keep.Contains(c.Id) && !c.Has(Keyword.Indestructible)))
                {
                    if (await SurvivesDestructionAsync(card)) continue;
                    bool creature = card.IsCreature;
                    await MoveCardAsync(card.Id, Zone.Graveyard);
                    ctx.Results.Destroyed++;
                    if (card.Zone == Zone.Graveyard) ctx.Results.DestroyedCards.Add(card.Id);
                    Emit(new PermanentDestroyed(card.Id));
                    if (creature) Emit(new CreatureDied(card.Id));
                }
                EndSimultaneous();
                break;
            }
            case HandToBottom hb:
            {
                var who = ctx.Controller;
                var hand = State.GetPlayer(who).Hand.ToList();
                int n = Math.Min(hb.Count, hand.Count);
                if (n == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"Put {n} card(s) from your hand on the bottom of your library", ctx.Source.Id,
                    hand.Select(id => ViewBuilder.Card(State, id, who)).ToList(), n, n, CardChoicePurpose.ScryToBottom));
                Require(pick.Count == n && pick.All(hand.Contains), "Choose cards from your hand.");
                foreach (var id in pick) await MoveCardAsync(id, Zone.Library, toBottom: true);
                PlacedInUnseenOrder(pick, who);
                break;
            }
            case TapAnyNumber ta:
            {
                var who = ctx.Controller;
                var options = State.PermanentsControlledBy(who).Where(c => !c.Tapped && Matches(ta.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Tap any number", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, options.Count, CardChoicePurpose.Sacrifice));
                foreach (var card in options.Where(c => pick.Contains(c.Id)))
                {
                    Tap(card);
                    ctx.Results.Tapped++;
                }
                break;
            }
            case OpponentChooses oc:
            {
                var opponent = Enumerable.Range(0, ctx.Targets.Count - ctx.TargetOffset).Select(i => ctx.TargetAt(i)?.Player).FirstOrDefault(p => p is not null)
                               ?? await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose an opponent");
                if (opponent is not { } chooser) break;
                bool yes = await ControllerOf(chooser).ChooseYesNoAsync(ViewFor(chooser), new YesNoRequest($"{ctx.Source.Name}: {oc.Prompt}", ctx.Source.Id));
                await ApplyAllAsync(yes ? oc.IfYes : oc.IfNo, ctx with { Trigger = new TriggerInfo(Player: chooser) });
                break;
            }
            case CastCopyOfExiled cc when cc.What.Kind == SubjectKind.ExiledThisWay:
                // "For each card exiled this way, copy it, and you may cast the copy": in the order the player chooses.
                foreach (var id in await OrderAsync(ctx.Controller, ctx.Results.Exiled.Where(e => State.GetCard(e).Zone == Zone.Exile).ToList(), "Choose the next card to copy and cast", ctx.Source))
                {
                    var single = ctx with { Results = new EffectResults() };
                    single.Results.Found.Add(id);
                    await ApplyAsync(cc with { What = new Subject(SubjectKind.Found) }, single);
                }
                break;
            case CastParadigmCopy pc:
            {
                var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                var copy = new Card(id, pc.Spell with { IsToken = true }, ctx.Controller) { Zone = Zone.Exile };
                State.Cards.Add(id, copy);
                State.GetPlayer(ctx.Controller).Exile.Add(id);
                if (HasLegalTargets(CastingTargets(copy.Definition), ctx.Controller, id)
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast the copy of {copy.Name} without paying its mana cost?", id)))
                    await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), id);
                if (copy.Zone == Zone.Exile)
                {
                    State.GetPlayer(ctx.Controller).Exile.Remove(id); // an uncast copy ceases to exist (rule 707.10a)
                    copy.CeasedToExist = true;
                }
                break;
            }
            case CastCopyOfExiled cc:
            {
                var original = CardsFor(cc.What, ctx).FirstOrDefault() ?? (ctx.TargetAt(0)?.Card is { } tc ? State.GetCard(tc) : null);
                if (original is null || original.Zone == Zone.Battlefield) break;
                if (original.Zone != Zone.Exile) await MoveCardAsync(original.Id, Zone.Exile);
                var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
                var copy = new Card(id, original.Definition with { IsToken = true }, ctx.Controller) { Zone = Zone.Exile };
                State.Cards.Add(id, copy);
                State.GetPlayer(ctx.Controller).Exile.Add(id);
                if ((cc.PayManaCost ? original.Definition.ManaCost : cc.Cost) is { } instead)
                {
                    // "You may cast the copy by paying [cost] rather than paying its mana cost."
                    _miracleCost[id] = instead;
                    try
                    {
                        if (!copy.Is(CardType.Land) && CanBeCast(copy, ctx.Controller, flashExtra: false)
                            && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast the copy of {copy.Name} by paying {instead}?", id)))
                            await CastSpellAsync(State.GetPlayer(ctx.Controller), id);
                    }
                    finally { _miracleCost.Remove(id); }
                }
                else if (!copy.Is(CardType.Land) && HasLegalTargets(CastingTargets(copy.Definition), ctx.Controller, id)
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast the copy of {copy.Name} without paying its mana cost?", id)))
                {
                    await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), id);
                }
                if (copy.Zone == Zone.Exile)
                {
                    State.GetPlayer(ctx.Controller).Exile.Remove(id); // an uncast copy ceases to exist (rule 704.5e)
                    copy.CeasedToExist = true;
                }
                break;
            }
            case Recruit rc:
                foreach (var player in PlayersFor(rc.Who, ctx).ToList())
                {
                    await DrawAsync(player);
                    var discarded = await DiscardAsync(player, 1, player);
                    ctx.Results.Discarded.AddRange(discarded);
                    if (!discarded.Any(id => !State.GetCard(id).Is(CardType.Land))) continue;
                    ctx.Results.Created.AddRange((await CreateTokenEventAsync(player, rc.Token, 1, false, ctx.Source)).Originals);
                }
                break;
            case AttachToRandomOtherOpponent:
            {
                if (!SourceOnBattlefield(ctx) || ctx.Source.AttachedPlayer is not { } enchanted) break;
                var others = State.OpponentsOf(ctx.Controller).Where(p => p != enchanted && !State.GetPlayer(p).HasLost).ToList();
                if (others.Count == 0) break; // no other opponent: it stays attached
                var next = others[Rng.Next(others.Count)];
                ctx.Source.AttachedPlayer = next;
                ctx.Source.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                Emit(new ChoiceMade(ctx.Source.Id, $"now enchanting {State.GetPlayer(next).Name} (chosen at random)"));
                break;
            }
            case Attach at:
            {
                var host = CardsFor(at.To, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield);
                if (at.To.Kind == SubjectKind.ChooseOne && at.To.Filter is { } hostFilter)
                {
                    // "Attach it to a creature you control": chosen as this happens.
                    var hosts = State.Battlefield.Select(State.GetCard).Where(c => Matches(hostFilter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (hosts.Count == 0) break;
                    var chosenHost = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose what to attach it to", ctx.Source.Id,
                        hosts.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(chosenHost.Count == 1 && hosts.Any(c => c.Id == chosenHost[0]), "Choose one of the listed permanents.");
                    host = State.GetCard(chosenHost[0]);
                }
                if (host is null) break;
                IEnumerable<Card> attaching = CardsFor(at.What, ctx);
                if (at.What.Kind == SubjectKind.ChooseOne && at.What.Filter is { } choose)
                {
                    var options = State.Battlefield.Select(State.GetCard)
                        .Where(c => c.Id != host.Id && Matches(choose, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0) break;
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"Choose what to attach to {host.Name} (or none)", ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(),
                        0, 1, CardChoicePurpose.ToBattlefield));
                    attaching = options.Where(c => pick.Contains(c.Id));
                }
                foreach (var card in attaching.Where(c => c.Zone == Zone.Battlefield && c.Id != host.Id).ToList())
                {
                    // Equipment attaches only to creatures; an Aura only to what it can enchant (rule 701.3b).
                    if (card.HasSubtype("Equipment") && !host.IsCreature) continue;
                    if (card.Definition.EnchantTarget is { } enchant && !CanEnchant(card, enchant, host)) continue;
                    if (card.AttachedTo == host.Id) continue;
                    card.AttachedTo = host.Id;
                    card.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                    ctx.Results.Attached++;
                    ctx.Results.AttachedCards.Add(card.Id);
                }
                break;
            }
            case TakeMilled tm:
            {
                var who = ctx.Controller;
                var eligible = ctx.Results.Milled.Select(State.GetCard)
                    .Where(c => c.Zone == Zone.Graveyard && (tm.Filter is null || Matches(tm.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, who))).ToList();
                if (eligible.Count == 0) break;
                IReadOnlyList<CardId> taken = eligible.Select(c => c.Id).ToList();
                if (tm.Count >= 0)
                {
                    int max = Math.Min(tm.Count, eligible.Count);
                    taken = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(tm.ToTop ? $"Put up to {max} on top of your library" : $"Put up to {max} into your hand", ctx.Source.Id,
                        eligible.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, max, CardChoicePurpose.ToHand));
                    Require(taken.Count <= max && taken.Distinct().Count() == taken.Count && taken.All(id => eligible.Any(c => c.Id == id)), "Choose among the milled cards.");
                }
                foreach (var id in taken)
                {
                    if (tm.ToTop) { await MoveCardAsync(id, Zone.Library); continue; } // the top: where a card put into a library goes
                    await MoveCardAsync(id, Zone.Hand);
                    if (State.GetCard(id).Zone == Zone.Hand) ctx.Results.Found.Add(id);
                }
                break;
            }
            case RemoveAllCounters rac:
                foreach (var card in CardsFor(rac.What, ctx).ToList())
                    foreach (var (kind, n) in card.Counters.Where(kv => kv.Value > 0).ToList())
                    {
                        int before = card.CounterCount(kind);
                        RemoveCountersFrom(card, kind, n);
                        ctx.Results.CountersRemoved += before - card.CounterCount(kind); // "draw a card for each counter removed this way"
                    }
                foreach (var player in PlayersFor(rac.What, ctx).Where(_ => IsPlayerSubject(rac.What)))
                {
                    var p = State.GetPlayer(player);
                    p.Poison = 0;
                    p.Experience = 0;
                }
                break;
            case RevealTopPutOntoBattlefield rtp:
            {
                BeginEnteringTogether();
                foreach (var player in PlayersFor(rtp.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(player).Library;
                    if (library.Count == 0) continue;
                    var top = library[0];
                    Emit(new CardsRevealed(player, new[] { top }));
                    if (Matches(rtp.Filter with { Controller = ControllerFilter.Any }, State.GetCard(top), player, ctx.Source, ctx.Controller))
                        await MoveCardAsync(top, Zone.Battlefield, controller: player);
                }
                EndEnteringTogether();
                break;
            }
            case ExileLibraryAllButBottom elb:
                foreach (var player in PlayersFor(elb.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(player).Library;
                    foreach (var id in library.Take(Math.Max(0, library.Count - elb.Keep)).ToList()) await MoveCardAsync(id, Zone.Exile, faceDown: elb.FaceDown);
                }
                break;
            case ExileUncastEntering eu:
                State.ExileUncastEntering.Add((eu.Filter, ctx.Controller, State.TurnNumber));
                break;
            case PreventCounters pc:
                // The ban holds while this permanent stays on the battlefield (rule 611.2b: it applies to what it was about as it began).
                foreach (var card in CardsFor(pc.What, ctx).ToList())
                    State.CounterBans.Add((card.Id, card.Version, null, ctx.Source.Id, ctx.Source.Version));
                foreach (var player in PlayersFor(pc.What, ctx).Where(_ => IsPlayerSubject(pc.What)))
                    State.CounterBans.Add((null, 0, player, ctx.Source.Id, ctx.Source.Version));
                break;
            case Blink bl:
            {
                var blinked = new List<Card>();
                foreach (var card in CardsFor(bl.What, ctx).Where(c => c.Zone == Zone.Battlefield && (bl.What.Kind != SubjectKind.Self || IsSourceObject(ctx))).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    if (card.Zone == Zone.Exile && !card.Definition.IsToken) blinked.Add(card);
                }
                BeginEnteringTogether();
                foreach (var card in blinked)
                {
                    if (bl.Transformed && !card.IsDoubleFaced) continue;
                    await MoveCardAsync(card.Id, Zone.Battlefield, controller: bl.UnderYourControl ? ctx.Controller : card.Owner, tapped: bl.Tapped, transformed: bl.Transformed,
                        withCounters: bl.EnterCounters > 0 ? new[] { (CounterKind.PlusOnePlusOne, bl.EnterCounters) } : null);
                }
                EndEnteringTogether();
                break;
            }
            case ShuffleIntoLibrary sil:
                foreach (var card in (sil.What.Kind == SubjectKind.Self ? (ctx.Source.Zone != Zone.Library && IsSourceObject(ctx) ? new[] { ctx.Source } : Array.Empty<Card>()) : CardsFor(sil.What, ctx)).ToList())
                {
                    await MoveCardAsync(card.Id, Zone.Library);
                    Shuffle(State.GetPlayer(card.Owner));
                }
                break;
            case AdditionalLandThisTurn:
                State.GetPlayer(ctx.Controller).ExtraLandsThisTurn++;
                break;
            case WarpWorld:
            {
                var players = State.ApnapOrder().ToList();
                var owned = players.ToDictionary(p => p, p => State.Battlefield.Where(b => State.GetCard(b).Owner == p).ToList());
                // Each player shuffles all permanents they own into their library (at the same time; tokens cease to exist).
                var leaving = owned.Values.SelectMany(x => x).ToList();
                await PlanMovesAsync(leaving, Zone.Library);
                BeginSimultaneous();
                foreach (var id in leaving)
                    if (State.GetCard(id).Zone == Zone.Battlefield) await MoveCardAsync(id, Zone.Library);
                EndSimultaneous();
                foreach (var p in players) Shuffle(State.GetPlayer(p));
                // ... then reveals that many cards from the top of their library.
                var revealed = players.ToDictionary(p => p, p => State.GetPlayer(p).Library.Take(owned[p].Count).ToList());
                foreach (var p in players.Where(p => revealed[p].Count > 0)) Emit(new CardsRevealed(p, revealed[p]));
                // Artifact, creature and land cards onto the battlefield, then (another event) enchantment cards.
                foreach (var batch in new[] { CardType.Artifact | CardType.Creature | CardType.Land, CardType.Enchantment })
                {
                    BeginEnteringTogether();
                    foreach (var p in players)
                        foreach (var id in revealed[p].Where(id => State.GetCard(id) is { Zone: Zone.Library } c && c.Is(batch)).ToList())
                            await MoveCardAsync(id, Zone.Battlefield, controller: p);
                    EndEnteringTogether();
                }
                // The rest on the bottom of their library, in an order their owner chooses (rule 401.4).
                foreach (var p in players)
                {
                    var library = State.GetPlayer(p).Library;
                    var rest = revealed[p].Where(library.Contains).ToList();
                    var ordered = await OrderAsync(p, rest, "Choose the next card for the bottom of your library (first goes deepest)", ctx.Source);
                    foreach (var id in ordered) { library.Remove(id); library.Add(id); }
                    PlacedInUnseenOrder(ordered, p);
                }
                break;
            }
            case RebuildLibraryFromExile rb:
            {
                var player = State.GetPlayer(ctx.Controller);
                var faceUp = player.Exile.Select(State.GetCard).Where(c => !c.FaceDown && c.Owner == ctx.Controller).ToList();
                IReadOnlyList<CardId> chosen = Array.Empty<CardId>();
                if (faceUp.Count > 0)
                {
                    chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: choose up to {rb.Count} face-up exiled cards you own to put on top of your library", ctx.Source.Id,
                        faceUp.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList(), 0, Math.Min(rb.Count, faceUp.Count), CardChoicePurpose.ToHand));
                    Require(chosen.Count <= rb.Count && chosen.Distinct().Count() == chosen.Count && chosen.All(id => faceUp.Any(c => c.Id == id)),
                        $"Choose up to {rb.Count} of your face-up exiled cards.");
                }
                var picked = chosen.Select(id => (Id: id, State.GetCard(id).Version)).ToList();
                // Exile all the cards from your library ...
                BeginSimultaneous();
                foreach (var id in player.Library.ToList()) await MoveCardAsync(id, Zone.Exile);
                EndSimultaneous();
                // ... then put the chosen cards on top of your library, in the order you choose.
                var stillExiled = picked.Where(x => State.GetCard(x.Id) is { Zone: Zone.Exile } c && c.Version == x.Version).Select(x => x.Id).ToList();
                var ordered = await OrderAsync(ctx.Controller, stillExiled, "Choose the card to put on top of your library next", ctx.Source);
                foreach (var id in ordered.Reverse()) await MoveCardAsync(id, Zone.Library);
                PlacedInUnseenOrder(ordered, ctx.Controller);
                break;
            }
            case ExileGraveyardAndNamesakes en:
                foreach (var p in PlayersFor(en.Who, ctx).ToList())
                {
                    var player = State.GetPlayer(p);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var exiling = player.Graveyard.Select(State.GetCard)
                        .Where(c => en.Filter is not { } f || Matches(f with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)).ToList();
                    BeginSimultaneous();
                    foreach (var card in exiling)
                    {
                        await MoveCardAsync(card.Id, Zone.Exile);
                        if (card.Zone != Zone.Exile) continue;
                        ctx.Results.Exiled.Add(card.Id);
                        names.Add(card.Name);
                    }
                    EndSimultaneous();
                    // Searching a hidden zone for cards with a stated quality: the searcher may find fewer than all (rule 701.19b).
                    Look(ctx.Controller, player.Library);
                    var namesakes = player.Library.Where(id => names.Contains(State.GetCard(id).Name)).ToList();
                    if (namesakes.Count > 0)
                    {
                        var found = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                            $"{ctx.Source.Name}: exile the cards from {player.Name}'s library with those names (you may leave some)", ctx.Source.Id,
                            namesakes.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, namesakes.Count, CardChoicePurpose.Keep));
                        Require(found.Distinct().Count() == found.Count && found.All(namesakes.Contains), "Choose among the cards with those names.");
                        BeginSimultaneous();
                        foreach (var id in found)
                        {
                            await MoveCardAsync(id, Zone.Exile);
                            if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
                        }
                        EndSimultaneous();
                    }
                    Shuffle(player);
                }
                break;
            case SearchThenNameCard sn:
                foreach (var p in PlayersFor(sn.Whose, ctx).ToList())
                {
                    var player = State.GetPlayer(p);
                    // Search that player's library for a card: with no stated quality, one is found if there is any (rule 701.19b).
                    Look(ctx.Controller, player.Library);
                    CardId? found = null;
                    if (player.Library.Count > 0)
                    {
                        var options = player.Library.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                        var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                            $"{ctx.Source.Name}: search {player.Name}'s library for a card", ctx.Source.Id, options, 1, 1, CardChoicePurpose.ToBattlefield));
                        Require(pick.Count == 1 && player.Library.Contains(pick[0]), "Choose one card from the library.");
                        found = pick[0];
                    }
                    // Then that player chooses a card name, without knowing which card was found.
                    var named = await ChooseCardNameAsync(p, ctx.Source.Id, $"{ctx.Source.Name}: choose a card name");
                    if (named is not null) Emit(new ChoiceMade(ctx.Source.Id, $"{player.Name} names {named}"));
                    if (found is { } card && State.GetCard(card) is { Zone: Zone.Library } foundCard
                        && Matches(sn.Filter with { Controller = ControllerFilter.Any }, foundCard, p, ctx.Source, ctx.Controller)
                        && !string.Equals(foundCard.Name, named, StringComparison.OrdinalIgnoreCase)
                        && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Put {foundCard.Name} onto the battlefield under your control?", card)))
                    {
                        BeginEnteringTogether();
                        await MoveCardAsync(card, Zone.Battlefield, controller: ctx.Controller);
                        EndEnteringTogether();
                    }
                    Shuffle(player);
                }
                break;
            case PlayTopFreeOrExile:
            {
                var player = State.GetPlayer(ctx.Controller);
                if (player.Library.Count == 0) break;
                var top = State.GetCard(player.Library[0]);
                Emit(new CardsRevealed(ctx.Controller, new[] { top.Id }));
                bool played = false;
                if (top.Is(CardType.Land))
                {
                    // Playing a land this way is the player's land play: only during their turn, with one left (rules 305.2, 305.3).
                    if (State.ActivePlayer == ctx.Controller && player.LandsPlayedThisTurn < LandsAllowed(ctx.Controller)
                        && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Play {top.Name}?", top.Id)))
                    {
                        player.LandsPlayedThisTurn++;
                        await MoveCardAsync(top.Id, Zone.Battlefield, controller: ctx.Controller);
                        Emit(new LandPlayed(ctx.Controller, top.Id));
                        played = true;
                    }
                }
                else if (HasLegalTargets(CastingTargets(top.Definition), ctx.Controller, top.Id) && !SpellsForbidden(ctx.Controller)
                         && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {top.Name} without paying its mana cost?", top.Id)))
                    played = await CastNowWithoutPayingAsync(player, top.Id);
                // "If you don't, exile it."
                if (!played && top.Zone == Zone.Library) await MoveCardAsync(top.Id, Zone.Exile);
                break;
            }
            case ShuffleLibrary sh:
                foreach (var player in PlayersFor(sh.Who, ctx).ToList()) Shuffle(State.GetPlayer(player));
                break;
            case OpponentsCantCastSpellsThisTurn oc:
                foreach (var opponent in State.OpponentsOf(ctx.Controller).ToList())
                    (oc.NoncreatureOnly ? State.NoncreatureSpellsForbiddenFor : State.SpellsForbiddenFor).Add((opponent, State.TurnNumber));
                break;
            case YouCantCastSpellsThisTurn:
                State.SpellsForbiddenFor.Add((ctx.Controller, State.TurnNumber));
                break;
            case NoSpellsThisTurn:
                State.SpellsForbiddenTurn = State.TurnNumber;
                break;
            case ExchangeControl ex:
            {
                var first = CardsFor(ex.First, ctx).FirstOrDefault();
                var second = CardsFor(ex.Second, ctx).FirstOrDefault();
                // Both must still be there, or nothing happens (rule 701.12b).
                if (first is null || second is null || first.Controller == second.Controller) break;
                var (a, b) = (first.Controller, second.Controller);
                long stamp = NewTimestamp();
                State.ControlEffects.Add(new ControlEffect(first.Id, first.Version, b, stamp) { MadeOnTurn = State.TurnNumber });
                State.ControlEffects.Add(new ControlEffect(second.Id, second.Version, a, stamp) { MadeOnTurn = State.TurnNumber });
                RecomputeContinuousEffects();
                break;
            }
            case AtNextUpkeep nu:
                (nu.EndStep ? State.AtNextEndStepTriggers : State.AtNextUpkeep).Add(new DelayedTrigger(ctx.Source.Id, nu.Ability, ctx.Controller, nu.Amount is { } nuAmount ? Eval(nuAmount, ctx) : 0)
                {
                    OnlyAtUpkeepOf = nu.Yours ? ctx.Controller : null, SourceVersion = ExpectedSourceVersion(ctx),
                    About = nu.Player is { } delayedAbout ? PlayersFor(delayedAbout, ctx).Cast<PlayerId?>().FirstOrDefault() : null,
                    SourceTransforms = ctx.Source.TransformCount,
                });
                break;
            case ChooseCreatureType:
            {
                var types = CreatureTypeOptions(ctx.Controller);
                int i = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller), new OptionRequest($"{ctx.Source.Name}: choose a creature type", ctx.Source.Id, types, OptionKind.CreatureType));
                Require(i >= 0 && i < types.Count, "Choose one of the creature types.");
                ctx.Source.ChosenType = types[i];
                Emit(new ChoiceMade(ctx.Source.Id, types[i]));
                break;
            }
            case RevealTopPutRandom rr:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(rr.Count).ToList();
                if (top.Count == 0) break;
                Emit(new CardsRevealed(ctx.Controller, top));
                var hits = top.Where(id => Matches(rr.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)).ToList();
                CardId? picked = hits.Count > 0 ? hits[Rng.Next(hits.Count)] : null;
                if (picked is { } chosen) await MoveCardAsync(chosen, rr.To, controller: ctx.Controller);
                var rest = top.Where(id => id != picked).ToList();
                Rng.Shuffle(rest);
                foreach (var id in rest) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(rest, null);
                break;
            }
            case SearchHandOrLibrary shl:
            {
                var who = ctx.Controller;
                var player = State.GetPlayer(who);
                var filter = shl.Filter with { Controller = ControllerFilter.Any };
                var options = (shl.AlsoGraveyard ? player.Graveyard.Concat(player.Hand) : player.Hand).Concat(player.Library).Select(State.GetCard).Where(c => Matches(filter, c, who, ctx.Source, who))
                    .Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList();
                if (options.Count > 0)
                {
                    var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(shl.AlsoGraveyard ? "Search your graveyard, hand and library: choose a card" : "Search your hand and library: choose a card", ctx.Source.Id,
                        options, 0, 1, shl.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand));
                    Require(pick.Count <= 1 && pick.All(id => options.Any(o => o.Id == id)), "Choose one of the matching cards.");
                    // "If you search your library this way, shuffle": not when the card comes from the hand.
                    bool fromHand = pick.Count == 1 && State.GetCard(pick[0]).Zone is Zone.Hand or Zone.Graveyard;
                    foreach (var id in pick) await MoveCardAsync(id, shl.To, controller: who);
                    if (fromHand) break;
                }
                Shuffle(player);
                break;
            }
            case AddManaInAnyCombination amc:
                for (int n = 0, total = Eval(amc.Count, ctx); n < total; n++)
                {
                    var allowed = amc.Types ?? new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green };
                    var names = allowed.Select(t => ColorNames[Array.IndexOf(new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }, t)]).ToList();
                    int i = allowed.Count == 1 ? 0 : await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"Choose the color of mana {n + 1} of {total}", ctx.Source.Id, names, OptionKind.Color));
                    Require(i >= 0 && i < allowed.Count, "Choose a color.");
                    var type = allowed[i];
                    var pool = State.GetPlayer(ctx.Controller).ManaPool;
                    if (amc.OnlyFor is { } only) pool.AddSpecial(new ManaUnit(type, ctx.Source.Id, only, amc.AbilitiesToo, ManaRider.None));
                    else pool.Add(type);
                    Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                }
                break;
            case Behold bh:
            {
                var who = ctx.Controller;
                var filter = bh.Filter with { Controller = ControllerFilter.Any };
                var options = State.PermanentsControlledBy(who).Where(c => Matches(filter, c, who, ctx.Source, who))
                    .Concat(State.GetPlayer(who).Hand.Select(State.GetCard).Where(c => Matches(filter, c, who, ctx.Source, who))).ToList();
                if (options.Count == 0) break;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Behold: choose one you control or reveal one from your hand (or none)", ctx.Source.Id,
                    options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.Keep));
                if (pick.Count != 1 || options.All(c => c.Id != pick[0])) break;
                if (State.GetCard(pick[0]).Zone == Zone.Hand) Emit(new CardsRevealed(who, pick.ToList()));
                await ApplyAllAsync(bh.Effects, ctx);
                break;
            }
            case CastFromGraveyardNow cg:
            {
                var who = ctx.Controller;
                var player = State.GetPlayer(who);
                int? maxValue = cg.MaxManaValue is { } mmv ? Eval(mmv, ctx) : null;
                IEnumerable<Card> pool = cg.Card is { } targeted ? CardsFor(targeted, ctx).Concat(ctx.ChosenAt(targeted.Index)?.Card is { } tc ? new[] { State.GetCard(tc) } : Array.Empty<Card>()).Distinct()
                    : cg.FromMilled ? ctx.Results.Milled.Select(State.GetCard)
                    : cg.FromExile ? player.Exile.Select(State.GetCard)
                    : (cg.Of is { } of ? PlayersFor(of, ctx) : new[] { who }).SelectMany(p => State.GetPlayer(p).Graveyard).Select(State.GetCard);
                var fromZone = cg.FromExile ? Zone.Exile : Zone.Graveyard;
                var permissions = cg.FromExile ? State.PlayableFromExile : State.PlayableFromGraveyard;
                var castable = pool
                    .Where(c => c.Zone == fromZone && !c.Is(CardType.Land) && Matches(cg.Filter with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, who)
                                && (maxValue is null || c.ManaValue <= maxValue)
                                && CastableWith(c))
                    .ToList();
                // Whether it could be cast with the permission this effect gives (tried, then taken back).
                bool CastableWith(Card c)
                {
                    var trial = new PlayableFromExile(c.Id, c.Version, who, State.TurnNumber) { AnyManaType = cg.AnyMana };
                    permissions.Add(trial);
                    bool free = cg.Free && _castFree.Add(c.Id);
                    try { return CanBeCast(c, who, flashExtra: false); }
                    finally
                    {
                        permissions.Remove(trial);
                        if (free) _castFree.Remove(c.Id);
                    }
                }
                if (castable.Count == 0) break;
                // "For each player, you may cast a card that player milled this way": one choice per owner, in turn order.
                foreach (var group in cg.PerPlayer ? State.ApnapOrder().Select(o => castable.Where(c => c.Owner == o).ToList()).Where(g => g.Count > 0).ToList() : new List<List<Card>> { castable })
                {
                var offered = group.Where(c => c.Zone == fromZone).ToList();
                if (offered.Count == 0) continue;
                var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(cg.Free ? "You may cast a spell without paying its mana cost" : cg.FromExile ? "You may cast a spell from exile" : "You may cast a spell from your graveyard", ctx.Source.Id,
                    offered.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                if (pick.Count != 1 || offered.All(c => c.Id != pick[0])) continue;
                var card = State.GetCard(pick[0]);
                var permission = new PlayableFromExile(card.Id, card.Version, who, State.TurnNumber) { AnyManaType = cg.AnyMana };
                permissions.Add(permission);
                if (cg.Free) _castFree.Add(card.Id);
                try
                {
                    // "If that spell would be put into a graveyard, exile it instead."
                    bool castNow = await CastSpellAsync(player, card.Id);
                    if (castNow) ctx.Results.CastThisWay = true;
                    if (castNow && !cg.NoExile && !cg.FromExile) State.ExileInsteadOfGraveyard.Add((card.Id, card.Version));
                }
                finally
                {
                    // The permission is for this cast only: a cast that was backed out of leaves the card where it was.
                    _castFree.Remove(card.Id);
                    permissions.Remove(permission);
                }
                }
                break;
            }
            default:
                return false;
        }
        return true;
    }
}
