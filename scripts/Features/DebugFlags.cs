using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Access to the game's own debug switches, which live on a single persistent object.</summary>
internal static class DebugFlags
{
    private static RPersistentDebugData? _data;

    public static RPersistentDebugData? Data
    {
        get
        {
            if (_data is { IsValid: true })
                return _data;

            _data = World.All<RPersistentDebugData>().FirstOrDefault();
            return _data;
        }
    }

    public static void Forget() => _data = null;

    /// <summary>A toggle for one debug switch. The game clears these on some transitions, so it's kept applied.</summary>
    public static Toggle AddFlag(string id, string label, string help, Func<RPersistentDebugData, bool> get, Action<RPersistentDebugData, bool> set)
    {
        void Write(bool on)
        {
            if (Data is { } d && get(d) != on)
                set(d, on);
        }

        return AddToggle(id, label, help, Write).Maintain(() => Write(true));
    }
}
