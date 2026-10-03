// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Overlay that draws curved arrows between cards (blocker → attacker), or from a card to the mouse while
/// the player is choosing. Redraws every frame while arrows exist so they follow animating cards.
/// </summary>
public partial class ArrowLayer : Control
{
    public readonly record struct Arrow(Control From, Control? To, Color Color);

    private readonly List<Arrow> _arrows = new();
    private readonly List<Arrow> _overlay = new();

    public ArrowLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void SetArrows(IEnumerable<Arrow> arrows)
    {
        _arrows.Clear();
        _arrows.AddRange(arrows);
        QueueRedraw();
    }

    /// <summary>Extra arrows owned by announcements, kept separate so board refreshes don't wipe them.</summary>
    public void SetOverlayArrows(IEnumerable<Arrow> arrows)
    {
        _overlay.Clear();
        _overlay.AddRange(arrows);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_arrows.Count > 0 || _overlay.Count > 0) QueueRedraw();
    }

    public override void _Draw()
    {
        var toLocal = GetGlobalTransform().AffineInverse();
        // Several arrows into the same card get spread-out tips so each one stays visible.
        var all = _arrows.Concat(_overlay).ToList();
        var byTarget = all.Where(a => a.To is not null).GroupBy(a => a.To!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var arrow in all)
        {
            if (!IsInstanceValid(arrow.From) || !arrow.From.IsVisibleInTree()) continue;
            var from = toLocal * Center(arrow.From);
            Vector2 to;
            if (arrow.To is null) to = GetLocalMousePosition();
            else if (IsInstanceValid(arrow.To) && arrow.To.IsVisibleInTree())
            {
                to = toLocal * Center(arrow.To);
                var siblings = byTarget[arrow.To];
                if (siblings.Count > 1)
                {
                    float spread = Mathf.Min(26, arrow.To.Size.X * 0.7f / (siblings.Count - 1));
                    to.X += (siblings.IndexOf(arrow) - (siblings.Count - 1) / 2f) * spread;
                }
            }
            else continue;
            DrawArrow(from, to, arrow.Color, shortenEnd: arrow.To is null ? 0 : 34);
        }
    }

    private static Vector2 Center(Control c) => c.GetGlobalTransform() * (c.Size / 2);

    private void DrawArrow(Vector2 from, Vector2 to, Color color, float shortenEnd)
    {
        var delta = to - from;
        float length = delta.Length();
        if (length < 20) return;
        var dir = delta / length;
        to -= dir * shortenEnd;
        from += dir * 20;

        // Quadratic bezier bowed sideways so arrows between overlapping rows stay readable.
        var normal = new Vector2(-dir.Y, dir.X);
        var control = (from + to) / 2 + normal * Mathf.Min(60, length * 0.18f);
        const int segments = 48;
        const float headLength = 18;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            points[i] = (1 - t) * (1 - t) * from + 2 * (1 - t) * t * control + t * t * to;
        }

        // The shaft runs along the curve until it reaches the arrowhead's base, then ends exactly there,
        // so the head stays attached however long the arrow gets.
        int k = 1;
        while (k < segments && points[k].DistanceTo(to) > headLength) k++;
        var inside = points[k];
        var outside = points[k - 1];
        float da = outside.DistanceTo(to), db = inside.DistanceTo(to);
        float f = da - db > 0.001f ? (da - headLength) / (da - db) : 1;
        var headBase = outside.Lerp(inside, Mathf.Clamp(f, 0, 1));
        var shaft = points[..k].Append(headBase).ToArray();

        var tipDir = (to - headBase).Normalized();
        var tipNormal = new Vector2(-tipDir.Y, tipDir.X);
        var head = new[] { to + tipDir * 4, headBase + tipNormal * 11 - tipDir * 2, headBase - tipNormal * 11 - tipDir * 2 };

        var outline = new Color(0, 0, 0, 0.55f);
        DrawPolyline(shaft, outline, 9, true);
        DrawColoredPolygon(head.Select(p => p + (p - to).Normalized() * 2).ToArray(), outline);
        DrawPolyline(shaft, color, 5, true);
        DrawColoredPolygon(head, color);
    }
}
