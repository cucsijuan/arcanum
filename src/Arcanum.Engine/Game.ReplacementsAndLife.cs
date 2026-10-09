// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// Replacement and prevention effects on life gain and damage ("you gain twice that much life instead", "that player loses
/// that much life instead", "prevent 1 of that damage", "that much damage plus 1"), a creature exiled instead of dying, tokens
/// exiled instead of entering, and the requirement to attack a planeswalker during a player's next turn.
/// </summary>
public sealed partial class Game
{
    // ------------------------------------------------------------------ life gain (rules 119.10, 614, 616.1)

    /// <summary>One replacement effect that could modify a life gain event.</summary>
    private sealed record LifeGainModifier(string Key, string Id, string Label, Func<int, int> Apply, bool BecomesLoss = false);

    /// <summary>
    /// A player gains life, after the replacement effects that modify it: each applies once (rule 614.5), in the order the
    /// player gaining life chooses (rule 616.1), and once one has turned the event into life loss ("that player loses that much
    /// life instead") those about gaining life no longer apply. Nothing happens if the player can't gain life (rule 119.7).
    /// </summary>
    private async Task GainLifeAsync(PlayerId player, int amount)
    {
        if (amount <= 0 || State.GetPlayer(player).HasLost) return;
        if (State.Battlefield.Select(State.GetCard).Any(c => c.Definition.PlayersCantGainLife || (c.Definition.OpponentsCantGainLife && c.Controller != player))) return;
        var applied = new HashSet<string>();
        bool loss = false;
        while (!loss)
        {
            var options = LifeGainModifiers(player).Where(m => !applied.Contains(m.Id)).ToList();
            if (options.Count == 0) break;
            var kinds = options.Select(m => m.Key).Distinct().ToList();
            var next = options[0];
            if (kinds.Count > 1)
            {
                int pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player), new OptionRequest(
                    $"You would gain {amount} life: which effect applies next?", null,
                    kinds.Select(k => options.First(m => m.Key == k).Label).ToList(), OptionKind.Other));
                Require(pick >= 0 && pick < kinds.Count, "Choose one of the effects.");
                next = options.First(m => m.Key == kinds[pick]);
            }
            applied.Add(next.Id);
            amount = next.Apply(amount);
            loss = next.BecomesLoss;
        }
        if (amount <= 0) return;
        ChangeLife(player, loss ? -amount : amount);
    }

    /// <summary>The replacement effects that would modify life gain by this player now (one entry per instance).</summary>
    private List<LifeGainModifier> LifeGainModifiers(PlayerId player)
    {
        var list = new List<LifeGainModifier>();
        bool Has(Card c, Replacements r) => !c.LosesAbilities && (c.Definition.Replaces & r) != 0;
        foreach (var c in State.PermanentsControlledBy(player))
        {
            if (Has(c, Replacements.ExtraLifeGain)) list.Add(new("plus1", $"plus1:{c.Id.Value}", $"Gain that much plus 1 ({c.Name})", n => n + 1));
            if (Has(c, Replacements.DoubleLifeGain)) list.Add(new("x2", $"x2:{c.Id.Value}", $"Gain twice that much ({c.Name})", n => n * 2));
            // "If you would gain life while you have 5 or less life."
            if (Has(c, Replacements.DoubleLifeGainAtFiveOrLess) && State.GetPlayer(player).Life <= 5)
                list.Add(new("x2", $"x2five:{c.Id.Value}", $"Gain twice that much ({c.Name})", n => n * 2));
        }
        foreach (var opponent in State.OpponentsOf(player))
            foreach (var c in State.PermanentsControlledBy(opponent).Where(c => Has(c, Replacements.OpponentsLifeGainBecomesLoss)))
                list.Add(new("loss", $"loss:{c.Id.Value}", $"Lose that much life instead ({c.Name})", n => n, BecomesLoss: true));
        return list;
    }

    // ------------------------------------------------------------------ damage (rules 614.1a, 615, 616.1)

    /// <summary>A damage source judged as it last existed on the battlefield when it has just left it (rule 609.7, 113.7a).</summary>
    private bool SourceLeftBattlefield(Card source) =>
        source.Zone is not (Zone.Battlefield or Zone.Stack) && source.ZoneChangedTurn == State.TurnNumber && source.LastKnownInfo is not null;

    /// <summary>"If a [matching] source would deal damage to you, prevent N of that damage" of the permanents the damaged player controls.</summary>
    private IEnumerable<DamageModifier> DamageToYouReductionsFor(DamagePart part, bool preventable, Action<int> notePrevented)
    {
        if (!preventable || part.ToPlayer is not { } player) yield break;
        var source = part.Source;
        foreach (var c in State.PermanentsControlledBy(player).Where(c => !c.LosesAbilities && c.Definition.DamageToYouReductions is not null).ToList())
            for (int i = 0; i < c.Definition.DamageToYouReductions!.Count; i++)
            {
                var r = c.Definition.DamageToYouReductions[i];
                if (!Matches(r.Sources, source, SourceController(source), c, c.Controller, lastKnown: SourceLeftBattlefield(source))) continue;
                int n = r.Amount;
                yield return new DamageModifier($"prevent{n}", $"preventYou:{c.Id.Value}:{i}", $"Prevent {n} of it ({c.Name})", d =>
                {
                    notePrevented(Math.Min(n, d));
                    return Math.Max(0, d - n);
                }) { PlainPrevention = true };
            }
    }

    /// <summary>"If a [matching] source you control would deal damage to a permanent or player, it deals that much damage plus N instead."</summary>
    private IEnumerable<DamageModifier> DamageBonusesFor(DamagePart part)
    {
        var source = part.Source;
        var controller = SourceController(source);
        foreach (var c in State.PermanentsControlledBy(controller).Where(c => !c.LosesAbilities && c.Definition.DamageBonuses is not null).ToList())
            for (int i = 0; i < c.Definition.DamageBonuses!.Count; i++)
            {
                var b = c.Definition.DamageBonuses[i];
                if (!Matches(b.Sources, source, controller, c, c.Controller, lastKnown: SourceLeftBattlefield(source))) continue;
                int n = b.Amount;
                yield return new DamageModifier($"plus{n}", $"bonus:{c.Id.Value}:{i}", $"That much plus {n} ({c.Name})", d => d + n);
            }
    }

    // ------------------------------------------------------------------ entering (rule 614.1)

    /// <summary>
    /// A token about to be created that an "if a creature would enter and it wasn't cast, exile it instead" effect exiles: it
    /// never enters the battlefield (and, a token outside the battlefield, ceases to exist: rule 111.7).
    /// </summary>
    private bool TokenExiledInsteadOfEntering(Card token) =>
        State.ExileUncastEntering.Any(r => r.Turn == State.TurnNumber
            && Matches(r.Filter with { Controller = ControllerFilter.Any }, token, token.Controller, null, r.Controller));

    // ------------------------------------------------------------------ combat

    /// <summary>"If [this] and at least N other creatures attacked this combat" (declared as attackers, rule 508.1).</summary>
    private bool SourceAndOthersAttacked(Card? source, int others) =>
        source is not null && State.Combat is { } combat && combat.Declared.Contains((source.Id, source.Version))
        && combat.Declared.Count(d => d.Card != source.Id) >= others;

    /// <summary>"[Creature] attacks [this planeswalker] during its controller's next turn if able."</summary>
    private void AddAttackSourceRequirements(AttacksSourceNextTurn effect, EffectContext ctx)
    {
        var planeswalker = ctx.Source;
        if (planeswalker.Zone != Zone.Battlefield || (ctx.SourceVersion is { } v && v != planeswalker.Version)) return;
        foreach (var card in CardsFor(effect.What, ctx).Where(c => c.Zone == Zone.Battlefield && c.IsCreature).ToList())
            State.AttackPlaneswalkerRequirements.Add(new AttackPlaneswalkerRequirement(card.Id, card.Version, planeswalker.Id, planeswalker.Version, card.Controller));
    }

    /// <summary>As a turn begins: requirements for "its controller's next turn" apply during this one if it's that player's; those whose turn is over end.</summary>
    private void BeginTurnForAttackRequirements(PlayerId active)
    {
        State.AttackPlaneswalkerRequirements.RemoveAll(r => r.ActiveTurn is { } turn && turn < State.TurnNumber);
        foreach (var r in State.AttackPlaneswalkerRequirements.Where(r => r.Player == active && r.ActiveTurn is null)) r.ActiveTurn = State.TurnNumber;
    }

    /// <summary>The requirements to attack a planeswalker that apply to this creature now (the same objects, that planeswalker still attackable).</summary>
    private IEnumerable<AttackRequirement> PlaneswalkerAttackRequirements(Card creature, IReadOnlyList<PlayerId> defenders) =>
        State.AttackPlaneswalkerRequirements
            .Where(r => r.ActiveTurn == State.TurnNumber && r.Card == creature.Id && r.Version == creature.Version
                        && State.GetCard(r.Planeswalker) is { Zone: Zone.Battlefield } pw && pw.Version == r.PlaneswalkerVersion && defenders.Contains(pw.Controller))
            .Select(r => new AttackRequirement(creature.Id, AttackRequirementKind.AttacksPlaneswalker, State.GetCard(r.Planeswalker).Controller) { Planeswalker = r.Planeswalker })
            .Distinct();
}
