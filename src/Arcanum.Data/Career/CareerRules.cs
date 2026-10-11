// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Formats;
using Arcanum.Data.Limited;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Career;

/// <summary>What an action did: the cards it gave (an opened booster, a deck) and the cash it moved.</summary>
public sealed record CareerResult(IReadOnlyList<PoolCard> Cards, int Cash);

/// <summary>A card a deck needs more copies of than the collection has.</summary>
public sealed record MissingCard(string Name, int Needed, int Owned);

/// <summary>
/// The career's rules: each action checked and applied to a state. Pure and deterministic, so the same journal always
/// gives the same career (on this device or on a server checking it).
/// </summary>
public static class CareerRules
{
    public const string CashReward = "cash", BoosterReward = "booster";
    public const string Daily = "daily", Practice = "practice", Ante = "ante";

    public static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(utc);

    /// <summary>The unopened boosters' key: the set code, ":kind" after it for its other boosters (":collector").</summary>
    public static string SealedKey(string set, string? kind) => string.IsNullOrEmpty(kind) ? set.ToLowerInvariant() : $"{set.ToLowerInvariant()}:{kind}";

    public static (string Set, string? Kind) ParseSealed(string key) => key.IndexOf(':') is var i and >= 0 ? (key[..i], key[(i + 1)..]) : (key, null);

    /// <summary>A career's state after its whole journal.</summary>
    public static CareerState Replay(IEnumerable<CareerAction> journal, CareerWorld world)
    {
        var state = new CareerState();
        foreach (var action in journal) Apply(state, action, world);
        return state;
    }

    /// <summary>Checks and applies one action; throws <see cref="CareerRuleException"/> (leaving the state as it was) when it isn't allowed.</summary>
    public static CareerResult Apply(CareerState state, CareerAction action, CareerWorld world)
    {
        if (action is StartCareer start) return Start(state, start, world);
        if (state.StartedUtc == default) throw new CareerRuleException("The career hasn't started.");
        if (action.Utc < state.LastUtc) throw new CareerRuleException("The career's clock can't go back.");
        var (lastUtc, day, dailies, practices, antes) = (state.LastUtc, state.Day, state.DailiesPlayed, state.PracticePlayed, state.AntesPlayed);
        var (offers, offersDay) = (state.Offers, state.OffersDay);
        Advance(state, action.Utc);
        if (state.OffersDay != state.Day) (state.Offers, state.OffersDay) = (MakeOffers(state, world, state.Day), state.Day);
        try
        {
            return action switch
            {
                StartDaily daily => BeginDaily(state, daily, world),
                StartPractice practice => BeginPractice(state, practice, world),
                StartAnte ante => BeginAnte(state, ante, world),
                FinishMatch result => EndMatch(state, result, world),
                BuyBooster buy => Buy(state, buy, world),
                BuyPrecon buy => Buy(state, buy, world),
                OpenBooster open => Open(state, open, world),
                SellCard sell => Sell(state, sell, world),
                TakeOffer offer => Take(state, offer, world),
                EnterEvent enter => Enter(state, enter, world),
                FinishEvent finish => Finish(state, finish, world),
                _ => throw new CareerRuleException($"Unknown career action {action.GetType().Name}."),
            };
        }
        catch (CareerRuleException)
        {
            (state.LastUtc, state.Day, state.DailiesPlayed, state.PracticePlayed, state.AntesPlayed) = (lastUtc, day, dailies, practices, antes);
            (state.Offers, state.OffersDay) = (offers, offersDay);
            throw;
        }
    }

    /// <summary>Daily matches still to play today (at <paramref name="utc"/>).</summary>
    public static int DailiesLeft(CareerState state, CareerWorld world, DateTime utc) =>
        DayOf(utc) > state.Day ? world.Content.Dailies.PerDay : Math.Max(0, world.Content.Dailies.PerDay - state.DailiesPlayed);

    /// <summary>The opponent and deck of the next daily match at <paramref name="utc"/>: fixed for that day and match, so leaving doesn't change it.</summary>
    public static (OpponentSpec Opponent, string Deck) NextDailyOpponent(CareerState state, CareerWorld world, DateTime utc)
    {
        var day = DayOf(utc) > state.Day ? DayOf(utc) : state.Day;
        int played = DayOf(utc) > state.Day ? 0 : state.DailiesPlayed;
        var random = CareerRandom.For(state.Seed, "daily", day.DayNumber, played);
        var opponents = Opponents(state, world);
        var opponent = opponents[random.Next(opponents.Count)];
        // Not the same regular twice in a row on one day when there is a choice.
        if (played > 0 && opponents.Count > 1 && opponent.Id == PreviousOpponent(state, world, day, played))
            opponent = opponents[(IndexOf(opponents, opponent) + 1 + random.Next(opponents.Count - 1)) % opponents.Count];
        return (opponent, opponent.Decks[random.Next(opponent.Decks.Count)]);
    }

    private static string PreviousOpponent(CareerState state, CareerWorld world, DateOnly day, int played)
    {
        var random = CareerRandom.For(state.Seed, "daily", day.DayNumber, played - 1);
        var opponents = Opponents(state, world);
        return opponents[random.Next(opponents.Count)].Id;
    }

    private static int IndexOf(IReadOnlyList<OpponentSpec> list, OpponentSpec item)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == item) return i;
        return -1;
    }

    /// <summary>The reputation tier reached (index in the content's tiers).</summary>
    public static int Tier(CareerState state, CareerWorld world)
    {
        int tier = 0;
        for (int i = 0; i < world.Content.Tiers.Count; i++)
            if (state.Reputation >= world.Content.Tiers[i].Reputation) tier = i;
        return tier;
    }

    /// <summary>The regulars the player can meet at their tier (all of them if the content gives none for it).</summary>
    public static IReadOnlyList<OpponentSpec> Opponents(CareerState state, CareerWorld world)
    {
        int tier = Tier(state, world);
        var met = world.Content.Opponents.Where(o => o.Tier <= tier).ToList();
        return met.Count > 0 ? met : world.Content.Opponents;
    }

    /// <summary>Paid practice matches left today.</summary>
    public static int PaidPracticeLeft(CareerState state, CareerWorld world, DateTime utc) =>
        Math.Max(0, world.Content.Practice.PaidPerDay - (DayOf(utc) > state.Day ? 0 : state.PracticePlayed));

    public static int AntesLeft(CareerState state, CareerWorld world, DateTime utc) =>
        Math.Max(0, world.Content.Ante.PerDay - (DayOf(utc) > state.Day ? 0 : state.AntesPlayed));

    /// <summary>The week a day belongs to (weeks start on Monday).</summary>
    public static int WeekOf(DateOnly day) => day.DayNumber / 7;

    /// <summary>The challenges of the week of <paramref name="day"/>, drawn from the content's pool by the career seed.</summary>
    public static IReadOnlyList<ChallengeSpec> WeekChallenges(CareerState state, CareerWorld world, DateOnly day)
    {
        var pool = world.Content.Challenges.ToList();
        var random = CareerRandom.For(state.Seed, "challenges", WeekOf(day));
        var chosen = new List<ChallengeSpec>();
        while (chosen.Count < world.Content.ChallengesPerWeek && pool.Count > 0)
        {
            int i = random.Next(pool.Count);
            chosen.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return chosen;
    }

    /// <summary>A challenge's progress in the week of <paramref name="day"/>.</summary>
    public static int Progress(CareerState state, ChallengeSpec challenge, DateOnly day) =>
        state.Challenges.GetValueOrDefault($"{WeekOf(day)}:{challenge.Id}");

    /// <summary>A deck's value: its cards' values (basic lands are worth nothing).</summary>
    public static int DeckValue(DeckList deck, CareerWorld world) => deck.Main.Concat(deck.Sideboard).Sum(e => world.Value(CardKey.Of(e)) * e.Count);

    /// <summary>The colors of a deck's spells' mana costs (W, U, B, R, G).</summary>
    public static HashSet<char> DeckColors(DeckList deck, CareerWorld world)
    {
        var colors = new HashSet<char>();
        foreach (var entry in deck.Main)
            if (world.Cards.Find(entry.Name) is { } card)
                foreach (char c in card.Definition.ManaCost.ToString())
                    if ("WUBRG".Contains(c)) colors.Add(c);
        return colors;
    }

    /// <summary>Why a deck can't be played in a career match (cards not owned, not a legal casual or Commander deck); empty when it can.</summary>
    public static List<string> DeckProblems(DeckList deck, CareerState state, CareerWorld world, bool commander = false)
    {
        var problems = Missing(deck, state, world).Select(m => $"You have {m.Owned} of the {m.Needed} {m.Name} this deck uses.").ToList();
        problems.AddRange(DeckValidator.Validate(deck, commander ? world.CommanderFormat : FormatRules.Casual, world.Cards)
            .Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
        return problems;
    }

    /// <summary>Cards the deck needs more copies of than the collection has, by name (any printing counts; basic lands are free).</summary>
    public static List<MissingCard> Missing(DeckList deck, CareerState state, CareerWorld world) =>
        deck.Main.Concat(deck.Sideboard).Concat(deck.Commander)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => !world.IsBasicLand(g.Key))
            .Select(g => new MissingCard(g.Key, g.Sum(e => e.Count), state.OwnedByName(g.Key)))
            .Where(m => m.Needed > m.Owned)
            .ToList();

    public static string Money(int cents) => cents < 0 ? "-" + Money(-cents) : $"${cents / 100}.{cents % 100:00}";

    // ---------------------------------------------------------------- actions

    private static void Advance(CareerState state, DateTime utc)
    {
        state.LastUtc = utc;
        var day = DayOf(utc);
        if (day > state.Day)
        {
            state.Day = day;
            state.DailiesPlayed = 0;
            state.PracticePlayed = 0;
            state.AntesPlayed = 0;
        }
    }

    private static CareerResult Start(CareerState state, StartCareer start, CareerWorld world)
    {
        if (state.StartedUtc != default) throw new CareerRuleException("The career has already started.");
        if (!world.Content.Starter.Decks.Contains(start.Deck, StringComparer.OrdinalIgnoreCase)) throw new CareerRuleException($"'{start.Deck}' isn't a starting deck.");
        var cards = DeckCards(world.Deck(start.Deck), world);
        state.Seed = start.Seed;
        state.StartedUtc = state.LastUtc = start.Utc;
        state.Day = DayOf(start.Utc);
        state.Cash = world.Content.Starter.Cash;
        foreach (var card in cards) state.AddCard(CardKey.Of(card), 1);
        var featured = world.FeaturedSet(state.Day).ToLowerInvariant();
        if (world.Content.Starter.Boosters > 0) state.AddSealed(featured, world.Content.Starter.Boosters);
        state.Note(start.Utc, $"Started a career at {world.Content.StoreName}.");
        return new CareerResult(cards, state.Cash);
    }

    private static CareerResult BeginDaily(CareerState state, StartDaily daily, CareerWorld world)
    {
        if (state.Pending is not null) throw new CareerRuleException("A match is already in progress.");
        if (daily.Reward is not (CashReward or BoosterReward)) throw new CareerRuleException("Choose cash or a booster as the prize.");
        if (DailiesLeft(state, world, daily.Utc) == 0) throw new CareerRuleException("No daily matches left today.");
        if (daily.Deck is not null) PlayableDeck(daily.Deck, state, world);
        var (opponent, deck) = NextDailyOpponent(state, world, daily.Utc);
        state.DailiesPlayed++;
        state.Pending = new PendingMatch(opponent.Id, deck, daily.Reward) { Set = world.FeaturedSet(state.Day).ToLowerInvariant(), PlayerDeck = daily.Deck ?? "" };
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static CareerResult BeginPractice(CareerState state, StartPractice practice, CareerWorld world)
    {
        if (state.Pending is not null) throw new CareerRuleException("A match is already in progress.");
        var opponent = Opponent(state, world, practice.Opponent);
        PlayableDeck(practice.Deck, state, world);
        var random = CareerRandom.For(state.Seed, Practice, state.Day.DayNumber, state.PracticePlayed);
        state.PracticePlayed++;
        state.Pending = new PendingMatch(opponent.Id, opponent.Decks[random.Next(opponent.Decks.Count)]) { Kind = Practice, PlayerDeck = practice.Deck };
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    /// <summary>A card a regular puts up for an ante match (a card of the deck they'd play, never a basic land) and its price.</summary>
    public sealed record AnteCard(CardKey Card, int Value);

    /// <summary>What a regular offers for the player's next ante match: the deck they'd play and the cards to choose from.</summary>
    public sealed record AnteOffer(OpponentSpec Opponent, string Deck, IReadOnlyList<AnteCard> Cards);

    /// <summary>
    /// The regular's offer for the player's next ante match on a day: fixed for that regular and that match, so
    /// looking and backing out doesn't change it.
    /// </summary>
    public static AnteOffer AnteOfferOf(CareerState state, CareerWorld world, string opponentId, DateOnly day)
    {
        var opponent = Opponents(state, world).FirstOrDefault(o => o.Id == opponentId) ?? throw new CareerRuleException("You can't play that regular yet.");
        int played = day > state.Day ? 0 : state.AntesPlayed;
        long id = opponent.Id.Aggregate(17L, (h, c) => h * 31 + c);
        var random = CareerRandom.For(state.Seed, Ante, day.DayNumber, played, id);
        var deckName = opponent.Decks[random.Next(opponent.Decks.Count)];
        var deck = OpponentDeck(state, world, opponent.Id, deckName);
        var pool = deck.Main.Where(e => !world.IsBasicLand(e.Name)).Select(CardKey.Of).Distinct().ToList();
        if (pool.Count == 0) throw new CareerRuleException($"{opponent.Name} has nothing to stake.");
        var cards = new List<AnteCard>();
        while (cards.Count < world.Content.Ante.Offers && pool.Count > 0)
        {
            int i = random.Next(pool.Count);
            cards.Add(new AnteCard(pool[i], CareerMarket.Price(state, world, pool[i], day)));
            pool.RemoveAt(i);
        }
        return new AnteOffer(opponent, deckName, cards.OrderBy(c => c.Value).ToList());
    }

    /// <summary>Whether the regular plays for this stake against their card: worth at least the content's share of it.</summary>
    public static bool AnteAccepted(CareerState state, CareerWorld world, AnteCard theirs, CardKey stake, DateOnly day) =>
        CareerMarket.Price(state, world, stake, day) >= theirs.Value * world.Content.Ante.MinRatio;

    /// <summary>
    /// An ante match: the player's stake (any card of the collection; in older journals a card at random from their
    /// deck) leaves the collection until the match ends, and the deck for the game when the deck needed that copy; the
    /// regular's card leaves their deck for the game.
    /// </summary>
    private static CareerResult BeginAnte(CareerState state, StartAnte ante, CareerWorld world)
    {
        if (state.Pending is not null) throw new CareerRuleException("A match is already in progress.");
        if (AntesLeft(state, world, ante.Utc) == 0) throw new CareerRuleException("No ante matches left today.");
        var opponent = Opponent(state, world, ante.Opponent);
        var deck = PlayableDeck(ante.Deck, state, world);
        CardKey key, theirs;
        string theirDeck;
        if (ante.Card is { } staked)
        {
            // Any card of the collection can be the stake.
            key = CardKey.Parse(staked);
            if (state.Owned(key) == 0) throw new CareerRuleException($"You have no {key.Name} in that printing.");
            var offer = AnteOfferOf(state, world, opponent.Id, state.Day);
            var chosen = ante.Theirs is { } wanted
                ? offer.Cards.FirstOrDefault(c => c.Card.ToString() == wanted) ?? throw new CareerRuleException($"{opponent.Name} isn't offering that card.")
                : offer.Cards[0];
            if (!AnteAccepted(state, world, chosen, key, state.Day)) throw new CareerRuleException($"{opponent.Name} won't play for {key.Name}.");
            (theirs, theirDeck) = (chosen.Card, offer.Deck);
        }
        else
        {
            var random = CareerRandom.For(state.Seed, Ante, state.Day.DayNumber, state.AntesPlayed);
            theirDeck = opponent.Decks[random.Next(opponent.Decks.Count)];
            var mine = Pick(deck, random);
            theirs = CardKey.Of(Pick(OpponentDeck(state, world, opponent.Id, theirDeck), random));
            // The card leaves the collection in a printing owned: the deck's own when it is, otherwise another one.
            key = CardKey.Of(mine);
            if (state.Owned(key) == 0 && !world.IsBasicLand(mine.Name))
                key = state.Cards.First(c => c.Card.Name.Equals(mine.Name, StringComparison.OrdinalIgnoreCase)).Card;
        }
        state.AddCard(key, -1);
        state.AntesPlayed++;
        state.Pending = new PendingMatch(opponent.Id, theirDeck)
        {
            Kind = Ante, PlayerDeck = ante.Deck, PlayerAnte = key.ToString(), OpponentAnte = theirs.ToString(),
        };
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static DeckEntry Pick(DeckList deck, Random random)
    {
        var cards = deck.Main.SelectMany(e => Enumerable.Repeat(e, e.Count)).ToList();
        if (cards.Count == 0) throw new CareerRuleException("The deck is empty.");
        return cards[random.Next(cards.Count)];
    }

    private static OpponentSpec Opponent(CareerState state, CareerWorld world, string id) =>
        Opponents(state, world).FirstOrDefault(o => o.Id == id) ?? throw new CareerRuleException("You can't play that regular yet.");

    private static DeckList PlayableDeck(string text, CareerState state, CareerWorld world, bool commander = false)
    {
        DeckList deck;
        try { deck = DeckList.Parse(text); }
        catch (FormatException) { throw new CareerRuleException("That deck list can't be read."); }
        if (DeckProblems(deck, state, world, commander) is [var first, ..]) throw new CareerRuleException(first);
        return deck;
    }

    private static CareerResult EndMatch(CareerState state, FinishMatch result, CareerWorld world)
    {
        var pending = state.Pending ?? throw new CareerRuleException("No match is in progress.");
        state.Pending = null;
        var name = world.Content.Opponents.FirstOrDefault(o => o.Id == pending.OpponentId)?.Name ?? pending.OpponentId;
        var outcome = pending.Kind switch
        {
            Practice => EndPractice(state, result, world, name),
            Ante => EndAnte(state, result, pending, name),
            _ => EndDaily(state, result, world, pending, name),
        };
        if (result.Won) state.Wins++;
        else state.Losses++;
        Count(state, world, result.Utc, pending, result.Won);
        return outcome;
    }

    private static CareerResult EndPractice(CareerState state, FinishMatch result, CareerWorld world, string name)
    {
        var practice = world.Content.Practice;
        if (state.PracticePlayed > practice.PaidPerDay)
        {
            state.Note(result.Utc, $"{(result.Won ? "Beat" : "Lost to")} {name} in practice.");
            return new CareerResult(Array.Empty<PoolCard>(), 0);
        }
        int cash = result.Won ? practice.Win : practice.Loss;
        if (result.Won) state.Reputation += 1;
        state.Cash += cash;
        state.Note(result.Utc, $"{(result.Won ? "Beat" : "Lost to")} {name} in practice: {Money(cash)}.");
        return new CareerResult(Array.Empty<PoolCard>(), cash);
    }

    private static CareerResult EndAnte(CareerState state, FinishMatch result, PendingMatch pending, string name)
    {
        var mine = CardKey.Parse(pending.PlayerAnte);
        var theirs = CardKey.Parse(pending.OpponentAnte);
        if (!result.Won)
        {
            state.Note(result.Utc, $"Lost an ante match to {name}: {name} keeps your {mine.Name}.");
            return new CareerResult(Array.Empty<PoolCard>(), 0);
        }
        state.AddCard(mine, 1);
        state.AddCard(theirs, 1);
        state.Reputation += 2;
        state.Note(result.Utc, $"Beat {name} in an ante match: you keep their {theirs.Name}.");
        return new CareerResult(new[] { new PoolCard(theirs.Name, theirs.Set.ToLowerInvariant(), theirs.Number, "", theirs.Foil) }, 0);
    }

    private static CareerResult EndDaily(CareerState state, FinishMatch result, CareerWorld world, PendingMatch pending, string name)
    {
        var dailies = world.Content.Dailies;
        int cash = 0;
        if (result.Won)
        {
            state.Reputation += 3;
            if (pending.Reward == BoosterReward)
            {
                state.AddSealed(pending.Set, dailies.WinBoosters);
                state.Note(result.Utc, $"Beat {name} in a daily match: {dailies.WinBoosters} {world.SetName(pending.Set)} booster{(dailies.WinBoosters == 1 ? "" : "s")}.");
            }
            else
            {
                cash = dailies.Win;
                state.Note(result.Utc, $"Beat {name} in a daily match: {Money(cash)}.");
            }
        }
        else
        {
            state.Reputation += 1;
            cash = dailies.Loss;
            state.Note(result.Utc, $"Lost a daily match to {name}: {Money(cash)}.");
        }
        state.Cash += cash;
        return new CareerResult(Array.Empty<PoolCard>(), cash);
    }

    // ---------------------------------------------------------------- rivals

    /// <summary>
    /// A regular's deck as they play it now: the module's deck, and for a rival, improved every week of the career
    /// (up to the content's limit): each week their least valuable spells give way to better cards of their colors
    /// and about the same mana value from the sets the store sells.
    /// </summary>
    public static DeckList OpponentDeck(CareerState state, CareerWorld world, string opponentId, string deckName)
    {
        var deck = world.Deck(deckName);
        var opponent = world.Content.Opponents.FirstOrDefault(o => o.Id == opponentId);
        int weeks = Math.Min(world.Content.Offers.RivalWeeks, WeekOf(state.Day) - WeekOf(DayOf(state.StartedUtc)));
        if (opponent is not { Rival: true } || weeks <= 0) return deck;

        var improved = DeckList.Parse(deck.Export());
        var colors = DeckColors(deck, world);
        var pool = world.Content.Sets.SelectMany(world.Playable)
            .Where(c => world.Cards.Find(c.Card.Name) is { } e && !e.Definition.Is(CardType.Land)
                && e.Definition.ManaCost.ToString().Where(ch => "WUBRG".Contains(ch)).All(colors.Contains))
            .GroupBy(c => c.Card.Name).Select(g => g.First()).ToList();
        for (int week = 1; week <= weeks; week++)
        {
            var random = CareerRandom.For(state.Seed, "rival:" + opponent.Id, week);
            for (int n = 0; n < world.Content.Offers.RivalUpgrades; n++)
            {
                // The least valuable spell goes; a better card of about its mana value, not yet four times in the deck, comes in.
                var weakest = improved.Main
                    .Where(e => world.Cards.Find(e.Name) is { } c && !c.Definition.Is(CardType.Land))
                    .OrderBy(e => world.Value(CardKey.Of(e))).ThenBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault();
                if (weakest is null) break;
                int value = world.Value(CardKey.Of(weakest));
                int manaValue = world.Cards.Find(weakest.Name)!.Definition.ManaCost.ManaValue;
                var better = pool.Where(c => c.Value > value && Math.Abs(world.Cards.Find(c.Card.Name)!.Definition.ManaCost.ManaValue - manaValue) <= 1
                        && improved.Main.Where(e => e.Name == c.Card.Name).Sum(e => e.Count) < 4).ToList();
                if (better.Count == 0) break;
                var chosen = better[random.Next(better.Count)].Card;
                DeckList.Adjust(improved.Main, weakest.Name, -1, weakest.Set, weakest.Number);
                DeckList.Adjust(improved.Main, chosen.Name, 1, chosen.Set.ToUpperInvariant(), chosen.Number);
            }
        }
        return improved;
    }

    // ---------------------------------------------------------------- the store's offers

    public const string Bargain = "bargain", Trade = "trade";

    /// <summary>
    /// The store's offers on a day: made when the day begins (the first action that day) and the same all day. Before
    /// that, what they will be: a regular selling a rare below its value, and a regular offering a card from their deck
    /// for one of the player's cards of about the same value.
    /// </summary>
    public static IReadOnlyList<CareerOffer> Offers(CareerState state, CareerWorld world, DateOnly day) =>
        state.OffersDay == day ? state.Offers : MakeOffers(state, world, day);

    private static List<CareerOffer> MakeOffers(CareerState state, CareerWorld world, DateOnly day)
    {
        var offers = new List<CareerOffer>();
        var random = CareerRandom.For(state.Seed, "offers", day.DayNumber);
        var opponents = world.Content.Opponents;
        var sets = world.Content.Sets.Where(world.SellsBoosters).ToList();
        if (sets.Count == 0 || opponents.Count == 0) return offers;

        var seller = opponents[random.Next(opponents.Count)];
        var rares = world.Playable(sets[random.Next(sets.Count)]).Where(c => c.Card.Rarity is "rare" or "mythic").ToList();
        if (rares.Count > 0)
        {
            var (card, _) = rares[random.Next(rares.Count)];
            int price = CareerMarket.Price(state, world, CardKey.Of(card), day);
            offers.Add(new CareerOffer(offers.Count, Bargain, seller.Id, CardKey.Of(card).ToString(), "", Math.Max(1, (int)(price * world.Content.Offers.BargainRate))));
        }

        var trader = opponents[random.Next(opponents.Count)];
        var theirDeck = OpponentDeck(state, world, trader.Id, trader.Decks[random.Next(trader.Decks.Count)]);
        var tradeable = theirDeck.Main.Where(e => !world.IsBasicLand(e.Name) && world.Value(CardKey.Of(e)) > world.Content.Values.GetValueOrDefault("common")).ToList();
        if (tradeable.Count > 0)
        {
            var get = CardKey.Of(tradeable[random.Next(tradeable.Count)]);
            int value = CareerMarket.Price(state, world, get, day);
            double spread = world.Content.Offers.TradeSpread;
            var wanted = state.Cards.Select(c => c.Card)
                .Where(c => !world.IsBasicLand(c.Name) && c.Name != get.Name)
                .Where(c => CareerMarket.Price(state, world, c, day) is var p && p >= value * (1 - spread) && p <= value * (1 + spread))
                .ToList();
            if (wanted.Count > 0)
                offers.Add(new CareerOffer(offers.Count, Trade, trader.Id, get.ToString(), wanted[random.Next(wanted.Count)].ToString(), 0));
        }
        return offers;
    }

    public static bool OfferTaken(CareerState state, DateOnly day, int index) => state.OffersTaken.Contains($"{day.DayNumber}:{index}");

    private static CareerResult Take(CareerState state, TakeOffer take, CareerWorld world)
    {
        var offers = Offers(state, world, state.Day);
        if (take.Offer < 0 || take.Offer >= offers.Count) throw new CareerRuleException("That offer isn't there today.");
        if (OfferTaken(state, state.Day, take.Offer)) throw new CareerRuleException("You already took that offer.");
        var offer = offers[take.Offer];
        var get = CardKey.Parse(offer.Get);
        var name = world.Content.Opponents.FirstOrDefault(o => o.Id == offer.OpponentId)?.Name ?? offer.OpponentId;
        if (offer.Kind == Bargain)
        {
            Pay(state, offer.Price);
            state.Note(take.Utc, $"Bought {get.Name} from {name} for {Money(offer.Price)}.");
        }
        else
        {
            var give = CardKey.Parse(offer.Give);
            // Any printing of the asked card will do.
            var owned = state.Owned(give) > 0 ? give
                : state.Cards.Where(c => c.Card.Name.Equals(give.Name, StringComparison.OrdinalIgnoreCase)).Select(c => (CardKey?)c.Card).FirstOrDefault()
                  ?? throw new CareerRuleException($"You have no {give.Name} to trade.");
            state.AddCard(owned, -1);
            state.Note(take.Utc, $"Traded your {give.Name} to {name} for {get.Name}.");
        }
        state.AddCard(get, 1);
        state.OffersTaken.Add($"{state.Day.DayNumber}:{take.Offer}");
        if (state.OffersTaken.Count > 60) state.OffersTaken.RemoveAt(0);
        return new CareerResult(new[] { new PoolCard(get.Name, get.Set.ToLowerInvariant(), get.Number, "", get.Foil) }, offer.Kind == Bargain ? -offer.Price : 0);
    }

    // ---------------------------------------------------------------- the calendar

    public const string Draft = "draft", Constructed = "constructed", Commander = "commander";
    public const string PayCash = "cash", PayBoosters = "boosters", PayTokens = "tokens";

    /// <summary>The store's events on a day (UTC).</summary>
    public static IReadOnlyList<EventSpec> EventsOn(CareerWorld world, DateOnly day) =>
        world.Content.Events.Where(e => Happens(e, day)).ToList();

    public static bool Happens(EventSpec e, DateOnly day) => e.When.Equals("lastSunday", StringComparison.OrdinalIgnoreCase)
        ? day.DayOfWeek == DayOfWeek.Sunday && day.AddDays(7).Month != day.Month
        : Enum.TryParse<DayOfWeek>(e.When, true, out var weekday) && day.DayOfWeek == weekday;

    /// <summary>The next day (from <paramref name="from"/>, included) the event happens.</summary>
    public static DateOnly NextDay(EventSpec e, DateOnly from)
    {
        for (int i = 0; i < 40; i++) if (Happens(e, from.AddDays(i))) return from.AddDays(i);
        return from;
    }

    /// <summary>The set a draft event opens on a day.</summary>
    public static string EventSet(CareerWorld world, EventSpec e, DateOnly day) =>
        (e.Set.Equals("featured", StringComparison.OrdinalIgnoreCase) ? world.FeaturedSet(day) : e.Set).ToLowerInvariant();

    public static bool Played(CareerState state, EventSpec e, DateOnly day) => state.EventsPlayed.Contains($"{day.DayNumber}:{e.Id}");

    /// <summary>What the player wins with a record and a place: the first prize that fits.</summary>
    public static PrizeSpec? PrizeFor(EventSpec e, int wins, int place) =>
        e.Prizes.FirstOrDefault(p => p.Wins >= 0 ? wins >= p.Wins : p.Place == 0 || place <= p.Place);

    private static CareerResult Enter(CareerState state, EnterEvent enter, CareerWorld world)
    {
        if (state.Event is not null) throw new CareerRuleException("You are already in an event.");
        if (state.Pending is not null) throw new CareerRuleException("Finish your match first.");
        var e = world.Content.Events.FirstOrDefault(x => x.Id == enter.Event) ?? throw new CareerRuleException("There is no such event.");
        if (!Happens(e, state.Day)) throw new CareerRuleException($"{e.Name} isn't today.");
        if (Played(state, e, state.Day)) throw new CareerRuleException($"You already played {e.Name} today.");
        if (Tier(state, world) < e.MinTier) throw new CareerRuleException($"{e.Name} needs a {world.Content.Tiers[Math.Min(e.MinTier, world.Content.Tiers.Count - 1)].Name}'s reputation.");
        string deck = "";
        if (e.Kind is Constructed or Commander)
        {
            if (enter.Deck is null) throw new CareerRuleException(e.Kind == Commander ? "Choose a Commander deck." : "Choose a deck.");
            PlayableDeck(enter.Deck, state, world, e.Kind == Commander);
            deck = enter.Deck;
        }
        var set = e.Kind == Draft ? EventSet(world, e, state.Day) : "";
        switch (enter.Pay)
        {
            case PayCash: Pay(state, e.Entry); break;
            case PayBoosters when e.EntryBoosters > 0 && e.Kind == Draft:
                if (state.Sealed.GetValueOrDefault(set) < e.EntryBoosters) throw new CareerRuleException($"It takes {e.EntryBoosters} {world.SetName(set)} boosters.");
                state.AddSealed(set, -e.EntryBoosters);
                break;
            case PayTokens when e.EntryTokens > 0:
                if (state.Tokens < e.EntryTokens) throw new CareerRuleException($"It takes {e.EntryTokens} qualification token{(e.EntryTokens == 1 ? "" : "s")}.");
                state.Tokens -= e.EntryTokens;
                break;
            default: throw new CareerRuleException("That's no way to pay this entry.");
        }
        var seed = (ulong)CareerRandom.For(state.Seed, "event:" + e.Id, state.Day.DayNumber).NextInt64();
        state.Event = new EventEntry(e.Id, state.Day, seed, set, deck);
        state.EventsPlayed.Add($"{state.Day.DayNumber}:{e.Id}");
        if (state.EventsPlayed.Count > 100) state.EventsPlayed.RemoveAt(0);
        state.Note(enter.Utc, $"Entered {e.Name}.");
        return new CareerResult(Array.Empty<PoolCard>(), enter.Pay == PayCash ? -e.Entry : 0);
    }

    private static CareerResult Finish(CareerState state, FinishEvent finish, CareerWorld world)
    {
        var entry = state.Event ?? throw new CareerRuleException("You aren't in an event.");
        var e = world.Content.Events.FirstOrDefault(x => x.Id == entry.Id) ?? throw new CareerRuleException("There is no such event.");
        if (finish.Wins + finish.Losses + finish.Draws > e.Rounds || finish.Place < 1 || finish.Place > e.Players)
            throw new CareerRuleException("That result can't be.");
        var pool = new List<PoolCard>();
        if (e.Kind == Draft)
        {
            // The drafted cards are the player's to keep: of the event's set (or a sheet of its boosters), a draft's worth.
            var generator = world.Generator(entry.Set);
            int expected = world.Sets[entry.Set].BoostersPerPlayer * generator.Set.Booster!.CardCount;
            var cards = (finish.Pool ?? Array.Empty<string>()).Select(CardKey.Parse).ToList();
            if (cards.Count > expected || cards.Any(c => world.Cards.Find(c.Name) is null)) throw new CareerRuleException("Those aren't the cards of this draft.");
            foreach (var card in cards)
            {
                state.AddCard(card, 1);
                pool.Add(new PoolCard(card.Name, card.Set.ToLowerInvariant(), card.Number, "", card.Foil));
            }
        }
        state.Event = null;
        state.Wins += finish.Wins;
        state.Losses += finish.Losses;
        // The event's wins count for the week's "win matches" challenges.
        foreach (var challenge in WeekChallenges(state, world, state.Day).Where(c => c.Kind == "winMatch"))
            for (int i = 0; i < finish.Wins; i++) AdvanceChallenge(state, world, finish.Utc, challenge);
        var prize = PrizeFor(e, finish.Wins, finish.Place);
        var won = new List<string>();
        if (prize is not null)
        {
            state.Cash += prize.Cash;
            var set = (e.Kind == Draft ? entry.Set : world.FeaturedSet(entry.Day)).ToLowerInvariant();
            if (prize.Boosters > 0) state.AddSealed(set, prize.Boosters);
            state.Tokens += prize.Tokens;
            state.Reputation += prize.Reputation;
            if (prize.Cash > 0) won.Add(Money(prize.Cash));
            if (prize.Boosters > 0) won.Add($"{prize.Boosters} {world.SetName(set)} booster{(prize.Boosters == 1 ? "" : "s")}");
            if (prize.Tokens > 0) won.Add($"{prize.Tokens} qualification token{(prize.Tokens == 1 ? "" : "s")}");
        }
        state.Note(finish.Utc, $"{e.Name}: {Ordinal(finish.Place)} of {e.Players}, {finish.Wins}-{finish.Losses}{(finish.Draws > 0 ? $"-{finish.Draws}" : "")}"
            + (won.Count > 0 ? $"; won {string.Join(", ", won)}." : "."));
        return new CareerResult(pool, prize?.Cash ?? 0);
    }

    public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ---------------------------------------------------------------- challenges

    /// <summary>Counts a finished match for the week's challenges (and pays the ones it completes).</summary>
    private static void Count(CareerState state, CareerWorld world, DateTime utc, PendingMatch match, bool won)
    {
        DeckList? deck = null;
        if (match.PlayerDeck.Length > 0)
            try { deck = DeckList.Parse(match.PlayerDeck); } catch (FormatException) { }
        foreach (var challenge in WeekChallenges(state, world, state.Day))
        {
            bool counts = challenge.Kind switch
            {
                "winDaily" => won && match.Kind == Daily,
                "winMatch" => won,
                "winAnte" => won && match.Kind == Ante,
                "playPractice" => match.Kind == Practice,
                "winCheap" => won && deck is not null && DeckValue(deck, world) <= challenge.Value,
                "winMono" => won && deck is not null && DeckColors(deck, world).Count == 1,
                _ => false,
            };
            if (counts) AdvanceChallenge(state, world, utc, challenge);
        }
    }

    private static void AdvanceChallenge(CareerState state, CareerWorld world, DateTime utc, ChallengeSpec challenge)
    {
        var key = $"{WeekOf(state.Day)}:{challenge.Id}";
        int progress = state.Challenges.GetValueOrDefault(key);
        if (progress >= challenge.Count) return;
        state.Challenges[key] = ++progress;
        if (progress < challenge.Count) return;
        state.Cash += challenge.Cash;
        var set = world.FeaturedSet(state.Day).ToLowerInvariant();
        if (challenge.Boosters > 0) state.AddSealed(set, challenge.Boosters);
        state.Reputation += 2;
        var prize = string.Join(" and ", new[]
        {
            challenge.Cash > 0 ? Money(challenge.Cash) : null,
            challenge.Boosters > 0 ? $"{challenge.Boosters} {world.SetName(set)} booster{(challenge.Boosters == 1 ? "" : "s")}" : null,
        }.OfType<string>());
        state.Note(utc, $"Challenge done: {challenge.Text}{(prize.Length > 0 ? $" ({prize})" : "")}.");
    }

    private static CareerResult Buy(CareerState state, BuyBooster buy, CareerWorld world)
    {
        var key = SealedKey(buy.Set, buy.Kind);
        if (!world.BoosterKinds(buy.Set.ToLowerInvariant()).Contains(string.IsNullOrEmpty(buy.Kind) ? null : buy.Kind))
            throw new CareerRuleException($"The store doesn't sell {world.BoosterName(key)}s.");
        int price = world.BoosterPrice(buy.Kind);
        Pay(state, price);
        state.AddSealed(key, 1);
        state.Note(buy.Utc, $"Bought a {world.BoosterName(key)}.");
        return new CareerResult(Array.Empty<PoolCard>(), -price);
    }

    private static CareerResult Buy(CareerState state, BuyPrecon buy, CareerWorld world)
    {
        var precon = world.Precon(buy.Deck) ?? throw new CareerRuleException("The store doesn't sell that deck.");
        var cards = DeckCards(world.Deck(precon.Deck), world);
        int price = world.PreconPrice(precon);
        Pay(state, price);
        foreach (var card in cards) state.AddCard(CardKey.Of(card), 1);
        state.Note(buy.Utc, $"Bought a preconstructed deck ({cards.Count} cards).");
        return new CareerResult(cards, -price);
    }

    private static CareerResult Open(CareerState state, OpenBooster open, CareerWorld world)
    {
        var key = SealedKey(open.Set, open.Kind);
        if (state.Sealed.GetValueOrDefault(key) == 0) throw new CareerRuleException($"No unopened {world.BoosterName(key)} booster.");
        var cards = world.Generator(open.Set.ToLowerInvariant(), open.Kind).Open(CareerRandom.For(state.Seed, "booster", state.BoostersOpened));
        state.BoostersOpened++;
        state.AddSealed(key, -1);
        foreach (var card in cards) state.AddCard(CardKey.Of(card), 1);
        foreach (var challenge in WeekChallenges(state, world, state.Day).Where(c => c.Kind == "openBoosters"))
            AdvanceChallenge(state, world, open.Utc, challenge);
        return new CareerResult(cards, 0);
    }

    private static CareerResult Sell(CareerState state, SellCard sell, CareerWorld world)
    {
        var card = CardKey.Parse(sell.Card);
        if (sell.Count < 1 || state.Owned(card) < sell.Count) throw new CareerRuleException($"You don't have {sell.Count} {card.Name}.");
        int cash = CareerMarket.SellPrice(state, world, card, state.Day) * sell.Count;
        state.AddCard(card, -sell.Count);
        state.Cash += cash;
        state.Note(sell.Utc, $"Sold {sell.Count} {card.Name} for {Money(cash)}.");
        return new CareerResult(Array.Empty<PoolCard>(), cash);
    }

    private static void Pay(CareerState state, int price)
    {
        if (state.Cash < price) throw new CareerRuleException($"Not enough cash: it costs {Money(price)}.");
        state.Cash -= price;
    }

    /// <summary>Every card of a deck (main, sideboard and commanders) as single cards in their printings.</summary>
    private static List<PoolCard> DeckCards(DeckList deck, CareerWorld world)
    {
        var cards = new List<PoolCard>();
        foreach (var entry in deck.Main.Concat(deck.Sideboard).Concat(deck.Commander))
        {
            var rarity = world.Cards.Find(entry.Name)?.Record.Printings
                .FirstOrDefault(p => p.Set.Equals(entry.Set ?? "", StringComparison.OrdinalIgnoreCase) && p.CollectorNumber == entry.Number)?.Rarity ?? "";
            for (int i = 0; i < entry.Count; i++) cards.Add(new PoolCard(entry.Name, entry.Set?.ToLowerInvariant() ?? "", entry.Number ?? "", rarity, entry.Foil));
        }
        return cards;
    }
}
