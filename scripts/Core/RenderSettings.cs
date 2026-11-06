using System.Globalization;
using System.Text.RegularExpressions;

namespace CeZeY.Oracle;

/// <summary>
/// The engine's live render settings (the [SystemSettings] values in BmSystemSettings.ini), changed through the
/// shipping exe's "scale set" console command. The game's own value is remembered the first time Oracle changes
/// a setting, so it can be put back exactly.
/// <para>
/// "scale set" also saves the new value into BmSystemSettings.ini, which would keep Oracle's value after the mod
/// is gone. So every change is followed by writing the game's value back into the ini, and the game's values
/// are kept in render_originals.txt until put back, so a crash or quit with an override on is repaired at the
/// next start.
/// </para>
/// </summary>
internal static class RenderSettings
{
    // Survives hot reload, so a reload over a live override still knows the game's real value.
    private static Dictionary<string, string> Originals =>
        Shared.Keep("RenderOriginals", () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static string IniPath => IniFile.GamePath("BmSystemSettings.ini");

    private static string RecordPath => Path.Combine(App.Dir, "render_originals.txt");

    /// <summary>Whether the console is reachable yet. Nothing can be read or changed before it is.</summary>
    public static bool Ready => World.Pc != null;

    /// <summary>Reads the game values left by an earlier session and puts them back into the ini.</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(RecordPath))
            {
                foreach (string line in File.ReadLines(RecordPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        Originals.TryAdd(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
        }
        catch (Exception e)
        {
            Log.Info("render settings record read failed: " + e.Message);
        }

        RepairIni();
    }

    // Any "scale set" re-saves every live value, so after each one all of the game's values go back.
    private static void RepairIni()
    {
        foreach ((string name, string value) in Originals.ToArray())
            WriteIni(name, value);
    }

    /// <summary>Sets a value, remembering the game's first. Returns false if the engine didn't accept it.</summary>
    public static bool Override(string name, float value)
    {
        if (!Ready)
            return false;

        if (!Originals.ContainsKey(name))
        {
            string? original = Current(name);
            if (original == null)
            {
                Log.Info("render setting " + name + ": couldn't read the game's value, so it won't be changed.");
                return false;
            }

            Originals[name] = original;
            SaveRecord();
            Log.Info("render setting " + name + ": game value " + original);
        }

        bool ok = Set(name, value.ToString("0.0###", CultureInfo.InvariantCulture));
        RepairIni();
        return ok;
    }

    /// <summary>Puts the game's value back, if Oracle changed it.</summary>
    public static void Restore(string name)
    {
        if (!Ready || !Originals.TryGetValue(name, out string? original))
            return;

        if (Set(name, original))
        {
            WriteIni(name, original);
            Originals.Remove(name);
            SaveRecord();
        }

        RepairIni();
    }

    private static void SaveRecord()
    {
        try
        {
            if (Originals.Count == 0)
                File.Delete(RecordPath);
            else
                File.WriteAllLines(RecordPath, Originals.Select(p => p.Key + "=" + p.Value));
        }
        catch (Exception e)
        {
            Log.Info("render settings record write failed: " + e.Message);
        }
    }

    /// <summary>Rewrites one [SystemSettings] line of the ini, leaving every other byte as it was.</summary>
    private static void WriteIni(string name, string value) => IniFile.Set(IniPath, "SystemSettings", name, value);

    public static void RestoreAll()
    {
        foreach (string name in Originals.Keys.ToArray())
            Restore(name);
    }

    private static bool Set(string name, string value)
    {
        string reply = Cvars.Exec("scale set " + name + " " + value);
        bool ok = reply.Contains(" set to ", StringComparison.OrdinalIgnoreCase);
        if (!ok)
            Log.Info("render setting " + name + " = " + value + " was refused: " + (reply.Trim().Length > 0 ? reply.Trim() : "no reply"));
        return ok;
    }

    /// <summary>The live value from the engine's settings dump, or the saved one from the ini.</summary>
    private static string? Current(string name)
    {
        var pattern = new Regex(@"\b" + Regex.Escape(name) + @"\b\s*[=:]?\s*(-?\d+(?:\.\d+)?|True|False)", RegexOptions.IgnoreCase);

        Match live = pattern.Match(Cvars.Exec("scale dump"));
        if (live.Success)
            return live.Groups[1].Value;

        return IniValue(name);
    }

    private static string? IniValue(string name) => IniFile.Get(IniPath, "SystemSettings", name);
}
