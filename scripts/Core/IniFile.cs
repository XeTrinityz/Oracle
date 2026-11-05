namespace CeZeY.Oracle;

/// <summary>
/// Reads and rewrites single values in the game's ini files (BmGame/Config), leaving every other byte as it was.
/// The files are plain ASCII with CRLF line ends; Latin-1 round-trips any byte, so nothing else can change.
/// </summary>
internal static class IniFile
{
    public static string GamePath(string file) => Path.GetFullPath(Path.Combine(App.Dir, "..", "..", "Config", file));

    public static string? Get(string path, string section, string key)
    {
        try
        {
            bool inSection = false;
            foreach (string raw in File.ReadLines(path, System.Text.Encoding.Latin1))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (inSection && eq > 0 && line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
            }
        }
        catch (Exception e)
        {
            Log.Info("ini read " + Path.GetFileName(path) + " " + key + " failed: " + e.Message);
        }

        return null;
    }

    /// <summary>Rewrites an existing key's value. Returns true if the file now holds it.</summary>
    public static bool Set(string path, string section, string key, string value)
    {
        try
        {
            var latin1 = System.Text.Encoding.Latin1;
            string[] lines = File.ReadAllText(path, latin1).Split('\n');
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.StartsWith('['))
                {
                    inSection = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (!inSection || eq <= 0 || !line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (line.Substring(eq + 1).Trim() == value)
                    return true;

                lines[i] = key + "=" + value + (lines[i].EndsWith('\r') ? "\r" : "");
                File.WriteAllText(path, string.Join('\n', lines), latin1);
                return true;
            }

            Log.Info("ini write " + Path.GetFileName(path) + ": no " + key + " in [" + section + "]");
        }
        catch (Exception e)
        {
            Log.Info("ini write " + Path.GetFileName(path) + " " + key + " failed: " + e.Message);
        }

        return false;
    }
}
