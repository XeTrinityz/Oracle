namespace CeZeY.Oracle;

/// <summary>The menu's state and controls. Keyboard and controller both drive it through <see cref="Press"/>.</summary>
internal static class Menu
{
    public const float RepeatDelay = 0.38f;
    public const float RepeatRate = 0.06f;
    public const float ConfirmSeconds = 10f;
    public const string OwnerName = "Oracle";
    private const int MaxResults = 80;

    /// <summary>ACTIVE sits above the catalog's pages.</summary>
    private const int FixedPages = 1;

    /// <summary>
    /// Puts the selected row on the favourites radial, or moves it there. Not reserved, so it can still be a hotkey
    /// while the menu is closed.
    /// </summary>
    public const Keys FavouriteKey = Keys.Home;

    /// <summary>Keys that drive the menu, which can't also be hotkeys.</summary>
    public static readonly Keys[] ControlKeys = { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Enter, Keys.Tab, Keys.PageUp, Keys.PageDown, Keys.Back, Keys.Delete, Keys.Insert, Keys.Escape };

    /// <summary>Keys the open menu also listens to: letters and digits type a search, plus the favourite key.</summary>
    public static readonly Keys[] TypingKeys = BuildTypingKeys();

    private static readonly Keys[] RepeatKeys = { Keys.Left, Keys.Right, Keys.Up, Keys.Down };
    private static readonly float[] _held = { -1f, -1f, -1f, -1f };
    private static readonly MenuPage _activePage = new() { Title = "ACTIVE" };
    private static readonly MenuPage _searchPage = new() { Title = "SEARCH" };
    private static readonly Dictionary<MenuPage, int> _rowMemory = new();
    private static readonly List<SearchEntry> _index = new();

    private static nint _inputPc;
    private static float _confirmLeft;
    private static (int Page, int Sub, int Group, int Row, int Level) _beforeSearch;

    private readonly record struct SearchEntry(Feature Item, string Path, string Label, string Words, string Help);

    public static bool IsOpen { get; private set; }

    public static int PageIndex { get; private set; } = FixedPages;

    public static int RowIndex { get; private set; }

    public static int SubIndex { get; private set; }

    /// <summary>Index in the top page's tabs of the opened tab whose own tabs are listed, or -1. See TabOwner.</summary>
    private static int _group = -1;

    /// <summary>How deep the player is: 0 on the page list, 1 on a page's sub-tabs, 2 on the rows.</summary>
    public static int Level { get; private set; }

    public static bool InRows => Level == 2;

    /// <summary>The search being typed, or null when not searching.</summary>
    public static string? Query { get; private set; }

    public static bool Searching => Query != null;

    public static int PageCount => Catalog.Pages.Count + FixedPages;

    public static MenuPage Top => PageAt(PageIndex);

    /// <summary>
    /// The page whose tabs the tab column lists: the top page, or a tab of it that has its own tabs once the player
    /// opens it (WORLD > SPAWNER). Only one level of these is supported.
    /// </summary>
    public static MenuPage TabOwner =>
        Top.HasSub && _group >= 0 && _group < Top.Children.Count && Top.Children[_group].HasSub ? Top.Children[_group] : Top;

    /// <summary>The tab column is showing the tabs of a tab, so BACK returns to the top page's tabs.</summary>
    public static bool InGroup => TabOwner != Top;

    /// <summary>The tab under the cursor in the tab column.</summary>
    private static MenuPage HoveredTab => TabOwner.Children[Math.Clamp(SubIndex, 0, TabOwner.Children.Count - 1)];

    /// <summary>
    /// The page whose rows show in the column: the search results while searching. A tab with its own tabs, not
    /// yet opened, previews its first.
    /// </summary>
    public static MenuPage Current => Searching ? _searchPage : !Top.HasSub ? Top : HoveredTab.HasSub ? HoveredTab.Children[0] : HoveredTab;

    public static bool IsActivePage(MenuPage page) => page == _activePage;

    public static Feature? Selected => Rows.Count == 0 ? null : Rows[Math.Clamp(RowIndex, 0, Rows.Count - 1)].Item;

    /// <summary>Row awaiting a second press before it runs.</summary>
    public static Feature? Confirming { get; private set; }

    /// <summary>How much of the confirmation window is left, 1 down to 0.</summary>
    public static float ConfirmLeft => Math.Clamp(_confirmLeft / ConfirmSeconds, 0f, 1f);

    /// <summary>Row whose next key press becomes its hotkey.</summary>
    public static Feature? Binding { get; private set; }

    /// <summary>The last key pressed while binding belongs to the menu, so the prompt stays open.</summary>
    public static bool BindRefused { get; private set; }

    public static bool IsReserved(Keys key) => key == Settings.MenuKey || key == Radial.Key || Array.IndexOf(ControlKeys, key) >= 0;

    /// <summary>Last command run, for the row's DONE or FAILED flash.</summary>
    public static Feature? Flashed { get; private set; }

    public static bool FlashOk { get; private set; }

    public static float FlashLeft;

    /// <summary>Last row whose value changed from the menu, and when, for the row flash and the uplink trace.</summary>
    public static Feature? Changed { get; private set; }

    public static double ChangedAt;

    private static List<MenuRow> Rows => Current.Rows;

    private static void Commit(Feature f)
    {
        Catalog.Commit(f);
        Changed = f;
        ChangedAt = Gfx.Time;
    }

    public static bool OwnsScreen => Shared.MenuOwner == OwnerName;

    public static MenuPage PageAt(int index) => index switch
    {
        0 => _activePage,
        _ => Catalog.Pages[index - FixedPages],
    };

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
        PageIndex = Math.Clamp(PageIndex, 0, PageCount - 1);
        RebuildActivePage();
        ResumePosition();
        Confirming = null;
        Binding = null;
        Shared.MenuOwner = OwnerName;
        MenuView.Opened();
        HoldInput();
    }

    /// <summary>Reopens where the player left off. ACTIVE can change while closed, so the row is re-checked.</summary>
    private static void ResumePosition()
    {
        if (RowIndex >= Rows.Count || Rows[RowIndex].Item == null)
            RowIndex = NextRow(-1, 1);
        if (Level == 2 && Selected == null)
            Level = 0;
    }

    public static void Close()
    {
        if (Searching)
            EndSearch();

        IsOpen = false;
        Confirming = null;
        Binding = null;
        Radial.EndArrange();
        Array.Fill(_held, -1f);
        Pad.MenuClosed();

        if (OwnsScreen)
            Shared.MenuOwner = null;

        ReleaseMovement();
    }

    /// <summary>
    /// Holds movement and mutes the game's bindings on the keys the menu uses, so typing a search or pressing ENTER
    /// doesn't also fire gadgets and moves. Oracle reads its own keys from window messages, so the menu still hears them.
    /// The radial also mutes the mouse (<paramref name="pointer"/>), so pointing and clicking don't turn the camera or strike.
    /// </summary>
    public static void HoldInput(bool pointer = false, bool escape = false)
    {
        Safe.Run("menu input", () =>
        {
            if (World.Pc is { } pc)
            {
                pc.IgnoreMoveInput(true);
                _inputPc = pc.Ptr;
            }
        });
        Safe.Run("menu keys", () => GameKeys.Mute(pointer, escape));
    }

    /// <summary>Hands input back to the game. Also used while the menu stays open for actions that wait on the player.</summary>
    public static void ReleaseMovement()
    {
        Safe.Run("menu keys", GameKeys.Unmute);
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

            if (!Keyboard.IsDown(RepeatKeys[i]))
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
        if (Radial.Arranging)
            return Radial.EditorPress(key, repeat);

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

        if (TypedChar(key) is char typed)
        {
            if (!repeat)
                Type(typed);
            return true;
        }

        if (Searching && PressInSearch(key))
            return true;

        if (key == FavouriteKey)
        {
            if (!repeat)
                Favourite(f);
            return true;
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
                else if (f != null && (!repeat || Repeats(f)) && !Blocked(f, repeat) && f.Step(-1))
                    Commit(f);

                return true;
            case Keys.Right:
                if (f != null && (!repeat || Repeats(f)) && !Blocked(f, repeat) && f.Step(1))
                    Commit(f);
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
                    RowIndex = Recall();
                }
                else if (Level == 1 && !InGroup && HoveredTab.HasSub)
                {
                    // A tab with its own tabs: the tab column now lists those, and BACK comes out again.
                    Remember();
                    _group = SubIndex;
                    SubIndex = 0;
                    RowIndex = Recall();
                    Confirming = null;
                    MenuView.PageTurned();
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

    /// <summary>
    /// Steps back one level: rows to sub-tabs (when the page has them), a tab's own tabs to the page's tabs, then to
    /// the page list. A search just ends.
    /// </summary>
    private static void GoBack()
    {
        if (Searching)
            EndSearch();
        else if (Level == 1 && InGroup)
        {
            Remember();
            SubIndex = _group;
            _group = -1;
            RowIndex = Recall();
            Confirming = null;
            MenuView.PageTurned();
        }
        else
            Level = Level == 2 && Top.HasSub ? 1 : 0;
    }

    /// <summary>A disabled row ignores the key; the first press (not its repeats) says why.</summary>
    private static bool Blocked(Feature f, bool repeat)
    {
        if (f.Disabled == null)
            return false;
        if (!repeat)
            Catalog.Refused(f);
        return true;
    }

    private static void ResetRow(Feature? f)
    {
        if (f == null || f is Command || f.IsDefault || Blocked(f, false))
            return;

        f.ResetValue();
        Commit(f);
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
                if (!Blocked(f, false) && f.Step(1))
                    Commit(f);
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
        Changed = c;
        ChangedAt = Gfx.Time;
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
        Remember();
        if (Level > 0 && Top.HasSub)
        {
            int count = TabOwner.Children.Count;
            SubIndex = (SubIndex + delta + count) % count;
        }
        else
        {
            PageIndex = (PageIndex + delta + PageCount) % PageCount;
            SubIndex = 0;
            _group = -1;
            if (PageIndex == 0)
                RebuildActivePage();
            if (Level == 2 && Top.HasSub)
                Level = 1;
        }

        RowIndex = Recall();
        if (Level == 2 && Selected == null)
            Level = 0;
        Confirming = null;
        MenuView.PageTurned();
    }

    /// <summary>Notes the row the player is on, so coming back to this page lands there again.</summary>
    private static void Remember()
    {
        if (!Searching)
            _rowMemory[Current] = RowIndex;
    }

    private static int Recall() =>
        _rowMemory.TryGetValue(Current, out int row) && row < Rows.Count && Rows[row].Item != null ? row : NextRow(-1, 1);

    private static IEnumerable<(MenuPage Page, string Path)> Leaves() => Catalog.Pages.SelectMany(p => p.Leaves());

    private static void RebuildActivePage()
    {
        _activePage.Rows.Clear();
        var listed = new HashSet<Feature>();   // a row shared by two pages shows once
        foreach ((MenuPage page, _) in Leaves())
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

    /// <summary>
    /// HOME on a row: opens the radial editor with the row in hand, to place it in any slot on any page, or to
    /// move or remove it if it's already there.
    /// </summary>
    private static void Favourite(Feature? f)
    {
        if (f == null)
            return;

        if (!Favourites.CanHold(f))
        {
            Overlay.Notice(f.Label, "CHANGES YOUR SAVE, SO IT CAN'T GO ON THE RADIAL", false);
            return;
        }

        Confirming = null;
        Radial.BeginArrange(f);
    }

    private static Keys[] BuildTypingKeys()
    {
        var keys = new List<Keys> { Keys.Space, FavouriteKey };
        for (Keys k = Keys.A; k <= Keys.Z; k++)
            keys.Add(k);
        for (Keys k = Keys.D0; k <= Keys.D9; k++)
            keys.Add(k);
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++)
            keys.Add(k);
        return keys.ToArray();
    }

    /// <summary>The character a key types into a search. Space only counts once a search is under way.</summary>
    private static char? TypedChar(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z)
            return (char)('A' + (key - Keys.A));
        if (key >= Keys.D0 && key <= Keys.D9)
            return (char)('0' + (key - Keys.D0));
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return (char)('0' + (key - Keys.NumPad0));
        if (key == Keys.Space && Query is { Length: > 0 } q && q[^1] != ' ')
            return ' ';
        return null;
    }

    private static void Type(char ch)
    {
        if (!Searching)
        {
            Remember();
            _beforeSearch = (PageIndex, SubIndex, _group, RowIndex, Level);
            BuildIndex();
            Query = "";
            Level = 2;
        }

        if (Query!.Length < 32)
            Query += ch;
        RunSearch();
    }

    /// <summary>Keys with a search-specific meaning. Everything else works on the results as on any page.</summary>
    private static bool PressInSearch(Keys key)
    {
        switch (key)
        {
            case Keys.Back:
                if (Query!.Length <= 1)
                    EndSearch();
                else
                {
                    Query = Query.Substring(0, Query.Length - 1).TrimEnd();
                    RunSearch();
                }

                return true;
            case Keys.Escape:
                EndSearch();
                return true;
            case Keys.Tab:
            case Keys.PageDown:
            case Keys.PageUp:
                EndSearch();
                TurnPage(key == Keys.PageUp ? -1 : 1);
                return true;
        }

        return false;
    }

    private static void EndSearch()
    {
        Query = null;
        _searchPage.Rows.Clear();
        (PageIndex, SubIndex, _group, RowIndex, Level) = _beforeSearch;
        PageIndex = Math.Clamp(PageIndex, 0, PageCount - 1);
        if (RowIndex >= Rows.Count || Rows[RowIndex].Item == null)
            RowIndex = NextRow(-1, 1);
        if (Level == 2 && Selected == null)
            Level = 0;
        MenuView.PageTurned();
    }

    /// <summary>Indexes every placed row once, with the words it can be found by: its label, page, tab and group.</summary>
    private static void BuildIndex()
    {
        _index.Clear();
        var seen = new HashSet<Feature>();
        foreach ((MenuPage page, string path) in Leaves())
        {
            string group = "";
            foreach (MenuRow row in page.Rows)
            {
                if (row.Header != null)
                    group = row.Header;
                else if (row.Item is { } f && seen.Add(f))
                {
                    string label = f.Label.ToUpperInvariant();
                    _index.Add(new SearchEntry(f, path, label, label + " " + path + " " + group, f.Help.ToUpperInvariant()));
                }
            }
        }
    }

    /// <summary>
    /// Every word typed must appear somewhere. Labels that start with the search come first, then labels with a
    /// word starting with it, then any label match, then page or group matches, and last, matches in the help.
    /// </summary>
    private static void RunSearch()
    {
        _searchPage.Rows.Clear();
        string q = Query!.Trim();
        if (q.Length == 0)
        {
            _searchPage.Rows.Add(new MenuRow { Header = "TYPE TO SEARCH EVERY PAGE" });
            RowIndex = 0;
            MenuView.ResultsChanged();
            return;
        }

        string[] terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<(int Rank, int Order, SearchEntry Entry)>();
        for (int i = 0; i < _index.Count; i++)
        {
            SearchEntry e = _index[i];
            int rank = Rank(e, q, terms);
            if (rank >= 0)
                hits.Add((rank, i, e));
        }

        hits.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        foreach ((_, _, SearchEntry e) in hits.Take(MaxResults))
            _searchPage.Rows.Add(new MenuRow { Item = e.Item, Path = e.Path });

        if (_searchPage.Rows.Count == 0)
            _searchPage.Rows.Add(new MenuRow { Header = "NOTHING MATCHES " + q });

        RowIndex = NextRow(-1, 1);
        MenuView.ResultsChanged();
    }

    private static int Rank(SearchEntry e, string q, string[] terms)
    {
        bool inWords = Array.TrueForAll(terms, t => e.Words.Contains(t, StringComparison.Ordinal));
        if (!inWords)
            return Array.TrueForAll(terms, t => e.Words.Contains(t, StringComparison.Ordinal) || e.Help.Contains(t, StringComparison.Ordinal)) ? 4 : -1;

        if (e.Label.StartsWith(q, StringComparison.Ordinal))
            return 0;
        if (e.Label.Contains(" " + terms[0], StringComparison.Ordinal))
            return 1;
        return e.Label.Contains(terms[0], StringComparison.Ordinal) ? 2 : 3;
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
