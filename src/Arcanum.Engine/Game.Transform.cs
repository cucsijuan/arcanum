// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Nonmodal double-faced cards (rule 712) and transforming (rule 701.27).</summary>
public sealed partial class Game
{
    /// <summary>
    /// Turns a double-faced permanent over to its other face (rule 701.27a). It stays the same object (712.18): its counters,
    /// damage, attachments, tapped status and the effects applying to it all stay. A permanent that isn't a double-faced card
    /// or token doesn't transform (701.27c), nor one whose other face is an instant or sorcery (701.27d).
    /// </summary>
    /// <returns>Whether it transformed.</returns>
    private bool TransformPermanent(Card card)
    {
        if (card.Zone != Zone.Battlefield || card.PrintedDefinition.BackFace is not { } back) return false;
        var into = card.Transformed ? card.PrintedDefinition : back;
        if (into.Is(CardType.Instant) || into.Is(CardType.Sorcery)) return false;
        card.Transformed = !card.Transformed;
        card.TransformCount++;
        RecomputeContinuousEffects();
        Emit(new PermanentTransformed(card.Id, card.Transformed));
        return true;
    }

    /// <summary>
    /// "Whenever this transforms" / "transforms into [a permanent matching it]": the abilities it has right after it transformed,
    /// judged as it is then (rule 701.27e); and other permanents watching for permanents that transform.
    /// </summary>
    private void CollectTransformTriggers(PermanentTransformed e)
    {
        var turned = State.GetCard(e.Card);
        if (turned.Zone != Zone.Battlefield) return;
        var about = new TriggerInfo(turned.Id, turned.Version, turned.Controller);
        foreach (var ability in TriggerAbilitiesOf(turned).Where(a => a.Trigger == TriggerEvent.Transforms && (a.Filter is null || a.OnSelf)).ToList())
            if (ability.Filter is null || Matches(ability.Filter with { Controller = ControllerFilter.Any }, turned, turned.Controller, turned, turned.Controller))
                AddPending(turned.Id, ability, turned.Controller, about);
        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Id != turned.Id))
            foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.Transforms && a.Filter is not null && !a.OnSelf))
                if (Matches(ability.Filter!, turned, turned.Controller, observer, observer.Controller))
                    AddPending(observer.Id, ability, observer.Controller, about);
    }

    /// <summary>
    /// What a token made as a copy of <paramref name="original"/> copies (rule 707.2): a double-faced permanent or card makes a
    /// double-faced token with both faces, entering with the face up that the original has up (707.8a); a copy effect on the
    /// original is copied instead.
    /// </summary>
    private static (CardDefinition Definition, bool BackFaceUp) CopiableForToken(Card original) =>
        original.CopiedDefinition is null && original.IsDoubleFaced ? (original.PrintedDefinition, original.Transformed) : (original.Definition, false);
}
