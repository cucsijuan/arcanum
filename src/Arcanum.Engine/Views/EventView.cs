// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Reflection;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Views;

/// <summary>
/// An event as one player may see it: hidden information removed, plus the cards it mentions as that player saw
/// them when it happened, so the event can be shown (log, announcements) without access to the game state.
/// </summary>
/// <param name="Stack">For a spell cast or an ability put on the stack: that stack object, with its targets.</param>
public sealed record EventView(GameEvent Event, IReadOnlyList<CardView> Cards, StackItemView? Stack = null)
{
    public CardView? Card(CardId id) => Cards.FirstOrDefault(c => c.Id == id);

    /// <summary>The card's name, or "a card" when the viewer can't see it.</summary>
    public string Name(CardId id) => Card(id) is { IsHidden: false, Name: { } name } ? name : "a card";

    public bool IsVisible(CardId id) => Card(id) is { IsHidden: false };
}

public static class EventViews
{
    private static readonly ConcurrentDictionary<Type, Func<GameEvent, IEnumerable<CardId>>> Readers = new();

    /// <summary>
    /// <paramref name="e"/> as <paramref name="viewer"/> may see it. Cards the viewer could see where they were
    /// (a permanent shuffled into a library, a card leaving their hand) stay visible in the event.
    /// </summary>
    public static EventView Build(GameState state, GameEvent e, PlayerId viewer, bool revealAll = false, int commanderTaxPerCast = 0)
    {
        // The seed decides every library's order: never shown to players.
        if (e is GameStarted started && !revealAll) e = started with { Seed = 0 };

        var revealed = e switch
        {
            CardsRevealed r => r.Cards.ToHashSet(),
            HandRevealed h => h.Cards.ToHashSet(),
            CardMoved m when m.From.IsPublic() || (m.From == Zone.Hand && m.Owner == viewer) => new HashSet<CardId> { m.Card },
            _ => new HashSet<CardId>(),
        };

        StackItemView? stack = null;
        var ids = CardsOf(e).ToList();
        CardId? source = e switch { SpellCast c => c.Card, AbilityActivated a => a.Source, AbilityTriggered t => t.Source, _ => null };
        if (source is { } src && state.Stack.LastOrDefault(s => s.SourceCard == src) is { } item)
        {
            var targets = item.Targets.Select(t => t.Target).ToList();
            stack = new StackItemView(ViewBuilder.Card(state, src, viewer, revealAll, commanderTaxPerCast), item.Controller,
                (item as AbilityOnStack)?.Ability.Text, targets, item.Id);
            ids.AddRange(targets.Where(t => t.Card is not null).Select(t => t.Card!.Value));
        }

        var cards = ids.Distinct()
            .Where(state.Cards.ContainsKey)
            .Select(id => ViewBuilder.Card(state, id, viewer, revealAll || revealed.Contains(id), commanderTaxPerCast))
            .ToList();
        return new EventView(e, cards, stack);
    }

    /// <summary>Every card id an event mentions (properties of type CardId, CardId? or a list of them).</summary>
    public static IEnumerable<CardId> CardsOf(GameEvent e) => Readers.GetOrAdd(e.GetType(), Reader)(e);

    private static Func<GameEvent, IEnumerable<CardId>> Reader(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(CardId) || p.PropertyType == typeof(CardId?)
                        || typeof(IEnumerable<CardId>).IsAssignableFrom(p.PropertyType))
            .ToList();
        return e => props.SelectMany(p => p.GetValue(e) switch
        {
            CardId id => new[] { id },
            IEnumerable<CardId> list => list,
            _ => Array.Empty<CardId>(),
        });
    }
}
