using BmSDK;
using BmSDK.BmGame;

namespace CeZeY.Oracle;

/// <summary>
/// The UE3 console's get and set, which reach every instance and the class default at once. Used for config
/// properties that must also apply to objects which haven't spawned yet.
/// </summary>
internal static class Cvars
{
    private const int MaxOriginalLength = 200;

    // Same key as Oracle 2.x, so a hot reload over the old version still knows the real vanilla values.
    private static Dictionary<string, string> Originals =>
        Shared.Keep("Originals", () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static string Exec(string command)
    {
        RPlayerController? pc = World.Pc;
        if (pc == null)
            return "";

        string result = pc.ConsoleCommand(new FString(command), false).ToString() ?? "";
        Log.Detail("exec " + command + " => " + result.Trim());
        return result;
    }

    public static string Get(string cls, string prop) => Exec("get " + cls + " " + prop).Trim();

    public static void Set(string cls, string prop, string value) => Exec("set " + cls + " " + prop + " " + value);

    /// <summary>Sets a value, remembering the vanilla one the first time so it can be put back.</summary>
    public static void Override(string cls, string prop, string value)
    {
        string key = cls + " " + prop;
        if (!Originals.ContainsKey(key))
        {
            string original = Get(cls, prop);
            if (original.Length > 0 && original.Length < MaxOriginalLength && !original.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase))
                Originals[key] = original;
        }

        Set(cls, prop, value);
    }

    /// <summary>Puts back a value only if this mod changed it, for properties the game may have set itself.</summary>
    public static void RestoreIfChanged(string cls, string prop)
    {
        if (Originals.TryGetValue(cls + " " + prop, out string? original))
            Set(cls, prop, original);
    }

    public static void Restore(string cls, string prop, string fallback)
    {
        if (Originals.TryGetValue(cls + " " + prop, out string? original))
            Set(cls, prop, original);
        else if (Get(cls, prop) != fallback)
            Set(cls, prop, fallback);
    }
}
