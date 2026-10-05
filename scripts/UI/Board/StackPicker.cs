// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Small modal asking "how many of these identical creatures?" (attacking or blocking with part of a stack).
/// Closes itself; <see cref="Chosen"/> fires only on confirm.
/// </summary>
public partial class StackPicker : Control
{
    private readonly Label _title = BoardStyle.MakeLabel("", 18, bold: true);
    private readonly Label _value = BoardStyle.MakeLabel("", 22, BoardStyle.Playable, bold: true);
    private readonly HSlider _slider = new() { Step = 1, CustomMinimumSize = new Vector2(280, 28), FocusMode = FocusModeEnum.None };
    private readonly Button _confirm = BoardStyle.MakePrimaryButton("OK", 16);
    private int _max;

    /// <summary>The player confirmed this many.</summary>
    public event Action<int>? Chosen;

    public StackPicker()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = BoardStyle.Z.Picker;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1 };
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) Close(); };
        AddChild(shade);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        var box = BoardStyle.Box(BoardStyle.Panel, 12, BoardStyle.ActiveBorder, 2);
        box.SetContentMarginAll(16);
        panel.AddThemeStyleboxOverride("panel", box);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(_title);
        column.AddChild(_value);
        column.AddChild(_slider);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 8);
        var none = BoardStyle.MakeButton("None", 15);
        none.Pressed += () => _slider.Value = 0;
        var all = BoardStyle.MakeButton("All", 15);
        all.Pressed += () => _slider.Value = _max;
        var cancel = BoardStyle.MakeButton("Cancel", 15);
        cancel.Pressed += Close;
        _confirm.Pressed += () =>
        {
            int count = (int)_slider.Value;
            Close();
            Chosen?.Invoke(count);
        };
        foreach (var b in new[] { none, all, cancel, _confirm })
        {
            b.CustomMinimumSize = new Vector2(76, 36);
            buttons.AddChild(b);
        }
        column.AddChild(buttons);
        panel.AddChild(column);
        AddChild(panel);
        _slider.ValueChanged += _ => ShowValue();
    }

    /// <param name="title">The question, e.g. "Attack with how many Spirit tokens?".</param>
    /// <param name="min">Fewest that may be chosen (0 to allow none).</param>
    public void Open(string title, int min, int max, int initial)
    {
        _title.Text = title;
        _max = max;
        _slider.MinValue = min;
        _slider.MaxValue = max;
        _slider.Value = Math.Clamp(initial, min, max);
        ShowValue();
        Visible = true;
    }

    private void ShowValue() => _value.Text = $"{(int)_slider.Value} of {_max}";

    public void Close()
    {
        Visible = false;
        QueueFree();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey { Pressed: true } key) return;
        if (key.Keycode == Key.Escape) Close();
        else if (key.Keycode is Key.Enter or Key.KpEnter or Key.Space) _confirm.EmitSignal(BaseButton.SignalName.Pressed);
        else if (key.Keycode == Key.Left) _slider.Value -= 1;
        else if (key.Keycode == Key.Right) _slider.Value += 1;
        else return;
        GetViewport().SetInputAsHandled();
    }
}
