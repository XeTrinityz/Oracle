using System.Globalization;

namespace CeZeY.Oracle;

internal static class Log
{
    private const double ThrottleSeconds = 5.0;

    private static readonly Dictionary<string, double> _next = new(StringComparer.Ordinal);
    private static string _path = "";

    public static bool Diagnostics;

    public static void Open(string dir)
    {
        try
        {
            _path = Path.Combine(dir, "Oracle.log");
            // A hot reload opens the log again: the one before is kept, so a fight logged just before an update survives.
            var old = new FileInfo(_path);
            if (old.Exists && old.Length > 200)
                File.Copy(_path, Path.Combine(dir, "Oracle.prev.log"), true);
            File.WriteAllText(_path, "Oracle " + App.Version + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine);
        }
        catch
        {
            _path = "";
        }
    }

    public static void Info(string text)
    {
        Debug.Log("[Oracle] " + text);
        if (_path.Length == 0)
            return;

        try
        {
            File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " | " + text + Environment.NewLine);
        }
        catch
        {
            // A locked log file must never cost a frame or a feature.
        }
    }

    public static void Detail(string text)
    {
        if (Diagnostics)
            Info(text);
    }

    /// <summary>
    /// An exception's message and where in Oracle it was thrown: the innermost Oracle frame of its stack, with the
    /// line when the build has one. A message alone ("Object reference not set...") says nothing about where.
    /// </summary>
    public static string Why(Exception ex)
    {
        try
        {
            return Where(ex);
        }
        catch (Exception)
        {
            // Reading the stack can fail on frames that pass through native code. The message still helps.
            return ex.Message + " (" + ex.GetType().Name + ")";
        }
    }

    private static string Where(Exception ex)
    {
        var trace = new System.Diagnostics.StackTrace(ex, true);
        foreach (System.Diagnostics.StackFrame frame in trace.GetFrames())
        {
            System.Reflection.MethodBase? m = frame.GetMethod();
            if (m?.DeclaringType?.Namespace?.StartsWith("CeZeY", StringComparison.Ordinal) != true)
                continue;

            string type = m.DeclaringType!.Name;
            int line = frame.GetFileLineNumber();
            return ex.Message + " (" + ex.GetType().Name + " in " + type + "." + m.Name + (line > 0 ? " line " + line : "") + ")";
        }

        return ex.Message + " (" + ex.GetType().Name + ")";
    }

    /// <summary>Logs at most once every few seconds per key, so one failing tick can't flood the log or hide another error.</summary>
    public static void Throttled(string key, string text)
    {
        double now = Clock.Now;
        if (_next.TryGetValue(key, out double due) && now < due)
            return;

        _next[key] = now + ThrottleSeconds;
        Info(text);
    }
}
