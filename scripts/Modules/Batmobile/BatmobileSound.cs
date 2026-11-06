using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Gives the Batmobile another skin's engine and tyre sounds. Every Batmobile's sound player (NewVehicleSound)
/// runs from one AkPlayerVehicleSoundInfo: engine loops, gears, boost, drift, squeal and skid events plus the
/// RTPC names that drive them. The car hands its archetype's info to Initialize in PostBeginPlay; Oracle loads
/// the donor skin's package, keeps only that info alive (its events hold their Wwise bank), and re-initialises
/// the live sound player with it. Archetypes are never changed, so a skin change or a reload starts clean.
/// </summary>
internal sealed class BatmobileSound : Module
{
    public static readonly BatmobileSound I = new();

    // How long to wait for a freshly loaded bank before swapping anyway.
    private const double BankWait = 6.0;

    private readonly record struct SoundSet(string Key, string Label, string Package, string InfoPath);

    // One entry per distinct engine bank in the skin packages (reskins share these, so they aren't listed).
    // Burton and Asylum keep the base tyre events; 1966, Tumbler and Snyder have their own squeal and skids.
    private static readonly SoundSet[] Sets =
    {
        new("ak", "ARKHAM KNIGHT", "Playable_Batmobile_SF", "VEH_Player.Batmobile"),
        new("mk2", "ARKHAM KNIGHT MK2", "Playable_BatmobileMK2_SF", "VEH_Player.Batmobile_MK2"),
        new("classic", "1966 TV SERIES", "Playable_BatmobileClassic_SF", "VEH_Batmobile_Classic.Batmobile_Classic"),
        new("burton", "1989 MOVIE", "Playable_BatmobileBurton_SF", "VEH_Batmobile_Burton.Batmobile_Burton"),
        new("tumbler", "TUMBLER", "Playable_BatmobileTumbler_SF", "VEH_Batmobile_Tumbler.Batmobile_Tumbler"),
        new("snyder", "BATMAN V SUPERMAN", "Playable_BatmobileSnyder_SF", "VEH_Batmobile_Snyder.Batmobile_Snyder"),
        new("asylum", "ARKHAM ASYLUM", "Playable_BatmobileAsylum_SF", "VEH_Batmobile_Asylum.Batmobile_Asylum"),
    };

    private readonly Dictionary<string, AkPlayerVehicleSoundInfo> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private Choice _sound = null!;

    // The car whose sound player Oracle changed, and the info it had before.
    private RVehicleBatmobileBase? _car;
    private AkVehicleSoundInfo? _original;
    private string _appliedKey = "";
    private string _waitingKey = "";
    private double _waitingSince;

    public override void Build()
    {
        var keys = new List<string> { "" };
        var labels = new List<string> { "SKIN'S OWN" };
        foreach (SoundSet set in Sets)
        {
            if (!Installed(set.Package))
                continue;
            keys.Add(set.Key);
            labels.Add(set.Label);
        }

        _sound = AddChoice("batmobile_engine_sound", "ENGINE SOUND",
            "Gives the Batmobile another skin's engine, boost and tyre sounds, whatever skin it wears. The first use of each loads that skin's sounds, which can take a moment.",
            keys.ToArray(), labels.ToArray(), i => { if (i == 0) Restore(); else _failed.Clear(); })
            .Maintain(Keep);
    }

    public override void WorldChanged()
    {
        // The car is rebuilt with its archetype's own info, so there is nothing to put back.
        _car = null;
        _original = null;
        _appliedKey = "";
        _waitingKey = "";
    }

    public override void Shutdown()
    {
        Restore();
        foreach (AkPlayerVehicleSoundInfo info in _loaded.Values)
        {
            if (info.IsValid)
                Safe.Run("engine sound unroot", info.RemoveFromRoot);
        }

        _loaded.Clear();
    }

    private void Keep()
    {
        string key = _sound.CurrentKey;
        if (key.Length == 0 || Transition.Busy || _failed.Contains(key))
            return;

        RVehicleBatmobileBase? car = Batmobile.I.Car();
        if (car is not { IsValid: true } || car.bDeleteMe || car.NewVehicleSound is not { IsValid: true } player)
            return;

        if (_car is not { IsValid: true } || _car.Ptr != car.Ptr)
        {
            // A new car (reload, skin change, call-in after destruction) starts on its archetype's info.
            _car = car;
            _original = player.m_VehicleInfo;
            _appliedKey = "";
        }

        if (_appliedKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            return;

        AkPlayerVehicleSoundInfo? donor = Resolve(key);
        if (donor == null)
            return;

        if (!BankReady(donor, key))
            return;

        Swap(car, player, donor);
        _appliedKey = key;
        _waitingKey = "";
        Overlay.Notice("ENGINE SOUND", _sound.State, true);
    }

    private void Restore()
    {
        RVehicleBatmobileBase? car = _car;
        AkVehicleSoundInfo? original = _original;
        _car = null;
        _original = null;
        _appliedKey = "";
        _waitingKey = "";
        if (car is not { IsValid: true } || car.bDeleteMe || original is not { IsValid: true })
            return;

        Safe.Run("engine sound restore", () =>
        {
            if (car.NewVehicleSound is { IsValid: true } player && player.m_VehicleInfo?.Ptr != original.Ptr)
                Swap(car, player, original);
        });
    }

    /// <summary>The donor info, loading its skin package the first time. Null while unavailable.</summary>
    private AkPlayerVehicleSoundInfo? Resolve(string key)
    {
        if (_loaded.TryGetValue(key, out AkPlayerVehicleSoundInfo? known) && known.IsValid)
            return known;

        SoundSet set = Array.Find(Sets, s => s.Key == key);
        if (set.Key == null)
        {
            _failed.Add(key);
            return null;
        }

        AkPlayerVehicleSoundInfo? info = Find(set.InfoPath);
        if (info == null)
        {
            Log.Info("Engine sound: loading " + set.Package + " for " + set.Label + ".");
            Safe.Run("engine sound package " + set.Package, () => Game.LoadPackage(set.Package));
            info = Find(set.InfoPath);
        }

        if (info == null)
        {
            _failed.Add(key);
            Log.Info("Engine sound: " + set.InfoPath + " was not found after loading " + set.Package + ".");
            Overlay.Notice("ENGINE SOUND", "NOT AVAILABLE", false);
            return null;
        }

        // Rooted, the info and the events (and through them the bank) outlive the rest of the skin package.
        info.AddToRoot();
        _loaded[key] = info;
        foreach (AkEvent ev in EngineEvents(info))
            Safe.Run("engine sound bank", () => ev.RequestAssetBankLoad());

        _waitingKey = key;
        _waitingSince = Clock.Now;
        return info;
    }

    private static AkPlayerVehicleSoundInfo? Find(string path)
    {
        try
        {
            return Game.FindObject<AkPlayerVehicleSoundInfo>(path) is { IsValid: true } found ? found : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Waits a little for the donor's bank so the first engine start isn't silent.</summary>
    private bool BankReady(AkPlayerVehicleSoundInfo donor, string key)
    {
        bool loaded = true;
        foreach (AkEvent ev in EngineEvents(donor))
        {
            if (!Safe.Try("engine sound bank state", ev.IsAssetBankLoaded, true))
                loaded = false;
        }

        if (loaded)
            return true;

        if (_waitingKey != key)
        {
            _waitingKey = key;
            _waitingSince = Clock.Now;
        }

        if (Clock.Now - _waitingSince < BankWait)
            return false;

        Log.Info("Engine sound: the bank for " + key + " still reports unloaded; swapping anyway.");
        return true;
    }

    private static IEnumerable<AkEvent> EngineEvents(AkVehicleSoundInfo info)
    {
        foreach (AkEvent ev in info.StartLoopingEvents)
        {
            if (ev is { IsValid: true })
                yield return ev;
        }
    }

    /// <summary>Stops the running engine, re-initialises the sound player on the new info and starts it again.</summary>
    private static void Swap(RVehicleBatmobileBase car, AkVehicleSoundVar player, AkVehicleSoundInfo info)
    {
        bool running = player.EngineStarted();
        bool driving = World.Pawn is { } pawn && pawn.Ptr == car.Ptr;
        string from = player.m_VehicleInfo?.GetPathName(null) ?? "none";

        // Wheel squeal loops are keyed by wheel bone and outlive Initialize, so stop them while the old setup can
        // still find them; otherwise they keep screeching with nothing left to update or stop them.
        StopWheels(car);
        player.StopEngine(true);
        player.StopSockets();
        player.StopCreationEvents();

        if (info is AkPlayerVehicleSoundInfo playerInfo)
            car.PlayerVehicleInfo = playerInfo;
        player.Initialize(info);
        StopWheels(car);

        // Same order as the game: initialise in PostBeginPlay, then the player gets in and the engine starts.
        if (driving)
            player.PlayerEntered();
        if (driving || running)
            player.StartEngine();

        Log.Info("Engine sound: " + from + " -> " + info.GetPathName(null) + ".");
    }

    private static void StopWheels(RVehicleBatmobileBase car)
    {
        Safe.Run("engine sound stop squeal", () =>
        {
            foreach (SVehicleWheel wheel in car.Wheels)
            {
                if (wheel is { IsValid: true })
                    car.AudioStopWheelSqueal(wheel.BoneName);
            }
        });
        Safe.Run("engine sound stop wheels", car.AudioStopAllWheels);
        Safe.Run("engine sound squeal reset", () => car.AudioSquealing(0f));
    }

    private static bool Installed(string package)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null)
                return true;

            // BatmanAK.exe is in Binaries/Win64; skins live in BmGame/CookedPCConsole or a DLC folder.
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
            string file = package + ".upk";
            if (File.Exists(Path.Combine(root, "BmGame", "CookedPCConsole", file)))
                return true;

            string dlc = Path.Combine(root, "DLC");
            return Directory.Exists(dlc) && Directory.EnumerateFiles(dlc, file, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return true;
        }
    }
}
