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
    /// <summary>What a card put onto the battlefield becomes as it enters ("They're an artifact", "They are Food artifacts with …").</summary>
    private void BecomeAsItEnters(Card card, PutOntoBattlefield p, EffectContext ctx)
    {
        if (card.Zone != Zone.Battlefield) return;
        if (p.SetTypes is not null || p.SetSubtypes is not null || p.AddAbilities is not null || p.SetPower is not null || p.SetToughness is not null)
            State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>())
            {
                SetTypes = p.SetTypes, SetSubtypes = p.SetSubtypes ?? (p.SetTypes is not null ? Array.Empty<string>() : null),
                Abilities = p.AddAbilities?.Select(a => BindGranter(a, ctx.Controller)).ToList(), Timestamp = NewTimestamp(),
                SetPower = p.SetPower, SetToughness = p.SetToughness,
            });
        if (p.AddSubtypes is not null || p.AddKeywords is not null || p.AddColors is not null || p.AddTypes != 0)
            State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, p.AddKeywords ?? (IReadOnlyList<Keyword>)Array.Empty<Keyword>())
            {
                AddSubtypes = p.AddSubtypes, AddColors = p.AddColors, AddTypes = p.AddTypes, Timestamp = NewTimestamp(),
            });
        RecomputeContinuousEffects();
    }

    /// <summary>Amass (rule 701.47): an Army gets the counters, creating one first if the player has none.</summary>
    private async Task AmassAsync(PlayerId player, Amass amass, int count, EffectContext ctx)
    {
        bool IsArmy(Card c) => c.IsCreature && c.HasSubtype("Army");
        if (!State.PermanentsControlledBy(player).Any(IsArmy))
        {
            ctx.Results.Created.AddRange((await CreateTokenEventAsync(player, amass.Token, 1, false, ctx.Source)).Originals);
        }
        var armies = State.PermanentsControlledBy(player).Where(IsArmy).ToList();
        if (armies.Count == 0) return;
        var army = armies[0];
        if (armies.Count > 1)
        {
            var options = armies.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList();
            var pick = await ControllerOf(player).ChooseCardsAsync(ViewFor(player),
                new CardChoiceRequest($"Amass {amass.Subtype}s {count}: choose an Army", ctx.Source.Id, options, 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && armies.Any(c => c.Id == pick[0]), "Choose one of your Armies.");
            army = State.GetCard(pick[0]);
        }
        PutCounters(army, CounterKind.PlusOnePlusOne, count, ctx.Controller);
        if (!army.HasSubtype(amass.Subtype))
            State.LastingEffects.Add(new UntilEndOfTurnEffect(army.Id, army.Version, 0, 0, Array.Empty<Keyword>())
            {
                AddSubtypes = new[] { amass.Subtype }, Timestamp = NewTimestamp(),
            });
        ctx.Results.Amassed = army.Id;
    }

    /// <summary>
    /// One event that creates tokens for a player, after the replacement effects that modify it: each one applies once, in
    /// the order the player chooses (rules 614.5 and 616.1) — "twice that many", "plus an additional Food", "a Food and a
    /// Treasure instead of a Food". Returns the tokens of the original kind and every token made.
    /// </summary>
    /// <param name="backFaceUp">A double-faced token copying a permanent with its back face up enters that way (rule 707.8a).</param>
    private async Task<(List<CardId> Originals, List<CardId> All)> CreateTokenEventAsync(PlayerId player, CardDefinition token, int count, bool tapped, Card source,
        bool backFaceUp = false, int enterCounters = 0)
    {
        var originals = new List<CardId>();
        var all = new List<CardId>();
        if (count <= 0) return (originals, all);
        var batch = new List<(CardDefinition Token, int Count, bool Original)> { (token, count, true) };
        int doublings = Instances(player, Replacements.DoubleTokens) + State.GetPlayer(player).TokenDoublingsThisTurn.Count(t => t == State.TurnNumber);
        int angels = State.PermanentsControlledBy(player).Count(c => c.HasTextAbilities && c.Definition.CreatureTokensBecomeAngels);
        int extraFoods = Instances(player, Replacements.ExtraFoodWithTokens);
        int extraFrogs = Instances(player, Replacements.ExtraFrogWithTokens);
        int treasures = Instances(player, Replacements.FoodAlsoTreasure);
        int dragons = State.PermanentsControlledBy(player).Count(c => c.HasTextAbilities && c.Definition.ArtifactTokensBecomeDragons);
        int eachOfThree = State.PermanentsControlledBy(player).Count(c => c.HasTextAbilities && c.Definition.ClueFoodTreasureEach);
        int squirrels = State.PermanentsControlledBy(player).Count(c => c.HasTextAbilities && c.Definition.TokensPlusThatManySquirrels);
        var replacements = State.PermanentsControlledBy(player).SelectMany(c => c.Abilities.OfType<TokenReplacement>()).ToList();
        // "The first time you would create one or more tokens each turn, you may instead create that many copies of equipped creature."
        bool firstThisTurn = State.TokensCreatedOnTurn.GetValueOrDefault(player, -1) != State.TurnNumber;
        State.TokensCreatedOnTurn[player] = State.TurnNumber;
        var crowns = new List<Card>();
        if (firstThisTurn)
            foreach (var crown in State.PermanentsControlledBy(player).Where(c => c.HasTextAbilities && c.Definition.FirstTokensCopyEquipped
                         && c.AttachedTo is { } host && State.GetCard(host) is { IsCreature: true, Zone: Zone.Battlefield }).ToList())
                if (await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest(
                        $"{crown.Name}: create copies of {State.GetCard(crown.AttachedTo!.Value).Name} instead?", crown.Id)))
                    crowns.Add(crown);
        static bool ClueFoodOrTreasure(CardDefinition t) => t.Types.HasFlag(CardType.Artifact) && (t.Subtypes.Contains("Clue") || t.Subtypes.Contains("Food") || t.Subtypes.Contains("Treasure"));
        while (true)
        {
            int foods = batch.Where(b => b.Token.Subtypes.Contains("Food")).Sum(b => b.Count);
            var choices = new List<(string Label, Action Apply)>();
            if (doublings > 0) choices.Add(("Twice that many tokens", () => { batch = batch.Select(b => (b.Token, b.Count * 2, b.Original)).ToList(); doublings--; }));
            if (angels > 0 && batch.Any(b => b.Token.Types.HasFlag(CardType.Creature) && b.Token.Name != AngelToken.Name))
                choices.Add(("That many 4/4 white Angels with flying and vigilance instead of the creature tokens", () =>
                {
                    batch = batch.Select(b => b.Token.Types.HasFlag(CardType.Creature) ? (AngelToken, b.Count, b.Original) : b).ToList();
                    angels--;
                }));
            if (extraFoods > 0) choices.Add(("Those tokens plus an additional Food", () => { batch.Add((PredefinedTokens.Food, 1, false)); extraFoods--; }));
            if (extraFrogs > 0) choices.Add(("Those tokens plus a 1/1 green Frog", () => { batch.Add((FrogToken, 1, false)); extraFrogs--; }));
            if (treasures > 0 && foods > 0) choices.Add(("A Treasure for each Food", () => { batch.Add((PredefinedTokens.Treasure, foods, false)); treasures--; }));
            if (dragons > 0 && batch.Any(b => b.Token.Types.HasFlag(CardType.Artifact)))
                choices.Add(("That many 5/5 red Dragons with flying instead of the artifact tokens", () =>
                {
                    batch = batch.Select(b => b.Token.Types.HasFlag(CardType.Artifact) ? (VisitingDragon, b.Count, b.Original) : b).ToList();
                    dragons--;
                }));
            // "Instead create one of each": each Clue, Food or Treasure about to be made becomes a Clue, a Food and a Treasure.
            if (eachOfThree > 0 && batch.Any(b => ClueFoodOrTreasure(b.Token)))
                choices.Add(("A Clue, a Food and a Treasure for each Clue, Food or Treasure", () =>
                {
                    int n = batch.Where(b => ClueFoodOrTreasure(b.Token)).Sum(b => b.Count);
                    bool original = batch.Any(b => ClueFoodOrTreasure(b.Token) && b.Original);
                    batch = batch.Where(b => !ClueFoodOrTreasure(b.Token))
                        .Concat(new[] { PredefinedTokens.Clue, PredefinedTokens.Food, PredefinedTokens.Treasure }.Select(t => (t, n, original))).ToList();
                    eachOfThree--;
                }));
            foreach (var r in replacements.ToList().Where(r => batch.Any(b => b.Token.Subtypes.Contains(r.Subtype))))
                choices.Add(($"A {r.Instead.Name} instead of each {r.Subtype}", () =>
                {
                    batch = batch.Select(b => b.Token.Subtypes.Contains(r.Subtype) ? (r.Instead, b.Count, b.Original) : b).ToList();
                    replacements.Remove(r);
                }));
            foreach (var crown in crowns.ToList())
                choices.Add(("That many copies of the equipped creature instead", () =>
                {
                    crowns.Remove(crown);
                    if (crown.AttachedTo is not { } host || State.GetCard(host) is not { IsCreature: true, Zone: Zone.Battlefield } equipped) return;
                    var (copied, copiedBackFaceUp) = CopiableForToken(equipped);
                    CardDefinition AsToken(CardDefinition face) => face with { IsToken = true, ImageByName = face.ImageByName || (!face.IsToken && face.ImageKey is null) };
                    var copyToken = AsToken(copied) with { BackFace = copied.BackFace is { } back ? AsToken(back) : null };
                    batch = new() { (copyToken, batch.Sum(b => b.Count), true) };
                    backFaceUp = copiedBackFaceUp;
                }));
            if (squirrels > 0)
                choices.Add(("Those tokens plus that many 1/1 green Squirrels", () => { batch.Add((SquirrelToken, batch.Sum(b => b.Count), false)); squirrels--; }));
            if (choices.Count == 0) break;
            int pick = 0;
            if (choices.Count > 1)
            {
                pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player), new OptionRequest(
                    $"Creating {string.Join(", ", batch.Select(b => $"{b.Count} {b.Token.Name}"))}: which replacement applies next?", source.Id,
                    choices.Select(c => c.Label).ToList(), OptionKind.Other));
                Require(pick >= 0 && pick < choices.Count, "Choose one of the replacements.");
            }
            choices[pick].Apply();
        }
        BeginEnteringTogether();
        foreach (var (def, n, original) in batch)
            for (int i = 0; i < n; i++)
                if (await CreateTokenAsync(def, player, original && tapped, original && backFaceUp, original ? enterCounters : 0) is { } made)
                {
                    all.Add(made);
                    if (original) originals.Add(made);
                }
        EndEnteringTogether();
        if (all.Count > 0) Emit(new TokensCreated(all, player));
        State.GetPlayer(player).TokensCreatedThisTurn += all.Count;
        return (originals, all);
    }

    private static readonly TriggeredAbility ReturnThatCreatureToHand = new()
    {
        Trigger = TriggerEvent.EndOfCombat,
        Effects = new Effect[] { new ReturnToHand(Subject.Triggered) },
        Text = "At end of combat, return that creature to its owner's hand.",
    };

    /// <summary>
    /// "Creatures you control are every creature type. The same is true for creature spells you control and creature cards you own that
    /// aren't on the battlefield."
    /// </summary>
    private void RecomputeEveryCreatureType()
    {
        var granters = State.Battlefield.Select(State.GetCard).Where(c => c.HasTextAbilities && c.Definition.YourCreaturesAreEveryType).Select(c => c.Controller).ToHashSet();
        var chosen = State.Battlefield.Select(State.GetCard)
            .Where(c => c.HasTextAbilities && (c.Definition.ExtraRules & ExtraRules.YourCreaturesAreChosenType) != 0 && c.ChosenType is not null)
            .GroupBy(c => c.Controller).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(c => c.ChosenType!).Distinct().ToList());
        if (granters.Count == 0 && chosen.Count == 0 && !_everyCreatureTypeApplied) return;
        _everyCreatureTypeApplied = granters.Count > 0 || chosen.Count > 0;
        foreach (var card in State.Cards.Values)
        {
            var whose = card.Zone is Zone.Battlefield or Zone.Stack ? card.Controller : card.Owner;
            card.EveryCreatureType = granters.Contains(whose);
            card.ChosenCreatureTypes = chosen.TryGetValue(whose, out var types) ? types : Array.Empty<string>();
        }
    }

    private bool _everyCreatureTypeApplied;

    /// <summary>The 1/1 green Squirrel creature token created along with other tokens.</summary>
    private static readonly CardDefinition SquirrelToken = new()
    {
        Name = "Squirrel", Types = CardType.Creature, Subtypes = new[] { "Squirrel" }, Power = 1, Toughness = 1, Colors = new[] { "G" }, IsToken = true,
    };

    /// <summary>The 5/5 red Dragon creature token with flying created instead of artifact tokens.</summary>
    private static readonly CardDefinition VisitingDragon = new()
    {
        Name = "Dragon", Types = CardType.Creature, Subtypes = new[] { "Dragon" }, Power = 5, Toughness = 5, Colors = new[] { "R" }, Keywords = new[] { "Flying" }, IsToken = true,
    };

    private async Task<CardId?> CreateTokenAsync(CardDefinition definition, PlayerId controller, bool tapped = false, bool backFaceUp = false, int enterCounters = 0)
    {
        var token = definition with { IsToken = true };
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        var card = new Card(id, token, controller) { Zone = Zone.Battlefield, Transformed = backFaceUp && token.BackFace is not null };
        // "If a creature would enter and it wasn't cast, exile it instead": the token is exiled and ceases to exist (rule 111.7).
        if (TokenExiledInsteadOfEntering(card)) return null;
        card.Tapped = (tapped || card.Definition.EntersTapped || (card.Definition.IsCreature() && OpponentsCreaturesEnterTapped(controller)))
                      && !(card.Definition.Is(CardType.Land) && Has(controller, Replacements.YourLandsEnterUntapped));
        State.Cards.Add(id, card);
        State.Battlefield.Add(id);
        State.GetPlayer(controller).CreatedTokenThisTurn = true;
        // It enters with the counters other permanents give it (rule 614.1c), so they are there when its enters triggers are collected.
        var entering = new List<(CounterKind Kind, int Count)>();
        if (enterCounters > 0) entering.Add((CounterKind.PlusOnePlusOne, enterCounters)); // "a token with N +1/+1 counters on it"
        if (card.IsCreature && ExtraEnterCounters(card) is var extra and > 0) entering.Add((CounterKind.PlusOnePlusOne, extra));
        var placed = PlaceEnterCounters(card, entering, await OrderEnterCountersAsync(card, entering));
        Emit(new TokenCreated(id, controller));
        Emit(new CardMoved(id, controller, Zone.Exile, Zone.Battlefield, controller));
        AnnounceEnterCounters(card, placed);
        return id;
    }

    /// <summary>Scry or surveil: the player looks at the top cards and picks which ones leave the top.</summary>
    private async Task LookAtTopAsync(PlayerId who, int count, CardChoicePurpose purpose, Card source)
    {
        var player = State.GetPlayer(who);
        var top = player.Library.Take(count).ToList();
        if (top.Count == 0) return;
        bool scry = purpose == CardChoicePurpose.ScryToBottom;
        Look(who, top);
        var prompt = scry
            ? $"Scry {count}: click the cards to put on the bottom of your library, any number; the rest stay on top"
            : $"Surveil {count}: click the cards to put into your graveyard, any number; the rest stay on top";
        var options = top.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
        var chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(prompt, source.Id, options, 0, top.Count, purpose));
        Require(chosen.Distinct().Count() == chosen.Count && chosen.All(top.Contains), "Choose among the cards looked at.");

        var kept = top.Where(id => !chosen.Contains(id)).ToList();
        foreach (var id in top) player.Library.Remove(id);
        // The player orders the cards left on top, and (scry) those put on the bottom (rule 701.22a).
        foreach (var id in (await OrderAsync(who, kept, "Choose the card to put on top next", source)).Reverse()) player.Library.Insert(0, id);
        if (scry)
            foreach (var id in await OrderAsync(who, chosen.ToList(), "Choose the next card for the bottom (first goes deepest)", source)) player.Library.Add(id);
        else
            foreach (var id in chosen)
            {
                player.Library.Insert(0, id); // briefly back on top so it moves from the library
                await MoveCardAsync(id, Zone.Graveyard);
            }
        // The cards go back face down: only the player who looked follows them, unless a single one stays in the library.
        PlacedInUnseenOrder(top, who);
        Emit(new LookedAtTop(who, top.Count, chosen.Count, scry));
        var happened = scry ? TriggerEvent.YouScry : TriggerEvent.YouSurveil;
        player.ScriedOrSurveilledThisTurn = true;
        foreach (var card in State.PermanentsControlledBy(who).ToList()) Queue(card.Id, happened, who, new TriggerInfo(Player: who, Amount: top.Count));
        foreach (var card in State.PermanentsControlledBy(who).ToList()) Queue(card.Id, TriggerEvent.YouScryOrSurveil, who, new TriggerInfo(Player: who, Amount: top.Count));
        foreach (var card in player.Graveyard.Select(State.GetCard).ToList())
            foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard && a.Trigger == happened))
                AddPending(card.Id, ability, who, new TriggerInfo(Player: who, Amount: top.Count));
    }

    /// <summary>Cards a search puts onto the battlefield together with what it finds (<see cref="SearchLibrary.WithExiled"/>).</summary>
    private List<CardId> _searchWithExiled = new();

    private async Task<IReadOnlyList<CardId>> SearchLibraryAsync(PlayerId who, SearchLibrary search, Card source)
    {
        var player = State.GetPlayer(who);
        Look(who, player.Library); // searching is looking through the whole library (rule 701.23a)
        var filter = search.Filter with { Controller = ControllerFilter.Any };
        var options = player.Library.Select(State.GetCard).Where(c => Matches(filter, c, who, source, who))
            .Select(c => ViewBuilder.Card(State, c.Id, who, reveal: true)).ToList();
        if (options.Count > 0)
        {
            var purpose = search.To == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand;
            var where = search.To switch
            {
                Zone.Battlefield => "onto the battlefield" + (search.Tapped ? " tapped" : ""),
                Zone.Graveyard => "into your graveyard",
                Zone.Library => "on top of your library",
                Zone.Exile => "into exile",
                _ => "into your hand",
            };
            var request = new CardChoiceRequest($"Search your library: choose up to {search.Count} to put {where}", source.Id, options, 0,
                Math.Min(search.Count, options.Count), purpose);
            IReadOnlyList<CardId> chosen;
            if (search.DifferentNames && search.Count > 1)
            {
                // "With different names": one at a time, each among the names not chosen yet.
                var picked = new List<CardId>();
                for (int i = 0; i < search.Count; i++)
                {
                    var left = options.Where(o => picked.All(p => State.GetCard(p).Name != State.GetCard(o.Id).Name)).ToList();
                    if (left.Count == 0) break;
                    var one = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                        $"Search your library: choose a card with a different name to put {where} ({i + 1} of up to {search.Count})", source.Id, left, 0, 1, purpose));
                    Require(one.Count <= 1 && one.All(id => left.Any(o => o.Id == id)), "Choose among the matching cards.");
                    if (one.Count == 0) break;
                    picked.Add(one[0]);
                }
                chosen = picked;
            }
            else
            {
                chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), request);
                Require(chosen.Count <= search.Count && chosen.Distinct().Count() == chosen.Count && chosen.All(id => options.Any(o => o.Id == id)),
                    "Choose among the matching cards.");
            }
            if (search.ShareLandType && chosen.Count > 1)
            {
                var landTypes = new[] { "Plains", "Island", "Swamp", "Mountain", "Forest", "Desert", "Gate", "Lair", "Locus", "Mine", "Power-Plant", "Tower", "Urza's", "Cave", "Sphere", "Town" };
                // Cards that don't share a land type with the first one chosen aren't found.
                var first = State.GetCard(chosen[0]).Definition.Subtypes;
                var shared = landTypes.Where(first.Contains).ToList();
                chosen = chosen.Where((id, i) => i == 0 || shared.Any(t => State.GetCard(id).Definition.Subtypes.Contains(t))).Take(shared.Count == 0 ? 1 : chosen.Count).ToList();
                if (shared.Count > 0 && chosen.Count > 1)
                {
                    var common = shared.Where(t => chosen.All(id => State.GetCard(id).Definition.Subtypes.Contains(t))).ToList();
                    if (common.Count == 0) chosen = chosen.Take(1).ToList();
                }
            }
            if (search.Reveal && chosen.Count > 0) Emit(new CardsRevealed(who, chosen.ToList()));
            // "Put one onto the battlefield tapped and the other into your hand."
            CardId? toBattlefield = null;
            if (search.OneToBattlefieldRestToHand && chosen.Count > 0)
            {
                toBattlefield = chosen[0];
                if (chosen.Count > 1)
                {
                    var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest("Choose the card to put onto the battlefield tapped", source.Id,
                        chosen.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList(), 1, 1, CardChoicePurpose.ToBattlefield));
                    Require(pick.Count == 1 && chosen.Contains(pick[0]), "Choose one of the cards found.");
                    toBattlefield = pick[0];
                }
            }
            if (search.MayBattlefieldIf is { } mayIf && chosen.Count > 0 && Holds(mayIf, who, source)
                && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Put {State.GetCard(chosen[0]).Name} onto the battlefield tapped (instead of into your hand)?", chosen[0])))
                toBattlefield = chosen[0];
            bool untappedInstead = false;
            if (search.BattlefieldInsteadIf is { } insteadIf && chosen.Count > 0 && Holds(insteadIf, who, source)
                && (search.BattlefieldInsteadIfFound is not { } insteadFilter || Matches(insteadFilter with { Controller = ControllerFilter.Any }, State.GetCard(chosen[0]), who, source, who)))
            {
                toBattlefield = chosen[0];
                untappedInstead = !search.Tapped;
            }
            var onTop = new List<CardId>();
            BeginEnteringTogether();
            if (search.WithExiled)
                foreach (var exiledId in _searchWithExiled.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList())
                    await MoveCardAsync(exiledId, Zone.Battlefield, controller: who);
            foreach (var id in chosen)
            {
                if (search.To == Zone.Library) { onTop.Add(id); continue; }
                var to = toBattlefield is null ? search.To : id == toBattlefield ? Zone.Battlefield : Zone.Hand;
                // "Put it into your hand or graveyard."
                if (search.HandOrGraveyard)
                    to = await ControllerOf(who).ChooseOptionAsync(ViewFor(who), new OptionRequest($"{source.Name}: put {State.GetCard(id).Name} into your hand or graveyard?",
                        id, new[] { "Hand", "Graveyard" }, OptionKind.Other)) == 1 ? Zone.Graveyard : Zone.Hand;
                await MoveCardAsync(id, to, controller: who, tapped: search.Tapped || (toBattlefield is not null && !untappedInstead),
                    withCounters: search.EntersWithCounter is { } enterKind && to == Zone.Battlefield ? new[] { (enterKind, 1) } : null);
            }
            EndEnteringTogether();
            Shuffle(player);
            foreach (var id in onTop) { player.Library.Remove(id); player.Library.Insert(0, id); }
            // The cards found are put on top after the shuffle: the searcher knows them (everyone, when they were revealed).
            Look(who, onTop);
            if (search.Reveal) RevealToAll(onTop);
            PlacedInUnseenOrder(onTop, who);
            return chosen;
        }
        if (search.WithExiled)
        {
            BeginEnteringTogether();
            foreach (var exiledId in _searchWithExiled.Where(id => State.GetCard(id).Zone == Zone.Exile).ToList())
                await MoveCardAsync(exiledId, Zone.Battlefield, controller: who);
            EndEnteringTogether();
        }
        Shuffle(player);
        return Array.Empty<CardId>();
    }

    private async Task<IReadOnlyList<CardId>> SacrificeAsync(PlayerId who, int count, ObjectFilter filter, Card source)
    {
        var chosen = await ChooseSacrificesAsync(who, count, filter, source);
        BeginSimultaneous();
        foreach (var id in chosen) await SacrificePermanentAsync(id);
        EndSimultaneous();
        return chosen;
    }

    /// <summary>The permanents a player chooses to sacrifice (all of them when there's no real choice).</summary>
    private async Task<IReadOnlyList<CardId>> ChooseSacrificesAsync(PlayerId who, int count, ObjectFilter filter, Card source)
    {
        var any = filter with { Controller = ControllerFilter.Any };
        var candidates = State.PermanentsControlledBy(who).Where(c => Matches(any, c, who, source, who) && CanBeSacrificedBy(c, who)).ToList();
        count = Math.Min(count, candidates.Count);
        if (count <= 0) return Array.Empty<CardId>();
        IReadOnlyList<CardId> chosen;
        if (count == candidates.Count) chosen = candidates.Select(c => c.Id).ToList();
        else
        {
            var options = candidates.Select(c => ViewBuilder.Card(State, c.Id, who)).ToList();
            chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who),
                new CardChoiceRequest($"Sacrifice {count}", source.Id, options, count, count, CardChoicePurpose.Sacrifice));
            Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(id => candidates.Any(c => c.Id == id)),
                $"Sacrifice exactly {count} of the listed permanents.");
        }
        return chosen;
    }

    /// <summary>Whether a player can sacrifice the permanent ("this artifact can't be sacrificed", "you can't sacrifice those creatures this turn").</summary>
    private bool CanBeSacrificedBy(Card card, PlayerId player) =>
        !(card.Definition.CantBeSacrificed && card.HasTextAbilities) && !card.StaticCantBeSacrificed
        && !State.CantSacrificeThisTurn.Any(r => r.Card == card.Id && r.Version == card.Version && r.Player == player && r.Turn == State.TurnNumber);

    private async Task SacrificePermanentAsync(CardId id)
    {
        if (State.GetCard(id) is { Zone: Zone.Battlefield } doomed && !CanBeSacrificedBy(doomed, doomed.Controller)) return;
        var card = State.GetCard(id);
        bool creature = card.IsCreature;
        await MoveCardAsync(id, Zone.Graveyard);
        Emit(new PermanentSacrificed(id));
        if (creature) Emit(new CreatureDied(id));
    }

    /// <summary>Replaces "the granter" in a granted ability's effects with the player who granted it.</summary>
    private static AbilityDefinition BindGranter(AbilityDefinition ability, PlayerId granter)
    {
        Subject Bind(Subject s) => s.Kind == SubjectKind.Granter ? new Subject(SubjectKind.FixedPlayer) { Player = granter } : s;
        Effect BindEffect(Effect e) => e switch
        {
            CreateTokens t => t with { Controller = Bind(t.Controller) },
            DrawCards d => d with { Who = Bind(d.Who) },
            GainLife g => g with { Who = Bind(g.Who) },
            LoseLife l => l with { Who = Bind(l.Who) },
            _ => e,
        };
        return ability with { Effects = ability.Effects.Select(BindEffect).ToList() };
    }

    private static string DescribeCost(ExtraCost cost) =>
        cost.Discard > 0 ? $"Discard {cost.Discard} card{(cost.Discard > 1 ? "s" : "")}"
        : cost.Sacrifice is { } s ? $"Sacrifice {(cost.SacrificeCount > 1 ? cost.SacrificeCount.ToString() : "a")} {(s.Subtype is { } st ? st : s.Token == true ? "token" : s.Types == CardType.Creature ? "creature" : "permanent")}{(cost.SacrificeCount > 1 ? "s" : "")}"
        : cost.PayLife > 0 ? $"Pay {cost.PayLife} life"
        : cost.ExileFromGraveyard > 0 ? $"Exile {cost.ExileFromGraveyard} cards from your graveyard"
        : cost.ExileYouControl is not null ? "Exile a permanent you control"
        : cost.ExileFromHand is { } fromHand ? $"Exile {(fromHand.Colors is { Count: 1 } colors ? (colors[0] switch { "W" => "a white", "U" => "a blue", "B" => "a black", "R" => "a red", "G" => "a green", _ => "a" }) : "a")} card from your hand"
        : cost.Blight > 0 ? $"Blight {cost.Blight} (put {CountersText(cost.Blight)} on a creature you control)"
        : cost.Forage ? "Forage (exile three cards from your graveyard or sacrifice a Food)" : "Pay";

    /// <summary>Copies a spell on the stack (rule 707.10); the copy's controller may choose new targets.</summary>
    /// <summary>Copies of permanent spells whose token gains haste and "At the beginning of the end step, sacrifice this token" as it enters.</summary>
    private readonly HashSet<CardId> _copiesGainHasteAndSacrifice = new();

    private async Task CopySpellAsync(SpellOnStack original, PlayerId controller, bool notLegendary = false, IReadOnlyList<ChosenTarget>? fixedTargets = null)
    {
        var card = State.GetCard(original.Card);
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        // A copy copies the choices made when casting (rule 707.10): modes, X and whether it was kicked.
        var definition = card.Definition with { IsToken = true };
        if (notLegendary) definition = definition with { Supertypes = definition.Supertypes & ~Supertype.Legendary }; // "except the copy isn't legendary"
        var copy = new Card(id, definition, controller) { Zone = Zone.Stack, Controller = controller, Kicked = original.Kicked, AdditionalCostPaid = card.AdditionalCostPaid };
        State.Cards.Add(id, copy);
        var ability = original.Ability ?? CastingTargets(card.Definition);
        IReadOnlyList<ChosenTarget> targets = fixedTargets ?? original.Targets;
        if (fixedTargets is null && ability is { Targets.Count: > 0 }
            && await ControllerOf(controller).ChooseYesNoAsync(ViewFor(controller), new YesNoRequest($"Choose new targets for the copy of {card.Name}?", id)))
            targets = await ChooseTargetsAsync(controller, ability, id, card.Name, canCancel: true, exactCount: original.Targets.Count) ?? original.Targets;
        copy.TimesKicked = original.KickCount;
        PushStack(original with { Card = id, Controller = controller, Targets = targets, Flashback = false, GiftTo = null, Dashed = false, Id = 0 });
        Emit(new SpellCopied(id, original.Card, controller));
    }

    private bool CanBeCountered(Card spell) =>
        !spell.Definition.CantBeCountered && !spell.Uncounterable
        && !(spell.Definition.CantBeCounteredIf is { } uncounterableIf && Holds(uncounterableIf, spell.Controller, spell)) // spell mastery: "this spell can't be countered"
        && !(spell.Definition.CantBeCounteredIfXAtLeast is { } xLimit && State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == spell.Id)?.X >= xLimit) // "if X is 5 or more, this spell can't be countered"
        && !((spell.Is(CardType.Instant) || spell.Is(CardType.Sorcery)) && Has(spell.Controller, Replacements.YourInstantsAndSorceriesCantBeCountered))
        && !State.PermanentsControlledBy(spell.Controller).Any(p => p.HasTextAbilities && p.Definition.YourSpellsCantBeCountered)
        && !State.Battlefield.Select(State.GetCard).Any(p => p.HasTextAbilities && p.Definition.SpellsCantBeCountered) // "Spells can't be countered."
        && State.GetPlayer(spell.Controller).SpellsCantBeCounteredTurn != State.TurnNumber; // "Spells you control can't be countered this turn."

    private async Task LookAtTopTakeAsync(PlayerId who, LookAtTopTake look, Card source, EffectResults? results = null)
    {
        var player = State.GetPlayer(who);
        var top = player.Library.Take(look.Count).ToList();
        if (top.Count == 0) return;
        Look(who, top);
        if (look.RevealAll) Emit(new CardsRevealed(who, top));
        var options = top.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
        var eligible = top.Where(id => look.Filter is null || Matches(look.Filter with { Controller = ControllerFilter.Any }, State.GetCard(id), who, source, who)).ToList();
        IReadOnlyList<CardId> chosen = Array.Empty<CardId>();
        // A look that takes nothing ("look at the top four, then put them back in any order") only orders them.
        if (eligible.Count > 0 && look.MaxTotalManaValue > 0)
        {
            // "With total mana value N or less": one at a time, among the cards that still fit.
            var picked = new List<CardId>();
            while (true)
            {
                int left = look.MaxTotalManaValue - picked.Sum(id => State.GetCard(id).ManaValue);
                var fits = eligible.Where(id => !picked.Contains(id) && State.GetCard(id).ManaValue <= left).ToList();
                if (fits.Count == 0) break;
                var one = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(
                    $"Look at the top {top.Count}: choose another card (total mana value {look.MaxTotalManaValue} or less, {left} left), or none", source.Id,
                    options.Where(o => fits.Contains(o.Id)).ToList(), 0, 1, look.TakeTo == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand));
                if (one.Count == 0) break;
                Require(one.Count == 1 && fits.Contains(one[0]), "Choose one of the cards that fit.");
                picked.Add(one[0]);
            }
            chosen = picked;
        }
        else if (eligible.Count > 0 && look.Take > 0)
        {
            var purpose = look.TakeTo == Zone.Battlefield ? CardChoicePurpose.ToBattlefield : CardChoicePurpose.ToHand; // exiled cards are chosen the same way as cards taken into hand
            var request = new CardChoiceRequest($"Look at the top {top.Count}: choose {(look.Required ? "" : "up to ")}{look.Take}", source.Id,
                options.Where(o => eligible.Contains(o.Id)).ToList(), look.Required ? Math.Min(look.Take, eligible.Count) : 0, Math.Min(look.Take, eligible.Count), purpose);
            chosen = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), request);
            Require(chosen.Count <= look.Take && chosen.Distinct().Count() == chosen.Count && chosen.All(eligible.Contains), "Choose among the matching cards.");
        }
        if (look.Reveal && !look.RevealAll && chosen.Count > 0) Emit(new CardsRevealed(who, chosen.ToList()));
        BeginEnteringTogether();
        foreach (var id in chosen)
        {
            if (look.TakeTo == Zone.Library) continue; // stays on top
            // "You may put it onto the battlefield if it's your turn. If you don't, put it into your hand."
            if (look.BattlefieldIfYourTurnElseHand)
            {
                bool onto = State.ActivePlayer == who && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Put {State.GetCard(id).Name} onto the battlefield?", source.Id));
                await MoveCardAsync(id, onto ? Zone.Battlefield : Zone.Hand, controller: who);
                results?.Found.Add(id);
                continue;
            }
            if (look.LandsToBattlefieldTapped && State.GetCard(id).Is(CardType.Land))
            {
                await MoveCardAsync(id, Zone.Battlefield, controller: who, tapped: true);
                continue;
            }
            await MoveCardAsync(id, look.TakeTo, controller: who, tapped: look.Tapped || look.Attacking);
            // "Tapped and attacking": its controller chooses which player it attacks (rule 508.4).
            if (look.Attacking && look.TakeTo == Zone.Battlefield && State.GetCard(id) is { Zone: Zone.Battlefield, IsCreature: true } entered && State.Combat is { } lookCombat)
                lookCombat.Attacks.Add(await AttackFor(entered, new EffectContext(who, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>())));
            if (State.GetCard(id).Zone == look.TakeTo) results?.Found.Add(id);
            if (look.PlayableThisTurn && look.TakeTo == Zone.Exile && State.GetCard(id).Zone == Zone.Exile)
                State.PlayableFromExile.Add(new PlayableFromExile(id, State.GetCard(id).Version, who, State.TurnNumber));
        }
        EndEnteringTogether();
        if (look.RestShuffled)
        {
            Shuffle(player);
            Emit(new LookedAtTop(who, top.Count, chosen.Count, Scry: false));
            return;
        }
        var rest = top.Where(id => !chosen.Contains(id)).ToList();
        if (look.RestToHand)
        {
            foreach (var id in rest) await MoveCardAsync(id, Zone.Hand);
            Emit(new LookedAtTop(who, top.Count, top.Count, Scry: false));
            return;
        }
        if (look.RestOnTop)
        {
            if (look.RestOrder == RestOrder.Chosen && rest.Count > 1)
            {
                // "Put the rest back on top of your library in any order."
                foreach (var id in rest) player.Library.Remove(id);
                var ordered = await OrderAsync(who, rest, "Choose the card to put on top next", source);
                player.Library.InsertRange(0, ordered);
                PlacedInUnseenOrder(ordered, who);
            }
            Emit(new LookedAtTop(who, top.Count, 0, Scry: false)); // otherwise the rest stays where it was
            return;
        }
        if (look.RestToGraveyard) { }
        else if (look.RestOrder == RestOrder.Chosen) rest = (await OrderAsync(who, rest, "Choose the next card for the bottom (first goes deepest)", source)).ToList(); // "in any order"
        else Rng.Shuffle(rest); // "on the bottom of your library in a random order"
        foreach (var id in rest)
        {
            if (look.RestToGraveyard) await MoveCardAsync(id, Zone.Graveyard);
            else
            {
                player.Library.Remove(id);
                player.Library.Add(id);
            }
        }
        if (!look.RestToGraveyard) PlacedInUnseenOrder(rest, look.RestOrder == RestOrder.Chosen ? who : null);
        Emit(new LookedAtTop(who, top.Count, top.Count - chosen.Count, Scry: !look.RestToGraveyard));
        // "You may cast the exiled card without paying its mana cost."
        if (look.CastTakenFree)
            foreach (var id in chosen.Where(c => State.GetCard(c).Zone == Zone.Exile).ToList())
                if (HasLegalTargets(CastingTargets(State.GetCard(id).Definition), who, id)
                    && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Cast {State.GetCard(id).Name} without paying its mana cost?", id)))
                    await CastNowWithoutPayingAsync(player, id);
    }

    /// <summary>Removes a spell from the stack to its owner's graveyard (exile if it was cast with flashback).</summary>
    private async Task CounterSpellOnStackAsync(CardId spellCard)
    {
        var item = State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == spellCard);
        State.Stack.RemoveAll(s => s is SpellOnStack sp && sp.Card == spellCard);
        await MoveCardAsync(spellCard, item?.Flashback == true ? Zone.Exile : Zone.Graveyard);
        Emit(new SpellCountered(spellCard));
    }

    /// <summary>A card's colors: from its mana cost, or its definition (tokens).</summary>
    internal static IReadOnlyList<string> ColorsOf(Card card) => card.Colors;

    /// <summary>The player puts cards in order, one pick at a time (first picked comes first).</summary>
    private async Task<IReadOnlyList<CardId>> OrderAsync(PlayerId who, List<CardId> cards, string prompt, Card source)
    {
        if (cards.Count < 2) return cards;
        var order = new List<CardId>();
        var left = cards.ToList();
        while (left.Count > 1)
        {
            var options = left.Select(id => ViewBuilder.Card(State, id, who, reveal: true)).ToList();
            var pick = await ControllerOf(who).ChooseCardsAsync(ViewFor(who), new CardChoiceRequest(prompt, source.Id, options, 1, 1, CardChoicePurpose.Order));
            var next = pick.Count == 1 && left.Contains(pick[0]) ? pick[0] : left[0];
            order.Add(next);
            left.Remove(next);
        }
        order.Add(left[0]);
        return order;
    }

    /// <summary>"An opponent" (the controller chooses which when there are several).</summary>
    private async Task<PlayerId?> ChooseOpponentAsync(PlayerId controller, Card source, string prompt, IReadOnlyList<PlayerId>? among = null)
    {
        var opponents = (among ?? State.OpponentsOf(controller)).ToList();
        if (opponents.Count <= 1) return opponents.FirstOrDefault();
        int i = await ControllerOf(controller).ChooseOptionAsync(ViewFor(controller),
            new OptionRequest(prompt, source.Id, opponents.Select(o => State.GetPlayer(o).Name).ToList(), OptionKind.Other));
        Require(i >= 0 && i < opponents.Count, "Choose one of the opponents.");
        return opponents[i];
    }

    /// <summary>
    /// Discards a card: it moves from its owner's hand to the graveyard after the replacement effects that modify that (rule 614),
    /// among them a card that says so going onto the battlefield instead when a spell or ability an opponent controls caused the
    /// discard. Those effects replace only where the card goes ("instead of putting it into your graveyard"), not the discard, so
    /// the card is discarded wherever it ends up (as with madness, rule 702.35).
    /// </summary>
    private async Task DiscardCardAsync(PlayerId who, CardId card, PlayerId? causedBy, bool byEffect = false)
    {
        var discarded = State.GetCard(card);
        bool byOpponent = causedBy is { } cause && State.OpponentsOf(who).Contains(cause);
        // "If an effect causes you to discard a card, … you may put it on top of your library instead of into your graveyard."
        if (byEffect && HasRule(who, Cards.ExtraRules.EffectDiscardsMayGoOnTop)
            && await ControllerOf(who).ChooseYesNoAsync(ViewFor(who), new YesNoRequest($"Put the discarded {discarded.Name} on top of your library instead of into your graveyard?", card)))
        {
            _movePlans[(card, discarded.Version)] = (Zone.Graveyard, new MovePlan(Zone.Library, false, Array.Empty<ZoneReplacement>()));
        }
        else
        {
            var plan = await PlanMoveAsync(discarded, Zone.Graveyard, discardedByOpponent: byOpponent);
            _movePlans[(card, discarded.Version)] = (Zone.Graveyard, plan);
        }
        await MoveCardAsync(card, Zone.Graveyard);
        Emit(new CardDiscarded(who, card));
        State.GetPlayer(who).CardsDiscardedThisTurn++;
        // "When a spell or ability an opponent controls causes you to discard this card": it triggers wherever the card ended up.
        if (byOpponent)
            foreach (var ability in discarded.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == TriggerEvent.DiscardedByOpponent))
                AddPending(card, ability, who, new TriggerInfo(card, discarded.Version, who));
        foreach (var ability in discarded.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == TriggerEvent.DiscardedThis))
            AddPending(card, ability, who, new TriggerInfo(card, discarded.Version, who));
        foreach (var (observer, abilities) in Observers())
            foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.PlayerDiscards))
                AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(card, discarded.Version, who));
    }

    /// <summary>
    /// "Discards N cards at random" (rule 701.9b): N cards of the hand chosen at random (all of them if fewer), discarded at once.
    /// </summary>
    private async Task<IReadOnlyList<CardId>> DiscardAtRandomAsync(PlayerId who, int count, PlayerId? causedBy)
    {
        var hand = State.GetPlayer(who).Hand.ToList();
        count = Math.Min(count, hand.Count);
        if (count <= 0) return Array.Empty<CardId>();
        Rng.Shuffle(hand);
        var chosen = hand.Take(count).ToList();
        BeginSimultaneous();
        foreach (var card in chosen) await DiscardCardAsync(who, card, causedBy, byEffect: true);
        EndSimultaneous();
        return chosen;
    }

    private async Task<IReadOnlyList<CardId>> DiscardAsync(PlayerId who, int count, PlayerId? causedBy = null)
    {
        var player = State.GetPlayer(who);
        count = Math.Min(count, player.Hand.Count);
        if (count == 0) return Array.Empty<CardId>();
        var chosen = await ControllerOf(who).ChooseDiscardAsync(ViewFor(who), count);
        Require(chosen.Count == count && chosen.Distinct().Count() == count && chosen.All(player.Hand.Contains),
            $"Must discard exactly {count} distinct cards from hand.");
        // Discarding several cards is one event: "one or more" triggers see it once.
        BeginSimultaneous();
        try { foreach (var card in chosen) await DiscardCardAsync(who, card, causedBy, byEffect: true); }
        finally { EndSimultaneous(); }
        return chosen;
    }

    /// <summary>Activated abilities a permanent has from lands its controller's opponents control.</summary>
    private readonly Dictionary<CardId, List<ActivatedAbility>> _borrowedLandAbilities = new();

    /// <summary>
    /// A mana ability (rule 605.1a): it has no target, isn't a loyalty ability and could add mana as it resolves (it may do other things too:
    /// "{T}: Add one mana of any color. When you next cast a creature spell this turn, …").
    /// </summary>
    private static bool IsManaAbilityOf(ActivatedAbility ability) =>
        ability.Targets.Count == 0 && ability.Cost.Loyalty is null && !ability.Cost.LoyaltyX && ability.Effects.Count > 0 && ability.Effects.Any(AddsManaOnly);

    /// <summary>An effect that only adds mana (or a choice among ways to add mana: "Add {G}{G}, {G}{U}, or {U}{U}").</summary>
    private static bool AddsManaOnly(Effect effect) =>
        effect is AddMana or AddManaOfAnyColor or AddManaInAnyCombination or AddManaOfOneColor
        || effect is ChooseOneEffect choose && choose.Choices.All(c => c.Effects.Count > 0 && c.Effects.All(AddsManaOnly));

    /// <summary>"Up to X target …": the requirement repeated X times, all optional and different (X worked out now).</summary>
    private T WithRepeatedTargets<T>(T ability, PlayerId controller, Card source, TriggerInfo? info, int x = 0) where T : AbilityDefinition
    {
        if (!ability.Targets.Any(t => t.RepeatFrom is not null || t.PerPlayer)) return ability;
        var ctx = new EffectContext(controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>(), x) { Trigger = info };
        var targets = ability.Targets.SelectMany(t => t.PerPlayer
            // "For each player, choose target … that player controls": one requirement for each player who has a legal choice, in turn order.
            ? State.ApnapOrder().Select(p => t with { PerPlayer = false, OnlyControlledBy = p }).Where(spec => LegalTargets(spec, controller, source.Id).Any()).ToList()
            : t.RepeatFrom is { } count
                ? Enumerable.Repeat(t with { RepeatFrom = null, Optional = !t.RepeatExactly }, Math.Max(0, Eval(count, ctx)))
                : new[] { t }).ToList();
        return ability with { Targets = targets, TargetRule = ability.TargetRule == TargetRule.None ? TargetRule.AllDifferent : ability.TargetRule };
    }

    /// <summary>The X announced for the ability whose targets are being chosen or checked ("target creature with power X").</summary>
    private int _announcedX = -1;

    /// <summary>The object a pending or resolving trigger is about, while its targets are chosen and checked.</summary>
    private CardId? _triggeredSubject;

    /// <summary>Creature types (subtypes that aren't land, artifact or enchantment types and the like).</summary>
    private static readonly HashSet<string> NonCreatureSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Plains", "Island", "Swamp", "Mountain", "Forest", "Desert", "Gate", "Lair", "Locus", "Mine", "Power-Plant", "Tower", "Urza's", "Cave", "Sphere", "Town",
        "Equipment", "Food", "Treasure", "Clue", "Vehicle", "Blood", "Gold", "Map", "Powerstone", "Junk", "Incubator", "Fortification", "Contraption", "Attraction", "Bobblehead",
        "Aura", "Saga", "Shrine", "Curse", "Background", "Cartouche", "Class", "Case", "Room", "Rune", "Role", "Shard", "Army",
    };

    private static bool IsCreatureType(string subtype) => !NonCreatureSubtypes.Contains(subtype) || subtype == "Army";

    /// <summary>The player a creature put onto the battlefield attacking attacks: its controller chooses among those being attacked (rule 508.4).</summary>
    /// <summary>
    /// A creature put onto the battlefield attacking (rule 508.4): its controller chooses an opponent or a planeswalker an opponent controls
    /// for it to attack, whoever the other attackers attack. It was never declared as an attacker.
    /// </summary>
    private async Task<AttackInfo> AttackFor(Card creature, EffectContext ctx)
    {
        var options = State.OpponentsOf(creature.Controller).Select(p => (Player: p, Planeswalker: (CardId?)null, Label: State.GetPlayer(p).Name))
            .Concat(State.OpponentsOf(creature.Controller).SelectMany(p => State.PermanentsControlledBy(p).Where(c => c.Is(CardType.Planeswalker))
                .Select(w => (Player: p, Planeswalker: (CardId?)w.Id, Label: $"{w.Name} ({State.GetPlayer(p).Name})"))))
            .ToList();
        int pick = 0;
        if (options.Count > 1)
        {
            pick = await ControllerOf(creature.Controller).ChooseOptionAsync(ViewFor(creature.Controller),
                new OptionRequest($"Choose what {creature.Name} attacks", creature.Id, options.Select(o => o.Label).ToList(), OptionKind.Other));
            Require(pick >= 0 && pick < options.Count, "Choose one of the listed players or planeswalkers.");
        }
        return new AttackInfo { Attacker = creature.Id, Defender = options[pick].Player, Planeswalker = options[pick].Planeswalker };
    }

    /// <summary>Whether that object is legendary (as it last existed if it left the battlefield).</summary>
    private bool WasLegendary(CardId id, int version) =>
        State.GetCard(id) is var c && ((c.Version == version && c.Zone == Zone.Battlefield ? c.Supertypes : c.LastKnownInfo?.Supertypes ?? c.Supertypes) & Supertype.Legendary) != 0;

    /// <summary>A permanent's power, or its power as it last existed on the battlefield.</summary>
    private int PowerOrLastKnown(Card card) => card.Zone == Zone.Battlefield || card.LastKnownInfo is null ? card.Power : card.LastKnownInfo.Power;

    /// <summary>
    /// The power of a resolving ability's source: the object that put it on the stack, as it last existed if it has left the
    /// battlefield, even when the card has come back since as a new object (rules 400.7, 608.2h).
    /// </summary>
    private int SourcePowerOrLastKnown(Card source) =>
        ResolvingVersionOf(source) is { } version && (source.Version != version || source.Zone != Zone.Battlefield) && ResolvingSourceLastKnown(source, version) is { } lki
            ? lki.Power
            : PowerOrLastKnown(source);

    /// <summary>Whether a permanent with the given subtype (an Aura, an Equipment) is attached to <paramref name="obj"/>.</summary>
    private bool HasAttached(Card obj, string subtype) =>
        State.Battlefield.Select(State.GetCard).Any(c => c.AttachedTo == obj.Id && c.HasSubtype(subtype));
}
