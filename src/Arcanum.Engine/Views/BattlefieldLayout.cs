// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;

namespace Arcanum.Engine.Views;

/// <summary>Which row of a player's side of the table a permanent stands in, back to front.</summary>
public enum BattlefieldRow { Lands, Other, Creatures }

/// <summary>Identical tokens shown as one pile; <see cref="Cards"/> are in battlefield order, the first one is the one drawn on top.</summary>
public sealed record CardStack(IReadOnlyList<CardView> Cards)
{
    public CardView Top => Cards[0];
    public int Count => Cards.Count;
}

/// <summary>
/// Pure rules for arranging a player's permanents: which row each one goes to and which tokens are shown as one stack.
/// Kept apart from the board nodes so it can be tested without the game board.
/// </summary>
public static class BattlefieldLayout
{
    /// <summary>Lands in the back row, creatures in the front row (also a land that is a creature now), everything else between them.</summary>
    public static BattlefieldRow RowOf(CardView card)
    {
        if ((card.Types & CardType.Creature) != 0) return BattlefieldRow.Creatures;
        if ((card.Types & CardType.Land) != 0) return BattlefieldRow.Lands;
        return BattlefieldRow.Other;
    }

    /// <summary>
    /// What makes two tokens interchangeable on the table: same name and printing, same characteristics, controller,
    /// tapped state, summoning sickness, damage and counters. Null when the card is never part of a stack (not a token,
    /// hidden, a commander, attached to something or carrying an attachment).
    /// </summary>
    /// <param name="tapped">Whether it is drawn tapped (includes sources picked in an unconfirmed payment).</param>
    /// <param name="hasAttachments">Something is attached to it: it keeps its own place to show what is on it.</param>
    public static string? StackKey(CardView card, bool tapped, bool hasAttachments)
    {
        if (!card.IsToken || card.IsHidden || card.IsCommander || card.AttachedTo is not null || hasAttachments) return null;
        string List(IEnumerable<string> items) => string.Join(",", items);
        return string.Join("|",
            card.Name, card.ImageKey, (int)card.Types, (int)card.Supertypes, List(card.Subtypes), List(card.Colors),
            card.Owner.Value, card.Controller.Value,
            tapped ? "T" : "U", card.SummoningSick ? "S" : "-", card.AttacksEachCombat ? "A" : "-",
            card.Power, card.Toughness, card.Damage, card.PlusOneCounters, card.MinusOneCounters, card.Loyalty, card.LoreCounters,
            List(card.OtherCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")),
            List(card.Keywords), card.LostAllAbilities ? "L" : "-", List(card.GainedAbilityTexts), card.ChosenColor, card.ChosenType,
            card.OracleText);
    }

    /// <summary>
    /// Groups a row's cards into stacks. <paramref name="keyOf"/> gives a card's stack key (null: never stacks);
    /// cards with equal keys share a stack. A stack stands where its first card was, except that tokens with the same
    /// name stay next to each other (a stack that split off follows its siblings). Lands of one name stay together too.
    /// </summary>
    public static IReadOnlyList<CardStack> Stack(IEnumerable<CardView> cards, Func<CardView, string?> keyOf)
    {
        var units = new List<List<CardView>>();
        var byKey = new Dictionary<string, List<CardView>>();
        foreach (var card in cards)
        {
            if (keyOf(card) is { } key)
            {
                if (byKey.TryGetValue(key, out var existing)) { existing.Add(card); continue; }
                var stack = new List<CardView> { card };
                byKey[key] = stack;
                units.Add(stack);
            }
            else units.Add(new List<CardView> { card });
        }

        // Cards that look alike (tokens, lands) sit together, in order of the first one of each name.
        var ordered = new List<List<CardView>>();
        var lastOfName = new Dictionary<string, int>();
        foreach (var unit in units)
        {
            var top = unit[0];
            bool alike = top.Name is not null && (top.IsToken || (top.Types & CardType.Land) != 0) && top.AttachedTo is null;
            if (alike && lastOfName.TryGetValue(top.Name!, out int at))
            {
                ordered.Insert(at + 1, unit);
                foreach (var name in lastOfName.Keys.ToList()) if (lastOfName[name] > at) lastOfName[name]++;
                lastOfName[top.Name!] = at + 1;
            }
            else
            {
                ordered.Add(unit);
                if (alike) lastOfName[top.Name!] = ordered.Count - 1;
            }
        }
        return ordered.Select(u => new CardStack(u)).ToList();
    }

    /// <summary>
    /// Makes exactly <paramref name="count"/> of the identical <paramref name="members"/> chosen (as attackers, blockers):
    /// the ones already chosen stay chosen first, then the others in order. Says which to add and which to drop.
    /// </summary>
    public static (IReadOnlyList<CardId> Add, IReadOnlyList<CardId> Remove) Pick(IReadOnlyList<CardId> members, Func<CardId, bool> chosen, int count)
    {
        count = Math.Clamp(count, 0, members.Count);
        var ordered = members.Where(chosen).Concat(members.Where(m => !chosen(m))).ToList();
        var wanted = ordered.Take(count).ToHashSet();
        return (members.Where(m => wanted.Contains(m) && !chosen(m)).ToList(), members.Where(m => !wanted.Contains(m) && chosen(m)).ToList());
    }
}
