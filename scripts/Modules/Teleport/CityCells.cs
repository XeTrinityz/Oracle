using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using BmSDK;
using BmSDK.BmGame;

namespace CeZeY.Oracle;

/// <summary>
/// The city's streaming districts: their bounds, and whether each is loaded. Shipped complete in cells.cache
/// and refreshed from the game, because a district that hasn't streamed in has no ground to stand on.
/// </summary>
internal static class CityCells
{
    public readonly record struct Cell(string Name, Vector3 Min, Vector3 Max)
    {
        public bool Contains(Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z - 5000f && p.Z <= Max.Z + 5000f;

        public float Size => (Max.X - Min.X) * (Max.Y - Min.Y);

        public float DistanceXY(Vector3 p)
        {
            float dx = Math.Max(0f, Math.Max(Min.X - p.X, p.X - Max.X));
            float dy = Math.Max(0f, Math.Max(Min.Y - p.Y, p.Y - Max.Y));
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    private const string FileName = "cells.cache";
    private const double ScanSeconds = 2.0;

    // The native entry is 112 bytes but BmSDK declares FLevelVolumeDesc as 97, so TArray indexing would drift.
    // It is read through ReadProcessMemory because a wrong layout then fails a check instead of crashing the game.
    private const int Stride = 112;
    private const int NameAt = 8;
    private const int FlagsAt = 36;
    private const int BoundsAt = 44;
    private const int FlagLoaded = 1;
    private const int FlagVisible = 2;
    private const int FlagOverworld = 16;
    private const int MaxEntries = 4096;

    public static readonly Dictionary<string, Cell> All = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> _flags = new(StringComparer.OrdinalIgnoreCase);
    private static string[] _names = Array.Empty<string>();
    private static nint _data;
    private static bool _loaded;
    private static bool _seeded;
    private static bool _broken;
    private static bool _live;
    private static Every _scan;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    private static bool IsCity(string name) => name.StartsWith("City", StringComparison.OrdinalIgnoreCase);

    private static bool ReadMem(nint at, byte[] buffer) =>
        at != 0 && ReadProcessMemory(GetCurrentProcess(), at, buffer, buffer.Length, out nint got) && got == buffer.Length;

    private static string ReadText(nint at)
    {
        var head = new byte[16];
        if (!ReadMem(at, head))
            return "";

        int count = BitConverter.ToInt32(head, 8);
        if (count <= 1 || count > 128)
            return "";

        var chars = new byte[(count - 1) * 2];
        return ReadMem((nint)BitConverter.ToInt64(head, 0), chars) ? Encoding.Unicode.GetString(chars) : "";
    }

    /// <summary>Reads the game's live level list. Returns false if it isn't available or doesn't look right.</summary>
    public static bool ReadLevels(bool seed)
    {
        _live = false;
        if (_broken || World.Info is not { } info)
            return false;

        var head = new byte[16];
        if (!ReadMem(info.LevelVolumeList.Ptr, head))
            return false;

        nint data = (nint)BitConverter.ToInt64(head, 0);
        int count = BitConverter.ToInt32(head, 8);
        if (data == 0 || count <= 0 || count > MaxEntries)
            return false;

        var raw = new byte[count * Stride];
        if (!ReadMem(data, raw))
            return false;

        if (data != _data || _names.Length != count)
        {
            var names = new string[count];
            int named = 0;
            for (int i = 0; i < count; i++)
            {
                names[i] = ReadText(data + i * Stride + NameAt);
                if (names[i].Length > 0 && char.IsLetter(names[i][0]))
                    named++;
            }

            if (named < count * 3 / 4)
            {
                _broken = true;
                Log.Info("Teleport: the game's level list looks different than expected, using " + FileName + " only.");
                return false;
            }

            _data = data;
            _names = names;
        }

        _flags.Clear();
        int added = 0;
        for (int i = 0; i < count; i++)
        {
            string name = _names[i];
            if (name.Length == 0)
                continue;

            int at = i * Stride;
            int flags = BitConverter.ToInt32(raw, at + FlagsAt);
            _flags[name] = flags;
            if (!seed || (flags & FlagOverworld) == 0 || !IsCity(name))
                continue;

            float B(int k) => MathF.Round(BitConverter.ToSingle(raw, at + BoundsAt + k * 4));
            var cell = new Cell(name, new Vector3(B(0), B(1), B(2)), new Vector3(B(3), B(4), B(5)));
            if (cell.Max.X <= cell.Min.X || cell.Max.Y <= cell.Min.Y)
                continue;
            if (All.TryGetValue(name, out Cell old) && old == cell)
                continue;

            All[name] = cell;
            added++;
        }

        _live = true;
        if (seed && added > 0)
        {
            Save();
            Log.Info("Teleport: updated " + added + " city cells from the game's level list (" + All.Count + " known).");
        }

        return true;
    }

    /// <summary>True when the district at a point is loaded and visible, or when that can't be known.</summary>
    public static bool Ready(Vector3 at)
    {
        if (!_live)
            return true;

        string? name = At(at);
        if (name == null || !_flags.TryGetValue(name, out int flags))
            return true;

        return (flags & (FlagLoaded | FlagVisible)) == (FlagLoaded | FlagVisible);
    }

    /// <summary>The name of an entry in the game's level list, by the index the game's own lookups return.</summary>
    public static string? LevelName(int index)
    {
        if (index < 0)
            return null;
        if (_names.Length == 0)
            Safe.Try("level list", () => ReadLevels(false));

        return index < _names.Length && _names[index].Length > 0 ? _names[index] : null;
    }

    /// <summary>Whether a named level is loaded and visible, or null when the level list doesn't know it.</summary>
    public static bool? Shown(string name)
    {
        if (!_live || !_flags.TryGetValue(name, out int flags))
            return null;

        return (flags & (FlagLoaded | FlagVisible)) == (FlagLoaded | FlagVisible);
    }

    public static string? At(Vector3 at)
    {
        Cell? best = null;
        foreach (Cell c in All.Values)
        {
            if (c.Contains(at) && (best == null || c.Size < best.Value.Size))
                best = c;
        }

        return best?.Name;
    }

    /// <summary>Reads cells.cache if nothing has yet, so the district list works before the first tick.</summary>
    public static void Ensure()
    {
        if (!_loaded)
            Load();
    }

    /// <summary>Slow background refresh, so new districts are known before a teleport needs them.</summary>
    public static void Tick()
    {
        Ensure();

        if (!_scan.Elapsed(ScanSeconds))
            return;

        if (!_seeded && !_broken)
            _seeded = Safe.Try("level list", () => ReadLevels(true));

        bool changed = false;
        foreach (RLevelVolumeOW v in World.All<RLevelVolumeOW>())
        {
            string name = v.Level.ToString();
            if (name.Length == 0 || name == "None" || name.Contains(' ') || !IsCity(name) || All.ContainsKey(name))
                continue;

            v.GetComponentsBoundingBox(out GameObject.FBox box);
            if (box.IsValid == 0)
                continue;

            All[name] = new Cell(name, box.Min, box.Max);
            changed = true;
        }

        if (changed)
            Save();
    }

    private static void Load()
    {
        _loaded = true;
        try
        {
            string path = Path.Combine(App.Dir, FileName);
            if (!File.Exists(path))
                return;

            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 7 || !IsCity(p[0]))
                    continue;

                float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                All[p[0]] = new Cell(p[0], new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6)));
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
            foreach (Cell c in All.Values)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0} {2:0} {3:0} {4:0} {5:0} {6:0}",
                    c.Name, c.Min.X, c.Min.Y, c.Min.Z, c.Max.X, c.Max.Y, c.Max.Z));
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(Path.Combine(App.Dir, FileName), lines);
        }
        catch (Exception ex)
        {
            Log.Info("Could not write " + FileName + ": " + ex.Message);
        }
    }
}
