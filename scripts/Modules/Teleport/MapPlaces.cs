using System.Numerics;
using System.Text.RegularExpressions;

namespace CeZeY.Oracle;

/// <summary>
/// Every place on the game's world map. BmGame.ini lists them all in world coordinates, and the game's
/// localization files name them, so this works from the first load with nothing to discover.
/// </summary>
internal static class MapPlaces
{
    public sealed record Place(string Key, string Label, Vector3 At, bool Surface);

    /// <summary>One category. With a Word, entries are numbered ("MIAGANI TROPHY 12"); without one they use the game's name.</summary>
    public sealed record Group(string Key, string Label, string? Word, bool Surface, params string[] Types)
    {
        public List<Place> Items { get; } = new();
    }

    private const string MostWantedType = "MostWanted";
    private const string RadiusSuffix = "Radius";
    private const string FrontendSection = "LevelDesc";
    private const string UnusedText = "ENTER BUG";

    private static readonly string[] CityMaps = { "Batentry", "Cityx", "Cityy", "Cityz" };

    private static readonly Regex Row = new(
        @"^\s*\.?MapElementItemList=\(MapName=""([^""]*)"", ItemType=""([^""]*)"", ItemName=""([^""]*)"", X=(-?\d+), Y=(-?\d+), Z=(-?\d+)",
        RegexOptions.Multiline);

    private static readonly Regex Number = new(@"\d+$");

    public static readonly List<Group> Groups = new()
    {
        new("landmarks", "LANDMARKS", null, true, "LabelTextBigArea", "LabelTextArea", "LabelTextFeature1", "BuildingMarker"),
        new("trophies", "RIDDLER TROPHIES", "TROPHY", false, "RiddlerPickup"),
        new("breakables", "RIDDLER BREAKABLES", "BREAKABLE", false, "RiddlerBreakable"),
        new("riddles", "RIDDLES", "RIDDLE", false, "RiddlerCameo"),
        new("bombs", "RIDDLER BOMBS", "BOMB", false, "RiddlerBomb"),
        new("races", "RIDDLER RACES", "RACE", false, "NigmaRace"),
        new("ar", "AR CHALLENGES", "AR CHALLENGE", false, "ARChallenge"),
        new("most_wanted", "MOST WANTED", null, false),
    };

    private static bool _loaded;

    public static void Ensure()
    {
        if (_loaded)
            return;

        _loaded = true;
        try
        {
            string game = Path.GetFullPath(Path.Combine(App.Dir, "..", ".."));
            string localization = Path.Combine(game, "Localization", "INT");
            Dictionary<string, string> frontend = Names(Path.Combine(localization, "FRONTEND.int"), FrontendSection);
            Dictionary<string, string> interfaceText = Names(Path.Combine(localization, "GFxUI.int"), null);
            Read(File.ReadAllText(Path.Combine(game, "Config", "BmGame.ini")), frontend, interfaceText);
            Log.Info("Teleport: map places: " + string.Join(", ", Groups.Select(g => g.Label + " " + g.Items.Count)) + ".");
        }
        catch (Exception ex)
        {
            Log.Info("Could not read the map places: " + ex.Message);
        }
    }

    /// <summary>Every Riddler item on any map, interiors included, as listed: repeats are possible.</summary>
    public static List<(string Type, string Key)> RiddlerRows()
    {
        string ini = File.ReadAllText(Path.GetFullPath(Path.Combine(App.Dir, "..", "..", "Config", "BmGame.ini")));
        return Row.Matches(ini)
            .Where(m => m.Groups[2].Value.StartsWith("Riddler", StringComparison.Ordinal))
            .Select(m => (m.Groups[2].Value, m.Groups[3].Value))
            .ToList();
    }

    private static void Read(string ini, Dictionary<string, string> frontend, Dictionary<string, string> interfaceText)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Row.Matches(ini))
        {
            string map = m.Groups[1].Value;
            string type = m.Groups[2].Value;
            string key = m.Groups[3].Value;
            if (!Array.Exists(CityMaps, c => c.Equals(map, StringComparison.OrdinalIgnoreCase)) || key.EndsWith(RadiusSuffix, StringComparison.Ordinal))
                continue;

            Group? group = GroupOf(type);
            if (group == null || !seen.Add(key))
                continue;

            string label = group.Word != null ? Numbered(group.Word, key) : Named(key, frontend, interfaceText);
            group.Items.Add(new Place(key, label, new Vector3(int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value)), group.Surface));
        }

        foreach (Group group in Groups)
            Finish(group);
    }

    private static Group? GroupOf(string type)
    {
        foreach (Group group in Groups)
        {
            if (Array.IndexOf(group.Types, type) >= 0)
                return group;
        }

        // Plain "MostWanted" is only Riot_Tutorial, which has no real place behind it.
        return type.StartsWith(MostWantedType, StringComparison.Ordinal) && type != MostWantedType ? Groups[^1] : null;
    }

    private static string Island(string key)
    {
        if (key.StartsWith("CityX", StringComparison.OrdinalIgnoreCase))
            return "MIAGANI ";
        if (key.StartsWith("CityY", StringComparison.OrdinalIgnoreCase))
            return "FOUNDERS ";
        return key.StartsWith("CityZ", StringComparison.OrdinalIgnoreCase) ? "BLEAKE " : "";
    }

    private static string Numbered(string word, string key) => Island(key) + word + " " + key.Substring(key.LastIndexOf('_') + 1).ToUpperInvariant();

    private static string Named(string key, Dictionary<string, string> frontend, Dictionary<string, string> interfaceText)
    {
        if (frontend.TryGetValue(key, out string? name) || interfaceText.TryGetValue(key, out name))
            return name;

        return key.Replace("SS_", "", StringComparison.Ordinal).Replace('_', ' ').ToUpperInvariant();
    }

    /// <summary>Sorts a category by name then number, and numbers any names the game uses more than once.</summary>
    private static void Finish(Group group)
    {
        List<Place> sorted = group.Items
            .OrderBy(p => Number.Replace(p.Label, ""), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => int.TryParse(Number.Match(p.Label).Value, out int n) ? n : 0)
            .ToList();

        var counts = sorted.GroupBy(p => p.Label).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 0);
        group.Items.Clear();
        foreach (Place place in sorted)
        {
            if (!counts.TryGetValue(place.Label, out int used))
            {
                group.Items.Add(place);
                continue;
            }

            counts[place.Label] = used + 1;
            group.Items.Add(place with { Label = place.Label + " " + (used + 1) });
        }
    }

    /// <summary>Reads key=value text from a localization file, from one section or from all of them.</summary>
    private static Dictionary<string, string> Names(string path, string? section)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string current = "";
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('['))
            {
                current = line.Trim().Trim('[', ']');
                continue;
            }

            int gap = line.IndexOf('=');
            if (gap <= 0 || (section != null && current != section))
                continue;

            string text = line.Substring(gap + 1).Trim().Trim('"').Replace('’', '\'').ToUpperInvariant();
            if (text.Length > 0 && !text.Contains(UnusedText, StringComparison.Ordinal))
                names.TryAdd(line.Substring(0, gap).Trim(), text);
        }

        return names;
    }

    /// <summary>The closest landmark, or null when none is within range.</summary>
    public static string? Near(Vector3 at, float range)
    {
        Ensure();
        return Closest(Groups[0], at, range)?.Label;
    }

    /// <summary>The closest place in a category, or null when none is within range.</summary>
    public static Place? Closest(Group group, Vector3 at, float range = float.MaxValue)
    {
        Place? best = null;
        float bestDistance = range == float.MaxValue ? range : range * range;
        foreach (Place place in group.Items)
        {
            float distance = Geo.DistXYSq(place.At, at);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = place;
            }
        }

        return best;
    }
}
