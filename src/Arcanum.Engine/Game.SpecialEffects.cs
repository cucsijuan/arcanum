// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Effects of single cards that need more than the general vocabulary: one method each.</summary>
public sealed partial class Game
{
    /// <summary>The controller may cast one matching spell card from their graveyard without paying its mana cost, as the effect resolves.</summary>
    private async Task CastFromGraveyardFreeAsync(CastFromGraveyardFree cfg, EffectContext ctx)
    {
        var who = ctx.Controller;
        int max = Eval(cfg.MaxManaValue, ctx);
        var options = State.GetPlayer(who).Graveyard.Select(State.GetCard)
            .Where(c => !c.Is(CardType.Land) && ManaValueOf(c) <= max && Matches(cfg.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)
                        && HasLegalTargets(CastingTargets(c.Definition), who, c.Id)).ToList();
        if (options.Count == 0) return;
        var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
            $"{ctx.Source.Name}: you may cast one of these from your graveyard without paying its mana cost", ctx.Source.Id,
            options.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList(), 0, 1, CardChoicePurpose.Keep));
        if (pick.Count != 1 || options.All(c => c.Id != pick[0])) return;
        // "If that spell would be put into your graveyard, exile it instead" (not when it would go anywhere else).
        if (await CastNowWithoutPayingAsync(State.GetPlayer(who), pick[0]) && cfg.ExileAfter)
            State.ExileInsteadOfGraveyard.Add((pick[0], State.GetCard(pick[0]).Version));
    }

    /// <summary>Each such player chooses a creature they control and exiles it, then exiles their graveyard.</summary>
    private async Task ExilesCreatureAndGraveyardAsync(ExilesCreatureAndGraveyard e, EffectContext ctx)
    {
        foreach (var player in PlayersFor(e.Who, ctx).ToList())
        {
            var creatures = State.PermanentsControlledBy(player).Where(c => c.IsCreature).ToList();
            if (creatures.Count > 0)
            {
                var pick = creatures.Count == 1 ? new[] { creatures[0].Id } : await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                    $"{ctx.Source.Name}: exile a creature you control", ctx.Source.Id, creatures.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 1, 1, CardChoicePurpose.Sacrifice));
                Require(pick.Count == 1 && creatures.Any(c => c.Id == pick[0]), "Choose a creature you control.");
                await MoveCardAsync(pick[0], Zone.Exile);
                if (State.GetCard(pick[0]).Zone == Zone.Exile) ctx.Results.Exiled.Add(pick[0]);
            }
            foreach (var id in State.GetPlayer(player).Graveyard.ToList())
            {
                await MoveCardAsync(id, Zone.Exile);
                if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
            }
        }
    }

    /// <summary>A card at random from the controller's graveyard is exiled; they may play it this turn.</summary>
    private async Task ExileRandomFromGraveyardPlayableAsync(EffectContext ctx)
    {
        var graveyard = State.GetPlayer(ctx.Controller).Graveyard;
        if (graveyard.Count == 0) return;
        var random = graveyard[Rng.Next(graveyard.Count)];
        await MoveCardAsync(random, Zone.Exile);
        var card = State.GetCard(random);
        if (card.Zone != Zone.Exile) return;
        ctx.Results.Exiled.Add(random);
        State.PlayableFromExile.Add(new PlayableFromExile(random, card.Version, ctx.Controller, State.TurnNumber));
    }

    /// <summary>One card of each color, each searched for in turn (a multicolored card can be the card of one of its colors); then shuffle.</summary>
    private async Task SearchOneOfEachColorAsync(SearchOneOfEachColor s, EffectContext ctx)
    {
        var who = ctx.Controller;
        var player = State.GetPlayer(who);
        var found = new List<CardId>();
        foreach (var color in s.Colors)
        {
            var options = player.Library.Where(id => !found.Contains(id) && State.GetCard(id).Colors.Contains(color)).ToList();
            if (options.Count == 0) continue;
            var name = ColorNames[Array.IndexOf(ColorLetters, color)];
            var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                $"Search your library: choose a {name} card", ctx.Source.Id, options.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToHand));
            Require(pick.All(options.Contains), "Choose a card of that color.");
            found.AddRange(pick);
        }
        foreach (var id in found) await MoveCardAsync(id, s.To);
        ctx.Results.Found.AddRange(found);
        Shuffle(player);
    }

    /// <summary>The controller chooses N cards in their graveyard (all, if fewer) and puts them on top of their library in the order they choose.</summary>
    private async Task GraveyardToLibraryTopAsync(GraveyardToLibraryTop g, EffectContext ctx)
    {
        var who = ctx.Controller;
        var graveyard = State.GetPlayer(who).Graveyard.ToList();
        int n = Math.Min(g.Count, graveyard.Count);
        if (n == 0) return;
        var chosen = n == graveyard.Count ? graveyard : (await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
            $"{ctx.Source.Name}: choose {n} cards to put on top of your library", ctx.Source.Id, graveyard.Select(id => ViewBuilder.Card(State, id, who)).ToList(), n, n, CardChoicePurpose.Keep))).ToList();
        Require(chosen.Count == n && chosen.Distinct().Count() == n && chosen.All(graveyard.Contains), $"Choose {n} cards from your graveyard.");
        // The order: the first card chosen ends up on top.
        var order = chosen.Count > 1 ? (await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
            "Order them: the first chosen goes on top", ctx.Source.Id, chosen.Select(id => ViewBuilder.Card(State, id, who)).ToList(), chosen.Count, chosen.Count, CardChoicePurpose.Order))).ToList() : chosen;
        if (order.Count != chosen.Count || !order.All(chosen.Contains)) order = chosen;
        for (int i = order.Count - 1; i >= 0; i--) await MoveCardAsync(order[i], Zone.Library);
    }

    /// <summary>The top card: to the hand (revealed) if it matches and the player wants; otherwise the player may put it into their graveyard.</summary>
    private async Task LookTopToHandOrGraveyardAsync(LookTopToHandOrGraveyard l, EffectContext ctx)
    {
        var who = ctx.Controller;
        var player = State.GetPlayer(who);
        if (player.Library.Count == 0) return;
        var top = State.GetCard(player.Library[0]);
        top.KnownTo.Add(who);
        if (Matches(l.Filter with { Controller = ControllerFilter.Any }, top, who, ctx.Source, who)
            && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Reveal {top.Name} and put it into your hand?", top.Id)))
        {
            Emit(new Events.CardsRevealed(who, new[] { top.Id }));
            await MoveCardAsync(top.Id, Zone.Hand);
            return;
        }
        if (await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Put {top.Name} into your graveyard?", top.Id)))
            await MoveCardAsync(top.Id, Zone.Graveyard);
    }

    /// <summary>The controller chooses a source (a permanent or a spell); the next damage it would deal them this turn is prevented, and the reflexive ability waits.</summary>
    private async Task PreventNextFromChosenSourceAsync(PreventNextFromChosenSource p, EffectContext ctx)
    {
        var who = ctx.Controller;
        var sources = State.Battlefield.Concat(State.Stack.OfType<SpellOnStack>().Select(s => s.Card)).Distinct().Where(id => id != ctx.Source.Id).ToList();
        if (sources.Count == 0) return;
        var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
            $"{ctx.Source.Name}: choose a source", ctx.Source.Id, sources.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 1, 1, CardChoicePurpose.DamageSource));
        Require(pick.Count == 1 && sources.Contains(pick[0]), "Choose a source.");
        var source = State.GetCard(pick[0]);
        Emit(new Events.ChoiceMade(ctx.Source.Id, source.Name));
        State.PreventionShields.Add(new PreventionShield(State.TurnNumber, false)
        {
            DealtBy = (source.Id, source.Version), ToPlayer = who, NextTimeOnly = true, Controller = who, SourceCard = ctx.Source.Id, Id = NewTimestamp(),
            WhenPrevented = new TriggeredAbility { Trigger = TriggerEvent.Reflexive, Effects = p.WhenPrevented, Text = p.Text },
        });
    }

    /// <summary>For each color in WUBRG order, the controller picks a matching permanent of theirs of that color (if any) and puts a +1/+1 counter on it.</summary>
    private async Task CounterPerColorWinOnFiveAsync(CounterPerColorWinOnFive c, EffectContext ctx)
    {
        var who = ctx.Controller;
        var given = new HashSet<CardId>();
        foreach (var color in ColorLetters)
        {
            var options = State.PermanentsControlledBy(who).Where(p => p.Colors.Contains(color) && Matches(c.Filter with { Controller = ControllerFilter.Any }, p, who, ctx.Source, who)).ToList();
            if (options.Count == 0) continue;
            var name = ColorNames[Array.IndexOf(ColorLetters, color)];
            var pick = options.Count == 1 ? new[] { options[0].Id } : await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                $"{ctx.Source.Name}: put a +1/+1 counter on a {name} one", ctx.Source.Id, options.Select(o => ViewBuilder.Card(State, o.Id, who)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && options.Any(o => o.Id == pick[0]), "Choose one of them.");
            var chosen = State.GetCard(pick[0]);
            int before = chosen.CounterCount(CounterKind.PlusOnePlusOne);
            PutCounters(chosen, CounterKind.PlusOnePlusOne, 1, who);
            await ResolvePendingCountersAsync();
            if (chosen.CounterCount(CounterKind.PlusOnePlusOne) > before) given.Add(chosen.Id);
        }
        if (given.Count >= 5 && !CantWin(who))
            foreach (var opponent in State.OpponentsOf(who).ToList())
                if (!CantLose(opponent)) Lose(opponent, $"{State.GetPlayer(who).Name} won the game ({ctx.Source.Name})");
    }

    /// <summary>For each opponent, a tapped token copy of the card exiled with the source, attacking that opponent; sacrificed at the controller's next end step.</summary>
    private async Task ExiledCopyAttackingEachOpponentAsync(EffectContext ctx)
    {
        if (State.Combat is not { } combat) return;
        foreach (var opponent in State.ApnapOrder().Where(o => State.OpponentsOf(ctx.Controller).Contains(o)).ToList())
        {
            int before = ctx.Results.Created.Count;
            await ApplyAsync(new CreateTokenCopy(new Subject(SubjectKind.ExiledWithSource), 1) { Tapped = true, SacrificeAtYourNextEndStep = true }, ctx);
            foreach (var made in ctx.Results.Created.Skip(before).Where(id => State.GetCard(id).Zone == Zone.Battlefield).ToList())
                combat.Attacks.Add(new AttackInfo { Attacker = made, Defender = opponent });
        }
    }

    /// <summary>The 4/4 white Angel creature token with flying and vigilance that replaces creature tokens.</summary>
    private static readonly CardDefinition AngelToken = new()
    {
        Name = "Angel",
        Types = CardType.Creature,
        Subtypes = new[] { "Angel" },
        Colors = new[] { "W" },
        Power = 4,
        Toughness = 4,
        Keywords = new[] { "Flying", "Vigilance" },
        IsToken = true,
    };

    /// <summary>The controller picks an opponent (the only one, in a two-player game), who chooses the card; it goes to the controller's hand.</summary>
    private async Task OpponentChoosesFromYourGraveyardAsync(OpponentChoosesFromYourGraveyard o, EffectContext ctx)
    {
        var who = ctx.Controller;
        var options = State.GetPlayer(who).Graveyard.Select(State.GetCard).Where(c => Matches(o.Filter with { Controller = ControllerFilter.Any }, c, who, ctx.Source, who)).ToList();
        if (options.Count == 0) return;
        if (await ChooseOpponentAsync(who, ctx.Source, $"{ctx.Source.Name}: choose an opponent to choose the card") is not { } chooser) return;
        var pick = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest(
            $"{ctx.Source.Name}: choose a card from {State.GetPlayer(who).Name}'s graveyard to return to their hand", ctx.Source.Id,
            options.Select(c => ViewBuilder.Card(State, c.Id, chooser)).ToList(), 1, 1, CardChoicePurpose.ToHand));
        Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the cards.");
        await MoveCardAsync(pick[0], Zone.Hand);
    }

    /// <summary>The top three: the controller sends one to the hand, one to the bottom and one to exile (playable this turn); fewer cards fill the slots in that order.</summary>
    private async Task HandBottomExileOfThreeAsync(EffectContext ctx)
    {
        var who = ctx.Controller;
        var player = State.GetPlayer(who);
        var looked = player.Library.Take(3).ToList();
        foreach (var id in looked) State.GetCard(id).KnownTo.Add(who);
        async Task<CardId?> Pick(string where, List<CardId> left)
        {
            if (left.Count == 0) return null;
            if (left.Count == 1) return left[0];
            var p = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest($"{ctx.Source.Name}: choose the card to put {where}", ctx.Source.Id,
                left.Select(id => ViewBuilder.Card(State, id, who)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(p.Count == 1 && left.Contains(p[0]), "Choose one of the cards.");
            return p[0];
        }
        var left = looked.ToList();
        var toHand = await Pick("into your hand", left);
        if (toHand is { } h) left.Remove(h);
        var toBottom = await Pick("on the bottom of your library", left);
        if (toBottom is { } b) left.Remove(b);
        if (toHand is { } hand) await MoveCardAsync(hand, Zone.Hand);
        if (toBottom is { } bottom) await MoveCardAsync(bottom, Zone.Library, toBottom: true);
        foreach (var id in left)
        {
            await MoveCardAsync(id, Zone.Exile);
            if (State.GetCard(id).Zone == Zone.Exile) State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, who, State.TurnNumber));
        }
    }

    /// <summary>In turn order each player chooses creatures they control with total power at most N; then every other creature is sacrificed at once.</summary>
    private async Task EachKeepsTotalPowerAtMostAsync(EachKeepsTotalPowerAtMost e, EffectContext ctx)
    {
        var doomed = new List<CardId>();
        foreach (var player in State.ApnapOrder().ToList())
        {
            var creatures = State.PermanentsControlledBy(player).Where(c => c.IsCreature).ToList();
            IReadOnlyList<CardId> kept = Array.Empty<CardId>();
            for (int attempt = 0; creatures.Count > 0; attempt++)
            {
                kept = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest(
                    $"{ctx.Source.Name}: choose creatures to keep, total power {e.Total} or less (the rest are sacrificed)", ctx.Source.Id,
                    creatures.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 0, creatures.Count, CardChoicePurpose.Keep));
                if (kept.All(id => creatures.Any(c => c.Id == id)) && kept.Distinct().Count() == kept.Count
                    && kept.Sum(id => Math.Max(0, State.GetCard(id).Power)) <= e.Total) break;
                Require(attempt < 2, $"Choose creatures with total power {e.Total} or less.");
            }
            doomed.AddRange(creatures.Select(c => c.Id).Where(id => !kept.Contains(id)));
        }
        BeginSimultaneous();
        foreach (var id in doomed) await SacrificePermanentAsync(id);
        EndSimultaneous();
    }

    /// <summary>Territorial attack: a random opponent this creature didn't attack during its controller's last combat, or it's tapped.</summary>
    private void AttackRandomOpponentAsTerritorial(EffectContext ctx)
    {
        var self = ctx.Source;
        if (self.Zone != Zone.Battlefield || (ExpectedSourceVersion(ctx) is { } v && v != self.Version)) return;
        var controller = State.GetPlayer(self.Controller);
        // "Your last combat": the combat before this one on its controller's turn (this trigger fires as a combat begins).
        int last = State.Step == Step.BeginCombat && State.ActivePlayer == controller.Id ? controller.PreviousCombat : controller.LatestCombat;
        var eligible = State.OpponentsOf(self.Controller)
            .Where(o => !(self.LastPlayerAttacked is { } attacked && attacked.Combat == last && attacked.Player == o)).ToList();
        if (eligible.Count == 0)
        {
            Tap(self);
            return;
        }
        var chosen = eligible[Rng.Next(eligible.Count)];
        Emit(new Events.ChoiceMade(self.Id, $"{State.GetPlayer(chosen).Name} (at random)"));
        State.AttackPlayerThisCombat.Add((self.Id, self.Version, chosen, State.CombatSerial));
    }

    /// <summary>Chooses an exiled card an opponent owns with that kind of counter; the controller may play it this turn without paying its mana cost.</summary>
    private async Task PlayOpponentsExiledWithCounterFreeAsync(PlayOpponentsExiledWithCounterFree poe, EffectContext ctx)
    {
        var who = ctx.Controller;
        var opponents = State.OpponentsOf(who).ToList();
        var options = State.Cards.Values.Where(c => c.Zone == Zone.Exile && opponents.Contains(c.Owner) && c.CounterCount(poe.Counter) > 0 && !c.Definition.IsToken).ToList();
        if (options.Count == 0) return;
        var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
            $"{ctx.Source.Name}: choose an exiled card to play this turn without paying its mana cost", ctx.Source.Id,
            options.Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
        Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of the exiled cards.");
        var chosen = State.GetCard(pick[0]);
        Emit(new Events.ChoiceMade(ctx.Source.Id, chosen.Name));
        State.PlayableFromExile.Add(new PlayableFromExile(chosen.Id, chosen.Version, who, State.TurnNumber, WithoutPaying: true));
    }

    /// <summary>An opponent picks one of the exiled found cards to shuffle into the library; the controller may cast the others free, one at a time, in any order.</summary>
    private async Task OpponentShufflesOneCastRestFreeAsync(EffectContext ctx)
    {
        var who = ctx.Controller;
        var exiled = ctx.Results.Found.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList();
        if (exiled.Count == 0) return;
        if (await ChooseOpponentAsync(who, ctx.Source, $"{ctx.Source.Name}: choose an opponent to choose the card") is { } chooser)
        {
            var pick = await ControllerOf(chooser).ChooseCardsAsync(ViewFor(chooser), new CardChoiceRequest(
                $"{ctx.Source.Name}: choose the card {State.GetPlayer(who).Name} shuffles into their library", ctx.Source.Id,
                exiled.Select(id => ViewBuilder.Card(State, id, chooser, reveal: true)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && exiled.Contains(pick[0]), "Choose one of the cards.");
            Emit(new Events.ChoiceMade(ctx.Source.Id, $"{State.GetCard(pick[0]).Name} (by {State.GetPlayer(chooser).Name}, shuffled into the library)"));
            await MoveCardAsync(pick[0], Zone.Library);
            Shuffle(State.GetPlayer(who));
            exiled.Remove(pick[0]);
        }
        // "You may cast the other cards without paying their mana costs": during this resolution, lands can't be played this way.
        while (true)
        {
            var castable = exiled.Select(State.GetCard).Where(c => c.Zone == Zone.Exile && !c.Is(CardType.Land)).ToList();
            if (castable.Count == 0) return;
            var next = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                $"{ctx.Source.Name}: you may cast one of these without paying its mana cost", ctx.Source.Id,
                castable.Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList(), 0, 1, CardChoicePurpose.Keep));
            if (next.Count != 1 || castable.All(c => c.Id != next[0])) return;
            exiled.Remove(next[0]);
            var permission = new PlayableFromExile(next[0], State.GetCard(next[0]).Version, who, State.TurnNumber, WithoutPaying: true) { CastOnly = true };
            State.PlayableFromExile.Add(permission);
            try { await CastNowWithoutPayingAsync(State.GetPlayer(who), next[0]); }
            finally { State.PlayableFromExile.Remove(permission); }
        }
    }
}
