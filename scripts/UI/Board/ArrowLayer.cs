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

    // The beam's profile from its center line outward: a flat band of the color made luminous, a quick step into a
    // deep, saturated edge, then a glow that fades out over a width that flickers along the beam.
    private const float BandHalf = 4.5f, EdgeHalf = 6.5f, GlowHalf = 18, HazeHalf = 30;
    private const float HeadLength = 34, HeadHalfWidth = 17, NotchDepth = 0.3f;

    /// <summary>
    /// An energy beam: thin and faint where it leaves its source and full width at the arrowhead, its glow
    /// flickering like a current, with a brighter pulse running along it toward the target.
    /// </summary>
    private void DrawArrow(Vector2 from, Vector2 to, Color color, float shortenEnd)
    {
        var delta = to - from;
        float length = delta.Length();
        if (length < 20) return;
        var dir = delta / length;
        to -= dir * shortenEnd;
        from += dir * 20;

        // Quadratic bezier bowed sideways into a high arc, which also keeps arrows between overlapping rows readable.
        var normal = new Vector2(-dir.Y, dir.X);
        var control = (from + to) / 2 + normal * Mathf.Min(150, length * 0.28f);
        const int segments = 48;
        var points = new Vector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            points[i] = (1 - t) * (1 - t) * from + 2 * (1 - t) * t * control + t * t * to;
        }

        // The shaft ends where the curve comes within a distance of the tip a little past the notch at the back of
        // the head, inside it, so the two join seamlessly however long the arrow gets.
        (int Index, Vector2 Point) CutAt(float distance)
        {
            int k = 1;
            while (k < segments && points[k].DistanceTo(to) > distance) k++;
            var inside = points[k];
            var outside = points[k - 1];
            float da = outside.DistanceTo(to), db = inside.DistanceTo(to);
            float f = da - db > 0.001f ? (da - distance) / (da - db) : 1;
            return (k, outside.Lerp(inside, Mathf.Clamp(f, 0, 1)));
        }
        var (end, shaftEnd) = CutAt(HeadLength * (1 - NotchDepth) - 4);
        var shaft = points[..end].Append(shaftEnd).ToArray();

        // The head points from where the shaft ends to the tip, so the shaft always enters it dead center.
        var tipDir = (to - shaftEnd).Normalized();
        var headBase = to - tipDir * HeadLength;
        var tipNormal = new Vector2(-tipDir.Y, tipDir.X);
        // A barbed head: tip, two swept-back barbs and a notch the shaft runs into.
        var head = new[]
        {
            to + tipDir * 4,
            headBase + tipNormal * HeadHalfWidth - tipDir * 3,
            headBase + tipDir * (HeadLength * NotchDepth),
            headBase - tipNormal * HeadHalfWidth - tipDir * 3,
        };
        var headCenter = headBase + tipDir * (HeadLength * 0.55f);

        _vertices.Clear();
        _colors.Clear();
        _indices.Clear();
        float time = (float)(Time.GetTicksMsec() / 1000.0);
        // The pulse: a bright spot sliding from source to target about once a second.
        float phase = time * 0.9f % 1f * 1.3f - 0.15f;
        int n = shaft.Length;
        var ts = new float[n];
        var arc = new float[n];
        var normals = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            ts[i] = i / (float)segments;
            if (i > 0) arc[i] = arc[i - 1] + shaft[i].DistanceTo(shaft[i - 1]);
            var tangent = (shaft[Math.Min(i + 1, n - 1)] - shaft[Math.Max(i - 1, 0)]).Normalized();
            normals[i] = new Vector2(-tangent.Y, tangent.X);
        }

        var band = Color.FromHsv(TowardLuminous(color.H), color.S * 0.75f, 1, 0.9f);
        var edge = Color.FromHsv(color.H, Mathf.Min(1, color.S * 1.05f), color.V * 0.75f, 0.75f);
        var haze = Color.FromHsv(color.H, color.S, color.V * 0.8f, 0.22f);
        var white = new Color(1, 1, 1);

        float Taper(int i) => 0.35f + 0.65f * ts[i];
        float Fade(int i) => Mathf.SmoothStep(0, 0.15f, ts[i]);
        Color Alpha(Color c, int i, float scale = 1) => c with { A = c.A * Fade(i) * scale };
        // Glows first, under everything: the head's own flickering glow and haze, then the shaft's, each side of the
        // shaft on its own so its two edges flicker independently.
        float headFlicker = Flicker(arc[n - 1], time, 7);
        AddSoftFan(headCenter, Circle(headCenter, HeadLength * (1.1f + 0.25f * headFlicker)), haze with { A = haze.A * 1.5f });
        AddSoftRing(head, head.Select(p => p + (p - headCenter).Normalized() * (12 + 10 * headFlicker)).ToArray(), edge with { A = 0.9f });
        foreach (int sign in new[] { 1, -1 })
        {
            AddStrip(shaft, normals, sign, i => 0, i => HazeHalf * Taper(i) * (0.8f + 0.4f * Flicker(arc[i], time, sign * 3.1f)),
                i => Alpha(haze, i), i => haze with { A = 0 });
            AddStrip(shaft, normals, sign, i => EdgeHalf * Taper(i),
                i => (EdgeHalf + (GlowHalf - EdgeHalf) * (0.45f + 0.8f * Flicker(arc[i], time, sign)) * (1 + 0.4f * Pulse(ts[i], phase))) * Taper(i),
                i => Alpha(edge, i), i => edge with { A = 0 });
        }

        // The head's deep edge, the shaft's band running into its notch, then the head itself over the band's end,
        // so the band disappears into it with no seam and no overlap showing.
        Head(head.Select(p => p + (p - headCenter).Normalized() * 2.5f).ToArray(), edge with { A = 1 });
        foreach (int sign in new[] { 1, -1 })
        {
            AddStrip(shaft, normals, sign, i => BandHalf * Taper(i), i => EdgeHalf * Taper(i),
                i => Alpha(band, i), i => Alpha(edge, i));
            AddStrip(shaft, normals, sign, i => 0, i => BandHalf * Taper(i),
                i => Alpha(band.Lerp(white, 0.35f * Pulse(ts[i], phase)), i), i => Alpha(band, i));
        }
        Head(head, band with { A = 1 });

        // A small flare where the beam leaves its source.
        AddSoftFan(from, Circle(from, 10), edge with { A = 0.6f });
        AddSoftFan(from, Circle(from, 3.5f), band);

        // Everything above, in that order, as one batch of triangles.
        RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), _indices.ToArray(), _vertices.ToArray(), _colors.ToArray());
    }

    // The arrow being drawn, gathered as triangles (in drawing order) and sent in one call instead of hundreds.
    private readonly List<Vector2> _vertices = new();
    private readonly List<Color> _colors = new();
    private readonly List<int> _indices = new();

    private void Triangle(Vector2 a, Vector2 b, Vector2 c, Color ca, Color cb, Color cc)
    {
        int first = _vertices.Count;
        _vertices.Add(a); _vertices.Add(b); _vertices.Add(c);
        _colors.Add(ca); _colors.Add(cb); _colors.Add(cc);
        _indices.Add(first); _indices.Add(first + 1); _indices.Add(first + 2);
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb, Color cc, Color cd)
    {
        int first = _vertices.Count;
        _vertices.Add(a); _vertices.Add(b); _vertices.Add(c); _vertices.Add(d);
        _colors.Add(ca); _colors.Add(cb); _colors.Add(cc); _colors.Add(cd);
        _indices.Add(first); _indices.Add(first + 1); _indices.Add(first + 2);
        _indices.Add(first); _indices.Add(first + 2); _indices.Add(first + 3);
    }

    /// <summary>The barbed head (tip, barb, notch, barb) in one color: two triangles sharing the tip and the notch.</summary>
    private void Head(Vector2[] head, Color color)
    {
        Triangle(head[0], head[1], head[2], color, color, color);
        Triangle(head[0], head[2], head[3], color, color, color);
    }

    /// <summary>
    /// The hue a little closer to the nearest of the brightest hues, yellow and cyan, the way light at the heart of
    /// a glow looks: blue toward cyan, red toward orange.
    /// </summary>
    private static float TowardLuminous(float hue)
    {
        const float yellow = 1 / 6f, cyan = 0.5f, shift = 0.07f;
        float Distance(float a, float b) { float d = Mathf.Abs(a - b) % 1; return Mathf.Min(d, 1 - d); }
        float target = Distance(hue, yellow) < Distance(hue, cyan) ? yellow : cyan;
        float delta = target - hue;
        if (delta > 0.5f) delta -= 1;
        else if (delta < -0.5f) delta += 1;
        return ((hue + Mathf.Clamp(delta, -shift, shift)) % 1 + 1) % 1;
    }

    /// <summary>0..1 brightness bump around the pulse position.</summary>
    private static float Pulse(float t, float phase)
    {
        float d = (t - phase) / 0.07f;
        return Mathf.Exp(-d * d);
    }

    /// <summary>0..1 noise along the beam (by distance along it) that drifts over time, for an unsteady, electric glow.</summary>
    private static float Flicker(float s, float time, float seed) =>
        0.5f + 0.5f * (0.5f * Mathf.Sin(s * 0.07f + time * 6.0f + seed)
                     + 0.3f * Mathf.Sin(s * 0.023f - time * 3.7f + seed * 2.3f)
                     + 0.2f * Mathf.Sin(s * 0.21f + time * 13.0f + seed * 5.1f));

    /// <summary>
    /// One side (<paramref name="sign"/>) of a strip along <paramref name="points"/>, between two offsets from the
    /// center line, with a color at each. Neighbouring quads share their edges, so nothing is drawn twice.
    /// </summary>
    private void AddStrip(Vector2[] points, Vector2[] normals, int sign, Func<int, float> inner, Func<int, float> outer,
        Func<int, Color> innerColor, Func<int, Color> outerColor)
    {
        int n = points.Length;
        var a = new Vector2[n];
        var b = new Vector2[n];
        var ca = new Color[n];
        var cb = new Color[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = points[i] + normals[i] * (sign * inner(i));
            b[i] = points[i] + normals[i] * (sign * outer(i));
            ca[i] = innerColor(i);
            cb[i] = outerColor(i);
        }
        for (int i = 0; i < n - 1; i++)
            Quad(a[i], a[i + 1], b[i + 1], b[i], ca[i], ca[i + 1], cb[i + 1], cb[i]);
    }

    /// <summary>A glow: triangles fanning out from <paramref name="center"/>, full color there and transparent at the rim.</summary>
    private void AddSoftFan(Vector2 center, Vector2[] rim, Color color)
    {
        var edge = color with { A = 0 };
        for (int i = 0; i < rim.Length; i++)
            Triangle(center, rim[i], rim[(i + 1) % rim.Length], color, edge, edge);
    }

    /// <summary>A glow around a shape: full color on its outline, fading out to <paramref name="outer"/>.</summary>
    private void AddSoftRing(Vector2[] inner, Vector2[] outer, Color color)
    {
        var edge = color with { A = 0 };
        for (int i = 0; i < inner.Length; i++)
        {
            int j = (i + 1) % inner.Length;
            Quad(inner[i], inner[j], outer[j], outer[i], color, color, edge, edge);
        }
    }

    private static Vector2[] Circle(Vector2 center, float radius) =>
        Enumerable.Range(0, 16).Select(i => center + Vector2.FromAngle(i * Mathf.Tau / 16) * radius).ToArray();
}
