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
