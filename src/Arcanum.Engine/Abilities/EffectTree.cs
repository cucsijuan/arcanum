// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>Walks the effects of an ability, including effects nested in other effects and in abilities they grant.</summary>
public static class EffectTree
{
    /// <summary>The ability with <paramref name="map"/> applied to every effect, innermost first.</summary>
    public static AbilityDefinition Map(AbilityDefinition ability, Func<Effect, Effect> map)
    {
        IReadOnlyList<Effect> List(IReadOnlyList<Effect> effects) => effects.Select(e => Map(e, map)).ToList();
        var mapped = ability with
        {
            Effects = List(ability.Effects),
            Modes = ability.Modes?.Select(m => m with { Effects = List(m.Effects) }).ToList(),
            WhenKicked = ability.WhenKicked is { } kicked ? Map(kicked, map) : null,
        };
        if (mapped is StaticAbility { GrantsAbilities: { } granted } st)
            mapped = st with { GrantsAbilities = granted.Select(a => Map(a, map)).ToList() };
        return mapped;
    }

    public static Effect Map(Effect effect, Func<Effect, Effect> map)
    {
        IReadOnlyList<Effect> List(IReadOnlyList<Effect> effects) => effects.Select(e => Map(e, map)).ToList();
        var inner = effect switch
        {
            IfThen i => i with { Then = List(i.Then), Else = i.Else is { } e ? List(e) : null },
            MayDo m => m with { Effects = List(m.Effects) },
            MayPay m => m with { Effects = List(m.Effects) },
            MayPayX m => m with { Effects = List(m.Effects) },
            ModeEffects m => m with { Effects = List(m.Effects) },
            Unless u => u with { Otherwise = List(u.Otherwise) },
            OpponentMaySacrifice o => o with { Effects = List(o.Effects) },
            ReflexiveTrigger r => r with { Ability = (TriggeredAbility)Map(r.Ability, map) },
            Become b when b.Abilities is { } abilities => b with { Abilities = abilities.Select(a => Map(a, map)).ToList() },
            CreateEmblem c => c with { Abilities = c.Abilities.Select(a => Map(a, map)).ToList() },
            _ => effect,
        };
        return map(inner);
    }

    /// <summary>Every effect of the ability, nested ones included.</summary>
    public static IEnumerable<Effect> All(AbilityDefinition ability)
    {
        var found = new List<Effect>();
        Map(ability, e => { found.Add(e); return e; });
        return found;
    }
}
