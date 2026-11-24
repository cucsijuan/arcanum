// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Bots.Simulation;

/// <summary>A card a player could have, and how likely it is next to the others (any positive scale).</summary>
public sealed record CardGuess(CardDefinition Card, double Weight);

/// <summary>
/// The cards a player could have in this game, from the cards seen from them so far (their commanders among them): the
/// set being drafted, the sets the seen cards come from, the commander's colors. Given by the host, which knows the card
/// database and the kind of game; it must not look at the player's actual deck.
/// </summary>
public delegate IReadOnlyList<CardGuess> CardCandidates(IReadOnlyList<CardDefinition> seen);

/// <summary>What a bot knows about an opponent's cards at the moment it imagines the hidden ones.</summary>
public sealed record OpponentKnowledge(
    IReadOnlyList<CardDefinition> Seen,
    IReadOnlyList<CardDefinition> Commanders,
    int DeckSize,
    int HiddenHand,
    int HiddenLibrary);

/// <summary>
/// Imagines an opponent's hidden cards (their hand and library) without looking at them: a deck that fits what has been
/// seen — the colors shown (or the commander's), cards that work with the seen ones, as many lands as a deck of that
/// size plays — drawn from the candidates the host gives (or, with none, from the seen cards themselves).
/// </summary>
public static class OpponentModel
{
    /// <summary>
    /// The candidates weighed for what has been seen (worked out once for each set of seen cards; dealing from it is
    /// then quick): spells likelier in the colors shown (or the commander's) and with the seen cards' themes, lands that
    /// make those colors.
    /// </summary>
    public sealed record Weighing(IReadOnlyList<(CardDefinition Card, double Weight)> Spells, IReadOnlyList<(CardDefinition Card, double Weight)> Lands,
        bool Singleton, int SeenLands);

    public static Weighing Weigh(OpponentKnowledge known, IReadOnlyList<CardGuess>? candidates)
    {
        bool commander = known.Commanders.Count > 0;
        var colors = commander
            ? known.Commanders.SelectMany(c => c.ColorIdentity).ToHashSet()
            : known.Seen.SelectMany(c => c.Is(CardType.Land) ? LandColors(c).Select(Letter) : c.ColorIdentity).ToHashSet();
        var pool = (candidates is { Count: > 0 } ? candidates : known.Seen.Select(c => new CardGuess(c, 1)).ToList())
            .Where(g => !commander || g.Card.ColorIdentity.All(colors.Contains)).ToList();

        // Spells: likelier in the colors shown (unseen colors fade as more of the deck is seen) and with the seen cards' themes.
        int seenSpells = known.Seen.Count(c => !c.Is(CardType.Land));
        double offColor = commander ? 0 : colors.Count == 0 ? 1 : Math.Pow(0.5, 1 + seenSpells / 3.0);
        var themes = known.Seen.SelectMany(Synergy.Payoffs).Distinct().ToList();
        var seenNames = known.Seen.Select(c => c.Name).ToHashSet();
        double SpellWeight(CardGuess g)
        {
            if (commander && seenNames.Contains(g.Card.Name) && (g.Card.Supertypes & Supertype.Basic) == 0) return 0; // singleton
            double w = g.Weight * (g.Card.ColorIdentity.All(colors.Contains) ? 1 : offColor);
            if (w <= 0) return 0;
            double fit = themes.Sum(t => Math.Min(Synergy.Enables(g.Card, t), 2));
            return w * (1 + 0.25 * fit);
        }
        double LandWeight(CardGuess g)
        {
            var made = LandColors(g.Card).Select(Letter).ToList();
            if (made.Count == 0) return 0;
            if (colors.Count == 0) return g.Weight;
            return made.Any(colors.Contains) && (!commander || made.All(colors.Contains)) ? g.Weight * (made.All(colors.Contains) ? 1 : 0.3) : 0;
        }
        static List<(CardDefinition, double)> Weighed(IEnumerable<CardGuess> guesses, Func<CardGuess, double> weight) =>
            guesses.Select(g => (g.Card, W: weight(g))).Where(x => x.W > 0).ToList();
        return new Weighing(Weighed(pool.Where(g => !g.Card.Is(CardType.Land)), SpellWeight), Weighed(pool.Where(g => g.Card.Is(CardType.Land)), LandWeight),
            commander, known.Seen.Count(c => c.Is(CardType.Land)));
    }

    public static (List<CardDefinition> Hand, List<CardDefinition> Library) Imagine(OpponentKnowledge known, IReadOnlyList<CardGuess>? candidates, Random random) =>
        ImagineFrom(known, Weigh(known, candidates), random);

    public static (List<CardDefinition> Hand, List<CardDefinition> Library) ImagineFrom(OpponentKnowledge known, Weighing weighing, Random random)
    {
        int slots = known.HiddenHand + known.HiddenLibrary;
        var cards = new List<CardDefinition>(slots);
        if (slots > 0)
        {
            // Lands: what a deck of that size plays, less those already seen, among the hidden cards.
            double landShare = weighing.Singleton ? 0.37 : known.DeckSize <= 45 ? 17 / 40.0 : 24 / 60.0;
            int hiddenLands = Math.Clamp((int)Math.Round(known.DeckSize * landShare) - weighing.SeenLands, 0, slots);
            cards.AddRange(Draw(weighing.Spells, slots - hiddenLands, random, weighing.Singleton));
            cards.AddRange(Draw(weighing.Lands, hiddenLands, random, weighing.Singleton));
            // Short of candidates (a narrow pool): what was seen fills the rest.
            while (cards.Count < slots && known.Seen.Count > 0) cards.Add(known.Seen[random.Next(known.Seen.Count)]);
            cards = cards.OrderBy(_ => random.Next()).ToList();
        }
        return (cards.Take(known.HiddenHand).ToList(), cards.Skip(known.HiddenHand).ToList());
    }

    /// <summary><paramref name="count"/> cards drawn by weight; in a singleton deck a nonbasic card once at most.</summary>
    private static List<CardDefinition> Draw(IReadOnlyList<(CardDefinition Card, double Weight)> pool, int count, Random random, bool singleton)
    {
        var drawn = new List<CardDefinition>(Math.Max(0, count));
        if (count <= 0 || pool.Count == 0) return drawn;
        var cumulative = new double[pool.Count];
        double total = 0;
        for (int i = 0; i < pool.Count; i++) cumulative[i] = total += pool[i].Weight;
        var taken = singleton ? new HashSet<int>() : null;
        for (int tries = 0; drawn.Count < count && tries < count * 20; tries++)
        {
            int k = Array.BinarySearch(cumulative, random.NextDouble() * total);
            if (k < 0) k = ~k;
            k = Math.Min(k, pool.Count - 1);
            if (taken is not null && (pool[k].Card.Supertypes & Supertype.Basic) == 0 && !taken.Add(k)) continue;
            drawn.Add(pool[k].Card);
        }
        return drawn;
    }

    /// <summary>The colors of mana a land can make (its mana abilities and basic land types).</summary>
    public static IEnumerable<ManaType> LandColors(CardDefinition land) =>
        land.TapForMana.Concat(land.ExtraManaOptions.SelectMany(o => o.Types))
            .Concat(land.Subtypes.Select(t => t switch
            {
                "Plains" => ManaType.White, "Island" => ManaType.Blue, "Swamp" => ManaType.Black, "Mountain" => ManaType.Red, "Forest" => ManaType.Green,
                _ => (ManaType?)null,
            }).OfType<ManaType>())
            .Distinct();

    private static string Letter(ManaType type) => type switch
    {
        ManaType.White => "W", ManaType.Blue => "U", ManaType.Black => "B", ManaType.Red => "R", ManaType.Green => "G", _ => "C",
    };
}
