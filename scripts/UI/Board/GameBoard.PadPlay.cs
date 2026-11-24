using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Arcanum.Client;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.Rules;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// ARCANUM_PAD_PLAY=1 (testing, with ARCANUM_VS_BOT=1): the player's side plays the way the autopilot would (lands, then
/// spells, attacking with everything, the first legal targets), but only by pressing a controller's buttons: the arrows
/// take the focus to the card or answer to use, and confirm presses it. A choice the buttons can't make is printed as
/// "PAD PLAY: stuck" (with what was wanted) and answered directly, so the game goes on; a summary is printed when the
/// game ends.
/// </summary>
public partial class GameBoard
{
    private static readonly bool PadPlay = OS.GetEnvironment("ARCANUM_PAD_PLAY") == "1";

    private bool _padDriving;
    private int _padAnswered, _padStuck, _padPresses;
    private readonly Dictionary<string, int> _padStuckKinds = new();
    private (CardId Card, int Times) _padRepeat;

    /// <summary>A step of a plan: what to press, found when it's its turn (the board is built again between steps).</summary>
    private sealed record PadStep(string What, Func<Control?> Find);

    private void PadPlayDecision(Decision decision, GameView view)
    {
        if (!PadPlay || _padDriving || decision.IsAnswered) return;
        _padDriving = true;
        _ = PadDriveAsync(decision, view);
    }

    private async Task PadDriveAsync(Decision decision, GameView view)
    {
        string kind = decision.GetType().Name;
        string? problem = null;
        var done = new List<string>();
        int pressesBefore = _padPresses;
        try
        {
            await PadWait(0.5); // the panel and any announcement settle
            foreach (var step in PadPlan(decision, view))
            {
                if (decision.IsAnswered || !IsInsideTree()) break;
                var control = step.Find();
                if (control is null || !control.IsVisibleInTree())
                {
                    problem = $"nothing to press for {step.What}";
                    break;
                }
                if (!await PadFocusAsync(control))
                {
                    problem = $"the arrows don't reach {step.What} (focus stays on {Describe(GetViewport().GuiGetFocusOwner())}; "
                              + $"it is a {control.GetType().Name} on screen at {OnScreen(control)}, mouse {control.MouseFilter}, focus {control.FocusMode}, "
                              + $"z {control.ZIndex}; path: {string.Join(" > ", _padPath)})";
                    break;
                }
                await PadPress("ui_accept");
                done.Add(step.What);
                await PadWait(0.35);
            }
            for (int i = 0; i < 10 && !decision.IsAnswered && problem is null; i++) await PadWait(0.1);
            if (!decision.IsAnswered && IsInsideTree())
            {
                problem ??= "the presses didn't answer it";
                _padStuck++;
                _padStuckKinds[kind] = _padStuckKinds.GetValueOrDefault(kind) + 1;
                GD.Print($"PAD PLAY: stuck on {kind}: {problem}");
                if (OS.GetEnvironment("ARCANUM_SHOTS").Split(',')[0] is { Length: > 0 } folder)
                {
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"stuck-{_padStuck}.png"));
                    // And without the player's own half, to see what it covers.
                    var mine = AreaOf(decision.Player);
                    mine.Visible = false;
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"stuck-{_padStuck}-under.png"));
                    mine.Visible = true;
                }
                AutoAnswer(decision, _session.ViewFor(decision.Player));
            }
            else if (decision.IsAnswered)
            {
                _padAnswered++;
                GD.Print($"PAD PLAY: {kind}: {string.Join(", ", done)} ({_padPresses - pressesBefore} presses)");
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"PAD PLAY: {kind} failed: {e}");
        }
        finally
        {
            _padDriving = false;
        }
        // A question asked while this one was driven was skipped: take it now.
        if (IsInsideTree() && _session.CurrentDecision is { IsAnswered: false } next && !ReferenceEquals(next, decision))
            PadPlayDecision(next, _session.ViewFor(next.Player));
    }

    private static readonly int PadPlayGames = int.TryParse(OS.GetEnvironment("ARCANUM_PAD_PLAY_GAMES"), out var n) ? Math.Max(1, n) : 1;
    private static int _padGamesPlayed;
    private bool _padReported;

    /// <summary>
    /// The game ended: what the buttons did. Another game follows (ARCANUM_PAD_PLAY_GAMES, default 1), then the game quits.
    /// </summary>
    private void PadPlayReport(GameView view)
    {
        if (!PadPlay || _padReported) return;
        _padReported = true;
        _padGamesPlayed++;
        GetTree().CreateTimer(3).Timeout += () =>
        {
            if (_padGamesPlayed >= PadPlayGames) GetTree().Quit();
            else
            {
                // The next game is dealt differently.
                OS.SetEnvironment("ARCANUM_SEED", ((ulong.TryParse(OS.GetEnvironment("ARCANUM_SEED"), out var seed) ? seed : 1) + 1).ToString());
                GetTree().ReloadCurrentScene();
            }
        };
        string winner = view.Players.FirstOrDefault(p => !p.HasLost)?.Name ?? "nobody";
        GD.Print($"PAD PLAY: game over on turn {view.TurnNumber} ({winner} left): {_padAnswered} choices made with the buttons, "
                 + $"{_padStuck} stuck ({string.Join(", ", _padStuckKinds.Select(k => $"{k.Key} {k.Value}"))}), {_padPresses} presses");
    }

    private IEnumerable<PadStep> PadPlan(Decision decision, GameView view)
    {
        PadStep Primary() => new("the default answer", () => _primaryAction is { Disabled: false } p ? p : null);
        PadStep Button(string text) => new($"\"{text}\"", () => PanelButtons().FirstOrDefault(b => b.Text == text && !b.Disabled));
        PadStep Card(CardId id, string why) => new($"{why} {view.FindCard(id)?.Name ?? id.ToString()}", () => _zoneViewer.FindCard(id) ?? FindCard(id) ?? PileCard(id));

        switch (decision)
        {
            case PriorityDecision p:
            {
                var action = p.Legal.FirstOrDefault(a => a is PlayLand) ?? p.Legal.FirstOrDefault(a => a is CastSpell);
                CardId? card = action switch { PlayLand l => l.Card, CastSpell c => c.Card, _ => null };
                // The same card tried again and again (its cast keeps being taken back): pass instead.
                if (card is { } again && _padRepeat.Card == again && _padRepeat.Times >= 3) card = null;
                if (card is { } id)
                {
                    _padRepeat = _padRepeat.Card == id ? (id, _padRepeat.Times + 1) : (id, 1);
                    yield return Card(id, action is PlayLand ? "the land" : "the spell");
                    // A card with more than one use asks which: the first that isn't taking it back.
                    yield return new PadStep("its first use", () => _abilityChoiceSource is null ? null
                        : PanelButtons().FirstOrDefault(b => b.Text != "Cancel" && !b.Disabled));
                    yield break;
                }
                yield return Primary();
                yield break;
            }
            case TargetDecision t:
            {
                var picks = new List<Arcanum.Engine.Abilities.Target>();
                for (int i = 0; i < t.Request.Specs.Count; i++)
                {
                    var options = t.Request.LegalAt(i).Where(o => t.Request.IsAllowed(i, o, picks)).ToList();
                    if (options.Count == 0) break;
                    picks.Add(options.FirstOrDefault(o => !o.IsNone) is { } o && !o.IsNone ? o : options[0]);
                }
                if (picks.Count < t.Request.Specs.Count && t.Request.CanCancel)
                {
                    yield return Button("Cancel");
                    yield break;
                }
                foreach (var pick in picks)
                {
                    var target = pick;
                    yield return pick.IsNone ? Button("None")
                        : new PadStep(pick.Player is { } pl ? $"the player {view.Players[pl.Value].Name}" : $"the target {Named(pick, view)}",
                    () => target.Card is { } c ? _zoneViewer.FindCard(c) ?? TargetControl(target) : TargetControl(target));
                }
                yield return Primary(); // "Done" where the targets wait for it
                yield break;
            }
            case ManaPaymentDecision:
                yield return Button("Auto");
                yield return Primary();
                yield break;
            case AttackDecision a:
            {
                var attackers = a.PossibleAttackers
                    .Take(view.AttackTaxes.FirstOrDefault(tax => tax.Defender == a.Defenders[0])?.Affordable ?? int.MaxValue).ToList();
                foreach (var id in attackers) yield return Card(id, "the attacker");
                yield return Primary();
                yield break;
            }
            case BlockDecision b when b.Request.MinimumBlockers.Count == 0 && !b.Request.HasRequirements:
            {
                var target = b.Attackers[0];
                foreach (var blocker in b.PossibleBlockers.Where(bl => b.Request.CanBlock[bl].Contains(target)).Take(2))
                {
                    yield return Card(blocker, "the blocker");
                    yield return Card(target, "the attacker to block");
                }
                yield return Primary();
                yield break;
            }
            case SelectCardsDecision s:
                foreach (var card in view.Players[s.Player.Value].Hand.Take(s.Count)) yield return Card(card.Id, "the card");
                yield return Primary();
                yield break;
            case ChooseCardsDecision c:
                foreach (var option in c.Request.Options.Take(Math.Max(c.Request.Min, 1)).Take(c.Request.Max))
                    yield return Card(option.Id, "the card");
                yield return Primary();
                yield break;
            case ChooseModesDecision md:
                foreach (var _ in Enumerable.Range(0, Math.Max(1, md.Request.Min)))
                    yield return new PadStep("a mode", () => PanelButtons().FirstOrDefault(btn => !btn.Disabled && btn.Text != "Cancel"
                        && !btn.Text.StartsWith("Confirm") && !btn.ButtonPressed));
                yield return Primary();
                yield break;
            case ChooseOptionDecision:
                yield return new PadStep("the first option", () => PanelButtons().FirstOrDefault(btn => !btn.Disabled && btn.Text != "Cancel"));
                yield break;
            default: // keep or mulligan, yes or no, a number, damage: the default answer
                yield return Primary();
                yield break;
        }
    }

    private IEnumerable<Button> PanelButtons() =>
        Below(_actionButtons).Concat(Below(_actionExtra)).OfType<Button>().Where(b => !b.IsQueuedForDeletion() && b.IsVisibleInTree());

    /// <summary>Every node under <paramref name="node"/>, each before its own, gathered at once child by child.</summary>
    private static List<Node> Below(Node node, List<Node>? found = null)
    {
        found ??= new List<Node>();
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            var child = node.GetChild(i);
            found.Add(child);
            Below(child, found);
        }
        return found;
    }

    /// <summary>Moves the focus with the arrows until it is on <paramref name="target"/>; false if it can't get there.</summary>
    private async Task<bool> PadFocusAsync(Control target)
    {
        var tried = new HashSet<(Control?, string)>();
        _padPath.Clear();
        for (int i = 0; i < 60; i++)
        {
            if (!IsInstanceValid(target) || !target.IsVisibleInTree()) return false;
            var owner = GetViewport().GuiGetFocusOwner();
            if (owner == target) return true;
            if (owner is null)
            {
                await PadPress("ui_down"); // the first press only brings the focus back
                continue;
            }
            var delta = target.GetGlobalRect().GetCenter() - owner.GetGlobalRect().GetCenter();
            string horizontal = delta.X < 0 ? "ui_left" : "ui_right", vertical = delta.Y < 0 ? "ui_up" : "ui_down";
            var order = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new[] { horizontal, vertical } : new[] { vertical, horizontal };
            // Each way once from each place: a way that leaves the focus where it was isn't tried again from there.
            var way = order.FirstOrDefault(w => !tried.Contains((owner, w)) && (w == horizontal ? Math.Abs(delta.X) > 4 : Math.Abs(delta.Y) > 4));
            if (way is null) return false;
            tried.Add((owner, way));
            await PadPress(way);
            _padPath.Add($"{Describe(owner)} {way[3..]}");
        }
        return GetViewport().GuiGetFocusOwner() == target;
    }

    private readonly List<string> _padPath = new();

    private async Task PadPress(string action)
    {
        if (InputMap.ActionGetEvents(action).OfType<InputEventJoypadButton>().FirstOrDefault() is not { } mapped) return;
        _padPresses++;
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = mapped.ButtonIndex, Pressed = true });
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = mapped.ButtonIndex, Pressed = false });
        await PadWait(0.12);
    }

    private async Task PadWait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static Rect2 OnScreen(Control control) => control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);

    private static string AreaRect(Control control)
    {
        for (Node? n = control; n is not null; n = n.GetParent())
            if (n is PlayerArea area) return $"{area.Player} {OnScreen(area)} clip {area.ClipContents}";
        return "none";
    }

    private static string Named(Arcanum.Engine.Abilities.Target target, GameView view) =>
        target.Card is { } c ? view.FindCard(c)?.Name ?? c.ToString() : target.ToString();

    private static string Describe(Control? control) => control switch
    {
        null => "nothing",
        Button b => $"\"{b.Text}\"",
        CardNode c => c.View?.Name ?? "a card",
        _ => control.Name,
    };
}
