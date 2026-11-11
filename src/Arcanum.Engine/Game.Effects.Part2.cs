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
    /// <summary>Effects handled by the group-2 part of the effect switch; false when the effect belongs to another group (the switch order is kept).</summary>
    private async Task<bool> ApplyGroup2Async(Effect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case CounterUnlessPays cu:
            {
                var item = State.Stack.FirstOrDefault(x => x.Id == cu.StackObject);
                if (item is null) break;
                if (cu.LifeFromPowerOf is { } lifeFrom) cu = cu with { Life = Math.Max(0, PowerOrLastKnown(State.GetCard(lifeFrom))) };
                var payer = State.GetPlayer(item.Controller);
                bool canPay = payer.Life >= cu.Life && (cu.Mana.ManaValue == 0 || Payable(item.Controller, cu.Mana, null)) && CanPayExtra(item.Controller, cu.Extra, item.SourceCard);
                bool paid = false;
                if (canPay && await ControllerOf(item.Controller).ChooseYesNoAsync(ViewFor(item.Controller),
                        new YesNoRequest($"{(cu.Extra is { } wardCost ? DescribeCost(wardCost) : "Pay")} {(cu.Mana.ManaValue > 0 ? cu.Mana.ToString() : "")}{(cu.Mana.ManaValue > 0 && cu.Life > 0 ? " and " : "")}{(cu.Life > 0 ? $"{cu.Life} life" : "")} so {State.GetCard(item.SourceCard).Name} isn't countered?", ctx.Source.Id)))
                {
                    paid = cu.Mana.ManaValue == 0 || await PayManaAsync(payer, item.SourceCard, cu.Mana, null);
                    if (paid && cu.Life > 0) ChangeLife(item.Controller, -cu.Life);
                    if (paid && cu.Extra is { } extraCost) await PayExtraAsync(item.Controller, extraCost, item.SourceCard);
                }
                if (paid)
                {
                    if (cu.IfPaid is { } ifPaid) await ApplyAllAsync(ifPaid, ctx);
                    break;
                }
                if (item is SpellOnStack sp)
                {
                    if (CanBeCountered(State.GetCard(sp.Card)))
                    {
                        // "If that spell is countered this way, exile it instead of putting it into its owner's graveyard."
                        if (cu.ExileIfCountered) State.ExileInsteadOfGraveyard.Add((sp.Card, State.GetCard(sp.Card).Version));
                        await CounterSpellOnStackAsync(sp.Card);
                    }
                }
                else
                {
                    State.Stack.Remove(item);
                    Emit(new AbilityCountered(item.SourceCard));
                }
                break;
            }
            case ChangeTarget { ToSource: true } toSource:
                await ChangeTargetToSourceAsync(toSource, ctx);
                break;
            case ChangeTarget ct:
            {
                var target = ct.What.Kind == SubjectKind.Target ? ctx.TargetAt(ct.What.Index) : null;
                var item = target is { StackObject: { } so } ? State.Stack.FirstOrDefault(s => s.Id == so)
                    : target is { Card: { } targetedSpell } ? State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == targetedSpell) : null;
                if (item is null || item.Targets.Count != 1) break;
                var ability = item switch
                {
                    SpellOnStack sp => sp.Ability ?? CastingTargets(State.GetCard(sp.Card).Definition),
                    AbilityOnStack ab => ab.Ability,
                    _ => null,
                };
                if (ability is null) break;
                var current = item.Targets[0].Target;
                var saved = _triggeredPlayer;
                _triggeredPlayer = (item as AbilityOnStack)?.Trigger?.Player;
                var options = LegalTargets(SpecAt(ability, 0), item.Controller, item.SourceCard).Where(t => t != current && !t.IsNone).ToList();
                _triggeredPlayer = saved;
                if (options.Count == 0) break;
                var request = new TargetRequest(ctx.Source.Id, $"Choose the new target for {State.GetCard(item.SourceCard).Name}", new[] { SpecAt(ability, 0) },
                    new[] { (IReadOnlyList<Target>)options }, CanCancel: true);
                var pick = await ControllerOf(ctx.Controller).ChooseTargetsAsync(ViewFor(ctx.Controller), request);
                if (pick is not { Count: 1 } || !options.Contains(pick[0])) break;
                int index = State.Stack.IndexOf(item);
                State.Stack[index] = item with { Targets = new[] { new ChosenTarget(pick[0], VersionOf(pick[0])) } };
                if (State.Stack[index] is SpellOnStack retargeted) _spellsOnStack[(retargeted.Card, State.GetCard(retargeted.Card).Version)] = retargeted;
                if (pick[0].Card is { } newTarget) QueueBecameTarget(State.Stack[index], new[] { newTarget });
                break;
            }
            case DestroySameName dn:
                foreach (var target in CardsFor(dn.What, ctx).ToList())
                {
                    var name = target.Name;
                    foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Name == name && !c.Has(Keyword.Indestructible)).ToList())
                    {
                        if (await SurvivesDestructionAsync(card)) continue;
                        bool creature = card.IsCreature;
                        await MoveCardAsync(card.Id, Zone.Graveyard);
                        ctx.Results.Destroyed++;
                        if (card.Zone == Zone.Graveyard) ctx.Results.DestroyedCards.Add(card.Id);
                        Emit(new PermanentDestroyed(card.Id));
                        if (creature) Emit(new CreatureDied(card.Id));
                    }
                }
                break;
            case DistributeCounters dc:
            {
                // Distributed as it was cast or put on the stack (rule 601.2d); a target that became illegal gets nothing.
                var division = await DivisionAtResolutionAsync(ctx, dc.TotalFrom is { } countersTotal ? Math.Max(0, Eval(countersTotal, ctx)) : dc.Total, "Counters");
                BeginSimultaneous();
                foreach (var (index, n) in division)
                    if (ctx.TargetAt(index)?.Card is { } c && n > 0) PutCounters(State.GetCard(c), CounterKind.PlusOnePlusOne, n, ctx.Controller);
                EndSimultaneous();
                break;
            }
            case RevealUntil ru:
            {
                var owners = ru.From is { } revealFrom ? PlayersFor(revealFrom, ctx).ToList() : new List<PlayerId> { ctx.Controller };
                if (owners.Count == 0) break;
                var player = State.GetPlayer(owners[0]);
                if (ru.CastFree)
                {
                    // "Exiles cards until an instant or sorcery card; you may cast it without paying its mana cost; the rest go to the bottom."
                    var exiled = new List<CardId>();
                    CardId? revealedHit = null;
                    // "Reveal cards until you reveal a nonland card. Exile that card and put the rest on the bottom": only that card is exiled.
                    var missed = new List<CardId>();
                    if (ru.HitStaysExiled)
                    {
                        foreach (var id in player.Library.ToList())
                        {
                            if (Matches(ru.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), player.Id, ctx.Source, ctx.Controller)) { revealedHit = id; break; }
                            missed.Add(id);
                        }
                        Emit(new CardsRevealed(player.Id, revealedHit is { } shown ? missed.Append(shown).ToList() : missed));
                        if (revealedHit is { } hitCard) await MoveCardAsync(hitCard, Zone.Exile);
                        foreach (var id in missed) player.Library.Remove(id);
                        Rng.Shuffle(missed);
                        player.Library.AddRange(missed);
                        PlacedInUnseenOrder(missed, null);
                    }
                    while (!ru.HitStaysExiled && player.Library.Count > 0)
                    {
                        var top = player.Library[0];
                        await MoveCardAsync(top, Zone.Exile);
                        exiled.Add(top);
                        if (Matches(ru.Filter with { Controller = ControllerFilter.Any }, State.GetCard(top), player.Id, ctx.Source, ctx.Controller)) { revealedHit = top; break; }
                    }
                    if (revealedHit is { } free && HasLegalTargets(CastingTargets(State.GetCard(free).Definition), ctx.Controller, free)
                        && (!ru.CastByDiscarding || State.GetPlayer(ctx.Controller).Hand.Count > 0)
                        && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(ru.CastByDiscarding
                            ? $"Cast {State.GetCard(free).Name} by discarding a card rather than paying its mana cost?" : $"Cast {State.GetCard(free).Name} without paying its mana cost?", free)))
                    {
                        if (ru.CastByDiscarding) _castFreeExtra[free] = new ExtraCost(Discard: 1);
                        try { await CastNowWithoutPayingAsync(State.GetPlayer(ctx.Controller), free, hasteAndSacrifice: ru.CreatureHasteAndSacrifice && State.GetCard(free).IsCreature); }
                        finally { _castFreeExtra.Remove(free); }
                    }
                    if (ru.RestStaysExiled) break; // "the rest stay exiled"
                    var leftover = exiled.Where(id => State.GetCard(id).Zone == Zone.Exile && !(ru.HitStaysExiled && id == revealedHit)).ToList();
                    Rng.Shuffle(leftover);
                    foreach (var id in leftover) await MoveCardAsync(id, Zone.Library, toBottom: true);
                    PlacedInUnseenOrder(leftover, null);
                    break;
                }
                var revealed = new List<CardId>();
                var hits = new List<CardId>();
                int wanted = ru.CountFrom is { } untilCount ? Eval(untilCount, ctx) : 1;
                var sharingWith = ru.SharesCreatureTypeWith is { } shareSubject ? CardsFor(shareSubject, ctx).ToList() : null;
                if (wanted > 0)
                    foreach (var id in player.Library.ToList())
                    {
                        if (Matches(ru.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), ctx.Controller, ctx.Source, ctx.Controller)
                            && (sharingWith is null || sharingWith.Any(other => SharesCreatureType(State.GetCard(id), other))))
                        {
                            hits.Add(id);
                            if (hits.Count == wanted) break;
                        }
                        else revealed.Add(id);
                    }
                Emit(new CardsRevealed(player.Id, revealed.Concat(hits).ToList()));
                if (ru.AnyNumber && hits.Count > 0)
                {
                    // "Put any number of those cards onto the battlefield": the others join the rest of the revealed cards.
                    var keep = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose any number of them to put onto the battlefield", ctx.Source.Id,
                        hits.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList(), 0, hits.Count, CardChoicePurpose.ToBattlefield));
                    Require(keep.All(hits.Contains) && keep.Distinct().Count() == keep.Count, "Choose among the revealed cards.");
                    revealed.AddRange(hits.Where(id => !keep.Contains(id)));
                    hits = hits.Where(keep.Contains).ToList();
                }
                ctx.Results.Found.AddRange(hits); // "when you reveal a creature card this way", "that card's mana value"
                if (hits.Count > 0) BeginEnteringTogether();
                foreach (var hit in hits)
                {
                    var to = ru.BattlefieldIf is { } toBattlefield
                             && Matches(toBattlefield with { Controller = ControllerFilter.Any }, State.GetCard(hit), ctx.Controller, ctx.Source, ctx.Controller)
                        ? Zone.Battlefield : ru.To;
                    if (ru.MayBattlefield && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                            new YesNoRequest($"Put {State.GetCard(hit).Name} onto the battlefield? (Otherwise it goes into your hand.)", ctx.Source.Id)))
                        to = Zone.Battlefield;
                    var host = ru.AttachTo is { } attachTo ? CardsFor(attachTo, ctx).FirstOrDefault(c => c.Zone == Zone.Battlefield && c.IsCreature) : null;
                    await MoveCardAsync(hit, to, controller: ru.RevealerPuts ? player.Id : ctx.Controller, attachTo: to == Zone.Battlefield && host is not null && State.GetCard(hit).HasSubtype("Equipment") ? host.Id : null, tapped: ru.Tapped);
                }
                if (hits.Count > 0) EndEnteringTogether();
                if (ru.RestToGraveyard)
                {
                    foreach (var id in revealed) await MoveCardAsync(id, Zone.Graveyard);
                    break;
                }
                if (ru.RestShuffled)
                {
                    Shuffle(player);
                    break;
                }
                if (ru.RestBottomAnyOrder)
                {
                    // "The rest on the bottom of their library in any order": the revealing player orders them, each next one under the last.
                    var ordered = await OrderAsync(player.Id, revealed, "Put on the bottom of your library next", ctx.Source);
                    foreach (var id in ordered) { player.Library.Remove(id); player.Library.Add(id); }
                    PlacedInUnseenOrder(ordered, player.Id);
                    break;
                }
                Rng.Shuffle(revealed);
                foreach (var id in revealed) { player.Library.Remove(id); player.Library.Add(id); }
                PlacedInUnseenOrder(revealed, null);
                break;
            }
            case Unless un:
                foreach (var player in PlayersFor(un.Who, ctx).ToList())
                {
                    var payable = un.Options.Where(o => CanPayExtra(player, o, ctx.Source.Id)).ToList();
                    int pick = payable.Count;
                    if (payable.Count > 0)
                    {
                        var labels = payable.Select(DescribeCost).Append("Neither").ToList();
                        pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player), new OptionRequest($"{ctx.Source.Name}: pay a cost, or suffer the effect", ctx.Source.Id, labels, OptionKind.Other));
                        Require(pick >= 0 && pick <= payable.Count, "Choose one of the options.");
                    }
                    if (pick < payable.Count) await PayExtraAsync(player, payable[pick], ctx.Source.Id, causedBy: ctx.Controller);
                    else await ApplyAllAsync(un.Otherwise, ctx with { Trigger = (ctx.Trigger ?? new TriggerInfo()) with { Player = player } });
                }
                break;
            case OpponentMaySacrifice os:
                foreach (var player in State.ApnapOrder().Where(p => p != ctx.Controller).ToList())
                {
                    var candidates = SacrificeCandidates(player, os.Filter, ctx.Source.Id);
                    if (candidates.Count == 0) continue;
                    if (!await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"Sacrifice a creature to {ctx.Source.Name}?", ctx.Source.Id))) continue;
                    var gone = await SacrificeAsync(player, 1, os.Filter, ctx.Source);
                    if (gone.Count == 0) continue;
                    await ApplyAllAsync(os.Effects, ctx);
                    break;
                }
                break;
            case Piles { OpponentSeparates: true } pl:
            {
                // "Choose an opponent. They look at the top four cards of your library and separate them into a face-down pile and
                // a face-up pile. Put one pile into your hand and the other into your graveyard."
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(pl.Count).ToList();
                if (top.Count == 0) break;
                if (pl.Revealed) Emit(new CardsRevealed(ctx.Controller, top));
                var separator = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who separates the piles") ?? State.OpponentsOf(ctx.Controller).First();
                Look(separator, top);
                var options = top.Select(id => ViewBuilder.Card(State, id, separator, reveal: true)).ToList();
                var faceUp = await ControllerOf(separator).ChooseCardsAsync(ViewFor(separator),
                    new CardChoiceRequest(pl.Revealed ? $"Separate {player.Name}'s revealed cards into two piles: choose the first pile" : $"Choose the cards for {player.Name}'s face-up pile (the rest go face down)",
                        ctx.Source.Id, options, 0, top.Count, CardChoicePurpose.ToHand));
                Require(faceUp.All(top.Contains) && faceUp.Distinct().Count() == faceUp.Count, "Choose among the top cards.");
                RevealToAll(faceUp); // the face-up pile is seen by everyone
                var faceDown = top.Where(id => !faceUp.Contains(id)).ToList();
                var labels = pl.Revealed
                    ? new[]
                    {
                        $"Pile 1 ({faceUp.Count}): {string.Join(", ", faceUp.Select(id => State.GetCard(id).Name))}",
                        $"Pile 2 ({faceDown.Count}): {string.Join(", ", faceDown.Select(id => State.GetCard(id).Name))}",
                    }
                    : new[]
                    {
                        $"Face-up pile ({faceUp.Count}): {string.Join(", ", faceUp.Select(id => State.GetCard(id).Name))}",
                        $"Face-down pile ({faceDown.Count} cards)",
                    };
                int chosen = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                    new OptionRequest("Choose the pile you put into your hand (the other goes to your graveyard)", ctx.Source.Id, labels, OptionKind.Other));
                var toHand = chosen == 0 ? faceUp : faceDown;
                foreach (var id in toHand) await MoveCardAsync(id, Zone.Hand);
                foreach (var id in top.Where(id => !toHand.Contains(id))) await MoveCardAsync(id, Zone.Graveyard);
                break;
            }
            case Piles pl:
            {
                var player = State.GetPlayer(ctx.Controller);
                var top = player.Library.Take(pl.Count).ToList();
                if (top.Count == 0) break;
                Look(ctx.Controller, top);
                var options = top.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).ToList();
                var faceUp = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest("Choose the cards for the face-up pile (the rest go face down)", ctx.Source.Id, options, 0, top.Count, CardChoicePurpose.ToHand));
                Require(faceUp.All(top.Contains) && faceUp.Distinct().Count() == faceUp.Count, "Choose among the top cards.");
                RevealToAll(faceUp); // the face-up pile is seen by everyone
                var faceDown = top.Where(id => !faceUp.Contains(id)).ToList();
                var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who picks the pile") ?? State.OpponentsOf(ctx.Controller).First();
                var labels = new[]
                {
                    $"Face-up pile ({faceUp.Count}): {string.Join(", ", faceUp.Select(id => State.GetCard(id).Name))}",
                    $"Face-down pile ({faceDown.Count} cards)",
                };
                int chosen = await ControllerOf(opponent).ChooseOptionAsync(ViewFor(opponent),
                    new OptionRequest($"Choose the pile {player.Name} puts into their hand", ctx.Source.Id, labels, OptionKind.Other));
                var toHand = chosen == 0 ? faceUp : faceDown;
                foreach (var id in toHand) await MoveCardAsync(id, Zone.Hand);
                foreach (var id in top.Where(id => !toHand.Contains(id))) await MoveCardAsync(id, Zone.Graveyard);
                break;
            }
            case WinGame:
                if (CantWin(ctx.Controller)) break;
                foreach (var opponent in State.OpponentsOf(ctx.Controller).ToList())
                    if (!CantLose(opponent)) Lose(opponent, $"{State.GetPlayer(ctx.Controller).Name} won the game");
                break;
            case UntapUpTo uu:
            {
                var tapped = State.PermanentsControlledBy(ctx.Controller).Where(c => c.Tapped && Matches(uu.Filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                if (uu.CountFrom is { } untapFrom) uu = uu with { Count = Math.Max(0, Eval(untapFrom, ctx)) };
                if (tapped.Count == 0 || uu.Count == 0) break;
                var options = tapped.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller)).ToList();
                var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                    new CardChoiceRequest($"Untap up to {uu.Count}", ctx.Source.Id, options, 0, Math.Min(uu.Count, tapped.Count), CardChoicePurpose.ToBattlefield));
                foreach (var id in chosen.Where(id => tapped.Any(c => c.Id == id)))
                {
                    Untap(State.GetCard(id));
                }
                break;
            }
            case AddPoison ap:
                foreach (var player in PlayersFor(ap.Who, ctx).Where(p => !CountersBannedOn(null, p)))
                {
                    int poison = ap.UpTo > 0 ? Math.Max(0, ap.UpTo - State.GetPlayer(player).Poison) : ap.Count << CountersYouPutDoublings(ctx.Controller);
                    if (poison == 0) continue;
                    State.GetPlayer(player).Poison += poison;
                    Emit(new PoisonGiven(player, poison));
                }
                break;
            case EndTheTurn:
                // Exile every spell on the stack and drop abilities; skip to the cleanup step (rule 724).
                foreach (var item in State.Stack.ToList())
                {
                    State.Stack.Remove(item);
                    if (item is SpellOnStack sp) await MoveCardAsync(sp.Card, Zone.Exile);
                }
                // The resolving spell is exiled too (rule 723.1b), instead of going to its owner's graveyard.
                if (ctx.Source.Zone == Zone.Stack) _exileResolvingSpell = ctx.Source.Id;
                State.Combat = null;
                _endTurnRequested = true;
                break;
            case AdditionalCombat { AfterThisMainWithMain: true }:
                if (State.Step.IsMain()) _extraCombatsAfterMain++;
                break;
            case AdditionalCombat ac:
                _extraCombats++;
                if (ac.UntapCreatures)
                    foreach (var card in State.PermanentsControlledBy(ctx.Controller).Where(c => c.IsCreature && c.Tapped).ToList())
                    {
                        Untap(card);
                    }
                break;
            case CopySpell cs:
            {
                if (cs.CounteredThisWay)
                {
                    // "You may copy the spell countered this way": as it last existed on the stack (rule 707.10).
                    foreach (var (counteredCard, counteredVersion) in ctx.Results.Countered.ToList())
                        if (_spellsOnStack.TryGetValue((counteredCard, counteredVersion), out var countered) && !State.GetCard(counteredCard).Definition.CantBeCopied)
                            for (int i = Eval(cs.Count, ctx); i > 0; i--) await CopySpellAsync(countered, ctx.Controller, cs.NotLegendary);
                    break;
                }
                var originals = cs.What.Kind == SubjectKind.Triggered ? (ctx.Trigger?.Subject is { } ts ? new[] { ts } : Array.Empty<CardId>())
                    : cs.What.Kind == SubjectKind.EachTarget ? CardsFor(cs.What, ctx).Select(c => c.Id).ToArray()
                    : CardsFor(cs.What, ctx).Take(1).Select(c => c.Id).ToArray();
                int copies = Eval(cs.Count, ctx);
                foreach (var oid in originals)
                {
                    // "Copy that spell" still copies a spell that has left the stack, as it last existed there (rule 707.10, 608.2h).
                    var original = State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == oid)
                                   ?? (cs.What.Kind == SubjectKind.Triggered && ctx.Trigger is { } copiedAbout
                                       && _spellsOnStack.TryGetValue((oid, copiedAbout.SubjectVersion), out var gone) ? gone : null);
                    if (original is null) continue;
                    if (State.GetCard(oid).Definition.CantBeCopied) continue; // "This spell can't be copied."
                    var copiers = cs.EachOtherPlayer ? State.ApnapOrder().Where(p => p != original.Controller).ToList() : new List<PlayerId> { ctx.Controller };
                    foreach (var copier in copiers)
                        for (int i = 0; i < copies; i++)
                        {
                            await CopySpellAsync(original, copier, cs.NotLegendary);
                            if (cs.SpellsGain is { } gained && State.Stack.OfType<SpellOnStack>().LastOrDefault() is { } made && made.Card != oid)
                                State.GetCard(made.Card).GrantedKeywords.UnionWith(gained);
                            if (cs.GainsHasteAndSacrifice && State.Stack.OfType<SpellOnStack>().LastOrDefault() is { } hasty && hasty.Card != oid)
                                _copiesGainHasteAndSacrifice.Add(hasty.Card);
                        }
                    if (cs.SpellsGain is { } gains && State.GetCard(oid) is { Zone: Zone.Stack } stillOnStack) stillOnStack.GrantedKeywords.UnionWith(gains);
                }
                break;
            }
            case OpponentsMaySacrificeSharingType share:
            {
                if (ctx.Results.Sacrificed.Count == 0) break;
                var gone = State.GetCard(ctx.Results.Sacrificed[^1]);
                var types = (gone.LastKnownInfo?.Types ?? gone.Types)
                    & (CardType.Artifact | CardType.Creature | CardType.Enchantment | CardType.Land | CardType.Planeswalker | CardType.Battle);
                var chosen = new List<CardId>();
                var refused = new List<PlayerId>();
                foreach (var opponent in State.ApnapOrder().Where(State.OpponentsOf(ctx.Controller).Contains).ToList())
                {
                    var options = State.PermanentsControlledBy(opponent).Where(c => (c.Types & types) != 0 && CanBeSacrificedBy(c, opponent)).ToList();
                    var picked = options.Count == 0 ? new List<CardId>()
                        : await ControllerOf(opponent).ChooseCardsAsync(ViewFor(opponent), new CardChoiceRequest(
                            $"Sacrifice a permanent that shares a card type with {gone.Name}? (Otherwise you lose {share.LifeLoss} life and its controller draws a card.)",
                            ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, opponent)).ToList(), 0, 1, CardChoicePurpose.Sacrifice));
                    Require(picked.Count <= 1 && picked.All(id => options.Any(c => c.Id == id)), "Choose among the listed permanents.");
                    if (picked.Count == 1) chosen.Add(picked[0]);
                    else refused.Add(opponent);
                }
                BeginSimultaneous();
                foreach (var id in chosen) await SacrificePermanentAsync(id);
                EndSimultaneous();
                foreach (var opponent in refused)
                {
                    ChangeLife(opponent, -share.LifeLoss);
                    await DrawAsync(ctx.Controller, share.Draw);
                }
                break;
            }
            case AddManaOfAnyColor anyColor:
                foreach (var adder in anyColor.Who is { } manaWho ? PlayersFor(manaWho, ctx).ToList() : new List<PlayerId> { ctx.Controller })
                {
                    var allColors = new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green };
                    var colors = anyColor.CommanderIdentity ? allColors.Where(CommanderIdentityTypes(adder).Contains).ToArray() : allColors;
                    if (colors.Length == 0) continue; // a colorless identity: no mana (rule 903.4f)
                    int i = colors.Length == 1 ? 0 : await ControllerOf(adder).ChooseOptionAsync(ViewFor(adder), new OptionRequest("Choose a color of mana", ctx.Source.Id,
                        colors.Select(c => ColorNames[Array.IndexOf(allColors, c)]).ToList(), OptionKind.Color));
                    Require(i >= 0 && i < colors.Length, "Choose a color.");
                    var type = colors[i];
                    if (anyColor.SourceBecomesThatColor && SourceOnBattlefield(ctx))
                        State.UntilEndOfTurn.Add(new UntilEndOfTurnEffect(ctx.Source.Id, ctx.Source.Version, 0, 0, Array.Empty<Keyword>())
                            { SetColors = new[] { ColorLetters[Array.IndexOf(allColors, type)] }, Timestamp = NewTimestamp() });
                    if (anyColor.SourceBecomesThatColor) RecomputeContinuousEffects();
                    int count = anyColor.DevotionToThatColor ? DevotionTo(adder, new[] { ColorLetters[Array.IndexOf(allColors, type)] }) : anyColor.Count;
                    for (int n = 0; n < count; n++)
                    {
                        if (anyColor.Rider != ManaRider.None) State.GetPlayer(adder).ManaPool.AddSpecial(new ManaUnit(type, ctx.Source.Id, null, true, anyColor.Rider));
                        else State.GetPlayer(adder).ManaPool.Add(type);
                        Emit(new ManaAdded(adder, type, ctx.Source.Id));
                    }
                }
                break;
            case Incubate inc:
            {
                // Incubate N (rule 701.53a): create an Incubator token with N +1/+1 counters on it.
                int n = Eval(inc.Amount, ctx);
                ctx.Results.Created.AddRange((await CreateTokenEventAsync(ctx.Controller, inc.Token, 1, false, ctx.Source, enterCounters: n)).Originals);
                break;
            }
            case PlayerExilesPermanents pe:
            {
                // Each player chooses in turn order, then everything chosen is exiled at the same time (rule 101.4).
                var players = PlayersFor(pe.Who, ctx).ToList();
                var chosenToExile = new List<CardId>();
                foreach (var player in State.ApnapOrder().Where(players.Contains).ToList())
                {
                    var options = State.PermanentsControlledBy(player).Where(c => Matches(pe.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller)).ToList();
                    int n = Math.Min(Eval(pe.Count, ctx), options.Count);
                    if (n == 0) continue;
                    var picked = options.Count == n ? options.Select(c => c.Id).ToList()
                        : await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest($"Exile {n} you control", ctx.Source.Id,
                            options.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), n, n, CardChoicePurpose.Sacrifice));
                    Require(picked.Count == n && picked.Distinct().Count() == n && picked.All(id => options.Any(c => c.Id == id)), "Choose among the listed permanents.");
                    chosenToExile.AddRange(picked);
                }
                BeginSimultaneous();
                foreach (var id in chosenToExile)
                {
                    await MoveCardAsync(id, Zone.Exile);
                    if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
                }
                EndSimultaneous();
                break;
            }
            case RevealExileOnePerType rx:
            {
                var who = ctx.Controller;
                var library = State.GetPlayer(who).Library;
                var revealed = library.Take(Math.Max(0, Eval(rx.Count, ctx))).ToList();
                if (revealed.Count == 0) break;
                Emit(new CardsRevealed(who, revealed));
                var picked = new List<CardId>();
                foreach (var type in new[] { CardType.Artifact, CardType.Battle, CardType.Creature, CardType.Enchantment, CardType.Instant, CardType.Kindred,
                             CardType.Land, CardType.Planeswalker, CardType.Sorcery })
                {
                    var ofType = revealed.Where(id => !picked.Contains(id) && State.GetCard(id).Is(type)).ToList();
                    if (ofType.Count == 0) continue;
                    var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"You may exile a {type.ToString().ToLowerInvariant()} card", ctx.Source.Id,
                        ofType.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
                    Require(pick.Count <= 1 && pick.All(ofType.Contains), "Choose at most one of the listed cards.");
                    picked.AddRange(pick);
                }
                foreach (var id in picked) await MoveCardAsync(id, Zone.Exile);
                foreach (var id in revealed.Where(id => !picked.Contains(id) && State.GetCard(id).Zone == Zone.Library).ToList()) await MoveCardAsync(id, Zone.Graveyard);
                var exiled = picked.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
                if (exiled.Count >= rx.CastIfAtLeast)
                {
                    var castable = exiled.Where(id => !State.GetCard(id).Is(CardType.Land) && HasLegalTargets(CastingTargets(State.GetCard(id).Definition), who, id)).ToList();
                    if (castable.Count > 0)
                    {
                        var cast = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("You may cast one of them without paying its mana cost", ctx.Source.Id,
                            castable.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 0, 1, CardChoicePurpose.Keep));
                        Require(cast.Count <= 1 && cast.All(castable.Contains), "Choose at most one of the listed cards.");
                        if (cast.Count == 1) await CastNowWithoutPayingAsync(State.GetPlayer(who), cast[0]);
                    }
                }
                foreach (var id in exiled.Where(id => State.GetCard(id).Zone == Zone.Exile)) await MoveCardAsync(id, Zone.Hand);
                break;
            }
            case DamageEachPlayerByCount dep:
                foreach (var player in State.ApnapOrder().ToList())
                {
                    int amount = State.PermanentsControlledBy(player).Count(c => Matches(dep.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller));
                    if (amount > 0) await ApplyAsync(new DealDamage(amount, new Subject(SubjectKind.FixedPlayer) { Player = player }), ctx);
                }
                break;
            case ExileFromOwnHand eh:
                foreach (var player in State.ApnapOrder().Where(PlayersFor(eh.Who, ctx).Contains).ToList())
                {
                    var hand = State.GetPlayer(player).Hand.ToList();
                    int n = Math.Min(Eval(eh.Count, ctx), hand.Count);
                    if (n == 0) continue;
                    var picked = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest($"Exile {n} from your hand", ctx.Source.Id,
                        hand.Select(id => ViewBuilder.Card(State, id, player)).ToList(), n, n, CardChoicePurpose.Discard));
                    Require(picked.Count == n && picked.Distinct().Count() == n && picked.All(hand.Contains), "Choose cards from your hand.");
                    foreach (var id in picked)
                    {
                        await MoveCardAsync(id, Zone.Exile);
                        if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
                    }
                }
                break;
            case TopCardToHand th:
                foreach (var player in PlayersFor(th.Who, ctx).ToList())
                    if (State.GetPlayer(player).Library.Count > 0) await MoveCardAsync(State.GetPlayer(player).Library[0], Zone.Hand);
                break;
            case CastExiledThisWay cet:
                foreach (var id in (cet.Found ? ctx.Results.Found : ctx.Results.Exiled).Where(id => State.GetCard(id).Zone == Zone.Exile).ToList())
                    State.PlayableFromExile.Add(cet.FreeThisTurn
                        ? new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, State.TurnNumber, WithoutPaying: true) { CastOnly = true }
                        : new PlayableFromExile(id, State.GetCard(id).Version, ctx.Controller, int.MaxValue) { AnyManaType = true });
                break;
            case PlayersGainHexproof ph:
                foreach (var player in PlayersFor(ph.Who, ctx)) State.GetPlayer(player).HexproofTurn = State.TurnNumber;
                break;
            case BecomeClassLevel bcl:
                // The level of the Class the ability came from, if it is still that permanent (rule 716.2a).
                if (ctx.Source.Zone == Zone.Battlefield && IsSourceObject(ctx) && ctx.Source.ClassLevel < bcl.Level)
                {
                    ctx.Source.ClassLevel = bcl.Level;
                    Emit(new ClassLevelChanged(ctx.Source.Id, bcl.Level));
                }
                break;
            case PutExiledWithThisIntoGraveyard pg:
            {
                var exiled = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith == (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version)).ToList();
                if (exiled.Count == 0) break;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest("Choose an exiled card to put into its owner's graveyard",
                    ctx.Source.Id, exiled.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(pick.Count == 1 && exiled.Any(c => c.Id == pick[0]), "Choose one of the exiled cards.");
                var card = State.GetCard(pick[0]);
                bool land = card.Is(CardType.Land);
                await MoveCardAsync(card.Id, Zone.Graveyard);
                await ApplyAllAsync(land ? pg.IfLand : pg.IfNonland, ctx);
                break;
            }
            case ReturnExiledWithThis rew:
            {
                var exiled = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == ctx.Source.Id
                                                           && (w.Version == ctx.Source.Version || w.Version == ctx.Source.Version - 1)).ToList();
                if (rew.Count > 0 && exiled.Count > rew.Count)
                {
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest($"Choose {rew.Count} exiled card(s) to put into its owner's hand",
                        ctx.Source.Id, exiled.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), rew.Count, rew.Count, CardChoicePurpose.ToHand));
                    Require(pick.Count == rew.Count && pick.All(id => exiled.Any(c => c.Id == id)), "Choose among the exiled cards.");
                    exiled = exiled.Where(c => pick.Contains(c.Id)).ToList();
                }
                if (rew.ToBattlefield)
                {
                    // "Return those cards to the battlefield under their owner's control": all at once.
                    BeginSimultaneous();
                    foreach (var card in exiled) await MoveCardAsync(card.Id, Zone.Battlefield, controller: card.Owner);
                    EndSimultaneous();
                }
                else foreach (var card in exiled) await MoveCardAsync(card.Id, Zone.Hand);
                break;
            }
            case SearchAndExileWithThis se:
            {
                var player = State.GetPlayer(ctx.Controller);
                Look(ctx.Controller, player.Library); // searching is looking through the whole library (rule 701.23a)
                var options = player.Library.Select(State.GetCard).Where(c => Matches(se.Filter with { Controller = ControllerFilter.Any }, c, ctx.Controller, ctx.Source, ctx.Controller))
                    .Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList();
                if (options.Count > 0)
                {
                    var chosen = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller),
                        new CardChoiceRequest($"Search your library: choose up to {se.Count} to exile", ctx.Source.Id, options, 0, Math.Min(se.Count, options.Count), CardChoicePurpose.ToHand));
                    foreach (var id in chosen.Where(id => options.Any(o => o.Id == id)))
                    {
                        await MoveCardAsync(id, Zone.Exile);
                        State.GetCard(id).ExiledWith = (ctx.Source.Id, ctx.Source.Version);
                    }
                }
                Shuffle(player);
                break;
            }
            case GrantFlashback gf:
                foreach (var card in (gf.EachInYourGraveyard is { } each
                             ? State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard).Where(c => Matches(each, c, c.Owner, ctx.Source, ctx.Controller)).ToList()
                             : CardsFor(gf.What, ctx)).Where(c => c.Zone == Zone.Graveyard).ToList())
                    State.FlashbackGranted.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber));
                break;
            case PlayableFromGraveyardThisTurn pg:
            {
                int group = pg.OneOnly ? ++_playGroups : 0;
                var gaining = pg.EachInYourGraveyard is { } each
                    ? State.GetPlayer(ctx.Controller).Graveyard.Select(State.GetCard).Where(c => Matches(each with { Controller = ControllerFilter.Any }, c, c.Owner, ctx.Source, ctx.Controller)).ToList()
                    : CardsFor(pg.What, ctx).ToList();
                foreach (var card in gaining.Where(c => c.Zone == Zone.Graveyard && !(pg.OneOnly && c.Is(CardType.Land))))
                    State.PlayableFromGraveyard.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber) { ExileInstead = pg.ExileInstead, Group = group });
                break;
            }
            case Become b:
            {
                // "For as long as this remains on the battlefield": nothing happens if it already left (or left and came back as a new object).
                if (b.WhileSourceRemains && !SourceOnBattlefield(ctx)) break;
                int? power = b.Power ?? (b.PowerFrom is { } pf && !b.Continuous ? Eval(pf, ctx) : null);
                int? toughness = b.Toughness ?? (b.ToughnessFrom is { } tf && !b.Continuous ? Eval(tf, ctx) : null);
                foreach (var card in CardsFor(b.What, ctx).ToList())
                {
                    bool stats = !b.PowerOnlyIfCreature || card.IsCreature;
                    var becomes = new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, b.Keywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
                    {
                        AddTypes = b.AddTypes, SetPower = stats ? power : null, SetToughness = stats ? toughness : null, UntilTurnOf = b.UntilYourNextTurn ? ctx.Controller : null, AddSubtypes = b.AddSubtypes, SetSubtypes = b.SetSubtypes, SetColors = b.SetColors, AddColors = b.AddColors,
                        SetPowerFrom = b.Continuous ? b.PowerFrom : null, SetToughnessFrom = b.Continuous ? b.ToughnessFrom : null,
                        Abilities = b.Abilities?.Select(a => BindGranter(a, ctx.Controller)).ToList(),
                        WhileSource = b.WhileSourceRemains ? (ctx.Source.Id, ctx.Source.Version) : null,
                        LosesAbilities = b.LosesAbilities, AddsEveryCreatureType = b.EveryCreatureType, SetTypes = b.SetTypes,
                        WhileCounter = b.WhileCounter,
                        LosesLandTypes = b.LosesLandTypes,
                        GrantsMana = b.GrantsMana,
                        Timestamp = NewTimestamp(),
                    };
                    (b.Permanent || b.WhileSourceRemains || b.WhileCounter is not null || b.UntilYourNextTurn ? State.LastingEffects : State.UntilEndOfTurn).Add(becomes);
                }
                break;
            }
            case MillUntil mu:
                foreach (var player in PlayersFor(mu.Who, ctx).ToList())
                {
                    var library = State.GetPlayer(player).Library;
                    while (library.Count > 0)
                    {
                        var top = State.GetCard(library[0]);
                        bool found = Matches(mu.Until with { Controller = ControllerFilter.Any }, top, player, ctx.Source, ctx.Controller);
                        await MoveCardAsync(top.Id, Zone.Graveyard);
                        if (found) break;
                    }
                }
                break;
            case ExileGraveyard eg:
                foreach (var player in PlayersFor(eg.Who, ctx).ToList())
                    foreach (var id in State.GetPlayer(player).Graveyard.ToList())
                    {
                        var card = State.GetCard(id);
                        if (eg.Filter is { } egFilter && !Matches(egFilter with { Controller = ControllerFilter.Any }, card, card.Owner, ctx.Source, ctx.Controller)) continue;
                        await MoveCardAsync(id, Zone.Exile);
                        if (card.Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
                        if (eg.Playable && card.Zone == Zone.Exile)
                            State.PlayableFromExile.Add(new PlayableFromExile(id, card.Version, ctx.Controller, int.MaxValue) { AnyManaType = true });
                    }
                break;
            case DoubleCounters dbl:
                foreach (var card in CardsFor(dbl.What, ctx).ToList())
                    foreach (var (kind, have) in card.Counters.Where(kv => kv.Value > 0 && (dbl.Kind is null || kv.Key == dbl.Kind)).ToList())
                        PutCounters(card, kind, have, ctx.Controller);
                break;
            case RemoveCounters rc:
            {
                int count = Eval(rc.Count, ctx);
                foreach (var card in CardsFor(rc.What, ctx).ToList()) RemoveCountersFrom(card, rc.Kind, count);
                break;
            }
            case ShuffleGraveyardIntoLibrary sg:
                foreach (var player in PlayersFor(sg.Who, ctx).ToList())
                {
                    foreach (var id in State.GetPlayer(player).Graveyard.ToList()) await MoveCardAsync(id, Zone.Library);
                    Shuffle(State.GetPlayer(player));
                }
                break;
            case AddMana am:
                for (int n = am.Times is { } times ? Eval(times, ctx) : 1; n > 0; n--)
                    foreach (var type in am.Types)
                    {
                        if (am.OnlyFor is { } only) State.GetPlayer(ctx.Controller).ManaPool.AddSpecial(new ManaUnit(type, ctx.Source.Id, only, false, ManaRider.None) { ForUnlock = am.ForUnlock });
                        else State.GetPlayer(ctx.Controller).ManaPool.Add(type);
                        Emit(new ManaAdded(ctx.Controller, type, ctx.Source.Id));
                    }
                ManaPoolChanged();
                break;
            case DealsDamageEqualToPower dp:
            {
                var biter = CardsFor(dp.Source, ctx).FirstOrDefault();
                if (biter is null || !biter.IsCreature) break;
                int power = biter.Power * dp.Times;
                var bitten = CardsFor(dp.To, ctx).ToList();
                // "If the creature you control has trample, excess damage is dealt to that creature's controller instead":
                // what's beyond lethal damage (1 from a deathtouch source, rule 702.2c).
                if (dp.ExcessToControllerIfTrample && biter.Has(Keyword.Trample))
                {
                    var excessParts = new List<DamagePart>();
                    foreach (var c in bitten)
                    {
                        int left = Math.Max(0, c.Toughness - c.Damage);
                        int lethal = left > 0 && biter.Has(Keyword.Deathtouch) ? 1 : left;
                        int toCreature = c.IsCreature ? Math.Min(power, lethal) : power;
                        excessParts.Add(new DamagePart(biter, c, null, toCreature, false) { Report = ctx.Results, CountExcess = false });
                        if (power > toCreature) excessParts.Add(new DamagePart(biter, null, c.Controller, power - toCreature, false) { Report = ctx.Results });
                    }
                    await DealDamageEventAsync(excessParts);
                    break;
                }
                // "Dealt damage this way" and excess damage are noted as the damage is dealt (DamagePart.Report).
                await DealDamageEventAsync(bitten.Select(c => new DamagePart(biter, c, null, power, false) { Report = ctx.Results })
                    .Concat(PlayersFor(dp.To, ctx).Select(p => new DamagePart(biter, null, p, power, false))).ToList());
                break;
            }
            case ReanimateAll ra:
                BeginEnteringTogether();
                foreach (var player in PlayersFor(ra.Who, ctx).ToList())
                    foreach (var card in State.GetPlayer(player).Graveyard.Select(State.GetCard)
                                 .Where(c => Matches(ra.Filter with { Controller = ControllerFilter.Any }, c, player, ctx.Source, ctx.Controller)).ToList())
                    {
                        if (ra.As is { Transformed: true } && !card.IsDoubleFaced) continue; // stays in the graveyard (rule 712.14a)
                        await MoveCardAsync(card.Id, Zone.Battlefield, controller: ra.As?.UnderOwnersControl == true ? card.Owner : ctx.Controller,
                            transformed: ra.As?.Transformed == true);
                        if (ra.As is { } becomes && card.Zone == Zone.Battlefield) BecomeAsItEnters(card, becomes, ctx);
                    }
                EndEnteringTogether();
                break;
            case BounceAll ba:
            {
                var relative = ba.RelativeTo is { } rel ? PlayersFor(rel, ctx).FirstOrDefault() : ctx.Controller;
                BeginSimultaneous(); // all of them return at once: leaves-the-battlefield abilities see each other leave (rule 603.10a)
                foreach (var card in State.Battlefield.Select(State.GetCard)
                             .Where(c => Matches(ba.Filter, c, c.Controller, ctx.Source, relative)).ToList())
                    await MoveCardAsync(card.Id, Zone.Hand);
                EndSimultaneous();
                break;
            }
            case PutOntoBattlefield p:
            {
                // "Return it": only the same object (a card that changed zones again is a new object, rule 400.7).
                var cards = p.What.Kind == SubjectKind.Self
                    ? (ctx.Source.Zone is Zone.Graveyard or Zone.Hand or Zone.Exile or Zone.Command && IsSourceObject(ctx)
                        ? new[] { ctx.Source } : Array.Empty<Card>())
                    : p.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc
                      && tc.Version == ti.SubjectVersion && tc.Zone != Zone.Battlefield ? new[] { tc }
                    : CardsFor(p.What, ctx).Where(c => c.Zone != Zone.Battlefield).ToArray();
                BeginEnteringTogether();
                var attachTo = p.AttachTo is { } host ? CardsFor(host, ctx).FirstOrDefault()?.Id : null;
                if (p.AttachTo is not null && attachTo is null) break; // an Aura returned attached to a creature that is gone stays where it is
                foreach (var card in cards)
                {
                    if (p.Transformed && !card.IsDoubleFaced) continue; // a card that isn't double-faced stays where it is (rule 712.14a)
                    // "With two +1/+1 counters on it", "with a vigilance counter and a lifelink counter on it": counters it enters with
                    // (rule 614.1c), on it before anything sees it enter.
                    var withCounters = new List<(CounterKind Kind, int Count)>();
                    if (p.Counters > 0) withCounters.Add((p.CounterKind, p.Counters));
                    if (p.CounterKinds is { } kinds) withCounters.AddRange(kinds.Select(k => (k, 1)));
                    bool faceDown = p.FaceDown || (p.FaceDownChoice && await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
                        new OptionRequest($"{card.Name}: return it face up or face down?", card.Id, new[] { "Face up", "Face down" }, OptionKind.Other)) == 1);
                    if (faceDown) { _enteringFaceDown = card.Id; _enteringFaceDownAs = p.FaceDownAs; }
                    try
                    {
                        await MoveCardAsync(card.Id, Zone.Battlefield, controller: p.UnderOwnersControl ? card.Owner : ctx.Controller, attachTo: attachTo, tapped: p.Tapped,
                            transformed: p.Transformed, withCounters: withCounters);
                    }
                    finally { if (faceDown) { _enteringFaceDown = null; _enteringFaceDownAs = null; } }
                    if (p.Attacking && State.Combat is { } combat && card.IsCreature)
                    {
                        // Put onto the battlefield attacking: its controller chooses which player it attacks (rule 508.4).
                        var defending = combat.Attacks.Select(a => a.Defender).Distinct().ToList();
                        if (defending.Count == 0) defending = State.OpponentsOf(card.Controller).ToList();
                        var defender = defending.Count == 1 ? defending[0]
                            : await ChooseOpponentAsync(card.Controller, ctx.Source, $"Choose the player {card.Name} attacks", defending) ?? defending[0];
                        combat.Attacks.Add(new AttackInfo { Attacker = card.Id, Defender = defender });
                    }
                    if (card.Zone == Zone.Battlefield) ctx.Results.Entered.Add(card.Id);
                    BecomeAsItEnters(card, p, ctx);
                    if (p.ExileIfLeaves && card.Zone == Zone.Battlefield) State.ExileIfLeaves.Add((card.Id, card.Version));
                    RecomputeContinuousEffects();
                }
                EndEnteringTogether();
                break;
            }
            case SearchLibrary sl:
                foreach (var searcher in (sl.Who is { } who ? PlayersFor(who, ctx) : new[] { ctx.Controller }).ToList())
                {
                    if (sl.Optional && !await ControllerOf(searcher).ChooseYesNoAsync(ViewFor(searcher), new YesNoRequest("Search your library?", ctx.Source.Id))) continue;
                    var search = sl.MaxManaValueX ? sl with { Filter = sl.Filter with { MaxManaValue = ctx.X } } : sl;
                    if (sl.CountFrom is { } countFrom) search = search with { Count = Eval(countFrom, ctx) };
                    if (search.Count <= 0) continue;
                    _searchWithExiled = sl.WithExiled ? ctx.Results.Exiled.ToList() : new List<CardId>();
                    ctx.Results.Found.AddRange(await SearchLibraryAsync(searcher, search, ctx.Source));
                }
                break;
            case Sacrifice sac:
            {
                // Each player chooses in turn order, then everything chosen is sacrificed at the same time (rule 101.4).
                var players = PlayersFor(sac.Who, ctx).ToList();
                var choices = new List<(PlayerId Player, IReadOnlyList<CardId> Chosen)>();
                foreach (var player in State.ApnapOrder().Where(players.Contains).ToList())
                    choices.Add((player, await ChooseSacrificesAsync(player, Eval(sac.Count, ctx with { AffectedPlayer = player }), sac.Filter, ctx.Source)));
                BeginSimultaneous();
                foreach (var (player, chosen) in choices)
                {
                    foreach (var id in chosen) await SacrificePermanentAsync(id);
                    ctx.Results.Sacrificed.AddRange(chosen);
                    if (player == ctx.Controller && chosen.Count > 0) ctx.Results.YouSacrificed = true;
                }
                EndSimultaneous();
                if (sac.OtherwiseDiscard > 0)
                    foreach (var (player, chosen) in choices.Where(c => c.Chosen.Count == 0))
                        ctx.Results.Discarded.AddRange(await DiscardAsync(player, sac.OtherwiseDiscard, ctx.Controller));
                break;
            }
            case PutIntoLibrary pl:
            {
                var placed = new List<(CardId Card, PlayerId Owner, bool Bottom)>();
                foreach (var card in (pl.What.Kind == SubjectKind.Self && (SourceIn(ctx, Zone.Graveyard) || SourceIn(ctx, Zone.Exile)) ? new[] { ctx.Source } : CardsFor(pl.What, ctx)).ToList())
                {
                    if (pl.Position > 0 && pl.OrBottom && await ControllerOf(card.Owner).ChooseYesNoAsync(ViewFor(card.Owner),
                            new YesNoRequest($"Put {card.Name} on the bottom of your library? (No: {OrdinalPosition(pl.Position)} from the top)", card.Id)))
                    {
                        await MoveCardAsync(card.Id, Zone.Library, toBottom: true);
                        placed.Add((card.Id, card.Owner, true));
                        continue;
                    }
                    if (pl.Position > 0)
                    {
                        // "Second from the top": below that many cards, or at the bottom of a shorter library.
                        await MoveCardAsync(card.Id, Zone.Library);
                        var library = State.GetPlayer(card.Owner).Library;
                        if (!library.Remove(card.Id)) continue; // a replacement effect put it elsewhere
                        library.Insert(Math.Min(pl.Position - 1, library.Count), card.Id);
                        continue;
                    }
                    bool bottom = !pl.Top && (pl.Bottom || await ControllerOf(card.Owner).ChooseYesNoAsync(ViewFor(card.Owner),
                        new YesNoRequest($"Put {card.Name} on the bottom of your library? (No: on top)", card.Id)));
                    await MoveCardAsync(card.Id, Zone.Library, toBottom: bottom);
                    placed.Add((card.Id, card.Owner, bottom));
                }
                // Several cards put on the same end of a library at once: their owner doesn't reveal the order (rule 401.4).
                foreach (var group in placed.GroupBy(x => (x.Owner, x.Bottom)))
                    PlacedInUnseenOrder(group.Select(x => x.Card), group.Key.Owner);
                break;
            }
            case GainControl g:
            {
                PlayerId? chosen = g.NewController is { Kind: SubjectKind.EachOpponent }
                    ? await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who gains control") ?? ctx.Controller
                    : g.NewController is { } who ? PlayersFor(who, ctx).Cast<PlayerId?>().FirstOrDefault() : ctx.Controller;
                // The player who would gain control is an illegal target now: nobody gains control (rule 608.2b).
                if (chosen is not { } newController) break;
                if (g.WhileYouControlSource && (!SourceOnBattlefield(ctx) || ctx.Source.Controller != newController)) break; // already over
                if (g.WhileSourceRemains && !SourceOnBattlefield(ctx)) break;
                PlayerId? whileMonarch = g.WhileTriggeredPlayerIsMonarch ? ctx.Trigger?.Player : null;
                if (g.WhileTriggeredPlayerIsMonarch && (whileMonarch is null || State.Monarch != whileMonarch)) break; // already over
                long stamp = NewTimestamp();
                ctx.Results.ControlGained.AddRange(CardsFor(g.What, ctx).Select(c => c.Id));
                foreach (var card in CardsFor(g.What, ctx).ToList())
                    State.ControlEffects.Add(new ControlEffect(card.Id, card.Version, newController, stamp)
                    {
                        UntilEndOfTurn = g.UntilEndOfTurn,
                        UntilEndOfNextTurnOf = g.UntilEndOfYourNextTurn ? ctx.Controller : null,
                        MadeOnTurn = State.TurnNumber,
                        WhileSource = g.WhileYouControlSource || g.WhileSourceRemains ? (ctx.Source.Id, ctx.Source.Version) : null,
                        WhileSourceRemainsOnly = g.WhileSourceRemains,
                        WhileMonarch = whileMonarch,
                        WhileCounter = g.WhileCounter,
                    });
                RecomputeContinuousEffects();
            }
                break;
            case Scry sc:
                await LookAtTopAsync(ctx.Controller, sc.CountFrom is { } scryCount ? Eval(scryCount, ctx) : sc.Count, CardChoicePurpose.ScryToBottom, ctx.Source);
                break;
            case Surveil sv:
            {
                var graveyardBefore = State.GetPlayer(ctx.Controller).Graveyard.ToHashSet();
                await LookAtTopAsync(ctx.Controller, sv.Count, CardChoicePurpose.SurveilToGraveyard, ctx.Source);
                ctx.Results.Surveilled.AddRange(State.GetPlayer(ctx.Controller).Graveyard.Where(id => !graveyardBefore.Contains(id)));
                break;
            }
            case SurveilledToHand sh:
            {
                int? most = sh.MaxManaValue is { } mmv ? Eval(mmv, ctx) : null;
                foreach (var card in ctx.Results.Surveilled.Select(State.GetCard).Where(c => c.Zone == Zone.Graveyard).ToList())
                    if (Matches(sh.Filter, card, card.Owner, ctx.Source, ctx.Controller) && (most is not { } m || ManaValueOf(card) <= m))
                        await MoveCardAsync(card.Id, Zone.Hand);
                break;
            }
            case ExileTopMayCast et:
            {
                var you = State.GetPlayer(ctx.Controller);
                if (you.Library.Count == 0) { await ApplyAllAsync(et.IfNot, ctx); break; }
                var topId = you.Library[0];
                await MoveCardAsync(topId, Zone.Exile);
                var top = State.GetCard(topId);
                bool cast = false;
                if (top.Zone == Zone.Exile && !top.Is(CardType.Land) && CanBeCast(top, ctx.Controller, flashExtra: false)
                    && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest($"Cast {top.Name}?", topId)))
                {
                    await CastSpellAsync(you, topId);
                    cast = top.Zone == Zone.Stack;
                }
                if (!cast) await ApplyAllAsync(et.IfNot, ctx);
                break;
            }
            case OwnerMayPutOnTop ot:
                foreach (var card in CardsFor(ot.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                {
                    bool onTop = await ControllerOf(card.Owner).ChooseYesNoAsync(ViewFor(card.Owner), new YesNoRequest($"{ctx.Source.Name}: put {card.Name} on top of your library?", card.Id));
                    await MoveCardAsync(card.Id, Zone.Library, toBottom: !onTop);
                    if (onTop) await ApplyAllAsync(ot.IfTop, ctx);
                }
                break;
            case Fight f:
            {
                var first = CardsFor(f.First, ctx).FirstOrDefault();
                var second = CardsFor(f.Second, ctx).FirstOrDefault();
                // If either creature is gone (or no longer a creature), no damage is dealt (rule 701.14b).
                if (first is null || second is null || !first.IsCreature || !second.IsCreature) break;
                int firstPower = first.Power, secondPower = second.Power;
                // Both creatures deal their damage at the same time (one damage event).
                var fightParts = new List<DamagePart> { new(first, second, null, firstPower, false) };
                if (first.Id != second.Id) fightParts.Add(new DamagePart(second, first, null, secondPower, false));
                await DealDamageEventAsync(fightParts);
                break;
            }
            case TokensFightOpponentsCreatures tf:
            {
                // "For each creature your opponents control, create a [token]. Each of those tokens fights a different one of those creatures."
                var prey = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature && State.OpponentsOf(ctx.Controller).Contains(c.Controller)).ToList();
                if (prey.Count == 0) break;
                var made = (await CreateTokenEventAsync(ctx.Controller, tf.Token, prey.Count, false, ctx.Source)).All;
                ctx.Results.Created.AddRange(made);
                var fights = new List<DamagePart>();
                foreach (var (hunter, quarry) in made.Select(State.GetCard).Zip(prey))
                {
                    if (hunter.Zone != Zone.Battlefield || !hunter.IsCreature || quarry.Zone != Zone.Battlefield || !quarry.IsCreature) continue;
                    fights.Add(new DamagePart(hunter, quarry, null, hunter.Power, false));
                    fights.Add(new DamagePart(quarry, hunter, null, quarry.Power, false));
                }
                await DealDamageEventAsync(fights);
                break;
            }
            case Discard d:
                foreach (var player in PlayersFor(d.Who, ctx).ToList())
                    ctx.Results.Discarded.AddRange(d.AtRandom
                        ? await DiscardAtRandomAsync(player, Eval(d.Count, ctx with { AffectedPlayer = player }), ctx.Controller)
                        : await DiscardAsync(player, Eval(d.Count, ctx with { AffectedPlayer = player }), ctx.Controller));
                break;
            case DiscardAnyNumber dan:
            {
                // "Discard any number of cards" / "discard up to two cards": the player chooses which (none is fine).
                PlayerId? discarderOrNone = dan.Who is { } danWho ? PlayersFor(danWho, ctx).Cast<PlayerId?>().FirstOrDefault() : ctx.Controller;
                if (discarderOrNone is not { } discarder) break;
                var hand = State.GetPlayer(discarder).Hand.ToList();
                if (hand.Count == 0) break;
                int most = Math.Min(hand.Count, dan.Max ?? hand.Count);
                var picked = await ControllerOf(discarder).ChooseCardsAsync(ViewFor(discarder), new CardChoiceRequest(
                    dan.Max is { } upTo ? $"{ctx.Source.Name}: discard up to {upTo} cards" : $"{ctx.Source.Name}: discard any number of cards", ctx.Source.Id,
                    hand.Select(id => ViewBuilder.Card(State, id, discarder)).ToList(), 0, most, CardChoicePurpose.Discard));
                Require(picked.Distinct().Count() == picked.Count && picked.All(hand.Contains) && picked.Count <= most, "Choose cards from your hand.");
                foreach (var id in picked)
                {
                    await DiscardCardAsync(discarder, id, ctx.Controller, byEffect: true);
                    ctx.Results.Discarded.Add(id);
                }
                break;
            }
            case IfThen c:
                await ApplyAllAsync(HoldsIn(c.Condition, ctx) ? c.Then : c.Else ?? Array.Empty<Effect>(), ctx);
                break;
            case MayDo m:
                if (m.OncePerTurn && ctx.Source.DoneThisTurn.Contains(m)) break; // "Do this only once each turn"
                if (await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller), new YesNoRequest(m.Prompt, ctx.Source.Id)))
                {
                    if (m.OncePerTurn) ctx.Source.DoneThisTurn.Add(m);
                    await ApplyAllAsync(m.Effects, ctx);
                }
                else if (m.Else is { } otherwise) await ApplyAllAsync(otherwise, ctx);
                break;
            case DealDamage d:
            {
                int amount = Eval(d.Amount, ctx);
                var cards = CardsFor(d.To, ctx).ToList();
                var parts = new List<DamagePart>();
                // "It deals X damage": another object deals the damage (no damage if there's no such object).
                var dealer = ctx.Source;
                if (d.From is { } from)
                {
                    // An object that has left its zone still deals the damage, as it last existed (rule 608.2h).
                    var other = from.Kind == SubjectKind.Triggered && ctx.Trigger?.Subject is { } dealerId ? State.GetCard(dealerId) : CardsFor(from, ctx).FirstOrDefault();
                    if (other is null) break;
                    dealer = other;
                }
                // Lethal damage is 1 from a deathtouch source (rule 702.2c).
                int LethalFor(Card card) => Math.Max(0, card.Toughness - card.Damage) is var l && dealer.Has(Keyword.Deathtouch) && l > 0 ? 1 : Math.Max(0, card.Toughness - card.Damage);
                // "Dealt damage this way" and excess damage are noted as the damage is dealt (DamagePart.Report): inside
                // "simultaneously" that's once the whole event has been put together and dealt.
                foreach (var card in cards)
                {
                    int lethal = LethalFor(card);
                    if (d.ExcessToController && card.IsCreature && amount > lethal)
                    {
                        // "Excess damage is dealt to that creature's controller instead."
                        parts.Add(new DamagePart(dealer, card, null, lethal, false) { Report = ctx.Results, CountExcess = false, Unpreventable = d.Unpreventable });
                        parts.Add(new DamagePart(dealer, null, card.Controller, amount - lethal, false) { Report = ctx.Results, Unpreventable = d.Unpreventable });
                        continue;
                    }
                    parts.Add(new DamagePart(dealer, card, null, amount, false) { Report = ctx.Results, Unpreventable = d.Unpreventable });
                }
                // "That creature" is the object the trigger is about, never its controller as well.
                if (d.To.Kind != SubjectKind.Triggered)
                    // "Equal to the number of cards in that player's hand": the amount worked out for each player dealt damage.
                    foreach (var player in PlayersFor(d.To, ctx))
                        parts.Add(new DamagePart(dealer, null, player, d.Amount.Kind == QuantityKind.AffectedHandSize ? Eval(d.Amount, ctx with { AffectedPlayer = player }) : amount, false)
                            { Report = ctx.Results, Unpreventable = d.Unpreventable });
                await DealDamageEventAsync(parts);
            }
                break;
            case DrawCards d:
                foreach (var player in PlayersFor(d.Who, ctx).ToList())
                {
                    int drawnBefore = State.GetPlayer(player).CardsDrawnThisTurn;
                    await DrawAsync(player, Eval(d.Count, ctx with { AffectedPlayer = player }));
                    if (State.GetPlayer(player).CardsDrawnThisTurn > drawnBefore) ctx.Results.PlayersWhoDrew.Add(player);
                    ctx.Results.Drawn += State.GetPlayer(player).CardsDrawnThisTurn - drawnBefore;
                }
                break;
            case GainLife g:
                foreach (var player in PlayersFor(g.Who, ctx).ToList()) await GainLifeAsync(player, Eval(g.Amount, ctx));
                break;
            case LoseLife l:
                foreach (var player in PlayersFor(l.Who, ctx))
                {
                    int before = State.GetPlayer(player).Life;
                    ChangeLife(player, -Eval(l.Amount, ctx with { AffectedPlayer = player }));
                    ctx.Results.LifeLost += Math.Max(0, before - State.GetPlayer(player).Life);
                }
                break;
            case Destroy d:
                BeginSimultaneous();
                foreach (var card in CardsFor(d.What, ctx).Where(c => !c.Has(Keyword.Indestructible) && !(d.ExceptCreatedThisWay && ctx.Results.Created.Contains(c.Id))).ToList())
                {
                    if (await SurvivesDestructionAsync(card, !d.CantBeRegenerated)) continue;
                    bool creature = card.IsCreature;
                    await MoveCardAsync(card.Id, Zone.Graveyard);
                    ctx.Results.Destroyed++;
                    if (card.Zone == Zone.Graveyard) ctx.Results.DestroyedCards.Add(card.Id);
                    Emit(new PermanentDestroyed(card.Id));
                    if (creature) Emit(new CreatureDied(card.Id));
                }
                EndSimultaneous();
                break;
            case ExileIt x:
            {
                var exiling = x.What.Kind == SubjectKind.Triggered && ctx.Trigger is { Subject: { } tid } ti && State.GetCard(tid) is { } tc && tc.Version == ti.SubjectVersion
                    ? new[] { tc }
                    : x.What.Kind == SubjectKind.Self && SourceIn(ctx, Zone.Graveyard) ? new[] { ctx.Source } // "exile this card from your graveyard"
                    : CardsFor(x.What, ctx).ToArray();
                foreach (var card in exiling.Where(c => !(x.ExceptCreatedThisWay && ctx.Results.Created.Contains(c.Id))))
                {
                    await MoveCardAsync(card.Id, Zone.Exile);
                    ctx.Results.Exiled.Add(card.Id);
                    if (x.WithCounter is { } kind && card.Zone == Zone.Exile) card.Counters[kind] = card.CounterCount(kind) + 1;
                    if (x.Linked && card.Zone == Zone.Exile) card.ExiledWith = (ctx.Source.Id, ctx.SourceVersion ?? ctx.Source.Version);
                    if (x.PlayableThisTurn && card.Zone == Zone.Exile) State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber));
                    if (x.OwnerMayPlayUntilEndOfTheirNextTurn && card.Zone == Zone.Exile) OwnerMayPlayUntilEndOfTheirNextTurn(card);
                    if (x.CastThisTurnAnyManaExileInstead && card.Zone == Zone.Exile)
                        State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, State.TurnNumber)
                        {
                            CastOnly = true, AnyManaType = true, ExileInstead = true,
                        });
                    if (x.CastableUntilEndOfNextTurn && card.Zone == Zone.Exile)
                        State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, ctx.Controller, int.MaxValue)
                        {
                            CastOnly = true, UntilEndOfNextTurnOf = ctx.Controller, MadeOnTurn = State.TurnNumber,
                        });
                }
                break;
            }
            default:
                return false;
        }
        return true;
    }
}
