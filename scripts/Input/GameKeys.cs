using BmSDK;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Keeps the game from acting on the keys the open menu uses (letters for search, ENTER, the arrows and so on).
/// The game ignores IgnoreButtonInput, so this renames those keys in the player's binding table while the menu is
/// open and puts back exactly what it renamed when it closes. Only the 8-byte key name of each binding is touched,
/// never its command string, so no game-owned memory changes hands.
/// </summary>
internal static unsafe class GameKeys
{
    /// <summary>
    /// Bytes per binding to try, smallest first. Each starts with the FName (8) and the command FString (16). In
    /// game it measured 28 (packed, matching BmSDK's FKeyBind), but the size is still confirmed at run time rather
    /// than trusted: the first size at which every entry reads as a binding and enough names are key names.
    /// Smallest first keeps every read inside the array.
    /// </summary>
    private static readonly int[] Strides = { 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80 };

    private const int MaxBindings = 4096;
    private const int MaxCommand = 4096;
    private const int MinKeyHits = 8;

    /// <summary>The game's names for the keys the menu uses. Escape stays live so the pause menu still opens.</summary>
    private static readonly string[] KeyNames = BuildNames();

    private static readonly List<(int Slot, int Index, int Number)> _muted = new();
    private static HashSet<int>? _keys;
    private static int _mark = -1;
    private static int _stride;
    private static nint _input;
    private static bool _warned;
    private static bool _reported;

    private static HashSet<int> Keys => _keys ??= KeyNames.Select(n => new FName(n).Index).ToHashSet();

    public static void Mute(PlayerController pc)
    {
        Unmute(pc);
        PlayerInput? input = pc.PlayerInput;
        if (input == null)
            return;

        if (!Read(input.Bindings.Ptr, out byte* data, out int count))
            return;

        if (_mark < 0)
            _mark = new FName("OracleMenuMuted").Index;

        HashSet<int> keys = Keys;
        for (int i = 0; i < count; i++)
        {
            int* name = (int*)(data + i * _stride);
            if (name[1] != 0 || !keys.Contains(name[0]))
                continue;

            _muted.Add((i, name[0], name[1]));
            name[0] = _mark;
        }

        _input = input.Ptr;
        if (!_reported)
        {
            _reported = true;
            Log.Info("Menu keys: " + count + " game bindings at " + _stride + " bytes each, " + _muted.Count + " switched off while the menu is open.");
        }
    }

    /// <summary>
    /// Puts the renamed keys back, only on the same live input object and only where a binding still carries the
    /// mark. If the game re-bound a key meanwhile, its new binding wins and the old one stays muted.
    /// </summary>
    public static void Unmute(PlayerController? pc)
    {
        if (_muted.Count == 0)
            return;

        try
        {
            if (pc?.PlayerInput is not { } input || input.Ptr != _input || !Read(input.Bindings.Ptr, out byte* data, out int count))
                return;

            var live = new HashSet<int>();
            for (int i = 0; i < count; i++)
                live.Add(((int*)(data + i * _stride))[0]);

            foreach ((int slot, int index, int number) in _muted)
            {
                if (slot >= count)
                    continue;

                int* name = (int*)(data + slot * _stride);
                if (name[0] == _mark && name[1] == 0 && !live.Contains(index))
                {
                    name[0] = index;
                    name[1] = number;
                }
            }
        }
        finally
        {
            _muted.Clear();
            _input = 0;
        }
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
            foreach (int s in Strides)
            {
                if (Valid(items, n, s) && KeyHits(items, n, s) >= MinKeyHits)
                {
                    _stride = s;
                    break;
                }
            }

            if (_stride == 0)
                return Fail(n + " entries, no entry size fits");
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

    private static int KeyHits(byte* items, int n, int stride)
    {
        HashSet<int> keys = Keys;
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
            Log.Info("Couldn't read the game's key bindings (" + why + "), so game keys stay live while the menu is open.");
        }

        return false;
    }

    private static string[] BuildNames()
    {
        var names = new List<string>
        {
            "Enter", "SpaceBar", "BackSpace", "Tab", "Home", "Delete", "Insert", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "NumPadZero", "NumPadOne", "NumPadTwo", "NumPadThree", "NumPadFour", "NumPadFive", "NumPadSix", "NumPadSeven", "NumPadEight", "NumPadNine",
        };
        for (char c = 'A'; c <= 'Z'; c++)
            names.Add(c.ToString());
        return names.ToArray();
    }
}
