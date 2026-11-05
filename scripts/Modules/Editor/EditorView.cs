using System.Globalization;
using System.Numerics;
using BmSDK.Engine;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// Draws the editor over the world: a box, name and tracer for each marked object (one DrawTris per colour for all
/// the lines), a white bracket on what SELECT AIMED would take, and the selected object's wireframe and card.
/// </summary>
internal static class EditorView
{
    private const float Metre = 100f;
    private const float DetailRange = 6000f;
    private const float NameScale = 0.58f;
    private const float DetailScale = 0.5f;
    private const float MinBox = 6f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly record struct Shown(Thing T, float X0, float Y0, float X1, float Y1, bool Removed);

    private static readonly List<Shown> _shown = new();

    public static void Draw(WorldEditor ed, Canvas canvas)
    {
        WorldView.Begin(canvas);
        if (!WorldView.Ready)
            return;

        Alpha = 1f;
        Thing? sel = ed.Selected;

        _shown.Clear();
        int marked = 0;
        foreach (Thing t in ed.Visible)
        {
            if (t == sel || !ed.Shows(t.Kind) || !t.Alive)
                continue;
            if (++marked > ed.MostShown)
                break;

            Vector3 at = t.Actor.Location;
            if (WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1))
                _shown.Add(new Shown(t, x0, y0, x1, y1, Edits.Find(t)?.Removed ?? false));
        }

        float thick = MathF.Max(1f, S(1.5f));
        if (ed.Boxes || ed.Tracers)
        {
            for (int k = 0; k < Things.KindCount; k++)
            {
                Lines(ed, (ThingKind)k, false, thick);
                Lines(ed, (ThingKind)k, true, thick);
            }
        }

        foreach (Shown s in _shown)
            Label(s, ed.Details);

        if (ed.Picking && ed.Aimed() is { } aimed && aimed != sel)
            Bracket(aimed);

        if (sel != null)
            DrawSelected(ed, sel);

        if (ed.Picking)
            Reticle();

        if (ed.ModeOn && ed.Picking)
            Controls(ed);
    }

    private static readonly string[] Keys =
    {
        "WASD  FLY", "SPACE CTRL  UP DOWN", "SHIFT  FAST", "CLICK  SELECT", "HOLD  GRAB", "RIGHT CLICK  DESELECT",
        "Q E  TURN", "Z X  SIZE", "F  FREEZE", "G  GROUND", "DEL  REMOVE", "R  RESTORE",
    };

    private static readonly string[] HeldKeys = { "W S  PUSH PULL", "Q E  TURN", "LET GO  DROP", "FLICK AS YOU LET GO  THROW" };

    /// <summary>The key guide along the bottom of the screen while editor mode is on.</summary>
    private static void Controls(WorldEditor ed)
    {
        string[] keys = ed.Grabbing ? HeldKeys : Keys;
        const float scale = 0.5f;
        float gap = S(22);
        float w = -gap;
        foreach (string k in keys)
            w += TextW(k, scale) + gap;

        string title = ed.Grabbing ? "HOLDING" : "EDITOR MODE";
        float th = TextH(0.6f), kh = TextH(scale);
        float pad = S(10);
        float pw = MathF.Max(w, TextW(title, 0.6f)) + pad * 2f;
        float ph = th + kh + pad * 2f + S(4);
        float x = (W - pw) * 0.5f;
        float y = H - ph - S(28);
        Panel(x, y, pw, ph, 0.8f);
        Text(x + pad, y + pad, title, Theme.Accent, 0.6f);
        float kx = x + pad;
        float ky = y + pad + th + S(4);
        foreach (string k in keys)
            kx += Text(kx, ky, k, Theme.Mid, scale) + gap;
    }

    private static Rgba ColourOf(Thing t, bool removed)
    {
        Rgba c = Things.Colors[(int)t.Kind];
        return removed ? Rgba.Mix(c, Theme.Black, 0.55f) : c;
    }

    /// <summary>Every box and tracer of one kind and state in a single call.</summary>
    private static void Lines(WorldEditor ed, ThingKind kind, bool removed, float thick)
    {
        Thing? first = null;
        foreach (Shown s in _shown)
        {
            if (s.T.Kind != kind || s.Removed != removed)
                continue;

            first ??= s.T;
            if (ed.Boxes)
                QueueRect(s.X0, s.Y0, s.X1, s.Y1, thick);
            if (ed.Tracers)
                Shapes.QueueLine(W * 0.5f, 0f, (s.X0 + s.X1) * 0.5f, s.Y0, thick * 0.75f);
        }

        if (first != null)
            Shapes.FlushLines(ColourOf(first, removed));
    }

    private static void QueueRect(float x0, float y0, float x1, float y1, float thick)
    {
        Grow(ref x0, ref x1);
        Grow(ref y0, ref y1);
        Shapes.QueueLine(x0, y0, x1, y0, thick);
        Shapes.QueueLine(x1, y0, x1, y1, thick);
        Shapes.QueueLine(x1, y1, x0, y1, thick);
        Shapes.QueueLine(x0, y1, x0, y0, thick);
    }

    /// <summary>Keeps a far-off object's box big enough to see.</summary>
    private static void Grow(ref float a, ref float b)
    {
        float min = S(MinBox);
        if (b - a >= min)
            return;

        float mid = (a + b) * 0.5f;
        a = mid - min * 0.5f;
        b = mid + min * 0.5f;
    }

    // ---- labels

    private static void Label(Shown s, bool details)
    {
        Thing t = s.T;
        Rgba col = ColourOf(t, s.Removed);
        float cx = (s.X0 + s.X1) * 0.5f;
        string name = s.Removed ? t.Name + "  REMOVED" : t.Name;
        float h = TextH(NameScale);
        float y = s.Y0 - h - S(4);
        Tag(cx, y, name, col, NameScale);

        float dist = Vector3.Distance(WorldView.Eye, t.Centre);
        if (!details || dist > DetailRange)
            return;

        string line = Fmt.Metres(dist / Metre);
        if (t.Actor is BmSDK.Engine.Pawn p && p.HealthMax > 0)
        {
            line += "  " + Math.Max(0, p.Health) + " HP";
            HealthBar(s.X0, s.Y0, s.Y1, p.Health, p.HealthMax, col);
        }

        Tag(cx, s.Y1 + S(3), line, Theme.Mid, DetailScale);
    }

    /// <summary>Text centred on x over a dark strip, so it reads against any background.</summary>
    private static void Tag(float cx, float y, string text, Rgba col, float scale)
    {
        float w = TextW(text, scale);
        float h = TextH(scale);
        Rect(cx - w * 0.5f - S(4), y, w + S(8), h, Theme.Black, 0.45f);
        Text(cx - w * 0.5f, y, text, col, scale);
    }

    private static void HealthBar(float x0, float y0, float y1, int health, int max, Rgba col)
    {
        float h = y1 - y0;
        if (h < S(14))
            return;

        float frac = Math.Clamp(health / (float)Math.Max(1, max), 0f, 1f);
        float bx = x0 - S(6);
        Rect(bx, y0, S(3), h, Theme.Black, 0.6f);
        Rect(bx, y1 - h * frac, S(3), h * frac, frac > 0.35f ? col : Theme.Warn);
    }

    // ---- aim and selection

    private static void Bracket(Thing t)
    {
        Vector3 at = t.Actor.Location;
        if (!WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1))
            return;

        float pad = S(6);
        x0 -= pad;
        y0 -= pad;
        x1 += pad;
        y1 += pad;
        float arm = MathF.Min(S(14), MathF.Min(x1 - x0, y1 - y0) * 0.4f);
        float thick = MathF.Max(1f, S(2f));
        Shapes.QueueLine(x0, y0, x0 + arm, y0, thick);
        Shapes.QueueLine(x0, y0, x0, y0 + arm, thick);
        Shapes.QueueLine(x1, y0, x1 - arm, y0, thick);
        Shapes.QueueLine(x1, y0, x1, y0 + arm, thick);
        Shapes.QueueLine(x0, y1, x0 + arm, y1, thick);
        Shapes.QueueLine(x0, y1, x0, y1 - arm, thick);
        Shapes.QueueLine(x1, y1, x1 - arm, y1, thick);
        Shapes.QueueLine(x1, y1, x1, y1 - arm, thick);
        Shapes.FlushLines(Theme.White);
        Tag((x0 + x1) * 0.5f, y1 + S(4), "SELECT  " + t.Name, Theme.Hi, DetailScale);
    }

    private static void DrawSelected(WorldEditor ed, Thing t)
    {
        Vector3 at = t.Actor.Location;
        Span<Vector2> c = stackalloc Vector2[8];
        bool front = WorldView.Corners(at + t.Min, at + t.Max, c);
        bool seen = WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1);
        if (!front || !seen)
        {
            EdgeMarker(t);
            return;
        }

        // The twelve edges of the box: corners whose index differs by one bit (one axis) are joined.
        float thick = MathF.Max(1.5f, S(2.5f));
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                int j = i | bit;
                if (j != i)
                    Shapes.QueueLine(c[i].X, c[i].Y, c[j].X, c[j].Y, thick);
            }
        }

        Shapes.FlushLines(Theme.Accent);
        Card(ed, t, x1 + S(14), y0);
    }

    private static void Card(WorldEditor ed, Thing t, float x, float y)
    {
        Actor a = t.Actor;
        var lines = new List<(string Text, Rgba Col, float Scale)>
        {
            (t.Name, Theme.Hi, 0.68f),
            (t.KindLabel + "  " + a.Class.Name.ToString().ToUpperInvariant(), Theme.Low, 0.48f),
        };

        string where = Fmt.Metres(Vector3.Distance(WorldView.Eye, t.Centre) / Metre) + " AWAY";
        if (a is BmSDK.Engine.Pawn p && p.HealthMax > 0)
            where += "   " + Math.Max(0, p.Health) + " / " + p.HealthMax + " HP";
        lines.Add((where, Theme.Mid, 0.52f));
        lines.Add(("AT " + Fmt.Position(a.Location), Theme.Low, 0.48f));

        string flags = Flags(ed, t);
        if (flags.Length > 0)
            lines.Add((flags, Theme.Signal, 0.5f));

        float pad = S(10);
        float w = 0f, h = pad * 2f;
        foreach (var l in lines)
        {
            w = MathF.Max(w, TextW(l.Text, l.Scale));
            h += TextH(l.Scale) + S(2);
        }

        w += pad * 2f;
        x = Math.Clamp(x, S(8), W - w - S(8));
        y = Math.Clamp(y, S(8), H - h - S(8));
        Panel(x, y, w, h, 0.82f);
        float ly = y + pad;
        foreach (var l in lines)
        {
            Text(x + pad, ly, l.Text, l.Col, l.Scale);
            ly += TextH(l.Scale) + S(2);
        }
    }

    private static string Flags(WorldEditor ed, Thing t)
    {
        var f = new List<string>();
        if (ed.Grabbing)
            f.Add("GRABBED");
        if (Edits.Find(t) is { } e)
        {
            if (e.Frozen)
                f.Add("FROZEN");
            if (e.Removed)
                f.Add("REMOVED");
            if (e.Ghost)
                f.Add("NO COLLISION");
            if (e.Scale != 1f)
                f.Add("SIZE " + e.Scale.ToString("0.0", Inv) + "X");
            if (e.Moved)
                f.Add("MOVED");
        }

        if (t.Fixed)
            f.Add("PART OF THE LEVEL");
        return string.Join("  ", f);
    }

    /// <summary>An arrow at the screen edge toward a selected object that is off screen or behind you.</summary>
    private static void EdgeMarker(Thing t)
    {
        Vector3 v = t.Centre - WorldView.Eye;
        float rx = Vector3.Dot(v, WorldView.Right);
        float uy = Vector3.Dot(v, WorldView.Up);
        if (MathF.Abs(rx) < 0.001f && MathF.Abs(uy) < 0.001f)
            rx = 1f;

        float cx = W * 0.5f, cy = H * 0.5f;
        float margin = S(60);
        float len = MathF.Sqrt(rx * rx + uy * uy);
        float dx = rx / len, dy = -uy / len;
        float sx = dx == 0f ? float.MaxValue : (cx - margin) / MathF.Abs(dx);
        float sy = dy == 0f ? float.MaxValue : (cy - margin) / MathF.Abs(dy);
        float r = MathF.Min(sx, sy);
        float px = cx + dx * r, py = cy + dy * r;

        float size = S(12);
        float nx = -dy, ny = dx;
        Shapes.Triangle(px + dx * size, py + dy * size, px + nx * size * 0.6f, py + ny * size * 0.6f, px - nx * size * 0.6f, py - ny * size * 0.6f, Theme.Accent, 1f);
        string label = t.Name + "  " + Fmt.Metres(v.Length() / Metre);
        Tag(px - dx * S(28), py - dy * S(28) - TextH(DetailScale) * 0.5f, label, Theme.Hi, DetailScale);
    }

    private static void Reticle()
    {
        float cx = W * 0.5f, cy = H * 0.5f;
        float l = S(7), t = MathF.Max(1f, S(2));
        Rect(cx - l, cy - t * 0.5f, l * 2f, t, Theme.White, 0.85f);
        Rect(cx - t * 0.5f, cy - l, t, l * 2f, Theme.White, 0.85f);
    }
}
