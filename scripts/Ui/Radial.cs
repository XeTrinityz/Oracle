namespace CeZeY.Oracle;

/// <summary>
/// The favourites radial: a wheel of features held open with one key or button. Point at a slot with the mouse,
/// a stick, WASD or the arrows, then let go to use it. Number keys use a slot at once, and Q / E (LB / RB) turn
/// to the next page of favourites.
///
/// Opened from the menu instead (HOME on a row, or ARRANGE RADIAL), the same wheel is the editor: place a feature
/// in any slot on any page, pick one up and move it, or take it off.
///
/// A used slot does exactly what its hotkey would: a switch flips, a slider jumps to its preset and back, a
/// choice moves on, a command runs. Rows that change the save can't go on the radial, as they can't be hotkeys.
/// </summary>
internal static class Radial
{
    public enum Source
    {
        Keyboard,
        Pad,
    }

    /// <summary>How far from the centre (0 to 1 of the ring) the pointer must be before it picks a slot.</summary>
    private const float Deadzone = 0.3f;

    /// <summary>1080p pixels of mouse travel from the centre to the rim at MOUSE SPEED 1.</summary>
    private const float MouseReach = 160f;

    /// <summary>A stick must lean this far (0 to 1) to point.</summary>
    private const float StickLean = 0.5f;

    /// <summary>How long a slot placed from a menu row stays on screen before the editor hands back to the menu.</summary>
    private const float PlacedHold = 0.5f;

    // ---- settings (SYSTEM > RADIAL) ---------------------------------------------------------------------

    public static bool Enabled = true;

    /// <summary>RADIAL MODE: HOLD uses the slot under the pointer when the key is let go; TAP keeps the wheel open until the key is pressed again.</summary>
    public static bool HoldMode = true;

    public static float MouseSpeed = 1f;

    /// <summary>RADIAL SIZE, on top of the screen's own scale.</summary>
    public static float Size = 1f;

    /// <summary>RADIAL BUTTON on a controller, as a button name for <see cref="Pad"/>: "rs", "ls", "lb+rb" or "off".</summary>
    public static string PadButton = "rs";

    public static readonly string[] PadIds = { "rs", "ls", "lb+rb", "off" };
    public static readonly string[] PadNames = { "R3 (RIGHT STICK)", "L3 (LEFT STICK)", "LB + RB", "OFF" };

    /// <summary>RADIAL KEY options; the first is the default. MOUSE 4 leads because the game uses the middle button.</summary>
    public static readonly string[] KeyIds = { "xbutton1", "xbutton2", "mbutton", "z", "m", "capital" };
    public static readonly string[] KeyNames = { "MOUSE 4", "MOUSE 5", "MIDDLE MOUSE", "Z", "M", "CAPS LOCK" };
    private static readonly Keys[] KeyCodes = { Keys.XButton1, Keys.XButton2, Keys.MButton, Keys.Z, Keys.M, Keys.Capital };

    public static Keys Key { get; private set; } = Keys.XButton1;

    /// <summary>The radial key's name for on-screen hints.</summary>
    public static string KeyLabel { get; private set; } = "MOUSE 4";

    public static void SetKey(int index)
    {
        index = Math.Clamp(index, 0, KeyCodes.Length - 1);
        Key = KeyCodes[index];
        KeyLabel = KeyNames[index];
    }

    /// <summary>Keys the open radial listens to, for the fallback that reads the keyboard itself.</summary>
    public static readonly Keys[] WatchKeys = BuildWatch();

    // ---- state ------------------------------------------------------------------------------------------

    /// <summary>Open over the game, held or tapped open.</summary>
    public static bool IsLive { get; private set; }

    /// <summary>Open over the menu as the editor.</summary>
    public static bool Arranging { get; private set; }

    public static bool Visible => IsLive || Arranging;

    public static Source OpenedBy { get; private set; }

    public static int Page { get; private set; }

    /// <summary>The slot on this page under the pointer, or -1.</summary>
    public static int Hover { get; private set; } = -1;

    /// <summary>The pointer, from the centre: 1 is the ring's edge. Screen directions, so +Y is down.</summary>
    public static float PointX { get; private set; }

    public static float PointY { get; private set; }

    /// <summary>Which way the last page turn went, for the wheel's spin.</summary>
    public static int PageDir { get; private set; }

    /// <summary>In the editor: the feature being placed or moved.</summary>
    public static string? Hand { get; private set; }

    /// <summary>In the editor: the slot the feature in hand was lifted from, or -1 when it came from a menu row.</summary>
    public static int HandFrom { get; private set; } = -1;

    /// <summary>The last slot used or placed, by its index across all pages, when, and whether it worked.</summary>
    public static int Flashed { get; private set; } = -1;

    public static double FlashedAt { get; private set; } = -10.0;

    public static bool FlashOk { get; private set; }

    public static int Slots => Favourites.PerPage;

    /// <summary>Pages on the wheel. The editor shows one blank page past the last used one, to start a new page on.</summary>
    public static int Pages => Arranging && Favourites.Count > 0 ? Math.Min(Favourites.MaxPages, Favourites.UsedPages + 1) : Favourites.UsedPages;

    public static Feature? HoverFeature => Hover < 0 ? null : Favourites.At(Page, Hover);

    public static double OpenedAt { get; private set; }

    private static bool _keyWas;
    private static bool _leftWas;
    private static bool _rightWas;
    private static bool _mouseArmed;
    private static int _centreX;
    private static int _centreY;
    private static int _usedSlot = -1;
    private static bool _fromRow;
    private static double _closeAt = -1.0;

    // ESC closes the radial, so the game's ESC stays muted until the key is back up: the game may handle the same
    // press after Oracle does, and would open the pause menu.
    private static bool _releaseLater;
    private static bool _reholdLater;

    // ---- geometry ---------------------------------------------------------------------------------------

    /// <summary>The centre angle of a slot in screen degrees: slot 0 points straight up and the rest follow clockwise.</summary>
    public static float SlotAngle(int slot, int slots) => -90f + slot * 360f / slots;

    private static int SlotAt(float x, float y, int slots)
    {
        float degrees = MathF.Atan2(y, x) * 180f / MathF.PI + 90f;
        float step = 360f / slots;
        int slot = (int)MathF.Floor((degrees + step * 0.5f) / step);
        return ((slot % slots) + slots) % slots;
    }

    // ---- live wheel -------------------------------------------------------------------------------------

    /// <summary>Whether the wheel may open over the game right now.</summary>
    public static bool CanOpen() =>
        Enabled && !IsLive && !Arranging && !Menu.IsOpen && World.InGame && !World.Paused && !Transition.Busy && Fader.IsClear && Shared.MenuOwner == null;

    /// <summary>Runs every rendered frame: watches the radial key, the pointer and the mouse buttons.</summary>
    public static void Frame()
    {
        HandBackEscape();

        bool front = Native.GameInFront();
        bool key = front && Enabled && Native.Down(Key);
        bool pressed = key && !_keyWas;
        _keyWas = key;

        if (Arranging)
        {
            if (!Menu.IsOpen || (_closeAt >= 0.0 && Gfx.Time >= _closeAt))
                EndArrange();
            else if (Page >= Pages)
                Page = Pages - 1;
            return;
        }

        if (!IsLive)
        {
            if (pressed && CanOpen())
                Open(Source.Keyboard);
            return;
        }

        if (World.Paused || !World.InGame || Menu.IsOpen || Transition.Busy || Shared.MenuOwner != Menu.OwnerName)
        {
            Close();
            return;
        }

        if (Page >= Pages)
            Page = Pages - 1;

        // Switching away from the game isn't letting go of the key: close without using anything.
        if (!front)
        {
            if (OpenedBy == Source.Keyboard)
                Close();
            return;
        }

        if (OpenedBy == Source.Keyboard)
        {
            if (HoldMode && !key)
            {
                Release();
                return;
            }

            if (!HoldMode && pressed)
            {
                Close();
                return;
            }
        }

        // The mouse is read even while WASD points, so its movement meanwhile is thrown away rather than saved up.
        bool keys = PollDirectionKeys();
        PollMouse(apply: !keys);

        bool left = Native.Down(Keys.LButton);
        if (left && !_leftWas)
            Use();
        _leftWas = left;

        bool right = Native.Down(Keys.RButton);
        if (right && !_rightWas)
            Close();
        _rightWas = right;
    }

    public static void Open(Source source)
    {
        if (!CanOpen())
            return;

        IsLive = true;
        _releaseLater = false;
        OpenedBy = source;
        OpenedAt = Gfx.Time;
        Hover = -1;
        PointX = 0f;
        PointY = 0f;
        PageDir = 0;
        _usedSlot = -1;
        if (Page >= Pages)
            Page = 0;

        _leftWas = Native.Down(Keys.LButton);
        _rightWas = Native.Down(Keys.RButton);
        _mouseArmed = false;
        ArmMouse();

        Shared.MenuOwner = Menu.OwnerName;
        Menu.HoldInput(pointer: true, escape: true);
        RadialView.Opened();
    }

    /// <summary>Closes the live wheel without using anything.</summary>
    public static void Close()
    {
        if (!IsLive)
            return;

        IsLive = false;
        if (!Menu.IsOpen && Shared.MenuOwner == Menu.OwnerName)
            Shared.MenuOwner = null;

        if (Native.Down(Keys.Escape))
            _releaseLater = true;
        else
            Menu.ReleaseMovement();
        Pad.MenuClosed();
    }

    /// <summary>Gives the game its keys back once ESC is up, after the radial or editor closed on an ESC press.</summary>
    private static void HandBackEscape()
    {
        if ((!_releaseLater && !_reholdLater) || Native.Down(Keys.Escape))
            return;

        if (_releaseLater && !IsLive && !Menu.IsOpen)
            Menu.ReleaseMovement();
        if (_reholdLater && Menu.IsOpen && !Arranging)
            Menu.HoldInput();
        _releaseLater = false;
        _reholdLater = false;
    }

    /// <summary>The radial key or button was let go: use the slot under the pointer, unless it was just clicked, and close.</summary>
    public static void Release()
    {
        Feature? f = HoverFeature;
        bool use = f != null && Hover != _usedSlot;
        int flat = Page * Slots + Hover;
        Close();
        if (use)
            Run(f!, flat);
    }

    /// <summary>Uses the slot under the pointer and keeps the wheel open, for switching several things in a row.</summary>
    public static void Use()
    {
        if (!IsLive || HoverFeature is not { } f)
            return;

        _usedSlot = Hover;
        Run(f, Page * Slots + Hover);
    }

    public static void TurnPage(int dir)
    {
        int pages = Pages;
        if (pages <= 1)
            return;

        Page = (Page + dir + pages) % pages;
        PageDir = dir;
        _usedSlot = -1;
        RadialView.PageTurned();
    }

    /// <summary>A key while the live wheel is open. Everything is swallowed, so nothing on the keyboard runs a hotkey meanwhile.</summary>
    public static void Press(Keys key)
    {
        if (!IsLive)
            return;

        int digit = Digit(key);
        if (digit >= 0)
        {
            if (digit < Slots)
            {
                Aim(digit);
                Use();
            }

            return;
        }

        switch (key)
        {
            case Keys.Q:
            case Keys.PageUp:
                TurnPage(-1);
                break;
            case Keys.E:
            case Keys.Tab:
            case Keys.PageDown:
                TurnPage(1);
                break;
            case Keys.Enter:
            case Keys.Space:
                Use();
                break;
            case Keys.Escape:
            case Keys.Back:
                Close();
                break;
        }
    }

    /// <summary>A controller stick, in screen directions (+Y down). Leaning far enough points; letting go keeps the slot.</summary>
    public static void PointStick(float x, float y)
    {
        float lean = MathF.Sqrt(x * x + y * y);
        if (lean < StickLean)
            return;

        Point(x / lean, y / lean, mouse: false);
    }

    /// <summary>A switch flips, a slider jumps, a choice moves on, a command runs: what the feature's hotkey does.</summary>
    private static void Run(Feature f, int flat)
    {
        if (!f.Bindable)
            return;
        if (Catalog.Refused(f))
        {
            Flashed = flat;
            FlashedAt = Gfx.Time;
            FlashOk = false;
            return;
        }

        Flashed = flat;
        FlashedAt = Gfx.Time;
        if (f is Command c)
        {
            FlashOk = Catalog.Run(c);
            Overlay.Toast(f.Label, FlashOk ? "DONE" : "FAILED", FlashOk);
            return;
        }

        f.Hotkey();
        Catalog.Commit(f, "radial");
        FlashOk = true;
        Overlay.Toast(f.Label, f.State, !f.IsDefault);
    }

    private static void Aim(int slot)
    {
        float a = SlotAngle(slot, Slots) * MathF.PI / 180f;
        Point(MathF.Cos(a), MathF.Sin(a), mouse: false);
        Hover = slot;
    }

    private static void Point(float x, float y, bool mouse)
    {
        float len = MathF.Sqrt(x * x + y * y);
        if (len > 1f)
        {
            x /= len;
            y /= len;
            len = 1f;
        }

        PointX = x;
        PointY = y;

        // Back in the middle with the mouse means "none of them", so letting go there uses nothing.
        int slot = len < Deadzone ? (mouse ? -1 : Hover) : SlotAt(x, y, Slots);
        if (slot != Hover)
        {
            Hover = slot;
            _usedSlot = -1;
        }
    }

    /// <summary>WASD and the arrows point in eight directions. Returns true while any of them is held.</summary>
    private static bool PollDirectionKeys()
    {
        int dx = (Native.Down(Keys.D) || Native.Down(Keys.Right) ? 1 : 0) - (Native.Down(Keys.A) || Native.Down(Keys.Left) ? 1 : 0);
        int dy = (Native.Down(Keys.S) || Native.Down(Keys.Down) ? 1 : 0) - (Native.Down(Keys.W) || Native.Down(Keys.Up) ? 1 : 0);
        if (dx == 0 && dy == 0)
            return false;

        float len = MathF.Sqrt(dx * dx + dy * dy);
        Point(dx / len, dy / len, mouse: false);
        return true;
    }

    /// <summary>
    /// The mouse moves a pointer inside the wheel. The game hides the cursor, so it's read and put back in the
    /// middle of the window each frame, and the game's own mouse look is muted while the wheel is open.
    /// </summary>
    private static void PollMouse(bool apply)
    {
        if (!_mouseArmed)
        {
            ArmMouse();
            return;
        }

        if (!Native.CursorAt(out int x, out int y))
            return;

        int dx = x - _centreX;
        int dy = y - _centreY;
        if (dx == 0 && dy == 0)
            return;

        Native.MoveCursor(_centreX, _centreY);

        // A one-pixel step is jitter, or a game that centres the cursor a pixel off ours; either way not a move.
        if (!apply || (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1))
            return;

        float reach = MouseReach * Math.Max(0.5f, Gfx.Scale) / Math.Max(0.1f, MouseSpeed);
        Point(PointX + dx / reach, PointY + dy / reach, mouse: true);
    }

    private static void ArmMouse()
    {
        if (!Native.GameCentre(out _centreX, out _centreY))
            return;

        Native.MoveCursor(_centreX, _centreY);
        _mouseArmed = true;
    }

    // ---- editor -----------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the editor over the menu. With a feature, it's in hand: lifted from its slot if it's already on the
    /// radial, or ready to place in the first free slot if not. Without one, slots can be picked up and moved.
    /// </summary>
    public static void BeginArrange(Feature? f)
    {
        if (IsLive)
            Close();
        if (!Menu.IsOpen)
            Menu.Open();
        if (!Menu.IsOpen)
            return;

        Arranging = true;
        _reholdLater = false;
        Menu.HoldInput(escape: true);   // ESC finishes arranging; it mustn't also pause the game
        _fromRow = f != null;
        _closeAt = -1.0;
        Hand = f?.Id;
        HandFrom = f == null ? -1 : Favourites.IndexOf(f.Id);
        PageDir = 0;

        int at = HandFrom >= 0 ? HandFrom : f != null ? Favourites.FirstFree() : -1;
        if (at < 0)
            at = Math.Clamp(Page, 0, Pages - 1) * Slots;

        Page = at / Slots;
        Aim(at % Slots);
        RadialView.Opened();
    }

    public static void EndArrange()
    {
        if (!Arranging)
            return;

        Arranging = false;
        Hand = null;
        HandFrom = -1;
        _closeAt = -1.0;

        // Back to the menu's usual keys, ESC live again so the pause menu still opens from the menu.
        if (Menu.IsOpen)
        {
            if (Native.Down(Keys.Escape))
                _reholdLater = true;
            else
                Menu.HoldInput();
        }
    }

    /// <summary>A menu key while the editor is open. The editor takes every key until it closes.</summary>
    public static bool EditorPress(Keys key, bool repeat)
    {
        if (_closeAt >= 0.0)
        {
            if (key is Keys.Escape or Keys.Back)
                EndArrange();
            return true;
        }

        int digit = Digit(key);
        if (digit >= 0)
        {
            if (!repeat && digit < Slots)
                Aim(digit);
            return true;
        }

        switch (key)
        {
            case Keys.Left:
            case Keys.Up:
                Aim((Math.Max(Hover, 0) - 1 + Slots) % Slots);
                break;
            case Keys.Right:
            case Keys.Down:
                Aim((Hover + 1) % Slots);
                break;
            case Keys.PageUp:
            case Keys.Q:
                if (!repeat)
                    TurnPage(-1);
                break;
            case Keys.PageDown:
            case Keys.Tab:
            case Keys.E:
                if (!repeat)
                    TurnPage(1);
                break;
            case Keys.Enter:
            case Keys.Space:
                if (!repeat)
                    Place();
                break;
            case Keys.Delete:
            case Keys.X:
                if (!repeat)
                    Remove();
                break;
            case Keys.Escape:
            case Keys.Back:
            case Menu.FavouriteKey:
                if (!repeat)
                    EndArrange();
                break;
        }

        return true;
    }

    /// <summary>
    /// ENTER in the editor: with something in hand, puts it in the slot (an occupant swaps places with a moved
    /// favourite, or is replaced by a new one); with nothing in hand, picks up the slot's favourite.
    /// </summary>
    private static void Place()
    {
        if (Hover < 0)
            return;

        int target = Page * Slots + Hover;
        if (Hand == null)
        {
            if (Favourites.At(target) is { } f)
            {
                Hand = f.Id;
                HandFrom = target;
            }

            return;
        }

        if (HandFrom >= 0)
            Favourites.Swap(HandFrom, target);
        else
            Favourites.Put(target, Hand);

        Hand = null;
        HandFrom = -1;
        Flash(target, true);
        if (_fromRow)
            _closeAt = Gfx.Time + PlacedHold;
    }

    /// <summary>DELETE in the editor: takes the favourite in hand (or under the pointer) off the radial.</summary>
    private static void Remove()
    {
        if (Hand != null)
        {
            if (HandFrom >= 0)
            {
                Favourites.Empty(HandFrom);
                Flash(HandFrom, false);
            }

            Hand = null;
            HandFrom = -1;
            if (_fromRow)
                _closeAt = Gfx.Time + PlacedHold;
            return;
        }

        int target = Page * Slots + Hover;
        if (Hover >= 0 && Favourites.At(target) != null)
        {
            Favourites.Empty(target);
            Flash(target, false);
        }
    }

    private static void Flash(int flat, bool ok)
    {
        Flashed = flat;
        FlashedAt = Gfx.Time;
        FlashOk = ok;
    }

    public static void Shutdown()
    {
        Close();
        EndArrange();
    }

    // ---- keys -------------------------------------------------------------------------------------------

    /// <summary>1 to 9 then 0 for slots 1 to 10, from either row of number keys. -1 for any other key.</summary>
    private static int Digit(Keys key)
    {
        int n = key >= Keys.D0 && key <= Keys.D9 ? key - Keys.D0 : key >= Keys.NumPad0 && key <= Keys.NumPad9 ? key - Keys.NumPad0 : -1;
        return n < 0 ? -1 : n == 0 ? 9 : n - 1;
    }

    private static Keys[] BuildWatch()
    {
        var keys = new List<Keys> { Keys.Q, Keys.E, Keys.Tab, Keys.Enter, Keys.Space, Keys.Escape, Keys.Back, Keys.PageUp, Keys.PageDown };
        for (Keys k = Keys.D0; k <= Keys.D9; k++)
            keys.Add(k);
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++)
            keys.Add(k);
        return keys.ToArray();
    }
}
