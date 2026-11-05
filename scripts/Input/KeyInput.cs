using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.Oracle;

/// <summary>Routes a key press: the game's own use first, then the radial, the menu, then hotkeys.</summary>
internal static class KeyInput
{
    public static void Pressed(Keys key)
    {
        Pad.KeyboardUsed();
        if (Menu.Binding == null && Characters.I.EntryKey(key))
            return;

        // The open radial takes every key, so a number picks a slot instead of running a hotkey. The menu key
        // swaps the radial for the menu.
        if (Radial.IsLive)
        {
            if (key == Settings.MenuKey)
            {
                Radial.Close();
                Menu.Open();
            }
            else
                Radial.Press(key);
            return;
        }

        if (key == Settings.MenuKey && Menu.Binding == null)
        {
            Menu.Toggle();
            return;
        }

        if (Menu.IsOpen)
        {
            Menu.Press(key, false);
            return;
        }

        // A hotkey saved on a key that later became the radial key gives way to the radial.
        if (key == Radial.Key && Radial.Enabled)
            return;

        if (Settings.TryKey(key, out IReadOnlyList<Feature> features))
            Catalog.Hotkey(features);
    }
}

/// <summary>
/// In some states the game stops ticking mods but keeps rendering. When ticks go quiet this reads the keyboard
/// directly and drives the update from the render hook, so the menu and hotkeys never go dead.
/// </summary>
internal static class Fallback
{
    private const double QuietSeconds = 0.5;

    private static readonly bool[] _down = new bool[256];
    private static readonly List<Keys> _watch = new();

    private static long _lastSdkTick = Stopwatch.GetTimestamp();
    private static bool _logged;

    public static bool Active => Stopwatch.GetElapsedTime(_lastSdkTick).TotalSeconds > QuietSeconds;

    public static void NoteSdkTick() => _lastSdkTick = Stopwatch.GetTimestamp();

    public static void Frame()
    {
        if (!Active || World.Paused)
            return;

        if (!_logged)
        {
            _logged = true;
            Log.Info("The game stopped ticking mods here, Oracle now reads the keyboard itself.");
        }

        _watch.Clear();
        _watch.Add(Settings.MenuKey);
        _watch.AddRange(Menu.ControlKeys);
        if (Menu.IsOpen)
            _watch.AddRange(Menu.TypingKeys);
        if (Radial.IsLive)
            _watch.AddRange(Radial.WatchKeys);
        _watch.AddRange(Settings.BoundKeys);
        if (Characters.I.PlayingOther)
            _watch.Add(Keys.E);

        bool front = Native.GameInFront();
        foreach (Keys k in _watch)
        {
            int vk = (int)k & 0xFF;
            bool down = front && (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down && !_down[vk])
                KeyInput.Pressed(k);
            _down[vk] = down;
        }

        App.Tick();
    }
}
