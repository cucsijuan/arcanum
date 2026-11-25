// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Godot;

namespace Arcanum.UI;

/// <summary>
/// ARCANUM_PAD_KEYS="w12 r r d a b" (testing): presses the controller's buttons in that order (l, r, u, d the arrows,
/// a confirm, b back, x the left face button; w&lt;seconds&gt; waits), with what has the focus printed after each, then
/// quits. The waiting starts once the cards are loaded. With ARCANUM_SHOTS a picture is left after each press
/// (pad_01_r.png…).
/// </summary>
public partial class PadScript : Node
{
    private readonly string[] _steps;

    private PadScript(string[] steps) => _steps = steps;

    /// <summary>The presses asked for by ARCANUM_PAD_KEYS, if any.</summary>
    public static PadScript? FromEnvironment() =>
        OS.GetEnvironment("ARCANUM_PAD_KEYS").Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } steps
            ? new PadScript(steps) { ProcessMode = ProcessModeEnum.Always } : null;

    public override async void _Ready()
    {
        while (Arcanum.Client.App.Instance.Cards is null) await Wait(0.2);
        var folder = OS.GetEnvironment("ARCANUM_SHOTS").Split(',')[0];
        int taken = 0;
        foreach (var step in _steps)
        {
            if (step[0] == 'w')
            {
                await Wait(double.Parse(step[1..], CultureInfo.InvariantCulture));
                continue;
            }
            await Press(step);
            var owner = GetViewport().GuiGetFocusOwner();
            GD.Print($"PAD KEYS: {step} -> {owner?.GetType().Name} {(owner as Button)?.Text ?? (owner as Board.CardNode)?.View?.Name} {owner?.GetGlobalRect()}");
            if (folder.Length == 0) continue;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"pad_{++taken:D2}_{step}.png"));
        }
        GetTree().Quit();
    }

    private async Task Press(string step)
    {
        var button = step switch
        {
            "l" => JoyButton.DpadLeft, "r" => JoyButton.DpadRight, "u" => JoyButton.DpadUp, "d" => JoyButton.DpadDown,
            "a" => Mapped("ui_accept"), "b" => Mapped("ui_cancel"), "x" => PadFocus.AltButton,
            _ => throw new ArgumentException($"ARCANUM_PAD_KEYS: {step}?"),
        };
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = true });
        await Wait(0.1);
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = false });
        await Wait(0.6);
    }

    private static JoyButton Mapped(string action) =>
        InputMap.ActionGetEvents(action).OfType<InputEventJoypadButton>().First().ButtonIndex;

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
