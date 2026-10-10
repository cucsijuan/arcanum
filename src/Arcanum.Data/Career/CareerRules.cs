// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Arcanum.Data.Limited;

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

    public static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(utc);

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
        var (lastUtc, day, played) = (state.LastUtc, state.Day, state.DailiesPlayed);
        Advance(state, action.Utc);
        try
        {
            return action switch
            {
                StartDaily daily => BeginDaily(state, daily, world),
                FinishDaily result => EndDaily(state, result, world),
                BuyBooster buy => Buy(state, buy, world),
                BuyPrecon buy => Buy(state, buy, world),
                OpenBooster open => Open(state, open, world),
                SellCard sell => Sell(state, sell, world),
                _ => throw new CareerRuleException($"Unknown career action {action.GetType().Name}."),
            };
        }
        catch (CareerRuleException)
        {
            (state.LastUtc, state.Day, state.DailiesPlayed) = (lastUtc, day, played);
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
        var opponents = world.Content.Opponents;
        var opponent = opponents[random.Next(opponents.Count)];
        // Not the same regular twice in a row on one day when there is a choice.
        if (played > 0 && opponents.Count > 1 && opponent.Id == PreviousOpponent(state, world, day, played))
            opponent = opponents[(opponents.ToList().IndexOf(opponent) + 1 + random.Next(opponents.Count - 1)) % opponents.Count];
        return (opponent, opponent.Decks[random.Next(opponent.Decks.Count)]);
    }

    private static string PreviousOpponent(CareerState state, CareerWorld world, DateOnly day, int played)
    {
        var random = CareerRandom.For(state.Seed, "daily", day.DayNumber, played - 1);
        return world.Content.Opponents[random.Next(world.Content.Opponents.Count)].Id;
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
        if (state.Pending is not null) throw new CareerRuleException("A daily match is already in progress.");
        if (daily.Reward is not (CashReward or BoosterReward)) throw new CareerRuleException("Choose cash or a booster as the prize.");
        if (DailiesLeft(state, world, daily.Utc) == 0) throw new CareerRuleException("No daily matches left today.");
        var (opponent, deck) = NextDailyOpponent(state, world, daily.Utc);
        state.DailiesPlayed++;
        state.Pending = new PendingDaily(opponent.Id, deck, daily.Reward) { Set = world.FeaturedSet(state.Day).ToLowerInvariant() };
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static CareerResult EndDaily(CareerState state, FinishDaily result, CareerWorld world)
    {
        var pending = state.Pending ?? throw new CareerRuleException("No daily match is in progress.");
        state.Pending = null;
        var dailies = world.Content.Dailies;
        var name = world.Content.Opponents.FirstOrDefault(o => o.Id == pending.OpponentId)?.Name ?? pending.OpponentId;
        int cash = 0;
        if (result.Won)
        {
            state.Wins++;
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
            state.Losses++;
            state.Reputation += 1;
            cash = dailies.Loss;
            state.Note(result.Utc, $"Lost a daily match to {name}: {Money(cash)}.");
        }
        state.Cash += cash;
        return new CareerResult(Array.Empty<PoolCard>(), cash);
    }

    private static CareerResult Buy(CareerState state, BuyBooster buy, CareerWorld world)
    {
        if (!world.SellsBoosters(buy.Set)) throw new CareerRuleException($"The store doesn't sell {world.SetName(buy.Set)} boosters.");
        Pay(state, world.Content.BoosterPrice);
        state.AddSealed(buy.Set.ToLowerInvariant(), 1);
        state.Note(buy.Utc, $"Bought a {world.SetName(buy.Set)} booster.");
        return new CareerResult(Array.Empty<PoolCard>(), -world.Content.BoosterPrice);
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
        var set = open.Set.ToLowerInvariant();
        if (state.Sealed.GetValueOrDefault(set) == 0) throw new CareerRuleException($"No unopened {world.SetName(set)} booster.");
        var cards = world.Generator(set).Open(CareerRandom.For(state.Seed, "booster", state.BoostersOpened));
        state.BoostersOpened++;
        state.AddSealed(set, -1);
        foreach (var card in cards) state.AddCard(CardKey.Of(card), 1);
        return new CareerResult(cards, 0);
    }

    private static CareerResult Sell(CareerState state, SellCard sell, CareerWorld world)
    {
        var card = CardKey.Parse(sell.Card);
        if (sell.Count < 1 || state.Owned(card) < sell.Count) throw new CareerRuleException($"You don't have {sell.Count} {card.Name}.");
        int cash = world.SellPrice(card) * sell.Count;
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
            for (int i = 0; i < entry.Count; i++) cards.Add(new PoolCard(entry.Name, entry.Set?.ToLowerInvariant() ?? "", entry.Number ?? "", rarity));
        }
        return cards;
    }
}
