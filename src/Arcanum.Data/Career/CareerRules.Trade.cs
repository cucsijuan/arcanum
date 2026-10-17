// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Limited;

namespace Arcanum.Data.Career;

/// <summary>
/// Single cards (see <see cref="MarketSpec"/>): the store's case, the forum's listings with their haggling, and the player's
/// own sale posts. Everything comes from the career's seed and the day, so the server works out the same. No way of
/// buying here ever costs less than what the store pays for the card, so buying and selling straight back never pays.
/// </summary>
public static partial class CareerRules
{
    // ---------------------------------------------------------------- what's on offer

    /// <summary>The store's case of singles on a day (empty before singles are traded).</summary>
    public static IReadOnlyList<CareerSingle> StoreSingles(CareerState state, CareerWorld world, DateOnly day)
    {
        var singles = new List<CareerSingle>();
        if (!CareerMarket.Trading(world, day)) return singles;
        var market = world.Content.Market;
        var random = CareerRandom.For(state.Seed, "singles", day.DayNumber);
        for (int i = 0; i < market.SinglesPerDay; i++)
        {
            if (PickSingle(world, random, 1) is not var (card, art, rarity)) break;
            int price = (int)Math.Round(CareerMarket.Price(state, world, card, day) * (1 + market.Markup.GetValueOrDefault(rarity, 0.3)));
            if (art != CardArt.Usual) price = Math.Max(price, (int)Math.Round(CareerMarket.UsualPrice(state, world, card, day) * market.StoreSpecialMin));
            price = Math.Max(price, CareerMarket.SellPrice(state, world, card, day) + 1);
            singles.Add(new CareerSingle(singles.Count, card, price, art));
        }
        return singles;
    }

    /// <summary>The forum's listings on a day: other players' cards at prices that stray either way from the card's price.</summary>
    public static IReadOnlyList<CareerListing> Listings(CareerState state, CareerWorld world, DateOnly day)
    {
        var listings = new List<CareerListing>();
        if (!CareerMarket.Trading(world, day)) return listings;
        var market = world.Content.Market;
        var random = CareerRandom.For(state.Seed, "listings", day.DayNumber);
        var sellers = world.Content.Opponents.Select(o => o.Name).Concat(world.Content.Visitors).ToList();
        if (sellers.Count == 0) sellers.Add("A player");
        int count = market.ListingsMin + random.Next(Math.Max(1, market.ListingsMax - market.ListingsMin + 1));
        for (int i = 0; i < count; i++)
        {
            if (PickSingle(world, random, market.ForumSpecialFactor) is not var (card, art, rarity)) break;
            var (least, most) = SpreadOf(market, rarity);
            double stray = least + random.NextDouble() * (most - least);
            int price = (int)Math.Round(CareerMarket.Price(state, world, card, day) * (1 + (random.Next(2) == 0 ? -stray : stray)));
            if (art != CardArt.Usual) price = Math.Max(price, (int)Math.Round(CareerMarket.UsualPrice(state, world, card, day) * market.ForumSpecialMin));
            int storePays = CareerMarket.SellPrice(state, world, card, day);
            price = Math.Max(price, storePays + 1);
            int floor = Math.Max(storePays + 1, (int)Math.Round(price * (market.FloorMin + random.NextDouble() * (market.FloorMax - market.FloorMin))));
            var seller = sellers[random.Next(sellers.Count)];
            string Fill(IReadOnlyList<string> lines) => (lines.Count == 0 ? "" : lines[random.Next(lines.Count)])
                .Replace("{card}", card.Name).Replace("{price}", Money(price));
            listings.Add(new CareerListing(listings.Count, seller, card, price, art, Math.Min(floor, price), Fill(market.ListingTitles), Fill(market.ListingBodies)));
        }
        return listings;
    }

    /// <summary>A card for the case or the forum: a set on the shelf, then a special art (by its chances) or a usual card of a weighted rarity, maybe foil.</summary>
    private static (CardKey Card, CardArt Art, string Rarity)? PickSingle(CareerWorld world, Random random, double specialFactor)
    {
        var market = world.Content.Market;
        var sets = world.Content.Sets.Where(world.SellsBoosters).ToList();
        if (sets.Count == 0) return null;
        var candidates = world.Singles(sets[random.Next(sets.Count)]);
        double roll = random.NextDouble();
        var art = roll < market.SpecialCollectorChance * specialFactor ? CardArt.SpecialCollector
            : roll < (market.SpecialCollectorChance + market.SpecialPlayChance) * specialFactor ? CardArt.SpecialPlay : CardArt.Usual;
        var pool = candidates.Where(c => c.Art == art).ToList();
        if (pool.Count == 0) pool = candidates.Where(c => c.Art == CardArt.Usual).ToList();
        if (pool.Count == 0) return null;
        if (pool[0].Art == CardArt.Usual)
        {
            var rarities = market.RarityWeights.Where(w => w.Value > 0 && pool.Any(c => Rarity(c.Card) == w.Key)).ToList();
            if (rarities.Count > 0)
            {
                int pick = random.Next(rarities.Sum(r => r.Value));
                var rarity = rarities.First(r => (pick -= r.Value) < 0).Key;
                pool = pool.Where(c => Rarity(c.Card) == rarity).ToList();
            }
        }
        var chosen = pool[random.Next(pool.Count)];
        bool foil = random.NextDouble() < market.FoilChance || FoilOnly(world, chosen.Card);
        return (CardKey.Of(chosen.Card with { Foil = foil }), chosen.Art, Rarity(chosen.Card));
    }

    private static string Rarity(PoolCard card) => card.Rarity.ToLowerInvariant();

    private static bool FoilOnly(CareerWorld world, PoolCard card) =>
        world.Cards.Find(card.Name)?.Record.Printings.FirstOrDefault(p => p.Set.Equals(card.Set, StringComparison.OrdinalIgnoreCase) && p.CollectorNumber == card.Number)?.FoilOnly == true;

    /// <summary>A card's rarity in its printing (its highest when the printing isn't known).</summary>
    public static string RarityOf(CareerWorld world, CardKey card)
    {
        var printings = world.Cards.Find(card.Name)?.Record.Printings ?? (IReadOnlyList<CardData.Printing>)Array.Empty<CardData.Printing>();
        var printing = printings.FirstOrDefault(p => p.Set.Equals(card.Set, StringComparison.OrdinalIgnoreCase) && p.CollectorNumber == card.Number);
        return (printing?.Rarity ?? printings.Select(p => p.Rarity).FirstOrDefault() ?? "common").ToLowerInvariant();
    }

    private static (double Least, double Most) SpreadOf(MarketSpec market, string rarity) =>
        market.Spread.TryGetValue(rarity, out var spread) && spread.Length == 2 ? (spread[0], spread[1]) : (0.15, 0.3);

    /// <summary>The most the player may ask for a card on the forum on a day: above it, nobody buys.</summary>
    public static int SaleCap(CareerState state, CareerWorld world, CardKey card, DateOnly day)
    {
        var market = world.Content.Market;
        double most = SpreadOf(market, RarityOf(world, card)).Most;
        int cap = (int)Math.Round(CareerMarket.Price(state, world, card, day) * (1 + most));
        if (world.ArtOf(card) != CardArt.Usual) cap = Math.Max(cap, (int)Math.Round(CareerMarket.UsualPrice(state, world, card, day) * market.ForumSpecialMin * (1 + most)));
        return Math.Max(1, cap);
    }

    private static string Key(DateOnly day, int index) => $"{day.DayNumber}:{index}";

    public static bool SingleBought(CareerState state, DateOnly day, int index) => state.SinglesBought.Contains(Key(day, index));

    public static bool ListingBought(CareerState state, DateOnly day, int index) => state.ListingsBought.Contains(Key(day, index));

    /// <summary>What a listing costs the player now: the seller's counter-offer when they made one, else their price.</summary>
    public static int ListingPrice(CareerState state, CareerListing listing, DateOnly day) =>
        state.ListingCounters.TryGetValue(Key(day, listing.Index), out var counter) ? counter : listing.Price;

    /// <summary>Offers the player can still make on a listing today.</summary>
    public static int OffersLeft(CareerState state, CareerWorld world, DateOnly day, int index) =>
        Math.Max(0, world.Content.Market.OfferAttempts - state.ListingOffers.GetValueOrDefault(Key(day, index)));

    // ---------------------------------------------------------------- buying

    private static CareerResult Buy(CareerState state, BuySingle buy, CareerWorld world)
    {
        var singles = StoreSingles(state, world, state.Day);
        if (buy.Index < 0 || buy.Index >= singles.Count) throw new CareerRuleException("That card isn't in the case today.");
        if (SingleBought(state, state.Day, buy.Index)) throw new CareerRuleException("Someone already bought that card.");
        var single = singles[buy.Index];
        Pay(state, single.Price);
        state.AddCard(single.Card, 1);
        state.SinglesBought.Add(Key(state.Day, buy.Index));
        state.Note(buy.Utc, $"Bought {single.Card.Name} at the store for {Money(single.Price)}.");
        return new CareerResult(new[] { ToPool(world, single.Card) }, -single.Price);
    }

    private static CareerListing ListingOf(CareerState state, CareerWorld world, int index)
    {
        var listings = Listings(state, world, state.Day);
        if (index < 0 || index >= listings.Count) throw new CareerRuleException("That post isn't on the forum today.");
        if (ListingBought(state, state.Day, index)) throw new CareerRuleException("That card is sold.");
        return listings[index];
    }

    private static CareerResult Buy(CareerState state, BuyListing buy, CareerWorld world)
    {
        var listing = ListingOf(state, world, buy.Index);
        int price = ListingPrice(state, listing, state.Day);
        return Bought(state, world, listing, price, buy.Utc);
    }

    private static CareerResult Bought(CareerState state, CareerWorld world, CareerListing listing, int price, DateTime utc)
    {
        Pay(state, price);
        state.AddCard(listing.Card, 1);
        state.ListingsBought.Add(Key(state.Day, listing.Index));
        state.Note(utc, $"Bought {listing.Card.Name} from {listing.Seller} for {Money(price)}.");
        return new CareerResult(new[] { ToPool(world, listing.Card) }, -price);
    }

    /// <summary>
    /// An offer on a listing: at or above the seller's lowest price they take it; a little below it (within a fifth) they
    /// counter halfway between it and their price, once; lower, they turn it down. Each listing takes a few offers a day.
    /// </summary>
    private static CareerResult Offer(CareerState state, OfferOnListing offer, CareerWorld world)
    {
        var listing = ListingOf(state, world, offer.Index);
        if (offer.Amount <= 0) throw new CareerRuleException("Offer some money.");
        var key = Key(state.Day, offer.Index);
        int made = state.ListingOffers.GetValueOrDefault(key);
        if (made >= world.Content.Market.OfferAttempts) throw new CareerRuleException($"{listing.Seller} isn't taking more offers today.");
        if (offer.Amount >= listing.Floor)
        {
            var result = Bought(state, world, listing, Math.Min(offer.Amount, ListingPrice(state, listing, state.Day)), offer.Utc);
            state.ListingOffers[key] = made + 1;
            return result;
        }
        state.ListingOffers[key] = made + 1;
        if (!state.ListingCounters.ContainsKey(key) && offer.Amount >= listing.Floor * 0.8)
            state.ListingCounters[key] = (int)Math.Round((listing.Floor + listing.Price) / 2.0);
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static PoolCard ToPool(CareerWorld world, CardKey card) =>
        new(card.Name, card.Set.ToLowerInvariant(), card.Number, RarityOf(world, card), card.Foil, card.Surge);

    // ---------------------------------------------------------------- the player's posts

    private static CareerResult Post(CareerState state, PostSale post, CareerWorld world)
    {
        if (!CareerMarket.Trading(world, state.Day)) throw new CareerRuleException("The forum doesn't take sale posts yet.");
        var card = CardKey.Parse(post.Card);
        if (state.Owned(card) < 1) throw new CareerRuleException($"You have no {card.Name}.");
        if (world.IsBasicLand(card.Name)) throw new CareerRuleException("Nobody buys basic lands.");
        if (post.Ask < 1) throw new CareerRuleException("Ask for some money.");
        int cap = SaleCap(state, world, card, state.Day);
        if (post.Ask > cap) throw new CareerRuleException($"Nobody would pay that: at most {Money(cap)}.");
        if (state.Posts.Count(p => p.Status is SaleStatus.Open or SaleStatus.Offered) >= world.Content.Market.PostsMax)
            throw new CareerRuleException($"You can have {world.Content.Market.PostsMax} posts up at once.");
        state.AddCard(card, -1);
        state.Posts.Add(new SalePost { Id = state.NextPostId++, Card = card.ToString(), Ask = post.Ask, Day = state.Day, Checked = state.Day, CheckedMidday = post.Utc.Hour >= 12 });
        state.Note(post.Utc, $"Posted {card.Name} for sale at {Money(post.Ask)}.");
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static SalePost PostOf(CareerState state, int id) =>
        state.Posts.FirstOrDefault(p => p.Id == id) ?? throw new CareerRuleException("That post isn't yours.");

    private static CareerResult Answer(CareerState state, AnswerSaleOffer answer, CareerWorld world)
    {
        var post = PostOf(state, answer.Post);
        if (post.Status != SaleStatus.Offered) throw new CareerRuleException("Nobody is waiting for an answer there.");
        var card = CardKey.Parse(post.Card);
        if (answer.Accept)
        {
            state.Cash += post.Offer;
            (post.Status, post.SoldFor) = (SaleStatus.Sold, post.Offer);
            state.Note(answer.Utc, $"Sold {card.Name} on the forum for {Money(post.Offer)}.");
            return new CareerResult(Array.Empty<PoolCard>(), post.Offer);
        }
        (post.Status, post.Offer) = (SaleStatus.Open, 0);
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    private static CareerResult TakeDown(CareerState state, TakeDownSale down)
    {
        var post = PostOf(state, down.Post);
        if (post.Status is not (SaleStatus.Open or SaleStatus.Offered)) throw new CareerRuleException("That post is already down.");
        var card = CardKey.Parse(post.Card);
        state.AddCard(card, 1);
        post.Status = SaleStatus.TakenDown;
        state.Note(down.Utc, $"Took down your post for {card.Name}.");
        return new CareerResult(Array.Empty<PoolCard>(), 0);
    }

    /// <summary>A post's looks at its luck: twice a day, at midnight and at midday (UTC), numbered in order.</summary>
    private static long SaleCheck(DateOnly day, bool midday) => day.DayNumber * 2L + (midday ? 1 : 0);

    private static long SaleCheck(DateTime utc) => SaleCheck(DayOf(utc), utc.Hour >= 12);

    /// <summary>Whether an open post has a look at its luck due by then.</summary>
    public static bool SaleCheckDue(SalePost post, DateTime utc) =>
        post.Status == SaleStatus.Open && SaleCheck(post.Checked, post.CheckedMidday) < SaleCheck(utc);

    /// <summary>
    /// The player's open posts catch up with each look at their luck since the last one, twice a day: each time a post may
    /// sell (more likely the closer its price is to the card's price, never above the cap), or get a lower offer (then it
    /// waits for the player's answer), with the day's chances shared between both looks; after
    /// <see cref="MarketSpec.PostDays"/> days it comes down (at midnight) and the card comes back.
    /// </summary>
    private static void ResolveSales(CareerState state, CareerWorld world, DateTime utc)
    {
        var market = world.Content.Market;
        // Yesterday's case and listings are gone: forget what was bought and offered there.
        string today = $"{state.Day.DayNumber}:";
        state.SinglesBought.RemoveAll(k => !k.StartsWith(today, StringComparison.Ordinal));
        state.ListingsBought.RemoveAll(k => !k.StartsWith(today, StringComparison.Ordinal));
        foreach (var key in state.ListingOffers.Keys.Concat(state.ListingCounters.Keys).Where(k => !k.StartsWith(today, StringComparison.Ordinal)).ToList())
        {
            state.ListingOffers.Remove(key);
            state.ListingCounters.Remove(key);
        }
        foreach (var post in state.Posts.Where(p => p.Status == SaleStatus.Open))
        {
            var card = CardKey.Parse(post.Card);
            for (long check = SaleCheck(post.Checked, post.CheckedMidday) + 1; check <= SaleCheck(utc); check++)
            {
                var day = DateOnly.FromDayNumber((int)(check / 2));
                bool midday = check % 2 == 1;
                (post.Checked, post.CheckedMidday) = (day, midday);
                if (!midday && day.DayNumber - post.Day.DayNumber > market.PostDays)
                {
                    state.AddCard(card, 1);
                    post.Status = SaleStatus.Expired;
                    state.Note(utc, $"Your post for {card.Name} expired: the card is back in your collection.");
                    break;
                }
                var random = midday ? CareerRandom.For(state.Seed, "sale", post.Id, day.DayNumber, 1) : CareerRandom.For(state.Seed, "sale", post.Id, day.DayNumber);
                double roll = random.NextDouble();
                // The day's chances of something happening, shared so that two looks give the same odds over the day.
                double daily = SaleChance(state, world, card, post.Ask, day), happens = Math.Min(1, daily + market.OfferChance);
                double share = happens <= 0 ? 0 : (1 - Math.Sqrt(1 - happens)) / happens;
                double chance = daily * share, offerChance = market.OfferChance * share;
                if (roll < chance)
                {
                    state.Cash += post.Ask;
                    (post.Status, post.SoldFor) = (SaleStatus.Sold, post.Ask);
                    state.Note(utc, $"Sold {card.Name} on the forum for {Money(post.Ask)}.");
                    break;
                }
                if (roll < chance + offerChance)
                {
                    post.Offer = Math.Max(1, (int)Math.Round(post.Ask * (market.OfferMin + random.NextDouble() * (market.OfferMax - market.OfferMin))));
                    post.Status = SaleStatus.Offered;
                    state.Note(utc, $"Someone offered {Money(post.Offer)} for your {card.Name}.");
                    break;
                }
            }
        }
    }

    /// <summary>A post's chance to sell on a day: the fair chance at the card's price or below, down to the cap's chance at the cap, none above it.</summary>
    public static double SaleChance(CareerState state, CareerWorld world, CardKey card, int ask, DateOnly day)
    {
        var market = world.Content.Market;
        int price = Math.Max(1, CareerMarket.Price(state, world, card, day));
        int cap = SaleCap(state, world, card, day);
        if (ask <= price) return market.SellChanceFair;
        if (ask > cap) return 0;
        double along = cap <= price ? 1 : (ask - price) / (double)(cap - price);
        return market.SellChanceFair + (market.SellChanceCap - market.SellChanceFair) * along;
    }
}
