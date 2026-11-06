using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// AUTO COMBAT: the Batmobile fights for you in pursuit or battle mode. Outside battle mode the weapons are holstered
/// and refuse to fire, so Oracle fires their shots itself, the way the weapons do: Spawn(archetype class, weapon,
/// None, location, rotation, archetype) then Init(direction), for the 60mm cannon's ProjectileArchetype and the
/// missile launcher's homing MissileClass (SetTarget first, bForceExplodeAtTarget for vehicles). The shots carry
/// the game's own damage, hits and kill credit. SHOOT DOWN MISSILES uses RTankMissile.TakeDamageFromWeapon, the
/// game's own "shot out of the sky" path, refused only in a missile's brief invincible start.
/// </summary>
internal sealed partial class Batmobile
{
    // Base gaps between shots at 1.00x FIRE RATE, in seconds.
    private const double ShellGap = 1.0;
    private const double MissileGap = 0.8;
    private const double BarrageGap = 8.0;
    private const double BarrageStagger = 0.12;
    private const int BarrageMin = 3;
    private const int BarrageDefault = 6;

    // Where shots leave from, above the car and out past its nose so they never hit it.
    private const float ShellLift = 160f;
    private const float ShellClear = 350f;
    private const float MissileLift = 220f;
    private const float MissileArc = 0.6f;

    // SHOOT DOWN MISSILES works between these distances from you (units), every so often.
    private const float ShootDownFar = 10000f;
    private const float ShootDownNear = 600f;
    private const float ShootDownClosing = 4000f;
    private const double ShootDownGap = 0.15;

    private Slider _combatRange = null!;
    private Slider _combatRate = null!;
    private readonly List<RVehicleNPC> _foes = new();
    private readonly Queue<RVehicleNPC> _barrage = new();
    private Every _foeScan;
    private double _shellAt;
    private double _missileAt;
    private double _barrageAt;
    private double _barrageNext;
    private double _downAt;

    private void BuildAutoCombat()
    {
        AddToggle("batmobile_auto_combat", "AUTO COMBAT", "While you drive, in pursuit or battle mode, the Batmobile fights for you: cannon shells at tanks, homing missiles at drones and missile barrages at groups. Heavy tanks are left to you.")
            .EveryTick(AutoCombatTick);
        AddToggle("batmobile_shoot_down", "SHOOT DOWN MISSILES", "Militia missiles heading for you are shot out of the sky before they arrive, in the Batmobile or on foot.")
            .EveryTick(ShootDownTick);
        _combatRange = AddSlider("batmobile_combat_range", "AUTO COMBAT RANGE", "How far away AUTO COMBAT picks its targets.", 120f, 40f, 300f, 10f, 200f, Fmt.Metres).Pref();
        _combatRate = AddSlider("batmobile_combat_rate", "AUTO COMBAT FIRE RATE", "How quickly AUTO COMBAT fires. 1.00x is a shell a second, a missile every 0.8 s and a barrage every 8 s.",
            1f, 0.5f, 4f, 0.25f, 2f, Fmt.Times).Pref();
    }

    private void AutoCombatTick()
    {
        if (World.Pawn is not RVehicleBatmobileBase car || World.Paused || Car() == null)
        {
            _barrage.Clear();
            return;
        }

        double now = Clock.Now;
        float rate = _combatRate.Value;
        if (_foeScan.Elapsed(0.5))
            FindFoes(car);

        RBatmobileMissileLauncher? launcher = Weapon<RBatmobileMissileLauncher>();
        if (_barrage.Count > 0)
        {
            if (now < _barrageNext)
                return;
            _barrageNext = now + BarrageStagger;
            RVehicleNPC next = _barrage.Dequeue();
            if (launcher != null && Hostile(next))
                FireMissile(car, launcher, next);
            return;
        }

        if (_foes.Count == 0)
            return;

        if (launcher != null && _foes.Count >= BarrageMin && now - _barrageAt > BarrageGap / rate)
        {
            _barrageAt = now;
            int size = launcher.MaxTargets > 0 ? launcher.MaxTargets : BarrageDefault;
            foreach (RVehicleNPC foe in _foes.Take(size))
                _barrage.Enqueue(foe);
            return;
        }

        RVehicleNPC target = _foes[0];
        RBatmobileHeavyCannon? cannon = Weapon<RBatmobileHeavyCannon>();
        bool flying = target is RVehicleFlying;
        if ((flying || cannon == null) && launcher != null)
        {
            if (now - _missileAt > MissileGap / rate)
            {
                _missileAt = now;
                FireMissile(car, launcher, target);
            }
        }
        else if (cannon != null && now - _shellAt > ShellGap / rate && FireShell(car, cannon, target))
        {
            _shellAt = now;
        }
    }

    private T? Weapon<T>() where T : RVehicleWeapon
    {
        foreach (RVehicleWeapon w in _weapons)
        {
            if (w is T { IsValid: true } found)
                return found;
        }

        return null;
    }

    /// <summary>Militia tanks and drones in range that know you're there, the ones attacking you first, then the nearest.</summary>
    private void FindFoes(RVehicleBatmobileBase car)
    {
        _foes.Clear();
        Vector3 at = car.Location;
        float reach = _combatRange.Value * 100f;
        var ranked = new List<(RVehicleNPC Foe, bool OnMe, float Dist)>();
        foreach (RVehicleNPC v in World.All<RVehicleNPC>())
        {
            if (v is not (RVehicleTank or RVehicleFlying))
                continue;

            float dist = Vector3.Distance(v.Location, at);
            if (dist > reach || !Hostile(v))
                continue;
            if (!Safe.Try("foe awareness", () => v.IsAwareOfBatmobile() || v.AwareOfPlayer() || v.IsAttacking()))
                continue;

            bool onMe = Safe.Try("foe target", () => v.IsAttacking() && v.GetTargetActor() is { } t && t.Ptr == car.Ptr);
            ranked.Add((v, onMe, dist));
        }

        foreach (var r in ranked.OrderBy(r => r.OnMe ? 0 : 1).ThenBy(r => r.Dist))
            _foes.Add(r.Foe);
    }

    /// <summary>A live enemy vehicle Oracle may shoot: not parked set dressing, not a boss-style heavy tank, not hacked to your side.</summary>
    private static bool Hostile(RVehicleNPC v) =>
        v is { IsValid: true, bDeleteMe: false, bHidden: false, DroneHacked: false, CivilianVehicle: false } && v.Health > 0 &&
        v is not RVehicleStaticTank && v is not RVehicleHeavyTank && v.CurrentBehaviour is { IsValid: true };

    /// <summary>One 60mm shell, aimed ahead of a moving target, only with a clear line to it.</summary>
    private bool FireShell(RVehicleBatmobileBase car, RBatmobileHeavyCannon cannon, RVehicleNPC target)
    {
        if (cannon.ProjectileArchetype is not { IsValid: true } shell)
            return false;

        Vector3 aim = target.Location + new Vector3(0f, 0f, 60f);
        Vector3 start = car.Location + new Vector3(0f, 0f, ShellLift);
        Vector3 dir = Vector3.Normalize(aim - start);
        Vector3 from = start + dir * ShellClear;
        if (shell.Speed > 0f)
            aim += target.Velocity * (Vector3.Distance(from, aim) / shell.Speed);
        dir = Vector3.Normalize(aim - from);
        if (!car.FastTrace(aim, from, Vector3.Zero, false))
            return false;

        if (cannon.Spawn(shell.Class, cannon, FName.None, from, Rotator.FromDirection(dir), shell, true) is not RProjectile p)
            return false;

        if (p is RBasicProjectile basic)
        {
            basic.SetTarget(target);
            basic.bLockedOn = true;
        }

        p.Init(dir);
        return true;
    }

    /// <summary>One homing missile from the barrage launcher, launched upwards so it arcs onto the target.</summary>
    private void FireMissile(RVehicleBatmobileBase car, RBatmobileMissileLauncher launcher, RVehicleNPC target)
    {
        if (launcher.MissileClass is not { IsValid: true } missile)
            return;

        Vector3 from = car.Location + new Vector3(0f, 0f, MissileLift);
        Vector3 dir = Vector3.Normalize(Vector3.Normalize(target.Location - from) + new Vector3(0f, 0f, MissileArc));
        if (launcher.Spawn(missile.Class, launcher, FName.None, from, Rotator.FromDirection(dir), missile, true) is not RProjectile p)
            return;

        if (p is RBatmobileMissile homing)
        {
            homing.SetTarget(target);
            homing.bForceExplodeAtTarget = true;
            homing.ForceExplodeProxDist = 100f;
        }

        p.Init(dir);
    }

    private void ShootDownTick()
    {
        if (World.Pawn is not { } me || World.Paused || Clock.Now - _downAt < ShootDownGap)
            return;

        Vector3 at = me.Location;
        nint player = World.Player?.Ptr ?? 0;
        foreach (RTankMissile m in World.All<RTankMissile>())
        {
            if (m.bDeleteMe || m.WasShotOutOfSky)
                continue;

            float dist = Vector3.Distance(m.Location, at);
            if (dist > ShootDownFar || dist < ShootDownNear)
                continue;

            bool aimedAtMe = m.Target is { } t && (t.Ptr == me.Ptr || t.Ptr == player);
            bool closing = Vector3.Dot(m.Velocity, at - m.Location) > 0f;
            if (!aimedAtMe && !(closing && dist < ShootDownClosing))
                continue;

            m.TakeDamageFromWeapon(1000, World.Pc!, m.Location, Vector3.UnitZ, Vector3.Zero, RDmgType_BatmobileChainGun.StaticClass(), default, me, false);
            if (m.WasShotOutOfSky)
            {
                _downAt = Clock.Now;
                return;
            }
        }
    }
}
