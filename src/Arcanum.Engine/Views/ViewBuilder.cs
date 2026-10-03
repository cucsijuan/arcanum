// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Views;

public static class ViewBuilder
{
    /// <param name="revealAll">Show every hidden card (deck-test / hotseat / spectator-with-cheats).</param>
    public static GameView Build(GameState state, PlayerId viewer, bool revealAll = false)
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
                Keywords = card.Definition.KeywordAbilities.Select(Cards.Keywords.DisplayName).ToList(),
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
                ManaPool = Enum.GetValues<Mana.ManaType>().Where(t => p.ManaPool[t] > 0).ToDictionary(t => t, t => p.ManaPool[t]),
            }).ToList(),
            Battlefield = Views(state.Battlefield),
            Stack = state.Stack.OfType<SpellOnStack>().Select(s => new StackItemView(View(s.Card), s.Controller)).ToList(),
            Attacks = state.Combat?.Attacks
                .Select(a => new AttackView(a.Attacker, a.Defender, a.Blockers.ToList(), a.IsBlocked)).ToList()
                ?? (IReadOnlyList<AttackView>)Array.Empty<AttackView>(),
            IsGameOver = state.IsGameOver,
            Winner = state.Winner,
        };
    }
}
