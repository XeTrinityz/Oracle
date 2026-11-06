using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// ORACLE UPLINK: the one look every screen overlay shares (readouts, the speedometer, the running list, key guides,
/// toasts), made from the menu's own parts so the HUD reads as the same machine: glass cards with a hairline of the
/// menu colour along the top, corner brackets, a tracked tag that decodes as the card appears, the glint that passes
/// along the edge, segmented meters, boxed key chips.
///
/// Cards dock instead of sitting at fixed spots: each corner stacks its cards in a fixed order and they ease into
/// place as others come and go, so nothing overlaps and there are no holes. A dock is laid out from the previous
/// frame's cards (one frame behind, hidden by the easing), so a card's place doesn't depend on which module draws first.
/// </summary>
internal static class Hud
{
    public enum Side { Left, Right, BottomLeft, BottomRight }

    /// <summary>OVERLAY SIZE: scales every overlay card.</summary>
    public static float Size = 1f;

    /// <summary>The width of a standard card, so a dock reads as one column.</summary>
    public const float CardW = 270f;

    public const float HeadH = 24f;
    public const float Pad = 12f;
    public const float TagScale = 0.5f;
    public const float BodyScale = 0.56f;
    public const float ValueScale = 0.72f;
    public const float SmallScale = 0.46f;

    private const float DockGap = 8f;
    private const float EnterSeconds = 0.28f;

    /// <summary>A length in 1080p pixels, scaled to the screen and OVERLAY SIZE.</summary>
    public static float U(float v) => S(v) * Size;

    public static float Sc(float scale) => scale * Size;

    private static bool Effects => MenuView.Effects;

    // ---- docking -------------------------------------------------------------------------------------------

    private readonly record struct Slot(Side Side, int Rank, float H);

    private static Dictionary<string, Slot> _last = new();
    private static Dictionary<string, Slot> _now = new();
    private static readonly Dictionary<string, float> _y = new();
    private static readonly Dictionary<string, double> _born = new();
    private static int _frame = -1;

    /// <summary>Starts a frame's layout. Called once per frame before any overlay draws.</summary>
    public static void BeginFrame()
    {
        if (_frame == Frame)
            return;
        _frame = Frame;
        (_last, _now) = (_now, _last);
        _now.Clear();
    }

    /// <summary>
    /// Takes a place in a dock for a card of the given size and lower ranks sit nearer the corner. Sets
    /// <see cref="Gfx.Alpha"/> for the card's entrance and returns where to draw it and how long it's been up.
    /// </summary>
    public static (float X, float Y, float Age) Place(string id, Side side, int rank, float w, float h)
    {
        _now[id] = new Slot(side, rank, h);
        if (!_last.ContainsKey(id))
            _born[id] = Time;
        float age = (float)(Time - (_born.TryGetValue(id, out double b) ? b : Time));

        float before = 0f;
        foreach ((string other, Slot s) in _last)
        {
            if (other != id && s.Side == side && (s.Rank < rank || (s.Rank == rank && string.CompareOrdinal(other, id) < 0)))
                before += s.H + U(DockGap);
        }

        float target = side switch
        {
            Side.Left => S(140) + before,
            Side.Right => S(150) + before,
            Side.BottomLeft => H - S(120) - before - h,
            _ => H - S(48) - before - h,
        };

        float y = _y.TryGetValue(id, out float was) && _last.ContainsKey(id) ? Follow(was, target, 14f) : target;
        _y[id] = y;

        float show = Effects ? Ease.Out(age / EnterSeconds) : 1f;
        Alpha = show;
        bool left = side is Side.Left or Side.BottomLeft;
        float x = left ? S(24) - U(16) * (1f - show) : W - S(24) - w + U(16) * (1f - show);
        return (MathF.Round(x), MathF.Round(y), age);
    }

    // ---- the card ------------------------------------------------------------------------------------------

    /// <summary>
    /// A card: drop shadow, glass, the menu-colour hairline and brackets, a tracked tag that decodes in, a note on the
    /// right, and a faint rule under the header. Returns where its body starts.
    /// </summary>
    public static float Card(float x, float y, float w, float h, string tag, string note, Rgba noteCol, float age, Rgba? edge = null)
    {
        Rgba accent = edge ?? Theme.Accent;
        Rect(x + U(3), y + U(4), w, h, Theme.Black, 0.25f);
        Rect(x, y, w, h, Theme.Panel, 0.84f);
        if (!Theme.LowEnd)
            Outline(x + U(3), y + U(3), w - U(6), h - U(6), accent, 0.06f);
        Rect(x, y, w, MathF.Max(1f, S(1)), accent, 0.75f);
        Brackets(x, y, w, h, accent);
        if (Effects && !Theme.LowEnd)
            Glint(x, y, w, accent);

        float head = U(HeadH);
        float ty = y + (head - TextH(Sc(TagScale))) * 0.5f + U(1);
        Tracked(x + U(Pad), ty, MenuView.Decode(tag, age), accent, Sc(TagScale), 1.6f * Size);
        if (note.Length > 0)
            TextRight(x + w - U(Pad), y + head * 0.5f + U(1), note, noteCol, Sc(SmallScale));
        Rect(x + U(Pad), y + head, w - U(Pad) * 2f, MathF.Max(1f, S(1)), Theme.White, 0.06f);
        return y + head + U(8);
    }

    /// <summary>The height of a card whose body is <paramref name="body"/> tall.</summary>
    public static float Height(float body) => U(HeadH) + U(8) + body + U(Pad) - U(2);

    public static void Brackets(float x, float y, float w, float h, Rgba col)
    {
        float arm = U(10);
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

    private static readonly float[] GlintAlpha = { 0.15f, 0.35f, 0.65f, 1f };

    /// <summary>The menu's glint: a bright streak along the top edge every few seconds, each card a beat after the last.</summary>
    private static void Glint(float x, float y, float w, Rgba accent)
    {
        const double period = 6.0;
        double phase = (Time - y / Math.Max(1f, H) * 0.8) % period / period;
        float s = (float)(phase / 0.3);
        if (s >= 1f)
            return;

        float seg = U(60);
        float head = x + (w + seg) * Ease.Smooth(s) - seg;
        for (int i = 0; i < GlintAlpha.Length; i++)
        {
            float a = Math.Max(x, head + seg * i / GlintAlpha.Length);
            float b = Math.Min(x + w, head + seg * (i + 1) / GlintAlpha.Length);
            if (b > a)
                Rect(a, y, b - a, MathF.Max(1f, S(2)), Rgba.Mix(accent, Theme.White, i * 0.15f), GlintAlpha[i]);
        }
    }

    public static void Outline(float x, float y, float w, float h, Rgba col, float alpha)
    {
        float t = MathF.Max(1f, MathF.Round(S(1)));
        Rect(x, y, w, t, col, alpha);
        Rect(x, y + h - t, w, t, col, alpha);
        Rect(x, y + t, t, h - t * 2f, col, alpha);
        Rect(x + w - t, y + t, t, h - t * 2f, col, alpha);
    }

    /// <summary>Letter-spaced text, which costs a draw call per letter, so LOW-END MODE draws it plain.</summary>
    public static void Tracked(float x, float y, string text, Rgba col, float scale, float spacing)
    {
        if (Theme.LowEnd)
            Text(x, y, text, col, scale);
        else
            Spaced(x, y, text, col, scale, spacing);
    }

    // ---- pieces --------------------------------------------------------------------------------------------

    /// <summary>A boxed label like the menu's key chips, centred on <paramref name="cy"/>. Returns its width.</summary>
    public static float Chip(float x, float cy, string text, Rgba col, float fill = 0.12f, Rgba? textCol = null)
    {
        float s = Sc(SmallScale);
        float w = TextW(text, s) + U(9);
        float h = TextH(s) + U(3);
        float y = cy - h * 0.5f;
        Rect(x, y, w, h, col, fill);
        if (!Theme.LowEnd)
            Outline(x, y, w, h, col, 0.45f);
        TextMid(x + U(4.5f), cy + U(0.5f), text, textCol ?? col, s);
        return w;
    }

    public static float ChipW(string text) => TextW(text, Sc(SmallScale)) + U(9);

    public static float ChipH => TextH(Sc(SmallScale)) + U(3);

    /// <summary>A labelled value in a faint box: the label small on the left, the value on the right.</summary>
    public static void Field(float x, float y, float w, string label, string value, Rgba valueCol)
    {
        float h = FieldH;
        Rect(x, y, w, h, Theme.White, 0.03f);
        if (!Theme.LowEnd)
            Outline(x, y, w, h, Theme.Accent, 0.16f);
        TextMid(x + U(6), y + h * 0.5f + U(0.5f), label, Theme.Low, Sc(SmallScale));
        TextRight(x + w - U(6), y + h * 0.5f + U(0.5f), value, valueCol, Sc(BodyScale));
    }

    public static float FieldH => TextH(Sc(BodyScale)) + U(6);

    /// <summary>A meter's caption: the label on the left and the value on the right, over the bar.</summary>
    public static void Caption(float x, float y, float w, string label, string value, Rgba labelCol)
    {
        Text(x, y, label, labelCol, Sc(SmallScale));
        if (value.Length > 0)
            TextRight(x + w, y + TextH(Sc(SmallScale)) * 0.5f, value, Theme.Hi, Sc(SmallScale));
    }

    public static float CaptionH => TextH(Sc(SmallScale)) + U(3);

    /// <summary>A big number over a small label, for stat columns.</summary>
    public static void Stat(float x, float y, string value, string label, Rgba col)
    {
        Text(x, y, value, col, Sc(0.95f));
        Text(x + U(1), y + TextH(Sc(0.95f)), label, Theme.Low, Sc(SmallScale));
    }

    public static float StatH => TextH(Sc(0.95f)) + TextH(Sc(SmallScale));

    /// <summary>
    /// Key guide rows: a chip for each key and its action beside it, flowing onto new rows as the width fills.
    /// Pass draw = false to measure. Returns the height used.
    /// </summary>
    public static float Keys(float x, float y, float w, IReadOnlyList<(string Key, string Action)> keys, bool draw = true)
    {
        float s = Sc(BodyScale * 0.92f);
        float row = ChipH + U(5);
        float cx = x, cy = y + ChipH * 0.5f;
        int rows = 1;
        foreach ((string key, string action) in keys)
        {
            float need = ChipW(key) + U(6) + TextW(action, s);
            if (cx > x && cx + need > x + w)
            {
                cx = x;
                cy += row;
                rows++;
            }

            if (draw)
            {
                float kw = Chip(cx, cy, key, Theme.Hi, 0.1f);
                TextMid(cx + kw + U(6), cy + U(0.5f), action, Theme.Mid, s);
            }

            cx += need + U(14);
        }

        return rows * row - U(5);
    }

    /// <summary>Splits "KEY  ACTION" strings at their first double space.</summary>
    public static List<(string, string)> Pairs(IEnumerable<string> lines)
    {
        var list = new List<(string, string)>();
        foreach (string l in lines)
        {
            int i = l.IndexOf("  ", StringComparison.Ordinal);
            list.Add(i < 0 ? (l, "") : (l.Substring(0, i).Trim(), l.Substring(i).Trim()));
        }

        return list;
    }
}
