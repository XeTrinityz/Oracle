namespace CeZeY.Oracle;

/// <summary>The menu's state and controls. Keyboard and controller both drive it through <see cref="Press"/>.</summary>
internal static class Menu
{
    public const float RepeatDelay = 0.38f;
    public const float RepeatRate = 0.06f;
    private const float ConfirmSeconds = 10f;
    private const string OwnerName = "Oracle";

    /// <summary>Keys that drive the menu, which can't also be hotkeys.</summary>
    public static readonly Keys[] ControlKeys = { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Enter, Keys.Tab, Keys.PageUp, Keys.PageDown, Keys.Back, Keys.Delete, Keys.Insert, Keys.Escape };

    private static readonly Keys[] RepeatKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down };
    private static readonly float[] _held = { -1f, -1f, -1f, -1f };
    private static readonly MenuPage _activePage = new() { Title = "ACTIVE" };

    private static nint _inputPc;
    private static float _confirmLeft;

    public static bool IsOpen { get; private set; }

    public static int PageIndex { get; private set; } = 1;

    public static int RowIndex { get; private set; }

    public static int SubIndex { get; private set; }

    /// <summary>How deep the player is: 0 on the page list, 1 on a page's sub-tabs, 2 on the rows.</summary>
    public static int Level { get; private set; }

    public static bool InRows => Level == 2;

    public static int PageCount => Catalog.Pages.Count + 1;

    public static MenuPage Top => PageAt(PageIndex);

    public static MenuPage Current => Top.HasSub ? Top.Children[Math.Clamp(SubIndex, 0, Top.Children.Count - 1)] : Top;

    public static Feature? Selected => Rows.Count == 0 ? null : Rows[Math.Clamp(RowIndex, 0, Rows.Count - 1)].Item;

    /// <summary>Row awaiting a second press before it runs.</summary>
    public static Feature? Confirming { get; private set; }

    /// <summary>Row whose next key press becomes its hotkey.</summary>
    public static Feature? Binding { get; private set; }

    /// <summary>The last key pressed while binding belongs to the menu, so the prompt stays open.</summary>
    public static bool BindRefused { get; private set; }

    public static bool IsReserved(Keys key) => key == Settings.MenuKey || Array.IndexOf(ControlKeys, key) >= 0;

    /// <summary>Last command run, for the row's DONE or FAILED flash.</summary>
    public static Feature? Flashed { get; private set; }

    public static bool FlashOk { get; private set; }

    public static float FlashLeft;

    private static List<MenuRow> Rows => Current.Rows;

    public static bool OwnsScreen => Shared.MenuOwner == OwnerName;

    public static MenuPage PageAt(int index) => index == 0 ? _activePage : Catalog.Pages[index - 1];

    public static void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public static void Open()
    {
        if (IsOpen)
            return;

        foreach (Module m in App.Modules)
            Safe.Run(m.Name + " menu", m.MenuOpening);

        IsOpen = true;
        RebuildActivePage();
        ResumePosition();
        Confirming = null;
        Binding = null;
        Shared.MenuOwner = OwnerName;
        MenuView.Opened();
        HoldMovement();
    }

    /// <summary>Reopens where the player left off. The ACTIVE page can change while closed, so its row is re-checked.</summary>
    private static void ResumePosition()
    {
        if (RowIndex >= Rows.Count || Rows[RowIndex].Item == null)
            RowIndex = NextRow(-1, 1);
        if (Level == 2 && Selected == null)
            Level = 0;
    }

    public static void Close()
    {
        IsOpen = false;
        Confirming = null;
        Binding = null;
        Array.Fill(_held, -1f);
        Pad.MenuClosed();

        if (OwnsScreen)
            Shared.MenuOwner = null;

        ReleaseMovement();
    }

    private static void HoldMovement()
    {
        Safe.Run("menu input", () =>
        {
            if (World.Pc is { } pc)
            {
                pc.IgnoreMoveInput(true);
                _inputPc = pc.Ptr;
            }
        });
    }

    /// <summary>Hands movement back to the game. Also used while the menu stays open for actions that wait on the player.</summary>
    public static void ReleaseMovement()
    {
        Safe.Run("menu input", () =>
        {
            if (_inputPc != 0 && World.Pc is { } pc && pc.Ptr == _inputPc)
                pc.IgnoreMoveInput(false);
            _inputPc = 0;
        });
    }

    public static void Tick(float dt)
    {
        if (!IsOpen)
            return;

        if (!OwnsScreen)
        {
            Close();
            return;
        }

        if (Confirming != null)
        {
            _confirmLeft -= dt;
            if (_confirmLeft <= 0f)
                Confirming = null;
        }

        for (int i = 0; i < RepeatKeys.Length; i++)
        {
            if (_held[i] < 0f)
                continue;

            if ((Native.GetAsyncKeyState((int)RepeatKeys[i]) & 0x8000) == 0)
            {
                _held[i] = -1f;
                continue;
            }

            _held[i] += dt;
            if (_held[i] >= RepeatDelay)
            {
                _held[i] -= RepeatRate;
                Press(RepeatKeys[i], true);
            }
        }
    }

    /// <summary>Handles a menu control. Returns true when the key belonged to the menu.</summary>
    public static bool Press(Keys key, bool repeat)
    {
        if (Binding != null)
        {
            if (!repeat)
                FinishBinding(key);
            return true;
        }

        Feature? f = Selected;
        if (Confirming != null)
        {
            if (repeat)
                return true;
            if (key == Keys.Enter && f == Confirming)
                Activate(f);
            else if (key != Keys.Enter)
                Confirming = null;
            return true;
        }

        if (!repeat)
        {
            int r = Array.IndexOf(RepeatKeys, key);
            if (r >= 0)
                _held[r] = 0f;
        }

        if (!InRows)
            return PressOnRail(key);

        switch (key)
        {
            case Keys.Up:
                RowIndex = NextRow(RowIndex, -1);
                return true;
            case Keys.Down:
                RowIndex = NextRow(RowIndex, 1);
                return true;
            case Keys.Left:
                if (f is Command)
                {
                    if (!repeat)
                        GoBack();
                }
                else if (f != null && (!repeat || Repeats(f)) && f.Step(-1))
                    Catalog.Commit(f);

                return true;
            case Keys.Right:
                if (f != null && (!repeat || Repeats(f)) && f.Step(1))
                    Catalog.Commit(f);
                return true;
            case Keys.Enter:
                Enter(f);
                return true;
            case Keys.Tab:
            case Keys.PageDown:
                TurnPage(1);
                return true;
            case Keys.PageUp:
                TurnPage(-1);
                return true;
            case Keys.Delete:
                ResetRow(f);
                return true;
            case Keys.Insert:
                if (f is { Bindable: true, IsSetting: false })
                {
                    Binding = f;
                    BindRefused = false;
                }

                return true;
            case Keys.Back:
                GoBack();
                return true;
        }

        return false;
    }

    private static bool Repeats(Feature f) => f is Slider or Choice { HoldToScroll: true };

    private static bool PressOnRail(Keys key)
    {
        switch (key)
        {
            case Keys.Up:
            case Keys.PageUp:
                TurnPage(-1);
                return true;
            case Keys.Down:
            case Keys.Tab:
            case Keys.PageDown:
                TurnPage(1);
                return true;
            case Keys.Enter:
            case Keys.Right:
                if (Level == 0 && Top.HasSub)
                {
                    Level = 1;
                    RowIndex = NextRow(-1, 1);
                }
                else if (Selected != null)
                {
                    Level = 2;
                    MenuView.RowsEntered();
                }

                return true;
            case Keys.Left:
                if (Level == 1)
                    GoBack();
                return true;
            case Keys.Back:
                if (Level == 1)
                    GoBack();
                else
                    Close();
                return true;
        }

        return false;
    }

    /// <summary>Steps back one level: rows to sub-tabs (when the page has them) to the page list.</summary>
    private static void GoBack() => Level = Level == 2 && Top.HasSub ? 1 : 0;

    private static void ResetRow(Feature? f)
    {
        if (f == null || f is Command || f.IsDefault)
            return;

        f.ResetValue();
        Catalog.Commit(f);
    }

    private static void Enter(Feature? f)
    {
        switch (f)
        {
            case null:
                break;
            case Command:
                Activate(f);
                break;
            case Slider:
                ResetRow(f);
                break;
            default:
                if (f.Step(1))
                    Catalog.Commit(f);
                break;
        }
    }

    private static void Activate(Feature f)
    {
        if (f is not Command c)
            return;

        if ((c.ChangesSave || c.NeedsConfirm) && Confirming != c)
        {
            Confirming = c;
            _confirmLeft = ConfirmSeconds;
            return;
        }

        Confirming = null;
        FlashOk = Catalog.Run(c);
        Flashed = c;
        FlashLeft = 1.25f;
    }

    private static void FinishBinding(Keys key)
    {
        Feature f = Binding!;
        if (key != Keys.Escape && key != Keys.Delete && IsReserved(key))
        {
            BindRefused = true;
            return;
        }

        Binding = null;
        BindRefused = false;
        if (key == Keys.Delete)
            Settings.Unbind(f);
        else if (key != Keys.Escape)
        {
            string[] others = Settings.SharingKey(f, key).ToArray();
            Settings.Bind(f, key);
            if (others.Length > 0)
                Overlay.Notice(key.ToString().ToUpperInvariant() + " IS SHARED", "ALSO " + string.Join(", ", others), true);
        }
    }

    private static void TurnPage(int delta)
    {
        if (Level > 0 && Top.HasSub)
        {
            int count = Top.Children.Count;
            SubIndex = (SubIndex + delta + count) % count;
        }
        else
        {
            PageIndex = (PageIndex + delta + PageCount) % PageCount;
            SubIndex = 0;
            if (PageIndex == 0)
                RebuildActivePage();
            if (Level == 2 && Top.HasSub)
                Level = 1;
        }

        RowIndex = NextRow(-1, 1);
        if (Level == 2 && Selected == null)
            Level = 0;
        Confirming = null;
        MenuView.PageTurned();
    }

    private static IEnumerable<MenuPage> Leaves()
    {
        foreach (MenuPage page in Catalog.Pages)
        {
            if (!page.HasSub)
                yield return page;

            foreach (MenuPage child in page.Children)
                yield return child;
        }
    }

    private static void RebuildActivePage()
    {
        _activePage.Rows.Clear();
        var listed = new HashSet<Feature>();   // a row shared by two pages shows once
        foreach (MenuPage page in Leaves())
        {
            bool headed = false;
            foreach (MenuRow row in page.Rows)
            {
                if (row.Item is not { Active: true } || !listed.Add(row.Item))
                    continue;

                if (!headed)
                {
                    headed = true;
                    _activePage.Rows.Add(new MenuRow { Header = page.Title });
                }

                _activePage.Rows.Add(row);
            }
        }

        if (_activePage.Rows.Count == 0)
            _activePage.Rows.Add(new MenuRow { Header = "NOTHING RUNNING" });
    }

    private static int NextRow(int from, int dir)
    {
        List<MenuRow> rows = Rows;
        int i = from;
        for (int guard = 0; guard < rows.Count; guard++)
        {
            i = (i + dir + rows.Count) % rows.Count;
            if (rows[i].Item != null)
                return i;
        }

        return 0;
    }
}
