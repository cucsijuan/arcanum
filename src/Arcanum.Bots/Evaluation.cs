// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Views;

namespace Arcanum.Bots;

/// <summary>Rough numeric values the bot uses to compare cards and decide what matters most.</summary>
public static class Evaluation
{
    /// <summary>How much a creature on the battlefield is worth: stats plus a bonus for strong keywords.</summary>
    public static double CreatureValue(CardView c)
    {
        double value = (c.Power ?? 0) * 1.5 + (c.Toughness ?? 0);
        foreach (var k in c.Keywords)
        {
            value += k switch
            {
                "Flying" => 1.5, "Deathtouch" => 1.5, "Double strike" => 2, "First strike" => 1, "Lifelink" => 1,
                "Trample" => 0.5, "Vigilance" => 0.5, "Menace" => 0.7, "Hexproof" => 1, "Indestructible" => 2,
                "Can't be blocked" => 1.5, "Can't block" => -1, "Defender" => -2, _ => 0.2,
            };
        }
        value += c.AbilityTexts.Count * 1.0;
        return value;
    }

    public static bool Has(CardView c, string keyword) => c.Keywords.Contains(keyword);

    public static int RemainingToughness(CardView c) => (c.Toughness ?? 0) - c.Damage;

    /// <summary>Whether an effect hurts what it is applied to (used to aim it at opponents).</summary>
    public static bool IsHarmful(Effect effect) => effect switch
    {
        DealDamage or Destroy or ExileIt or TapIt or CounterSpell or LoseLife or Mill => true,
        ReturnToHand => true,
        PumpUntilEndOfTurn p => p.Power.Estimate + p.Toughness.Estimate < 0,
        AddCounters a => a.Kind == CounterKind.MinusOneMinusOne,
        _ => false,
    };

    /// <summary>Whether the effects of an ability aimed at target <paramref name="index"/> hurt it.</summary>
    public static bool TargetIsHarmed(AbilityDefinition ability, int index)
    {
        bool harmful = false, helpful = false;
        foreach (var effect in ability.Effects)
        {
            if (!RefersToTarget(effect, index)) continue;
            if (IsHarmful(effect)) harmful = true; else helpful = true;
        }
        if (ability is StaticAbility st && StaticIsHarmful(st)) harmful = true;
        return harmful && !helpful;
    }

    /// <summary>Whether a static ability makes what it affects worse: smaller, without abilities, unable to attack, block or untap, or stolen.</summary>
    public static bool StaticIsHarmful(StaticAbility st) =>
        st.Power + st.Toughness < 0
        || (st.PowerBonus?.Estimate ?? 0) + (st.ToughnessBonus?.Estimate ?? 0) < 0
        || st.LosesAllAbilities
        || st.GivesControl
        || st.SetPower is <= 1 && st.SetToughness is <= 1
        || st.GrantedKeywords.Contains(Keyword.DoesntUntap)
        // "Can't attack or block" with no bonus is a drawback; with one ("+3/+0 and can't block") it's for your own creature.
        || (st.Power + st.Toughness <= 0 && st.GrantedKeywords.Any(k => k is Keyword.CantAttack or Keyword.CantBlock or Keyword.Defender));

    /// <summary>
    /// Whether an Aura is meant for an opponent's creature: a harmful static ability on what it enchants, or an ability that
    /// harms it when the Aura enters (tap it, destroy it, remove its counters…).
    /// </summary>
    public static bool AuraIsHarmful(CardDefinition aura) =>
        aura.Abilities.OfType<StaticAbility>().Any(s => s.Affects.Scope == AffectedScope.Enchanted && StaticIsHarmful(s))
        || aura.Abilities.OfType<TriggeredAbility>().SelectMany(t => t.Effects)
            .Any(e => (IsHarmful(e) || e is RemoveAllCounters or GainControl) && SubjectOf(e) is { Kind: SubjectKind.Attached });

    public static bool RefersToTarget(Effect effect, int index) => SubjectOf(effect) is { Kind: SubjectKind.Target } s && s.Index == index;

    /// <summary>What an effect is applied to, for the effects the computer reasons about.</summary>
    private static Subject? SubjectOf(Effect effect) => effect switch
    {
        DealDamage d => d.To, DrawCards d => d.Who, GainLife g => g.Who, LoseLife l => l.Who, Destroy d => d.What,
        ExileIt x => x.What, ReturnToHand r => r.What, TapIt t => t.What, UntapIt u => u.What, Mill m => m.Who,
        CounterSpell c => c.What, PumpUntilEndOfTurn p => p.What, AddCounters a => a.What, AttachSelf a => a.To,
        RemoveAllCounters r => r.What, GainControl g => g.What, _ => null,
    };

    /// <summary>Damage an ability deals to target <paramref name="index"/>, if any.</summary>
    public static int DamageTo(AbilityDefinition ability, int index) =>
        ability.Effects.OfType<DealDamage>().Where(d => d.To.Kind == SubjectKind.Target && d.To.Index == index).Sum(d => d.Amount.Estimate);

    public static int ManaValue(string? cost)
    {
        if (string.IsNullOrEmpty(cost)) return 0;
        int total = 0;
        int i = 0;
        while ((i = cost.IndexOf('{', i)) >= 0)
        {
            int close = cost.IndexOf('}', i);
            var symbol = cost.Substring(i + 1, close - i - 1);
            total += int.TryParse(symbol, out int n) ? n : 1;
            i = close + 1;
        }
        return total;
    }
}
