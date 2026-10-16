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
        IReadOnlyList<Effect>? Maybe(IReadOnlyList<Effect>? effects) => effects is null ? null : List(effects);
        var inner = effect switch
        {
            IfThen i => i with { Then = List(i.Then), Else = i.Else is { } e ? List(e) : null },
            MayDo m => m with { Effects = List(m.Effects), Else = Maybe(m.Else) },
            MayPay m => m with { Effects = List(m.Effects), Else = Maybe(m.Else) },
            ChooseOneEffect c => c with { Choices = c.Choices.Select(x => x with { Effects = List(x.Effects) }).ToList() },
            GuessTopCard g => g with { Right = List(g.Right), Wrong = List(g.Wrong) },
            RevealTop r => r with { Effects = List(r.Effects), Else = Maybe(r.Else) },
            Simultaneously si => si with { Effects = List(si.Effects) },
            AtPlayersNextEndStep a => a with { Ability = (TriggeredAbility)Map(a.Ability, map) },
            AtNextUpkeep a => a with { Ability = (TriggeredAbility)Map(a.Ability, map) },
            AtNextEndStepAbout a => a with { Effects = List(a.Effects) },
            MoveChosenCounters mc => mc with { Then = List(mc.Then) },
            Behold b => b with { Effects = List(b.Effects) },
            OpponentChooses o => o with { IfYes = List(o.IfYes), IfNo = List(o.IfNo) },
            PlayerMayPay pm => pm with { IfNot = List(pm.IfNot) },
            ForEachPlayer f => f with { Effects = List(f.Effects) },
            Repeat rp => rp with { Effects = List(rp.Effects) },
            FlipCoin fc => fc with { Won = List(fc.Won), Lost = List(fc.Lost) },
            CastExiledNow ce => ce with { Else = List(ce.Else) },
            CreateTokenCopy tc when tc.AtNextEndStep is { } later => tc with { AtNextEndStep = List(later) },
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
