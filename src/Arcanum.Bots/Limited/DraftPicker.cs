// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Bots.Limited;

/// <summary>
/// A card offered to a draft bot: its definition and rarity, and what the community's drafts say of it (on the 0–10
/// scale of <see cref="CardRating"/>, with how much to trust it, 0–1) when the set has statistics.
/// </summary>
public sealed record DraftOption(CardDefinition Card, string Rarity, double? Community = null, double Confidence = 0);

/// <summary>
/// A draft bot: takes the best card early, then leans into the two colors its best picks share, moving away from
/// cards it couldn't play. Its skill (0–1) is how much it knows of the community's statistics and how steady its
/// judgment is: a weaker bot leans on its own rating of the cards and takes a worse card more often, but always
/// stays in its colors.
/// </summary>
public static class DraftPicker
{
    private static readonly ManaType[] FiveColors = { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green };

    /// <summary>Index of the card to take from <paramref name="pack"/>, given what was already drafted.</summary>
    public static int Pick(IReadOnlyList<DraftOption> pack, IReadOnlyList<DraftOption> picks, Random random, double skill = 1)
    {
        skill = Math.Clamp(skill, 0, 1);
        var colors = FavoriteColors(picks, skill);
        double commitment = Math.Clamp((picks.Count - 4) / 12.0, 0, 1); // grows from the fifth pick on
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < pack.Count; i++)
        {
            var option = pack[i];
            double score = CardRating.Value(option, skill);
            if (colors.Count == 2 && !option.Card.Is(CardType.Land))
            {
                int offColors = CardRating.ColoredPips(option.Card).Count(c => !colors.Contains(c))
                                + option.Card.ManaCost.Hybrid.Count(h => !colors.Contains(h.First) && !colors.Contains(h.Second));
                score += offColors == 0 ? 1.0 * commitment : -2.5 * offColors * commitment;
            }
            score += random.NextDouble() * (0.3 + (1 - skill) * 1.7); // bots don't all draft alike; weaker ones misjudge more
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>The two colors with the most value among the picks so far (empty before there's anything to go on).</summary>
    public static IReadOnlySet<ManaType> FavoriteColors(IReadOnlyList<DraftOption> picks, double skill = 1)
    {
        var weight = FiveColors.ToDictionary(c => c, _ => 0.0);
        foreach (var pick in picks)
        {
            double value = Math.Max(0, CardRating.Value(pick, skill) - 2);
            var pips = CardRating.ColoredPips(pick.Card);
            foreach (var color in pips) weight[color] += value / pips.Count;
            foreach (var h in pick.Card.ManaCost.Hybrid) { weight[h.First] += value / 4; weight[h.Second] += value / 4; }
        }
        var top = weight.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key).ToHashSet();
        return top.Count == 2 ? top : new HashSet<ManaType>();
    }
}
