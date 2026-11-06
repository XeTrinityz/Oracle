using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// Draws the menu as one block of fixed-height panels over the game: the page rail, a page's tabs, and the rows
/// with the selected row's help in a footer. Tabs and rows slide in as you enter them, or show dimmed ahead of
/// time with PREVIEW PAGES on. A key strip sits under the block and changes with what's selected.
/// </summary>
internal static class MenuView
{
    private const float RailW = 186f;
    private const float SubW = 156f;
    private const float ColumnW = 440f;
    private const float Gap = 8f;
    private const float HeadH = 58f;
    private const float TabH = 28f;
    private const float RowH = 27f;
    private const float GroupH = 25f;
    private const float BodyH = 378f;
    private const float Inset = 13f;
    private const float ControlW = 150f;
    private const int MaxHelpLines = 4;
    private const float HelpLineH = 17f;
    private const float FooterPad = 9f;
    private const float HintH = 26f;
    private const float SearchBoxH = 26f;

    private const float BackAlpha = 0.86f;
    private const float PreviewAlpha = 0.5f;
    private const float TitleScale = 0.95f;
    private const float HeadScale = 0.84f;
    private const float LabelScale = 0.76f;
    private const float SmallScale = 0.66f;
    private const float ChipScale = 0.54f;
    private const float TagScale = 0.5f;
    private const int MaxWrapCache = 512;

    /// <summary>The fork's name, drawn under ORACLE in the rail header.</summary>
    public const string Edition = "TRINITY EDITION";

    private const string SaveWarning = " Changes your save and can't be undone.";

    private static readonly Dictionary<string, List<string>> _wrapped = new();
    private static readonly Dictionary<Feature, string> _helpText = new();
    private static readonly Dictionary<MenuPage, string> _contents = new();
    private static readonly Dictionary<Keys, string> _keyNames = new();
    private static readonly List<(string Key, string Label)> _hints = new();
    private static float _wrapScale = -1f;

    private static float _scroll = -1f;
    private static float _selY = -1f;
    private static float _railY = -1f;
    private static float _subY = -1f;
    private static float _hintW = -1f;
    private static float _sub;
    private static float _col;
    private static float _prev;
    private const float PreviewW = 460f;
    private static float _focus;

    /// <summary>
    /// PREVIEW PAGES: show a page's tabs and rows, dimmed, while the page list has focus. Off, they open only when
    /// you enter them.
    /// </summary>
    public static bool Preview;

    /// <summary>MENU POSITION: which side of the screen the menu sits on.</summary>
    public static int Position;

    public const int PositionCentre = 1;
    public const int PositionRight = 2;

    /// <summary>
    /// MENU EFFECTS: titles that decode, rows that cascade in, a light sweeping the panel edges, a scanline, a
    /// pulsing selection and a flash on the row you change. LOW-END MODE keeps them but drops the scanline and glint.
    /// </summary>
    public static bool Effects = true;

    private const string Glyphs = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789#%/<>";
    private static readonly float[] GlintAlpha = { 0.15f, 0.35f, 0.65f, 1f };
    private const float DecodeSeconds = 0.45f;
    private const float FlashSeconds = 0.45f;

    private static float _openAge;
    private static float _pageAge;
    private static float _menuIn;

    /// <summary>Age of the tab list, which only animates in when the top page changes, not on every tab.</summary>
    private static float _tabsAge;
    private static MenuPage? _tabsOf;

    public static void Opened()
    {
        _selY = -1f;
        _railY = -1f;
        _subY = -1f;
        _hintW = -1f;
        _scroll = -1f;
        _focus = Menu.InRows ? 1f : 0f;
        _sub = ShowSub ? 1f : 0f;
        _col = ShowColumn ? 1f : 0f;
        _openAge = 0f;
        _pageAge = 0f;
        _tabsAge = 0f;
        _tabsOf = Menu.Top;
    }

    /// <summary>A new page: the scroll jumps straight to the selected row, and the title and rows animate in.</summary>
    public static void PageTurned()
    {
        ResultsChanged();
        _pageAge = 0f;
    }

    /// <summary>Entering the rows of a page already on screen: nothing replays, the column just takes focus.</summary>
    public static void RowsEntered() => ResultsChanged();

    /// <summary>New search results: jump to the top, without replaying the page's entrance on every key.</summary>
    public static void ResultsChanged()
    {
        _selY = -1f;
        _scroll = -1f;
    }

    /// <summary>A gentle breathing factor for selection highlights.</summary>
    private static float Pulse => Effects ? 0.82f + 0.18f * MathF.Sin((float)Time * 3.2f) : 1f;

    /// <summary>Text that resolves left to right out of scrambled glyphs over its first moments on screen.</summary>
    public static string Decode(string text, float age)
    {
        if (!Effects || age >= DecodeSeconds || text.Length == 0)
            return text;

        int resolved = Math.Max(0, (int)(text.Length * (age / DecodeSeconds)));
        char[] c = text.ToCharArray();
        int tick = (int)(Time * 28.0);
        for (int i = resolved; i < c.Length; i++)
        {
            if (c[i] != ' ')
                c[i] = Glyphs[(int)((uint)(i * 7919 + tick * 104729 + text.Length * 31) % (uint)Glyphs.Length)];
        }

        return new string(c);
    }

    /// <summary>0 to 1 as the n-th item of a list eases in after the list appears.</summary>
    private static float Cascade(float age, int n, float delay) =>
        Effects ? Ease.Out(Math.Clamp((age - n * delay) / 0.2f, 0f, 1f)) : 1f;

    private static bool ShowSub => Menu.Top.HasSub && !Menu.Searching && (Preview || Menu.Level > 0);

    private static bool ShowColumn => Preview || Menu.InRows;

    private static float RowHeight(MenuRow r) => S(r.Item == null ? GroupH : RowH);

    private static float FooterMax => S(FooterPad) * 2f + S(HelpLineH) * (MaxHelpLines + 1);

    public static void Draw(float anim)
    {
        float screenScale = Scale;
        Scale *= Theme.MenuSize;
        try
        {
            if (_wrapScale != Scale || _wrapped.Count > MaxWrapCache)
            {
                _wrapScale = Scale;
                _wrapped.Clear();
            }

            float e = Ease.Out(anim);
            Alpha = e;
            _menuIn = e;
            Rgba accent = Theme.Accent;
            _openAge += Dt;
            _pageAge += Dt;
            _tabsAge += Dt;

            // Each panel animates in when it appears on screen. The tab list also does when its top page
            // changes, but not when moving between its own tabs.
            float step = Dt * 8f;
            bool showSub = ShowSub;
            bool showCol = ShowColumn;
            if (showSub && (_sub <= 0f || Menu.Top != _tabsOf))
                _tabsAge = 0f;
            if (showCol && _col <= 0f)
                _pageAge = 0f;
            _tabsOf = Menu.Top;
            _sub = Toward(_sub, showSub ? 1f : 0f, step);
            _col = Toward(_col, showCol ? 1f : 0f, step);
            _focus = Toward(_focus, Menu.InRows ? 1f : 0f, step);

            MenuPage page = Menu.Current;
            float content = 0f;
            foreach (MenuRow r in page.Rows)
                content += RowHeight(r);

            // Every panel is one fixed height, so nothing resizes as you move between pages.
            float h = Math.Min(S(HeadH + BodyH + 12f) + FooterMax, Gfx.H - S(HintH + 48f));
            float y = Math.Max(S(20), (Gfx.H - h - S(HintH + 8f)) * 0.5f);

            // MENU POSITION: the block's current width (it widens as tabs and rows slide in) decides where it
            // starts, so on the right it grows leftward from the edge and in the centre it stays centred.
            float margin = S(28);
            float width = S(RailW)
                + (_sub > 0f && Menu.Top.HasSub ? (S(SubW) + S(Gap)) * Ease.Out(_sub) : 0f)
                + (_col > 0f ? (S(Gap) + S(ColumnW)) * Ease.Out(_col) : 0f);

            // The asset preview is a fourth panel on the end, as wide as the screen allows, and counts toward
            // the block's width like the others, so every MENU POSITION keeps it beside the rows.
            _prev = Toward(_prev, AssetBrowser.I.Previewing ? 1f : 0f, step);
            float previewW = Math.Min(S(PreviewW), Gfx.W - margin * 2f - width - S(Gap));
            if (previewW < S(140))
                _prev = 0f;
            if (_prev > 0f)
                width += (S(Gap) + previewW) * Ease.Out(_prev);

            float slideIn = S(18) * (1f - e);
            float x = Position switch
            {
                PositionCentre => (Gfx.W - width) * 0.5f,
                PositionRight => Gfx.W - margin - width + slideIn,
                _ => margin - slideIn,
            };

            DrawRail(x, y, h, accent);

            float next = x + S(RailW) + S(Gap);
            if (_sub > 0f && Menu.Top.HasSub)
            {
                float c = Ease.Out(_sub);
                float outer = Alpha;
                Alpha *= c;
                DrawSubTabs(next - S(16) * (1f - c), y, h, accent);
                Alpha = outer;
                next += (S(SubW) + S(Gap)) * c;
            }

            float blockW = next - S(Gap) - x;
            if (_col > 0f)
            {
                float c = Ease.Out(_col);
                float outer = Alpha;
                Alpha *= c;
                DrawColumn(next - S(16) * (1f - c), y, h, page, content, FooterMax, accent);
                Alpha = outer;
                blockW += (S(Gap) + S(ColumnW)) * c;
            }

            if (_prev > 0f)
            {
                float c = Ease.Out(_prev);
                float px = x + blockW + S(Gap) - S(16) * (1f - c);
                float outer = Alpha;
                Alpha *= c;
                Backing(px, y, previewW, h, accent);
                AssetBrowser.I.DrawPreview(px, y, previewW, h, accent);
                Alpha = outer;
                blockW += (S(Gap) + previewW) * c;
            }

            DrawHints(x, y + h + S(8), blockW);

            if (Menu.Confirming != null)
                DrawConfirm(accent);
            else if (Menu.Binding != null)
                DrawBinding(accent);
        }
        finally
        {
            Scale = screenScale;
        }
    }

    private static float Toward(float value, float target, float step) =>
        value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    // ---- shared pieces ------------------------------------------------------------------------------------

    private static void Backing(float x, float y, float w, float h, Rgba accent)
    {
        Rect(x, y, w, h, Theme.Panel, BackAlpha);
        if (!Theme.LowEnd)
        {
            Grid(x, y, w, h, accent);
            Outline(x + S(4), y + S(4), w - S(8), h - S(8), accent, 0.07f);
        }

        Rect(x, y, w, S(1), accent, 0.7f);
        Brackets(x, y, w, h, accent);
        if (Effects && !Theme.LowEnd)
        {
            Scanline(x, y, w, h, accent);
            Glint(x, y, w, accent);
        }
    }

    /// <summary>Faint vertical columns across a panel, like the layout grid on Oracle's screens.</summary>
    private static void Grid(float x, float y, float w, float h, Rgba accent)
    {
        float step = S(62);
        for (float gx = x + step; gx < x + w - S(8); gx += step)
            Rect(gx, y + S(4), S(1), h - S(8), accent, 0.025f);
    }

    /// <summary>HUD corner brackets on all four corners of a panel.</summary>
    private static void Brackets(float x, float y, float w, float h, Rgba accent)
    {
        float arm = S(11);
        float t = Math.Max(1f, MathF.Round(S(2)));
        float r = x + w;
        float b = y + h;
        Rect(x, y, arm, t, accent, 0.95f);
        Rect(x, y, t, arm, accent, 0.95f);
        Rect(r - arm, y, arm, t, accent, 0.95f);
        Rect(r - t, y, t, arm, accent, 0.95f);
        Rect(x, b - t, arm, t, accent, 0.7f);
        Rect(x, b - arm, t, arm, accent, 0.7f);
        Rect(r - arm, b - t, arm, t, accent, 0.7f);
        Rect(r - t, b - arm, t, arm, accent, 0.7f);
    }

    /// <summary>A bright streak that runs along a panel's top edge every few seconds, each panel a beat after the last.</summary>
    private static void Glint(float x, float y, float w, Rgba accent)
    {
        const double period = 4.5;
        double phase = (Time - x / Math.Max(1f, Gfx.W) * 0.6) % period / period;
        float s = (float)(phase / 0.35);
        if (s >= 1f)
            return;

        float seg = S(70);
        float head = x + (w + seg) * Ease.Smooth(s) - seg;
        for (int i = 0; i < GlintAlpha.Length; i++)
        {
            float a = Math.Max(x, head + seg * i / GlintAlpha.Length);
            float bEnd = Math.Min(x + w, head + seg * (i + 1) / GlintAlpha.Length);
            if (bEnd > a)
                Rect(a, y, bEnd - a, S(2), Rgba.Mix(accent, Theme.White, i * 0.15f), GlintAlpha[i]);
        }
    }

    /// <summary>A faint line with a fading trail that drifts down the panel.</summary>
    private static void Scanline(float x, float y, float w, float h, Rgba accent)
    {
        const double period = 7.0;
        float sy = y + h * (float)(Time % period / period);
        float trail = S(30);
        for (int i = 0; i < 3; i++)
        {
            float top = sy - trail + trail * i / 3f;
            if (top > y)
                Rect(x, top, w, trail / 3f, accent, 0.012f + 0.012f * i);
        }

        Rect(x, sy, w, S(1), accent, 0.1f);
    }

    private static void Outline(float x, float y, float w, float h, Rgba col, float alpha)
    {
        float t = Math.Max(1f, MathF.Round(S(1)));
        Rect(x, y, w, t, col, alpha);
        Rect(x, y + h - t, w, t, col, alpha);
        Rect(x, y + t, t, h - t * 2f, col, alpha);
        Rect(x + w - t, y + t, t, h - t * 2f, col, alpha);
    }

    /// <summary>A panel header: a spaced title with a small accent tag beneath it, and a note on the right.</summary>
    private static void Header(float x, float y, float w, string title, string tag, string note, Rgba noteCol, Rgba accent, float scale, float spacing)
    {
        float nameH = TextH(scale);
        float tagH = TextH(TagScale);
        float ny = y + (S(HeadH) - nameH - S(3) - tagH) * 0.5f;
        Tracked(x + S(Inset), ny, title, Theme.Hi, scale, spacing);
        if (tag.Length > 0)
            Tracked(x + S(Inset) + S(1), ny + nameH + S(3), tag, accent, TagScale, 1.5f);
        if (note.Length > 0)
            TextRight(x + w - S(Inset), ny + nameH * 0.5f, note, noteCol, SmallScale);
        Rect(x + S(Inset), y + S(HeadH) - S(1), w - S(Inset) * 2f, S(1), Theme.White, 0.06f);
    }

    /// <summary>Letter-spaced text, which costs a draw call per letter, so LOW-END MODE draws it plain.</summary>
    private static void Tracked(float x, float y, string text, Rgba col, float scale, float spacing)
    {
        if (Theme.LowEnd)
            Text(x, y, text, col, scale);
        else
            Spaced(x, y, text, col, scale, spacing);
    }

    /// <summary>A small boxed label, like a key name. Returns its width.</summary>
    private static float Chip(float x, float cy, string text, Rgba col, float fill = 0.12f)
    {
        float w = TextW(text, ChipScale) + S(9);
        float h = TextH(ChipScale) + S(2);
        float y = cy - h * 0.5f;
        Rect(x, y, w, h, col, fill);
        if (!Theme.LowEnd)
            Outline(x, y, w, h, col, 0.4f);
        TextMid(x + S(4.5f), cy, text, col, ChipScale);
        return w;
    }

    private static string KeyName(Keys key)
    {
        if (!_keyNames.TryGetValue(key, out string? name))
            _keyNames[key] = name = key.ToString().ToUpperInvariant();
        return name;
    }

    private static bool Blink => Time * 2.0 % 2.0 < 1.2;

    // ---- rail and tabs -----------------------------------------------------------------------------------

    private static void DrawRail(float x, float y, float h, Rgba accent)
    {
        float w = S(RailW);
        Backing(x, y, w, h, accent);

        int active = Catalog.ActiveCount;
        Header(x, y, w, Decode("ORACLE", _openAge), Edition, active == 0 ? "STANDBY" : active + " ON", active > 0 ? accent : Theme.Low, accent, TitleScale, 4f);

        float top = y + S(HeadH);
        float rowH = S(TabH);
        bool searching = Menu.Searching;
        float outer = Alpha;
        if (searching)
            Alpha *= 0.45f;

        _railY = Follow(_railY, top + Menu.PageIndex * rowH, 24f);
        if (!searching)
        {
            bool railFocus = Menu.Level == 0;
            Rect(x, _railY, S(2), rowH, railFocus ? Theme.Hi : accent);
            GradientX(x + S(2), _railY, w - S(2), rowH, accent, (railFocus ? 0.26f : 0.12f) * Pulse, 0f);
        }

        for (int i = 0; i < Menu.PageCount; i++)
        {
            MenuPage page = Menu.PageAt(i);
            float cy = top + rowH * (i + 0.5f);
            bool on = i == Menu.PageIndex && !searching;
            float inAt = Cascade(_openAge, i, 0.035f);
            float rowAlpha = Alpha;
            Alpha *= inAt;
            float tx = x + S(Inset) - S(12) * (1f - inAt);
            TextMid(tx, cy, page.Title, on ? Theme.Hi : Theme.Mid, LabelScale);

            int n = i == 0 ? active : page.ActiveCount;
            if (n > 0)
                TextRight(x + w - S(Inset), cy, n.ToString(), on ? accent : Theme.Low, SmallScale);
            Alpha = rowAlpha;
        }

        Alpha = outer;

        // The search field sits at the foot of the rail when there's room; typing anywhere fills it.
        float rowsEnd = top + rowH * Menu.PageCount;
        float boxH = S(SearchBoxH);
        float bx = x + S(Inset) * 0.7f;
        float bw = w - S(Inset) * 1.4f;
        float by = y + h - S(10) - boxH;
        bool box = !Pad.InUse && by >= rowsEnd + S(8);

        // ORACLE UPLINK fills whatever room is left between the pages and the search field.
        float uplinkAlpha = Alpha;
        float uplinkIn = Arrive(Menu.PageCount + 1);
        Alpha *= uplinkIn;
        float decodeAge = Menu.IsOpen ? _openAge - (Menu.PageCount + 1) * 0.035f : DecodeSeconds * uplinkIn;
        Uplink.Draw(x, rowsEnd + S(10), w, box ? by - S(14) : y + h - S(14), accent, Effects, uplinkIn, decodeAge);
        Alpha = uplinkAlpha;

        if (!box)
            return;

        float boxIn = Arrive(Menu.PageCount + 3);
        Alpha *= boxIn;
        by += S(12) * (1f - boxIn);
        float cyBox = by + boxH * 0.5f;
        Rect(bx, by, bw, boxH, Theme.Black, 0.3f);
        Outline(bx, by, bw, boxH, searching ? accent : Theme.White, searching ? 0.8f : 0.12f);
        if (!searching)
        {
            TextMid(bx + S(9), cyBox, "TYPE TO SEARCH", Theme.Low, SmallScale);
        }
        else
        {
            string q = Menu.Query!;
            float room = bw - S(22);
            while (q.Length > 1 && TextW(q, SmallScale) > room)
                q = q.Substring(1);
            float qw = 0f;
            if (q.Length > 0)
            {
                TextMid(bx + S(9), cyBox, q, Theme.Hi, SmallScale);
                qw = TextW(q, SmallScale);
            }
            if (Blink)
                Rect(bx + S(9) + qw + S(2), cyBox - S(6), S(2), S(12), Theme.Signal);
        }

        Alpha = outer;
    }

    /// <summary>
    /// 0 to 1 for the late parts of the rail (uplink, search field): they ease in after the page list when the menu
    /// opens, and drop away first when it closes, before the panels fade.
    /// </summary>
    private static float Arrive(int n)
    {
        if (!Effects)
            return 1f;
        return Menu.IsOpen ? Cascade(_openAge, n, 0.035f) : Ease.Out(Math.Clamp(_menuIn * 1.6f - 0.6f, 0f, 1f));
    }

    private static void DrawSubTabs(float x, float y, float h, Rgba accent)
    {
        MenuPage top = Menu.Top;
        float w = S(SubW);
        float rowH = S(TabH);
        Backing(x, y, w, h, accent);

        int active = top.ActiveCount;
        Header(x, y, w, Decode(top.Title, _tabsAge), top.Children.Count + " TABS", active > 0 ? active + " ON" : "", accent, accent, HeadScale, 2f);

        float rows = y + S(HeadH);
        _subY = Follow(_subY, rows + Menu.SubIndex * rowH, 24f);

        bool focused = Menu.Level == 1;
        Rect(x, _subY, S(2), rowH, focused ? Theme.Hi : accent);
        GradientX(x + S(2), _subY, w - S(2), rowH, accent, (focused ? 0.26f : 0.12f) * Pulse, 0f);

        for (int i = 0; i < top.Children.Count; i++)
        {
            MenuPage page = top.Children[i];
            float cy = rows + rowH * (i + 0.5f);
            bool on = i == Menu.SubIndex;
            float inAt = Cascade(_tabsAge, i, 0.03f);
            float rowAlpha = Alpha;
            Alpha *= inAt;
            TextMid(x + S(Inset) - S(12) * (1f - inAt), cy, page.Title, on ? Theme.Hi : Theme.Mid, LabelScale);
            if (page.ActiveCount > 0)
                TextRight(x + w - S(Inset), cy, page.ActiveCount.ToString(), on ? accent : Theme.Low, SmallScale);
            Alpha = rowAlpha;
        }

        float outer = Alpha;
        float appear = Cascade(_tabsAge, top.Children.Count + 1, 0.03f);
        Alpha *= appear;
        DrawRunningHere(top, x, w, rows + rowH * top.Children.Count + S(10) + S(12) * (1f - appear), y + h - S(14), accent);
        Alpha = outer;
    }

    private static readonly List<Feature> _running = new();
    private static readonly HashSet<Feature> _seen = new();
    private const float RunLineH = 15f;
    private const float RunScale = 0.56f;

    /// <summary>
    /// RUNNING HERE, in the tab panel's spare space: how much of this page is switched on, as a count and a
    /// segmented bar, and the names of what's running across all its tabs, as many as fit.
    /// </summary>
    private static void DrawRunningHere(MenuPage top, float x, float w, float y, float bottom, Rgba accent)
    {
        if (bottom - y < S(70))
            return;

        _running.Clear();
        _seen.Clear();
        int total = 0;
        foreach (MenuPage tab in top.Children)
        {
            foreach (MenuRow row in tab.Rows)
            {
                if (row.Item is not { } f || f.IsPref || f.IsSetting || f is Command || !_seen.Add(f))
                    continue;
                total++;
                if (f.Active)
                    _running.Add(f);
            }
        }

        if (total == 0)
            return;

        float left = x + S(Inset);
        float right = x + w - S(Inset);
        Rect(left, y, right - left, S(1), Theme.White, 0.06f);
        y += S(10);

        float tagH = TextH(TagScale);
        Text(left, y, "RUNNING HERE", accent, TagScale);
        string count = _running.Count + " / " + total;
        Text(right - TextW(count, TagScale), y, count, _running.Count > 0 ? Theme.Mid : Theme.Low, TagScale);
        y += tagH + S(8);

        // One block per switchable feature on the page, lit for each one running.
        int blocks = Math.Min(total, 24);
        float gap = S(2);
        float bw = (right - left - gap * (blocks - 1)) / blocks;
        float lit = _running.Count * blocks / (float)total;
        for (int i = 0; i < blocks; i++)
        {
            float part = Math.Clamp(lit - i, 0f, 1f);
            Rect(left + i * (bw + gap), y, bw, S(5), Theme.White, 0.08f);
            if (part > 0f)
                Rect(left + i * (bw + gap), y, bw * part, S(5), accent, 0.9f);
        }

        y += S(5) + S(12);

        if (_running.Count == 0)
        {
            Text(left, y, "NOTHING RUNNING", Theme.Low, RunScale);
            return;
        }

        float lineH = S(RunLineH);
        int fits = (int)((bottom - y) / lineH);
        int show = _running.Count <= fits ? _running.Count : Math.Max(0, fits - 1);
        for (int i = 0; i < show; i++)
        {
            float cy = y + lineH * 0.5f;
            Rect(left, cy - S(4), S(2), S(8), accent, 0.8f);
            TextMid(left + S(8), cy, Clip(_running[i].Label, right - left - S(8), RunScale, ".."), Theme.Mid, RunScale);
            y += lineH;
        }

        if (show < _running.Count)
            TextMid(left + S(8), y + lineH * 0.5f, "+" + (_running.Count - show) + " MORE", Theme.Low, RunScale);
    }

    // ---- rows column --------------------------------------------------------------------------------------

    private static void DrawColumn(float x, float y, float h, MenuPage page, float content, float footerH, Rgba accent)
    {
        List<MenuRow> rows = page.Rows;
        float w = S(ColumnW);
        float focus = Ease.Out(_focus);
        Backing(x, y, w, h, accent);
        ColumnHeader(x, y, w, page, accent);

        float pad = S(6);
        float bodyY = y + S(HeadH) + pad;
        float bodyH = Math.Max(S(RowH), h - S(HeadH) - pad * 2f - footerH);

        float selTop = 0f;
        float at = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == Menu.RowIndex)
                selTop = at;
            at += RowHeight(rows[i]);
        }

        float cur = Math.Max(0f, _scroll);
        float want = cur;
        if (content <= bodyH)
            want = 0f;
        else
        {
            float above = Menu.RowIndex > 0 && Menu.RowIndex < rows.Count && rows[Menu.RowIndex - 1].Item == null ? S(GroupH) : 0f;
            if (selTop - above < want)
                want = selTop - above;
            if (selTop + S(RowH) > want + bodyH)
                want = selTop + S(RowH) - bodyH;
            want = Math.Clamp(want, 0f, content - bodyH);
        }

        if (_scroll < 0f)
            _scroll = want;
        else
        {
            _scroll += (want - _scroll) * Ease.Follow(18f, Dt);
            if (MathF.Abs(want - _scroll) < 0.5f)
                _scroll = want;
        }

        Feature? selected = Menu.Selected;
        _selY = Follow(_selY, bodyY + selTop - _scroll, 26f);
        if (selected != null && focus > 0f)
        {
            float a = Math.Max(_selY, bodyY);
            float b = Math.Min(_selY + S(RowH), bodyY + bodyH);
            if (b > a)
            {
                GradientX(x, a, w, b - a, accent, 0.2f * focus * Pulse, 0f);
                Rect(x, a, S(2), b - a, Theme.Hi, focus);
            }
        }

        float left = x + S(Inset);
        float right = x + w - S(Inset);
        float ry = bodyY - _scroll;
        float dim = PreviewAlpha + (1f - PreviewAlpha) * focus;
        int shown = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            MenuRow row = rows[i];
            float rh = RowHeight(row);
            float top = ry;
            ry += rh;

            if (top + rh <= bodyY || top >= bodyY + bodyH)
                continue;

            // A row fades by how much of it is inside the list, so one hanging over the footer never draws over it.
            float inside = Math.Min(top + rh - bodyY, bodyY + bodyH - top) / rh;
            float fade = Math.Clamp((inside - 0.4f) / 0.6f, 0f, 1f);
            float inAt = Cascade(_pageAge, shown++, 0.022f);
            float slide = S(14) * (1f - inAt);
            float outer = Alpha;
            Alpha *= fade * dim * inAt;
            if (row.Item == null)
                DrawGroup(row.Header!, left + slide, right, top + rh * 0.5f, accent);
            else
                DrawRow(row, i == Menu.RowIndex && Menu.InRows, left + slide, right, top + rh * 0.5f, accent);
            Alpha = outer;
        }

        if (content > bodyH)
        {
            float thumbH = Math.Max(S(20), bodyH * bodyH / content);
            float thumbY = bodyY + (bodyH - thumbH) * (_scroll / (content - bodyH));
            Rect(x + w - S(4), bodyY, S(2), bodyH, Theme.White, 0.05f);
            Rect(x + w - S(4), thumbY, S(2), thumbH, accent, 0.8f);
        }

        DrawFooter(x, y + h - footerH, w, footerH, page, selected, accent);
    }

    private static void ColumnHeader(float x, float y, float w, MenuPage page, Rgba accent)
    {
        int count = 0;
        int position = 0;
        for (int i = 0; i < page.Rows.Count; i++)
        {
            if (page.Rows[i].Item == null)
                continue;
            count++;
            if (i <= Menu.RowIndex)
                position = count;
        }

        string where = Menu.InRows && count > 0 ? position + " / " + count : "";
        if (Menu.Searching)
        {
            string q = Menu.Query!.Trim();
            Header(x, y, w, "RESULTS", q.Length == 0 ? "TYPE ANY WORDS" : "FOR " + q, count + " FOUND", count > 0 ? accent : Theme.Low, accent, HeadScale, 2f);
            return;
        }

        if (Menu.IsActivePage(page))
        {
            Header(x, y, w, Decode("ACTIVE", _pageAge), "EVERYTHING SWITCHED ON", where, Theme.Low, accent, HeadScale, 2f);
            return;
        }

        int on = page.ActiveCount;
        string tag = Fmt.Plural(count, "FEATURE");
        if (on > 0)
            tag += "   " + on + " ON";
        Header(x, y, w, Decode(page.Title, _pageAge), tag, where, Theme.Low, accent, HeadScale, 2f);
    }

    private static void DrawGroup(string label, float left, float right, float cy, Rgba accent)
    {
        Rgba col = label.Contains("SAVE", StringComparison.Ordinal) ? Theme.Warn : accent;
        TextMid(left, cy + S(3), label, col, SmallScale, 0.9f);
        float tw = TextW(label, SmallScale);
        Rect(left + tw + S(10), cy + S(3), right - left - tw - S(10), S(1), Theme.White, 0.07f);
    }

    private static void DrawRow(MenuRow row, bool sel, float left, float right, float cy, Rgba accent)
    {
        Feature f = row.Item!;
        if (Effects && Menu.Changed == f)
        {
            float flash = 1f - (float)(Time - Menu.ChangedAt) / FlashSeconds;
            if (flash > 0f)
            {
                float rowH = S(RowH);
                GradientX(left - S(Inset), cy - rowH * 0.5f, right - left + S(Inset) * 2f, rowH, Theme.Accent, 0.4f * flash, 0.05f * flash);
            }
        }

        int key = f.GetHashCode() * 8;
        float shift = Approach(key, sel ? 1f : 0f, 20f);
        float lx = left + S(4) * shift;

        // A disabled row keeps its place but reads as unavailable: dimmed label, and the reason where its control was.
        if (f.Disabled is { } why)
        {
            TextMid(lx, cy, f.Label, Rgba.Mix(Theme.Low, Theme.Mid, shift * 0.5f), LabelScale);
            float cw = TextW(why, ChipScale) + S(9);
            Chip(right - cw, cy, why, Theme.Low, 0.05f);
            return;
        }

        TextMid(lx, cy, f.Label, Rgba.Mix(Theme.Mid, Theme.Hi, shift), LabelScale);
        float end = lx + TextW(f.Label, LabelScale);

        // A running row gets a slim tick on the panel's edge, where the selection bar sits, so runs of them read
        // as a segmented strip down the side.
        if (f.Active && !sel)
        {
            float rowH = S(RowH);
            Rect(left - S(Inset), cy - rowH * 0.5f + S(5), S(2), rowH - S(10), Theme.Accent, 0.75f);
        }

        if (Favourites.Contains(f))
            end += S(8) + RadialMark(end + S(8), cy, sel ? Theme.Hi : Theme.Mid);
        if (Settings.Hotkeys.TryGetValue(f.Id, out Keys hotkey))
            end += S(8) + Chip(end + S(8), cy, KeyName(hotkey), Theme.Mid, 0.08f);
        if (f.ChangesSave)
            end += S(8) + Chip(end + S(8), cy, "SAVE", Theme.Warn);

        if (row.Path != null)
        {
            float room = right - ControlRoom(f) - S(10);
            if (end + S(10) + TextW(row.Path, TagScale) <= room)
                TextMid(end + S(10), cy, row.Path, Theme.Low, TagScale);
        }

        if (f is Toggle t)
            DrawSwitch(t, right, cy, sel, key, accent);
        else if (f is Slider s)
            DrawSlider(s, right, cy, sel, key, accent);
        else if (f is Choice c)
            DrawChoice(c, right, cy, sel, key, accent);
        else
            DrawCommand(f, right, cy, sel, key, accent);
    }

    /// <summary>A small ring with a lit segment, after the label of a row that's on the favourites radial. Returns its width.</summary>
    private static float RadialMark(float x, float cy, Rgba col)
    {
        float r = S(4.5f);
        Shapes.Ring(x + r, cy, r - S(1.5f), r, col, 0.7f);
        Shapes.Arc(x + r, cy, r - S(1.5f), r + S(0.5f), -120f, -60f, Theme.Accent, 1f);
        return r * 2f;
    }

    /// <summary>Roughly how much of the row's right side its control takes, so a page tag never runs under it.</summary>
    private static float ControlRoom(Feature f) => f switch
    {
        Toggle => S(SwitchW) + S(8),
        Slider => S(SliderW) + S(8),
        Choice => S(SliderW) + S(36),
        _ => S(84),
    };

    private const float SwitchW = 60f;
    private const int MaxPips = 8;
    private const int MeterSegments = 14;

    /// <summary>Height of the boxed controls: switches and command chips.</summary>
    private static float ControlH => TextH(ChipScale) + S(6);

    /// <summary>Centred text in a box, for labels drawn on controls.</summary>
    private static void Label(float cx, float cy, string text, Rgba col, float alpha = 1f) =>
        TextMid(cx - TextW(text, ChipScale) * 0.5f, cy, text, col, ChipScale, alpha);

    /// <summary>A segmented OFF | ON switch: a solid block slides to the live side and carries its name.</summary>
    private static void DrawSwitch(Toggle f, float right, float cy, bool sel, int key, Rgba accent)
    {
        float t = Approach(key + 1, f.On ? 1f : 0f, 16f);
        float w = S(SwitchW);
        float h = ControlH;
        float x = right - w;
        float y = cy - h * 0.5f;

        if (t > 0f && !Theme.LowEnd)
            Rect(x - S(2), y - S(2), w + S(4), h + S(4), accent, 0.1f * t);
        Rect(x, y, w, h, Theme.Black, 0.4f);
        Outline(x, y, w, h, Rgba.Mix(Theme.Low, accent, t), sel ? 0.9f : 0.5f);

        float inner = S(2);
        float half = (w - inner * 2f) * 0.5f;
        Rect(x + inner + t * half, y + inner, half, h - inner * 2f, Rgba.Mix(Theme.Off, accent, t), 0.95f);

        Label(x + inner + half * 0.5f, cy, "OFF", Rgba.Mix(Theme.Hi, Theme.Low, t), 1f - 0.5f * t);
        Label(x + inner + half * 1.5f, cy, "ON", Rgba.Mix(Theme.Low, Theme.Black, t), 0.5f + 0.5f * t);
    }

    /// <summary>A segmented meter that fills block by block, brighter toward its end, with the default marked beneath.</summary>
    private static void DrawSlider(Slider f, float right, float cy, bool sel, int key, Rgba accent)
    {
        float range = f.Max - f.Min;
        float t = range <= 0f ? 0f : Math.Clamp((f.Value - f.Min) / range, 0f, 1f);
        float home = range <= 0f ? 0f : Math.Clamp((f.Default - f.Min) / range, 0f, 1f);
        float at = Approach(key + 2, t, 20f);

        // Meter on the left, then a fixed value area, so a long value never reaches the meter.
        float valueW = S(SliderValueW);
        float trackW = S(SliderW) - valueW - S(10);
        float tx = right - S(SliderW);
        float sh = S(9);
        float top = cy - sh * 0.5f;
        Rgba fill = f.IsDefault ? Theme.Mid : accent;
        float strength = sel ? 1f : 0.75f;

        if (Theme.LowEnd)
        {
            Rect(tx, top, trackW, sh, Theme.White, 0.08f);
            Rect(tx, top, trackW * at, sh, fill, strength);
        }
        else
        {
            float gap = S(2);
            float sw = (trackW - gap * (MeterSegments - 1)) / MeterSegments;
            for (int i = 0; i < MeterSegments; i++)
            {
                float sx = tx + i * (sw + gap);
                float part = Math.Clamp(at * MeterSegments - i, 0f, 1f);
                float rise = (i + 1) / (float)MeterSegments;
                Rect(sx, top, sw, sh, Theme.White, 0.08f);
                if (part > 0f)
                    Rect(sx, top, sw * part, sh, Rgba.Mix(fill, Theme.White, sel ? 0.15f * rise : 0f), strength * (0.5f + 0.5f * rise));
            }
        }

        // The default sits as a small block under the meter, so a changed value shows how far it moved.
        if (!f.IsDefault)
            Rect(tx + trackW * home - S(1.5f), top + sh + S(2), S(3), S(3), Theme.Mid, 0.9f);

        // The value is centred in its own area between arrows pinned to the area's edges, shrunk to fit if long.
        string value = f.State;
        Rgba col = f.IsDefault ? (sel ? Theme.Hi : Theme.Mid) : accent;
        float vx = right - valueW;
        float arrow = S(11);
        float room = valueW - arrow * 2f - S(4);
        float scale = LabelScale;
        float vw = TextW(value, scale);
        if (vw > room)
        {
            scale *= Math.Max(0.6f, room / vw);
            vw = TextW(value, scale);
        }

        TextMid(vx + (valueW - vw) * 0.5f, cy, value, col, scale);
        if (sel)
        {
            float nudge = Effects ? S(1.5f) * MathF.Sin((float)Time * 6f) : 0f;
            Label(vx + arrow * 0.5f - nudge, cy, "<", Theme.Hi);
            Label(right - arrow * 0.5f + nudge, cy, ">", Theme.Hi);
        }
    }

    private const float SliderW = 196f;
    private const float SliderValueW = 88f;

    private static readonly Dictionary<Choice, (int Index, int Dir, float Left)> _slides = new();
    private static readonly Dictionary<(string, float), int> _tickerEnds = new();

    /// <summary>The smallest a choice's name shrinks before it scrolls or is cut short instead.</summary>
    private const float MinNameScale = 0.85f;
    private const float TickerPause = 1.2f;
    private const float TickerStep = 0.11f;

    /// <summary>The longest leading part of the text that fits, with an optional tail like "..".</summary>
    private static string Clip(string text, float room, float scale, string tail)
    {
        if (TextW(text, scale) <= room)
            return text;

        var key = (text, MathF.Round(room), scale * Scale, tail);
        if (_clips.TryGetValue(key, out string? clipped))
            return clipped;
        if (_clips.Count > 1024)
            _clips.Clear();

        int len = text.Length;
        while (len > 1 && TextW(text.Substring(0, len).TrimEnd() + tail, scale) > room)
            len--;
        return _clips[key] = text.Substring(0, len).TrimEnd() + tail;
    }

    private static readonly Dictionary<(string, float, float, string), string> _clips = new();

    /// <summary>
    /// A window onto text too long for its box: it holds at the start, steps a letter at a time to the end,
    /// holds there, then starts over.
    /// </summary>
    private static string Ticker(string text, float room, float scale)
    {
        if (!_tickerEnds.TryGetValue((text, scale), out int last))
        {
            last = 0;
            while (last < text.Length - 1 && TextW(text.Substring(last), scale) > room)
                last++;
            _tickerEnds[(text, scale)] = last;
        }

        float cycle = TickerPause * 2f + last * TickerStep;
        float t = (float)(Time % cycle);
        int start = t < TickerPause ? 0 : Math.Min(last, (int)((t - TickerPause) / TickerStep));
        return Clip(text.Substring(start), room, scale, "");
    }

    /// <summary>
    /// How far a choice's value is still sliding in, -1 to 1: positive enters from the right (stepped forward),
    /// negative from the left. Wrapping from last to first still counts as one step forward.
    /// </summary>
    private static float Slide(Choice f, int n)
    {
        if (!_slides.TryGetValue(f, out var s))
            s = (f.Index, 0, 0f);

        if (s.Index != f.Index)
        {
            int dir = n > 1 && f.Index == (s.Index + 1) % n ? 1 : n > 1 && f.Index == (s.Index - 1 + n) % n ? -1 : 0;
            s = (f.Index, dir, dir == 0 ? 0f : 1f);
        }

        s.Left = Math.Max(0f, s.Left - Dt * 7f);
        _slides[f] = s;
        return s.Dir * s.Left * s.Left;
    }

    /// <summary>
    /// A fixed-width selector: arrow cells pinned to both ends and the value centred between them, so nothing
    /// shifts with the length of the option. A strip along the bottom shows where the option sits in its list
    /// (a segment per option for short lists, a moving marker for long ones), and a new value slides in from the
    /// side you stepped toward.
    /// </summary>
    private static void DrawChoice(Choice f, float right, float cy, bool sel, int key, Rgba accent)
    {
        float w = S(SliderW);
        float h = TextH(SmallScale) + S(9);
        float x = right - w;
        float y = cy - h * 0.5f;
        int n = f.Labels.Length;
        Rgba edge = sel ? accent : Theme.Low;

        Rect(x, y, w, h, Theme.Black, 0.35f);
        if (!Theme.LowEnd)
            Outline(x, y, w, h, edge, sel ? 0.75f : 0.3f);

        // Arrow cells at both ends, lit while the row is selected.
        float cell = h;
        Rect(x, y, cell, h, edge, sel ? 0.22f : 0.06f);
        Rect(x + w - cell, y, cell, h, edge, sel ? 0.22f : 0.06f);
        float nudge = sel && Effects ? S(1.5f) * MathF.Sin((float)Time * 6f) : 0f;
        Label(x + cell * 0.5f - nudge, cy - S(1), "<", sel ? Theme.Hi : Theme.Low);
        Label(x + w - cell * 0.5f + nudge, cy - S(1), ">", sel ? Theme.Hi : Theme.Low);

        // The value, centred in the middle. A step slides it in from that side. A long name shrinks a little,
        // then scrolls through the box while selected, or is cut short with ".." when not.
        float inner = w - cell * 2f - S(10);
        float slide = Effects ? Slide(f, n) : 0f;
        string name = f.State;
        float swatch = f.Swatch != null ? S(12) : 0f;
        float scale = SmallScale;
        float room = inner - swatch;
        float tw = TextW(name, scale);
        if (tw > room)
        {
            scale *= Math.Max(MinNameScale, room / tw);
            if (TextW(name, scale) > room)
                name = sel && Effects ? Ticker(name, room, scale) : Clip(name, room, scale, "..");
            tw = TextW(name, scale);
        }

        float mid = x + w * 0.5f + slide * S(16);
        float start = mid - (tw + swatch) * 0.5f;
        float outer = Alpha;
        Alpha *= Math.Max(0f, 1f - MathF.Abs(slide) * 1.4f);
        if (f.Swatch != null)
            Rect(start, cy - S(5), S(8), S(8), f.Swatch(f.Index));
        TextMid(start + swatch, cy - S(1), name, f.IsDefault ? (sel ? Theme.Hi : Theme.Mid) : accent, scale);
        Alpha = outer;

        // Position strip along the bottom edge, between the arrow cells.
        if (n > 1)
        {
            float sx = x + cell + S(4);
            float sw = w - cell * 2f - S(8);
            float sy = y + h - S(3);
            float sh = Math.Max(1f, MathF.Round(S(1.5f)));
            if (n <= MaxPips * 2)
            {
                float gap = S(2);
                float seg = (sw - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                {
                    bool on = i == f.Index;
                    Rect(sx + i * (seg + gap), sy, seg, sh, on ? (f.IsDefault ? Theme.Hi : accent) : Theme.White, on ? 0.95f : 0.14f);
                }
            }
            else
            {
                Rect(sx, sy, sw, sh, Theme.White, 0.1f);
                float mark = Math.Max(S(6), sw / n);
                float at = Approach(key + 4, f.Index / (float)(n - 1), 16f);
                Rect(sx + (sw - mark) * at, sy, mark, sh, f.IsDefault ? Theme.Hi : accent, 0.95f);
            }
        }

        // Long lists say where you are, just left of the selector, when the row is selected.
        if (sel && n > MaxPips * 2)
            TextRight(x - S(8), cy, (f.Index + 1) + "/" + n, Theme.Low, TagScale);
    }

    /// <summary>
    /// A command's action chip: a small outlined chevron at rest, a solid RUN chip with a nudging chevron when
    /// selected, and CONFIRM, DONE or FAILED as it goes. Commands that change the save use the warning colour.
    /// Between rest and active it morphs: the box widens and fills, the chevron glides to the chip's end, and the
    /// word fades in once there's room, all in reverse on the way back.
    /// </summary>
    private static void DrawCommand(Feature f, float right, float cy, bool sel, int key, Rgba accent)
    {
        bool flashing = Menu.Flashed == f && Menu.FlashLeft > 0f;
        bool confirming = Menu.Confirming == f;
        bool active = sel || flashing || confirming;
        float h = ControlH;
        float y = cy - h * 0.5f;
        Rgba tone = f.ChangesSave ? Theme.Warn : accent;

        float t = Effects ? Approach(key + 5, active ? 1f : 0f, 14f) : (active ? 1f : 0f);
        float e = Ease.Smooth(t);

        string text = flashing ? (Menu.FlashOk ? "DONE" : "FAILED") : confirming ? "CONFIRM" : "RUN";
        Rgba col = flashing ? (Menu.FlashOk ? accent : Theme.Warn) : confirming ? Theme.Warn : tone;
        float strength = confirming && Effects ? 0.75f + 0.25f * MathF.Sin((float)Time * 9f) : 0.95f;
        bool arrow = !flashing;

        float full = Math.Max(S(58), TextW(text, ChipScale) + S(arrow ? 30 : 18));
        float w = h + (full - h) * e;
        float x = right - w;

        if (e > 0f)
        {
            if (!Theme.LowEnd)
                Rect(x - S(2), y - S(2), w + S(4), h + S(4), col, 0.12f * e);
            Rect(x, y, w, h, col, strength * e);
        }

        if (e < 1f)
            Outline(x, y, w, h, tone, 0.35f * (1f - e));

        // The chevron glides from the middle of the resting box to the chip's right end, darkening onto the fill.
        // A DONE or FAILED flash has no chevron, so it fades out instead.
        float nudge = active && arrow && Effects ? S(2) * MathF.Sin((float)Time * 6f) * e : 0f;
        float chevronX = right - h * 0.5f + (h * 0.5f - S(9)) * e + nudge;
        float chevronAlpha = arrow ? 1f : 1f - e;
        if (chevronAlpha > 0f)
            Label(chevronX, cy, ">", Rgba.Mix(Theme.Low, Theme.Black, e), chevronAlpha);

        // The word arrives once the chip is most of the way open, easing in from the right.
        float word = Math.Clamp((e - 0.45f) / 0.55f, 0f, 1f);
        if (word > 0f)
        {
            float textX = arrow ? x + (w - S(14)) * 0.5f : x + w * 0.5f;
            Label(textX + S(6) * (1f - word), cy, text, Theme.Black, word);
        }
    }

    // ---- footer -------------------------------------------------------------------------------------------

    private static void DrawFooter(float x, float y, float w, float h, MenuPage page, Feature? f, Rgba accent)
    {
        float left = x + S(Inset);
        float right = x + w - S(Inset);
        Rect(left, y, right - left, S(1), Theme.White, 0.06f);

        bool rows = Menu.InRows && f != null;
        string? off = rows ? f!.Disabled : null;
        string text = rows ? (off != null ? "UNAVAILABLE: " + off + ". " + f!.Help : HelpText(f!)) : Summary(page);
        Rgba col = off != null ? Theme.Low : rows && f!.ChangesSave ? Theme.Warn : rows ? Theme.Mid : Theme.Low;
        float ly = y + S(FooterPad);
        foreach (string line in Wrap(text, right - left))
        {
            Text(left, ly, line, col, SmallScale);
            ly += S(HelpLineH);
        }

        if (rows)
            DrawMeta(f!, left, right, y + h - S(FooterPad) - S(HelpLineH) * 0.5f, accent);
    }

    /// <summary>The footer's last line: hotkey, default value, option count and radial slot, whichever apply.</summary>
    private static void DrawMeta(Feature f, float left, float right, float cy, Rgba accent)
    {
        float mx = left;
        if (Settings.Hotkeys.TryGetValue(f.Id, out Keys key))
        {
            TextMid(mx, cy, "HOTKEY", Theme.Low, TagScale);
            mx += TextW("HOTKEY", TagScale) + S(6);
            mx += Chip(mx, cy, KeyName(key), Theme.Mid, 0.08f) + S(14);
        }

        string? fallback = f switch
        {
            Slider { IsDefault: false } s => s.Format(s.Default),
            Choice { IsDefault: false, Labels.Length: > 0 } c => c.Labels[0],
            _ => null,
        };
        if (fallback != null)
        {
            TextMid(mx, cy, "DEFAULT", Theme.Low, TagScale);
            mx += TextW("DEFAULT", TagScale) + S(6);
            TextMid(mx, cy, fallback, Theme.Mid, TagScale);
            mx += TextW(fallback, TagScale) + S(14);
        }

        if (f is Choice { Labels.Length: > 2 } many)
            TextMid(mx, cy, "OPTION " + (many.Index + 1) + " OF " + many.Labels.Length, Theme.Low, TagScale);

        // Page and slot on the radial, kept short so it never runs into the hotkey and default on the left.
        int at = Favourites.IndexOf(f.Id);
        if (at >= 0)
            TextRight(right, cy, "RADIAL " + (at / Favourites.PerPage + 1) + "-" + (at % Favourites.PerPage + 1), accent, TagScale);
    }

    private static string HelpText(Feature f)
    {
        if (!f.ChangesSave)
            return f.Help;
        if (!_helpText.TryGetValue(f, out string? text))
            _helpText[f] = text = f.Help + SaveWarning;
        return text;
    }

    /// <summary>What the footer says while you're browsing rather than on a row.</summary>
    private static string Summary(MenuPage page)
    {
        if (Menu.Searching)
            return "Type to narrow it down: every word must match. BACKSPACE erases, ESC ends the search.";
        if (Menu.IsActivePage(page))
            return "Everything you switch on shows up here, so it's easy to find and switch off again.";

        if (_contents.TryGetValue(page, out string? text))
            return text;

        var groups = page.Rows.Where(r => r.Header != null).Select(r => r.Header!).Distinct().ToList();
        text = groups.Count == 0 ? "" : "IN HERE: " + string.Join("  /  ", groups);
        _contents[page] = text;
        return text;
    }

    /// <summary>Word-wraps help text to the footer, cut short with an ellipsis past the line limit.</summary>
    private static List<string> Wrap(string text, float width)
    {
        if (_wrapped.TryGetValue(text, out List<string>? lines))
            return lines;

        lines = new List<string>();
        string rest = text;
        while (rest.Length > 0 && lines.Count < MaxHelpLines)
        {
            string next = FitLine(rest, width, SmallScale, out string line);
            if (next.Length == rest.Length)
                break;

            lines.Add(next.Length > 0 && lines.Count == MaxHelpLines - 1 ? line + "..." : line);
            rest = next;
        }

        _wrapped[text] = lines;
        return lines;
    }

    // ---- key strip and prompts ----------------------------------------------------------------------------

    private static void DrawHints(float x, float y, float w)
    {
        BuildHints();
        float need = S(Inset) * 1.4f - S(16);
        foreach ((string key, string label) in _hints)
            need += TextW(key, ChipScale) + S(9) + S(6) + TextW(label, SmallScale * 0.92f) + S(16);

        // At least as wide as the panels above, and wide enough for its keys when only the page list is open.
        _hintW = Follow(_hintW, Math.Max(w, need), 18f);
        float extra = _hintW - w;
        if (Position == PositionRight)
            x -= extra;
        else if (Position == PositionCentre)
            x -= extra * 0.5f;
        w = _hintW;
        float h = S(HintH);
        float cy = y + h * 0.5f;
        Rect(x, y, w, h, Theme.Panel, BackAlpha);

        float hx = x + S(Inset) * 0.7f;
        float end = x + w - S(Inset) * 0.7f + S(1);
        foreach ((string key, string label) in _hints)
        {
            float kw = TextW(key, ChipScale) + S(9);
            float lw = TextW(label, SmallScale * 0.92f);
            if (hx + kw + S(6) + lw > end)
                break;

            hx += Chip(hx, cy, key, Theme.Hi, 0.1f) + S(6);
            TextMid(hx, cy, label, Theme.Mid, SmallScale * 0.92f);
            hx += TextW(label, SmallScale * 0.92f) + S(16);
        }
    }

    private static void BuildHints()
    {
        _hints.Clear();
        Feature? f = Menu.Selected;
        bool rows = Menu.InRows && f != null;
        string verb = f switch
        {
            Toggle t => t.On ? "TURN OFF" : "TURN ON",
            Slider => "DEFAULT",
            Choice => "NEXT",
            _ => "RUN",
        };
        bool off = f?.Disabled != null;
        bool adjusts = f is Slider or Choice && !off;
        bool resets = f is { IsDefault: false } and not Command && !off;
        string pin = f != null && Favourites.Contains(f) ? "MOVE ON RADIAL" : "ADD TO RADIAL";

        if (Pad.InUse)
        {
            if (rows)
            {
                if (!off)
                    _hints.Add(("A", verb));
                if (resets)
                    _hints.Add(("X", "RESET"));
                if (f is { Bindable: true })
                    _hints.Add(("Y", pin));
                _hints.Add(("LB RB", "PAGE"));
                _hints.Add(("B", "BACK"));
            }
            else
            {
                _hints.Add(("A", "OPEN"));
                _hints.Add(("LB RB", Menu.Level == 1 ? "TAB" : "PAGE"));
                _hints.Add(("B", Menu.Level == 1 ? "BACK" : "CLOSE"));
            }

            return;
        }

        if (rows)
        {
            if (!off)
                _hints.Add(("ENTER", verb));
            if (adjusts)
                _hints.Add(("< >", "ADJUST"));
            if (resets)
                _hints.Add(("DEL", "RESET"));
            if (f is { Bindable: true, IsSetting: false })
                _hints.Add(("INS", "BIND"));
            if (f is { Bindable: true })
                _hints.Add(("HOME", pin));
        }
        else if (!Menu.Searching)
            _hints.Add(("ENTER", "OPEN"));

        if (Menu.Searching)
        {
            _hints.Add(("BKSP", "ERASE"));
            _hints.Add(("ESC", "DONE"));
            return;
        }

        if (Menu.Level > 0)
            _hints.Add(("BKSP", "BACK"));
        _hints.Add(("A-Z", "SEARCH"));
        if (Menu.Level == 0)
            _hints.Add((KeyName(Settings.MenuKey), "CLOSE"));
    }

    private static void Modal(string title, string keys, Rgba edge, float timeLeft)
    {
        Rect(0f, 0f, Gfx.W, Gfx.H, Theme.Black, 0.35f);

        float w = Math.Max(TextW(title, 0.9f), TextW(keys, SmallScale)) + S(64);
        float h = S(88);
        float x = (Gfx.W - w) * 0.5f;
        float y = (Gfx.H - h) * 0.5f;

        Rect(x + S(4), y + S(5), w, h, Theme.Black, 0.35f);
        Rect(x, y, w, h, Theme.Panel);
        Rect(x, y, w, S(2), edge, 0.95f);
        TextCentre(x + w * 0.5f, y + S(20), title, Theme.Hi, 0.9f);
        TextCentre(x + w * 0.5f, y + h - S(30), keys, Theme.Mid, SmallScale);
        if (timeLeft > 0f)
            Rect(x, y + h - S(3), w * timeLeft, S(3), edge, 0.8f);
    }

    private static void DrawConfirm(Rgba accent)
    {
        Feature f = Menu.Confirming!;
        string title = f.ConfirmPrompt?.Invoke() ?? (f.ChangesSave ? "CHANGE YOUR SAVE: " + f.Label + "?" : f.Label + "?");
        Modal(title, Pad.InUse ? "A CONFIRM     ANY OTHER BUTTON CANCEL" : "ENTER CONFIRM     ANY OTHER KEY CANCEL", f.ChangesSave ? Theme.Warn : accent, Menu.ConfirmLeft);
    }

    private static void DrawBinding(Rgba accent) =>
        Modal(Menu.BindRefused ? "THE MENU USES THAT KEY, PICK ANOTHER" : "PRESS A KEY FOR " + Menu.Binding!.Label, "ESC CANCEL     DEL REMOVE HOTKEY", Menu.BindRefused ? Theme.Warn : accent, 0f);
}
