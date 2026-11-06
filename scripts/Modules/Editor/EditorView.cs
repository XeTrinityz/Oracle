using System.Globalization;
using System.Numerics;
using BmSDK.Engine;
using static CeZeY.Oracle.Gfx;
using static CeZeY.Oracle.Lens;

namespace CeZeY.Oracle;

/// <summary>
/// Draws the editor over the world in the ORACLE LENS look (see <see cref="Lens"/>): corner brackets round each
/// marked object (a diamond over it with BOXES off), name plates that carry more the closer it is, a card for
/// whatever you aim at, a white lock on what SELECT AIMED would take, and the selected object's wireframe and card.
/// All the brackets and diamonds of one colour go to the game in one batch.
/// </summary>
internal static class EditorView
{
    private const float NearRange = 3000f;
    private const float DetailRange = 6000f;
    private const float MinFrame = 14f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly record struct Shown(Thing T, float X0, float Y0, float X1, float Y1, bool Removed, float Dist, Tier Tier, float Age, bool Framed);

    private static readonly List<Shown> _shown = new();
    private static readonly List<(string, Rgba)> _lines = new();
    private static readonly HashSet<nint> _aboard = new();
    private static readonly Dictionary<nint, List<Pawn>> _crews = new();
    private static readonly Stack<List<Pawn>> _pool = new();
    private static readonly List<Pawn> _scratch = new();
    private static readonly List<(float X, float Y, bool Hostile)> _pips = new();
    private static nint _aimPtr;
    private static double _aimSince;

    public static void Draw(WorldEditor ed, Canvas canvas)
    {
        WorldView.Begin(canvas);
        if (!WorldView.Ready)
            return;

        Alpha = 1f;
        Discard();
        Thing? sel = ed.Selected;
        CollectCrews(ed);

        _shown.Clear();
        int marked = 0;
        foreach (Thing t in ed.Visible)
        {
            // Spawned bodyguards and militia threats wear their own markers instead.
            if (t == sel || !ed.Shows(t.Kind) || !t.Alive || Spawner.I.IsBodyguard(t.Ptr) || Population.I.Marks(t.Ptr))
                continue;

            // Someone riding in a marked vehicle is shown as part of it (its crew pips and card), not as a box inside a box.
            if (t.Kind is ThingKind.Enemy or ThingKind.Friendly && _aboard.Contains(t.Ptr))
                continue;
            if (++marked > ed.MostShown)
                break;

            Vector3 at = t.Actor.Location;
            if (!WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1))
                continue;

            float dist = Vector3.Distance(WorldView.Eye, t.Centre);
            bool framed = ed.Boxes;
            if (framed)
            {
                // Every marked object gets brackets: a far-off one (or one seen from straight above) keeps a box big
                // enough to read, rather than turning into something else.
                Grow(ref x0, ref x1, L(MinFrame * 0.75f));
                Grow(ref y0, ref y1, L(MinFrame));
            }

            Offer(t.Ptr, x0, y0, x1, y1);
            _shown.Add(new Shown(t, x0, y0, x1, y1, Edits.Find(t)?.Removed ?? false, dist, TierOf(t.Ptr, dist, NearRange, DetailRange), Age(t.Ptr), framed));
        }

        // Washes first (plain tiles), then every bracket and diamond, one batch per colour, then tracers and labels on top.
        foreach (Shown s in _shown)
        {
            // No wash over something that fills much of the screen: it would tint the whole view.
            if (s.Framed && s.Tier >= Tier.Full && (s.X1 - s.X0) * (s.Y1 - s.Y0) < W * H * 0.15f)
                Wash(s.X0, s.Y0, s.X1, s.Y1, ColourOf(s.T, s.Removed), 0.06f * Settle(s.Age));
        }

        for (int k = 0; k < Things.KindCount; k++)
        {
            Markers((ThingKind)k, false);
            Markers((ThingKind)k, true);
        }

        foreach (Shown s in _shown)
        {
            if (_crews.TryGetValue(s.T.Ptr, out List<Pawn>? crew))
                Crew.Pips(crew, (s.X0 + s.X1) * 0.5f, s.Y0 - (s.Framed ? L(11) : L(19)), _pips);
        }

        Crew.FlushPips(_pips);

        if (ed.Tracers)
            Tracers();

        foreach (Shown s in _shown)
            Label(s, ed.Details);

        foreach (Shown s in _shown)
        {
            if (s.Tier == Tier.Card)
                FocusCard(s, ed.Details);
        }

        Thing? aimed = ed.Picking ? ed.Aimed() : null;
        if (aimed != null && aimed != sel)
            Bracket(aimed);

        if (sel != null)
            DrawSelected(ed, sel);

        if (ed.Picking)
            Reticle(aimed != null);

        if (ed.ModeOn && ed.Picking)
            Controls(ed);
    }

    private static readonly string[] Keys =
    {
        "WASD  FLY", "SPACE CTRL  UP DOWN", "SHIFT  FAST", "CLICK  SELECT", "HOLD  GRAB", "RIGHT CLICK  DESELECT",
        "Q E  TURN", "Z X  SIZE", "F  FREEZE", "G  GROUND", "DEL  REMOVE", "R  RESTORE",
    };

    private static readonly string[] HeldKeys = { "W S  PUSH PULL", "Q E  TURN", "LET GO  DROP", "FLICK AS YOU LET GO  THROW" };

    private static readonly List<(string, string)> KeyPairs = Hud.Pairs(Keys);
    private static readonly List<(string, string)> HeldPairs = Hud.Pairs(HeldKeys);

    /// <summary>The key guide along the bottom of the screen while editor mode is on: an overlay card of key chips.</summary>
    private static void Controls(WorldEditor ed)
    {
        List<(string, string)> keys = ed.Grabbing ? HeldPairs : KeyPairs;
        string title = ed.Grabbing ? "HOLDING" : "EDITOR MODE";

        // SELECT FROM narrows what a click can pick; said here, so a filter left on isn't mistaken for a broken editor.
        string filter = ed.PickFilter.Length > 0 ? "SELECTING " + ed.PickFilter + " ONLY" : "";
        float want = 0f;
        foreach ((string k, string a) in keys)
            want += Hud.ChipW(k) + Hud.U(6) + TextW(a, Hud.Sc(Hud.BodyScale * 0.92f)) + Hud.U(14);
        float inner = MathF.Min(want, W * 0.7f);
        float w = inner + Hud.U(Hud.Pad) * 2f;
        float h = Hud.Height(Hud.Keys(0f, 0f, inner, keys, false));
        float x = MathF.Round((W - w) * 0.5f);
        float y = MathF.Round(H - h - S(28));
        float top = Hud.Card(x, y, w, h, title, filter, Theme.Signal, 1f);
        Hud.Keys(x + Hud.U(Hud.Pad), top, inner, keys);
    }

    private static Rgba ColourOf(Thing t, bool removed) => ColourOf(t.Kind, removed);

    private static Rgba ColourOf(ThingKind kind, bool removed)
    {
        Rgba c = Things.Colors[(int)kind];
        return removed ? Rgba.Mix(c, Dormant, 0.7f) : c;
    }

    /// <summary>
    /// Who rides in each marked vehicle, read fresh each frame, and everyone aboard any vehicle an ESP marks (this one's
    /// or THREAT ESP's), so their own markers stand down.
    /// </summary>
    private static void CollectCrews(WorldEditor ed)
    {
        foreach (List<Pawn> l in _crews.Values)
        {
            l.Clear();
            _pool.Push(l);
        }

        _crews.Clear();
        _aboard.Clear();
        if (ed.Shows(ThingKind.Vehicle))
        {
            foreach (Thing t in ed.Visible)
            {
                if (t.Kind != ThingKind.Vehicle || !t.Alive || t.Actor is not Vehicle || Crew.Of(t.Actor, _scratch) == 0)
                    continue;

                List<Pawn> crew = _pool.Count > 0 ? _pool.Pop() : new List<Pawn>();
                crew.AddRange(_scratch);
                _crews[t.Ptr] = crew;
                foreach (Pawn p in _scratch)
                    _aboard.Add(p.Ptr);
            }
        }

        Population.I.AddCrews(_aboard);
    }

    private static void Grow(ref float a, ref float b, float min)
    {
        if (b - a >= min)
            return;

        float mid = (a + b) * 0.5f;
        a = mid - min * 0.5f;
        b = mid + min * 0.5f;
    }

    // ---- markers

    /// <summary>Every bracket and diamond of one kind and state in a single batch.</summary>
    private static void Markers(ThingKind kind, bool removed)
    {
        float thick = MathF.Max(1.5f, L(1.75f));
        bool any = false;
        foreach (Shown s in _shown)
        {
            if (s.T.Kind != kind || s.Removed != removed)
                continue;

            any = true;
            float settle = Settle(s.Age);
            float cx = (s.X0 + s.X1) * 0.5f;
            if (s.Framed)
            {
                // New markers settle in from a little wider, like the menu's rows easing into place.
                float pad = L(3f) + L(10f) * (1f - settle);
                Brackets(s.X0 - pad, s.Y0 - pad, s.X1 + pad, s.Y1 + pad, L(12f), thick);
            }
            else
            {
                Diamond(cx, s.Y0 - L(7f), L(4.5f) * (0.5f + 0.5f * settle));
            }
        }

        if (any)
            Flush(ColourOf(kind, removed));
    }

    /// <summary>Thin lines from a notch at the top of the screen to each marked object, one batch per colour.</summary>
    private static void Tracers()
    {
        float ox = W * 0.5f;
        Rect(ox - L(16), 0f, L(32), MathF.Max(2f, L(2)), Theme.Accent, 0.9f);
        float thick = MathF.Max(1f, L(1f));
        for (int k = 0; k < Things.KindCount * 2; k++)
        {
            var kind = (ThingKind)(k >> 1);
            bool removed = (k & 1) == 1;
            bool any = false;
            foreach (Shown s in _shown)
            {
                if (s.T.Kind != kind || s.Removed != removed)
                    continue;
                any = true;
                Line(ox, L(2), (s.X0 + s.X1) * 0.5f, s.Framed ? s.Y0 - L(3) : s.Y0 - L(12), thick);
            }

            if (any)
                Flush(ColourOf(kind, removed), rim: false);
        }
    }

    // ---- labels

    private static float MeterH => MathF.Max(3f, L(3.5f));

    private static void Label(Shown s, bool details)
    {
        if (s.Tier is Tier.Mark or Tier.Card)
            return;

        Thing t = s.T;
        Rgba col = ColourOf(t, s.Removed);
        float settle = Settle(s.Age);
        float cx = (s.X0 + s.X1) * 0.5f;
        string name = s.Removed ? t.Name + "  REMOVED" : t.Name;
        string info = details ? Metres(s.Dist) : "";
        float top = s.Framed ? s.Y0 - L(6) : s.Y0 - L(14);
        if (_crews.ContainsKey(t.Ptr))
            top -= L(12);

        bool meter = s.Tier == Tier.Full && details && t.Actor is Pawn { HealthMax: > 0 };
        if (!meter)
        {
            Plate(cx, top, name, info, col, settle);
            return;
        }

        var p = (Pawn)t.Actor;
        float frac = Math.Max(0, p.Health) / (float)p.HealthMax;
        var (x, w, _) = Plate(cx, top - MeterH - L(2), name, info, col, settle);
        Meter(x, top - MeterH, w, MeterH, frac, Trail(t.Ptr, frac), col, 8);
    }

    /// <summary>The lock and card on whatever is nearest the middle of the screen.</summary>
    private static void FocusCard(Shown s, bool details)
    {
        Thing t = s.T;
        Rgba col = ColourOf(t, s.Removed);
        Lock(s.X0, s.Y0, s.X1, s.Y1, Theme.Accent, FocusAge);

        _lines.Clear();
        string where = Metres(s.Dist) + " AWAY";
        float health = -1f, trail = -1f;
        if (t.Actor is Pawn p && p.HealthMax > 0)
        {
            where += "   " + Math.Max(0, p.Health) + " / " + p.HealthMax + " HP";
            health = Math.Max(0, p.Health) / (float)p.HealthMax;
            trail = Trail(t.Ptr, health);
        }

        _lines.Add((where, Theme.Mid));
        if (_crews.TryGetValue(t.Ptr, out List<Pawn>? crew))
            Crew.Lines(crew, _lines);
        if (details)
            _lines.Add((ClassName(t.Actor), Theme.Low));
        if (s.Removed)
            _lines.Add(("REMOVED", Theme.Signal));

        float pad = L(4);
        Card((s.X0 + s.X1) * 0.5f, (s.Y0 + s.Y1) * 0.5f, s.X1 + pad, s.Y0 - pad, t.KindLabel, t.Name, _lines, col, health, trail, FocusAge);
    }

    private static string ClassName(Actor a) => a.Class is { } c ? c.Name.ToString().ToUpperInvariant() : "";

    // ---- aim and selection

    /// <summary>A white lock on what SELECT AIMED would take, with its name beneath.</summary>
    private static void Bracket(Thing t)
    {
        Vector3 at = t.Actor.Location;
        if (!WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1))
            return;

        if (t.Ptr != _aimPtr)
        {
            _aimPtr = t.Ptr;
            _aimSince = Time;
        }

        float age = (float)(Time - _aimSince);
        Lock(x0, y0, x1, y1, Theme.Hi, age);
        float h = TextH(NameScale * Size) + L(3);
        Plate((x0 + x1) * 0.5f, y1 + L(12) + h, "SELECT  " + t.Name, t.KindLabel, Theme.Hi, Settle(age));
    }

    private static void DrawSelected(WorldEditor ed, Thing t)
    {
        Vector3 at = t.Actor.Location;
        Span<Vector2> c = stackalloc Vector2[8];
        bool front = WorldView.Corners(at + t.Min, at + t.Max, c);
        bool seen = WorldView.Bounds(at + t.Min, at + t.Max, out float x0, out float y0, out float x1, out float y1);
        if (!front || !seen)
        {
            Edge(t.Centre, Theme.Accent, t.Name + "  " + Metres(Vector3.Distance(WorldView.Eye, t.Centre)));
            return;
        }

        Rgba accent = Theme.Accent;
        if ((x1 - x0) * (y1 - y0) < W * H * 0.15f)
            Wash(x0, y0, x1, y1, accent, 0.05f);

        // The twelve edges of the box, corners whose index differs by one bit (one axis), with a node on each corner.
        float thick = MathF.Max(1.5f, L(2f));
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                int j = i | bit;
                if (j != i)
                    Line(c[i].X, c[i].Y, c[j].X, c[j].Y, thick);
            }
        }

        float node = MathF.Max(2f, L(3f));
        for (int i = 0; i < 8; i++)
            Diamond(c[i].X, c[i].Y, node);
        Flush(accent);

        _lines.Clear();
        Actor a = t.Actor;
        string where = Metres(Vector3.Distance(WorldView.Eye, t.Centre)) + " AWAY";
        float health = -1f, trail = -1f;
        if (a is Pawn p && p.HealthMax > 0)
        {
            where += "   " + Math.Max(0, p.Health) + " / " + p.HealthMax + " HP";
            health = Math.Max(0, p.Health) / (float)p.HealthMax;
            trail = Trail(t.Ptr, health);
        }

        _lines.Add((where, Theme.Mid));
        if (Crew.Of(a, _scratch) > 0)
            Crew.Lines(_scratch, _lines);
        _lines.Add(("AT " + Fmt.Position(a.Location), Theme.Low));
        _lines.Add((ClassName(a), Theme.Low));
        string flags = Flags(ed, t);
        if (flags.Length > 0)
            _lines.Add((flags, Theme.Signal));

        Card((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, x1, y0, "SELECTED  " + t.KindLabel, t.Name, _lines, accent, health, trail, Age(t.Ptr));
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
}
