using System.Text;

namespace CeZeY.Oracle;

/// <summary>
/// settings.toml: everything the player chooses. mod.toml stays a read-only manifest, so an update can replace
/// it without touching anyone's settings. Writes are debounced and atomic; hand edits apply while playing.
/// </summary>
internal static class Settings
{
    private const string FileName = "settings.toml";
    private const float SaveDelay = 0.75f;
    private const double PollSeconds = 1.0;
    private static readonly string[] OwnSections = { "general", "hotkeys", "gamepad", "cheats" };

    public static Keys MenuKey = Keys.F3;
    public static bool Remember = true;
    public static float ApplyDelay = 4f;
    public static string PadOpenButton = "back";

    /// <summary>Hotkeys by feature id. Menu rebinding and hand edits share this one source.</summary>
    public static readonly Dictionary<string, Keys> Hotkeys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Keys, List<Feature>> _byKey = new();
    private static DateTime _stamp = DateTime.MinValue;
    private static bool _dirty;
    private static float _saveLeft;
    private static Every _poll;
    private static string _lastError = "";

    private static string SettingsPath => Path.Combine(App.Dir, FileName);

    private static string ManifestPath => Path.Combine(App.Dir, "mod.toml");

    /// <summary>Every feature on a key, in menu order. A key can be shared, so one press can do several things.</summary>
    public static bool TryKey(Keys key, out IReadOnlyList<Feature> features)
    {
        bool found = _byKey.TryGetValue(key, out List<Feature>? list);
        features = list ?? (IReadOnlyList<Feature>)Array.Empty<Feature>();
        return found;
    }

    public static IEnumerable<Keys> BoundKeys => _byKey.Keys;

    public static string KeyFor(Feature f) => Hotkeys.TryGetValue(f.Id, out Keys k) ? k.ToString().ToUpperInvariant() : "";

    /// <summary>The other features already on a key, by label.</summary>
    public static IEnumerable<string> SharingKey(Feature f, Keys key) =>
        _byKey.TryGetValue(key, out List<Feature>? list) ? list.Where(o => o != f).Select(o => o.Label) : Enumerable.Empty<string>();

    /// <summary>Puts a feature on a key. Whatever is already on that key keeps it, so they share it.</summary>
    public static void Bind(Feature f, Keys key)
    {
        Hotkeys[f.Id] = key;
        RebuildKeys();
        MarkDirty();
    }

    public static void Unbind(Feature f)
    {
        if (Hotkeys.Remove(f.Id))
        {
            RebuildKeys();
            MarkDirty();
        }
    }

    public static void MarkDirty()
    {
        _dirty = true;
        _saveLeft = SaveDelay;
    }

    public static void Tick(float dt)
    {
        if (_dirty)
        {
            _saveLeft -= dt;
            if (_saveLeft <= 0f)
                Flush();
            return;
        }

        if (_poll.Elapsed(PollSeconds))
            Load(first: false);
    }

    public static void Flush()
    {
        if (!_dirty)
            return;

        _dirty = false;
        try
        {
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, Compose());
            File.Move(temp, SettingsPath, overwrite: true);
            _stamp = File.GetLastWriteTimeUtc(SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Info("Could not save " + FileName + ": " + ex.Message);
        }
    }

    public static void Load(bool first)
    {
        string path = SettingsPath;
        bool migrate = first && !File.Exists(path);
        string source = migrate ? ManifestPath : path;
        if (!File.Exists(source))
            return;

        DateTime stamp = File.GetLastWriteTimeUtc(source);
        if (!first && stamp == _stamp)
            return;

        TomlTable root;
        try
        {
            root = Tomlyn.TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(source))!;
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastError)
            {
                _lastError = ex.Message;
                Log.Info(FileName + " could not be read, keeping the current settings: " + ex.Message);
            }

            return;
        }

        _lastError = "";
        _stamp = stamp;
        Take(root, first);

        if (migrate)
            MigrateFromManifest(root);
        else if (!first)
            Log.Info("Settings reloaded from " + FileName + ".");
    }

    private static void Take(TomlTable root, bool first)
    {
        TomlTable general = Section(root, "general");
        MenuKey = System.Enum.TryParse(Text(general, "menu_key", "F3"), true, out Keys menuKey) ? menuKey : Keys.F3;
        Log.Diagnostics = general.TryGetValue("diagnostics", out object? diag) && diag is bool on && on;
        PadOpenButton = Text(Section(root, "gamepad"), "open_button", "back");
        Remember = !general.TryGetValue("remember_cheats", out object? keep) || keep is not bool remember || remember;

        // The favourites radial replaced the PINNED page: a settings file from before it starts the radial with the pins.
        if (general.TryGetValue("radial", out object? radial) && radial is TomlArray slots)
            Favourites.Load(slots);
        else if (general.TryGetValue("pinned", out object? pins) && pins is TomlArray pinned)
        {
            Favourites.FromPins(pinned.OfType<string>());
            if (Favourites.Count > 0)
            {
                Log.Info("Moved " + Favourites.Count + " pinned rows onto the favourites radial.");
                MarkDirty();
            }
        }
        else
            Favourites.Load(null);

        foreach (Feature f in Catalog.All)
        {
            if (f is Command)
                continue;

            bool wasActive = f.Active;
            string before = f.State;

            // With remembering off, a fresh start ignores saved cheats. Menu settings always load.
            if (first && !Remember && !f.IsSetting)
                f.ResetValue();
            else
                f.Read(Section(root, f.Section));

            if (first)
            {
                if (f.IsSetting)
                    f.Apply();
            }
            else if (f.State != before && (wasActive || f.Active || f.IsSetting))
                Catalog.ApplyExternal(f);
        }

        Hotkeys.Clear();
        foreach (KeyValuePair<string, object> kv in Section(root, "hotkeys"))
        {
            if (kv.Value is not string name || name.Length == 0 || !Catalog.ById.ContainsKey(kv.Key) || !System.Enum.TryParse(name, true, out Keys key))
                continue;

            if (Menu.IsReserved(key))
                Log.Info("Hotkey " + kv.Key + " ignored: the menu uses " + key + ".");
            else
                Hotkeys[kv.Key] = key;
        }

        RebuildKeys();
    }

    /// <summary>Oracle 2.x kept settings inside mod.toml. Moves them out, once, and leaves a clean manifest.</summary>
    private static void MigrateFromManifest(TomlTable root)
    {
        _dirty = true;
        Flush();
        if (!root.ContainsKey("cheats"))
            return;

        try
        {
            var kept = new List<string>();
            foreach (string line in File.ReadAllLines(ManifestPath))
            {
                if (Array.Exists(OwnSections, s => line.Trim() == "[" + s + "]"))
                    break;
                kept.Add(line);
            }

            while (kept.Count > 0 && kept[^1].Trim().Length == 0)
                kept.RemoveAt(kept.Count - 1);

            File.WriteAllLines(ManifestPath, kept);
            Log.Info("Moved your settings from mod.toml to " + FileName + ".");
        }
        catch (Exception ex)
        {
            Log.Info("Settings copied to " + FileName + ", but mod.toml could not be tidied: " + ex.Message);
        }
    }

    private static void RebuildKeys()
    {
        _byKey.Clear();
        foreach (KeyValuePair<string, Keys> kv in Hotkeys)
        {
            if (!Catalog.ById.TryGetValue(kv.Key, out Feature? f))
                continue;
            if (!_byKey.TryGetValue(kv.Value, out List<Feature>? list))
                _byKey[kv.Value] = list = new List<Feature>();
            list.Add(f);
        }

        // Menu order decides which shared command is tried first.
        foreach (List<Feature> list in _byKey.Values)
            list.Sort((a, b) => Catalog.All.IndexOf(a).CompareTo(Catalog.All.IndexOf(b)));
    }

    private static string Compose()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Oracle settings. The menu keeps this file up to date, and edits made here apply while you play.");
        sb.AppendLine();
        sb.AppendLine("[general]");
        sb.AppendLine("# key that opens and closes the menu");
        sb.AppendLine("menu_key = \"" + MenuKey + "\"");
        sb.AppendLine("# true = writes every console command to Oracle.log, for bug reports");
        sb.AppendLine("diagnostics = " + (Log.Diagnostics ? "true" : "false"));
        sb.AppendLine("# the favourites radial, slot by slot and page by page; \"\" is an empty slot. Easiest to arrange in the menu:");
        sb.AppendLine("# HOME on any row, or SYSTEM > RADIAL > ARRANGE RADIAL.");
        sb.AppendLine("radial = " + Favourites.Toml());
        WriteValues(sb, "general");
        sb.AppendLine();
        sb.AppendLine("[gamepad]");
        sb.AppendLine("# hold this to open the menu: back, start, a, b, x, y, lb, rb, ls, rs, up, down, left, right. Combine with +, like \"lb+rb\"");
        sb.AppendLine("open_button = \"" + PadOpenButton + "\"");
        WriteValues(sb, "gamepad");
        sb.AppendLine();
        sb.AppendLine("[hotkeys]");
        sb.AppendLine("# feature_id = \"KeyName\". Toggles flip, sliders jump to a preset, choices cycle, commands run.");
        sb.AppendLine("# Easiest way to bind: press INSERT on a row in the menu.");
        foreach (KeyValuePair<string, Keys> kv in Hotkeys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(kv.Key + " = \"" + kv.Value + "\"");
        sb.AppendLine();
        sb.AppendLine("[cheats]");
        sb.AppendLine("# true/false for switches, numbers for sliders, names for choices.");
        WriteValues(sb, "cheats");
        return sb.ToString();
    }

    private static void WriteValues(StringBuilder sb, string section)
    {
        foreach (Feature f in Catalog.All)
        {
            if (f.Section == section && f.Toml is { } value)
                sb.AppendLine(f.Key + " = " + value);
        }
    }

    private static string Text(TomlTable table, string key, string fallback) =>
        table.TryGetValue(key, out object? value) && value is string text ? text : fallback;

    private static TomlTable Section(TomlTable root, string name) =>
        root.TryGetValue(name, out object? raw) && raw is TomlTable table ? table : new TomlTable();
}
