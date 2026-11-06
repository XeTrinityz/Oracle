using BmSDK.Engine;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>Everything drawn over the game, in order: fades, HUD elements, the running list, the menu, toasts.</summary>
internal static class Overlay
{
    private const float ToastSeconds = 1.8f;
    private const float NoticeSeconds = 3f;

    private const float MaxFrame = 0.1f;

    private static FrameTimer _frame;
    private static double _noticeDue;
    private static float _menuAnim;
    private static float _radialAnim;
    private static float _toastLeft;
    private static float _toastLength = 1f;
    private static string _toastTitle = "";
    private static string _toastState = "";
    private static bool _toastOn;

    public static bool ShowRunningList;

    /// <summary>Set while a shared hotkey tries a command that may fail quietly, because another one follows it.</summary>
    public static bool Muted;

    /// <summary>Brief feedback for a hotkey. Hidden while the menu or radial is open, which show the change themselves, and under a notice.</summary>
    public static void Toast(string title, string state, bool on)
    {
        if (!Muted && !Menu.IsOpen && !Radial.IsLive && _noticeDue <= Clock.Now)
            Show(title, state, on, ToastSeconds);
    }

    /// <summary>A message the player must see, even over the menu. A hotkey's own DONE or FAILED won't replace it.</summary>
    public static void Notice(string title, string state, bool on)
    {
        if (Muted)
            return;

        _noticeDue = Clock.Now + NoticeSeconds;
        Show(title, state, on, NoticeSeconds);
    }

    private static void Show(string title, string state, bool on, float seconds)
    {
        _toastTitle = title;
        _toastState = state;
        _toastOn = on;
        _toastLeft = seconds;
        _toastLength = seconds;
    }

    public static void Draw(Canvas? canvas)
    {
        bool paused = World.Paused;
        if (Menu.IsOpen && (paused || !Menu.OwnsScreen))
        {
            Menu.Close();
            _menuAnim = 0f;
        }

        if (Radial.IsLive && paused)
        {
            Radial.Close();
            _radialAnim = 0f;
        }

        Dt = _frame.Step(MaxFrame);
        Time += Dt;
        Frame++;

        Theme.Update(Dt);
        Fader.Advance(Dt);
        Menu.FlashLeft = Math.Max(0f, Menu.FlashLeft - Dt);
        _toastLeft = Math.Max(0f, _toastLeft - Dt);

        float target = Menu.IsOpen ? 1f : 0f;
        float speed = Menu.IsOpen ? 5f : 7f;
        _menuAnim = _menuAnim < target ? Math.Min(target, _menuAnim + Dt * speed) : Math.Max(target, _menuAnim - Dt * speed);

        // The radial snaps open faster than the menu, since it's held for a moment at a time, and leaves faster still.
        float radialTarget = Radial.Visible ? 1f : 0f;
        float radialSpeed = Radial.Visible ? 8f : 10f;
        _radialAnim = _radialAnim < radialTarget ? Math.Min(radialTarget, _radialAnim + Dt * radialSpeed) : Math.Max(radialTarget, _radialAnim - Dt * radialSpeed);

        bool list = ShowRunningList && _menuAnim <= 0f && _radialAnim <= 0f && !paused && Catalog.Ready && Catalog.ActiveCount > 0;
        bool hud = !paused && Fader.IsClear && Catalog.Ready && AnyHud();
        if (canvas == null || (_menuAnim <= 0f && _radialAnim <= 0f && _toastLeft <= 0f && !list && !hud && Fader.Level <= 0f))
            return;

        if (!Begin(canvas, out Font? font))
            return;

        Hud.BeginFrame();
        try
        {
            if (Fader.Level > 0f)
                DrawFade();

            if (hud)
                DrawHud(canvas);

            if (list)
                DrawRunningList();

            if (_menuAnim > 0f)
                MenuView.Draw(_menuAnim);

            if (_radialAnim > 0f)
                RadialView.Draw(_radialAnim);

            if (_toastLeft > 0f)
                DrawToast();
        }
        finally
        {
            canvas.Font = font;
        }
    }

    /// <summary>Most HUDs give way to the menu as it opens; a few are what the menu is working on, so they stay.</summary>
    private static bool Shows(Module m) => m.HasHud && (_menuAnim <= 0f || m.HudWithMenu);

    private static bool AnyHud()
    {
        foreach (Module m in App.Modules)
        {
            if (Shows(m))
                return true;
        }

        return false;
    }

    private static void DrawHud(Canvas canvas)
    {
        foreach (Module m in App.Modules)
        {
            if (!Shows(m))
                continue;

            Alpha = 1f;
            try
            {
                m.Draw(canvas);
            }
            catch (Exception ex)
            {
                Log.Error("hud " + m.Name, m.Name + " HUD", ex);
            }
        }
    }

    private static void DrawFade()
    {
        float level = Math.Clamp(Fader.Level, 0f, 1f);
        Alpha = 1f;
        Rect(0f, 0f, Gfx.W, Gfx.H, Theme.Black, level);
        if (Fader.Caption.Length == 0)
            return;

        float cx = Gfx.W * 0.5f;
        float cy = Gfx.H * 0.5f;
        DrawSpinner(cx, cy - S(74), level);
        TextCentre(cx, cy - S(18), Fader.Caption, Theme.Accent, 0.86f, level);

        float bw = S(220);
        float bx = cx - bw * 0.5f;
        float by = cy + S(14);
        Rect(bx, by, bw, S(2), Theme.White, 0.08f * level);
        float seg = bw * 0.28f;
        float p = (float)(Time * 0.9 % 1.0);
        float sx = bx + (bw + seg) * p - seg;
        float x0 = Math.Max(bx, sx);
        float x1 = Math.Min(bx + bw, sx + seg);
        if (x1 > x0)
            Rect(x0, by, x1 - x0, S(2), Theme.Accent, 0.9f * level);
    }

    /// <summary>The game's own spinning bat symbol, a strip of square frames, tinted with the menu colour.</summary>
    private const string SpinnerTexture = "BmFonts.Loading_DVDIcon";
    private const float SpinnerFps = 14f;

    /// <summary>Draws the spinner centred on a point; <paramref name="size"/> is in unscaled pixels.</summary>
    public static void DrawSpinner(float cx, float cy, float level, float size = 64f)
    {
        if (Find(SpinnerTexture) is not { } strip || strip.SizeY <= 0)
            return;

        int frames = Math.Max(1, strip.SizeX / strip.SizeY);
        int frame = (int)(Time * SpinnerFps) % frames;
        size = S(size);
        Rgba tint = Rgba.Mix(Theme.Accent, Theme.White, 0.3f);
        Sprite(strip, cx - size * 0.5f, cy - size * 0.5f, size, size, frame * strip.SizeY, 0f, strip.SizeY, strip.SizeY, tint, level);
    }

    private const int MaxRunningRows = 14;
    private static readonly List<(string Name, string State)> _running = new();

    /// <summary>
    /// RUNNING: what's switched on, as a card at the top of the right dock. Each row carries the menu's running tick,
    /// with a setting's value on the right, and the rows cascade in after the card.
    /// </summary>
    private static void DrawRunningList()
    {
        Alpha = 1f;
        _running.Clear();
        foreach (Feature f in Catalog.All)
        {
            if (f.Active)
                _running.Add((f.Label, f is Toggle ? "" : f.State));
        }

        if (_running.Count == 0)
            return;

        float ls = Hud.Sc(Hud.BodyScale), ss = Hud.Sc(Hud.SmallScale);
        int shown = Math.Min(_running.Count, MaxRunningRows);
        int more = _running.Count - shown;
        float w = Hud.U(190);
        for (int i = 0; i < shown; i++)
        {
            (string name, string state) = _running[i];
            w = Math.Max(w, TextW(name, ls) + (state.Length > 0 ? TextW(state, ss) + Hud.U(14) : 0f) + Hud.U(Hud.Pad) * 2f + Hud.U(10));
        }

        float lh = Hud.U(19);
        float h = Hud.Height(lh * (shown + (more > 0 ? 1 : 0)) - Hud.U(4));
        var (x, y, age) = Hud.Place("running", Hud.Side.Right, 0, w, h);
        float top = Hud.Card(x, y, w, h, "RUNNING", _running.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Theme.Accent, age);

        float card = Alpha;
        float left = x + Hud.U(Hud.Pad), right = x + w - Hud.U(Hud.Pad);
        for (int i = 0; i < shown; i++)
        {
            (string name, string state) = _running[i];
            Alpha = card * (MenuView.Effects ? Ease.Out(Math.Clamp((age - 0.1f - i * 0.035f) / 0.2f, 0f, 1f)) : 1f);
            float cy = top + lh * i + lh * 0.5f - Hud.U(2);
            Rect(left, cy - lh * 0.5f + Hud.U(4), MathF.Max(2f, Hud.U(2)), lh - Hud.U(8), Theme.Accent, 0.8f);
            TextMid(left + Hud.U(10), cy + Hud.U(0.5f), name, Theme.Mid, ls);
            if (state.Length > 0)
                TextRight(right, cy + Hud.U(0.5f), state, Theme.Accent, ss);
        }

        Alpha = card;
        if (more > 0)
            TextMid(left + Hud.U(10), top + lh * shown + lh * 0.5f - Hud.U(2), "+" + more + " MORE", Theme.Low, ss);
    }

    /// <summary>
    /// A toast or notice: a small card at the top of the screen in the menu's style, the title tracked and the new
    /// state in a chip, solid in the menu colour when something turned on.
    /// </summary>
    private static void DrawToast()
    {
        Alpha = Ease.Out(Math.Min(1f, _toastLeft / 0.3f) * Math.Min(1f, (_toastLength - _toastLeft) / 0.15f));
        float age = _toastLength - _toastLeft;

        Rgba col = _toastOn ? Theme.Accent : Theme.Low;
        float ts = Hud.Sc(0.78f);
        float spacing = 1.2f * Hud.Size;
        float tw = TextW(_toastTitle, ts) + (Theme.LowEnd ? 0f : _toastTitle.Length * S(spacing));
        string state = MenuView.Decode(_toastState, age);
        float cw = Hud.ChipW(_toastState);
        float pad = Hud.U(16);
        float w = pad + tw + Hud.U(14) + cw + pad;
        float h = Hud.U(40);
        float x = MathF.Round((Gfx.W - w) * 0.5f);
        float y = MathF.Round(S(150) - Hud.U(10) * (1f - Alpha));

        Rect(x + Hud.U(3), y + Hud.U(4), w, h, Theme.Black, 0.3f);
        Rect(x, y, w, h, Theme.Panel, 0.92f);
        Rect(x, y, w, MathF.Max(1f, S(1)), col, 0.8f);
        Hud.Brackets(x, y, w, h, col);
        Hud.Tracked(x + pad, y + (h - TextH(ts)) * 0.5f + Hud.U(1), _toastTitle, Theme.Hi, ts, spacing);
        float cx = x + pad + tw + Hud.U(14);
        if (_toastOn)
            Hud.Chip(cx, y + h * 0.5f, state, col, 1f, Theme.Black);
        else
            Hud.Chip(cx, y + h * 0.5f, state, col, 0.1f);
    }
}
