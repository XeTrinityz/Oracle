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

    private static void DrawRunningList()
    {
        Alpha = 1f;
        const float scale = 0.72f;
        float lh = S(21);
        var names = new List<string>();
        float w = TextW("ORACLE", scale) + S(30);
        foreach (Feature f in Catalog.All)
        {
            if (!f.Active)
                continue;

            string name = f is Toggle ? f.Label : f.Label + "  " + f.State;
            names.Add(name);
            w = Math.Max(w, TextW(name, scale) + S(34));
        }

        float x = Gfx.W - w - S(24);
        float y = S(150);
        float h = S(32) + names.Count * lh + S(8);
        Rect(x, y, w, h, Theme.Panel, 0.75f);
        Rect(x + w - S(2), y, S(2), h, Theme.Accent, 0.9f);
        Text(x + S(12), y + S(9), "ORACLE", Theme.Accent, scale);
        float ly = y + S(32);
        foreach (string n in names)
        {
            Rect(x + S(12), ly + lh * 0.5f - S(2), S(4), S(4), Theme.Accent, 0.8f);
            TextMid(x + S(22), ly + lh * 0.5f, n, Theme.Mid, scale);
            ly += lh;
        }
    }

    private static void DrawToast()
    {
        Alpha = Ease.Out(Math.Min(1f, _toastLeft / 0.3f) * Math.Min(1f, (_toastLength - _toastLeft) / 0.15f));

        const float scale = 0.9f;
        Rgba col = _toastOn ? Theme.Accent : Theme.Low;
        float tw = TextW(_toastTitle, scale);
        float sw = TextW(_toastState, scale);
        float w = tw + sw + S(64);
        float h = S(40);
        float x = (Gfx.W - w) * 0.5f;
        float y = S(150) - S(10) * (1f - Alpha);

        Rect(x + S(4), y + S(5), w, h, Theme.Black, 0.3f);
        Rect(x, y, w, h, Theme.Panel);
        Rect(x, y, S(3), h, col);
        Rect(x, y, w, S(1), col, 0.5f);
        TextMid(x + S(22), y + h * 0.5f, _toastTitle, Theme.Hi, scale);
        TextMid(x + S(22) + tw + S(20), y + h * 0.5f, _toastState, col, scale);
    }
}

/// <summary>Drives the per-frame update from the viewport, which keeps running when game ticks stop.</summary>
internal static class RenderHook
{
    [Redirect(typeof(GameViewportClient), "PostRender")]
    private static void PostRender(GameViewportClient self, Canvas canvas)
    {
        self.PostRender(canvas);
        Safe.Run("frame", () => App.Frame(canvas));
    }
}
