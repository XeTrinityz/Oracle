using System.Numerics;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// ORACLE LENS: the one look every marker drawn over the world shares (the editor's ESP, THREAT ESP, the bodyguard
/// markers), built from the menu's own parts so the two read as one display: dark glass plates with a hairline of
/// colour along the top, corner brackets, segmented meters and titles that decode as they appear.
///
/// How it stays readable:
/// - Everything has a dark rim or sits on glass, so it reads on bright sky and in black alleys alike.
/// - Colour means what a thing is; grey means it's asleep, parked or down. The menu colour belongs to Oracle itself:
///   what you're aiming at, what you've selected.
/// - Detail follows distance. Close up a marker carries its name, distance and health; further off just a name; far
///   away only a marker. LABELS picks how far names reach.
/// - Whatever is nearest the middle of the screen gets the full card, so aiming at something is how you read it.
/// - Motion means something: markers settle in when they first appear, live threats turn, low health blinks.
///
/// Lines and shapes go to the game in batches (two DrawTris calls per colour however many there are), since every
/// Canvas call is a script call. DrawTris draws solid, so anything translucent is a plain tile (Gfx.Rect).
/// </summary>
internal static class Lens
{
    public enum LabelMode { Off, Near, All }

    /// <summary>LABELS: name plates on markers. OFF (the default) shows none, NEAR shows them close by, ALL on every marker.</summary>
    public static LabelMode Labels = LabelMode.Off;

    /// <summary>AIM CARD: whatever is nearest the middle of the screen gets a lock and a card. Off, nothing takes focus.</summary>
    public static bool Cards = true;

    /// <summary>MARKER SIZE: scales every marker, plate and card.</summary>
    public static float Size = 1f;

    /// <summary>The dark edge under every line and shape: the menu's glass, solid.</summary>
    public static readonly Rgba Rim = new(5, 9, 13);

    /// <summary>Asleep, parked, knocked out or removed.</summary>
    public static readonly Rgba Dormant = new(124, 136, 138);

    public const float NameScale = 0.54f;
    public const float InfoScale = 0.46f;

    private const float FocusReach = 90f;
    private const double ForgetAfter = 1.0;
    private const float SettleSeconds = 0.22f;
    private const float LowHealth = 0.3f;

    /// <summary>A length in 1080p pixels, scaled to the screen and MARKER SIZE.</summary>
    public static float L(float v) => S(v) * Size;

    private static float Sc(float scale) => scale * Size;

    private static bool Effects => MenuView.Effects;

    // ---- per-object memory: when it appeared, and its health a moment ago -----------------------------------

    private sealed class Track
    {
        public double First;
        public double Last;
        public float Frac = -1f;
        public float Lag = -1f;
        public double Hold;
    }

    private static readonly Dictionary<nint, Track> _tracks = new();
    private static readonly List<nint> _stale = new();
    private static double _pruneAt;

    private static Track Get(nint ptr)
    {
        double now = Time;
        if (!_tracks.TryGetValue(ptr, out Track? t) || now - t.Last > ForgetAfter)
            _tracks[ptr] = t = new Track { First = now };
        t.Last = now;
        return t;
    }

    /// <summary>Seconds since this object's marker appeared. It starts over once the marker has been gone a second.</summary>
    public static float Age(nint ptr) => (float)(Time - Get(ptr).First);

    /// <summary>0 to 1 as a new marker settles in; 1 straight away with MENU EFFECTS off.</summary>
    public static float Settle(float age) => Effects ? Ease.Out(age / SettleSeconds) : 1f;

    /// <summary>
    /// The health a moment ago, for the pale trail a hit leaves on a meter: it holds briefly after the hit, then
    /// drains down to the real value. Healing shows at once.
    /// </summary>
    public static float Trail(nint ptr, float frac)
    {
        Track t = Get(ptr);
        if (t.Lag < 0f || frac >= t.Lag)
        {
            t.Lag = t.Frac = frac;
            return frac;
        }

        if (frac < t.Frac - 0.0001f)
            t.Hold = Time + 0.45;
        t.Frac = frac;
        if (Time > t.Hold)
            t.Lag = MathF.Max(frac, t.Lag - Dt * 0.9f);
        return t.Lag;
    }

    // ---- focus: what's nearest the middle of the screen ------------------------------------------------------

    private static nint _offer;
    private static float _offerScore = float.MaxValue;
    private static nint _focus;
    private static double _focusSince;
    private static int _frame = -1;

    /// <summary>Called once a frame by WorldView.Begin: last frame's offers decide this frame's focus.</summary>
    public static void NewFrame()
    {
        if (_frame == Frame)
            return;
        _frame = Frame;

        if (_offer != _focus)
        {
            _focus = _offer;
            _focusSince = Time;
        }

        _offer = 0;
        _offerScore = float.MaxValue;

        if (Time < _pruneAt)
            return;
        _pruneAt = Time + 2.0;
        _stale.Clear();
        foreach ((nint ptr, Track t) in _tracks)
        {
            if (Time - t.Last > ForgetAfter * 2.0)
                _stale.Add(ptr);
        }

        foreach (nint ptr in _stale)
            _tracks.Remove(ptr);
    }

    /// <summary>
    /// Puts a marker forward for focus by its screen box. Whatever box the middle of the screen is in, or nearest,
    /// wins; inside several, the smallest (a guard standing in front of a tank). The current focus is a little
    /// stickier, so it doesn't flick between two neighbours.
    /// </summary>
    public static void Offer(nint ptr, float x0, float y0, float x1, float y1)
    {
        if (ptr == 0 || !Aiming)
            return;

        float cx = WorldView.Width * 0.5f, cy = WorldView.Height * 0.5f;
        float dx = MathF.Max(0f, MathF.Max(x0 - cx, cx - x1));
        float dy = MathF.Max(0f, MathF.Max(y0 - cy, cy - y1));
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d > L(FocusReach))
            return;

        float score = d + MathF.Sqrt(MathF.Max(1f, (x1 - x0) * (y1 - y0))) * 0.15f - (ptr == _focus ? L(12) : 0f);
        if (score < _offerScore)
        {
            _offerScore = score;
            _offer = ptr;
        }
    }

    /// <summary>Offers a marker that is just a point on screen.</summary>
    public static void Offer(nint ptr, Vector2 at, float radius) =>
        Offer(ptr, at.X - radius, at.Y - radius, at.X + radius, at.Y + radius);

    /// <summary>
    /// Focus stands down with AIM CARD off, under the open menu or radial, where its card would sit behind them, and while the editor is
    /// picking, since its aim bracket does the same job.
    /// </summary>
    private static bool Aiming => Cards && !Menu.IsOpen && !Radial.IsLive && !WorldEditor.I.Picking;

    public static bool IsFocus(nint ptr) => ptr != 0 && ptr == _focus;

    /// <summary>Seconds since the focus moved to what it's on now.</summary>
    public static float FocusAge => (float)(Time - _focusSince);

    // ---- detail by distance ---------------------------------------------------------------------------------

    public enum Tier { Mark, Name, Full, Card }

    /// <summary>
    /// How much a marker shows. <paramref name="near"/> and <paramref name="far"/> are world distances: within near it
    /// shows everything, within far its name, beyond that only the marker. LABELS moves both.
    /// </summary>
    public static Tier TierOf(nint ptr, float dist, float near, float far)
    {
        if (IsFocus(ptr))
            return Tier.Card;

        return Labels switch
        {
            LabelMode.Off => Tier.Mark,
            LabelMode.All => dist <= near ? Tier.Full : Tier.Name,
            _ => dist <= near ? Tier.Full : dist <= far ? Tier.Name : Tier.Mark,
        };
    }

    // ---- batched lines ----------------------------------------------------------------------------------------

    private readonly record struct Seg(float X0, float Y0, float X1, float Y1, float T);

    private static readonly List<Seg> _segs = new();

    /// <summary>Queues a bar for <see cref="Flush"/>. Nothing else may draw with Shapes until then.</summary>
    public static void Line(float x0, float y0, float x1, float y1, float thick) => _segs.Add(new Seg(x0, y0, x1, y1, thick));

    /// <summary>Queues a diamond with its top and bottom points <paramref name="r"/> above and below the centre.</summary>
    public static void Diamond(float x, float y, float r)
    {
        // A bar on the diagonal as thick as it is long is a square turned 45 degrees.
        float a = r * 0.5f;
        _segs.Add(new Seg(x - a, y - a, x + a, y + a, r * 1.41421f));
    }

    /// <summary>Queues corner brackets around a box. Arms are capped to a share of each side, so small boxes stay boxes.</summary>
    public static void Brackets(float x0, float y0, float x1, float y1, float arm, float thick)
    {
        float ax = MathF.Min(arm, (x1 - x0) * 0.32f);
        float ay = MathF.Min(arm, (y1 - y0) * 0.32f);
        float h = thick * 0.5f;
        Line(x0 - h, y0, x0 + ax, y0, thick);
        Line(x0, y0 - h, x0, y0 + ay, thick);
        Line(x1 + h, y0, x1 - ax, y0, thick);
        Line(x1, y0 - h, x1, y0 + ay, thick);
        Line(x0 - h, y1, x0 + ax, y1, thick);
        Line(x0, y1 + h, x0, y1 - ay, thick);
        Line(x1 + h, y1, x1 - ax, y1, thick);
        Line(x1, y1 + h, x1, y1 - ay, thick);
    }

    /// <summary>
    /// Draws everything queued in one colour, over a dark rim of the same shapes a little larger: two DrawTris calls
    /// however much was queued.
    /// </summary>
    public static void Flush(Rgba col, bool rim = true)
    {
        if (_segs.Count == 0)
            return;

        if (rim)
        {
            float r = MathF.Max(1f, L(1.25f));
            foreach (Seg s in _segs)
            {
                float dx = s.X1 - s.X0, dy = s.Y1 - s.Y0;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.001f)
                    continue;
                float ex = dx / len * r, ey = dy / len * r;
                Shapes.QueueLine(s.X0 - ex, s.Y0 - ey, s.X1 + ex, s.Y1 + ey, s.T + r * 2f);
            }

            Shapes.FlushLines(Rim);
        }

        foreach (Seg s in _segs)
            Shapes.QueueLine(s.X0, s.Y0, s.X1, s.Y1, s.T);
        Shapes.FlushLines(col);
        _segs.Clear();
    }

    /// <summary>Drops anything queued but not drawn, so one HUD's leftovers never land in another's colour.</summary>
    public static void Discard() => _segs.Clear();

    // ---- pieces ---------------------------------------------------------------------------------------------

    /// <summary>A faint wash of colour over a marked object's box: the menu's glass, tinted.</summary>
    public static void Wash(float x0, float y0, float x1, float y1, Rgba col, float alpha = 0.07f) =>
        Rect(x0, y0, x1 - x0, y1 - y0, col, alpha);

    /// <summary>
    /// A name plate centred on <paramref name="cx"/>, its bottom edge at <paramref name="bottom"/>: glass with a
    /// hairline of the marker's colour along the top, like a menu panel, the name, and an optional readout on the
    /// right. Returns its left edge and width, for a meter to hang under it.
    /// </summary>
    public static (float X, float W, float Top) Plate(float cx, float bottom, string name, string info, Rgba col, float settle = 1f)
    {
        float ns = Sc(NameScale), si = Sc(InfoScale);
        float pad = L(5);
        float nw = TextW(name, ns);
        float iw = info.Length > 0 ? TextW(info, si) + L(9) : 0f;
        float h = TextH(ns) + L(3);
        float w = nw + iw + pad * 2f;
        float x = MathF.Round(cx - w * 0.5f);
        float y = MathF.Round(bottom - h - L(3) * (1f - settle));

        float a = Alpha;
        Alpha = a * settle;
        Rect(x, y, w, h, Theme.Panel, 0.84f);
        Rect(x, y, w, MathF.Max(1f, S(1)), col, 0.9f);
        TextMid(x + pad, y + h * 0.5f + S(0.5f), name, Rgba.Mix(col, Theme.Hi, 0.55f), ns);
        if (info.Length > 0)
            TextMid(x + pad + nw + L(9), y + h * 0.5f + S(0.5f), info, Theme.Mid, si);
        Alpha = a;
        return (x, w, y);
    }

    /// <summary>Small text centred under a point on a strip of glass, for distances beside a marker.</summary>
    public static void Caption(float cx, float top, string text, Rgba col)
    {
        float s = Sc(InfoScale);
        float w = TextW(text, s) + L(6);
        float h = TextH(s) + L(1);
        Rect(cx - w * 0.5f, top, w, h, Theme.Panel, 0.7f);
        Text(cx - w * 0.5f + L(3), top + L(0.5f), text, col, s);
    }

    private static bool Blink => Effects && Time * 3.0 % 1.0 < 0.5;

    /// <summary>
    /// A segmented health meter like the menu's sliders: the missing part tinted, a pale trail where a hit just
    /// landed, and red that blinks once it's low.
    /// </summary>
    public static void Meter(float x, float y, float w, float h, float frac, float trail, Rgba col, int segments)
    {
        frac = float.IsFinite(frac) ? Math.Clamp(frac, 0f, 1f) : 0f;
        trail = Math.Clamp(MathF.Max(trail, frac), 0f, 1f);
        bool low = frac < LowHealth && frac > 0f;
        Rgba fill = low ? Theme.Warn : col;
        float e = MathF.Max(1f, S(1));

        Rect(x - e, y - e, w + e * 2f, h + e * 2f, Rim, 0.85f);
        Rect(x, y, w, h, col, 0.16f);
        if (trail > frac)
            Rect(x + w * frac, y, w * (trail - frac), h, Theme.Hi, 0.8f);
        if (frac > 0f)
            Rect(x, y, w * frac, h, fill, low && Blink ? 0.55f : 1f);
        for (int i = 1; i < segments; i++)
            Rect(MathF.Round(x + w * i / segments), y, e, h, Rim, 0.9f);
    }

    /// <summary>A downward chevron, its tip at <paramref name="tipY"/>, for characters: rimmed, notched, solid.</summary>
    public static void Chevron(float x, float tipY, float size, Rgba col)
    {
        float w = size, h = size * 1.1f, rim = MathF.Max(1f, L(1.5f));
        Shapes.Triangle(x - w - rim * 1.6f, tipY - h - rim, x + w + rim * 1.6f, tipY - h - rim, x, tipY + rim * 1.8f, Rim, 1f);
        Shapes.Triangle(x - w, tipY - h, x + w, tipY - h, x, tipY, col, 1f);
        float notch = h * 0.5f;
        Shapes.Triangle(x - w * 0.45f, tipY - h - S(0.5f), x + w * 0.45f, tipY - h - S(0.5f), x, tipY - h + notch, Rim, 1f);
    }

    /// <summary>
    /// A round threat marker: a dark face with a code in the middle, the ring itself as a health gauge running
    /// clockwise from the top, and, while it's live, four ticks turning around it.
    /// </summary>
    public static void Beacon(float x, float y, float r, Rgba col, string code, float health, bool live)
    {
        float band = MathF.Max(2f, L(2.5f));
        Shapes.Disc(x, y, r + MathF.Max(1f, L(1.5f)), Rim, 1f);
        Shapes.Disc(x, y, r - band, Rgba.Mix(Rim, col, 0.16f), 1f);

        float h = health < 0f ? 1f : Math.Clamp(health, 0f, 1f);
        Rgba ring = health >= 0f && h < LowHealth ? Theme.Warn : col;
        if (h > 0f)
            Shapes.Arc(x, y, r - band, r, -90f, -90f + 360f * h, ring, 1f);
        if (h < 1f)
            Shapes.Arc(x, y, r - band, r, -90f + 360f * h, 270f, Rgba.Mix(col, Rim, 0.7f), 1f);

        if (live)
        {
            float spin = Effects ? (float)(Time * 40.0 % 90.0) : 0f;
            Shapes.Ticks(x, y, r + L(2.5f), r + L(6f), 4, spin - 45f, MathF.Max(1.5f, L(2f)), col, 1f);
        }

        if (code.Length == 0 || r < L(9f))
            return;

        float s = Sc(Math.Clamp(r / L(13f), 0.6f, 1.3f) * (code.Length > 2 ? 0.4f : 0.46f));
        TextCentre(x, y - TextH(s) * 0.5f + S(0.5f), code, Rgba.Mix(col, Theme.Hi, 0.5f), s);
    }

    /// <summary>
    /// The lock on what you're aiming at: brackets in the menu colour that close in as it takes focus, with a tick
    /// at the middle of each side pointing in.
    /// </summary>
    public static void Lock(float x0, float y0, float x1, float y1, Rgba col, float age)
    {
        float close = Effects ? Ease.Out(age / 0.2f) : 1f;
        float pad = L(4f) + L(12f) * (1f - close);
        float breathe = Effects ? MathF.Sin((float)Time * 3.2f) * L(1f) : 0f;
        pad += breathe;
        x0 -= pad;
        y0 -= pad;
        x1 += pad;
        y1 += pad;

        float t = MathF.Max(1.5f, L(2f));
        Brackets(x0, y0, x1, y1, L(16f), t);
        float tick = L(5f);
        float mx = (x0 + x1) * 0.5f, my = (y0 + y1) * 0.5f;
        if (x1 - x0 > tick * 6f)
        {
            Line(mx, y0, mx, y0 + tick, t);
            Line(mx, y1, mx, y1 - tick, t);
        }

        if (y1 - y0 > tick * 6f)
        {
            Line(x0, my, x0 + tick, my, t);
            Line(x1, my, x1 - tick, my, t);
        }

        Flush(col);
    }

    /// <summary>
    /// The focus card, a small menu panel beside what you're aiming at: a tracked tag in the marker's colour, the
    /// name decoding in, then readout lines, and a health meter if it has health. A leader line joins it to the box.
    /// </summary>
    public static void Card(float anchorX, float anchorY, float boxRight, float boxTop, string tag, string title,
        IReadOnlyList<(string Text, Rgba Col)> lines, Rgba col, float health, float trail, float age)
    {
        float ts = Sc(0.66f), gs = Sc(0.46f), ls = Sc(0.5f);
        float pad = L(9);
        float tagW = TextW(tag, gs) + tag.Length * L(1.5f);
        float w = MathF.Max(TextW(title, ts), tagW);
        foreach (var l in lines)
            w = MathF.Max(w, TextW(l.Text, ls));
        w = MathF.Max(w, L(120)) + pad * 2f;

        float lh = TextH(ls) + L(2);
        float h = pad * 2f + TextH(gs) + L(3) + TextH(ts) + L(4) + lines.Count * lh + (health >= 0f ? L(10) : 0f);

        float x = boxRight + L(26);
        float y = boxTop - L(6);
        if (x + w > W - S(12))
            x = Math.Max(S(12), anchorX - L(26) - w - (boxRight - anchorX));
        x = Math.Clamp(x, S(12), W - w - S(12));
        y = Math.Clamp(y, S(12), H - h - S(12));

        float show = Effects ? Ease.Out(age / 0.18f) : 1f;
        float a = Alpha;
        Alpha = a * show;
        x += L(8) * (1f - show);

        // The leader: from the box's near top corner out to the card's edge.
        bool right = x > anchorX;
        float from = right ? boxRight : anchorX - (boxRight - anchorX);
        Line(from, boxTop, right ? x : x + w, y + pad + TextH(gs) * 0.5f, MathF.Max(1f, L(1.25f)));
        Flush(col);

        Rect(x + L(4), y + L(5), w, h, Theme.Black, 0.3f);
        Rect(x, y, w, h, Theme.Panel, 0.9f);
        Rect(x, y, w, MathF.Max(1f, S(1)), col, 0.95f);
        CardBrackets(x, y, w, h, col);

        float ty = y + pad;
        if (Theme.LowEnd)
            Text(x + pad, ty, tag, col, gs);
        else
            Spaced(x + pad, ty, tag, col, gs, 1.5f * Size);
        ty += TextH(gs) + L(3);
        Text(x + pad, ty, MenuView.Decode(title, age), Theme.Hi, ts);
        ty += TextH(ts) + L(4);
        foreach (var l in lines)
        {
            Text(x + pad, ty, l.Text, l.Col, ls);
            ty += lh;
        }

        if (health >= 0f)
            Meter(x + pad, ty + L(3), w - pad * 2f, MathF.Max(3f, L(4)), health, trail, col, 10);
        Alpha = a;
    }

    private static void CardBrackets(float x, float y, float w, float h, Rgba col)
    {
        float arm = L(9);
        float t = MathF.Max(1f, MathF.Round(S(2)));
        float r = x + w, b = y + h;
        Rect(x, y, arm, t, col, 0.95f);
        Rect(x, y, t, arm, col, 0.95f);
        Rect(r - arm, y, arm, t, col, 0.95f);
        Rect(r - t, y, t, arm, col, 0.95f);
        Rect(x, b - t, arm, t, col, 0.6f);
        Rect(x, b - arm, t, arm, col, 0.6f);
        Rect(r - arm, b - t, arm, t, col, 0.6f);
        Rect(r - t, b - arm, t, arm, col, 0.6f);
    }

    /// <summary>
    /// For something off screen or behind you: an arrow on an oval just inside the screen edge, pointing its way,
    /// with a caption toward the middle. The direction comes from the camera's own axes, so it's right even when the
    /// target is behind the camera.
    /// </summary>
    public static void Edge(Vector3 where, Rgba col, string caption)
    {
        Vector3 d = where - WorldView.Eye;
        float rx = Vector3.Dot(d, WorldView.Right);
        float uy = Vector3.Dot(d, WorldView.Up);
        if (MathF.Abs(rx) < 0.001f && MathF.Abs(uy) < 0.001f)
            rx = 1f;

        float a = MathF.Atan2(-uy, rx);
        float fx = MathF.Cos(a), fy = MathF.Sin(a);
        float cx = WorldView.Width * 0.5f, cy = WorldView.Height * 0.5f;
        float inset = S(54);
        float ax = cx + fx * (cx - inset), ay = cy + fy * (cy - inset);

        // A slow nudge outward, like the radial's pointer, so it reads as live.
        float push = Effects ? (0.5f + 0.5f * MathF.Sin((float)Time * 4f)) * L(3f) : 0f;
        ax += fx * push;
        ay += fy * push;

        float size = L(11), rim = MathF.Max(1f, L(1.75f));
        float sx = -fy, sy = fx;
        Shapes.Triangle(ax + fx * (size + rim * 1.8f), ay + fy * (size + rim * 1.8f),
            ax - fx * (size * 0.55f + rim) + sx * (size + rim * 1.4f), ay - fy * (size * 0.55f + rim) + sy * (size + rim * 1.4f),
            ax - fx * (size * 0.55f + rim) - sx * (size + rim * 1.4f), ay - fy * (size * 0.55f + rim) - sy * (size + rim * 1.4f), Rim, 1f);
        Shapes.Triangle(ax + fx * size, ay + fy * size, ax - fx * size * 0.55f + sx * size, ay - fy * size * 0.55f + sy * size,
            ax - fx * size * 0.55f - sx * size, ay - fy * size * 0.55f - sy * size, col, 1f);
        Shapes.Triangle(ax - fx * size * 0.55f + sx * size * 0.5f, ay - fy * size * 0.55f + sy * size * 0.5f,
            ax - fx * size * 0.55f - sx * size * 0.5f, ay - fy * size * 0.55f - sy * size * 0.5f, ax - fx * size * 0.05f, ay - fy * size * 0.05f, Rim, 1f);

        if (caption.Length == 0)
            return;
        float s = Sc(InfoScale);
        float tx = ax - fx * L(34), ty = ay - fy * L(26);
        Caption(tx, ty - (TextH(s) + L(1)) * 0.5f, caption, Rgba.Mix(col, Theme.Hi, 0.5f));
    }

    /// <summary>The editor's crosshair: four rimmed ticks round a gap and a centre dot, lit in the menu colour when it's on something.</summary>
    public static void Reticle(bool onSomething)
    {
        float cx = W * 0.5f, cy = H * 0.5f;
        float gap = L(onSomething ? 6f : 4f), len = L(6f), t = MathF.Max(1.5f, L(2f));
        Line(cx - gap - len, cy, cx - gap, cy, t);
        Line(cx + gap, cy, cx + gap + len, cy, t);
        Line(cx, cy - gap - len, cx, cy - gap, t);
        Line(cx, cy + gap, cx, cy + gap + len, t);
        Diamond(cx, cy, MathF.Max(1.5f, L(1.75f)));
        Flush(onSomething ? Theme.Accent : Theme.Hi);
    }

    /// <summary>A distance in metres, as markers print it.</summary>
    public static string Metres(float units) => Fmt.Metres(units / 100f);
}
