using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Weapon extras beyond power and cooldowns: the cannon's ammo and heat, the full arsenal, the missile barrage and the assisted takedown.</summary>
internal sealed partial class Batmobile
{
    private const float MaxMissileTargets = 40f;

    private readonly Held<RBatmobileMissileLauncher, int> _missileCap = new("missile targets", m => m.MaxTargets, (m, v) => m.MaxTargets = v);

    private Slider _missileTargets = null!;

    // The pursuit missile (RBatmobileEMPMissile), the lock-on shot for car chases and APCs. Its lock builds over
    // MissileLockOnDuration (CivilianMissileLockOnDuration for civilian cars); 0 locks at once. Its hit strength is
    // CalculateDamageFactor: AngleDamageFactor x (angle to the target's tail / MaxDamageAngle, capped at 1) plus the
    // rest as a wobbling random part, so a factor of 1 with a near-zero angle always gives a full hit.
    private readonly Held<RBatmobileEMPMissile, float> _lockFor = new("missile lock", m => m.MissileLockOnDuration, (m, v) => m.MissileLockOnDuration = v);
    private readonly Held<RBatmobileEMPMissile, float> _civilianLockFor = new("civilian missile lock", m => m.CivilianMissileLockOnDuration, (m, v) => m.CivilianMissileLockOnDuration = v);
    private readonly Held<RBatmobileEMPMissile, float> _angleShare = new("missile angle share", m => m.AngleDamageFactor, (m, v) => m.AngleDamageFactor = v);
    private readonly Held<RBatmobileEMPMissile, float> _fullAngle = new("missile full angle", m => m.MaxDamageAngle, (m, v) => m.MaxDamageAngle = v);
    // Missiles already scaled, by address and firing time. Addresses get reused by later missiles, so entries expire.
    private const double MissileMemory = 6.0;
    private readonly Dictionary<nint, double> _scaledMissiles = new();
    private Slider _pursuitDamage = null!;

    private void BuildArsenal()
    {
        AddToggle("batmobile_no_overheat", "NO RELOAD OR OVERHEAT", "The Batmobile's cannon never runs out of shells, never stops to reload and never overheats.").EveryTick(CannonTick);

        // RemoveBatmobileWeapon clears the weapon's unlock flag in the save, so this can't be undone from here and is a command, not a toggle.
        AddCommand("batmobile_all_weapons", "ARM EVERY WEAPON", "Fits the Batmobile with every weapon it has, unlocked or not. Nothing is written to your save: they stay until the Batmobile is next rebuilt, by a reload or a skin change.", ArmEverything);

        _missileTargets = AddSlider("batmobile_missile_targets", "MISSILE BARRAGE TARGETS", "How many targets the missile barrage can lock on to at once. GAME keeps your upgrade's own number.",
            0f, 0f, MaxMissileTargets, 1f, 20f, v => v <= 0f ? "GAME" : Fmt.Count(v), v => { if (v <= 0f) _missileCap.Restore(); }).EveryTick(MissileTick);

        AddToggle("pursuit_instant_lock", "INSTANT MISSILE LOCK", "The pursuit missile locks on to cars, APCs and anything else it can target the moment they are in your sights.",
            on => { if (!on) { _lockFor.Restore(); _civilianLockFor.Restore(); } }).EveryTick(LockTick);
        AddToggle("pursuit_full_hits", "FULL-STRENGTH MISSILE HITS", "Every pursuit missile hits as hard as a perfect shot from right behind, whatever the angle.",
            on => { if (!on) { _angleShare.Restore(); _fullAngle.Restore(); } }).EveryTick(FullHitTick);
        _pursuitDamage = AddSlider("pursuit_damage", "PURSUIT MISSILE DAMAGE", "Multiplies the damage of each pursuit missile, so APCs and fleeing cars go down in fewer hits.",
            1f, 1f, 10f, 1f, 5f, Fmt.Multiplier);

        AddToggle("batmobile_takedown_ready", "BATMOBILE TAKEDOWN READY", "In a fight on foot, the Batmobile's assisted takedown is always charged, so it's ready whenever the car is close enough to help.").EveryTick(TakedownTick);
    }

    private void CannonTick()
    {
        if (Car() == null || _cannon is not { IsValid: true } c)
            return;

        // It only reloads below a full magazine, and only overheats once its heat builds up.
        if (c.AmmoPerMagazine > 0 && c.CurrentAmmo < c.AmmoPerMagazine)
            c.CurrentAmmo = c.AmmoPerMagazine;
        if (c.CurrentHeatLevel > 0f)
            c.CurrentHeatLevel = 0f;
        if (c.bOverheating)
            c.StopOverHeat();
    }

    private void ArmEverything()
    {
        RVehicleBatmobileBase car = Car() ?? throw new InvalidOperationException("there's no Batmobile in this area");
        int before = _weapons.Count;
        car.AddBatmobileWeapons(true);
        FindWeapons(car);
        int added = _weapons.Count - before;
        if (added <= 0)
            throw new InvalidOperationException("the Batmobile already has every weapon");

        Overlay.Notice("ARM EVERY WEAPON", Fmt.Plural(added, "WEAPON") + " ADDED", true);
    }

    private void MissileTick()
    {
        if (Car() == null)
            return;

        foreach (RVehicleWeapon w in _weapons)
        {
            if (w is RBatmobileMissileLauncher { IsValid: true } launcher)
            {
                _missileCap.Set(launcher, (int)_missileTargets.Value);
                return;
            }
        }
    }

    private RBatmobileEMPMissile? PursuitLauncher()
    {
        if (Car() == null)
            return null;

        foreach (RVehicleWeapon w in _weapons)
        {
            if (w is RBatmobileEMPMissile { IsValid: true } launcher)
                return launcher;
        }

        return null;
    }

    private void LockTick()
    {
        if (PursuitLauncher() is { } m)
        {
            _lockFor.Set(m, 0f);
            _civilianLockFor.Set(m, 0f);
        }
    }

    private void FullHitTick()
    {
        if (PursuitLauncher() is { } m)
        {
            _angleShare.Set(m, 1f);
            _fullAngle.Set(m, 0.001f);
        }
    }

    /// <summary>
    /// Where the pursuit missile's damage is set, right after it is fired. RBatmobileEMPMissile has no subclasses, so
    /// calling the original here can't land in another override. A retarget calls it again on the same missile,
    /// so each one is scaled once.
    /// </summary>
    [Redirect(typeof(RBatmobileEMPMissile), nameof(RBatmobileEMPMissile.OnProjectileSpawned))]
    private static void PursuitMissileFiredRedirect(RBatmobileEMPMissile self, RProjectile projectile, Actor target)
    {
        self.OnProjectileSpawned(projectile, target);
        Safe.Run("pursuit missile damage", () =>
        {
            if (I._pursuitDamage is not { } slider || slider.Value <= 1f || projectile is not { IsValid: true })
                return;
            double now = Clock.Now;
            Dictionary<nint, double> seen = I._scaledMissiles;
            if (seen.TryGetValue(projectile.Ptr, out double firedAt) && now - firedAt < MissileMemory)
                return;
            if (seen.Count > 64)
            {
                foreach (nint old in seen.Where(kv => now - kv.Value >= MissileMemory).Select(kv => kv.Key).ToList())
                    seen.Remove(old);
            }

            seen[projectile.Ptr] = now;
            projectile.Damage *= slider.Value;
        });
    }

    /// <summary>Tops up the meter the assisted takedown waits for. Only in a fight on foot, where the game shows and spends it.</summary>
    private static void TakedownTick()
    {
        if (World.Pc is not RPlayerControllerCombat pc || World.Pawn is RVehicleBatmobileBase || !pc.IsInCombat(false))
            return;

        float full = pc.GetBatmobileTakedownAdrenalineSpecialLimit();
        if (full > 0f && pc.BatmobileTakedownAdrenalineValue < full)
        {
            pc.BatmobileTakedownAdrenalineValue = full;
            pc.ShowBatmobileTakedownAdrenaline();
        }
    }
}
