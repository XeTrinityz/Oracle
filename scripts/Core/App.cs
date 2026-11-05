using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>The kernel: owns the module list and turns SDK callbacks into one ordered, isolated update.</summary>
internal static class App
{
    public const string Version = "3.0.0";
    private const float MaxStep = 0.25f;

    /// <summary>Update order, and through registration the order of rows within each menu page.</summary>
    public static readonly Module[] Modules =
    {
        Player.I, GlideMod.I, Combat.I, Gadgets.I, Npcs.I, Screen.I, Atmosphere.I, Cosmetics.I, Rain.I, JammedGuns.I, CameraRig.I, Batmobile.I,
        BatmobileSkin.I, CharacterSuit.I, Teleport.I, TeleportTools.I, TimeControl.I, Characters.I, Music.I, NewGamePlus.I, Progress.I, Riddler.I, SystemPage.I,
    };

    private static FrameTimer _tick;
    private static bool _started;
    private static bool _inWorld;

    public static string Dir { get; private set; } = "";

    public static void Start(string dir)
    {
        if (_started)
            return;
        _started = true;

        Dir = dir;
        Log.Open(dir);
        Catalog.Clear();
        Each("build", m => m.Build());
        Catalog.Seal();
        Layout.Apply();
        Settings.Load(first: true);
        Each("start", m => m.Started());
        Catalog.Arm();
        Log.Info("Loaded " + Catalog.All.Count + " features across " + Modules.Length + " modules, menu key " + Settings.MenuKey + ".");
    }

    public static void Stop()
    {
        if (!_started)
            return;
        _started = false;

        Menu.Close();
        Pad.Shutdown();
        Settings.Flush();
        Each("shutdown", m => m.Shutdown());
        Catalog.RestoreConsoleProps();
        World.Forget();
    }

    public static void EnterWorld()
    {
        Menu.Close();
        World.Forget();
        if (!Characters.I.Busy)
            Fader.Clear();
        Each("world change", m => m.WorldChanged());
        Catalog.Arm();
    }

    public static void EnterMenu()
    {
        Menu.Close();
        World.Forget();
    }

    public static void SdkTick()
    {
        Fallback.NoteSdkTick();
        Tick();
    }

    public static void SdkKey(Keys key)
    {
        if (_started && !Fallback.Active)
            KeyInput.Pressed(key);
    }

    /// <summary>One game update. Runs from OnTick, or from the render hook when the game stops ticking mods.</summary>
    public static void Tick()
    {
        if (!_started)
            return;

        float dt = _tick.Step(MaxStep);

        World.Refresh();
        Settings.Tick(dt);

        if (World.InGame)
        {
            _inWorld = true;
            if (!Characters.I.GuardLoadedCharacter() && Catalog.Tick(dt))
            {
                TickModules(dt);
                Catalog.Maintain(dt);
            }
        }
        else if (World.InFrontend && _inWorld)
        {
            _inWorld = false;
            Each("leave game", m => m.LeftGame());
            Catalog.Arm();
        }

        Pad.GameTick();
        Menu.Tick(dt);
    }

    /// <summary>One rendered frame. Runs inside the PostRender redirect, so nothing here may throw.</summary>
    public static void Frame(Canvas canvas)
    {
        if (!_started)
            return;

        World.Refresh();
        foreach (Module m in Modules)
        {
            try
            {
                m.Frame();
            }
            catch (Exception ex)
            {
                Log.Throttled("frame " + m.Name, m.Name + " frame failed: " + ex.Message);
            }
        }

        Safe.Run("controller", Pad.Frame);
        Safe.Run("fallback", Fallback.Frame);
        Safe.Run("overlay", () => Overlay.Draw(canvas));
    }

    private static void TickModules(float dt)
    {
        foreach (Module m in Modules)
        {
            try
            {
                m.Tick(dt);
            }
            catch (Exception ex)
            {
                Log.Throttled("tick " + m.Name, m.Name + " tick failed: " + ex.Message);
            }
        }
    }

    private static void Each(string what, Action<Module> call)
    {
        foreach (Module m in Modules)
        {
            try
            {
                call(m);
            }
            catch (Exception ex)
            {
                Log.Info(m.Name + " " + what + " failed: " + ex.Message);
            }
        }
    }
}
