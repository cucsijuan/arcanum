// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>
/// Preparation cards ("While it's prepared, you may cast a copy of its spell. Doing so unprepares it.") and empower Jace.
/// </summary>
public sealed partial class Game
{
    /// <summary>
    /// The creature becomes prepared: a copy of its prepare spell is put into exile, castable by the creature's current controller while it
    /// stays prepared. A creature without a prepare spell, or one already prepared, doesn't become prepared (again).
    /// </summary>
    private void Prepare(Card creature, bool announce = true)
    {
        if (creature.Zone != Zone.Battlefield || creature.PreparedCopy is not null || creature.Definition.PrepareSpell is not { } spell) return;
        var id = new CardId(State.Cards.Keys.Max(k => k.Value) + 1);
        var copy = new Card(id, spell with { IsToken = true }, creature.Controller) { Zone = Zone.Exile, PreparedSpellOf = creature.Id };
        State.Cards.Add(id, copy);
        State.GetPlayer(copy.Owner).Exile.Add(id);
        creature.PreparedCopy = id;
        if (announce) Emit(new PreparedChanged(creature.Id, true));
    }

    /// <summary>The creature becomes unprepared: the copy of its prepare spell still in exile ceases to exist.</summary>
    private void Unprepare(Card creature, bool announce)
    {
        if (creature.PreparedCopy is not { } id) return;
        creature.PreparedCopy = null;
        var copy = State.GetCard(id);
        copy.PreparedSpellOf = null;
        if (copy.Zone == Zone.Exile && !copy.CeasedToExist)
        {
            State.GetPlayer(copy.Owner).Exile.Remove(id);
            copy.CeasedToExist = true;
        }
        if (announce) Emit(new PreparedChanged(creature.Id, false));
    }

    /// <summary>The copies of prepare spells <paramref name="player"/> may cast: those of the prepared creatures they control now.</summary>
    private IEnumerable<CardId> PreparedSpells(PlayerId player) =>
        State.PermanentsControlledBy(player).Where(c => c.PreparedCopy is { } id && State.GetCard(id).Zone == Zone.Exile).Select(c => c.PreparedCopy!.Value).ToList();

    /// <summary>
    /// Casting the copy of a prepare spell unprepares its creature (the copy itself is now on the stack). Called with the spell just put
    /// on the stack and the creature it was prepared on.
    /// </summary>
    private void CastPreparedSpell(Card spell, CardId creatureId)
    {
        spell.CastPrepared = true;
        var creature = State.GetCard(creatureId);
        if (creature.PreparedCopy == spell.Id) creature.PreparedCopy = null;
    }

    /// <summary>Empower Jace N (see <see cref="Abilities.EmpowerJace"/>).</summary>
    private async Task EmpowerJaceAsync(PlayerId player, int amount, Card source)
    {
        List<Card> Jaces() => State.PermanentsControlledBy(player).Where(c => c.PrintedDefinition.IsToken && c.Is(CardType.Planeswalker) && c.HasSubtype("Jace")).ToList();
        var jaces = Jaces();
        if (jaces.Count == 0)
        {
            await CreateTokenEventAsync(player, PredefinedTokens.Jace, 1, false, source);
            jaces = Jaces();
        }
        if (jaces.Count == 0) return;
        var jace = jaces[0];
        if (jaces.Count > 1)
        {
            var pick = await ControllerOf(player).ChooseCardsAsync(ViewFor(player), new CardChoiceRequest($"{source.Name}: choose the Jace token to empower", source.Id,
                jaces.Select(c => ViewBuilder.Card(State, c.Id, player)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && jaces.Any(c => c.Id == pick[0]), "Choose one of your Jace tokens.");
            jace = State.GetCard(pick[0]);
        }
        if (amount > 0) PutCounters(jace, Abilities.CounterKind.Loyalty, amount, player);
    }
}
