using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.Oracle;

/// <summary>XInput controller support. The game keeps reading the same pad, so its bindings are parked while the menu is open.</summary>
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

    private static bool _legacy;
    private static bool _missing;
    private static int _index = -1;
    private static double _nextScan;
    private static uint _packet;
    private static ushort _now;
    private static ushort _prev;
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
            Log.Info("Pad: controller " + i + " connected.");
            return;
        }
    }

    private static void Take(XInputState state)
    {
        XInputGamepad g = state.Gamepad;
        ushort b = g.Buttons;
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
            _now = 0;
            _prev = 0;
            return;
        }

        ReadOpenButton();
        Poll();
        if (!Connected)
        {
            _held = -1f;
            _mapDeferred = false;
            if (_blockedInput != 0 || _restorePending)
                Unblock("controller disconnected");
            return;
        }

        if (!Menu.IsOpen)
        {
            if (_restorePending && (_now & ~Start) == 0)
                Unblock("menu closed");
            RepairBindings();
            ClosedFrame(dt);
            return;
        }

        Block();
        MenuFrame(dt);
    }

    private static void ClosedFrame(float dt)
    {
        Array.Fill(_repeat, -1f);

        if (Pressed(LB) && (_openMask & LB) == 0 && Characters.I.PlayingOther && !Characters.I.Busy)
            _enterCar = true;

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

        if (Pressed(A))
            Menu.Press(Keys.Enter, false);
        else if (Pressed(B))
            Menu.Press(Menu.Binding != null ? Keys.Escape : Keys.Back, false);
        else if (Pressed(LB))
            Menu.Press(Keys.PageUp, false);
        else if (Pressed(RB))
            Menu.Press(Keys.PageDown, false);
        else if (Pressed(X))
            Menu.Press(Menu.Confirming != null ? Keys.Escape : Keys.Delete, false);
        else if (Pressed(Y) && Menu.Confirming != null)
            Menu.Press(Keys.Escape, false);

        if (!Menu.IsOpen)
            return;

        for (int i = 0; i < Dirs.Length; i++)
        {
            if (!Held(Dirs[i]) || ((_openMask & Dirs[i]) != 0 && _waitRelease))
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

    public static void Shutdown() => Unblock("mod unloaded");

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
            if ((b.Control && b.Shift && b.Alt) == park || !IsPadBind(b.Name.ToString() ?? ""))
                continue;

            b.Control = park;
            b.Shift = park;
            b.Alt = park;
            changed++;
        }

        return changed;
    }
}
