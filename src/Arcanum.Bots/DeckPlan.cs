// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Bots;

/// <summary>What a deck is built to do, read from its list: its game style and the synergies between its cards.</summary>
public sealed class DeckPlan
{
    /// <summary>The deck's themes: how many of its cards reward each one, and how much the rest feeds it.</summary>
    public IReadOnlyDictionary<Theme, (int Payoffs, double Enablers)> Themes { get; }

    /// <summary>From −1 (control: answers, card draw, expensive threats) to +1 (aggro: cheap creatures, pressure).</summary>
    public double Aggression { get; }

    /// <summary>The average mana value of the nonland cards.</summary>
    public double AverageManaValue { get; }

    /// <summary>The lands in the deck.</summary>
    public int Lands { get; }

    /// <summary>The colors of mana the deck's spells ask for.</summary>
    public IReadOnlySet<string> Colors { get; }

    private DeckPlan(Dictionary<Theme, (int, double)> themes, double aggression, double averageManaValue, int lands, IReadOnlySet<string> colors)
    {
        Themes = themes;
        Aggression = aggression;
        AverageManaValue = averageManaValue;
        Lands = lands;
        Colors = colors;
    }

    public static DeckPlan Build(IReadOnlyList<CardDefinition> deck, IReadOnlyList<CardDefinition> commanders)
    {
        var all = deck.Concat(commanders).ToList();
        var spells = all.Where(c => !c.Is(CardType.Land)).ToList();
        // A theme counts when at least two cards reward it (one for a commander, which is always there).
        var payoffs = new Dictionary<Theme, int>();
        foreach (var card in all)
            foreach (var theme in Synergy.Payoffs(card))
                payoffs[theme] = payoffs.GetValueOrDefault(theme) + (commanders.Contains(card) ? 3 : 1);
        var themes = new Dictionary<Theme, (int, double)>();
        foreach (var (theme, count) in payoffs)
        {
            if (count < 2) continue;
            double enablers = all.Sum(c => Synergy.Enables(c, theme));
            // Too few cards feed it (a lone "whenever a Bird enters" in a deck of two Birds): not a theme of the deck.
            if (enablers < Math.Max(3, all.Count / 15.0)) continue;
            themes[theme] = (count, enablers);
        }

        double average = spells.Count == 0 ? 3 : spells.Average(c => c.ManaCost.ManaValue);
        double creatures = spells.Count == 0 ? 0 : spells.Count(c => c.IsCreature()) / (double)spells.Count;
        double cheap = spells.Count == 0 ? 0 : spells.Count(c => c.IsCreature() && c.ManaCost.ManaValue <= 2) / (double)spells.Count;
        double reactive = spells.Count == 0 ? 0 : spells.Count(IsReactive) / (double)spells.Count;
        double aggression = Math.Clamp((3.1 - average) * 0.8 + (creatures - 0.6) * 1.5 + (cheap - 0.25) * 2 - (reactive - 0.1) * 2, -1, 1);
        var colors = spells.SelectMany(c => c.ColorList).ToHashSet();
        return new DeckPlan(themes, aggression, average, all.Count(c => c.Is(CardType.Land)), colors);
    }

    /// <summary>Answers and card advantage: counterspells, instant-speed removal, card draw.</summary>
    private static bool IsReactive(CardDefinition card)
    {
        if (card.Spell is not { } spell) return false;
        var effects = EffectTree.All(spell).ToList();
        if (effects.Any(e => e is CounterSpell)) return true;
        if (effects.OfType<DrawCards>().Any(d => d.Who.Kind == SubjectKind.You && d.Count.Estimate >= 2)) return true;
        return card.Is(CardType.Instant) && spell.Targets.Count > 0 && effects.Any(Evaluation.IsHarmful);
    }

    /// <summary>How much a card fits the deck's themes, whatever is on the battlefield (for tutors, discards, keeping a hand).</summary>
    public double DeckFit(CardDefinition card)
    {
        double fit = 0;
        foreach (var (theme, (payoffs, enablers)) in Themes)
        {
            if (Synergy.Payoffs(card).Contains(theme)) fit += Math.Min(enablers, 12) * 0.15;
            double feeds = Synergy.Enables(card, theme);
            if (feeds > 0) fit += feeds * Math.Min(payoffs, 4) * 0.35;
        }
        return fit;
    }

    /// <summary>One line for logs: style and themes.</summary>
    public override string ToString() =>
        $"aggression {Aggression:+0.00;-0.00}, avg mv {AverageManaValue:0.0}; themes: " +
        string.Join(", ", Themes.OrderByDescending(t => t.Value.Payoffs).Select(t =>
            $"{t.Key.Kind}{(t.Key.Subtype is { } s ? " " + s : "")}{(t.Key.Types != 0 ? " " + t.Key.Types : "")} ({t.Value.Payoffs}/{t.Value.Enablers:0})"));
}
