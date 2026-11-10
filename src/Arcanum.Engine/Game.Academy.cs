// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Quantities and effects of the academy set (see <c>CardScript.Academy.cs</c>).</summary>
public sealed partial class Game
{
    private int EvalAcademy(Quantity q, EffectContext ctx) => q.Kind switch
    {
        QuantityKind.CardsYouDrewThisTurn => State.GetPlayer(ctx.Controller).CardsDrawnThisTurn,
        QuantityKind.TriggeredSpellX => ctx.Trigger is { Subject: { } spell } about
            ? (State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == spell) ?? _spellsOnStack.GetValueOrDefault((spell, about.SubjectVersion)))?.X ?? 0
            : 0,
        QuantityKind.ManaSpentOnSource => ctx.Source.ManaSpent,
        QuantityKind.ManaSpentOnTarget => ctx.ChosenAt(q.Index)?.Card is { } target ? State.GetCard(target).ManaSpent : 0,
        // 2ˣ, capped where it no longer fits (no library holds that many cards).
        QuantityKind.TwoToThe => q.Parts is [var exponent] && Eval(exponent, ctx) is var power and >= 0 ? power >= 30 ? 1 << 30 : 1 << power : 0,
        QuantityKind.AffectedHalfLifeRoundedDown => Math.Max(0, State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).Life) / 2,
        QuantityKind.AffectedHalfHandRoundedDown => State.GetPlayer(ctx.AffectedPlayer ?? ctx.Controller).Hand.Count / 2,
        QuantityKind.AffectedHalfPermanentsRoundedDown => State.PermanentsControlledBy(ctx.AffectedPlayer ?? ctx.Controller).Count() / 2,
        QuantityKind.TriggeredSpellColorsSpent => ctx.Trigger?.Subject is { } cast
            ? State.GetCard(cast).SpentManaTypes.Where(t => t is Mana.ManaType.White or Mana.ManaType.Blue or Mana.ManaType.Black or Mana.ManaType.Red or Mana.ManaType.Green).Distinct().Count()
            : 0,
        QuantityKind.TargetHandSize => ctx.ChosenAt(q.Index)?.Player is { } who ? State.GetPlayer(who).Hand.Count : 0,
        QuantityKind.DifferentlyNamedYouControl => State.PermanentsControlledBy(ctx.Controller)
            .Where(c => q.Filter is null || Matches(q.Filter, c, c.Controller, ctx.Source, ctx.Controller)).Select(c => c.Name).Where(n => n.Length > 0).Distinct().Count(),
        QuantityKind.PermanentsPutIntoGraveyardThisTurn => State.PermanentsPutIntoGraveyardThisTurn,
        QuantityKind.YourNontokenCreaturesDestroyedThisWay => ctx.Results.DestroyedCards.Select(State.GetCard)
            .Count(c => !c.PrintedDefinition.IsToken && c.LastKnownInfo is { } was && was.Controller == ctx.Controller && (was.Types & Cards.CardType.Creature) != 0),
        _ => throw new InvalidOperationException($"Not an academy quantity: {q.Kind}"),
    };

    private async Task ApplyAcademyAsync(AcademyEffect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case WhenYouNextCastThisTurn whenNext:
                State.WhenNextCast.Add((new DelayedTrigger(ctx.Source.Id, whenNext.Ability, ctx.Controller, 0) { SourceVersion = ctx.Source.Version }, whenNext.Spells, State.TurnNumber));
                break;
            case DrawTwoPayOrReturnDrawn:
            {
                var player = State.GetPlayer(ctx.Controller);
                if (!await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id), new YesNoRequest($"{ctx.Source.Name}: draw two additional cards?", ctx.Source.Id))) break;
                await DrawAsync(player.Id, 2);
                var drawn = player.Hand.Where(player.DrawnThisTurn.Contains).ToList();
                var chosen = drawn.Count <= 2 ? drawn : (await ControllerOf(player.Id).ChooseCardsAsync(ViewFor(player.Id), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose two cards in your hand drawn this turn", ctx.Source.Id,
                    drawn.Select(id => ViewBuilder.Card(State, id, player.Id)).ToList(), 2, 2, CardChoicePurpose.Keep))).ToList();
                Require(chosen.Count == Math.Min(2, drawn.Count) && chosen.All(drawn.Contains) && chosen.Distinct().Count() == chosen.Count, "Choose two cards drawn this turn.");
                foreach (var id in chosen)
                {
                    var card = State.GetCard(id);
                    bool canPay = player.Life >= 4 && !player.CantLoseLifeNow(State.TurnNumber); // rule 119.4
                    int pick = !canPay ? 1 : await ControllerOf(player.Id).ChooseOptionAsync(ViewFor(player.Id), new OptionRequest(
                        $"{ctx.Source.Name}: {card.Name}", id, new List<string> { "Pay 4 life", "Put it on top of your library" }, OptionKind.Other));
                    Require(pick is 0 or 1, "Choose one of the options.");
                    if (pick == 0) ChangeLife(player.Id, -4);
                    else await MoveCardAsync(id, Zone.Library);
                }
                break;
            }
            case ExileFromHandUntilLeaves fromHand:
            {
                foreach (var whose in PlayersFor(fromHand.Whose, ctx).ToList())
                {
                    var hand = State.GetPlayer(whose).Hand.ToList();
                    Look(ctx.Controller, hand);
                    var options = hand.Where(id => Matches(fromHand.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), whose, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0 || !SourceOnBattlefield(ctx)) continue; // it would return at once
                    var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: you may exile a card from this hand", ctx.Source.Id,
                        hand.Select(id => ViewBuilder.Card(State, id, ctx.Controller, reveal: true)).Where(v => options.Contains(v.Id)).ToList(), 0, 1, CardChoicePurpose.Discard));
                    Require(pick.Count <= 1 && pick.All(options.Contains), "Choose one of the cards.");
                    foreach (var id in pick)
                    {
                        await MoveCardAsync(id, Zone.Exile);
                        var exiled = State.GetCard(id);
                        if (exiled.Zone == Zone.Exile) State.LinkedExiles.Add(new LinkedExile(ctx.Source.Id, ctx.Source.Version, id, exiled.Version) { ToHand = true });
                    }
                }
                break;
            }
            case WarpExile:
                foreach (var warped in CardsFor(Subject.Triggered, ctx).ToList())
                {
                    await MoveCardAsync(warped.Id, Zone.Exile);
                    if (warped.Zone == Zone.Exile && !warped.Definition.IsToken)
                        State.PlayableFromExile.Add(new PlayableFromExile(warped.Id, warped.Version, warped.Owner, int.MaxValue)
                            { CastOnly = true, LaterTurnOnly = true, MadeOnTurn = State.TurnNumber });
                }
                break;
            case SolveCase:
                if (SourceOnBattlefield(ctx) && !ctx.Source.Solved)
                {
                    ctx.Source.Solved = true;
                    Emit(new ChoiceMade(ctx.Source.Id, "solved"));
                }
                break;
            case CastFreeNow castNow:
            {
                var cards = (castNow.What.Kind == SubjectKind.ExiledThisWay ? ctx.Results.Exiled : CardsFor(castNow.What, ctx).Select(c => c.Id)).ToList();
                await CastAnyNumberFreeAsync(ctx, cards, castNow.Most, "from among them");
                break;
            }
            case RevealToHandLoseLifeRepeat:
            {
                var player = State.GetPlayer(ctx.Controller);
                do
                {
                    if (player.Library.Count == 0) break;
                    var top = State.GetCard(player.Library[0]);
                    Emit(new CardsRevealed(player.Id, new[] { top.Id }));
                    int lost = top.ManaValue;
                    await MoveCardAsync(top.Id, Zone.Hand);
                    if (lost > 0) ChangeLife(player.Id, -lost);
                } while (player.Library.Count > 0 && await ControllerOf(player.Id).ChooseYesNoAsync(ViewFor(player.Id),
                             new YesNoRequest($"{ctx.Source.Name}: reveal the next card and lose life equal to its mana value? (life {player.Life})", ctx.Source.Id)));
                break;
            }
            case CantLoseGameThisTurn:
                State.GetPlayer(ctx.Controller).CantLoseGameTurn = State.TurnNumber;
                break;
            case DamageCantReduceLifeBelowOneThisTurn:
                State.GetPlayer(ctx.Controller).DamageCantReduceLifeBelowOneTurn = State.TurnNumber;
                break;
            case YourSpellsCantBeCounteredThisTurn:
                State.GetPlayer(ctx.Controller).SpellsCantBeCounteredTurn = State.TurnNumber;
                break;
            case HexproofFromColorsThisTurn hexproof:
            {
                var you = State.GetPlayer(ctx.Controller);
                you.HexproofFromColorsThisTurn = (State.TurnNumber, you.HexproofFromColorsThisTurn.Turn == State.TurnNumber
                    ? you.HexproofFromColorsThisTurn.Colors.Union(hexproof.Colors).ToList() : hexproof.Colors);
                foreach (var permanent in State.PermanentsControlledBy(ctx.Controller))
                    permanent.HexproofFromColorsThisTurn = (State.TurnNumber, permanent.HexproofFromColorsThisTurn.Turn == State.TurnNumber
                        ? permanent.HexproofFromColorsThisTurn.Colors.Union(hexproof.Colors).ToList() : hexproof.Colors);
                break;
            }
            case ControllersSearchBasicPerExiled:
            {
                var perPlayer = ctx.Results.Exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && (c.LastKnownInfo?.Types ?? c.Types).HasFlag(Cards.CardType.Creature))
                    .GroupBy(c => c.LastKnownInfo?.Controller ?? c.Owner).ToDictionary(g => g.Key, g => g.Count());
                var basic = new ObjectFilter(Cards.CardType.Land) { Supertype = Cards.Supertype.Basic };
                foreach (var searcher in State.ApnapOrder().Where(perPlayer.ContainsKey))
                    await ApplyAsync(new SearchLibrary(basic, perPlayer[searcher], Zone.Battlefield, Tapped: true), ctx with { Controller = searcher });
                break;
            }
            case ChooseLandCardName:
                if (await ChooseCardNameAsync(ctx.Controller, ctx.Source.Id, $"{ctx.Source.Name}: choose a land card name", landOnly: true) is not { } named) break;
                ctx.Source.ChosenName = named;
                Emit(new ChoiceMade(ctx.Source.Id, named));
                break;
            case AtYourNextMainPhase next:
                State.AtYourNextMainPhase.Add(new DelayedTrigger(ctx.Source.Id, next.Ability, ctx.Controller, next.Amount is { } amount ? Eval(amount, ctx) : 0)
                {
                    SourceVersion = ExpectedSourceVersion(ctx), SourceTransforms = ctx.Source.TransformCount,
                });
                break;
            case WhenDealsCombatDamageThisCombat whenDamage:
                foreach (var creature in CardsFor(whenDamage.About, ctx).Where(c => c.Zone == Zone.Battlefield).ToList())
                    State.WhenDealsCombatDamageThisCombat.Add((new DelayedTrigger(ctx.Source.Id, whenDamage.Ability, ctx.Controller, 0)
                    {
                        SourceVersion = ExpectedSourceVersion(ctx), SourceTransforms = ctx.Source.TransformCount, AboutCard = (creature.Id, creature.Version),
                    }, State.CombatSerial));
                break;
            case AnimateEnchantedCard { Sacrifice: true }:
                // The Aura that animated it, as it left the battlefield (its next version), or still there.
                if (ctx.Source.Animated is { } animated && ctx.Source.Version - animated.AuraVersion is 0 or 1
                    && State.GetCard(animated.Creature) is { Zone: Zone.Battlefield } doomed && doomed.Version == animated.CreatureVersion)
                    await SacrificePermanentAsync(doomed.Id);
                break;
            case AnimateEnchantedCard:
            {
                if (ctx.Source.Zone != Zone.Battlefield || !IsSourceObject(ctx) || ctx.Source.AttachedTo is not { } enchanted) break;
                var card = State.GetCard(enchanted);
                if (card.Zone != Zone.Graveyard) break;
                await MoveCardAsync(card.Id, Zone.Battlefield, controller: ctx.Controller);
                if (card.Zone != Zone.Battlefield || ctx.Source.Zone != Zone.Battlefield) break;
                ctx.Source.AttachedTo = card.Id;
                ctx.Source.Timestamp = 0; // a new timestamp as it becomes attached (rule 613.7e)
                ctx.Source.Animated = (ctx.Source.Version, card.Id, card.Version);
                break;
            }
            case RevealTopDuel duel:
            {
                if (PlayersFor(duel.Opponent, ctx).Cast<PlayerId?>().FirstOrDefault() is not { } rival || rival == ctx.Controller) break;
                var duelists = new[] { ctx.Controller, rival };
                var revealed = duelists.ToDictionary(p => p, p => State.GetPlayer(p).Library.Count > 0 ? State.GetPlayer(p).Library[0] : (CardId?)null);
                foreach (var p in duelists)
                    if (revealed[p] is { } top) Emit(new CardsRevealed(p, new[] { top }));
                // Each loses life equal to the mana value of the card the other revealed (simultaneously).
                var losses = duelists.ToDictionary(p => p, p => revealed[p == ctx.Controller ? rival : ctx.Controller] is { } other ? State.GetCard(other).ManaValue : 0);
                foreach (var p in duelists.Where(p => losses[p] > 0)) ChangeLife(p, -losses[p]);
                foreach (var p in duelists)
                    if (revealed[p] is { } top && State.GetCard(top).Zone == Zone.Library) await MoveCardAsync(top, Zone.Hand);
                break;
            }
            case ReturnCopiedCardToGraveyard:
                if (ctx.Source.CopiedFromCard is { } copied && State.GetCard(copied.Card) is { Zone: Zone.Exile } exiledOriginal && exiledOriginal.Version == copied.Version)
                    await MoveCardAsync(exiledOriginal.Id, Zone.Graveyard);
                break;
            case CastExiledFreeThisTurnBottomInstead castExiled:
                foreach (var exiledCard in CardsFor(castExiled.What, ctx).Where(c => c.Zone == Zone.Exile && !c.Is(Cards.CardType.Land)).ToList())
                    State.PlayableFromExile.Add(new PlayableFromExile(exiledCard.Id, exiledCard.Version, ctx.Controller, State.TurnNumber, WithoutPaying: true) { BottomInsteadOfGraveyard = true });
                break;
            case CopyForEachOtherCreatureItCouldTarget:
            {
                if (ctx.Trigger?.Subject is not { } spellId || State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == spellId) is not { } spell) break;
                var spellCard = State.GetCard(spellId);
                if (spellCard.Version != ctx.Trigger.SubjectVersion) break;
                if ((spell.Ability ?? CastingTargets(spellCard.Definition)) is not { Targets: [var requirement] }) break;
                var legal = LegalTargets(requirement, spell.Controller, spellId).ToList();
                foreach (var other in State.PermanentsControlledBy(spell.Controller).Where(c => c.IsCreature && c.Id != ctx.Source.Id && legal.Contains(Target.Of(c.Id))).ToList())
                    await CopySpellAsync(spell, spell.Controller, fixedTargets: new[] { new ChosenTarget(Target.Of(other.Id), other.Version) });
                break;
            }
            case ExiledPilesOpponentChooses piles:
            {
                var player = State.GetPlayer(ctx.Controller);
                var faceDown = player.Library.Take(piles.Count).ToList();
                foreach (var id in faceDown) await MoveCardAsync(id, Zone.Exile, faceDown: true);
                var faceUp = player.Library.Take(piles.Count).ToList();
                foreach (var id in faceUp) await MoveCardAsync(id, Zone.Exile);
                if (faceDown.Count + faceUp.Count == 0) break;
                var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who picks the pile") ?? State.OpponentsOf(ctx.Controller).First();
                var labels = new[]
                {
                    $"Face-down pile ({faceDown.Count} cards)",
                    $"Face-up pile ({faceUp.Count}): {string.Join(", ", faceUp.Select(id => State.GetCard(id).Name))}",
                };
                int pick = await ControllerOf(opponent).ChooseOptionAsync(ViewFor(opponent),
                    new OptionRequest($"Choose the pile {player.Name} puts into their graveyard", ctx.Source.Id, labels, OptionKind.Other));
                Require(pick is 0 or 1, "Choose one of the piles.");
                var (toGraveyard, other) = pick == 0 ? (faceDown, faceUp) : (faceUp, faceDown);
                BeginSimultaneous();
                foreach (var id in toGraveyard.Where(id => State.GetCard(id).Zone == Zone.Exile)) await MoveCardAsync(id, Zone.Graveyard);
                EndSimultaneous();
                var kept = other.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
                Look(ctx.Controller, kept);
                var castable = kept.Select(State.GetCard).Where(c => !c.Is(Cards.CardType.Land) && CastableFreeNow(c, ctx.Controller)).ToList();
                if (castable.Count > 0)
                {
                    var cast = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                        $"{ctx.Source.Name}: you may cast a spell from among them without paying its mana cost", ctx.Source.Id,
                        castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                    Require(cast.Count <= 1 && cast.All(id => castable.Any(c => c.Id == id)), "Choose one of the cards.");
                    if (cast.Count == 1)
                    {
                        State.GetCard(cast[0]).FaceDown = false; // casting it reveals it
                        await CastNowWithoutPayingAsync(player, cast[0]);
                    }
                }
                foreach (var id in kept.Where(id => State.GetCard(id).Zone == Zone.Exile)) await MoveCardAsync(id, Zone.Hand);
                break;
            }
            case ExileTopAsYouChooseCastIfTotalAtMost dance:
            {
                var player = State.GetPlayer(ctx.Controller);
                var exiled = new List<CardId>();
                while (player.Library.Count > 0)
                {
                    int total = exiled.Sum(id => State.GetCard(id).ManaValue);
                    if (!await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                            new YesNoRequest($"{ctx.Source.Name}: exile the top card of your library? (total mana value so far {total})", ctx.Source.Id)))
                        break;
                    var top = player.Library[0];
                    await MoveCardAsync(top, Zone.Exile);
                    if (State.GetCard(top).Zone == Zone.Exile) exiled.Add(top);
                }
                if (exiled.Sum(id => State.GetCard(id).ManaValue) > dance.MaxTotal) break;
                await CastAnyNumberFreeAsync(ctx, exiled, int.MaxValue, "from among the exiled cards");
                break;
            }
            case EachExilesUntilNonlandOpponentPicks plargg:
            {
                var exiled = new List<CardId>();
                foreach (var p in State.ApnapOrder().ToList())
                {
                    var library = State.GetPlayer(p).Library;
                    while (library.Count > 0)
                    {
                        var top = library[0];
                        await MoveCardAsync(top, Zone.Exile);
                        if (State.GetCard(top).Zone != Zone.Exile) continue;
                        exiled.Add(top);
                        if (!State.GetCard(top).Is(Cards.CardType.Land)) break;
                    }
                }
                var nonland = exiled.Where(id => !State.GetCard(id).Is(Cards.CardType.Land)).ToList();
                if (nonland.Count == 0) break;
                var opponent = await ChooseOpponentAsync(ctx.Controller, ctx.Source, "Choose the opponent who picks a card") ?? State.OpponentsOf(ctx.Controller).First();
                var barred = await ControllerOf(opponent).ChooseCardsAsync(ViewFor(opponent), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose a nonland card exiled this way ({State.GetPlayer(ctx.Controller).Name} can't cast it)", ctx.Source.Id,
                    nonland.Select(id => ViewBuilder.Card(State, id, opponent, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
                Require(barred.Count == 1 && nonland.Contains(barred[0]), "Choose one of the nonland cards.");
                Emit(new ChoiceMade(barred[0], $"chosen by {State.GetPlayer(opponent).Name}"));
                await CastAnyNumberFreeAsync(ctx, exiled.Where(id => id != barred[0]).ToList(), plargg.Casts, "from among the other exiled cards");
                break;
            }
            case DoubleTriggeredSpellX:
            {
                int index = ctx.Trigger?.Subject is { } spellId ? State.Stack.FindIndex(s => s is SpellOnStack sos && sos.Card == spellId) : -1;
                if (index >= 0 && State.Stack[index] is SpellOnStack doubled && State.GetCard(doubled.Card).Version == ctx.Trigger!.SubjectVersion)
                    State.Stack[index] = doubled with { X = doubled.X * 2 };
                break;
            }
            case WhenYourCreatureDealsCombatDamageThisTurn yours:
            {
                var aboutCard = yours.About.Kind == SubjectKind.ExiledThisWay ? ctx.Results.Exiled.Select(State.GetCard).FirstOrDefault() : CardsFor(yours.About, ctx).FirstOrDefault();
                State.WhenYourCreatureDealsCombatDamageThisTurn.Add((new DelayedTrigger(ctx.Source.Id, yours.Ability, ctx.Controller, 0)
                {
                    AboutCard = aboutCard is null ? null : (aboutCard.Id, aboutCard.Version),
                }, State.TurnNumber));
                break;
            }
            case FlipCoinsSkipTurns flips:
            {
                int heads = 0;
                for (int i = 0; i < flips.Coins; i++) if (Rng.Next(2) == 0) heads++;
                Emit(new CoinsFlipped(ctx.Source.Id, ctx.Controller, flips.Coins, heads));
                foreach (var player in PlayersFor(flips.Who, ctx).ToList()) State.GetPlayer(player).TurnsToSkip += heads;
                break;
            }
            case MoveCountersOfKind move:
            {
                var from = CardsFor(move.From, ctx).FirstOrDefault();
                var to = CardsFor(move.To, ctx).FirstOrDefault();
                if (from is not { Zone: Zone.Battlefield } || to is not { Zone: Zone.Battlefield } || from.Id == to.Id) break;
                int n = Math.Min(from.CounterCount(move.Kind), Math.Max(0, Eval(move.Count, ctx)));
                if (n == 0) break;
                RemoveCountersFrom(from, move.Kind, n);
                PutCounters(to, move.Kind, n, ctx.Controller);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown academy effect {effect.GetType().Name}");
        }
    }

    /// <summary>"Its owner may play it until the end of their next turn": a permission for the exiled card's owner.</summary>
    private void OwnerMayPlayUntilEndOfTheirNextTurn(Card card) =>
        State.PlayableFromExile.Add(new PlayableFromExile(card.Id, card.Version, card.Owner, int.MaxValue)
        {
            UntilEndOfNextTurnOf = card.Owner, MadeOnTurn = State.TurnNumber,
        });

    /// <summary>Casts up to <paramref name="most"/> spells from among these exiled cards without paying their mana costs, one at a time, as the player chooses.</summary>
    private async Task CastAnyNumberFreeAsync(EffectContext ctx, IReadOnlyList<CardId> cards, int most, string from)
    {
        var caster = State.GetPlayer(ctx.Controller);
        var backedOut = new HashSet<CardId>();
        for (int cast = 0; cast < most;)
        {
            var castable = cards.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && !backedOut.Contains(c.Id) && !c.Is(Cards.CardType.Land)
                                                                  && CastableFreeNow(c, ctx.Controller)).ToList();
            if (castable.Count == 0) break;
            var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                $"{ctx.Source.Name}: you may cast a spell {from} without paying its mana cost{(most == int.MaxValue ? "" : $" ({most - cast} left)")}", ctx.Source.Id,
                castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
            if (pick.Count == 0) break;
            Require(pick.Count == 1 && castable.Any(c => c.Id == pick[0]), "Choose one of the cards.");
            if (await CastNowWithoutPayingAsync(caster, pick[0])) cast++;
            else backedOut.Add(pick[0]);
        }
    }

    /// <summary>The delayed "whenever that creature deals combat damage to a player this combat" abilities about <paramref name="source"/>.</summary>
    private void TriggerWhenDealsCombatDamage(Card source, PlayerId hurt, int amount)
    {
        if (source.IsCreature)
            foreach (var (delayed, _) in State.WhenYourCreatureDealsCombatDamageThisTurn.Where(w => w.Turn == State.TurnNumber && w.Trigger.Controller == source.Controller).ToList())
                _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller,
                    delayed.AboutCard is { } about ? new TriggerInfo(about.Card, about.Version, hurt, amount) : new TriggerInfo(Player: hurt, Amount: amount)));
        foreach (var (delayed, _) in State.WhenDealsCombatDamageThisCombat.Where(w => w.Combat == State.CombatSerial && w.Trigger.AboutCard == (source.Id, source.Version)).ToList())
            _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller, new TriggerInfo(source.Id, source.Version, hurt, amount))
            {
                SourceTransforms = delayed.SourceTransforms, SourceVersion = delayed.SourceVersion ?? -1,
            });
    }

    /// <summary>"At the beginning of your next main phase": the delayed abilities of the player whose main phase begins.</summary>
    private void ReleaseAtYourNextMainPhase(PlayerId active)
    {
        foreach (var delayed in State.AtYourNextMainPhase.Where(d => d.Controller == active).ToList())
        {
            State.AtYourNextMainPhase.Remove(delayed);
            _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller, new TriggerInfo(Amount: delayed.Amount))
            {
                SourceTransforms = delayed.SourceTransforms, SourceVersion = delayed.SourceVersion ?? -1,
            });
        }
    }
}
