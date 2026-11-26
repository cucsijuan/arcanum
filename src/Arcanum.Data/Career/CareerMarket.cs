// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;

namespace Arcanum.Data.Career;

/// <summary>The decks that won the store's weekly tournaments (the week's meta): their cards are in demand.</summary>
/// <param name="Opponent">The regular who won.</param>
public sealed record MetaWin(OpponentSpec Opponent, DeckList Deck);

/// <summary>
/// The store's prices: each card's value (from its rarity, twice for a foil) times a market factor that moves with the
/// career's weeks. The decks that won the store's weekly tournaments put their cards in demand for a few weeks; the
/// week's featured set is opened a lot, so its cards are a little cheaper; and every card drifts a little from week
/// to week. Everything comes from the career's seed and the day, so the prices are the same on every device.
/// </summary>
public static class CareerMarket
{
    private static readonly double[] MetaDemand = { 0.6, 0.35, 0.15 }; // this week, last week, the week before

    /// <summary>The weekly tournament winners of the week (two of the regulars, rivals more often).</summary>
    public static IReadOnlyList<MetaWin> Meta(CareerState state, CareerWorld world, int week)
    {
        // A rival's deck improves week by week, up to the week the career is in: the winners are that week's too.
        var key = (state.Seed, week, CareerRules.WeekOf(state.Day));
        if (world.MetaCache.TryGetValue(key, out var cached)) return cached;
        var random = CareerRandom.For(state.Seed, "meta", week);
        var weighted = world.Content.Opponents.SelectMany(o => Enumerable.Repeat(o, o.Rival ? 3 : 1)).ToList();
        var wins = new List<MetaWin>();
        while (wins.Count < Math.Min(2, world.Content.Opponents.Count) && weighted.Count > 0)
        {
            var winner = weighted[random.Next(weighted.Count)];
            weighted.RemoveAll(o => o.Id == winner.Id);
            var deck = CareerRules.OpponentDeck(state, world, winner.Id, winner.Decks[random.Next(winner.Decks.Count)]);
            wins.Add(new MetaWin(winner, deck));
        }
        return world.MetaCache[key] = wins;
    }

    /// <summary>The names of the cards the week's winners play (the prices of a whole collection ask for each card).</summary>
    private static HashSet<string> MetaNames(CareerState state, CareerWorld world, int week)
    {
        var key = (state.Seed, week, CareerRules.WeekOf(state.Day));
        if (world.MetaNamesCache.TryGetValue(key, out var cached)) return cached;
        return world.MetaNamesCache[key] = Meta(state, world, week).SelectMany(w => w.Deck.Main).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Works out ahead the tournament winners a day's prices need, for a worker thread (seconds on slow devices): they come
    /// from the career's seed, its start and the day it's on, so the career itself isn't needed.
    /// </summary>
    public static void Prepare(ulong seed, DateTime startedUtc, DateOnly careerDay, CareerWorld world, DateOnly day)
    {
        foreach (var on in new[] { careerDay, day }.DistinctBy(CareerRules.WeekOf))
            for (int ago = 0; ago < MetaDemand.Length; ago++)
                Meta(new CareerState { Seed = seed, StartedUtc = startedUtc, Day = on }, world, CareerRules.WeekOf(day) - ago);
    }

    /// <summary>How a card's price compares to its value on a day (1: at its value).</summary>
    public static double Factor(CareerState state, CareerWorld world, string name, DateOnly day)
    {
        int week = CareerRules.WeekOf(day);
        // Drift: from this week's level to next week's through the week.
        double through = (day.DayNumber % 7) / 7.0;
        long hash = NameHash(name);
        double factor = Drift(state.Seed, hash, week) * (1 - through) + Drift(state.Seed, hash, week + 1) * through;
        for (int ago = 0; ago < MetaDemand.Length; ago++)
            if (MetaNames(state, world, week - ago).Contains(name))
                factor *= 1 + MetaDemand[ago];
        var featured = world.FeaturedSet(day);
        if (world.Cards.Find(name)?.Record.Printings.Any(p => p.Set.Equals(featured, StringComparison.OrdinalIgnoreCase)) == true)
            factor *= 0.9;
        return Math.Clamp(factor, 0.5, 3);
    }

    /// <summary>A card's price on a day (its value times the market).</summary>
    public static int Price(CareerState state, CareerWorld world, CardKey card, DateOnly day)
    {
        int value = world.Value(card);
        if (value == 0) return 0;
        double art = Trading(world, day) && world.ArtOf(card) != CardArt.Usual ? world.Content.Market.SpecialPrice : 1;
        return Math.Max(1, (int)Math.Round(value * art * Factor(state, world, card.Name, day)));
    }

    /// <summary>A card's price on a day as if it were its usual art (special arts cost more, see <see cref="MarketSpec"/>).</summary>
    public static int UsualPrice(CareerState state, CareerWorld world, CardKey card, DateOnly day)
    {
        int value = world.Value(card);
        return value == 0 ? 0 : Math.Max(1, (int)Math.Round(value * Factor(state, world, card.Name, day)));
    }

    /// <summary>Whether singles are traded on a day (see <see cref="MarketSpec.Since"/>).</summary>
    public static bool Trading(CareerWorld world, DateOnly day) => world.Content.Market.Since is { } since && day >= since;

    /// <summary>What the store pays for one copy on a day.</summary>
    public static int SellPrice(CareerState state, CareerWorld world, CardKey card, DateOnly day) =>
        (int)Math.Floor(Price(state, world, card, day) * world.Content.SellRate);

    /// <summary>A card's own drift in a week: between 0.85 and 1.15.</summary>
    private static double Drift(ulong seed, long nameHash, int week) =>
        0.85 + 0.3 * CareerRandom.For(seed, "drift", nameHash, week).NextDouble();

    private static long NameHash(string name)
    {
        long hash = 17;
        foreach (char c in name.ToLowerInvariant()) hash = hash * 31 + c;
        return hash;
    }
}
