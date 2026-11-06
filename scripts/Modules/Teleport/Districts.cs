using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace CeZeY.Oracle;

/// <summary>
/// The city's streaming districts, read from the game's own district volumes, which are always loaded. The
/// flight asks which district a point is in to enter each one as it crosses the line, and the DISTRICT page
/// lists them with their bounds.
/// </summary>
internal static class Districts
{
    public readonly record struct Cell(string Name, Vector3 Min, Vector3 Max);

    private const double RefreshSeconds = 2.0;

    private static readonly Dictionary<string, Cell> _all = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FName> _names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(RLevelVolumeOW Volume, string Name)> _volumes = new();
    private static Every _refresh;

    /// <summary>Every city district by name, with the bounds of its volume.</summary>
    public static IReadOnlyDictionary<string, Cell> All
    {
        get
        {
            Refresh();
            return _all;
        }
    }

    /// <summary>The district volume a point is inside, by the game's own priority, or null outside the city.</summary>
    public static string? At(Vector3 point)
    {
        Refresh();
        RLevelVolumeOW? best = null;
        string? name = null;
        foreach ((RLevelVolumeOW v, string n) in _volumes)
        {
            if (v is { IsValid: true } && v.EncompassesPoint(point) && (best == null || v.Priority > best.Priority))
            {
                best = v;
                name = n;
            }
        }

        return name;
    }

    /// <summary>A district's name as the game's FName, made once per district rather than per use.</summary>
    public static FName NameOf(string level)
    {
        if (!_names.TryGetValue(level, out FName name))
            _names[level] = name = new FName(level);
        return name;
    }

    /// <summary>The volume of a named district, if the game has one loaded.</summary>
    public static RLevelVolumeOW? Volume(string level)
    {
        Refresh();
        foreach ((RLevelVolumeOW v, string name) in _volumes)
        {
            if (v is { IsValid: true } && name.Equals(level, StringComparison.OrdinalIgnoreCase))
                return v;
        }

        return null;
    }

    /// <summary>A new world is loading: every volume is about to go stale.</summary>
    public static void Forget()
    {
        _all.Clear();
        _volumes.Clear();
        _refresh.Force();
    }

    private static void Refresh()
    {
        if (!_refresh.Elapsed(RefreshSeconds))
            return;

        try
        {
            _volumes.Clear();
            foreach (RLevelVolumeOW v in World.All<RLevelVolumeOW>())
            {
                string name = v.Level.ToString();
                if (name.Length == 0 || name == "None" || !name.StartsWith("City", StringComparison.OrdinalIgnoreCase))
                    continue;

                _volumes.Add((v, name));
                if (_all.ContainsKey(name))
                    continue;

                v.GetComponentsBoundingBox(out GameObject.FBox box);
                if (box.IsValid != 0)
                    _all[name] = new Cell(name, box.Min, box.Max);
            }
        }
        catch (Exception ex)
        {
            Log.Throttled("districts", "Districts: couldn't read the city's volumes: " + ex.Message);
        }
    }
}
