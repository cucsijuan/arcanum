// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// A tournament's broadcast overlay on the board: the round's clock in a tab on the line between the players (the
/// event's name above it) and a plate for each player with their record in the event and the games won in this match.
/// When the round's time runs out it calls time on the game (the end-of-match procedure) and counts the additional turns.
/// </summary>
public partial class BroadcastOverlay : Control
{
    private const string FontPath = "res://fonts/bebas-neue/BebasNeue-Regular.ttf";

    /// <summary>The board's height the sizes below were drawn for.</summary>
    private const float Reference = 1368f;

    private static readonly Color Red = new("f64b4e"), Dark = new(0.07f, 0.063f, 0.07f, 0.92f), White = new("f0eaec"), Dim = new("c8c8c8"), Off = new("3c3c3c");

    private RoundBroadcast? _broadcast;
    private Func<int>? _turn;
    private Action? _callTime;
    private Func<int?>? _timeCalledAt;
    private Font? _font;
    private float _line, _right;

    /// <summary>The plates (top: the far seat, bottom: this player) and the clock tab, for the board to lower under a lifted card.</summary>
    public IReadOnlyList<Control> Panels => _panels;

    private readonly Control[] _panels = { new(), new(), new() };

    public BroadcastOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        foreach (var panel in _panels)
        {
            panel.MouseFilter = MouseFilterEnum.Ignore;
            AddChild(panel);
        }
        _panels[0].Draw += () => DrawPlate(_panels[0], 1);
        _panels[1].Draw += () => DrawPlate(_panels[1], 0);
        _panels[2].Draw += () => DrawClock(_panels[2]);
        Resized += Layout;
    }

    public override void _Ready() => _font = ResourceLoader.Exists(FontPath) ? GD.Load<Font>(FontPath) : ThemeDB.FallbackFont;

    /// <param name="turn">The game's turn number now.</param>
    /// <param name="timeCalledAt">The turn time was called at, or null.</param>
    /// <param name="callTime">Calls time on the game.</param>
    public void Show(RoundBroadcast broadcast, Func<int> turn, Func<int?> timeCalledAt, Action callTime)
    {
        _broadcast = broadcast;
        _turn = turn;
        _timeCalledAt = timeCalledAt;
        _callTime = callTime;
        Visible = true;
        Layout();
    }

    /// <summary>Where the line between the players is (from the top) and where the clock's tab ends on the right.</summary>
    public void Place(float line, float right)
    {
        _line = line;
        _right = right;
        Layout();
    }

    private float K => Size.Y / Reference;

    private void Layout()
    {
        float k = K;
        var plate = new Vector2(470, 82) * k;
        _panels[0].Position = new Vector2(150, 40) * k;
        _panels[1].Position = new Vector2(150 * k, Size.Y - 218 * k);
        _panels[0].Size = _panels[1].Size = plate;
        var tab = new Vector2(300, 170) * k;
        _panels[2].Size = tab;
        _panels[2].Position = new Vector2((_right > 0 ? _right : Size.X * 0.93f) - tab.X, (_line > 0 ? _line : Size.Y / 2) - tab.Y + 64 * k);
        foreach (var panel in _panels) panel.QueueRedraw();
    }

    private double _sinceTick;

    public override void _Process(double delta)
    {
        if (_broadcast is not { } b || !Visible) return;
        _sinceTick += delta;
        if (_sinceTick < 0.25) return;
        _sinceTick = 0;
        if (b.Deadline is { } deadline && DateTime.UtcNow >= deadline && _timeCalledAt?.Invoke() is null) _callTime?.Invoke();
        _panels[2].QueueRedraw();
    }

    private void Text(Control on, Vector2 at, string text, int size, Color color, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1)
    {
        var font = _font ?? ThemeDB.FallbackFont;
        // Centered on the height given: the font's ascent above the middle.
        var baseline = at + new Vector2(0, (font.GetAscent(size) - font.GetDescent(size)) / 2);
        on.DrawStringOutline(font, baseline, text, align, width, size, Math.Max(2, size / 12), Colors.Black);
        on.DrawString(font, baseline, text, align, width, size, color);
    }

    /// <summary>A player's plate: a red stripe, the name, the record before this match, a dot for each game won of the ones it takes.</summary>
    private void DrawPlate(Control on, int seat)
    {
        if (_broadcast?.Plates.ElementAtOrDefault(seat) is not { } p) return;
        float k = K, w = on.Size.X, h = on.Size.Y;
        on.DrawRect(new Rect2(0, 0, w, h), Dark);
        on.DrawRect(new Rect2(0, 0, 10 * k, h), Red);
        on.DrawRect(new Rect2(0, h, w, 4 * k), Red);
        Text(on, new Vector2(28 * k, h / 2), p.Name.ToUpperInvariant(), (int)(46 * k), White);
        Text(on, new Vector2(0, h / 2), p.Record, (int)(40 * k), Dim, HorizontalAlignment.Right, w - 120 * k);
        int needed = _broadcast.BestOf / 2 + 1;
        for (int i = 0; i < needed; i++)
        {
            var center = new Vector2(w - 80 * k + i * 34 * k, h / 2);
            on.DrawCircle(center, 12 * k, White);
            on.DrawCircle(center, 10 * k, i < p.Games ? Red : Off);
        }
    }

    /// <summary>The clock's tab, pointing at the table's middle: the round and the time left; once time is called, the additional turn.</summary>
    private void DrawClock(Control on)
    {
        if (_broadcast is not { } b) return;
        float k = K, w = on.Size.X, mid = on.Size.Y - 64 * k, hh = 58 * k, x0 = 0, tip = 40 * k;
        Text(on, new Vector2(0, mid - hh - 22 * k), b.Event.ToUpperInvariant(), (int)(30 * k), White, HorizontalAlignment.Right, w - 8 * k);
        on.DrawColoredPolygon(new[] { new Vector2(x0 - 14 * k, mid), new Vector2(x0 + tip - 6 * k, mid - hh - 6 * k), new Vector2(w, mid - hh - 6 * k), new Vector2(w, mid + hh + 6 * k), new Vector2(x0 + tip - 6 * k, mid + hh + 6 * k) }, Red);
        on.DrawColoredPolygon(new[] { new Vector2(x0, mid), new Vector2(x0 + tip, mid - hh), new Vector2(w, mid - hh), new Vector2(w, mid + hh), new Vector2(x0 + tip, mid + hh) }, Dark);
        float split = 150 * k;
        on.DrawLine(new Vector2(split, mid - hh + 12 * k), new Vector2(split, mid + hh - 12 * k), new Color(0.35f, 0.35f, 0.35f), 2 * k);
        Text(on, new Vector2(tip, mid), b.Round.ToUpperInvariant(), (int)(30 * k), White, HorizontalAlignment.Center, split - tip);
        string clock;
        if (_timeCalledAt?.Invoke() is { } called)
        {
            // Turn 0 is the one in progress when time was called, then the additional turns.
            int extra = Math.Clamp((_turn?.Invoke() ?? called) - called, 0, Arcanum.Engine.Game.AdditionalTurns);
            clock = $"T{extra}/{Arcanum.Engine.Game.AdditionalTurns}";
        }
        else if (b.Deadline is { } deadline)
        {
            var left = deadline - DateTime.UtcNow;
            clock = left > TimeSpan.Zero ? $"{(int)left.TotalMinutes:00}:{left.Seconds:00}" : "TIME";
        }
        else clock = "UNTIMED";
        Text(on, new Vector2(split, mid), clock, (int)(64 * k), clock.StartsWith('T') && clock != "TIME" ? Red : White, HorizontalAlignment.Center, w - split);
    }
}
