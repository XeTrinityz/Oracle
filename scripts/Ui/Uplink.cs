using System.Globalization;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// The ORACLE UPLINK readout in the empty lower half of the page list: a live signal trace and a few facts about
/// where Batman is. Game values are read twice a second, never while drawing, and every read is guarded.
/// </summary>
internal static class Uplink
{
    private const float RefreshSeconds = 0.5f;
    private const float TagScale = 0.5f;
    private const float ValueScale = 0.62f;
    private const float LineH = 15f;

    private static Every _refresh;
    private static bool _signal;
    private static string _near = "";
    private static string _range = "";
    private static string _clock = "";
    private static string _session = "";

    /// <summary>
    /// Draws into the band between the page rows and the search field. Skips itself when there's no room.
    /// <paramref name="appear"/> runs 0 to 1 as it arrives (and back as it leaves): it rises into place and its
    /// trace grows. <paramref name="decodeAge"/> drives the readout values decoding in, or scrambling out.
    /// </summary>
    public static void Draw(float x, float top, float w, float bottom, Rgba accent, bool effects, float appear, float decodeAge)
    {
        if (bottom - top < S(96))
            return;

        Refresh();

        float left = x + S(13);
        float right = x + w - S(13);
        float y = top + S(14) * (1f - appear);

        // Header: the tag and a blinking LIVE light.
        Rect(left, y, right - left, S(1), Theme.White, 0.06f);
        y += S(10);
        float tagH = TextH(TagScale);
        Text(left, y, "ORACLE UPLINK", accent, TagScale);
        string state = _signal ? "LIVE" : "NO SIGNAL";
        float sw = TextW(state, TagScale);
        Text(right - sw, y, state, _signal ? Theme.Mid : Theme.Warn, TagScale);
        bool lit = !_signal || !effects || Time * 1.6 % 1.0 < 0.6;
        if (lit)
            Rect(right - sw - S(9), y + tagH * 0.5f - S(2), S(4), S(4), _signal ? Theme.Signal : Theme.Warn);
        y += tagH + S(10);

        // The signal trace: mirrored bars that roll with time and jump when something is switched.
        float traceH = Math.Min(S(44), (bottom - y) * 0.4f);
        DrawTrace(left, y, right - left, traceH, accent, effects, appear);
        y += traceH + S(12);

        // Big readouts, like the numerals on Oracle's right-hand screen, when there's room for them and two lines.
        float bigH = TextH(BigScale) + S(5) + TextH(TagScale) + S(12);
        bool big = bottom - y >= bigH + S(LineH) * 2f;
        if (big)
        {
            float colW = (right - left) * 0.5f;
            Numeral(left, y, colW, MenuView.Decode(_clock, decodeAge - 0.1f), "LOCAL TIME", accent);
            Numeral(left + colW, y, colW, MenuView.Decode(Catalog.ActiveCount.ToString("00", CultureInfo.InvariantCulture), decodeAge - 0.16f), "RUNNING", accent);
            y += bigH;
        }

        // Readout lines, as many as fit.
        Line(ref y, bottom, left, right, "NEAREST", MenuView.Decode(_near, decodeAge - 0.22f));
        Line(ref y, bottom, left, right, "DISTANCE", MenuView.Decode(_range, decodeAge - 0.28f));
        if (!big)
            Line(ref y, bottom, left, right, "LOCAL TIME", MenuView.Decode(_clock, decodeAge - 0.34f));
        Line(ref y, bottom, left, right, "SESSION", MenuView.Decode(_session, decodeAge - 0.4f));
    }

    private const float BigScale = 1.3f;

    /// <summary>A large figure with a short accent bar and its label beneath.</summary>
    private static void Numeral(float x, float y, float w, string value, string label, Rgba accent)
    {
        float vh = TextH(BigScale);
        Text(x, y, value, Theme.Hi, BigScale);
        float by = y + vh + S(2);
        Rect(x, by, Math.Min(w - S(10), S(22)), S(2), accent, 0.9f);
        Text(x, by + S(4), label, Theme.Low, TagScale);
    }

    private static void Line(ref float y, float bottom, float left, float right, string label, string value)
    {
        if (value.Length == 0 || y + S(LineH) > bottom)
            return;

        float cy = y + S(LineH) * 0.5f;
        TextMid(left, cy, label, Theme.Low, TagScale);
        float lw = TextW(label, TagScale);
        string shown = Fit(value, right - left - lw - S(10));
        TextRight(right, cy, shown, Theme.Mid, ValueScale);
        y += S(LineH);
    }

    private static string Fit(string text, float room)
    {
        if (TextW(text, ValueScale) <= room)
            return text;
        while (text.Length > 2 && TextW(text + "..", ValueScale) > room)
            text = text.Substring(0, text.Length - 1).TrimEnd();
        return text + "..";
    }

    private static void DrawTrace(float x, float y, float w, float h, Rgba accent, bool effects, float appear)
    {
        int bars = Theme.LowEnd ? 12 : 26;
        float gap = S(2);
        float bw = (w - gap * (bars - 1)) / bars;
        float mid = y + h * 0.5f;
        Rect(x, mid, w, S(1), accent, 0.18f);

        float t = effects ? (float)Time : 0f;
        float kick = Menu.Changed != null ? Math.Clamp(1f - (float)(Time - Menu.ChangedAt) / 0.6f, 0f, 1f) : 0f;
        for (int i = 0; i < bars; i++)
        {
            float v;
            if (!_signal)
                v = 0.05f;
            else
            {
                v = 0.45f + 0.3f * MathF.Sin(t * 2.3f + i * 0.55f) + 0.2f * MathF.Sin(t * 4.1f - i * 1.3f) + 0.1f * MathF.Sin(t * 7.7f + i * 2.9f);
                v = Math.Clamp(v + kick * 0.6f * MathF.Sin(i * 0.9f + t * 12f), 0.08f, 1f);
            }

            // The bars grow out of the centre line from left to right as the readout arrives.
            v *= Math.Clamp(appear * 1.5f - i / (float)bars * 0.5f, 0f, 1f);
            float bh = Math.Max(S(1), h * 0.5f * v);
            Rect(x + i * (bw + gap), mid - bh, bw, bh * 2f, Rgba.Mix(accent, Theme.Hi, v * 0.35f), 0.25f + 0.6f * v);
        }
    }

    private static void Refresh()
    {
        if (!_refresh.Elapsed(RefreshSeconds))
            return;

        _clock = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var session = TimeSpan.FromSeconds(Time);
        _session = session.TotalHours >= 1 ? ((int)session.TotalHours) + session.ToString("\\:mm\\:ss", CultureInfo.InvariantCulture) : session.ToString("m\\:ss", CultureInfo.InvariantCulture);

        _signal = false;
        _near = "";
        _range = "";
        Safe.Run("uplink", () =>
        {
            if (!World.InGame || World.Pawn is not { } pawn)
                return;

            _signal = true;
            var at = pawn.Location;
            if (MapPlaces.Closest(MapPlaces.Groups[0], at) is { } place)
            {
                _near = place.Label;
                _range = Fmt.Metres(Geo.DistXY(place.At, at) / 100f);
            }
        });
    }
}
