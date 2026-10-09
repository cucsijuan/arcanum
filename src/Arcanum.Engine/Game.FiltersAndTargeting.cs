// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>The object version the trigger being put on the stack or resolving is about (with <see cref="_triggeredSubject"/>).</summary>
    private int _triggeredSubjectVersion;

    /// <summary>
    /// Filter parts about keywords lacked, power against toughness, owners, the triggering object, the resolving spell's X and
    /// what a spell targets. <paramref name="power"/>, <paramref name="toughness"/> and <paramref name="hasKeyword"/> are the
    /// object's as <see cref="Matches"/> judges it (as it last existed, when asked to).
    /// </summary>
    private bool MatchesOriginsFilters(ObjectFilter filter, Card obj, int power, int toughness, Func<Keyword, bool> hasKeyword, PlayerId sourceController)
    {
        if (filter.WithoutKeywords is { Count: > 0 } without && without.Any(hasKeyword)) return false;
        if (filter.PowerNotEqualToughness && power == toughness) return false;
        if (filter.NotOwnedByYou && obj.Owner == sourceController) return false;
        // "Each other creature": a different object from the one the trigger is about (the same card back as a new object counts, rule 400.7).
        if (filter.NotTriggered && _triggeredSubject is { } about && obj.Id == about && obj.Version == _triggeredSubjectVersion) return false;
        if (filter.MaxManaValueX && (_announcedX < 0 || ManaValueOf(obj) > _announcedX)) return false;
        if (filter.TargetsYou && !(obj.Zone == Zone.Stack && State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == obj.Id) is { } spell
                                   && spell.Targets.Any(t => t.Target.Player == sourceController))) return false;
        return true;
    }

    /// <summary>
    /// "You may change a target of target spell or ability to this creature" (rule 115.7): the controller picks one target of
    /// the spell or ability (whatever its number of targets) for which this permanent is a legal choice, or none. The change
    /// is made only if the permanent is still the object that put the ability on the stack and is on the battlefield, and
    /// only to a legal target: it must meet that target's requirement, not be chosen twice for the same "target" word, and
    /// keep the rules between the spell's targets.
    /// </summary>
    private async Task ChangeTargetToSourceAsync(ChangeTarget ct, EffectContext ctx)
    {
        var source = ctx.Source;
        if (!SourceOnBattlefield(ctx)) return;
        var target = ct.What.Kind == SubjectKind.Target ? ctx.TargetAt(ct.What.Index) : null;
        var item = target is { StackObject: { } so } ? State.Stack.FirstOrDefault(s => s.Id == so)
            : target is { Card: { } targetedSpell } ? State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == targetedSpell) : null;
        if (item is null || item.Targets.Count == 0) return;
        var ability = item switch
        {
            SpellOnStack sp => sp.Ability ?? CastingTargets(State.GetCard(sp.Card).Definition),
            AbilityOnStack ab => ab.Ability,
            _ => null,
        };
        if (ability is null || ability.Targets.Count == 0) return;

        var newTarget = Target.Of(source.Id);
        var saved = _triggeredPlayer;
        _triggeredPlayer = (item as AbilityOnStack)?.Trigger?.Player;
        var changeable = new List<int>();
        try
        {
            for (int i = 0; i < item.Targets.Count; i++)
            {
                var current = item.Targets[i].Target;
                if (current.IsNone || current == newTarget) continue;
                if (!LegalTargets(SpecAt(ability, i), item.Controller, item.SourceCard).Contains(newTarget)) continue;
                var after = item.Targets.Select(t => t.Target).ToList();
                after[i] = newTarget;
                // The same object only once for one "target" word (115.3), and the rules between the targets (601.2c).
                bool repeated = after.Where((t, j) => j != i && t == newTarget && SpecAt(ability, j) == SpecAt(ability, i)).Any();
                bool allowed = Enumerable.Range(0, after.Count).All(k => TargetAllowed(ability, k, after[k], after.Take(k).ToList()));
                if (!repeated && allowed) changeable.Add(i);
            }
        }
        finally { _triggeredPlayer = saved; }
        if (changeable.Count == 0) return;

        string Describe(Target t) => t.Card is { } c ? State.GetCard(c).Name : t.Player is { } p ? State.GetPlayer(p).Name : "a spell or ability";
        var labels = changeable.Select(i => $"Change the target {Describe(item.Targets[i].Target)} to {source.Name}").Append("Don't change a target").ToList();
        int pick = await ControllerOf(ctx.Controller).ChooseOptionAsync(ViewFor(ctx.Controller),
            new OptionRequest($"Change a target of {State.GetCard(item.SourceCard).Name} to {source.Name}?", source.Id, labels, OptionKind.Other));
        Require(pick >= 0 && pick < labels.Count, "Choose one of the options.");
        if (pick == changeable.Count) return;
        int index = State.Stack.IndexOf(item);
        var targets = item.Targets.ToList();
        targets[changeable[pick]] = new ChosenTarget(newTarget, source.Version);
        State.Stack[index] = item with { Targets = targets };
        if (State.Stack[index] is SpellOnStack retargeted) _spellsOnStack[(retargeted.Card, State.GetCard(retargeted.Card).Version)] = retargeted;
        QueueBecameTarget(State.Stack[index], new[] { source.Id });
    }

    /// <summary>
    /// "This creature can't be the target of nongreen spells or abilities from nongreen sources": the card's restriction on what
    /// may target it, matched against the spell or the ability's source (as it last existed if it has left the battlefield).
    /// It applies to every player's spells and abilities.
    /// </summary>
    private bool CantBeTargetedBy(Card card, Card source) =>
        card.Definition.CantBeTargetedBy is { } restriction && !card.LosesAbilities
        && Matches(restriction with { Controller = ControllerFilter.Any }, source, source.Controller, card, card.Controller,
            lastKnown: source.Zone is not (Zone.Battlefield or Zone.Stack));
}
