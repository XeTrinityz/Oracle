using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// Draws the menu as floating columns over the game: a slim page list, and once a page is entered, a second
/// column with its rows and the selected row's help beneath. Nothing shows until it's needed.
/// </summary>
internal static class MenuView
{
    private const float RailW = 186f;
    private const float RailRowH = 28f;
    private const float RailHeadH = 56f;
    private const float EditionScale = 0.5f;

    /// <summary>The fork's name, drawn under ORACLE in the rail header.</summary>
    public const string Edition = "TRINITY EDITION";
    private const float SubW = 150f;
    private const float ColumnW = 430f;
    private const float ColumnGap = 8f;
    private const float RowH = 27f;
    private const float GroupH = 25f;
    private const float MaxBodyH = 470f;
    private const float Inset = 13f;
    private const float ControlW = 150f;
    private const int MaxHelpLines = 4;
    private const float HelpLineH = 17f;

    private const float BackAlpha = 0.84f;
    private const float LabelScale = 0.76f;
    private const float SmallScale = 0.66f;

    private static float _scroll;
    private static float _selY = -1f;
    private static float _railY = -1f;
    private static float _column;
    private static float _sub;
    private static float _subY = -1f;

    public static void Opened()
    {
        _selY = -1f;
        _railY = -1f;
        _subY = -1f;
        _column = Menu.InRows ? 1f : 0f;
        _sub = Menu.Top.HasSub && Menu.Level > 0 ? 1f : 0f;
    }

    public static void PageTurned()
    {
        _selY = -1f;
        _scroll = 0f;
    }

    public static void RowsEntered()
    {
        _selY = -1f;
        _scroll = 0f;
    }

    private static float RowHeight(MenuRow r) => S(r.Item == null ? GroupH : RowH);

    public static void Draw(float anim)
    {
        float screenScale = Scale;
        Scale *= Theme.MenuSize;
        try
        {
            float e = Ease.Out(anim);
            Alpha = e;
            Rgba accent = Theme.Accent;

            float railH = S(RailHeadH) + S(RailRowH) * Menu.PageCount + S(6);
            float x = S(28) - S(18) * (1f - e);
            float y = Math.Max(S(20), (Gfx.H - railH - S(160)) * 0.5f);

            DrawRail(x, y, railH, accent);

            float step = Dt * 8f;
            _sub = Toward(_sub, Menu.Top.HasSub && Menu.Level > 0 ? 1f : 0f, step);
            _column = Toward(_column, Menu.InRows ? 1f : 0f, step);

            float next = x + S(RailW) + S(ColumnGap);
            float bottom = y + railH;
            if (_sub > 0f && Menu.Top.HasSub)
            {
                float c = Ease.Out(_sub);
                float outer = Alpha;
                Alpha *= c;
                bottom = Math.Max(bottom, DrawSubTabs(next - S(16) * (1f - c), y, accent));
                Alpha = outer;
                next += (S(SubW) + S(ColumnGap)) * c;
            }

            if (_column > 0f)
            {
                float c = Ease.Out(_column);
                float outer = Alpha;
                Alpha *= c;
                bottom = Math.Max(bottom, DrawColumn(next - S(16) * (1f - c), y, accent));
                Alpha = outer;
            }

            DrawHints(x, bottom + S(8));

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

    private static float DrawSubTabs(float x, float y, Rgba accent)
    {
        MenuPage top = Menu.Top;
        float w = S(SubW);
        float rowH = S(RailRowH);
        float h = S(RailHeadH) + rowH * top.Children.Count + S(6);
        Backing(x, y, w, h, accent);
        TextMid(x + S(Inset), y + S(RailHeadH) * 0.5f, top.Title, accent, SmallScale);

        float rows = y + S(RailHeadH);
        float target = rows + Menu.SubIndex * rowH;
        _subY = Follow(_subY, target, 24f);

        bool focused = Menu.Level == 1;
        Rect(x, _subY, S(2), rowH, focused ? Theme.Hi : accent);
        GradientX(x + S(2), _subY, w - S(2), rowH, accent, focused ? 0.26f : 0.12f, 0f);

        for (int i = 0; i < top.Children.Count; i++)
        {
            MenuPage page = top.Children[i];
            float cy = rows + rowH * (i + 0.5f);
            bool on = i == Menu.SubIndex;
            TextMid(x + S(Inset), cy, page.Title, on ? Theme.Hi : Theme.Mid, LabelScale);
            if (page.ActiveCount > 0)
                TextRight(x + w - S(Inset), cy, page.ActiveCount.ToString(), on ? accent : Theme.Low, SmallScale);
        }

        return y + h;
    }

    private static void Backing(float x, float y, float w, float h, Rgba accent)
    {
        Rect(x, y, w, h, Theme.Panel, BackAlpha);
        Rect(x, y, w, S(1), accent, 0.7f);
    }

    private static void DrawRail(float x, float y, float h, Rgba accent)
    {
        float w = S(RailW);
        Backing(x, y, w, h, accent);

        // ORACLE with the edition tag beneath it, the pair centred in the header.
        float nameH = TextH(0.95f);
        float tagH = TextH(EditionScale);
        float nameY = y + (S(RailHeadH) - nameH - S(2) - tagH) * 0.5f;
        Spaced(x + S(Inset), nameY, "ORACLE", Theme.Hi, 0.95f, 4f);
        Spaced(x + S(Inset) + S(1), nameY + nameH + S(2), Edition, accent, EditionScale, 1.5f);
        int active = Catalog.ActiveCount;
        TextRight(x + w - S(Inset), nameY + nameH * 0.5f, active == 0 ? "STANDBY" : active + " ON", active > 0 ? accent : Theme.Low, SmallScale);

        float top = y + S(RailHeadH);
        float rowH = S(RailRowH);
        float target = top + Menu.PageIndex * rowH;
        _railY = Follow(_railY, target, 24f);

        bool railFocus = Menu.Level == 0;
        Rect(x, _railY, S(2), rowH, railFocus ? Theme.Hi : accent);
        GradientX(x + S(2), _railY, w - S(2), rowH, accent, railFocus ? 0.26f : 0.12f, 0f);

        for (int i = 0; i < Menu.PageCount; i++)
        {
            MenuPage page = Menu.PageAt(i);
            float cy = top + rowH * (i + 0.5f);
            bool on = i == Menu.PageIndex;
            TextMid(x + S(Inset), cy, page.Title, on ? Theme.Hi : Theme.Mid, LabelScale);

            int n = i == 0 ? active : page.ActiveCount;
            if (n > 0)
                TextRight(x + w - S(Inset), cy, n.ToString(), on ? accent : Theme.Low, SmallScale);
        }
    }

    /// <summary>Draws the rows and help. Returns the column's bottom edge.</summary>
    private static float DrawColumn(float x, float y, Rgba accent)
    {
        List<MenuRow> rows = Menu.Current.Rows;
        float w = S(ColumnW);
        float pad = S(6);
        float content = 0f;
        float selTop = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == Menu.RowIndex)
                selTop = content;
            content += RowHeight(rows[i]);
        }

        Feature? selected = Menu.Selected;
        List<string> help = HelpLines(selected, w - S(Inset) * 2f);
        float helpH = S(HelpLineH) * help.Count + S(14);
        float bodyH = Math.Min(content, Math.Min(S(MaxBodyH), Gfx.H - y - helpH - S(56)));

        Backing(x, y, w, bodyH + pad * 2f, accent);
        float bodyY = y + pad;

        float want = _scroll;
        if (content <= bodyH)
            want = 0f;
        else
        {
            float above = Menu.RowIndex > 0 && rows[Menu.RowIndex - 1].Item == null ? S(GroupH) : 0f;
            if (selTop - above < want)
                want = selTop - above;
            if (selTop + S(RowH) > want + bodyH)
                want = selTop + S(RowH) - bodyH;
            want = Math.Clamp(want, 0f, content - bodyH);
        }

        _scroll += (want - _scroll) * Ease.Follow(18f, Dt);
        if (MathF.Abs(want - _scroll) < 0.5f)
            _scroll = want;

        float selY = bodyY + selTop - _scroll;
        _selY = Follow(_selY, selY, 26f);
        if (selected != null)
        {
            float a = Math.Max(_selY, bodyY);
            float b = Math.Min(_selY + S(RowH), bodyY + bodyH);
            if (b > a)
            {
                GradientX(x, a, w, b - a, accent, 0.2f, 0f);
                Rect(x, a, S(2), b - a, Theme.Hi);
            }
        }

        float left = x + S(Inset);
        float right = x + w - S(Inset);
        float ry = bodyY - _scroll;
        for (int i = 0; i < rows.Count; i++)
        {
            MenuRow row = rows[i];
            float rh = RowHeight(row);
            float at = ry;
            ry += rh;

            if (at + rh <= bodyY || at >= bodyY + bodyH)
                continue;

            float fade = Math.Clamp(Math.Min((at + rh - bodyY) / S(10), (bodyY + bodyH - at) / S(10)), 0f, 1f);
            float outer = Alpha;
            Alpha *= fade;
            if (row.Item == null)
                DrawGroup(row.Header!, left, right, at + rh * 0.5f, accent);
            else
                DrawRow(row.Item, i, left, right, at + rh * 0.5f, accent);
            Alpha = outer;
        }

        if (content > bodyH)
        {
            float thumbH = Math.Max(S(20), bodyH * bodyH / content);
            float thumbY = bodyY + (bodyH - thumbH) * (_scroll / (content - bodyH));
            Rect(x + w - S(3), thumbY, S(2), thumbH, accent, 0.8f);
        }

        float helpY = y + bodyH + pad * 2f + S(4);
        Rect(x, helpY, w, helpH, Theme.Panel, BackAlpha);
        Rgba helpCol = selected is { ChangesSave: true } ? Theme.Warn : Theme.Mid;
        float ly = helpY + S(7);
        foreach (string line in help)
        {
            Text(left, ly, line, helpCol, SmallScale);
            ly += S(HelpLineH);
        }

        return helpY + helpH;
    }

    private static List<string> HelpLines(Feature? f, float width)
    {
        var lines = new List<string>();
        string rest = f == null ? "Everything you switch on shows up here." : f.Help;
        if (f is { ChangesSave: true })
            rest += " Changes your save and can't be undone.";

        while (rest.Length > 0 && lines.Count < MaxHelpLines)
        {
            string next = FitLine(rest, width, SmallScale, out string line);
            if (next.Length == rest.Length)
                break;

            lines.Add(next.Length > 0 && lines.Count == MaxHelpLines - 1 ? line + "..." : line);
            rest = next;
        }

        if (f is { Bindable: true, IsSetting: false } && Settings.KeyFor(f) is { Length: > 0 } key)
            lines.Add("HOTKEY " + key);
        return lines;
    }

    private static void DrawGroup(string label, float left, float right, float cy, Rgba accent)
    {
        Rgba col = label.Contains("SAVE", StringComparison.Ordinal) ? Theme.Warn : accent;
        float tw = TextMid(left, cy + S(3), label, col, SmallScale, 0.9f);
        Rect(left + tw + S(10), cy + S(3), right - left - tw - S(10), S(1), Theme.White, 0.07f);
    }

    private static void DrawRow(Feature f, int index, float left, float right, float cy, Rgba accent)
    {
        bool sel = index == Menu.RowIndex;
        int key = ((Menu.PageIndex + 1) * 32 + Menu.SubIndex) * 1024 + index * 8;
        float shift = Approach(key, sel ? 1f : 0f, 20f);
        float lw = TextMid(left + S(4) * shift, cy, f.Label, Rgba.Mix(Theme.Mid, Theme.Hi, shift), LabelScale);

        if (f.Active)
            Rect(left - S(7), cy - S(2), S(3), S(4), accent, 0.9f);

        if (f is Toggle t)
            DrawSwitch(t, right, cy, key, accent);
        else if (f is Slider s)
            DrawSlider(s, right, cy, sel, key, accent);
        else if (f is Choice c)
            DrawChoice(c, right, cy, sel, accent);
        else
            DrawCommand(f, right, cy, sel, left + S(4) * shift + lw, accent);
    }

    private static void DrawSwitch(Toggle f, float right, float cy, int key, Rgba accent)
    {
        float t = Approach(key + 1, f.On ? 1f : 0f, 18f);
        float tw = S(26);
        float th = S(10);
        float tx = right - tw;
        Rect(tx, cy - th * 0.5f, tw, th, Rgba.Mix(Theme.Off, accent, t), 0.95f);
        Rect(tx + t * (tw - th), cy - th * 0.5f, th, th, Rgba.Mix(Theme.Mid, Theme.White, t));
    }

    private static void DrawSlider(Slider f, float right, float cy, bool sel, int key, Rgba accent)
    {
        float range = f.Max - f.Min;
        float t = range <= 0f ? 0f : Math.Clamp((f.Value - f.Min) / range, 0f, 1f);
        float at = Approach(key + 2, t, 20f);

        float trackW = S(ControlW) * 0.5f;
        float tx = right - S(ControlW);
        Rect(tx, cy - S(1), trackW, S(2), Theme.White, 0.14f);
        Rect(tx, cy - S(1), trackW * at, S(2), f.IsDefault ? Theme.Mid : accent, sel ? 1f : 0.75f);
        Rect(tx + trackW * at - S(1), cy - S(5), S(3), S(10), sel ? Theme.Hi : Theme.Mid);

        TextRight(right, cy, f.State, f.IsDefault ? (sel ? Theme.Hi : Theme.Mid) : accent, LabelScale);
    }

    private static void DrawChoice(Choice f, float right, float cy, bool sel, Rgba accent)
    {
        string name = f.State;
        float scale = LabelScale;
        float room = S(ControlW) - S(30);
        if (TextW(name, scale) > room)
            scale *= Math.Max(0.72f, room / TextW(name, scale));

        float arrows = sel ? S(13) : 0f;
        float w = TextRight(right - arrows, cy, name, f.IsDefault ? Theme.Mid : accent, scale);
        if (f.Swatch != null)
            Rect(right - arrows - w - S(13), cy - S(4), S(8), S(8), f.Swatch(f.Index));

        if (sel)
        {
            TextRight(right, cy, ">", Theme.Hi, LabelScale);
            TextRight(right - arrows - w - (f.Swatch != null ? S(17) : S(5)), cy, "<", Theme.Hi, LabelScale);
        }
    }

    private static void DrawCommand(Feature f, float right, float cy, bool sel, float labelEnd, Rgba accent)
    {
        if (f.ChangesSave)
            TextMid(labelEnd + S(8), cy, "SAVE", Theme.Warn, 0.58f);

        bool flashing = Menu.Flashed == f && Menu.FlashLeft > 0f;
        bool confirming = Menu.Confirming == f;
        string text = flashing ? (Menu.FlashOk ? "DONE" : "FAILED") : confirming ? "CONFIRM?" : sel ? "RUN" : "";
        Rgba col = flashing ? (Menu.FlashOk ? accent : Theme.Warn) : confirming ? Theme.Warn : Theme.Hi;
        if (text.Length > 0)
            TextRight(right, cy, text, col, LabelScale);
    }

    private static void DrawHints(float x, float y)
    {
        string hints;
        if (Pad.InUse)
            hints = Menu.InRows ? "A SELECT   X RESET   LB/RB PAGE   B BACK" : Menu.Level == 1 ? "A OPEN   LB/RB TAB   B BACK" : "A OPEN   LB/RB PAGE   B CLOSE";
        else if (Menu.InRows)
            hints = "ENTER SELECT   DEL RESET   INS BIND   BACKSPACE BACK";
        else if (Menu.Level == 1)
            hints = "ENTER OPEN   BACKSPACE BACK";
        else
            hints = "ENTER OPEN   " + Settings.MenuKey.ToString().ToUpperInvariant() + " CLOSE";

        Text(x, y, hints, Theme.Hi, 0.6f, 0.75f);
    }

    private static void Modal(string title, string keys, Rgba edge)
    {
        float w = Math.Max(TextW(title, 0.9f), TextW(keys, SmallScale)) + S(56);
        float h = S(84);
        float x = (Gfx.W - w) * 0.5f;
        float y = (Gfx.H - h) * 0.5f;

        Rect(x, y, w, h, Theme.Panel);
        Rect(x, y, w, S(1), edge, 0.9f);
        TextCentre(x + w * 0.5f, y + S(18), title, Theme.Hi, 0.9f);
        TextCentre(x + w * 0.5f, y + h - S(28), keys, Theme.Mid, SmallScale);
    }

    private static void DrawConfirm(Rgba accent)
    {
        Feature f = Menu.Confirming!;
        string title = f.ConfirmPrompt?.Invoke() ?? (f.ChangesSave ? "CHANGE YOUR SAVE: " + f.Label + "?" : f.Label + "?");
        Modal(title, Pad.InUse ? "A CONFIRM     ANY OTHER BUTTON CANCEL" : "ENTER CONFIRM     ANY OTHER KEY CANCEL", f.ChangesSave ? Theme.Warn : accent);
    }

    private static void DrawBinding(Rgba accent) =>
        Modal(Menu.BindRefused ? "THE MENU USES THAT KEY, PICK ANOTHER" : "PRESS A KEY FOR " + Menu.Binding!.Label, "ESC CANCEL     DEL REMOVE HOTKEY", Menu.BindRefused ? Theme.Warn : accent);
}
