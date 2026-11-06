using System.Globalization;
using System.Numerics;
using BmSDK;
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

    // VEHICLE RADAR ON FOOT. RPlayerController.PreRender (the on-foot HUD; driving runs DrivingMode.PreRender) only
    // runs the full radar when `BatmobilePawn != none && bAlwaysShowVehicleRadar`, and Possess sets BatmobilePawn to
    // the possessed pawn, so on foot it is always none and the flag alone does nothing. The radar block sits between
    // UpdatePlayerWaypoint and CurrentForensicsDevice.PreRender (each called only from there), so the parked car is lent
    // to BatmobilePawn after the first and taken back before the second: nothing else in that frame sees it.
    private readonly Held<RPlayerController, bool> _radar = new("vehicle radar", pc => pc.bAlwaysShowVehicleRadar, (pc, v) => pc.bAlwaysShowVehicleRadar = v);
    private Toggle _radarOnFoot = null!;
    private RPlayerController? _radarLentTo;

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
        _radarOnFoot = AddToggle("vehicle_radar_on_foot", "VEHICLE RADAR ON FOOT", "The minimap keeps showing militia drones and tanks while you are on foot, not only the heavy tanks.",
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
        ReturnRadarCar();
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
        ReturnRadarCar();
        _radar.Restore();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.UpdatePlayerWaypoint))]
    private static void UpdatePlayerWaypointRedirect(RPlayerController self)
    {
        self.UpdatePlayerWaypoint();
        Safe.Run("vehicle radar lend", () => I.LendRadarCar(self));
    }

    [Redirect(typeof(RForensicsDevice), nameof(RForensicsDevice.PreRender))]
    private static void ForensicsPreRenderRedirect(RForensicsDevice self)
    {
        Safe.Run("vehicle radar return", I.ReturnRadarCar);
        self.PreRender();
    }

    private void LendRadarCar(RPlayerController pc)
    {
        if (!_radarOnFoot.On || !pc.bAlwaysShowVehicleRadar || World.Pawn is RVehicleBatmobileBase || pc.BatmobilePawn is { IsValid: true })
            return;

        if (Car() is not { IsValid: true, bDeleteMe: false } car)
            return;

        pc.BatmobilePawn = car;
        _radarLentTo = pc;
    }

    private void ReturnRadarCar()
    {
        RPlayerController? pc = _radarLentTo;
        _radarLentTo = null;
        if (pc is { IsValid: true } && pc.Pawn is not RVehicleBatmobileBase)
            pc.BatmobilePawn = null;
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

    private const float DialR = 56f;

    /// <summary>
    /// The speedometer: a dial that sweeps with speed (orange while boosting), the number in its middle, and health
    /// and weapon power as meters beneath.
    /// </summary>
    public override void Draw(Canvas c)
    {
        if (World.Pawn is not RVehicleBatmobileBase vehicle)
            return;

        float speed = vehicle.Velocity.Length();
        float mph = speed * UnitsToMph;
        bool boosting = Safe.Try("boost state", vehicle.IsBoosting);

        float r = Hud.U(DialR);
        float meters = (Hud.CaptionH + Hud.U(4)) * 2f + Hud.U(8);
        float stats = _stats.On ? Hud.U(8) + Gfx.TextH(Hud.Sc(Hud.SmallScale)) : 0f;
        float body = r * 2f - Hud.U(8) + Hud.U(6) + meters + stats;
        float w = Hud.U(250);
        float h = Hud.Height(body);
        var (x, y, age) = Hud.Place("speedometer", Hud.Side.BottomRight, 1, w, h);
        string title = Piloting ? AutopilotTitle(vehicle) : boosting ? "BOOST" : "BATMOBILE";
        float top = Hud.Card(x, y, w, h, title, _units.Index == 1 ? "KM/H" : "MPH", Theme.Low, age, boosting ? Theme.Signal : null);

        float cx = x + w * 0.5f, cy = top + r;
        Dial(cx, cy, r, mph / GaugeMaxMph, boosting);
        string value = MathF.Round(speed * (_units.Index == 1 ? UnitsToKmh : UnitsToMph)).ToString(CultureInfo.InvariantCulture);
        float vs = Hud.Sc(1.3f);
        Gfx.TextCentre(cx, cy - Gfx.TextH(vs) * 0.6f, value, Theme.Hi, vs);
        Gfx.TextCentre(cx, cy + Gfx.TextH(vs) * 0.42f, _units.Index == 1 ? "KM/H" : "MPH", Theme.Low, Hud.Sc(Hud.SmallScale));

        float left = x + Hud.U(Hud.Pad), inner = w - Hud.U(Hud.Pad) * 2f;
        float my = top + r * 2f - Hud.U(8) + Hud.U(6);
        float maxHealth = MathF.Max(_maxHealth, vehicle.HealthMax + ArmourBonus(vehicle));
        float maxPower = MaxPower(vehicle);
        float hp = maxHealth > 0f ? vehicle.Health / maxHealth : 0f;
        Hud.Caption(left, my, inner, "ARMOUR", Percent(hp), Theme.Mid);
        my += Hud.CaptionH;
        Lens.Meter(left, my, inner, Hud.U(4), hp, Lens.Trail(vehicle.Ptr, hp), Theme.Accent, 10);
        my += Hud.U(4) + Hud.U(8);
        float pwr = maxPower > 0f ? vehicle.WeaponGeneratorPower / maxPower : 0f;
        Hud.Caption(left, my, inner, "WEAPON POWER", Percent(pwr), Theme.Mid);
        my += Hud.CaptionH;
        Lens.Meter(left, my, inner, Hud.U(4), pwr, 0f, Rgba.Mix(Theme.Accent, Theme.White, 0.35f), 10);
        my += Hud.U(4);

        if (_stats.On)
        {
            string jump = _bestAir <= 0f ? "-" : _bestAir.ToString("0.0", CultureInfo.InvariantCulture) + " S " + MathF.Round(_bestAirDistance / 100f).ToString(CultureInfo.InvariantCulture) + " M";
            Gfx.TextCentre(cx, my + Hud.U(8), "BEST " + Speed(_bestSpeed) + "   JUMP " + jump, Theme.Mid, Hud.Sc(Hud.SmallScale));
        }
    }

    private static string Percent(float f) => MathF.Round(Math.Clamp(float.IsFinite(f) ? f : 0f, 0f, 9.99f) * 100f).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A 270 degree dial open at the bottom: a faint track, the speed sweeping round it with a bright head, and ticks
    /// every tenth. Shapes over the card blend with its glass, since DrawTris draws solid.
    /// </summary>
    private static void Dial(float cx, float cy, float r, float frac, bool hot)
    {
        Rgba under = Shapes.Under;
        Shapes.Under = Theme.Panel;
        float band = Hud.U(6);
        Rgba col = hot ? Theme.Signal : Theme.Accent;
        frac = float.IsFinite(frac) ? Math.Clamp(frac, 0f, 1f) : 0f;

        Shapes.Arc(cx, cy, r - band, r, 135f, 405f, Theme.White, 0.09f);
        float end = 135f + 270f * frac;
        if (end > 135.5f)
        {
            Shapes.Arc(cx, cy, r - band, r, 135f, end, col, 0.95f);
            Shapes.Arc(cx, cy, r - band - Hud.U(2), r + Hud.U(2), MathF.Max(135f, end - 5f), end, Rgba.Mix(col, Theme.White, 0.55f), 1f);
        }

        float t = MathF.Max(1f, Gfx.S(1.5f));
        for (int i = 0; i <= 10; i++)
        {
            float a = (135f + 27f * i) * MathF.PI / 180f;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            float r0 = r - band - Hud.U(i % 5 == 0 ? 10 : 7), r1 = r - band - Hud.U(3);
            Shapes.QueueLine(cx + c * r0, cy + s * r0, cx + c * r1, cy + s * r1, t);
        }

        Shapes.FlushLines(Rgba.Mix(Theme.Panel, Theme.Mid, 0.6f));
        Shapes.Ring(cx, cy, r - band - Hud.U(15), r - band - Hud.U(14), col, 0.18f);
        Shapes.Under = under;
    }

    /// <summary>A speed in units per second, in the chosen speedometer unit.</summary>
    private string Speed(float unitsPerSecond)
    {
        bool kmh = _units.Index == 1;
        return MathF.Round(unitsPerSecond * (kmh ? UnitsToKmh : UnitsToMph)).ToString(CultureInfo.InvariantCulture) + (kmh ? " KM/H" : " MPH");
    }
}
