// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Shown beside the large card preview: what is different about the card right now compared with what is printed
/// on it (type and color changes, lost or gained abilities, counters, damage, modified power/toughness), plus its
/// state on the battlefield (tapped, attacking, attached, controlled by someone else).
/// </summary>
public partial class CardStatusPanel : VBoxContainer
{
    private static readonly Color Changed = new("f2c14e");
    private static readonly Color Lost = new("ff7a5c");
    private static readonly Color Gained = new("6fd08c");
    private static readonly Color Counter = new("b48cff");
    private static readonly Color State = new("7fb2ff");
    private static readonly Color Neutral = new("a0a1a8");

    public CardStatusPanel()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 6);
        CustomMinimumSize = new Vector2(260, 0);
    }

    /// <summary>Fills the panel for <paramref name="card"/>; returns false (and hides) when there is nothing to tell.</summary>
    public bool ShowFor(CardView card, GameView view)
    {
        foreach (var child in GetChildren()) child.QueueFree();
        if (card.IsHidden) { Visible = false; return false; }
        foreach (var (text, color) in Chips(card, view)) AddChild(Chip(text, color));
        Visible = GetChildCount() > 0;
        return Visible;
    }

    private static IEnumerable<(string Text, Color Color)> Chips(CardView c, GameView view)
    {
        string NameOf(Arcanum.Engine.Core.CardId id) => view.FindCard(id)?.Name ?? "a permanent";
        string Player(Arcanum.Engine.Core.PlayerId p) => view.Players[p.Value].Name;
        bool onBattlefield = c.Zone == Arcanum.Engine.State.Zone.Battlefield;

        // Characteristics changed by effects.
        if (c.Types != c.PrintedTypes || !SameSet(c.Subtypes, c.PrintedSubtypes))
        {
            yield return ($"Is now: {TypeLine(c.Supertypes, c.Types, c.Subtypes, c.Colors)}", Changed);
            var lostTypes = Enum.GetValues<CardType>().Where(t => t != CardType.None && (c.PrintedTypes & t) != 0 && (c.Types & t) == 0).Select(t => t.ToString()).ToList();
            var lostSubtypes = c.PrintedSubtypes.Where(s => !c.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (lostTypes.Count + lostSubtypes.Count > 0) yield return ($"No longer: {string.Join(", ", lostTypes.Concat(lostSubtypes))}", Lost);
            var gainedSubtypes = c.Subtypes.Where(s => !c.PrintedSubtypes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            var gainedTypes = Enum.GetValues<CardType>().Where(t => t != CardType.None && (c.Types & t) != 0 && (c.PrintedTypes & t) == 0).Select(t => t.ToString()).ToList();
            if (gainedTypes.Count + gainedSubtypes.Count > 0) yield return ($"Also: {string.Join(", ", gainedTypes.Concat(gainedSubtypes))}", Gained);
        }
        if (!SameSet(c.Colors, c.PrintedColors))
            yield return (c.Colors.Count == 0 ? $"Colorless (printed {ColorWords(c.PrintedColors)})" : $"{ColorWords(c.Colors)} (printed {ColorWords(c.PrintedColors)})", Changed);
        if (c.LostAllAbilities) yield return ("Lost its abilities", Lost);
        foreach (var keyword in c.Keywords.Where(k => !c.PrintedKeywords.Contains(k))) yield return ($"+ {keyword}", Gained);
        if (!c.LostAllAbilities)
            foreach (var keyword in c.PrintedKeywords.Where(k => !c.Keywords.Contains(k))) yield return ($"Lost {keyword}", Lost);
        foreach (var text in c.GainedAbilityTexts) yield return ($"+ \"{Shorten(text, 90)}\"", Gained);

        // Power and toughness, counters, damage.
        if (c.Power is { } p && c.Toughness is { } t && (p != c.BasePower || t != c.BaseToughness))
            yield return ($"{p}/{t} (printed {c.BasePower}/{c.BaseToughness})", p + t >= (c.BasePower ?? 0) + (c.BaseToughness ?? 0) ? Gained : Lost);
        if (c.PlusOneCounters > 0) yield return ($"+1/+1 counters: {c.PlusOneCounters}", Counter);
        if (c.MinusOneCounters > 0) yield return ($"−1/−1 counters: {c.MinusOneCounters}", Counter);
        if (c.Loyalty > 0) yield return ($"Loyalty: {c.Loyalty}", Counter);
        foreach (var (kind, count) in c.OtherCounters) yield return ($"{kind} counters: {count}", Counter);
        if (c.Damage > 0) yield return ($"{c.Damage} damage marked", Lost);

        // Battlefield state.
        if (onBattlefield)
        {
            if (c.Controller != c.Owner) yield return ($"Controlled by {Player(c.Controller)} (owner: {Player(c.Owner)})", Changed);
            if (c.Tapped) yield return ("Tapped", State);
            if (c.SummoningSick && (c.Types & CardType.Creature) != 0) yield return ("Summoning sick: can't attack or use {T} this turn", Neutral);
            if (view.Attacks.FirstOrDefault(a => a.Attacker == c.Id) is { } attack)
                yield return ($"Attacking {(attack.Planeswalker is { } pw ? NameOf(pw) : Player(attack.Defender))}" + (attack.IsBlocked ? " (blocked)" : ""), State);
            foreach (var blocked in view.Attacks.Where(a => a.Blockers.Contains(c.Id))) yield return ($"Blocking {NameOf(blocked.Attacker)}", State);
            if (c.AttachedTo is { } host) yield return ($"Attached to {NameOf(host)}", State);
            var attachments = view.Battlefield.Where(a => a.AttachedTo == c.Id).Select(a => a.Name ?? "?").ToList();
            if (attachments.Count > 0) yield return ($"Attached: {string.Join(", ", attachments)}", State);
            if (c.AttacksEachCombat) yield return ("Attacks each combat if able", Neutral);
        }
        if (c.HeldUnder is { } holder) yield return ($"Exiled by {NameOf(holder)} until it leaves the battlefield", State);
        var heldCards = view.Players.SelectMany(p => p.Exile).Where(e => e.HeldUnder == c.Id).Select(e => e.IsHidden ? "a face-down card" : e.Name ?? "?").ToList();
        if (heldCards.Count > 0) yield return ($"Exiled under it: {string.Join(", ", heldCards)}", State);
        if (c.ChosenColor is { } color) yield return ($"Chosen color: {ColorWords(new[] { color })}", Neutral);
        if (c.ChosenType is { } type) yield return ($"Chosen type: {type}", Neutral);
        if (c.IsCommander) yield return ("Commander", Neutral);
        if (c.IsToken) yield return ("Token", Neutral);
    }

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));

    private static string TypeLine(Supertype supertypes, CardType types, IReadOnlyList<string> subtypes, IReadOnlyList<string> colors)
    {
        var words = new List<string>();
        if (colors.Count == 0) words.Add("colorless");
        words.AddRange(Enum.GetValues<Supertype>().Where(s => s != Supertype.None && (supertypes & s) != 0).Select(s => s.ToString().ToLowerInvariant()));
        words.AddRange(Enum.GetValues<CardType>().Where(t => t != CardType.None && (types & t) != 0).Select(t => t.ToString().ToLowerInvariant()));
        var line = string.Join(' ', words);
        line = char.ToUpper(line[0]) + line[1..];
        return subtypes.Count > 0 ? $"{line} — {string.Join(' ', subtypes)}" : line;
    }

    private static string ColorWords(IEnumerable<string> colors) => string.Join("/", colors.Select(c => c switch
    {
        "W" => "white", "U" => "blue", "B" => "black", "R" => "red", "G" => "green", _ => c,
    })) is { Length: > 0 } s ? s : "colorless";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static Control Chip(string text, Color accent)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var box = BoardStyle.Box(new Color(0.08f, 0.08f, 0.1f, 0.94f), 8, accent, 1);
        box.BorderWidthLeft = 5;
        box.SetContentMarginAll(8);
        box.ContentMarginLeft = 12;
        panel.AddThemeStyleboxOverride("panel", box);
        var label = BoardStyle.MakeLabel(text, 15, accent.Lerp(Colors.White, 0.35f));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(240, 0);
        panel.AddChild(label);
        return panel;
    }
}
