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
        DealDamageDivided or DealDamageDividedEvenly or DestroyOneAtRandom => true,
        Discard => true,
        ReturnToHand => true,
        PumpUntilEndOfTurn p => p.Power.Estimate + p.Toughness.Estimate < 0,
        AddCounters a => a.Kind == CounterKind.MinusOneMinusOne,
        _ => false,
    };

    /// <summary>Whether the effects of an ability aimed at target <paramref name="index"/> hurt it.</summary>
    public static bool TargetIsHarmed(AbilityDefinition ability, int index)
    {
        bool harmful = false, helpful = false;
        foreach (var effect in Unwrapped(ability.Effects))
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

    /// <summary>The effects with those inside "you may …" and "if …" ones (they are applied to the same targets).</summary>
    public static IEnumerable<Effect> Unwrapped(IEnumerable<Effect> effects)
    {
        foreach (var effect in effects)
        {
            var inner = effect switch
            {
                MayDo m => m.Effects, MayPay m => m.Effects, MayPayX m => m.Effects,
                IfThen i => i.Else is { } e ? i.Then.Concat(e) : i.Then,
                _ => null,
            };
            if (inner is null) yield return effect;
            else foreach (var e in Unwrapped(inner)) yield return e;
        }
    }

    /// <summary>
    /// How much a permanent is worth to the player who controls it: a creature by <see cref="CreatureValue"/>, anything
    /// else by its mana value and abilities (more when it's attached to a creature, like an Equipment or Aura).
    /// </summary>
    public static double PermanentValue(CardView c) =>
        c.Power is not null ? CreatureValue(c) : ManaValue(c.ManaCost) + c.AbilityTexts.Count + (c.AttachedTo is not null ? 2 : 0) - (c.IsToken ? 1 : 0);

    public static bool RefersToTarget(Effect effect, int index) =>
        effect is DealDamageDivided or DealDamageDividedEvenly // among all the targets
        || SubjectOf(effect) is { Kind: SubjectKind.Target } s && s.Index == index
        || SubjectOf(effect) is { Kind: SubjectKind.EachTarget } each && index >= each.Index;

    /// <summary>What an effect is applied to, for the effects the computer reasons about.</summary>
    private static Subject? SubjectOf(Effect effect) => effect switch
    {
        DealDamage d => d.To, DrawCards d => d.Who, GainLife g => g.Who, LoseLife l => l.Who, Destroy d => d.What,
        ExileIt x => x.What, ReturnToHand r => r.What, TapIt t => t.What, UntapIt u => u.What, Mill m => m.Who,
        CounterSpell c => c.What, PumpUntilEndOfTurn p => p.What, AddCounters a => a.What, AttachSelf a => a.To,
        RemoveAllCounters r => r.What, GainControl g => g.What, DestroyOneAtRandom d => d.Among, Discard d => d.Who, _ => null,
    };

    /// <summary>Damage an ability deals to target <paramref name="index"/>, if any (<paramref name="x"/>: the X it would have, when known).</summary>
    public static int DamageTo(AbilityDefinition ability, int index, int? x = null) =>
        Unwrapped(ability.Effects).OfType<DealDamage>().Where(d => d.To.Kind == SubjectKind.Target && d.To.Index == index)
            .Sum(d => d.Amount.Kind == QuantityKind.X && x is { } value ? value * d.Amount.Multiplier : d.Amount.Estimate);

    /// <summary>How much toughness an ability takes from target <paramref name="index"/> until end of turn ("gets -2/-2"), if any.</summary>
    public static int ShrinkOf(AbilityDefinition ability, int index, int? x = null) =>
        -Unwrapped(ability.Effects).OfType<PumpUntilEndOfTurn>().Where(p => RefersToTarget(p, index))
            .Sum(p => p.Toughness.Kind == QuantityKind.X && x is { } value ? value * p.Toughness.Multiplier : p.Toughness.Estimate);

    /// <summary>
    /// Whether all an ability does to target <paramref name="index"/> is damage and lowering its stats until end of turn: it only
    /// matters to a creature it kills (or in a combat it changes).
    /// </summary>
    public static bool OnlyLethalHarm(AbilityDefinition ability, int index) =>
        Unwrapped(ability.Effects).Where(e => RefersToTarget(e, index)).ToList() is { Count: > 0 } onTarget
        && onTarget.All(e => e is DealDamage || e is PumpUntilEndOfTurn p && IsHarmful(p))
        && onTarget.Any(e => e is PumpUntilEndOfTurn);

    /// <summary>Whether a creature dies from <paramref name="damage"/> more damage after losing <paramref name="shrink"/> toughness.</summary>
    public static bool DiesFrom(CardView c, int damage, int shrink, bool deathtouch = false)
    {
        int toughness = (c.Toughness ?? 0) - shrink;
        if (toughness <= 0) return true;
        if (Has(c, "Indestructible")) return false;
        return toughness <= c.Damage + damage || deathtouch && damage > 0;
    }

    /// <summary>Whether an ability deals damage to target <paramref name="index"/>.</summary>
    public static bool DamagesTarget(AbilityDefinition ability, int index) =>
        Unwrapped(ability.Effects).OfType<DealDamage>().Any(d => d.To.Kind == SubjectKind.Target && d.To.Index == index);

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
