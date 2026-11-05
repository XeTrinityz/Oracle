using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// Draws the favourites radial in the menu's style: dark glass, the accent colour, corner brackets, decoding
/// text and a light that sweeps the edges. Slots are segments of a ring around a hub that reads out the slot
/// under the pointer. The highlight glides between slots, slots cascade in when the wheel opens and spin in when
/// the page turns, and a used slot flashes and sends a ripple out from the hub.
/// </summary>
internal static class RadialView
{
    // Geometry in 1080p pixels at RADIAL SIZE 100%.
    private const float HubR = 90f;
    private const float RingIn = 118f;
    private const float RingOut = 256f;
    private const float Lift = 12f;
    private const float GapPx = 6f;
    private const float NumberR = 268f;
    private const float TickIn = 280f;
    private const float TickOut = 287f;
    private const float RailR = 296f;
    private const float HaloR = 314f;

    private const float LabelScale = 0.6f;
    private const float StateScale = 0.54f;
    private const float TagScale = 0.5f;
    private const float HubTitleScale = 0.95f;
    private const float HubLabelScale = 0.74f;
    private const float HubStateScale = 1.1f;
    private const float ChipScale = 0.54f;
    private const float HintScale = 0.6f;
    private const float HintH = 26f;

    private const float FlashSeconds = 0.55f;
    private const float SpinSeconds = 0.32f;
    private const int AnimKey = 0x5AD1A100;
    private const float Rad = MathF.PI / 180f;

    private static readonly Dictionary<(string, int, float), string[]> _lines = new();
    private static readonly List<(string Key, string Label)> _hints = new();
    private static Dictionary<Feature, string>? _paths;

    private static float _openAge;
    private static float _pageAge = 10f;
    private static float _hoverAge;
    private static int _lastHover = -2;
    private static float _hoverAngle = float.NaN;
    private static float _hoverShow;
    private static float _pip = -1f;
    private static float _zoom = 1f;

    // Each slot's drawn angle, lift and fade this frame, kept for the number pass drawn over the highlight.
    private static readonly float[] _numMid = new float[Favourites.MaxPerPage];
    private static readonly float[] _numLift = new float[Favourites.MaxPerPage];
    private static readonly float[] _numAlpha = new float[Favourites.MaxPerPage];

    // The wheel's solid surfaces. DrawTris draws solid in AK, so every translucent touch is mixed against the
    // surface beneath it (Shapes.Under): the halo behind everything, a slot's glass, an empty slot, the hub.
    private static Rgba _halo;
    private static Rgba _glass;
    private static Rgba _bare;
    private static Rgba _hubFill;

    public static void Opened()
    {
        _openAge = 0f;
        _pageAge = 10f;
        _hoverShow = 0f;
        _hoverAngle = float.NaN;
        _lastHover = -2;
        _pip = -1f;
    }

    public static void PageTurned() => _pageAge = 0f;

    private static bool Effects => MenuView.Effects;

    private static float Pulse => Effects ? 0.82f + 0.18f * MathF.Sin((float)Time * 3.2f) : 1f;

    /// <summary>0 to 1 as the n-th piece eases in after <paramref name="age"/> starts counting.</summary>
    private static float Cascade(float age, int n, float delay) =>
        Effects ? Ease.Out(Math.Clamp((age - n * delay) / 0.22f, 0f, 1f)) : 1f;

    /// <summary>A distance on the wheel: scaled to the screen and RADIAL SIZE, and by the open zoom.</summary>
    private static float R(float v) => S(v) * _zoom;

    private static float Wrap(float degrees) => ((degrees % 360f) + 540f) % 360f - 180f;

    public static void Draw(float anim)
    {
        float screenScale = Scale;
        Scale *= Radial.Size;
        try
        {
            DrawWheel(anim);
        }
        finally
        {
            Scale = screenScale;
            Shapes.Under = Theme.Panel;
        }
    }

    private static void DrawWheel(float anim)
    {
        float e = Ease.Out(anim);
        Alpha = e;
        _zoom = 0.8f + 0.2f * e;
        _openAge += Dt;
        _pageAge += Dt;
        _hoverAge += Dt;

        Rgba accent = Theme.Accent;
        _halo = Rgba.Mix(Theme.Black, Theme.Panel, 0.55f);
        _glass = Rgba.Mix(Theme.Panel, accent, 0.05f);
        _bare = Rgba.Mix(_halo, Theme.Panel, 0.5f);
        _hubFill = Rgba.Mix(_halo, Theme.Panel, 0.4f);
        int slots = Radial.Slots;
        int pages = Radial.Pages;
        int page = Math.Clamp(Radial.Page, 0, pages - 1);
        float step = 360f / slots;
        float cx = Gfx.W * 0.5f;
        float cy = Gfx.H * 0.5f;

        int hover = Radial.Hover;
        if (hover != _lastHover)
        {
            _hoverAge = 0f;
            _lastHover = hover;
        }

        // The highlight glides the short way round to the slot under the pointer, and fades when there's none.
        if (hover >= 0)
        {
            float want = Radial.SlotAngle(hover, slots);
            _hoverAngle = float.IsNaN(_hoverAngle) || _hoverShow <= 0.01f ? want : _hoverAngle + Wrap(want - _hoverAngle) * Ease.Follow(22f, Dt);
        }

        _hoverShow = Math.Clamp(_hoverShow + (hover >= 0 ? Dt : -Dt) * 8f, 0f, 1f);

        // Backdrop: the game dims, and more over the menu, so the editor reads clearly on top of it.
        Rect(0f, 0f, Gfx.W, Gfx.H, Theme.Black, Radial.Arranging ? 0.6f : 0.38f);

        DrawFrame(cx, cy, slots, step, accent);

        // A page turn spins the ring in from the side it turned toward.
        float spin = Effects ? -Radial.PageDir * 26f * (1f - Ease.Out(Math.Clamp(_pageAge / SpinSeconds, 0f, 1f))) : 0f;
        float gap = GapPx / ((RingIn + RingOut) * 0.5f) / Rad;
        for (int i = 0; i < slots; i++)
        {
            float appear = Math.Min(Cascade(_openAge, i, 0.03f), Cascade(_pageAge, i, 0.018f));
            float outer = Alpha;
            Alpha *= appear;
            DrawSlot(cx, cy, page, i, slots, Radial.SlotAngle(i, slots) + spin * (1.6f - appear * 0.6f), step, gap, hover, accent);
            Alpha = outer;
        }

        DrawHighlight(cx, cy, step, gap, accent);
        DrawNumbers(cx, cy, slots, hover, accent);
        DrawHub(cx, cy, page, pages, slots, accent);
        DrawRipple(cx, cy, accent);

        float below = cy + R(HaloR) + S(18);
        below = DrawPages(cx, below, page, pages, accent);
        DrawHints(cx, below + S(10));
    }

    // ---- frame around the ring --------------------------------------------------------------------------

    private static void DrawFrame(float cx, float cy, int slots, float step, Rgba accent)
    {
        float halo = R(HaloR);
        Shapes.Under = Theme.Black;
        Shapes.Disc(cx, cy, halo, _halo, 1f);
        Shapes.Under = _halo;
        Shapes.Ring(cx, cy, halo - S(1), halo, accent, 0.3f);

        if (!Theme.LowEnd)
        {
            // Faint guide lines through the centre and a mid ring, like the layout grid on the menu's panels.
            Shapes.Line(cx - halo + S(8), cy, cx + halo - S(8), cy, S(1), accent, 0.04f);
            Shapes.Line(cx, cy - halo + S(8), cx, cy + halo - S(8), S(1), accent, 0.04f);
            Shapes.Ring(cx, cy, R(RingIn - 12f), R(RingIn - 11f), accent, 0.06f);
        }

        // A slowly turning scale of fine ticks, and fixed longer ticks at every slot boundary.
        float turn = Effects ? (float)(Time * 5.0 % 360.0) : 0f;
        Shapes.Ticks(cx, cy, R(TickIn), R(TickOut), Theme.LowEnd ? 36 : 90, turn, S(1.2f), accent, 0.2f);
        Shapes.Ticks(cx, cy, R(TickIn - 4f), R(TickOut + 4f), slots, -90f - step * 0.5f, S(2f), accent, 0.55f);

        // The outer rail, with a light running round it.
        float rail = R(RailR);
        Shapes.Ring(cx, cy, rail, rail + S(1.5f), accent, 0.2f);
        if (Effects && !Theme.LowEnd)
        {
            float head = (float)(Time * 80.0 % 360.0);
            for (int k = 0; k < 4; k++)
                Shapes.Arc(cx, cy, rail - S(0.5f), rail + S(2f), head - 14f * (k + 1), head - 14f * k, Rgba.Mix(accent, Theme.White, 0.3f - k * 0.07f), 0.7f * (1f - k / 4f));
        }
    }

    // ---- slots ------------------------------------------------------------------------------------------

    private static void DrawSlot(float cx, float cy, int page, int i, int slots, float mid, float step, float gap, int hover, Rgba accent)
    {
        int flat = page * slots + i;
        bool over = i == hover;
        float lift = Approach(AnimKey + i, over ? 1f : 0f, 16f);
        float a0 = mid - step * 0.5f + gap * 0.5f;
        float a1 = mid + step * 0.5f - gap * 0.5f;
        float r0 = R(RingIn) + S(3f) * lift;
        float r1 = R(RingOut) + S(Lift) * lift;

        Feature? f = Favourites.At(flat);
        bool arranging = Radial.Arranging;
        bool lifted = arranging && Radial.Hand != null && Radial.HandFrom == flat;
        Feature? hand = arranging && over && Radial.Hand != null && Catalog.ById.TryGetValue(Radial.Hand, out Feature? h) ? h : null;
        bool dropping = hand != null;

        _numMid[i] = mid;
        _numLift[i] = lift;
        _numAlpha[i] = Alpha;

        if (f == null && hand == null)
        {
            DrawEmpty(cx, cy, r0, r1, a0, a1, mid, over, lift, accent);
            DrawFlash(cx, cy, r0, r1, a0, a1, flat);
            Shapes.Under = _halo;
            return;
        }

        float body = lifted && !over ? 0.4f : 1f;
        float outer = Alpha;
        Alpha *= body;

        // Glass, then an accent wash that deepens toward the rim, so a lit slot glows from its edge in. It stays
        // dark enough under the label for white text to read.
        Shapes.Arc(cx, cy, r0, r1, a0, a1, _glass, 1f);
        Shapes.Under = _glass;
        float wash = dropping ? 0.2f * Pulse : 0.03f + 0.13f * lift;
        Shapes.FadeArc(cx, cy, r0, r1, a0, a1, accent, wash * 0.3f, wash * 1.6f, 4);

        // The rim: a bright lip along the outer edge, thicker and whiter on the slot under the pointer.
        Rgba rim = over ? Rgba.Mix(accent, Theme.White, 0.3f * Pulse) : accent;
        Shapes.Arc(cx, cy, r1 - S(2.5f + 2f * lift), r1, a0, a1, rim, 0.35f + 0.65f * lift);
        Shapes.Arc(cx, cy, r0, r0 + S(1f), a0, a1, Theme.White, 0.08f);

        // Running: the inner edge lights up, like the tick a running row gets in the menu.
        float arm = Math.Min(6f, (a1 - a0) * 0.22f);
        if (f is { Active: true } && !dropping)
            Shapes.Arc(cx, cy, r0, r0 + S(4f), a0 + arm * 0.5f, a1 - arm * 0.5f, accent, 0.9f);

        DrawFlash(cx, cy, r0, r1, a0, a1, flat);

        // Corner brackets, as on every menu panel: short arcs off the outer corners and strokes down the sides.
        // They sit on the halo, outside the glass.
        Shapes.Under = _halo;
        Shapes.Arc(cx, cy, r1 + S(3f), r1 + S(5f), a0, a0 + arm, accent, 0.9f);
        Shapes.Arc(cx, cy, r1 + S(3f), r1 + S(5f), a1 - arm, a1, accent, 0.9f);
        Spoke(cx, cy, a0, r1 - S(14f), r1 + S(5f), S(2f), accent, 0.9f);
        Spoke(cx, cy, a1, r1 - S(14f), r1 + S(5f), S(2f), accent, 0.9f);

        if (dropping)
            Shapes.Dashes(cx, cy, r0 - S(5f), r0 - S(3f), a0, a1, 4f, 3f, Theme.White, 0.5f * Pulse);

        Feature shown = dropping ? hand! : f!;
        (string state, Rgba stateCol) = SlotState(shown, dropping ? f : null, dropping, lifted, accent);
        DrawContent(cx, cy, r0, r1, mid, step, over, shown.Label, state, stateCol);
        Alpha = outer;
    }

    /// <summary>
    /// The line under a slot's label. In the editor it says what ENTER would do there; on the live wheel it's the
    /// feature's state, or DONE / FAILED just after a command ran.
    /// </summary>
    private static (string, Rgba) SlotState(Feature f, Feature? occupant, bool dropping, bool lifted, Rgba accent)
    {
        if (Radial.Arranging)
        {
            if (dropping && lifted)
                return ("PUT BACK", accent);
            if (dropping && occupant == null)
                return ("PLACE HERE", accent);
            if (dropping)
                return Radial.HandFrom >= 0 ? ("SWAP", accent) : ("REPLACE", Theme.Warn);
            if (lifted)
                return ("MOVING", accent);
            return (StateText(f), f.Active ? accent : Theme.Low);
        }

        bool flashing = f is Command && Favourites.At(Radial.Flashed) == f && Time - Radial.FlashedAt < 1.2;
        if (flashing)
            return Radial.FlashOk ? ("DONE", accent) : ("FAILED", Theme.Warn);
        return (StateText(f), f.Active ? accent : Theme.Low);
    }

    private static void DrawEmpty(float cx, float cy, float r0, float r1, float a0, float a1, float mid, bool over, float lift, Rgba accent)
    {
        Shapes.Arc(cx, cy, r0, r1, a0, a1, _bare, 1f);
        Shapes.Under = _bare;
        if (lift > 0.01f)
            Shapes.FadeArc(cx, cy, r0, r1, a0, a1, accent, 0.02f * lift, 0.12f * lift, 3);

        float edge = over ? 0.5f : 0.16f;
        Shapes.Dashes(cx, cy, r1 - S(1.5f), r1, a0, a1, 3f, 2.5f, over ? accent : Theme.White, edge);
        Shapes.Dashes(cx, cy, r0, r0 + S(1.5f), a0, a1, 3f, 2.5f, Theme.White, edge * 0.6f);

        float rm = (r0 + r1) * 0.5f;
        float tx = cx + MathF.Cos(mid * Rad) * rm;
        float ty = cy + MathF.Sin(mid * Rad) * rm;
        string text = Radial.Arranging ? "+" : "EMPTY";
        float scale = Radial.Arranging ? 1.1f : TagScale;
        TextCentre(tx, ty - TextH(scale) * 0.5f, text, over ? Theme.Mid : Theme.Low, scale, Radial.Arranging ? 0.6f + 0.4f * lift : 1f);
    }

    /// <summary>A used or placed slot flashes white (or the warning colour if it failed or was taken off).</summary>
    private static void DrawFlash(float cx, float cy, float r0, float r1, float a0, float a1, int flat)
    {
        if (flat != Radial.Flashed)
            return;

        float t = (float)(Time - Radial.FlashedAt) / FlashSeconds;
        if (t is >= 0f and < 1f)
            Shapes.Arc(cx, cy, r0, r1, a0, a1, Radial.FlashOk ? Theme.White : Theme.Warn, 0.5f * (1f - t) * (1f - t));
    }

    /// <summary>
    /// Each slot's number key, in the gap between the ring and the outer ticks. Drawn after the highlight, so the
    /// hovered slot's number (pushed out with its slot) stays on top, on a small dark plate over the ticks and glow.
    /// </summary>
    private static void DrawNumbers(float cx, float cy, int slots, int hover, Rgba accent)
    {
        float outer = Alpha;
        float th = TextH(TagScale);
        for (int i = 0; i < Math.Min(slots, 10); i++)
        {
            Alpha = _numAlpha[i];
            string n = i == 9 ? "0" : (i + 1).ToString();
            float lift = _numLift[i];
            float r = R(NumberR) + S(Lift + 2f) * lift;
            float x = cx + MathF.Cos(_numMid[i] * Rad) * r;
            float y = cy + MathF.Sin(_numMid[i] * Rad) * r;
            if (lift > 0.01f)
            {
                float w = TextW(n, TagScale) + S(10f);
                float h = th + S(4f);
                Rect(x - w * 0.5f, y - h * 0.5f, w, h, _halo, 0.95f * lift);
                Rect(x - w * 0.5f, y + h * 0.5f - S(1f), w, S(1f), accent, 0.8f * lift);
            }

            TextCentre(x, y - th * 0.5f, n, i == hover ? accent : Theme.Low, TagScale);
        }

        Alpha = outer;
    }

    /// <summary>The label (two lines at most) and the state line beneath it, centred in the slot.</summary>
    private static void DrawContent(float cx, float cy, float r0, float r1, float mid, float step, bool over, string label, string state, Rgba stateCol)
    {
        float rm = (r0 + r1) * 0.5f;
        float tx = cx + MathF.Cos(mid * Rad) * rm;
        float ty = cy + MathF.Sin(mid * Rad) * rm;
        float room = Math.Min(S(150f), 2f * rm * MathF.Sin(Math.Min(step, 120f) * 0.5f * Rad) * 0.8f);

        string[] lines = Lines(label, room, LabelScale);
        state = Clip(state, room, StateScale);
        float lh = TextH(LabelScale);
        float sh = TextH(StateScale);
        float total = lh * lines.Length + S(4f) + sh;
        float y = ty - total * 0.5f;
        Rgba labelCol = over ? Theme.Hi : Theme.Mid;
        foreach (string line in lines)
        {
            Shadowed(tx, y, line, labelCol, LabelScale);
            y += lh;
        }

        Shadowed(tx, y + S(4f), state, stateCol, StateScale);
    }

    /// <summary>Centred text with a dark shadow a pixel down and right, so it reads on a lit slot.</summary>
    private static void Shadowed(float cx, float y, string text, Rgba col, float scale)
    {
        float o = Math.Max(1f, MathF.Round(S(1.2f)));
        TextCentre(cx + o, y + o, text, Theme.Black, scale, 0.75f);
        TextCentre(cx, y, text, col, scale);
    }

    private static string StateText(Feature f) => f switch
    {
        Toggle t => t.On ? "ON" : "OFF",
        Command => "RUN",
        _ => f.State,
    };

    /// <summary>What letting go (or clicking) does to a feature, for the hub.</summary>
    private static string Verb(Feature f) => f switch
    {
        Toggle t => t.On ? "TURN OFF" : "TURN ON",
        Slider s => s.IsDefault ? "SET " + s.Format(s.HotValue) : "RESET",
        Choice => "NEXT OPTION",
        _ => "RUN",
    };

    /// <summary>A short radial stroke at an angle, between two radii.</summary>
    private static void Spoke(float cx, float cy, float degrees, float r0, float r1, float thick, Rgba col, float alpha)
    {
        float c = MathF.Cos(degrees * Rad);
        float s = MathF.Sin(degrees * Rad);
        Shapes.Line(cx + c * r0, cy + s * r0, cx + c * r1, cy + s * r1, thick, col, alpha);
    }

    // ---- highlight, hub and ripple ----------------------------------------------------------------------

    /// <summary>The highlight that glides between slots: a bright segment on the outer rail and a notch by the hub.</summary>
    private static void DrawHighlight(float cx, float cy, float step, float gap, Rgba accent)
    {
        if (_hoverShow <= 0f || float.IsNaN(_hoverAngle))
            return;

        float outer = Alpha;
        Alpha *= Ease.Out(_hoverShow);
        float a0 = _hoverAngle - step * 0.5f + gap;
        float a1 = _hoverAngle + step * 0.5f - gap;
        float rail = R(RailR);
        Shapes.Arc(cx, cy, rail - S(2f), rail + S(3.5f), a0, a1, accent, 0.95f * Pulse);
        Shapes.FadeArc(cx, cy, rail - S(16f), rail - S(2f), a0, a1, accent, 0f, 0.14f, 3);

        // A small chevron on the rail, pointing out of the wheel.
        float c = MathF.Cos(_hoverAngle * Rad);
        float s = MathF.Sin(_hoverAngle * Rad);
        float tip = rail + S(14f);
        float baseR = rail + S(6f);
        float half = S(6f);
        Shapes.Triangle(cx + c * tip, cy + s * tip, cx + c * baseR - s * half, cy + s * baseR + c * half, cx + c * baseR + s * half, cy + s * baseR - c * half, accent, 0.95f);
        Alpha = outer;
    }

    private static void DrawHub(float cx, float cy, int page, int pages, int slots, Rgba accent)
    {
        float hub = R(HubR);
        Shapes.Disc(cx, cy, hub, _hubFill, 1f);
        Shapes.Under = _hubFill;
        Shapes.Ring(cx, cy, hub - S(1.5f), hub, accent, 0.55f);
        if (!Theme.LowEnd)
            Shapes.Ring(cx, cy, hub - S(9f), hub - S(8f), Theme.White, 0.05f);
        Shapes.Under = _halo;

        // Two arcs circling the hub one way and a thinner one the other, like Oracle's scanning displays.
        float turn = Effects ? (float)(Time * 48.0 % 360.0) : 0f;
        Shapes.Arc(cx, cy, hub + S(5f), hub + S(8f), turn, turn + 70f, accent, 0.7f);
        Shapes.Arc(cx, cy, hub + S(5f), hub + S(8f), turn + 180f, turn + 250f, accent, 0.7f);
        if (!Theme.LowEnd)
            Shapes.Arc(cx, cy, hub + S(12f), hub + S(13.5f), -turn * 0.6f, -turn * 0.6f + 120f, accent, 0.3f);

        DrawPointer(cx, cy, hub, accent);
        DrawReadout(cx, cy, hub, page, pages, slots, accent);
    }

    /// <summary>Where the pointer leans: a lit notch on the hub's edge and an arrow just outside it.</summary>
    private static void DrawPointer(float cx, float cy, float hub, Rgba accent)
    {
        float x = Radial.PointX;
        float y = Radial.PointY;
        float lean = Math.Min(1f, MathF.Sqrt(x * x + y * y));
        if (lean < 0.05f)
            return;

        float a = MathF.Atan2(y, x) / Rad;
        Shapes.Under = _hubFill;
        Shapes.Arc(cx, cy, hub - S(4f), hub + S(1f), a - 16f, a + 16f, Rgba.Mix(accent, Theme.White, 0.25f), 0.9f * lean);
        Shapes.Under = _halo;

        float c = MathF.Cos(a * Rad);
        float s = MathF.Sin(a * Rad);
        float tip = hub + S(4f) + R(RingIn - HubR - 6f) * Math.Max(0.35f, lean);
        float baseR = hub + S(3f);
        float half = S(6f);
        Shapes.Triangle(cx + c * tip, cy + s * tip, cx + c * baseR - s * half, cy + s * baseR + c * half, cx + c * baseR + s * half, cy + s * baseR - c * half, accent, 0.85f * lean);
    }

    /// <summary>The hub's text: the slot under the pointer, or the wheel's own title and page.</summary>
    private static void DrawReadout(float cx, float cy, float hub, int page, int pages, int slots, Rgba accent)
    {
        float room = hub * 1.6f;
        Feature? f = Radial.HoverFeature;

        if (Radial.Arranging)
        {
            Feature? hand = Radial.Hand != null && Catalog.ById.TryGetValue(Radial.Hand, out Feature? h) ? h : null;
            string doing = hand == null ? "PICK A SLOT" : Radial.HandFrom >= 0 ? "MOVING" : "PLACING";
            string what = hand?.Label ?? (f != null ? f.Label : "EMPTY SLOT");
            Stack(cx, cy, room, MenuView.Decode("ARRANGE", _openAge), doing, what,
                "SLOT " + (Radial.Hover + 1) + "  PAGE " + (page + 1) + " / " + pages, accent, hand != null ? accent : Theme.Mid);
            return;
        }

        if (Favourites.Count == 0)
        {
            Stack(cx, cy, room, "RADIAL", "NO FAVOURITES YET", "", Pad.InUse ? "PRESS Y ON A MENU ROW" : "PRESS HOME ON A MENU ROW", accent, Theme.Low);
            return;
        }

        if (f != null)
        {
            string path = PathOf(f);
            string value = f is Toggle t ? (t.On ? "ON" : "OFF") : f is Command ? "" : f.State;
            float lineH = TextH(HubLabelScale);
            string[] label = Lines(f.Label, room, HubLabelScale);
            float valueH = value.Length > 0 ? TextH(HubStateScale) + S(2f) : 0f;
            float tagH = TextH(TagScale);
            float total = tagH + S(6f) + lineH * label.Length + S(4f) + valueH + S(4f) + tagH;
            float y = cy - total * 0.5f;
            TextCentre(cx, y, Clip(path, room, TagScale), Theme.Low, TagScale);
            y += tagH + S(6f);
            foreach (string line in label)
            {
                TextCentre(cx, y, MenuView.Decode(line, _hoverAge), Theme.Hi, HubLabelScale);
                y += lineH;
            }

            y += S(4f);
            if (value.Length > 0)
            {
                TextCentre(cx, y, Clip(value, room, HubStateScale), f.Active ? accent : Theme.Mid, HubStateScale);
                y += valueH;
            }

            string hint = (Radial.HoldMode && Radial.IsLive ? "LET GO TO " : "CLICK TO ") + Verb(f);
            TextCentre(cx, y + S(4f), Clip(hint, room, TagScale), accent, TagScale);
            return;
        }

        string title = MenuView.Decode("ORACLE", _openAge);
        string sub = Radial.Hover >= 0 ? "EMPTY SLOT" : "FAVOURITES";
        Stack(cx, cy, room, title, sub, "", "PAGE " + (page + 1) + " / " + pages, accent, Theme.Mid);
    }

    /// <summary>A centred block: a letter-spaced title, an accent tag, an optional line, and a quiet footnote.</summary>
    private static void Stack(float cx, float cy, float room, string title, string tag, string line, string foot, Rgba accent, Rgba lineCol)
    {
        float titleH = TextH(HubTitleScale);
        float tagH = TextH(TagScale);
        float lineH = TextH(StateScale);
        string[] lines = line.Length > 0 ? Lines(line, room, StateScale) : Array.Empty<string>();
        float total = titleH + (tag.Length > 0 ? S(4f) + tagH : 0f) + (lines.Length > 0 ? S(8f) + lineH * lines.Length : 0f) + (foot.Length > 0 ? S(8f) + tagH : 0f);
        float y = cy - total * 0.5f;

        float spacing = 3f;
        float tw = Theme.LowEnd ? TextW(title, HubTitleScale) : SpacedWidth(title, HubTitleScale, spacing);
        if (Theme.LowEnd)
            Text(cx - tw * 0.5f, y, title, Theme.Hi, HubTitleScale);
        else
            Spaced(cx - tw * 0.5f, y, title, Theme.Hi, HubTitleScale, spacing);
        y += titleH;

        if (tag.Length > 0)
        {
            TextCentre(cx, y + S(4f), Clip(tag, room, TagScale), accent, TagScale);
            y += S(4f) + tagH;
        }

        if (lines.Length > 0)
        {
            y += S(8f);
            foreach (string l in lines)
            {
                TextCentre(cx, y, l, lineCol, StateScale);
                y += lineH;
            }
        }

        if (foot.Length > 0)
            TextCentre(cx, y + S(8f), Clip(foot, room * 1.1f, TagScale), Theme.Low, TagScale);
    }

    private static float SpacedWidth(string text, float scale, float spacing)
    {
        float w = 0f;
        foreach (char ch in text)
            w += TextW(ch.ToString(), scale) + S(spacing);
        return Math.Max(0f, w - S(spacing));
    }

    /// <summary>A ring that races out from the hub when a slot is used: the accent colour, or the warning colour on a failure.</summary>
    private static void DrawRipple(float cx, float cy, Rgba accent)
    {
        float t = (float)(Time - Radial.FlashedAt) / FlashSeconds;
        if (!Effects || t is < 0f or >= 1f || Radial.Arranging)
            return;

        float r = R(HubR) + (R(RailR) - R(HubR)) * Ease.Out(t);
        Shapes.Ring(cx, cy, r, r + S(1f) + S(3f) * (1f - t), Radial.FlashOk ? accent : Theme.Warn, 0.6f * (1f - t));
    }

    // ---- pages and key strip ----------------------------------------------------------------------------

    /// <summary>One segment per page under the wheel, the current one lit, with the page keys either side. Returns where it ends.</summary>
    private static float DrawPages(float cx, float y, int page, int pages, Rgba accent)
    {
        if (pages <= 1 && !Radial.Arranging)
            return y;

        float w = S(28f);
        float gap = S(6f);
        float h = S(4f);
        float total = pages * w + (pages - 1) * gap;
        float x0 = cx - total * 0.5f;
        float tagH = TextH(TagScale);
        TextCentre(cx, y, "PAGE " + (page + 1) + " / " + pages, Theme.Mid, TagScale);
        float by = y + tagH + S(7f);

        for (int p = 0; p < pages; p++)
        {
            bool spare = Radial.Arranging && p == pages - 1 && p >= Favourites.UsedPages;
            Rect(x0 + p * (w + gap), by, w, h, Theme.White, spare ? 0.05f : 0.14f);
        }

        _pip = Follow(_pip, page, 18f);
        Rect(x0 + _pip * (w + gap), by, w, h, accent, 0.95f);

        bool pad = Pad.InUse;
        float cyKeys = by + h * 0.5f;
        string back = pad ? "LB" : "Q";
        string next = pad ? "RB" : "E";
        float bw = Chip(0f, 0f, back, Theme.Hi, 0f, measure: true);
        Chip(x0 - S(12f) - bw, cyKeys, back, Theme.Hi, 0.1f);
        Chip(x0 + total + S(12f), cyKeys, next, Theme.Hi, 0.1f);
        return by + h + S(8f);
    }

    /// <summary>The key strip under the wheel, in the menu's style. It changes with how the wheel was opened and what's in hand.</summary>
    private static void DrawHints(float cx, float y)
    {
        BuildHints();
        float pad = S(10f);
        float space = S(16f);
        float w = pad * 2f - space;
        foreach ((string key, string label) in _hints)
            w += Chip(0f, 0f, key, Theme.Hi, 0f, measure: true) + S(6f) + TextW(label, HintScale) + space;

        float h = S(HintH);
        float x = cx - w * 0.5f;
        float mid = y + h * 0.5f;
        Rect(x, y, w, h, Theme.Panel, 0.86f);
        Rect(x, y, w, S(1f), Theme.Accent, 0.5f);

        float hx = x + pad;
        foreach ((string key, string label) in _hints)
        {
            hx += Chip(hx, mid, key, Theme.Hi, 0.1f) + S(6f);
            hx += TextMid(hx, mid, label, Theme.Mid, HintScale) + space;
        }
    }

    private static void BuildHints()
    {
        _hints.Clear();
        bool pad = Pad.InUse;
        int slots = Radial.Slots;
        string digits = slots >= 10 ? "1-0" : "1-" + slots;

        if (Radial.Arranging)
        {
            string act = Radial.Hand != null ? "PLACE" : "PICK UP";
            if (pad)
            {
                _hints.Add(("STICK", "SLOT"));
                _hints.Add(("A", act));
                _hints.Add(("X", "REMOVE"));
                _hints.Add(("B", "DONE"));
            }
            else
            {
                _hints.Add(("< >", "SLOT"));
                _hints.Add(("ENTER", act));
                _hints.Add(("DEL", "REMOVE"));
                _hints.Add(("TAB", "PAGE"));
                _hints.Add(("ESC", "DONE"));
            }

            return;
        }

        bool hold = Radial.HoldMode;
        if (pad && Radial.OpenedBy == Radial.Source.Pad)
        {
            _hints.Add(("STICK", "POINT"));
            if (hold)
                _hints.Add(("RELEASE", "USE"));
            _hints.Add(("A", hold ? "USE, STAY" : "USE"));
            _hints.Add(("B", "CANCEL"));
            return;
        }

        _hints.Add(("MOUSE", "POINT"));
        if (hold)
            _hints.Add(("LET GO", "USE"));
        _hints.Add(("CLICK", hold ? "USE, STAY" : "USE"));
        _hints.Add((digits, "QUICK USE"));
        _hints.Add(("RMB", "CANCEL"));
    }

    /// <summary>A small boxed key name, vertically centred on <paramref name="cy"/>. Returns its width; with measure, draws nothing.</summary>
    private static float Chip(float x, float cy, string text, Rgba col, float fill, bool measure = false)
    {
        float w = TextW(text, ChipScale) + S(9f);
        if (measure)
            return w;

        float h = TextH(ChipScale) + S(2f);
        float y = cy - h * 0.5f;
        Rect(x, y, w, h, col, fill);
        if (!Theme.LowEnd)
        {
            float t = Math.Max(1f, MathF.Round(S(1f)));
            Rect(x, y, w, t, col, 0.4f);
            Rect(x, y + h - t, w, t, col, 0.4f);
            Rect(x, y + t, t, h - t * 2f, col, 0.4f);
            Rect(x + w - t, y + t, t, h - t * 2f, col, 0.4f);
        }

        TextMid(x + S(4.5f), cy, text, col, ChipScale);
        return w;
    }

    // ---- text helpers -----------------------------------------------------------------------------------

    /// <summary>Wraps text to at most two lines that fit, the second cut short with ".." if it runs over. Cached.</summary>
    private static string[] Lines(string text, float room, float scale)
    {
        var key = (text, (int)room, scale * Scale);
        if (_lines.TryGetValue(key, out string[]? lines))
            return lines;
        if (_lines.Count > 512)
            _lines.Clear();

        string rest = FitLine(text, room, scale, out string first);
        if (first.Length == 0 || TextW(first, scale) > room)
        {
            first = Clip(text, room, scale);
            rest = "";
        }

        lines = rest.Length == 0 ? new[] { first } : new[] { first, Clip(rest, room, scale) };
        _lines[key] = lines;
        return lines;
    }

    private static string Clip(string text, float room, float scale)
    {
        if (TextW(text, scale) <= room)
            return text;

        int len = text.Length;
        while (len > 1 && TextW(text.Substring(0, len).TrimEnd() + "..", scale) > room)
            len--;
        return text.Substring(0, len).TrimEnd() + "..";
    }

    /// <summary>Where a feature lives in the menu, like "PLAYER / COMBAT", for the hub.</summary>
    private static string PathOf(Feature f)
    {
        if (_paths == null)
        {
            _paths = new Dictionary<Feature, string>();
            foreach (MenuPage page in Catalog.Pages)
            {
                foreach (MenuRow row in page.Rows)
                {
                    if (row.Item != null)
                        _paths.TryAdd(row.Item, page.Title);
                }

                foreach (MenuPage child in page.Children)
                {
                    foreach (MenuRow row in child.Rows)
                    {
                        if (row.Item != null)
                            _paths.TryAdd(row.Item, page.Title + " / " + child.Title);
                    }
                }
            }
        }

        return _paths.GetValueOrDefault(f, "");
    }
}
