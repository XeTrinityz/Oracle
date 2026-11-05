using System.Globalization;

namespace CeZeY.Oracle;

internal readonly record struct ConsoleProp(string Cls, string Name, string On, string Off);

internal sealed class MenuRow
{
    public string? Header;
    public Feature? Item;

    /// <summary>Where the row lives, like "PLAYER / GADGETS". Shown on pages that gather rows from elsewhere.</summary>
    public string? Path;
}

internal sealed class MenuPage
{
    public string Title = "";
    public readonly List<MenuRow> Rows = new();
    public readonly List<MenuPage> Children = new();

    public bool HasSub => Children.Count > 0;

    public int ActiveCount
    {
        get
        {
            int n = 0;
            foreach (MenuRow r in Rows)
            {
                if (r.Item is { Active: true })
                    n++;
            }

            foreach (MenuPage child in Children)
                n += child.ActiveCount;

            return n;
        }
    }
}

/// <summary>
/// The registry of every feature, the builder modules register them with, and the engine that switches them
/// on after a load and keeps them applied.
/// </summary>
internal static class Catalog
{
    private const float MaintainGap = 0.25f;

    public static readonly List<Feature> All = new();
    public static readonly Dictionary<string, Feature> ById = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<MenuPage> Pages = new();

    private static readonly List<Feature> _ticking = new();
    private static readonly List<Feature> _maintained = new();
    private static readonly List<ConsoleProp> _consoleProps = new();
    private static readonly Queue<Feature> _applyQueue = new();

    private static bool _applied;
    private static float _applyLeft;
    private static float _maintainLeft;
    private static nint _lastPawn;

    /// <summary>True once saved cheats have been switched back on in this world.</summary>
    public static bool Ready => _applied && World.InGame;

    public static int ActiveCount
    {
        get
        {
            int n = 0;
            foreach (Feature f in All)
            {
                if (f.Active)
                    n++;
            }

            return n;
        }
    }

    public static void Clear()
    {
        All.Clear();
        ById.Clear();
        Pages.Clear();
        _ticking.Clear();
        _maintained.Clear();
        _consoleProps.Clear();
        _applyQueue.Clear();
    }

    // ---- building


    private static T Add<T>(T f, string id, string label, string help) where T : Feature
    {
        f.Id = id;
        f.Key = id;
        f.Label = label;
        f.Help = help;

        All.Add(f);
        ById[id] = f;
        return f;
    }

    public static Toggle AddToggle(string id, string label, string help, Action<bool>? changed = null, bool on = false) =>
        Add(new Toggle { On = on, Default = on, Changed = changed }, id, label, help);

    public static Slider AddSlider(string id, string label, string help, float def, float min, float max, float step, float hot, Func<float, string> format, Action<float>? changed = null) =>
        Add(new Slider { Value = def, Default = def, Min = min, Max = max, Stride = step, HotValue = hot, Format = format, Changed = changed }, id, label, help);

    public static Choice AddChoice(string id, string label, string help, string[] keys, string[] labels, Action<int>? changed = null) =>
        Add(new Choice { Keys = keys, Labels = labels, Saved = keys.Length == 0 ? "" : keys[0], Changed = changed }, id, label, help);

    public static Command AddCommand(string id, string label, string help, Action run) =>
        Add(new Command { Run = run }, id, label, help);

    /// <summary>A save-changing command: always confirmed, never bound to a key.</summary>
    public static Command AddSaveCommand(string id, string label, string help, Action run)
    {
        Command c = AddCommand(id, label, help, run);
        c.ChangesSave = true;
        return c;
    }

    /// <summary>A toggle that flips config properties through the console.</summary>
    public static Toggle AddConsoleToggle(string id, string label, string help, params ConsoleProp[] props)
    {
        _consoleProps.AddRange(props);
        return AddToggle(id, label, help, on =>
        {
            foreach (ConsoleProp p in props)
            {
                if (on)
                    Cvars.Override(p.Cls, p.Name, p.On);
                else
                    Cvars.Restore(p.Cls, p.Name, p.Off);
            }
        });
    }

    /// <summary>A slider that writes one config property through the console.</summary>
    public static Slider AddConsoleSlider(string id, string label, string help, string cls, string prop, float def, float min, float max, float step, float hot, Func<float, string> format)
    {
        string off = def.ToString("0.0#####", CultureInfo.InvariantCulture);
        _consoleProps.Add(new ConsoleProp(cls, prop, "", off));
        return AddSlider(id, label, help, def, min, max, step, hot, format, v =>
        {
            if (MathF.Abs(v - def) < 0.0001f)
                Cvars.Restore(cls, prop, off);
            else
                Cvars.Override(cls, prop, v.ToString("0.0#####", CultureInfo.InvariantCulture));
        });
    }

    /// <summary>Call after every module has built, so the update lists hold exactly the features that need them.</summary>
    public static void Seal()
    {
        _ticking.Clear();
        _maintained.Clear();
        foreach (Feature f in All)
        {
            if (f.TickStep != null)
                _ticking.Add(f);
            if (f.MaintainStep != null)
                _maintained.Add(f);
        }
    }

    // ---- running

    /// <summary>Starts the wait before saved cheats switch back on. Called after any load.</summary>
    public static void Arm()
    {
        _applied = false;
        _applyLeft = Settings.ApplyDelay;
        _applyQueue.Clear();
        _lastPawn = 0;
    }

    /// <summary>Advances the startup queue and runs per-tick features. Returns false until the world has settled.</summary>
    public static bool Tick(float dt)
    {
        if (!_applied)
        {
            _applyLeft -= dt;
            if (_applyLeft > 0f)
                return false;

            _applied = true;
            foreach (Feature f in All)
            {
                if (f.Active)
                    _applyQueue.Enqueue(f);
            }

            _lastPawn = World.PawnPtr;
            if (_applyQueue.Count > 0)
                Log.Info("Switching " + _applyQueue.Count + " saved cheats back on.");
            return false;
        }

        // One per tick: console commands are slow enough that a whole batch shows up as a hitch after loading.
        if (_applyQueue.TryDequeue(out Feature? next) && next.Active)
            ApplyLogged(next, "load");

        foreach (Feature f in _ticking)
        {
            if (f.Active)
                Guarded(f, f.TickStep!, "tick");
        }

        return true;
    }

    public static void Maintain(float dt)
    {
        nint pawn = World.PawnPtr;
        bool newPawn = pawn != _lastPawn;
        _lastPawn = pawn;

        _maintainLeft -= dt;
        if (_maintainLeft > 0f && !newPawn)
            return;

        _maintainLeft = MaintainGap;
        foreach (Feature f in _maintained)
        {
            if (f.Active)
                Guarded(f, f.MaintainStep!, "maintain");
        }
    }

    /// <summary>Records a user change: applies it, or leaves it for the next load, and schedules a save.</summary>
    public static void Commit(Feature f, string reason = "menu")
    {
        if (f.IsSetting)
            Guarded(f, f.Apply, "apply");
        else if (Ready)
            ApplyLogged(f, reason);
        else
            Log.Info(f.Label + " -> " + f.State + " (waits for the game)");

        Settings.MarkDirty();
    }

    /// <summary>Applies a value that changed in settings.toml while the game is running.</summary>
    public static void ApplyExternal(Feature f)
    {
        if (f.IsSetting)
            Guarded(f, f.Apply, "apply");
        else if (Ready)
            ApplyLogged(f, "settings.toml");
    }

    /// <summary>Runs a command. Returns false, without throwing, if it failed.</summary>
    public static bool Run(Command c)
    {
        if (!c.IsSetting && !World.InGame)
        {
            Log.Info(c.Label + " skipped: not in game.");
            return false;
        }

        try
        {
            c.Run?.Invoke();
            Log.Info(c.Label + " done.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info(c.Label + " failed: " + ex.Message);
            // The menu's FAILED flash can't say why, so put the reason on screen too.
            Overlay.Notice(c.Label, ex.Message.ToUpperInvariant(), false);
            return false;
        }
    }

    /// <summary>
    /// A key press for everything on that key. Toggles, sliders and choices all change, and every command runs in
    /// menu order. Commands that move the player are the exception: only one move can happen, so they are tried
    /// until one works. TELEPORT TO WAYPOINT and TELEPORT TO OBJECTIVE on one key go to the waypoint when there is
    /// one and the objective otherwise; only the last move's failure shows.
    /// </summary>
    public static void Hotkey(IReadOnlyList<Feature> features)
    {
        if (features.Count == 1)
        {
            Hotkey(features[0]);
            return;
        }

        var commands = new List<Command>();
        foreach (Feature f in features)
        {
            if (!f.Bindable)
                continue;
            if (f is Command c)
                commands.Add(c);
            else
                Hotkey(f);
        }

        if (commands.Count == 0)
            return;

        int lastMove = commands.FindLastIndex(c => c.MovesPlayer);
        bool moved = false;
        Command? firstDone = null;
        Command? failed = null;
        int done = 0;

        for (int i = 0; i < commands.Count; i++)
        {
            Command c = commands[i];
            if (c.MovesPlayer && moved)
                continue;

            // A move with another move after it may fail quietly: the next one is the fallback.
            bool fallback = c.MovesPlayer && i < lastMove;
            Overlay.Muted = fallback;
            bool ok;
            try
            {
                ok = Run(c);
            }
            finally
            {
                Overlay.Muted = false;
            }

            if (ok)
            {
                done++;
                firstDone ??= c;
                moved |= c.MovesPlayer;
            }
            else if (!fallback)
            {
                failed = c;
            }
        }

        if (failed != null)
            Overlay.Toast(failed.Label, "FAILED", false);
        else if (firstDone != null)
            Overlay.Toast(done == 1 ? firstDone.Label : firstDone.Label + " +" + (done - 1), "DONE", true);
    }

    public static void Hotkey(Feature f)
    {
        if (!f.Bindable)
            return;

        if (f is Command c)
        {
            bool ok = Run(c);
            Overlay.Toast(f.Label, ok ? "DONE" : "FAILED", ok);
            return;
        }

        f.Hotkey();
        Commit(f, "hotkey");
        Overlay.Toast(f.Label, f.State, !f.IsDefault);
    }

    public static void TurnAllOff()
    {
        foreach (Feature f in All)
        {
            if (f.Active)
            {
                f.ResetValue();
                Commit(f);
            }
        }
    }

    /// <summary>
    /// Puts every console-driven value back. They live on class defaults, so unlike edits to live objects
    /// they would otherwise outlast the mod.
    /// </summary>
    public static void RestoreConsoleProps()
    {
        foreach (ConsoleProp p in _consoleProps)
            Safe.Run("restore " + p.Name, () => Cvars.RestoreIfChanged(p.Cls, p.Name));
    }

    private static void ApplyLogged(Feature f, string reason)
    {
        Guarded(f, f.Apply, "apply");
        Log.Info(f.Label + " -> " + f.State + " (" + reason + ")");
    }

    private static void Guarded(Feature f, Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Throttled(f.Id + what, f.Label + " " + what + " failed: " + ex.Message);
        }
    }
}
