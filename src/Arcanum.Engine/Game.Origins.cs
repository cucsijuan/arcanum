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
/// Effects and rules first needed by Magic Origins cards: sweeping hands, graveyards and permanents into libraries, casting
/// revealed cards while an effect resolves, naming a card and searching every zone for it, names that can't be cast, quantities
/// about the affected or revealed object, and mana abilities that remove any number of counters.
/// </summary>
public sealed partial class Game
{
    private async Task ApplyOriginsAsync(OriginsEffect effect, EffectContext ctx)
    {
        switch (effect)
        {
            case ShuffleIntoLibraries s:
                await ShuffleIntoLibrariesAsync(s, ctx);
                break;
            case EachPutsFromHand ep:
                await EachPutsFromHandAsync(ep, ctx);
                break;
            case RevealTopCastFree rc:
                await RevealTopCastFreeAsync(rc, ctx);
                break;
            case NameThenExileFromAllZones ne:
                await NameThenExileFromAllZonesAsync(ne, ctx);
                break;
            default:
                throw new NotSupportedException($"Effect {effect.GetType().Name} is not implemented.");
        }
    }

    /// <summary>The players a "each player …" effect reaches, in turn order (rule 101.4).</summary>
    private List<PlayerId> InTurnOrder(Subject who, EffectContext ctx)
    {
        var reached = PlayersFor(who, ctx).ToList();
        return State.ApnapOrder().Where(reached.Contains).ToList();
    }

    private async Task ShuffleIntoLibrariesAsync(ShuffleIntoLibraries s, EffectContext ctx)
    {
        var players = InTurnOrder(s.Who, ctx);
        // What each player puts into their library: cards from their hand and/or graveyard, permanents they own (wherever they are
        // controlled, tokens included).
        var moving = players.ToDictionary(p => p, p =>
        {
            var player = State.GetPlayer(p);
            var ids = new List<CardId>();
            if (s.Hand) ids.AddRange(player.Hand);
            if (s.Graveyard) ids.AddRange(player.Graveyard);
            if (s.Permanents) ids.AddRange(State.Battlefield.Where(b => State.GetCard(b).Owner == p));
            return ids.Select(id => (Id: id, From: State.GetCard(id).Zone, State.GetCard(id).Version)).ToList();
        });
        var all = moving.Values.SelectMany(x => x).ToList();
        // One event: the choices among replacement effects are made first, then everything moves at once.
        await PlanMovesAsync(all.Select(x => x.Id), Zone.Library);
        BeginSimultaneous();
        foreach (var (id, from, version) in all)
            if (State.GetCard(id) is var card && card.Zone == from && card.Version == version) await MoveCardAsync(id, Zone.Library);
        EndSimultaneous();
        foreach (var p in players) Shuffle(State.GetPlayer(p));
        // "Then draws that many cards": as many as they shuffled into their library this way, tokens counted.
        if (s.DrawThatMany)
            foreach (var p in players)
                if (moving[p].Count > 0) await DrawAsync(p, moving[p].Count);
    }

    private async Task EachPutsFromHandAsync(EachPutsFromHand ep, EffectContext ctx)
    {
        // Each player chooses in turn order, then all the chosen cards enter at the same time (rule 101.4).
        var chosen = new List<(PlayerId Player, CardId Card)>();
        foreach (var p in InTurnOrder(ep.Who, ctx))
        {
            var options = State.GetPlayer(p).Hand.Select(State.GetCard)
                .Where(c => Matches(ep.Filter with { Controller = ControllerFilter.Any }, c, p, ctx.Source, p)).ToList();
            if (options.Count == 0) continue;
            var pick = await ControllerOf(p).ChooseCardsAsync(ViewFor(p), new CardChoiceRequest($"{ctx.Source.Name}: put any number onto the battlefield", ctx.Source.Id,
                options.Select(c => ViewBuilder.Card(State, c.Id, p)).ToList(), 0, options.Count, CardChoicePurpose.ToBattlefield));
            Require(pick.Distinct().Count() == pick.Count && pick.All(id => options.Any(c => c.Id == id)), "Choose among the listed cards in your hand.");
            chosen.AddRange(pick.Select(id => (p, id)));
        }
        BeginEnteringTogether();
        foreach (var (p, id) in chosen)
            if (State.GetCard(id).Zone == Zone.Hand) await MoveCardAsync(id, Zone.Battlefield, controller: p);
        EndEnteringTogether();
    }

    private async Task RevealTopCastFreeAsync(RevealTopCastFree rc, EffectContext ctx)
    {
        var caster = State.GetPlayer(ctx.Controller);
        // Spell mastery and the like are checked as the spell resolves.
        int casts = rc.MoreCastsIf is { } more && HoldsIn(more, ctx) ? rc.MoreCasts : rc.Casts;
        foreach (var p in InTurnOrder(rc.Whose, ctx))
        {
            var library = State.GetPlayer(p).Library;
            var revealed = library.Take(rc.Count).ToList();
            if (revealed.Count == 0) continue;
            Emit(new CardsRevealed(p, revealed));
            // Cast while this resolves, from among the revealed cards still in the library (rule 608.2g): timing doesn't matter,
            // everything else about casting does (targets, "can't cast" effects, additional costs).
            var backedOut = new HashSet<CardId>();
            for (int cast = 0; cast < casts;)
            {
                var castable = revealed.Select(State.GetCard)
                    .Where(c => c.Zone == Zone.Library && !backedOut.Contains(c.Id) && !c.Is(CardType.Land)
                                && Matches(rc.Filter with { Controller = ControllerFilter.Any }, c, p, ctx.Source, ctx.Controller) && CastableFreeNow(c, ctx.Controller))
                    .ToList();
                if (castable.Count == 0) break;
                int left = casts - cast;
                var pick = await ControllerOf(ctx.Controller).ChooseCardsAsync(ViewFor(ctx.Controller), new CardChoiceRequest(
                    $"{ctx.Source.Name}: you may cast {(left == 1 ? "a spell" : $"up to {left} spells")} from among the revealed cards without paying {(left == 1 ? "its mana cost" : "their mana costs")}",
                    ctx.Source.Id, castable.Select(c => ViewBuilder.Card(State, c.Id, ctx.Controller, reveal: true)).ToList(), 0, 1, CardChoicePurpose.ToBattlefield));
                if (pick.Count == 0) break;
                Require(pick.Count == 1 && castable.Any(c => c.Id == pick[0]), "Choose one of the revealed cards.");
                if (await CastNowWithoutPayingAsync(caster, pick[0])) cast++;
                else backedOut.Add(pick[0]);
            }
            // "Then that player puts the rest into their graveyard."
            if (rc.RestToGraveyard)
            {
                var rest = revealed.Where(id => State.GetCard(id).Zone == Zone.Library).ToList();
                BeginSimultaneous();
                foreach (var id in rest) await MoveCardAsync(id, Zone.Graveyard);
                EndSimultaneous();
            }
        }
    }

    /// <summary>Whether the player could cast the card now without paying its mana cost, as part of a resolving effect.</summary>
    private bool CastableFreeNow(Card card, PlayerId caster)
    {
        bool added = _castFree.Add(card.Id);
        try { return !SpellsForbidden(caster) && CanBeCast(card, caster, flashExtra: false); }
        finally { if (added) _castFree.Remove(card.Id); }
    }

    private async Task NameThenExileFromAllZonesAsync(NameThenExileFromAllZones ne, EffectContext ctx)
    {
        var who = ctx.Controller;
        // "Choose a [creature] card name" (rule 201.3): any such card's name, not only those in the game.
        var names = NamesOfCardsMatching(who, ne.NameFilter);
        if (names.Count == 0) return;
        int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{ctx.Source.Name}: choose a card name", ctx.Source.Id, names, OptionKind.CardName));
        Require(i >= 0 && i < names.Count, "Choose one of the names.");
        var name = names[i];
        Emit(new ChoiceMade(ctx.Source.Id, name));
        foreach (var p in InTurnOrder(ne.Whose, ctx))
        {
            var player = State.GetPlayer(p);
            // Searching a hand and a library: the searcher sees them (rule 701.19); a graveyard is public anyway.
            if (player.Hand.Count > 0) Emit(new HandLookedAt(who, p, player.Hand.ToList()));
            Look(who, player.Hand);
            Look(who, player.Library);
            var namesakes = player.Graveyard.Concat(player.Hand).Concat(player.Library).Where(id => HasName(State.GetCard(id), name)).ToList();
            if (namesakes.Count > 0)
            {
                // "Any number": the searcher may leave some (rule 701.19b).
                var found = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                    $"{ctx.Source.Name}: exile any number of the cards named {name} from {player.Name}'s graveyard, hand and library", ctx.Source.Id,
                    namesakes.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList(), 0, namesakes.Count, CardChoicePurpose.Keep));
                Require(found.Distinct().Count() == found.Count && found.All(namesakes.Contains), "Choose among the cards with that name.");
                BeginSimultaneous();
                foreach (var id in found)
                {
                    await MoveCardAsync(id, Zone.Exile);
                    if (State.GetCard(id).Zone == Zone.Exile) ctx.Results.Exiled.Add(id);
                }
                EndSimultaneous();
            }
            // "That player shuffles."
            Shuffle(player);
        }
    }

    /// <summary>Whether a card (in a hand, library or graveyard) has this name: a split card has both its halves' names.</summary>
    private static bool HasName(Card card, string name) =>
        string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase)
        || (card.PrintedDefinition.SplitHalves?.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>
    /// The names offered for "choose a [kind] card name": the game's card database (<see cref="GameConfig.CreatureCardNames"/> for
    /// creature cards) or, without one, the matching cards the chooser knows of (never giving away a hidden card).
    /// </summary>
    private List<string> NamesOfCardsMatching(PlayerId who, ObjectFilter filter)
    {
        bool creatures = filter.Types == CardType.Creature;
        var known = State.Cards.Values.Where(c => !c.PrintedDefinition.IsToken && !c.PrintedDefinition.IsEmblem
                                                  && (c.Owner == who || c.IsVisibleTo(who) || ViewBuilder.RevealedByEffect(State, c, who)))
            .SelectMany(c => c.PrintedDefinition.BackFace is { } back ? new[] { c.PrintedDefinition, back } : new[] { c.PrintedDefinition })
            .Where(face => (face.Types & filter.Types) != 0 || filter.Types == 0)
            .Select(face => face.Name);
        IEnumerable<string> all = creatures && Config.CreatureCardNames is { } listed ? listed : filter.Types == 0 && Config.CardNames is { } any ? any : known;
        return all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ "can't cast spells with the chosen name"

    /// <summary>
    /// Whether a static ability stops <paramref name="caster"/> casting this card (as the half or Adventure it's being cast as): "your
    /// opponents can't cast spells with the chosen name (as long as this creature is on the battlefield)" of a permanent an opponent
    /// controls that still has its abilities.
    /// </summary>
    private bool CastForbiddenByName(Card card, PlayerId caster)
    {
        var banned = State.Battlefield.Select(State.GetCard)
            .Where(p => p.Definition.OpponentsCantCastChosenName && p.ChosenName is not null && !p.LosesAbilities && !p.LosesTextAbilities
                        && State.OpponentsOf(p.Controller).Contains(caster))
            .Select(p => p.ChosenName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (banned.Count == 0) return false;
        // A split card not yet cast as a half can still be cast as a half whose name isn't banned.
        if (card.CastHalf is null && !card.AsAdventure && card.PrintedDefinition.SplitHalves is { } halves)
            return halves.All(h => banned.Contains(h.Name));
        return banned.Contains(card.Name);
    }

    /// <summary>
    /// "As this enters, each opponent reveals their hand. You choose the name of a nonland card revealed this way." With no nonland
    /// card revealed, no name is chosen.
    /// </summary>
    private async Task ChooseRevealedNonlandNameAsync(Card card, PlayerId who)
    {
        var names = new List<string>();
        foreach (var opponent in State.ApnapOrder().Where(p => State.OpponentsOf(who).Contains(p)).ToList())
        {
            var hand = State.GetPlayer(opponent).Hand.ToList();
            Emit(new CardsRevealed(opponent, hand));
            foreach (var revealed in hand.Select(State.GetCard).Where(c => !c.Is(CardType.Land)))
                names.AddRange(revealed.PrintedDefinition.SplitHalves is { } halves ? halves.Select(h => h.Name) : new[] { revealed.Name });
        }
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return;
        int i = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{card.Name}: choose the name of a nonland card revealed this way", card.Id, names, OptionKind.CardName));
        Require(i >= 0 && i < names.Count, "Choose one of the revealed names.");
        card.ChosenName = names[i];
        Emit(new ChoiceMade(card.Id, names[i]));
    }

    // ------------------------------------------------------------------ quantities

    private int EvalOrigins(Quantity q, EffectContext ctx) => q.Kind switch
    {
        QuantityKind.AffectedManaValue => ctx.AffectedCard is { } affected ? ManaValueOf(affected) : 0,
        QuantityKind.FoundPower => FoundStats(ctx)?.Power ?? 0,
        QuantityKind.FoundToughness => FoundStats(ctx)?.Toughness ?? 0,
        _ => throw new NotSupportedException($"Quantity {q.Kind} is not implemented."),
    };

    /// <summary>The power and toughness the last card a reveal found had as it was revealed (rule 608.2h: as it last existed there).</summary>
    private (int Power, int Toughness)? FoundStats(EffectContext ctx) =>
        ctx.Results.Found.Count > 0 && ctx.Results.FoundStats.TryGetValue(ctx.Results.Found[^1], out var stats) ? stats
        : ctx.Results.Found.Count > 0 ? (State.GetCard(ctx.Results.Found[^1]).Power, State.GetCard(ctx.Results.Found[^1]).Toughness)
        : null;

    // ------------------------------------------------------------------ casting from a graveyard, exiled afterwards

    /// <summary>
    /// Whether a card cast from its graveyard now is cast with a permission that exiles it instead of putting it into a graveyard
    /// ("you may cast target instant or sorcery card from your graveyard this turn. If that spell would be put into your graveyard,
    /// exile it instead"). A card that may also be cast from the graveyard some other way is cast the way that doesn't exile it.
    /// </summary>
    private bool CastWithExilingPermission(Card card) =>
        card.Zone == Zone.Graveyard
        && State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version && p.ExileInstead)
        && !State.PlayableFromGraveyard.Any(p => p.Card == card.Id && p.Version == card.Version && !p.ExileInstead)
        && !HasGraveyardCastRight(card);
}
