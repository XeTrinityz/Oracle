using BmSDK;
using BmSDK.Engine;
using FColor = BmSDK.GameObject.FColor;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// Filled shapes the Canvas has no call for: arcs, rings, discs and slanted quads. Each shape is built from
/// quads and sent to the game in one Canvas.DrawTris call, so a whole ring costs one script call, not one per
/// segment. Angles are in degrees on screen: 0 points right and they run clockwise, because screen y runs down.
/// </summary>
internal static unsafe class Shapes
{
    /// <summary>Floats per triangle: three corners of x, y, u, v, the native FCanvasUVTri layout (48 bytes).</summary>
    private const int TriFloats = 12;

    /// <summary>The longest an arc segment's outer edge may be, in 1080p pixels, before the arc gets another.</summary>
    private const float SegmentPx = 7f;

    private const float Rad = MathF.PI / 180f;

    private static TArray<Canvas.FCanvasUVTri>? _tris;
    private static float[] _buf = new float[TriFloats * 512];
    private static int _count;
    private static bool _broken;

    /// <summary>
    /// The colour under the shapes being drawn. AK's DrawTris ignores alpha and always draws solid (seen in game,
    /// 2026-11-05), so a translucent shape is drawn as the solid colour it would blend to over this. Callers set it
    /// to whatever they've drawn beneath; the global <see cref="Gfx.Alpha"/> fades shapes toward it too.
    /// </summary>
    public static Rgba Under = new(6, 13, 20);

    /// <summary>
    /// True once DrawTris has failed. Shapes then fall back to one rotated tile per quad: rougher edges and more
    /// calls, but the radial still draws.
    /// </summary>
    public static bool Broken => _broken;

    /// <summary>Frees the triangle buffer. Called when the mod unloads.</summary>
    public static void Release()
    {
        TArray<Canvas.FCanvasUVTri>? tris = _tris;
        _tris = null;
        try
        {
            tris?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Info("Shapes: could not free the triangle buffer: " + ex.Message);
        }
    }

    // ---- shapes -------------------------------------------------------------------------------------------

    /// <summary>A band between two radii from angle <paramref name="a0"/> to <paramref name="a1"/>. An inner radius of 0 fills to the centre.</summary>
    public static void Arc(float cx, float cy, float r0, float r1, float a0, float a1, Rgba col, float alpha)
    {
        if (r1 <= r0 || a1 <= a0 || !Visible(col, alpha))
            return;

        AddArc(cx, cy, r0, r1, a0, a1);
        Flush(col, alpha);
    }

    public static void Ring(float cx, float cy, float r0, float r1, Rgba col, float alpha) =>
        Arc(cx, cy, r0, r1, 0f, 360f, col, alpha);

    public static void Disc(float cx, float cy, float r, Rgba col, float alpha) =>
        Arc(cx, cy, 0f, r, 0f, 360f, col, alpha);

    /// <summary>
    /// An arc whose brightness fades across its width: <paramref name="bands"/> thin arcs from
    /// <paramref name="inner"/> alpha at r0 to <paramref name="outer"/> alpha at r1.
    /// </summary>
    public static void FadeArc(float cx, float cy, float r0, float r1, float a0, float a1, Rgba col, float inner, float outer, int bands)
    {
        bands = Theme.LowEnd ? Math.Min(bands, 3) : bands;
        float step = (r1 - r0) / bands;
        for (int i = 0; i < bands; i++)
            Arc(cx, cy, r0 + step * i, r0 + step * (i + 1), a0, a1, col, inner + (outer - inner) * ((i + 0.5f) / bands));
    }

    /// <summary>A straight bar from one point to another, <paramref name="thick"/> wide.</summary>
    public static void Line(float x0, float y0, float x1, float y1, float thick, Rgba col, float alpha)
    {
        if (!Visible(col, alpha))
            return;

        AddLine(x0, y0, x1, y1, thick);
        Flush(col, alpha);
    }

    /// <summary>
    /// Adds a bar to a batch that <see cref="FlushLines"/> draws in one call, for many lines of one colour (the
    /// editor's boxes). Nothing else may be drawn with Shapes between the two.
    /// </summary>
    public static void QueueLine(float x0, float y0, float x1, float y1, float thick) => AddLine(x0, y0, x1, y1, thick);

    /// <summary>Draws every queued line, solid. Lines over the game world have no known colour under them to blend with.</summary>
    public static void FlushLines(Rgba col)
    {
        if (_count > 0)
            Flush(col with { A = 255 }, 1f);
    }

    /// <summary>A solid triangle.</summary>
    public static void Triangle(float x0, float y0, float x1, float y1, float x2, float y2, Rgba col, float alpha)
    {
        if (!Visible(col, alpha))
            return;

        AddQuad(x0, y0, x1, y1, x2, y2, x2, y2);
        Flush(col, alpha);
    }

    /// <summary>
    /// Short radial strokes around a circle, all in one call: <paramref name="count"/> ticks spaced evenly from
    /// <paramref name="start"/>, each <paramref name="width"/> pixels wide.
    /// </summary>
    public static void Ticks(float cx, float cy, float r0, float r1, int count, float start, float width, Rgba col, float alpha)
    {
        if (count <= 0 || !Visible(col, alpha))
            return;

        float step = 360f / count;
        for (int i = 0; i < count; i++)
        {
            float a = (start + step * i) * Rad;
            float c = MathF.Cos(a);
            float s = MathF.Sin(a);
            AddLine(cx + c * r0, cy + s * r0, cx + c * r1, cy + s * r1, width);
        }

        Flush(col, alpha);
    }

    /// <summary>A dashed arc: dashes of <paramref name="dash"/> degrees with <paramref name="gap"/> degrees between, in one call.</summary>
    public static void Dashes(float cx, float cy, float r0, float r1, float a0, float a1, float dash, float gap, Rgba col, float alpha)
    {
        if (!Visible(col, alpha) || dash <= 0f)
            return;

        for (float a = a0; a < a1; a += dash + gap)
            AddArc(cx, cy, r0, r1, a, Math.Min(a1, a + dash));
        Flush(col, alpha);
    }

    // ---- building ---------------------------------------------------------------------------------------

    private static bool Visible(Rgba col, float alpha) => col.A * alpha * Gfx.Alpha >= 0.5f;

    private static void AddArc(float cx, float cy, float r0, float r1, float a0, float a1)
    {
        float seg = S(Theme.LowEnd ? SegmentPx * 2f : SegmentPx);
        int n = Math.Clamp((int)MathF.Ceiling((a1 - a0) * Rad * r1 / Math.Max(1f, seg)), 1, 180);
        float step = (a1 - a0) / n;
        float c0 = MathF.Cos(a0 * Rad);
        float s0 = MathF.Sin(a0 * Rad);
        for (int i = 1; i <= n; i++)
        {
            float a = (a0 + step * i) * Rad;
            float c1 = MathF.Cos(a);
            float s1 = MathF.Sin(a);
            AddQuad(cx + c0 * r0, cy + s0 * r0, cx + c0 * r1, cy + s0 * r1, cx + c1 * r1, cy + s1 * r1, cx + c1 * r0, cy + s1 * r0);
            c0 = c1;
            s0 = s1;
        }
    }

    private static void AddLine(float x0, float y0, float x1, float y1, float thick)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0.001f)
            return;

        float nx = -dy / len * thick * 0.5f;
        float ny = dx / len * thick * 0.5f;

        // Corners go across the bar first (a to b), then along it (b to c), the same order an arc segment uses.
        AddQuad(x0 - nx, y0 - ny, x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny);
    }

    /// <summary>
    /// Queues a quad as two triangles. Corners run a, b, c, d around it: a to b crosses the shape (an arc
    /// segment's inner to outer edge) and b to c runs along it. The rotated-tile fallback depends on that order.
    /// </summary>
    private static void AddQuad(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
    {
        int need = (_count + 2) * TriFloats;
        if (need > _buf.Length)
            Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));

        int i = _count * TriFloats;
        Corner(i, ax, ay);
        Corner(i + 4, bx, by);
        Corner(i + 8, cx, cy);
        Corner(i + 12, ax, ay);
        Corner(i + 16, cx, cy);
        Corner(i + 20, dx, dy);
        _count += 2;
    }

    private static void Corner(int i, float x, float y)
    {
        _buf[i] = x;
        _buf[i + 1] = y;
        _buf[i + 2] = 0.5f;
        _buf[i + 3] = 0.5f;
    }

    // ---- drawing ----------------------------------------------------------------------------------------

    private static void Flush(Rgba col, float alpha)
    {
        int n = _count;
        _count = 0;
        if (n == 0 || WhiteTexture is not { } white)
            return;

        float t = Math.Clamp(col.A / 255f * alpha * Gfx.Alpha, 0f, 1f);
        var solid = new Rgba((byte)(Under.R + (col.R - Under.R) * t), (byte)(Under.G + (col.G - Under.G) * t), (byte)(Under.B + (col.B - Under.B) * t));
        const byte a = 255;
        col = solid;
        if (_broken)
        {
            DrawTiles(white, n, col, a);
            return;
        }

        try
        {
            _tris ??= new TArray<Canvas.FCanvasUVTri>(Math.Max(512, n));

            // The array only ever grows: Push blank triangles once when it's too small, then set the count in its
            // header (data pointer, count, capacity) for each shape, which costs nothing per triangle.
            if (_tris.Capacity < n)
                _tris.Push(new Canvas.FCanvasUVTri[n - _tris.Count]);
            if (_tris.Capacity < n)
                throw new InvalidOperationException("the triangle buffer didn't grow");

            *(int*)(_tris.Ptr + 8) = n;
            float* dst = *(float**)_tris.Ptr;
            if (dst == null)
                throw new InvalidOperationException("the triangle buffer has no memory");

            fixed (float* src = _buf)
                Buffer.MemoryCopy(src, dst, (long)n * TriFloats * sizeof(float), (long)n * TriFloats * sizeof(float));

            C.DrawTris(white, _tris, new FColor { R = col.R, G = col.G, B = col.B, A = a });
        }
        catch (Exception ex)
        {
            _broken = true;
            Log.Info("Shapes: DrawTris failed (" + ex.Message + "), drawing the radial with rotated tiles instead.");
            DrawTiles(white, n, col, a);
        }
    }

    /// <summary>
    /// The fallback: each queued quad as one tile rotated to match it, sized from the averages of its opposite
    /// edges. Exact for rectangles and close for the thin segments arcs are made of.
    /// </summary>
    private static void DrawTiles(Texture2D white, int triangles, Rgba col, byte a)
    {
        try
        {
            C.SetDrawColor(col.R, col.G, col.B, a);
            for (int q = 0; q + 1 < triangles; q += 2)
            {
                int i = q * TriFloats;
                float ax = _buf[i], ay = _buf[i + 1];
                float bx = _buf[i + 4], by = _buf[i + 5];
                float cx = _buf[i + 8], cy = _buf[i + 9];
                float dx = _buf[i + 20], dy = _buf[i + 21];

                // Along the shape: a to d and b to c. Across it: a to b and d to c.
                float ux = (dx - ax + cx - bx) * 0.5f;
                float uy = (dy - ay + cy - by) * 0.5f;
                float vx = (bx - ax + cx - dx) * 0.5f;
                float vy = (by - ay + cy - dy) * 0.5f;
                float len = MathF.Sqrt(ux * ux + uy * uy);
                float thick = MathF.Sqrt(vx * vx + vy * vy);
                if (len < 0.5f || thick < 0.5f)
                    continue;

                float mx = (ax + bx + cx + dx) * 0.25f;
                float my = (ay + by + cy + dy) * 0.25f;
                float yaw = MathF.Atan2(uy, ux) / Rad;
                C.SetPos(mx - len * 0.5f, my - thick * 0.5f, 0f);
                C.DrawRotatedTile(white, Rotator.FromRuu(0, (int)(yaw * 65536f / 360f), 0), len + 0.5f, thick, 0f, 0f, 32f, 32f, 0.5f, 0.5f);
            }
        }
        catch (Exception ex)
        {
            Log.Throttled("shapes tiles", "Shapes: rotated tiles failed too: " + ex.Message);
        }
    }
}
