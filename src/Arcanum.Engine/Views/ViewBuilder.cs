// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Views;

public static class ViewBuilder
{
    /// <param name="revealAll">Show every hidden card (deck-test / hotseat / spectator-with-cheats).</param>
    /// <param name="commanderTaxPerCast">Commander tax per previous cast (0 outside commander games).</param>
    public static GameView Build(GameState state, PlayerId viewer, bool revealAll = false, int commanderTaxPerCast = 0)
    {
        CardView View(CardId id) => Card(state, id, viewer, revealAll, commanderTaxPerCast);

        IReadOnlyList<CardView> Views(IEnumerable<CardId> ids) => ids.Select(View).ToList();

        return new GameView
        {
            Viewer = viewer,
            TurnNumber = state.TurnNumber,
            ActivePlayer = state.ActivePlayer,
            PriorityPlayer = state.PriorityPlayer,
            Step = state.Step,
            Players = state.Players.Select(p => new PlayerView
            {
                Id = p.Id,
                Name = p.Name,
                Life = p.Life,
                HasLost = p.HasLost,
                LibraryCount = p.Library.Count,
                LibraryTop = p.Id == viewer && p.Library.Count > 0
                             && state.PermanentsControlledBy(p.Id).Any(c => (c.Definition.Replaces & Cards.Replacements.CreaturesFromLibraryTop) != 0)
                    ? Card(state, p.Library[0], viewer, reveal: true)
                    : null,
                Hand = Views(p.Hand),
                Graveyard = Views(p.Graveyard),
                Exile = Views(p.Exile),
                Command = Views(p.Command),
                ManaPoolTotal = p.ManaPool.Total,
                CommanderDamage = new Dictionary<CardId, int>(p.CommanderDamageTaken),
                ManaPool = Enum.GetValues<Mana.ManaType>().Where(t => p.ManaPool.AllOf(t) > 0).ToDictionary(t => t, t => p.ManaPool.AllOf(t)),
            }).ToList(),
            Battlefield = Views(state.Battlefield),
            Stack = state.Stack.Select(s => new StackItemView(
                View(s.SourceCard), s.Controller, (s as AbilityOnStack)?.Ability.Text, s.Targets.Select(t => t.Target).ToList(), s.Id)).ToList(),
            Attacks = state.Combat?.Attacks
                .Select(a => new AttackView(a.Attacker, a.Defender, a.Blockers.ToList(), a.IsBlocked, a.Planeswalker)).ToList()
                ?? (IReadOnlyList<AttackView>)Array.Empty<AttackView>(),
            IsGameOver = state.IsGameOver,
            Winner = state.Winner,
        };
    }

    /// <summary>One card as <paramref name="viewer"/> may see it; <paramref name="reveal"/> shows it even if hidden.</summary>
    public static CardView Card(GameState state, CardId id, PlayerId viewer, bool reveal = false, int commanderTaxPerCast = 0)
    {
        var card = state.GetCard(id);
        bool visible = reveal || card.Zone.IsPublic() || (card.Zone == Zone.Hand && card.Owner == viewer);
        if (!visible)
        {
            return new CardView
            {
                Id = card.Id, Owner = card.Owner, Controller = card.Controller, Zone = card.Zone, IsHidden = true,
            };
        }
        return new CardView
        {
            Id = card.Id,
            Owner = card.Owner,
            Controller = card.Controller,
            Zone = card.Zone,
            IsHidden = false,
            Name = card.Name,
            ManaCost = card.Definition.ManaCost.ToString(),
            Types = card.Types,
            Power = card.Definition.Power is null ? null : card.Power,
            Toughness = card.Definition.Toughness is null ? null : card.Toughness,
            Tapped = card.Tapped,
            Damage = card.Damage,
            SummoningSick = card.Zone == Zone.Battlefield && card.IsSummoningSick,
            Keywords = Enum.GetValues<Cards.Keyword>().Where(card.Has).Select(Cards.Keywords.DisplayName).ToList(),
            PlusOneCounters = card.CounterCount(Abilities.CounterKind.PlusOnePlusOne),
            MinusOneCounters = card.CounterCount(Abilities.CounterKind.MinusOneMinusOne),
            Loyalty = card.CounterCount(Abilities.CounterKind.Loyalty),
            AttacksEachCombat = card.Definition.AttacksEachCombat,
            IsToken = card.Definition.IsToken,
            OracleText = card.Definition.OracleText,
            Colors = card.Colors,
            ImageKey = card.Definition.ImageKey,
            AttachedTo = card.AttachedTo,
            IsCommander = card.IsCommander,
            CommanderTax = card.IsCommander ? commanderTaxPerCast * state.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id) : 0,
            BasePower = card.Definition.Power,
            BaseToughness = card.Definition.Toughness,
            AbilityTexts = card.Abilities.Select(a => a.Text).ToList(),
            Supertypes = card.Definition.Supertypes,
            Subtypes = card.CurrentSubtypes,
            PrintedTypes = card.Definition.Types,
            PrintedSubtypes = card.Definition.Subtypes,
            PrintedColors = card.Definition.ColorList,
            PrintedKeywords = card.Definition.KeywordAbilities.Select(Cards.Keywords.DisplayName).ToList(),
            LostAllAbilities = card.LostAllAbilities,
            GainedAbilityTexts = card.GainedAbilities.Select(a => a.Text).Where(t => !string.IsNullOrEmpty(t)).ToList(),
            OtherCounters = card.Counters.Where(kv => kv.Value > 0 && kv.Key is not (Abilities.CounterKind.PlusOnePlusOne or Abilities.CounterKind.MinusOneMinusOne or Abilities.CounterKind.Loyalty))
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ChosenColor = card.ChosenColor,
            ChosenType = card.ChosenType,
        };
    }
}
