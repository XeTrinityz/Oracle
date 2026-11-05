using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.Oracle;

/// <summary>
/// XInput controller support. The game keeps reading the same pad, so its bindings are parked while the menu or
/// radial is open. A single-button RADIAL BUTTON (R3 or L3) is Oracle's for good: its game binding stays parked.
/// </summary>
internal static class Pad
{
    private const ushort Up = 0x0001;
    private const ushort Down = 0x0002;
    private const ushort Left = 0x0004;
    private const ushort Right = 0x0008;
    private const ushort Start = 0x0010;
    private const ushort Back = 0x0020;
    private const ushort LS = 0x0040;
    private const ushort RS = 0x0080;
    private const ushort LB = 0x0100;
    private const ushort RB = 0x0200;
    private const ushort A = 0x1000;
    private const ushort B = 0x2000;
    private const ushort X = 0x4000;
    private const ushort Y = 0x8000;

    private const short StickPush = 16000;
    private const short StickNoise = 9000;
    private const byte TriggerNoise = 40;
    private const double ScanSeconds = 2.0;
    private const int RescanFrames = 30;

    private static readonly Dictionary<string, ushort> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = A, ["b"] = B, ["x"] = X, ["y"] = Y,
        ["lb"] = LB, ["rb"] = RB, ["ls"] = LS, ["rs"] = RS, ["l3"] = LS, ["r3"] = RS,
        ["back"] = Back, ["view"] = Back, ["select"] = Back, ["start"] = Start, ["menu"] = Start,
        ["up"] = Up, ["down"] = Down, ["left"] = Left, ["right"] = Right,
    };

    private static readonly ushort[] Dirs = { Up, Down, Left, Right };
    private static readonly Keys[] DirKeys = { Keys.Up, Keys.Down, Keys.Left, Keys.Right };

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int userIndex, out XInputState state);

    public static bool Enabled = true;
    public static float OpenHold = 0.5f;

    private static string _openText = "";
    private static ushort _openMask = Back;
    private static string _radialText = "";
    private static ushort _radialMask;
    private static string? _ownedBind;
    private static string? _parkedOwned;
    private static nint _ownedInput;
    private static int _ownedFrames;

    private static bool _legacy;
    private static bool _missing;
    private static int _index = -1;
    private static double _nextScan;
    private static uint _packet;
    private static ushort _now;
    private static ushort _prev;

    /// <summary>The buttons alone, without the left stick standing in for the D-pad.</summary>
    private static ushort _raw;
    private static ushort _rawPrev;
    private static short _lx;
    private static short _ly;
    private static short _rx;
    private static short _ry;
    private static FrameTimer _frame;
    private static long _padUsedAt;
    private static long _keyUsedAt;

    private static float _held = -1f;
    private static bool _waitRelease;
    private static bool _mapDeferred;
    private static bool _replayMap;
    private static bool _replaying;
    private static bool _enterCar;
    private static readonly float[] _repeat = { -1f, -1f, -1f, -1f };

    private static nint _blockedInput;
    private static nint _repairedInput;
    private static int _blockFrames;
    private static bool _restorePending;

    private static bool Connected => _index >= 0;

    /// <summary>True when the controller was touched more recently than the keyboard, for on-screen button hints.</summary>
    public static bool InUse => Connected && _padUsedAt > _keyUsedAt;

    public static void KeyboardUsed() => _keyUsedAt = Stopwatch.GetTimestamp();

    private static bool Held(ushort mask) => mask != 0 && (_now & mask) == mask;

    private static bool Pressed(ushort mask) => mask != 0 && (_now & mask) == mask && (_prev & mask) != mask;

    private static bool RawHeld(ushort mask) => mask != 0 && (_raw & mask) == mask;

    private static bool RawPressed(ushort mask) => mask != 0 && (_raw & mask) == mask && (_rawPrev & mask) != mask;

    /// <summary>
    /// RADIAL BUTTON: "rs", "ls", "lb+rb" or "off". A single stick click becomes Oracle's, so its game binding is
    /// kept parked (see <see cref="SyncOwned"/>). It can't share the menu's open button.
    /// </summary>
    private static void ReadRadialButton()
    {
        string text = Radial.Enabled ? Radial.PadButton : "off";
        if (_radialText == text)
            return;

        _radialText = text;
        ushort mask = 0;
        if (!text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                mask |= Names.TryGetValue(part, out ushort bit) ? bit : (ushort)0;
        }

        if (mask != 0 && (mask & _openMask) != 0)
        {
            Log.Info("[gamepad] the radial button shares a button with the menu's open button, so the radial is keyboard only.");
            mask = 0;
        }

        _radialMask = mask;
        _ownedBind = mask == RS ? "XboxTypeS_RightThumbstick" : mask == LS ? "XboxTypeS_LeftThumbstick" : null;
        _ownedFrames = RescanFrames;
    }

    private static void ReadOpenButton()
    {
        if (_openText == Settings.PadOpenButton)
            return;

        _openText = Settings.PadOpenButton;
        ushort mask = 0;
        foreach (string part in _openText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Names.TryGetValue(part, out ushort bit))
            {
                mask = 0;
                break;
            }

            mask |= bit;
        }

        if (mask == 0)
            Log.Info("[gamepad] open_button \"" + _openText + "\" is not a button name, using back.");
        _openMask = mask == 0 ? Back : mask;
    }

    private static bool Read(int index, out XInputState state)
    {
        state = default;
        if (_missing)
            return false;

        try
        {
            return (_legacy ? XInputGetState910(index, out state) : XInputGetState14(index, out state)) == 0;
        }
        catch (DllNotFoundException) when (!_legacy)
        {
            _legacy = true;
            Log.Info("Pad: xinput1_4.dll not found, using xinput9_1_0.dll.");
            return Read(index, out state);
        }
        catch (Exception ex)
        {
            _missing = true;
            Log.Info("Pad: XInput is not available, controller support is off: " + ex.Message);
            return false;
        }
    }

    private static void Poll()
    {
        _prev = _now;
        _rawPrev = _raw;
        if (_index >= 0)
        {
            if (Read(_index, out XInputState state))
            {
                Take(state);
                return;
            }

            Log.Info("Pad: controller " + _index + " disconnected.");
            _index = -1;
            _now = 0;
            _raw = 0;
            _nextScan = 0.0;
        }

        // Probing an empty XInput slot can stall for milliseconds, so scan rarely.
        double time = Clock.Now;
        if (time < _nextScan)
            return;

        _nextScan = time + ScanSeconds;
        for (int i = 0; i < 4; i++)
        {
            if (!Read(i, out XInputState state))
                continue;

            _index = i;
            _packet = state.PacketNumber;
            _now = state.Gamepad.Buttons;
            _prev = _now;
            _raw = _now;
            _rawPrev = _raw;
            Log.Info("Pad: controller " + i + " connected.");
            return;
        }
    }

    private static void Take(XInputState state)
    {
        XInputGamepad g = state.Gamepad;
        ushort b = g.Buttons;
        _raw = b;
        _lx = g.ThumbLX;
        _ly = g.ThumbLY;
        _rx = g.ThumbRX;
        _ry = g.ThumbRY;
        if (g.ThumbLY > StickPush)
            b |= Up;
        else if (g.ThumbLY < -StickPush)
            b |= Down;
        if (g.ThumbLX < -StickPush)
            b |= Left;
        else if (g.ThumbLX > StickPush)
            b |= Right;
        _now = b;

        if (state.PacketNumber == _packet)
            return;

        _packet = state.PacketNumber;
        if (g.Buttons != 0 || g.LeftTrigger > TriggerNoise || g.RightTrigger > TriggerNoise ||
            Math.Abs((int)g.ThumbLX) > StickNoise || Math.Abs((int)g.ThumbLY) > StickNoise ||
            Math.Abs((int)g.ThumbRX) > StickNoise || Math.Abs((int)g.ThumbRY) > StickNoise)
            _padUsedAt = Stopwatch.GetTimestamp();
    }

    public static void Frame()
    {
        float dt = _frame.Step(0.1f);

        if (!Enabled)
        {
            if (_blockedInput != 0 || _restorePending)
                Unblock("controller support off");
            ReleaseOwned();
            _now = 0;
            _prev = 0;
            _raw = 0;
            _rawPrev = 0;
            return;
        }

        ReadOpenButton();
        ReadRadialButton();
        Poll();
        if (!Connected)
        {
            _held = -1f;
            _mapDeferred = false;
            if (_blockedInput != 0 || _restorePending)
                Unblock("controller disconnected");
            if (Radial.IsLive && Radial.OpenedBy == Radial.Source.Pad)
                Radial.Close();
            return;
        }

        if (Radial.IsLive)
        {
            Block();
            RadialFrame();
            return;
        }

        if (!Menu.IsOpen)
        {
            if (_restorePending && (_now & ~Start) == 0)
                Unblock("menu closed");
            RepairBindings();
            SyncOwned();
            ClosedFrame(dt);
            return;
        }

        Block();
        MenuFrame(dt);
    }

    private static void ClosedFrame(float dt)
    {
        Array.Fill(_repeat, -1f);

        if (Pressed(LB) && (_openMask & LB) == 0 && (_radialMask & LB) == 0 && Characters.I.PlayingOther && !Characters.I.Busy)
            _enterCar = true;

        if (RawPressed(_radialMask) && !_waitRelease && Radial.CanOpen())
        {
            _padUsedAt = Stopwatch.GetTimestamp();
            Radial.Open(Radial.Source.Pad);
            return;
        }

        if (_waitRelease)
        {
            if (!Held(_openMask))
                _waitRelease = false;
            _held = -1f;
            return;
        }

        if (!Held(_openMask))
        {
            if (_mapDeferred)
            {
                _mapDeferred = false;
                _replayMap = true;
            }

            _held = -1f;
            return;
        }

        if (_held < 0f)
        {
            if (!Pressed(_openMask) && !_mapDeferred)
                return;
            _held = 0f;
        }

        _held += dt;
        if (_held < OpenHold)
            return;

        _held = -1f;
        _waitRelease = true;
        bool deferred = _mapDeferred;
        _mapDeferred = false;

        if (World.Paused || !World.InGame || Transition.Busy)
        {
            if (deferred)
                _replayMap = true;
            return;
        }

        _padUsedAt = Stopwatch.GetTimestamp();
        Menu.Open();
    }

    private static void MenuFrame(float dt)
    {
        _mapDeferred = false;

        if (_waitRelease)
        {
            if (!Held(_openMask))
                _waitRelease = false;
        }
        else if (Pressed(_openMask))
        {
            _waitRelease = true;
            Menu.Close();
            return;
        }

        bool arranging = Radial.Arranging;
        if (Pressed(A))
            Menu.Press(Keys.Enter, false);
        else if (Pressed(B))
            Menu.Press(Menu.Binding != null || arranging ? Keys.Escape : Keys.Back, false);
        else if (Pressed(LB))
            Menu.Press(Keys.PageUp, false);
        else if (Pressed(RB))
            Menu.Press(Keys.PageDown, false);
        else if (Pressed(X))
            Menu.Press(Menu.Confirming != null ? Keys.Escape : Keys.Delete, false);
        else if (Pressed(Y))
            Menu.Press(Menu.Confirming != null ? Keys.Escape : Menu.FavouriteKey, false);

        if (!Menu.IsOpen)
            return;

        // In the radial editor a stick points straight at a slot, so only the D-pad steps from slot to slot.
        ushort dirs = _now;
        if (Radial.Arranging)
        {
            PointWithSticks();
            dirs = _raw;
        }

        for (int i = 0; i < Dirs.Length; i++)
        {
            if ((dirs & Dirs[i]) == 0 || ((_openMask & Dirs[i]) != 0 && _waitRelease))
            {
                _repeat[i] = -1f;
                continue;
            }

            if (_repeat[i] < 0f)
            {
                _repeat[i] = 0f;
                Menu.Press(DirKeys[i], false);
                continue;
            }

            _repeat[i] += dt;
            if (_repeat[i] >= Menu.RepeatDelay)
            {
                _repeat[i] -= Menu.RepeatRate;
                Menu.Press(DirKeys[i], true);
            }

            if (!Menu.IsOpen)
                return;
        }
    }

    /// <summary>
    /// The live radial. Held open: letting go of the radial button uses the slot under the pointer. Tapped open
    /// (RADIAL MODE TAP): the button closes it again. Either stick points, A uses a slot and stays open, B closes,
    /// LB / RB or the D-pad's left and right turn the page.
    /// </summary>
    private static void RadialFrame()
    {
        if (Radial.OpenedBy == Radial.Source.Pad)
        {
            if (Radial.HoldMode && !RawHeld(_radialMask))
            {
                Radial.Release();
                return;
            }

            if (!Radial.HoldMode && RawPressed(_radialMask) && Gfx.Time - Radial.OpenedAt > 0.05)
            {
                Radial.Close();
                return;
            }
        }

        PointWithSticks();

        if (Pressed(A))
            Radial.Use();
        else if (Pressed(B))
            Radial.Close();
        else if (RawPressed(Left) || ((_radialMask & LB) == 0 && RawPressed(LB)))
            Radial.TurnPage(-1);
        else if (RawPressed(Right) || ((_radialMask & RB) == 0 && RawPressed(RB)))
            Radial.TurnPage(1);
    }

    /// <summary>Whichever stick leans further points the radial. XInput's Y runs up, the screen's runs down.</summary>
    private static void PointWithSticks()
    {
        float lx = _lx / 32767f;
        float ly = _ly / 32767f;
        float rx = _rx / 32767f;
        float ry = _ry / 32767f;
        if (lx * lx + ly * ly >= rx * rx + ry * ry)
            Radial.PointStick(lx, -ly);
        else
            Radial.PointStick(rx, -ry);
    }

    /// <summary>
    /// Keeps the RADIAL BUTTON's own game binding parked, so pressing it opens the radial and nothing else. Checked
    /// every so often, as the game rebuilds its bindings on a new pawn or an options change.
    /// </summary>
    private static void SyncOwned()
    {
        if (++_ownedFrames < RescanFrames && _ownedBind == _parkedOwned)
            return;

        _ownedFrames = 0;
        try
        {
            PlayerInput? input = CurrentInput();
            if (input == null)
                return;

            if (_parkedOwned != null && (_parkedOwned != _ownedBind || input.Ptr != _ownedInput))
            {
                if (input.Ptr == _ownedInput)
                    ParkNamed(input, _parkedOwned, false);
                _parkedOwned = null;
            }

            if (_ownedBind != null)
            {
                int parked = ParkNamed(input, _ownedBind, true);
                if (parked > 0)
                    Log.Detail("Pad: " + _ownedBind + " belongs to the radial now (" + parked + " bindings parked).");
                _parkedOwned = _ownedBind;
                _ownedInput = input.Ptr;
            }
        }
        catch (Exception ex)
        {
            Log.Throttled("pad owned", "Pad: could not reserve the radial button: " + ex.Message);
        }
    }

    /// <summary>Gives the radial button's game binding back, when controller support is switched off or the mod unloads.</summary>
    private static void ReleaseOwned()
    {
        if (_parkedOwned == null)
            return;

        string name = _parkedOwned;
        _parkedOwned = null;
        try
        {
            if (CurrentInput() is { } input && input.Ptr == _ownedInput)
                ParkNamed(input, name, false);
        }
        catch (Exception ex)
        {
            Log.Info("Pad: could not give " + name + " back to the game: " + ex.Message);
        }
    }

    private static int ParkNamed(PlayerInput input, string name, bool park)
    {
        int changed = 0;
        Span<Input.FKeyBind> binds = input.Bindings.AsSpan();
        for (int i = 0; i < binds.Length; i++)
        {
            ref Input.FKeyBind b = ref binds[i];
            if ((b.Control && b.Shift && b.Alt) == park || !string.Equals(b.Name.ToString(), name, StringComparison.OrdinalIgnoreCase))
                continue;

            b.Control = park;
            b.Shift = park;
            b.Alt = park;
            changed++;
        }

        return changed;
    }

    /// <summary>Holds back the map on BACK until we know it's a tap, not the start of opening the menu.</summary>
    private static bool DefersMap()
    {
        if (_replaying || !Enabled || _openMask != Back || _index < 0 || Menu.IsOpen)
            return false;

        if (!Read(_index, out XInputState state) || (state.Gamepad.Buttons & Back) == 0)
            return false;

        _mapDeferred = true;
        return true;
    }

    public static void GameTick()
    {
        if (_enterCar)
        {
            _enterCar = false;
            if (!Menu.IsOpen && !World.Paused)
                Characters.I.EnterNearby();
        }

        if (!_replayMap)
            return;

        _replayMap = false;
        RPlayerController? pc = World.Pc;
        if (pc == null || Menu.IsOpen || World.Paused)
            return;

        _replaying = true;
        try
        {
            pc.OpenLevelUpOrMap();
        }
        finally
        {
            _replaying = false;
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.OpenLevelUpOrMap))]
    private static void OpenLevelUpOrMapRedirect(RPlayerController self)
    {
        if (Menu.IsOpen || Safe.Try("map check", DefersMap))
            return;

        self.OpenLevelUpOrMap();
    }

    private static bool IsPadBind(string name) =>
        (name.StartsWith("XboxTypeS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Gamepad_", StringComparison.OrdinalIgnoreCase)) &&
        !name.Equals("XboxTypeS_Start", StringComparison.OrdinalIgnoreCase);

    private static PlayerInput? CurrentInput()
    {
        PlayerInput? input = World.Pc?.PlayerInput;
        return input is { IsValid: true } ? input : null;
    }

    // A binding that requires Ctrl+Shift+Alt can never match a pad button, which parks it without losing it.
    private static void Block()
    {
        try
        {
            PlayerInput? input = CurrentInput();
            if (input == null)
                return;

            if (input.Ptr == _blockedInput && ++_blockFrames < RescanFrames)
                return;

            _blockFrames = 0;
            _restorePending = false;
            Park(input, true);
            _blockedInput = input.Ptr;
        }
        catch (Exception ex)
        {
            Log.Throttled("pad block", "Pad: could not pause the game's controller buttons: " + ex.Message);
        }
    }

    /// <summary>Waits for held buttons to come up first, so their release doesn't leak into the game.</summary>
    public static void MenuClosed()
    {
        Array.Fill(_repeat, -1f);
        if (_blockedInput == 0)
            return;

        if (Connected && (_now & ~Start) != 0)
            _restorePending = true;
        else
            Unblock("menu closed");
    }

    public static void Shutdown()
    {
        Unblock("mod unloaded");
        ReleaseOwned();
    }

    private static void Unblock(string why)
    {
        _restorePending = false;
        _blockedInput = 0;
        _blockFrames = 0;

        try
        {
            PlayerInput? input = CurrentInput();
            if (input == null)
                return;

            int restored = Park(input, false);
            _repairedInput = input.Ptr;
            Log.Detail("Pad: " + restored + " controller bindings back on (" + why + ").");
        }
        catch (Exception ex)
        {
            Log.Info("Pad: could not give the controller buttons back to the game: " + ex.Message);
        }
    }

    /// <summary>Clears bindings left parked if an earlier session ended with the menu open.</summary>
    private static void RepairBindings()
    {
        if (_blockedInput != 0 || _restorePending)
            return;

        PlayerInput? input = CurrentInput();
        if (input == null || input.Ptr == _repairedInput)
            return;

        _repairedInput = input.Ptr;
        int restored = Park(input, false);
        if (restored > 0)
            Log.Info("Pad: repaired " + restored + " controller bindings left paused by an earlier session.");
    }

    /// <summary>Parks or un-parks every controller binding not already that way. Returns how many changed.</summary>
    private static int Park(PlayerInput input, bool park)
    {
        int changed = 0;
        Span<Input.FKeyBind> binds = input.Bindings.AsSpan();
        for (int i = 0; i < binds.Length; i++)
        {
            ref Input.FKeyBind b = ref binds[i];
            string name = b.Name.ToString() ?? "";
            if ((b.Control && b.Shift && b.Alt) == park || !IsPadBind(name))
                continue;

            // The radial button stays parked while the radial owns it.
            if (!park && _parkedOwned != null && name.Equals(_parkedOwned, StringComparison.OrdinalIgnoreCase))
                continue;

            b.Control = park;
            b.Shift = park;
            b.Alt = park;
            changed++;
        }

        return changed;
    }
}
