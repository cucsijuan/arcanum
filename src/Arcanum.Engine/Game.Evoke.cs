// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// Evoke (rule 702.74): cast for its evoke cost rather than its mana cost, the permanent is sacrificed by the trigger
/// "when this enters, if its evoke cost was paid" (added by the card factory). Its non-mana costs exile a card from the hand.
/// </summary>
public sealed partial class Game
{
    /// <summary>The triggered mana abilities of a land being tapped for mana (rule 605.1b): auras on it, effects on all basic lands.</summary>
    private async Task AddLandManaBonusAsync(Player player, Card land, IReadOnlyList<Mana.ManaType> produced)
    {
        var colors = new[] { Mana.ManaType.White, Mana.ManaType.Blue, Mana.ManaType.Black, Mana.ManaType.Red, Mana.ManaType.Green };
        foreach (var giver in State.Battlefield.Select(State.GetCard).Where(c => c.HasTextAbilities && c.Definition.LandManaBonus is not null).ToList())
        {
            var bonus = giver.Definition.LandManaBonus!;
            if (bonus.EnchantedLand ? giver.AttachedTo != land.Id : (land.Supertypes & Cards.Supertype.Basic) == 0) continue;
            IReadOnlyList<Mana.ManaType> choices = bonus.Kind switch
            {
                Cards.LandManaBonusKind.ChosenColor => giver.ChosenColor is { } c && Mana.ManaTypeExtensions.TryParse(c[0], out var chosen) ? new[] { chosen } : Array.Empty<Mana.ManaType>(),
                Cards.LandManaBonusKind.SameTypeFromBasic => produced.Distinct().ToList(),
                _ => colors,
            };
            if (choices.Count == 0) continue;
            int pick = choices.Count == 1 ? 0 : await ControllerOf(player.Id).ChooseOptionAsync(ViewFor(player.Id), new OptionRequest(
                $"{giver.Name}: add one more mana of which type?", giver.Id, choices.Select(k => k.ToString()).ToList(), OptionKind.Other));
            Require(pick >= 0 && pick < choices.Count, "Choose one of the types.");
            player.ManaPool.Add(choices[pick]);
            Emit(new ManaAdded(player.Id, choices[pick], giver.Id));
        }
    }

    private static string EvokeCostText(Card card, Mana.ManaCost cost) =>
        card.Definition.EvokeExtra is { ExileFromHand: not null } extra && card.Definition.Evoke is { } own && own.Equals(cost)
            ? (cost.ManaValue > 0 ? $"{cost}, " : "") + DescribeCost(extra).ToLowerInvariant()
            : cost.ToString();

    /// <summary>The evoke costs permanents the caster controls give this card as it is cast from their hand.</summary>
    private IEnumerable<Mana.ManaCost> GrantedEvokeCosts(Card card, PlayerId caster) =>
        State.PermanentsControlledBy(caster)
            .Where(p => p.HasTextAbilities && p.Definition is { SpellsFromHandGainEvoke: { } filter, GrantedEvokeCost: not null }
                        && Matches(filter with { Controller = ControllerFilter.Any }, card, caster, p, caster))
            .Select(p => p.Definition.GrantedEvokeCost!).Distinct();

    /// <summary>The trigger of a granted evoke (rule 702.74a), for a permanent whose own card has no evoke.</summary>
    private static readonly TriggeredAbility GrantedEvokeSacrifice = new()
    {
        Trigger = TriggerEvent.EntersBattlefield,
        Condition = new WasEvoked(),
        Effects = new Effect[] { new SacrificeIt(Subject.Self) },
        Text = "Evoke — When this creature enters, if its evoke cost was paid, its controller sacrifices it.",
    };

    /// <summary>The cards in a player's hand an "exile a [filter] card from your hand" cost can exile (not the spell being cast).</summary>
    private List<CardId> ExilableFromHand(PlayerId player, ExtraCost extra, CardId source) =>
        State.GetPlayer(player).Hand.Where(id => id != source && extra.ExileFromHand is { } filter
            && Matches(filter with { Controller = ControllerFilter.Any }, State.GetCard(id), player, State.GetCard(source), player)).ToList();
}
