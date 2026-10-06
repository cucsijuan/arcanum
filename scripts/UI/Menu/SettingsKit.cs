// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// Controls of the settings screen: tabs, option rows that light up under the pointer, an on/off switch, a value
/// between arrows and a slider between arrows. They use the interface kit, or plain shapes without it.
/// </summary>
public static partial class SettingsKit
{
    public const float RowHeight = 46;
    public const float ControlWidth = 380;

    private static readonly Color Dark = UiArt.Well;
    private static readonly Color DimText = new(0.62f, 0.66f, 0.72f);

    /// <summary>A section heading with the ornamental divider under it.</summary>
    public static Control Section(string title)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        box.AddChild(BoardStyle.MakeTitle(title, 22));
        box.AddChild(UiArt.Divider());
        return box;
    }

    /// <summary>A kit piece drawn as a whole style box (fill under frame), or a flat box without the kit.</summary>
    private static StyleBox Pieces(Color fill, Color plainBorder, params (string Name, bool Tinted)[] layers)
    {
        if (UiArt.Available)
        {
            var box = new KitBox();
            foreach (var (name, tinted) in layers) box.Add(UiArt.Get(name)!, tinted ? fill : Colors.White, fixedHeight: true);
            return box;
        }
        var flat = BoardStyle.Box(fill, 6, plainBorder, 1);
        flat.SetContentMarginAll(0);
        return flat;
    }

    /// <summary>The value box between two arrows.</summary>
    private static PanelContainer ValueBox(Label label, float width)
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 34), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Pieces(Dark, BoardStyle.PanelBorder, ("choice_fill", true), ("choice_frame", false)));
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        panel.AddChild(label);
        return panel;
    }

    /// <summary>A left or right arrow; dimmed when it can't go further.</summary>
    private static BaseButton Arrow(bool right, Action pressed)
    {
        BaseButton button;
        if (UiArt.Texture(right ? "arrow_right" : "arrow_left") is { } texture)
            button = new TextureButton { TextureNormal = texture, StretchMode = TextureButton.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(30, 34) };
        else
        {
            var plain = BoardStyle.MakeButton(right ? ">" : "<", 15, plain: true);
            plain.CustomMinimumSize = new Vector2(30, 30);
            button = plain;
        }
        button.FocusMode = Control.FocusModeEnum.None;
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        button.MouseEntered += () => { if (!button.Disabled) button.Modulate = new Color(1.3f, 1.25f, 1.15f); };
        button.MouseExited += () => button.Modulate = button.Disabled ? new Color(1, 1, 1, 0.35f) : Colors.White;
        button.Pressed += pressed;
        return button;
    }

    private static void SetEnabled(BaseButton arrow, bool enabled)
    {
        arrow.Disabled = !enabled;
        arrow.Modulate = enabled ? Colors.White : new Color(1, 1, 1, 0.35f);
    }

    /// <summary>A value picked with arrows on each side (no wrap-around).</summary>
    public static Control Choice(IReadOnlyList<string> items, int selected, Action<int> changed)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(ControlWidth, 0) };
        row.AddThemeConstantOverride("separation", 4);
        int index = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        var label = BoardStyle.MakeLabel(items.ElementAtOrDefault(index) ?? "", 16);
        BaseButton left = null!, right = null!;
        void Show()
        {
            label.Text = items.ElementAtOrDefault(index) ?? "";
            SetEnabled(left, index > 0);
            SetEnabled(right, index < items.Count - 1);
        }
        void Step(int delta)
        {
            int next = Math.Clamp(index + delta, 0, items.Count - 1);
            if (next == index) return;
            index = next;
            Show();
            changed(index);
        }
        left = Arrow(false, () => Step(-1));
        right = Arrow(true, () => Step(1));
        var value = ValueBox(label, 0);
        value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(left);
        row.AddChild(value);
        row.AddChild(right);
        Show();
        return row;
    }

    /// <summary>An On/Off switch: the chosen half is highlighted.</summary>
    public static Control OnOff(bool on, Action<bool> changed)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(ControlWidth, 0) };
        row.AddThemeConstantOverride("separation", 0);
        var halves = new Button[2];
        void Style()
        {
            for (int i = 0; i < 2; i++)
            {
                bool chosen = (i == 0) == on;
                var fill = chosen ? UiArt.Highlight : Dark;
                string side = i == 0 ? "left" : "right";
                var normal = Pieces(fill, chosen ? UiArt.Highlight : BoardStyle.PanelBorder, ($"switch_fill_{side}", true), ($"switch_{side}", false));
                var hover = Pieces(chosen ? fill : fill.Lightened(0.08f), BoardStyle.TextDim, ($"switch_fill_{side}", true), ($"switch_{side}", false));
                foreach (var state in new[] { "normal", "pressed", "focus" }) halves[i].AddThemeStyleboxOverride(state, normal);
                foreach (var state in new[] { "hover", "hover_pressed" }) halves[i].AddThemeStyleboxOverride(state, hover);
                var text = chosen ? Colors.White : DimText;
                foreach (var color in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
                    halves[i].AddThemeColorOverride(color, text);
            }
        }
        for (int i = 0; i < 2; i++)
        {
            bool value = i == 0;
            var half = halves[i] = new Button { Text = value ? "On" : "Off", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
            half.AddThemeFontSizeOverride("font_size", 16);
            half.Pressed += () =>
            {
                if (on == value) return;
                on = value;
                Style();
                changed(on);
            };
            row.AddChild(half);
        }
        Style();
        return row;
    }

    /// <summary>
    /// A slider between arrows with its value in a box. <paramref name="commitOnRelease"/>: dragging only previews
    /// the value; it is set when the pointer is released (or an arrow is pressed).
    /// </summary>
    public static Control Slider(double min, double max, double step, double value, Func<double, string> format, Action<double> changed, bool commitOnRelease = false)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(ControlWidth, 0) };
        row.AddThemeConstantOverride("separation", 4);
        var label = BoardStyle.MakeLabel(format(value), 16);
        var track = new SliderTrack(min, max, step, value);
        BaseButton left = null!, right = null!;
        void Show(double v)
        {
            label.Text = format(v);
            SetEnabled(left, v > min + step / 2);
            SetEnabled(right, v < max - step / 2);
        }
        void Commit(double v) { Show(v); changed(v); }
        left = Arrow(false, () => { track.Value = Math.Max(min, track.Value - step); Commit(track.Value); });
        right = Arrow(true, () => { track.Value = Math.Min(max, track.Value + step); Commit(track.Value); });
        track.Moved += v => { if (commitOnRelease) Show(v); else Commit(v); };
        track.Released += v => { if (commitOnRelease) Commit(v); };
        row.AddChild(left);
        row.AddChild(track);
        row.AddChild(ValueBox(label, 96));
        row.AddChild(right);
        Show(value);
        return row;
    }

    /// <summary>The rails of a slider with the filled part; click or drag to set it.</summary>
    private partial class SliderTrack : Control
    {
        private readonly double _min, _max, _step;
        private double _value;
        private bool _dragging;

        public event Action<double>? Moved;
        public event Action<double>? Released;

        public SliderTrack(double min, double max, double step, double value)
        {
            (_min, _max, _step, _value) = (min, max, step, value);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(120, 34);
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = CursorShape.PointingHand;
        }

        public double Value
        {
            get => _value;
            set { _value = Math.Clamp(value, _min, _max); QueueRedraw(); }
        }

        private const float Inset = 9;

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                _dragging = button.Pressed;
                if (button.Pressed) MoveTo(button.Position.X);
                else Released?.Invoke(_value);
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion motion && _dragging)
            {
                MoveTo(motion.Position.X);
                AcceptEvent();
            }
        }

        private void MoveTo(float x)
        {
            double t = Math.Clamp((x - Inset) / Math.Max(1, Size.X - 2 * Inset), 0, 1);
            double v = Math.Round((_min + t * (_max - _min)) / _step) * _step;
            v = Math.Clamp(v, _min, _max);
            if (Math.Abs(v - _value) < 1e-9) return;
            Value = v;
            Moved?.Invoke(v);
        }

        public override void _Draw()
        {
            float t = (float)((_value - _min) / Math.Max(1e-9, _max - _min));
            float y = Size.Y / 2;
            var inner = new Rect2(Inset, y - 3, Size.X - 2 * Inset, 6);
            if (UiArt.Get("slider_frame") is { } frame)
            {
                var item = GetCanvasItem();
                UiArt.Draw(item, frame, new Rect2(0, Mathf.Round(y - frame.Size.Y / 2), Size.X, frame.Size.Y), Colors.White);
                UiArt.Draw(item, UiArt.Get("slider_track")!, inner, Colors.White);
                if (t > 0) UiArt.Draw(item, UiArt.Get("slider_fill")!, inner with { Size = new Vector2(Mathf.Max(inner.Size.X * t, 12), inner.Size.Y) }, Colors.White);
                return;
            }
            DrawRect(inner, new Color(0.1f, 0.11f, 0.13f));
            DrawRect(inner with { Size = new Vector2(inner.Size.X * t, inner.Size.Y) }, BoardStyle.Playable);
        }
    }

    /// <summary>
    /// One option of a page: its name on the left, its control on the right. Under the pointer it lights up (a glow with
    /// gold lines fading to the right) and shows its description.
    /// </summary>
    public partial class Row : Control
    {
        private static Row? _lit;
        private readonly Label _name;
        private float _light;

        /// <summary>Raised with the description of the row the pointer is on.</summary>
        public static event Action<string>? Described;

        public string Description { get; }

        public Row(string name, Control control, string description)
        {
            Description = description;
            CustomMinimumSize = new Vector2(0, RowHeight);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            MouseFilter = MouseFilterEnum.Pass;
            _name = BoardStyle.MakeLabel(name, 17);
            _name.AnchorBottom = 1;
            _name.OffsetLeft = 34;
            _name.VerticalAlignment = VerticalAlignment.Center;
            AddChild(_name);
            control.AnchorLeft = 1; control.AnchorRight = 1; control.AnchorTop = 0.5f; control.AnchorBottom = 0.5f;
            control.OffsetLeft = -ControlWidth - 8; control.OffsetRight = -8;
            control.GrowVertical = GrowDirection.Both;
            AddChild(control);
            // Hovering the control also lights its row.
            control.MouseEntered += Light;
            MouseEntered += Light;
        }

        private void Light()
        {
            if (_lit == this) return;
            _lit?.Dim();
            _lit = this;
            _name.AddThemeColorOverride("font_color", UiArt.Gold);
            CreateTween().TweenMethod(Callable.From<float>(v => Glow = v), Glow, 1f, 0.15);
            Described?.Invoke(Description);
        }

        private void Dim()
        {
            _name.AddThemeColorOverride("font_color", BoardStyle.Text);
            CreateTween().TweenMethod(Callable.From<float>(v => Glow = v), Glow, 0f, 0.2);
        }

        public override void _ExitTree()
        {
            if (_lit == this) _lit = null;
        }

        /// <summary>How lit the row is (0–1), tweened.</summary>
        public float Glow
        {
            get => _light;
            set { _light = value; QueueRedraw(); }
        }

        public override void _Draw()
        {
            if (_light <= 0.01f) return;
            float width = Size.X - ControlWidth - 24;
            var tint = UiArt.Highlight with { A = 0.85f * _light };
            if (UiArt.Texture("gradient") is { } gradient)
                DrawTextureRect(gradient, new Rect2(8, 3, width, Size.Y - 6), false, tint);
            else
                DrawRect(new Rect2(8, 3, width, Size.Y - 6), tint with { A = 0.4f * _light });
            var white = new Color(1, 1, 1, _light);
            if (UiArt.Get("row_top") is { } top && UiArt.Get("row_bottom") is { } bottom)
            {
                UiArt.Draw(GetCanvasItem(), top, new Rect2(4, 0, width + 4, top.Size.Y), white);
                UiArt.Draw(GetCanvasItem(), bottom, new Rect2(4, Size.Y - bottom.Size.Y, width + 4, bottom.Size.Y), white);
            }
            if (UiArt.Texture("arrow_right") is { } arrow)
                DrawTextureRect(arrow, new Rect2(6, (Size.Y - 20) / 2, 18, 20), false, white);
        }
    }

    /// <summary>The page names along the top; the chosen page wears the small modal frame.</summary>
    public partial class Tabs : HBoxContainer
    {
        private readonly List<Button> _buttons = new();
        private int _current = -1;

        public event Action<int>? Changed;

        public Tabs(IEnumerable<string> names)
        {
            AddThemeConstantOverride("separation", 14);
            Alignment = AlignmentMode.Center;
            foreach (var name in names)
            {
                int index = _buttons.Count;
                var button = new Button { Text = name.ToUpperInvariant(), FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(130, 0) };
                button.AddThemeFontSizeOverride("font_size", 18);
                button.Pressed += () => Select(index);
                _buttons.Add(button);
                AddChild(button);
            }
        }

        public int Current => _current;

        public void Select(int index)
        {
            index = Math.Clamp(index, 0, _buttons.Count - 1);
            if (index == _current) return;
            _current = index;
            for (int i = 0; i < _buttons.Count; i++) Style(_buttons[i], i == index);
            Changed?.Invoke(index);
        }

        private static void Style(Button button, bool selected)
        {
            var frame = UiArt.Tab(UiArt.Highlight with { A = 0.9f });
            var empty = new StyleBoxEmpty();
            if (frame is not null) { empty.ContentMarginLeft = frame.ContentMarginLeft; empty.ContentMarginRight = frame.ContentMarginRight; empty.ContentMarginTop = frame.ContentMarginTop; empty.ContentMarginBottom = frame.ContentMarginBottom; }
            else empty.SetContentMarginAll(10);
            StyleBox look = selected ? frame ?? BoardStyle.Box(UiArt.Highlight, 6, UiArt.Gold, 1) : empty;
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus" }) button.AddThemeStyleboxOverride(state, look);
            button.AddThemeColorOverride("font_color", selected ? Colors.White : DimText);
            button.AddThemeColorOverride("font_hover_color", selected ? Colors.White : UiArt.Gold);
            button.AddThemeColorOverride("font_pressed_color", Colors.White);
        }
    }
}
