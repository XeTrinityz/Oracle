namespace CeZeY.Oracle;

/// <summary>Boundary guards. Nothing may throw past these into BmSDK.</summary>
internal static class Safe
{
    public static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Throttled(what, what + " failed: " + Log.Why(ex));
        }
    }

    public static bool Try(string what, Func<bool> check, bool fallback = false)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            Log.Throttled(what, what + " failed: " + Log.Why(ex));
            return fallback;
        }
    }
}
