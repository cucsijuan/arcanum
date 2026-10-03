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
        CardView View(CardId id)
        {
            var card = state.GetCard(id);
            bool visible = revealAll || card.Zone.IsPublic() || (card.Zone == Zone.Hand && card.Owner == viewer);
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
                IsToken = card.Definition.IsToken,
                AttachedTo = card.AttachedTo,
                IsCommander = card.IsCommander,
                CommanderTax = card.IsCommander ? commanderTaxPerCast * state.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id) : 0,
                BasePower = card.Definition.Power,
                BaseToughness = card.Definition.Toughness,
                AbilityTexts = card.Definition.Abilities.Select(a => a.Text).ToList(),
            };
        }

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
                Hand = Views(p.Hand),
                Graveyard = Views(p.Graveyard),
                Exile = Views(p.Exile),
                Command = Views(p.Command),
                ManaPoolTotal = p.ManaPool.Total,
                CommanderDamage = new Dictionary<CardId, int>(p.CommanderDamageTaken),
                ManaPool = Enum.GetValues<Mana.ManaType>().Where(t => p.ManaPool[t] > 0).ToDictionary(t => t, t => p.ManaPool[t]),
            }).ToList(),
            Battlefield = Views(state.Battlefield),
            Stack = state.Stack.Select(s => new StackItemView(
                View(s.SourceCard), s.Controller, (s as AbilityOnStack)?.Ability.Text, s.Targets.Select(t => t.Target).ToList())).ToList(),
            Attacks = state.Combat?.Attacks
                .Select(a => new AttackView(a.Attacker, a.Defender, a.Blockers.ToList(), a.IsBlocked)).ToList()
                ?? (IReadOnlyList<AttackView>)Array.Empty<AttackView>(),
            IsGameOver = state.IsGameOver,
            Winner = state.Winner,
        };
    }
}
