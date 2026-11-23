using System;
using System.Linq;
using Godot;

namespace Arcanum.UI;

/// <summary>
/// ARCANUM_PAD_SOAK=<seconds>[,<ms between presses>] (testing): plays with nothing but a controller's buttons, pressed at
/// random (the arrows, confirm and back, as a player would use them), for that long, then quits. A game that ends or a
/// screen left starts another game. The main thread stopping for over two seconds is printed as "PAD SOAK: stall"; every
/// press is counted and the turns reached are printed at the end. With ARCANUM_SHOTS it leaves pictures to look over.
/// </summary>
public partial class PadSoak : Node
{
    private const string BoardScene = "res://scenes/game_board/GameBoard.tscn";

    private static readonly (string Action, int Weight)[] Presses =
    {
        ("ui_left", 16), ("ui_right", 16), ("ui_up", 10), ("ui_down", 10), ("ui_accept", 38), ("ui_cancel", 10),
    };

    private readonly Random _random;
    private readonly double _seconds;
    private readonly double _every;
    private double _elapsed, _sinceLast;
    private ulong _lastFrameMs;
    private int _presses, _games, _stalls;
    private readonly System.Collections.Generic.Dictionary<string, int> _count = new();

    private PadSoak(double seconds, double every, int seed)
    {
        _seconds = seconds;
        _every = every;
        _random = new Random(seed);
    }

    /// <summary>The soak asked for by ARCANUM_PAD_SOAK, if any.</summary>
    public static PadSoak? FromEnvironment()
    {
        var parts = OS.GetEnvironment("ARCANUM_PAD_SOAK").Split(',');
        if (!double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return null;
        double ms = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 300;
        int seed = int.TryParse(OS.GetEnvironment("ARCANUM_SEED"), out var s) ? s : System.Environment.TickCount;
        return new PadSoak(seconds, ms / 1000, seed) { ProcessMode = ProcessModeEnum.Always };
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksMsec();
        if (_lastFrameMs != 0 && now - _lastFrameMs > 2000)
        {
            _stalls++;
            GD.Print($"PAD SOAK: stall of {(now - _lastFrameMs) / 1000.0:F1} s at {_elapsed:F0} s ({Screen()})");
        }
        _lastFrameMs = now;
        _elapsed += delta;
        if (_elapsed >= _seconds)
        {
            GD.Print($"PAD SOAK: done after {_elapsed:F0} s: {_presses} presses ({string.Join(", ", _count.Select(c => $"{c.Key} {c.Value}"))}), "
                     + $"{_games} games started, {_stalls} stalls, last turn {Turn()}");
            SetProcess(false);
            GetTree().Quit();
            return;
        }
        if (Arcanum.Client.App.Instance.Cards is null) return; // still loading
        // Not on the table (the game was left, or the menu was reached): another game.
        if (GetTree().CurrentScene is { } scene && scene.SceneFilePath != BoardScene)
        {
            _games++;
            GD.Print($"PAD SOAK: left the table for {scene.SceneFilePath} at {_elapsed:F0} s: game {_games + 1}");
            GetTree().ChangeSceneToFile(BoardScene);
            return;
        }
        _sinceLast += delta;
        if (_sinceLast < _every) return;
        _sinceLast = 0;
        Press(Pick());
    }

    private string Pick()
    {
        int roll = _random.Next(Presses.Sum(p => p.Weight));
        foreach (var (action, weight) in Presses)
        {
            if (roll < weight) return action;
            roll -= weight;
        }
        return "ui_accept";
    }

    /// <summary>Presses and lets go of the controller button the action is mapped to.</summary>
    private void Press(string action)
    {
        if (InputMap.ActionGetEvents(action).OfType<InputEventJoypadButton>().FirstOrDefault() is not { } mapped) return;
        _presses++;
        _count[action] = _count.GetValueOrDefault(action) + 1;
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = mapped.ButtonIndex, Pressed = true });
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = mapped.ButtonIndex, Pressed = false });
    }

    private string Screen()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        return $"{GetTree().CurrentScene?.SceneFilePath}, focus {owner?.GetType().Name} {(owner as Button)?.Text}";
    }

    private string Turn() => GetTree().CurrentScene is Board.GameBoard board ? board.TurnForTesting : "-";
}
