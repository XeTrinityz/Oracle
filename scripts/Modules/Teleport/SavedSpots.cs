using System.Globalization;
using System.Numerics;

namespace CeZeY.Oracle;

/// <summary>The player's saved locations, kept in their own file because settings.toml only holds feature values.</summary>
internal static class SavedSpots
{
    public const int Slots = 8;

    public sealed record Entry(string Map, Vector3 At, float Yaw, string Name);

    private const string FileName = "saved_spots.txt";
    private const char Gap = '|';

    private static readonly Entry?[] _entries = new Entry?[Slots];

    private static string SavePath => Path.Combine(App.Dir, FileName);

    public static Entry? Get(int slot) => _entries[slot];

    public static void Set(int slot, Entry? entry)
    {
        _entries[slot] = entry;
        Save();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SavePath))
                return;

            foreach (string line in File.ReadAllLines(SavePath))
            {
                string[] p = line.Split(Gap);
                if (p.Length != 7 || !int.TryParse(p[0], out int slot) || slot < 0 || slot >= Slots)
                    continue;

                float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                _entries[slot] = new Entry(p[1], new Vector3(F(2), F(3), F(4)), F(5), p[6]);
            }
        }
        catch (Exception ex)
        {
            Log.Info("Could not read " + FileName + ": " + ex.Message);
        }
    }

    private static void Save()
    {
        try
        {
            var lines = new List<string>();
            for (int i = 0; i < Slots; i++)
            {
                if (_entries[i] is { } e)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{1}{0}{2}{0}{3:0.#}{0}{4:0.#}{0}{5:0.#}{0}{6:0.#}{0}{7}",
                        Gap, i, e.Map, e.At.X, e.At.Y, e.At.Z, e.Yaw, e.Name));
                }
            }

            File.WriteAllLines(SavePath, lines);
        }
        catch (Exception ex)
        {
            Log.Info("Could not write " + FileName + ": " + ex.Message);
        }
    }
}
