using System.Globalization;
using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>The BATMOBILE page. Handling and stunts are in BatmobileDrive.cs, weapon extras in BatmobileArsenal.cs.</summary>
internal sealed partial class Batmobile : Module
{
    public static readonly Batmobile I = new();

    private const double RefreshSeconds = 1.0;

    // Unreal units are centimetres.
    private const float UnitsToMph = 0.022369363f;
    private const float UnitsToKmh = 0.036f;
    private const float GaugeMaxMph = 140f;

    private sealed class BoostOriginals
    {
        public readonly RVehicleBatmobileBase Vehicle;
        public readonly bool Infinite;
        public RVehicleSimBatmobile? Sim;
        public bool SimInfinite;

        public BoostOriginals(RVehicleBatmobileBase vehicle)
        {
            Vehicle = vehicle;
            Infinite = vehicle.InfiniteBoost;
        }
    }

    private readonly List<RVehicleWeapon> _weapons = new();
    private BoostOriginals? _boost;
    private RVehicleBatmobileBase? _car;
    private RBatmobileCannon? _cannon;
    private Every _refresh;
    private int _maxHealth;
    private Toggle _speedometer = null!;
    private Toggle _stats = null!;
    private Choice _units = null!;

    private readonly Held<RPlayerController, bool> _radar = new("vehicle radar", pc => pc.bAlwaysShowVehicleRadar, (pc, v) => pc.bAlwaysShowVehicleRadar = v);

    // DRIVING STATS, for this session: best speed, and the longest time and distance off the ground.
    private const float MinJumpSeconds = 0.4f;
    private float _bestSpeed;
    private float _bestAir;
    private float _bestAirDistance;
    private float _airFor = -1f;
    private Vector3 _airFrom;

    public override bool HasHud => _speedometer.On && World.Pawn is RVehicleBatmobileBase;

    public override void Build()
    {
        BuildDrive();
        BuildArsenal();
        BuildAutopilot();
        BuildRemote();
        BuildAutoCombat();
        BuildBattle();

        AddToggle("batmobile_invincible", "INVINCIBLE BATMOBILE", "The Batmobile's armour stays full, so it can't be destroyed.").EveryTick(ArmourTick);

        AddToggle("infinite_power", "INFINITE MISSILE POWER", "The weapon generator stays full, so the missile barrage is always ready.").EveryTick(PowerTick);
        AddToggle("no_cooldowns", "NO WEAPON COOLDOWNS", "Missiles, EMP, 60mm cannon and the other special weapons are ready again straight after firing.").EveryTick(CooldownTick);
        AddToggle("perfect_aim", "PERFECT ACCURACY", "The Vulcan gun never loses accuracy while firing.").EveryTick(AccuracyTick);

        AddToggle("infinite_vehicle_boost", "UNLIMITED VEHICLE BOOST", "The Batmobile's boost does not run out or need to recharge.", on => { if (!on) RestoreBoost(); }).EveryTick(BoostTick);

        _speedometer = AddToggle("batmobile_speedometer", "SPEEDOMETER", "Shows the Batmobile's current speed while you are driving.");
        _units = AddChoice("batmobile_speed_units", "SPEEDOMETER UNITS", "Chooses the unit shown on the Batmobile speedometer.", new[] { "mph", "kmh" }, new[] { "MPH", "KM/H" }).Pref();
        _stats = AddToggle("batmobile_trip_stats", "DRIVING STATS", "Adds your best speed and longest jump of this session to the speedometer.").Pref();
        AddCommand("batmobile_trip_reset", "RESET DRIVING STATS", "Starts the best speed and longest jump on the speedometer again from zero.", ResetStats);
        AddToggle("vehicle_radar_on_foot", "VEHICLE RADAR ON FOOT", "The minimap keeps showing militia drones and tanks while you are on foot, not only the heavy tanks.",
            on => { if (!on) _radar.Restore(); }).Maintain(() => { if (World.Pc is { } pc) _radar.Set(pc, true); });

        AddCommand("rescue_batmobile", "RESCUE BATMOBILE", "Puts a stuck or flipped Batmobile back on its wheels.", () => World.RequireCheats().RescueBatmobile());
        AddCommand("force_exit_batmobile", "FORCE EXIT BATMOBILE", "Gets you out of the Batmobile when the game won't let you, such as a mission insisting you deliver a prisoner first.", ForceExit);
    }

    public override void WorldChanged()
    {
        StopAutopilot(null);
        RestoreBoost();
        RestoreDrive();
        RestoreMissileCap();
        RestoreRemoteAndPursuit();
        _radar.Restore();
        _battleForcedCar = 0;
        _airFor = -1f;
        _foes.Clear();
        _barrage.Clear();
        Forget();
    }

    public override void Shutdown()
    {
        LeaveForcedBattle("unloaded");
        StopAutopilot(null);
        RestoreBoost();
        RestoreDrive();
        RestoreGrip();
        RestoreMissileCap();
        RestoreRemoteAndPursuit();
        _radar.Restore();
    }

    private void RestoreRemoteAndPursuit()
    {
        _remoteKeeps.Restore();
        _lockFor.Restore();
        _civilianLockFor.Restore();
        _angleShare.Restore();
        _fullAngle.Restore();
        _scaledMissiles.Clear();
    }

    public override void Tick(float dt)
    {
        AutopilotTick(dt);
        DriveTick(dt);
        BattleDriveTick(dt);
        StatsTick(dt);
    }

    private void StatsTick(float dt)
    {
        if (World.Pawn is not RVehicleBatmobileBase car)
        {
            _airFor = -1f;
            return;
        }

        _bestSpeed = MathF.Max(_bestSpeed, car.Velocity.Length());
        if (!car.HasWheelsOnGround())
        {
            if (_airFor < 0f)
            {
                _airFor = 0f;
                _airFrom = car.Location;
            }

            _airFor += dt;
            return;
        }

        if (_airFor >= MinJumpSeconds && _airFor > _bestAir)
        {
            _bestAir = _airFor;
            _bestAirDistance = Geo.DistXY(_airFrom, car.Location);
        }

        _airFor = -1f;
    }

    private void ResetStats()
    {
        _bestSpeed = 0f;
        _bestAir = 0f;
        _bestAirDistance = 0f;
        _airFor = -1f;
    }

    /// <summary>Vehicle.DriverLeave with bForceLeave skips the game's CanLeaveVehicle check, which mission script can hold shut.</summary>
    private static void ForceExit()
    {
        if (World.Pawn is not RVehicleBatmobileBase car)
            throw new InvalidOperationException("you are not driving the Batmobile");

        car.bPreventBatmbileExit = false;
        if (World.Pc is { } pc)
            pc.bPreventBatmbileExit = false;

        bool left = car.DriverLeave(true, false);
        Log.Info("Force exit Batmobile: DriverLeave returned " + left + ", now controlling " + (World.Pawn?.GetPathName(null) ?? "nothing") + ".");
        if (!left)
            throw new InvalidOperationException("the game refused even a forced exit");
    }

    public void Forget()
    {
        _car = null;
        _winchMeshDone = 0;
        _weapons.Clear();
        _cannon = null;
        _refresh.Force();
        _maxHealth = 0;
    }

    /// <summary>The player's Batmobile. Hits and misses are both cached, so having none doesn't scan every call.</summary>
    public RVehicleBatmobileBase? Car()
    {
        bool due = _refresh.Elapsed(RefreshSeconds);
        if (World.Pawn is RVehicleBatmobileBase driving)
        {
            if (_car is not { IsValid: true } || _car.Ptr != driving.Ptr)
                Track(driving);
            else if (due && WeaponsStale())
                FindWeapons(driving);
            return _car;
        }

        bool usable = _car == null || (_car.IsValid && !_car.bDeleteMe);
        if (!due && usable)
            return _car;

        RVehicleBatmobileBase? found = LocateParked();
        if (found == null || _car is not { IsValid: true } || found.Ptr != _car.Ptr)
            Track(found);
        else if (WeaponsStale())
            FindWeapons(found);

        return _car;
    }

    private static RVehicleBatmobileBase? LocateParked()
    {
        RVehicleBatmobileBase? known = World.Info?.GetBatmobile();
        if (known is { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false })
            return known;

        foreach (RVehicleBatmobileBase b in World.All<RVehicleBatmobileBase>())
        {
            if (!b.bDeleteMe && b.GetPathName(null).Contains("PersistentLevel", StringComparison.Ordinal))
                return b;
        }

        return null;
    }

    private void Track(RVehicleBatmobileBase? found)
    {
        if (found == null || _car is not { IsValid: true } || found.Ptr != _car.Ptr)
            _maxHealth = 0;
        _car = found;
        FindWeapons(found);
    }

    private bool WeaponsStale()
    {
        if (_weapons.Count == 0)
            return true;

        foreach (RVehicleWeapon w in _weapons)
        {
            if (!w.IsValid)
                return true;
        }

        return false;
    }

    private void FindWeapons(RVehicleBatmobileBase? owner)
    {
        _weapons.Clear();
        _cannon = null;
        if (owner == null)
            return;

        foreach (RVehicleWeapon w in World.All<RVehicleWeapon>())
        {
            if (w.Owner == null || w.Owner.Ptr != owner.Ptr)
                continue;

            _weapons.Add(w);
            if (w is RBatmobileCannon c)
                _cannon = c;
        }
    }

    private static int ArmourBonus(RVehicleBatmobileBase b)
    {
        var armour = b.ArmourLevels;
        return armour.Count == 0 ? 0 : armour[Math.Clamp(b.CurrentArmourLevel, 0, armour.Count - 1)];
    }

    private static float MaxPower(RVehicleBatmobileBase b)
    {
        var levels = b.MaxWeaponGeneratorPower;
        return levels.Count == 0 ? 0f : levels[Math.Clamp(b.CurrentGeneratorLevel, 0, levels.Count - 1)];
    }

    private void ArmourTick()
    {
        RVehicleBatmobileBase? b = Car();
        if (b == null || b.Health <= 0)
            return;

        _maxHealth = Math.Max(_maxHealth, Math.Max(b.Health, b.HealthMax + ArmourBonus(b)));
        if (b.Health < _maxHealth)
            b.Health = _maxHealth;
    }

    private void PowerTick()
    {
        RVehicleBatmobileBase? b = Car();
        if (b == null)
            return;

        float max = MaxPower(b);
        if (max > 0f && b.WeaponGeneratorPower < max)
            b.WeaponGeneratorPower = max;
    }

    private void CooldownTick()
    {
        if (Car() == null)
            return;

        foreach (RVehicleWeapon w in _weapons)
        {
            if (!w.IsValid || w is RBatmobileCannon)
                continue;
            if (w.CoolDown > 0f)
                w.CoolDown = 0f;
            if (w.CurrentRechargeTime > 0f)
                w.CurrentRechargeTime = 0f;
        }
    }

    private void AccuracyTick()
    {
        if (Car() == null || _cannon is not { IsValid: true } c)
            return;

        if (c.CurrentInaccuracyTime > 0f)
            c.CurrentInaccuracyTime = 0f;
        if (c.CurrentAccuracyZeroToOne < 1f)
            c.CurrentAccuracyZeroToOne = 1f;
        if (!c.bPerfectShot)
            c.bPerfectShot = true;
    }

    private void BoostTick()
    {
        RVehicleBatmobileBase? vehicle = Car();
        if (vehicle == null)
        {
            RestoreBoost();
            return;
        }

        if (_boost == null || !_boost.Vehicle.IsValid || _boost.Vehicle.Ptr != vehicle.Ptr)
        {
            RestoreBoost();
            _boost = new BoostOriginals(vehicle);
        }

        RVehicleSimBatmobile? sim = vehicle.SimObj is RVehicleSimBatmobile { IsValid: true } found ? found : null;
        if (_boost.Sim != null && (sim == null || !_boost.Sim.IsValid || _boost.Sim.Ptr != sim.Ptr))
        {
            if (_boost.Sim.IsValid)
                _boost.Sim.bInfiniteBoost = _boost.SimInfinite;
            _boost.Sim = null;
        }

        if (sim != null && _boost.Sim == null)
        {
            _boost.Sim = sim;
            _boost.SimInfinite = sim.bInfiniteBoost;
        }

        if (!vehicle.InfiniteBoost)
            vehicle.InfiniteBoost = true;
        if (_boost.Sim is { IsValid: true } active && !active.bInfiniteBoost)
            active.bInfiniteBoost = true;
    }

    private void RestoreBoost()
    {
        if (_boost is not { } saved)
            return;

        _boost = null;
        Safe.Run("boost restore", () =>
        {
            if (saved.Vehicle.IsValid)
                saved.Vehicle.InfiniteBoost = saved.Infinite;
            if (saved.Sim is { IsValid: true } sim)
                sim.bInfiniteBoost = saved.SimInfinite;
        });
    }

    public override void Draw(Canvas c)
    {
        if (World.Pawn is not RVehicleBatmobileBase vehicle)
            return;

        float speed = vehicle.Velocity.Length();
        float mph = speed * UnitsToMph;
        string value = Speed(speed);
        bool boosting = Safe.Try("boost state", vehicle.IsBoosting);

        const float width = 196f;
        float height = _stats.On ? 118f : 100f;
        float x = Gfx.W - Gfx.S(width) - Gfx.S(28f);
        float y = Gfx.H - Gfx.S(height) - Gfx.S(48f);
        Gfx.Panel(x, y, Gfx.S(width), Gfx.S(height));

        float cx = x + Gfx.S(width * 0.5f);
        string title = Piloting ? AutopilotTitle(vehicle) : boosting ? "BOOST" : "BATMOBILE";
        Gfx.TextCentre(cx, y + Gfx.S(9f), title, Piloting || boosting ? Theme.Accent : Theme.Mid, 0.66f);
        Gfx.TextCentre(cx, y + Gfx.S(28f), value, Theme.Hi, 1.15f);

        float barX = x + Gfx.S(14f);
        float barW = Gfx.S(width - 28f);
        Bar(barX, y + Gfx.S(60f), barW, Gfx.S(3f), mph / GaugeMaxMph, null);
        float maxHealth = MathF.Max(_maxHealth, vehicle.HealthMax + ArmourBonus(vehicle));
        float maxPower = MaxPower(vehicle);
        Bar(barX, y + Gfx.S(72f), barW, Gfx.S(5f), maxHealth > 0f ? vehicle.Health / maxHealth : 0f, "HP");
        Bar(barX, y + Gfx.S(86f), barW, Gfx.S(5f), maxPower > 0f ? vehicle.WeaponGeneratorPower / maxPower : 0f, "PWR");

        if (_stats.On)
        {
            string jump = _bestAir <= 0f ? "-" : _bestAir.ToString("0.0", CultureInfo.InvariantCulture) + " S " + MathF.Round(_bestAirDistance / 100f).ToString(CultureInfo.InvariantCulture) + " M";
            Gfx.TextCentre(cx, y + Gfx.S(100f), "BEST " + Speed(_bestSpeed) + "   JUMP " + jump, Theme.Mid, 0.56f);
        }
    }

    /// <summary>A speed in units per second, in the chosen speedometer unit.</summary>
    private string Speed(float unitsPerSecond)
    {
        bool kmh = _units.Index == 1;
        return MathF.Round(unitsPerSecond * (kmh ? UnitsToKmh : UnitsToMph)).ToString(CultureInfo.InvariantCulture) + (kmh ? " KM/H" : " MPH");
    }

    private static void Bar(float x, float y, float w, float h, float fraction, string? label)
    {
        if (label != null)
        {
            Gfx.TextMid(x, y + h * 0.5f, label, Theme.Mid, 0.56f);
            x += Gfx.S(30f);
            w -= Gfx.S(30f);
        }

        Gfx.Rect(x, y, w, h, Theme.Mid, 0.25f);
        fraction = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        Gfx.Rect(x, y, w * fraction, h, Theme.Accent, 0.95f);
    }
}
