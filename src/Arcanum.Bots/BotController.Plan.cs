// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using static Arcanum.Bots.Evaluation;
using Arcanum.Engine.Core;

namespace Arcanum.Bots;

public sealed partial class BotController
{
    /// <summary>The player this bot plays.</summary>
    public PlayerId Player => _me;

    /// <summary>Play by the deck's plan (its game style and synergies) when the deck list is known; off: the plain heuristics.</summary>
    public bool UsePlan { get; set; } = true;

    private DeckPlan? _plan;

    /// <summary>The parts of the plan in use (all by default; fewer to measure each one).</summary>
    public PlanParts Parts { get; set; } = PlanParts.All;

    private bool Uses(PlanParts part) => Plan is not null && (Parts & part) != 0;

    /// <summary>The bot's own deck list (and commanders): what the deck is built to do. A player knows their own deck.</summary>
    public void KnowDeck(IReadOnlyList<CardDefinition> deck, IReadOnlyList<CardDefinition>? commanders = null) =>
        _plan = DeckPlan.Build(deck, commanders ?? Array.Empty<CardDefinition>());

    /// <summary>The plan in use, or null when playing without one.</summary>
    private DeckPlan? Plan => UsePlan ? _plan : null;

    /// <summary>
    /// How much a permanent on the battlefield adds through synergy with its controller's other permanents: a payoff with
    /// what feeds it, a piece that feeds the payoffs there. The same for anyone's board: a lord is worth more with its army.
    /// </summary>
    private double OnBoardSynergy(GameView view, CardView c)
    {
        if (!Uses(PlanParts.Targets) || Rules(view, c.Id) is not { } rules) return 0;
        return Synergy_(view, rules, c.Controller, c.Id);
    }

    /// <summary>The synergy a card would have with the permanents <paramref name="controller"/> has (other than <paramref name="self"/>).</summary>
    private double Synergy_(GameView view, CardDefinition card, PlayerId controller, CardId? self)
    {
        var board = BoardRules(view, controller).Where(b => b.Id != self).ToList();
        double value = 0;
        foreach (var theme in Synergy.Payoffs(card))
            value += Math.Min(board.Sum(b => Synergy.Enables(b.Rules, theme)), 5) * 0.6 * SynergyWeight;
        foreach (var (_, rules) in board)
            foreach (var theme in Synergy.Payoffs(rules))
                value += Math.Min(Synergy.Enables(card, theme), 3) * 0.8 * SynergyWeight;
        return value;
    }

    /// <summary>Scale of the synergy values (for measuring).</summary>
    public static double SynergyWeight = double.TryParse(Environment.GetEnvironmentVariable("ARCANUM_SYNERGY_WEIGHT"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1;

    private GameView? _boardView;
    private readonly Dictionary<PlayerId, List<(CardId Id, CardDefinition Rules)>> _boards = new();

    /// <summary>The rules of a player's permanents, read once per view.</summary>
    private List<(CardId Id, CardDefinition Rules)> BoardRules(GameView view, PlayerId controller)
    {
        if (!ReferenceEquals(view, _boardView)) { _boardView = view; _boards.Clear(); }
        if (!_boards.TryGetValue(controller, out var list))
        {
            list = view.Battlefield.Where(c => c.Controller == controller && !c.IsHidden)
                .Select(c => (c.Id, Rules: Rules(view, c.Id))).Where(x => x.Rules is not null).Select(x => (x.Id, x.Rules!)).ToList();
            _boards[controller] = list;
        }
        return list;
    }

    /// <summary>How well a card fits the plan: its deck themes and its synergy with our board now.</summary>
    private double Fit(GameView view, CardView c)
    {
        if (!Uses(PlanParts.Cards) || _definitions(c.Id) is not { } rules) return 0;
        return Plan!.DeckFit(rules) * 0.5 + Synergy_(view, rules, _me, c.Id);
    }

    /// <summary>Extra worth of a card we may put in our hand or onto the battlefield: a land when we're short, else its fit.</summary>
    private double Wanted(GameView view, CardView c, int lands)
    {
        if (!Uses(PlanParts.Cards)) return 0;
        bool land = (c.Types & CardType.Land) != 0;
        if (land) return lands < Math.Min(5, 2 + view.TurnNumber / 2) ? 6 : 0;
        int mv = ManaValue(c.ManaCost);
        return Fit(view, c) + (mv > lands + 2 ? -2 : 0);
    }

    /// <summary>What the plan adds to casting this spell now: its synergy with our board, its fit, creatures for an aggro deck.</summary>
    private double PlanBonus(GameView view, CardDefinition rules, bool mainPhase)
    {
        if (Plan is not { } plan) return 0;
        double bonus = Uses(PlanParts.Casting) ? Synergy_(view, rules, _me, null) + plan.DeckFit(rules) * 0.2 : 0;
        if (Uses(PlanParts.Style) && mainPhase && rules.IsCreature()) bonus += Math.Max(0, plan.Aggression) * 1.5;
        return bonus;
    }

    /// <summary>
    /// How much more a creature of ours may be worth than the blocker it trades with for the attack to still be made: an
    /// aggro deck trades readily, more so when the defender is low on life.
    /// </summary>
    private double TradeMargin(GameView view, PlayerId defender)
    {
        if (!Uses(PlanParts.Style) || Plan!.Aggression <= 0) return 1;
        var plan = Plan;
        int life = view.Players[defender.Value].Life;
        return 1 + plan.Aggression * (life <= 10 ? 4 : 2.5);
    }

    /// <summary>
    /// The spell to cast first among the set of castable spells that is worth most within the mana we have (several
    /// cheap spells can beat one expensive one): the most valuable of that set, so the rest stays castable.
    /// </summary>
    private CastSpell? BestUseOfMana(GameView view, List<(CastSpell Cast, double Score, int Cost)> scored)
    {
        int mana = AvailableMana(view);
        var options = scored.GroupBy(s => s.Cast.Card).Select(g => g.MaxBy(s => s.Score)).Where(s => s.Cost <= mana).OrderByDescending(s => s.Score).Take(10).ToList();
        if (options.Count < 2) return null;
        double bestTotal = 0;
        List<(CastSpell Cast, double Score, int Cost)>? bestSet = null;
        // Every subset of the (few) candidates that fits the mana.
        for (int mask = 1; mask < 1 << options.Count; mask++)
        {
            int cost = 0;
            double total = 0;
            for (int i = 0; i < options.Count; i++)
                if ((mask & 1 << i) != 0) { cost += options[i].Cost; total += options[i].Score; }
            if (cost > mana || total <= bestTotal) continue;
            bestTotal = total;
            bestSet = Enumerable.Range(0, options.Count).Where(i => (mask & 1 << i) != 0).Select(i => options[i]).ToList();
        }
        return bestSet?.MaxBy(s => s.Score).Cast;
    }

    /// <summary>The colors of mana a land can make (its mana abilities and basic land types).</summary>
    private static IEnumerable<ManaType> LandColors(CardDefinition land) =>
        land.TapForMana.Concat(land.ExtraManaOptions.SelectMany(o => o.Types))
            .Concat(land.Subtypes.Select(t => t switch
            {
                "Plains" => ManaType.White, "Island" => ManaType.Blue, "Swamp" => ManaType.Black, "Mountain" => ManaType.Red, "Forest" => ManaType.Green,
                _ => (ManaType?)null,
            }).OfType<ManaType>())
            .Distinct();

    /// <summary>
    /// The land to play: the one that gives the hand's spells the colored mana they still lack (the spells we can cast
    /// soonest count most), and that enters untapped when we have something to cast with it this turn.
    /// </summary>
    private PlayLand? ChooseLand(GameView view, IReadOnlyList<PlayLand> lands)
    {
        var fronts = lands.Where(l => !l.Back).ToList();
        if (fronts.Count == 0) return lands.FirstOrDefault();
        if (fronts.Count == 1) return fronts[0];
        var sources = new Dictionary<ManaType, int>();
        int landCount = 0;
        foreach (var c in view.Battlefield.Where(c => c.Controller == _me && (c.Types & CardType.Land) != 0))
        {
            landCount++;
            if (Rules(view, c.Id) is { } rules)
                foreach (var color in LandColors(rules)) sources[color] = sources.GetValueOrDefault(color) + 1;
        }
        var spells = view.Self.Hand.Where(c => (c.Types & CardType.Land) == 0).Select(c => Rules(view, c.Id)).OfType<CardDefinition>().ToList();
        double Missing(Dictionary<ManaType, int> have) => spells.Sum(spell =>
        {
            int lack = spell.ManaCost.Pips.GroupBy(p => p).Sum(g => Math.Max(0, g.Count() - have.GetValueOrDefault(g.Key)));
            // A spell castable sooner matters more.
            return lack / (1.0 + Math.Max(0, spell.ManaCost.ManaValue - (landCount + 1)));
        });
        double before = Missing(sources);
        bool castNow = spells.Any(s => s.ManaCost.ManaValue <= landCount + 1 && s.ManaCost.ManaValue > 0);
        PlayLand? best = null;
        double bestScore = double.MinValue;
        foreach (var land in fronts)
        {
            if (Rules(view, land.Card) is not { } rules) continue;
            var with = new Dictionary<ManaType, int>(sources);
            var colors = LandColors(rules).ToList();
            foreach (var color in colors) with[color] = with.GetValueOrDefault(color) + 1;
            double score = (before - Missing(with)) * 2 + colors.Count * 0.1;
            if (rules.EntersTapped) score += castNow ? -1.5 : 0.3; // a tapped land is best played on a turn it costs nothing
            if (score > bestScore) { bestScore = score; best = land; }
        }
        return best ?? fronts[0];
    }

    /// <summary>
    /// Whether the opening hand's lands make the colors of at least one of its spells castable in the first turns (a hand
    /// of only Swamps with red spells is a mulligan). Hands without spells or with any-color lands pass.
    /// </summary>
    private bool HandHasColors(GameView view)
    {
        var hand = view.Self.Hand;
        var lands = hand.Where(c => (c.Types & CardType.Land) != 0).Select(c => Rules(view, c.Id)).OfType<CardDefinition>().ToList();
        var spells = hand.Where(c => (c.Types & CardType.Land) == 0).Select(c => Rules(view, c.Id)).OfType<CardDefinition>().ToList();
        if (spells.Count == 0) return true;
        var colors = lands.SelectMany(LandColors).ToHashSet();
        // Lands whose colors aren't known here (they choose a color, they fetch) could make anything.
        if (lands.Any(l => !LandColors(l).Any())) return true;
        return spells.Any(s => s.ManaCost.ManaValue <= Math.Max(3, lands.Count + 1)
                               && s.ManaCost.Pips.All(colors.Contains)
                               && s.ManaCost.Hybrid.All(h => colors.Contains(h.First) || colors.Contains(h.Second)));
    }

    /// <summary>
    /// What taking the target creature is worth: for good, the creature twice (theirs gone, ours gained); until end of turn,
    /// only in our first main phase when the attack with it is lethal (with their best blocks), and nothing otherwise.
    /// Null when the spell takes nothing.
    /// </summary>
    private double? StealValue(GameView view, AbilityDefinition spell, Target target)
    {
        if (!Uses(PlanParts.Steal) || target.Card is not { } id || view.FindCard(id) is not { } victim) return null;
        var steal = Unwrapped(spell.Effects).OfType<GainControl>().FirstOrDefault(g => g.NewController is null && RefersToTarget(g, 0));
        if (steal is null) return null;
        if (!steal.UntilEndOfTurn && !steal.UntilEndOfYourNextTurn) return 2 * ((victim.Power is null ? PermanentValue(victim) : CreatureValue(victim)) + 1);
        if (victim.Power is not { } power || power <= 0 || view.ActivePlayer != _me || view.Step != Step.PrecombatMain) return 0;
        var defender = victim.Controller;
        var attackers = view.Battlefield.Where(c => c.Controller == _me && (c.Types & CardType.Creature) != 0 && !c.Tapped && !c.SummoningSick
                                                    && (c.Power ?? 0) > 0 && !Has(c, "Defender")).Append(victim).ToList();
        var blockers = view.Battlefield.Where(c => c.Controller == defender && c.Id != id && (c.Types & CardType.Creature) != 0 && !c.Tapped && !Has(c, "Can't block")).ToList();
        int life = view.Players[defender.Value].Life;
        var predicted = AssignBlocks(attackers, blockers, life, (b, a) => CanBlock(b, a) && !Has(a, "Can't be blocked"), a => Has(a, "Menace") ? 2 : 1);
        return attackers.Sum(a => Through(a, predicted[a.Id])) >= life ? 100 : 0;
    }
}

/// <summary>The parts of a deck plan a bot uses.</summary>
[Flags]
public enum PlanParts
{
    None = 0,
    /// <summary>Synergy and deck fit when choosing what to cast.</summary>
    Casting = 1,
    /// <summary>Deck fit when choosing cards to search for, keep, discard or put on the bottom.</summary>
    Cards = 2,
    /// <summary>Synergy of the opponents' permanents when choosing what to remove.</summary>
    Targets = 4,
    /// <summary>The deck's game style: aggro decks trade in attacks and favour creatures.</summary>
    Style = 8,
    /// <summary>The spells cast in a main phase are the set that makes the best use of the mana, not the single best one first.</summary>
    Curve = 16,
    /// <summary>The land played is the one that best gives the colors the hand needs.</summary>
    Lands = 32,
    /// <summary>An opening hand is kept only if its lands give the colors of some of its spells.</summary>
    Mulligan = 64,
    /// <summary>Spells that take a creature: kept for the turn they win, or worth twice the creature for good.</summary>
    Steal = 128,
    All = Casting | Cards | Targets | Style | Curve | Lands | Mulligan | Steal,
}
