using BmSDK.Engine;
using FLinearColor = BmSDK.GameObject.FLinearColor;

namespace CeZeY.Oracle;

/// <summary>
/// Immediate-mode drawing on the game's Canvas, scaled from a 1080p layout. Every Canvas call is a full
/// UnrealScript function call, so text is drawn as whole strings and sizes are cached; per-letter spacing
/// is kept for the few places it's worth the cost.
/// </summary>
internal static class Gfx
{
    private const int MaxCachedWidths = 4096;

    private static readonly float[] Linear = BuildLinear();
    private static readonly Dictionary<(string, float), float> _widths = new();
    private static readonly Dictionary<float, float> _heights = new();
    private static readonly Dictionary<int, float> _anim = new();
    private static Texture2D? _white;
    private static Font? _font;
    private static Font? _engineFont;

    /// <summary>The menu font everything falls back to, and the one other fonts are sized to match.</summary>
    public const string DefaultFont = "BmFonts.EngineSmallFont";

    private static string _fontPath = DefaultFont;
    private static float _fontNorm = 1f;
    private static bool _measureFont = true;
    private static double _fontRetry;

    public static Canvas C = null!;
    public static float Scale = 1f;
    public static float Alpha = 1f;
    public static float Dt;
    public static double Time;

    public static float W => C.ClipX;

    public static float H => C.ClipY;

    public static float S(float v) => v * Scale;

    /// <summary>The plain white texture every rectangle and shape is drawn with. Null until it has loaded.</summary>
    public static Texture2D? WhiteTexture => _white is { IsValid: true } ? _white : null;

    /// <summary>Selects the canvas for this frame. Returns false until the drawing resources have loaded.</summary>
    public static bool Begin(Canvas canvas, out Font? previous)
    {
        previous = null;
        if (_white is not { IsValid: true })
            _white = Game.FindObject<Texture2D>("EngineResources.WhiteSquareTexture");
        if (_engineFont is not { IsValid: true })
            _engineFont = Game.FindObject<Font>(DefaultFont);
        if (_font is not { IsValid: true } && Time >= _fontRetry)
            FindFont();
        if (_white == null)
            return false;

        C = canvas;
        Scale = Math.Clamp(canvas.ClipY / 1080f, 0.75f, 2.5f);
        Alpha = 1f;
        previous = canvas.Font;
        Font? font = _font is { IsValid: true } ? _font : _engineFont;
        if (font != null)
        {
            if (_measureFont)
                MeasureFont(canvas, font);
            canvas.Font = font;
        }

        return true;
    }

    /// <summary>Switches the menu font by its object path. A font that isn't loaded falls back to the engine font.</summary>
    public static void UseFont(string path)
    {
        _fontPath = path.Length == 0 ? DefaultFont : path;
        _font = null;
        _fontRetry = 0;
        _measureFont = true;
    }

    private static void FindFont()
    {
        Font? found = _fontPath == DefaultFont ? null : Game.FindObject<Font>(_fontPath);
        if (found == null)
            _fontRetry = Time + 2.0;   // not loaded (yet); the engine font stands in, and this is tried again
        if (found != _font)
        {
            _font = found;
            _measureFont = true;
        }
    }

    /// <summary>
    /// Fonts come in very different sizes, so each one is scaled to draw as tall as the engine font at the same
    /// scale. Every cached width and height is for the old font, so they go too.
    /// </summary>
    private static void MeasureFont(Canvas canvas, Font font)
    {
        _measureFont = false;
        _widths.Clear();
        _heights.Clear();
        _fontNorm = 1f;
        if (_engineFont == null || font == _engineFont)
            return;

        canvas.Font = _engineFont;
        canvas.TextSize("AQ", out _, out float reference, 1f, 1f);
        canvas.Font = font;
        canvas.TextSize("AQ", out _, out float height, 1f, 1f);
        if (reference > 0f && height > 0f)
            _fontNorm = Math.Clamp(reference / height, 0.05f, 20f);
    }

    public static void Rect(float x, float y, float w, float h, Rgba col, float alpha = 1f)
    {
        if (w <= 0f || h <= 0f)
            return;

        C.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        var color = new FLinearColor { R = Linear[col.R], G = Linear[col.G], B = Linear[col.B], A = col.A / 255f * alpha * Alpha };
        C.DrawTile(_white!, MathF.Round(w), MathF.Round(h), 0f, 0f, 32f, 32f, color, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    /// <summary>Draws a whole texture into a box, untinted.</summary>
    public static void Image(Texture2D tex, float x, float y, float w, float h, float alpha = 1f) =>
        Sprite(tex, x, y, w, h, 0f, 0f, tex.SizeX, tex.SizeY, Theme.White, alpha);

    /// <summary>Draws part of a texture (u, v, width, height in texels) into a box, tinted.</summary>
    public static void Sprite(Texture2D tex, float x, float y, float w, float h, float u, float v, float ul, float vl, Rgba tint, float alpha = 1f)
    {
        if (w <= 0f || h <= 0f)
            return;

        C.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        var color = new FLinearColor { R = Linear[tint.R], G = Linear[tint.G], B = Linear[tint.B], A = tint.A / 255f * alpha * Alpha };
        C.DrawTile(tex, MathF.Round(w), MathF.Round(h), u, v, ul, vl, color, false, EngineTypes.EBlendMode.BLEND_Translucent);
    }

    private static readonly Dictionary<string, (Texture2D? Tex, double Retry)> _textures = new();

    /// <summary>
    /// A game texture by path, cached. Null while it isn't loaded; looked up again every few seconds, so a
    /// texture that streams in later still turns up.
    /// </summary>
    public static Texture2D? Find(string path)
    {
        if (_textures.TryGetValue(path, out var entry) && (entry.Tex is { IsValid: true } || Time < entry.Retry))
            return entry.Tex is { IsValid: true } ? entry.Tex : null;

        Texture2D? tex = null;
        try
        {
            tex = Game.FindObject<Texture2D>(path);
        }
        catch (Exception)
        {
        }

        _textures[path] = (tex, Time + 3.0);
        return tex;
    }

    /// <summary>A HUD box: the dark backing with an accent bar down its left edge.</summary>
    public static void Panel(float x, float y, float w, float h, float alpha = 0.8f, float bar = 3f)
    {
        Rect(x, y, w, h, Theme.Panel, alpha);
        Rect(x, y, S(bar), h, Theme.Accent, alpha + 0.15f);
    }

    public static void GradientX(float x, float y, float w, float h, Rgba col, float from, float to)
    {
        int slices = Theme.LowEnd ? 4 : 12;
        for (int i = 0; i < slices; i++)
        {
            float x0 = MathF.Round(x + w * i / slices);
            float x1 = MathF.Round(x + w * (i + 1) / slices);
            Rect(x0, y, x1 - x0, h, col, from + (to - from) * ((i + 0.5f) / slices));
        }
    }

    public static float TextW(string text, float scale)
    {
        if (_widths.Count >= MaxCachedWidths)
            _widths.Clear();

        float size = scale * Scale * _fontNorm;
        if (_widths.TryGetValue((text, size), out float w))
            return w;

        C.TextSize(text, out w, out _, size, size);
        _widths[(text, size)] = w;
        return w;
    }

    public static float TextH(float scale)
    {
        float size = scale * Scale * _fontNorm;
        if (_heights.TryGetValue(size, out float h))
            return h;

        C.TextSize("AQ", out _, out h, size, size);
        _heights[size] = h;
        return h;
    }

    /// <summary>
    /// Draws text. It doesn't measure it: a width is another UnrealScript call, and readouts that change every frame
    /// would pay it every frame. Callers that lay out after the text ask <see cref="TextW"/>.
    /// </summary>
    public static void Text(float x, float y, string text, Rgba col, float scale, float alpha = 1f)
    {
        C.SetDrawColor(col.R, col.G, col.B, (byte)Math.Clamp(col.A * alpha * Alpha, 0f, 255f));
        C.SetPos(MathF.Round(x), MathF.Round(y), 0f);
        float size = scale * Scale * _fontNorm;
        C.DrawText(text, false, size, size, out _);
    }

    /// <summary>Draws text vertically centred on <paramref name="cy"/>.</summary>
    public static void TextMid(float x, float cy, string text, Rgba col, float scale, float alpha = 1f) =>
        Text(x, cy - TextH(scale) * 0.5f, text, col, scale, alpha);

    public static float TextRight(float right, float cy, string text, Rgba col, float scale, float alpha = 1f)
    {
        float w = TextW(text, scale);
        TextMid(right - w, cy, text, col, scale, alpha);
        return w;
    }

    public static void TextCentre(float cx, float y, string text, Rgba col, float scale, float alpha = 1f) =>
        Text(cx - TextW(text, scale) * 0.5f, y, text, col, scale, alpha);

    public static float Spaced(float x, float y, string text, Rgba col, float scale, float spacing, float alpha = 1f)
    {
        float start = x;
        foreach (char ch in text)
        {
            string letter = ch.ToString();
            Text(x, y, letter, col, scale, alpha);
            x += TextW(letter, scale) + S(spacing);
        }
        return x - start - S(spacing);
    }

    /// <summary>Splits off as many words as fit the width. Returns the remainder, empty when all of it fit.</summary>
    public static string FitLine(string text, float width, float scale, out string line)
    {
        if (TextW(text, scale) <= width)
        {
            line = text;
            return "";
        }

        int cut = text.Length;
        while (cut > 0)
        {
            int space = text.LastIndexOf(' ', cut - 1);
            if (space <= 0)
                break;

            cut = space;
            if (TextW(text.Substring(0, cut), scale) <= width)
                break;
        }

        line = text.Substring(0, cut);
        return text.Substring(cut).TrimStart();
    }

    /// <summary>Eases a value toward a target, independent of frame rate. Below 0 means unset, so it jumps straight there.</summary>
    public static float Follow(float value, float target, float rate) =>
        value < 0f ? target : value + (target - value) * Ease.Follow(rate, Dt);

    /// <summary>Eases a keyed value toward a target, independent of frame rate.</summary>
    public static float Approach(int key, float target, float rate)
    {
        if (!_anim.TryGetValue(key, out float v))
            v = target;

        v += (target - v) * Ease.Follow(rate, Dt);
        if (MathF.Abs(target - v) < 0.001f)
            v = target;
        _anim[key] = v;
        return v;
    }

    private static float[] BuildLinear()
    {
        var table = new float[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = MathF.Pow(i / 255f, 2.2f);
        return table;
    }
}
