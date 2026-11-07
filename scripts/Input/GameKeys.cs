using BmSDK;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Keeps the game from acting on the keys the open menu uses (letters for search, ENTER, the arrows and so on).
/// Everything goes through BmSDK (<see cref="Keyboard"/>): keys and mouse buttons are swallowed before the game's
/// input sees them, and the mouse axes and wheel, which aren't keys, are swallowed by their engine names in the
/// viewport. The game's bindings never fire while Oracle still sees the input. The binding table itself is only
/// touched for the developer bindings <see cref="DebugKeys"/> switches off, and only its 8-byte key names.
/// </summary>
internal static unsafe class GameKeys
{
    /// <summary>
    /// Bytes per binding: BmSDK's FKeyBind size (28; the fork sizes structs by their array stride). Each entry starts
    /// with the FName (8) and the command FString (16). The array is still checked at that size before anything is
    /// written: every entry must read as a binding and enough names must be key names.
    /// </summary>
    private static readonly int BindStride = System.Runtime.InteropServices.Marshal.SizeOf<BmSDK.Engine.Input.FKeyBind>();

    private const int MaxBindings = 4096;
    private const int MaxCommand = 4096;
    private const int MinKeyHits = 8;

    /// <summary>The keys the menu uses, swallowed while it is open. Escape stays live so the pause menu still opens.</summary>
    private static readonly Keys[] MenuKeys = BuildMenuKeys();

    /// <summary>The mouse buttons: swallowed by the radial (clicking a slot mustn't throw a punch) and by editor mode.</summary>
    private static readonly Keys[] MouseButtons = { Keys.LButton, Keys.RButton, Keys.MButton, Keys.XButton1, Keys.XButton2 };

    /// <summary>The modifier keys editor mode uses. Windows reports them without a side, so the plain keys are swallowed.</summary>
    private static readonly Keys[] Modifiers = { Keys.ShiftKey, Keys.ControlKey, Keys.Menu };

    /// <summary>
    /// Inputs swallowed by their engine name because they aren't keys: the mouse axes (the radial, so pointing
    /// doesn't turn the camera) and the wheel (the radial and editor mode).
    /// </summary>
    private static readonly string[] AxisNames = { "MouseX", "MouseY" };
    private static readonly string[] WheelNames = { "MouseScrollUp", "MouseScrollDown" };

    private static readonly List<Keys> _swallowed = new();
    private static readonly List<string> _swallowedNames = new();
    private static readonly List<(int Slot, int Index, int Number, string Command)> _blocked = new();
    private static nint _blockedInput;
    private static int _blockMark = -1;
    private static int _stride;
    private static bool _warned;
    private static bool _reported;

    /// <summary>True while game keys are swallowed. Whoever muted last owns them; the menu closing unmutes all.</summary>
    public static bool IsMuted => _swallowed.Count > 0 || _swallowedNames.Count > 0;

    /// <summary>
    /// Mutes the menu's keys, with <paramref name="pointer"/> the mouse as well, with <paramref name="editor"/> the
    /// mouse buttons, modifiers and wheel but not the mouse axes (editor mode), and with <paramref name="escape"/>
    /// ESCAPE too, for the radial, where ESC closes it rather than pausing the game. Works on the title screen too.
    /// </summary>
    public static void Mute(bool pointer = false, bool escape = false, bool editor = false)
    {
        Unmute();

        _swallowed.AddRange(MenuKeys);
        if (pointer || editor)
            _swallowed.AddRange(MouseButtons);
        if (editor)
            _swallowed.AddRange(Modifiers);
        if (escape)
            _swallowed.Add(Keys.Escape);
        foreach (Keys key in _swallowed)
            Keyboard.Swallow(key);

        if (pointer)
            _swallowedNames.AddRange(AxisNames);
        if (pointer || editor)
            _swallowedNames.AddRange(WheelNames);
        foreach (string name in _swallowedNames)
            Keyboard.Swallow(name);

        if (!_reported)
        {
            _reported = true;
            Log.Info("Menu keys: " + _swallowed.Count + " keys and " + _swallowedNames.Count + " named inputs swallowed while the menu is open.");
        }
    }

    /// <summary>Lets the game see every muted key again.</summary>
    public static void Unmute()
    {
        foreach (Keys key in _swallowed)
            Keyboard.Unswallow(key);
        _swallowed.Clear();

        foreach (string name in _swallowedNames)
            Keyboard.Unswallow(name);
        _swallowedNames.Clear();
    }

    /// <summary>
    /// Renames, until <see cref="UnblockDebug"/>, every binding whose commands all pass <paramref name="isDebug"/>
    /// (see <see cref="DebugKeys"/>). It uses its own mark, so the menu's muting never touches these. Safe to call
    /// again: bindings already switched off are skipped. Returns how many were switched off by this call.
    /// </summary>
    public static int BlockDebug(PlayerController pc, Func<string, bool> isDebug)
    {
        if (pc.PlayerInput is not { } input || !Read(input.Bindings.Ptr, out byte* data, out int count))
            return 0;

        if (_blockMark < 0)
            _blockMark = new FName("OracleDebugKeyOff").Index;
        if (input.Ptr != _blockedInput)
        {
            _blocked.Clear();
            _blockedInput = input.Ptr;
        }

        int added = 0;
        for (int i = 0; i < count; i++)
        {
            byte* e = data + i * _stride;
            int* name = (int*)e;
            if (name[0] == _blockMark || Command(e) is not { } command || !AllDebug(command, isDebug))
                continue;

            int index = name[0], number = name[1];
            _blocked.Add((i, index, number, command));
            name[0] = _blockMark;
            name[1] = 0;
            added++;
        }

        return added;
    }

    /// <summary>Puts back what <see cref="BlockDebug"/> switched off, where a binding still carries its mark and command.</summary>
    public static void UnblockDebug(PlayerController? pc)
    {
        if (_blocked.Count == 0)
            return;

        try
        {
            if (pc?.PlayerInput is not { } input || input.Ptr != _blockedInput || !Read(input.Bindings.Ptr, out byte* data, out int count))
                return;

            foreach ((int slot, int index, int number, string command) in _blocked)
            {
                if (slot >= count)
                    continue;

                byte* e = data + slot * _stride;
                int* name = (int*)e;
                if (name[0] == _blockMark && Command(e) == command)
                {
                    name[0] = index;
                    name[1] = number;
                }
            }
        }
        finally
        {
            _blocked.Clear();
            _blockedInput = 0;
        }
    }

    /// <summary>The FName index of every key whose binding runs the command (say "ToggleBattleMode"), read-only.</summary>
    public static HashSet<int> KeysFor(PlayerInput input, string command)
    {
        var found = new HashSet<int>();
        if (!Read(input.Bindings.Ptr, out byte* data, out int count))
            return found;

        for (int i = 0; i < count; i++)
        {
            byte* e = data + i * _stride;
            if (Command(e) is { } text && Runs(text, command))
                found.Add(*(int*)e);
        }

        return found;
    }

    private static bool Runs(string text, string command)
    {
        foreach (string raw in text.Split('|'))
        {
            string part = raw.Trim();
            if (part.StartsWith("OnRelease ", StringComparison.OrdinalIgnoreCase))
                continue;

            int space = part.IndexOf(' ');
            if ((space < 0 ? part : part[..space]).Equals(command, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>A binding's command string, or null when it has none.</summary>
    private static string? Command(byte* entry)
    {
        char* text = *(char**)(entry + 8);
        int len = *(int*)(entry + 16);
        return text == null || len <= 1 ? null : new string(text, 0, len - 1);
    }

    /// <summary>True when every command in "A | OnRelease B | C x" passes the check.</summary>
    private static bool AllDebug(string command, Func<string, bool> isDebug)
    {
        int seen = 0;
        foreach (string raw in command.Split('|'))
        {
            string part = raw.Trim();
            if (part.StartsWith("OnRelease ", StringComparison.OrdinalIgnoreCase))
                part = part[10..].TrimStart();
            if (part.Length == 0)
                continue;

            int space = part.IndexOf(' ');
            if (!isDebug(space < 0 ? part : part[..space]))
                return false;
            seen++;
        }

        return seen > 0;
    }

    /// <summary>Reads the array header and checks every entry looks like a key binding before anything is written.</summary>
    private static bool Read(nint array, out byte* data, out int count)
    {
        data = null;
        count = 0;
        if (array == 0)
            return Fail("no binding array");

        byte* items = *(byte**)array;
        int n = *(int*)(array + 8);
        int max = *(int*)(array + 12);
        if (items == null || n <= 0 || n > max || n > MaxBindings)
            return Fail("array header " + n + "/" + max);

        if (_stride == 0)
        {
            if (Valid(items, n, BindStride) && KeyHits(items, n, BindStride) >= MinKeyHits)
                _stride = BindStride;
            else
                return Fail(n + " entries, not key bindings at " + BindStride + " bytes");
        }
        else if (!Valid(items, n, _stride))
            return Fail("entries changed shape at " + _stride + " bytes");

        data = items;
        count = n;
        return true;
    }

    /// <summary>Every entry has a sane name and a well-formed command string at this size. Stops at the first bad one.</summary>
    private static bool Valid(byte* items, int n, int stride)
    {
        for (int i = 0; i < n; i++)
        {
            byte* e = items + i * stride;
            int index = *(int*)e;
            int number = *(int*)(e + 4);
            nint text = *(nint*)(e + 8);
            int len = *(int*)(e + 16);
            int cap = *(int*)(e + 20);
            if (index <= 0 || number < 0 || number > 0xFFFF || len < 0 || len > cap || cap > MaxCommand || (cap > 0) != (text != 0))
                return false;
        }

        return true;
    }

    /// <summary>Binding key names any sane binding table contains, used only to recognise the array before writing to it.</summary>
    private static HashSet<int> KnownNames => _knownNames ??=
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(new[] { "Enter", "SpaceBar", "Escape", "MouseX", "MouseY", "LeftMouseButton", "RightMouseButton", "Up", "Down" })
            .Select(n => new FName(n).Index).ToHashSet();

    private static HashSet<int>? _knownNames;

    private static int KeyHits(byte* items, int n, int stride)
    {
        HashSet<int> keys = KnownNames;
        int hits = 0;
        for (int i = 0; i < n; i++)
        {
            if (keys.Contains(*(int*)(items + i * stride)))
                hits++;
        }

        return hits;
    }

    private static bool Fail(string why)
    {
        if (!_warned)
        {
            _warned = true;
            Log.Info("Couldn't read the game's key bindings (" + why + "), so developer bindings stay live.");
        }

        return false;
    }

    private static Keys[] BuildMenuKeys()
    {
        var keys = new List<Keys>
        {
            Keys.Enter, Keys.Space, Keys.Back, Keys.Tab, Keys.Home, Keys.Delete, Keys.Insert, Keys.PageUp, Keys.PageDown,
            Keys.Up, Keys.Down, Keys.Left, Keys.Right,
        };
        for (Keys k = Keys.D0; k <= Keys.D9; k++)
            keys.Add(k);
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++)
            keys.Add(k);
        for (Keys k = Keys.A; k <= Keys.Z; k++)
            keys.Add(k);
        return keys.ToArray();
    }
}
