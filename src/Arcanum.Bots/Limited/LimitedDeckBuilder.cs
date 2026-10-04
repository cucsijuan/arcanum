// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Bots.Limited;

/// <summary>A limited deck chosen from a pool: indices of the pool cards in the deck and how many basic lands of each color.</summary>
public sealed record LimitedDeckChoice(IReadOnlyList<int> PoolCards, IReadOnlyDictionary<ManaType, int> BasicLands, IReadOnlySet<ManaType> Colors);

/// <summary>
/// Builds a 40-card deck from a limited pool: the best two colors, 23 spells with enough creatures and a sensible
/// curve, lands of those colors from the pool, and basic lands split by the colored symbols of the spells.
/// </summary>
public static class LimitedDeckBuilder
{
    public const int DeckSize = 40;
    public const int Lands = 17;

    private static readonly ManaType[] FiveColors = { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green };

    public static LimitedDeckChoice Build(IReadOnlyList<DraftOption> pool)
    {
        LimitedDeckChoice? best = null;
        double bestScore = double.MinValue;
        for (int a = 0; a < FiveColors.Length; a++)
            for (int b = a + 1; b < FiveColors.Length; b++)
            {
                var colors = new HashSet<ManaType> { FiveColors[a], FiveColors[b] };
                var (choice, score) = BuildFor(pool, colors);
                if (score > bestScore) { bestScore = score; best = choice; }
            }
        return best!;
    }

    /// <summary>The deck for one color pair and how good it looks.</summary>
    public static (LimitedDeckChoice Choice, double Score) BuildFor(IReadOnlyList<DraftOption> pool, IReadOnlySet<ManaType> colors)
    {
        var spells = Enumerable.Range(0, pool.Count)
            .Where(i => !pool[i].Card.Is(CardType.Land) && CardRating.Castable(pool[i].Card, colors))
            .Select(i => (Index: i, Rating: CardRating.Rate(pool[i].Card, pool[i].Rarity)))
            .OrderByDescending(x => x.Rating)
            .ToList();

        // Creatures first (a limited deck wants about 15), then the best of everything else, keeping the curve low.
        var chosen = new List<(int Index, double Rating)>();
        foreach (var c in spells.Where(x => pool[x.Index].Card.IsCreature()).Take(15)) chosen.Add(c);
        foreach (var s in spells.Where(x => !chosen.Contains(x)))
        {
            if (chosen.Count >= DeckSize - Lands) break;
            int mv = pool[s.Index].Card.ManaCost.ManaValue;
            if (mv >= 6 && chosen.Count(x => pool[x.Index].Card.ManaCost.ManaValue >= 6) >= 3) continue;
            chosen.Add(s);
        }
        // Too few playables: fill with whatever is left (still castable).
        foreach (var s in spells.Where(x => !chosen.Contains(x)))
        {
            if (chosen.Count >= DeckSize - Lands) break;
            chosen.Add(s);
        }
        chosen = chosen.Take(DeckSize - Lands).ToList();

        // Lands from the pool that make this deck's colors (and fetch lands), up to 17 in all.
        var lands = Enumerable.Range(0, pool.Count)
            .Where(i => pool[i].Card.Is(CardType.Land) && (pool[i].Card.Supertypes & Supertype.Basic) == 0 && LandFits(pool[i].Card, colors))
            .Take(4)
            .ToList();

        int nonland = chosen.Count;
        int landCount = DeckSize - nonland;
        int basics = Math.Max(0, landCount - lands.Count);
        var pips = colors.ToDictionary(c => c, c => chosen.Sum(x => pool[x.Index].Card.ManaCost.Pips.Count(p => p == c)
                                                                + pool[x.Index].Card.ManaCost.Hybrid.Count(h => h.First == c || h.Second == c) * 0.5));
        double totalPips = Math.Max(1, pips.Values.Sum());
        var basicLands = new Dictionary<ManaType, int>();
        var ordered = colors.OrderByDescending(c => pips[c]).ToList();
        int assigned = 0;
        foreach (var color in ordered)
        {
            int n = color == ordered[^1] ? basics - assigned : (int)Math.Round(basics * pips[color] / totalPips);
            basicLands[color] = Math.Max(0, n);
            assigned += basicLands[color];
        }

        double score = chosen.Sum(x => x.Rating) - Math.Max(0, 14 - chosen.Count(x => pool[x.Index].Card.IsCreature())) * 1.5
                       - Math.Max(0, DeckSize - Lands - chosen.Count) * 3;
        return (new LimitedDeckChoice(chosen.Select(x => x.Index).Concat(lands).ToList(), basicLands, colors), score);
    }

    /// <summary>A nonbasic land worth playing in these colors: it makes only these colors (at least one), or fetches a basic land.</summary>
    private static bool LandFits(CardDefinition land, IReadOnlySet<ManaType> colors)
    {
        var made = land.TapForMana.Where(t => t != ManaType.Colorless).Distinct().ToList();
        if (made.Count > 0) return made.All(colors.Contains) && made.Count >= 2;
        return (land.Spell is null ? land.Abilities : land.Abilities.Prepend(land.Spell))
            .SelectMany(a => a.Effects).OfType<SearchLibrary>().Any(s => (s.Filter.Supertype & Supertype.Basic) != 0);
    }
}
