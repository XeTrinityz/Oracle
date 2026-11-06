using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Disruptor options on the PLAYER/GADGETS page. Read from BmGame.upk:
/// - Charges are RDisruptorSniper.iAntiEquipmentRemainingAmmo (mirrored into Ammo for the HUD). AimedFireDD refuses
///   with NoAmmoLeftThought at 0 and otherwise calls ReduceAmmo after each disruption. FullyRestoreAmmo sets it back
///   to iAntiEquipmentMaximumAmmo; predator rooms do the same in OnRoomChange, the open world refills on a timer.
/// - Which kinds of target can be disrupted is the bBatmanHas...Upgrade flags. UpdateDisruptorUpgrades only ever
///   turns a flag on (from PC.HasUpgrade), so flags set here stay set until cleared.
/// </summary>
internal sealed class Gadgets : Module
{
    public static readonly Gadgets I = new();

    public Toggle UnlimitedCharges = null!;
    private Toggle _allTargets = null!;

    /// <summary>Every upgrade flag that unlocks a kind of disruptor target. ExtraAmmo is left out: it changes the max.</summary>
    private static readonly (Func<RDisruptorSniper, bool> Get, Action<RDisruptorSniper, bool> Set)[] TargetUpgrades =
    {
        (d => d.bBatmanHasDroneDisruptorUpgrade, (d, v) => d.bBatmanHasDroneDisruptorUpgrade = v),
        (d => d.bBatmanHasMineDisruptorUpgrade, (d, v) => d.bBatmanHasMineDisruptorUpgrade = v),
        (d => d.bBatmanHasMedicDisruptorUpgrade, (d, v) => d.bBatmanHasMedicDisruptorUpgrade = v),
        (d => d.bBatmanHasStunStickDisruptorUpgrade, (d, v) => d.bBatmanHasStunStickDisruptorUpgrade = v),
        (d => d.bBatmanHasInvisibleThugDisruptorUpgrade, (d, v) => d.bBatmanHasInvisibleThugDisruptorUpgrade = v),
        (d => d.bBatmanHasSentryGunDispenserDisruptorUpgrade, (d, v) => d.bBatmanHasSentryGunDispenserDisruptorUpgrade = v),
        (d => d.bBatmanHasMinidroneControllerDisruptorUpgrade, (d, v) => d.bBatmanHasMinidroneControllerDisruptorUpgrade = v),
        (d => d.bBatmanHasDetectiveModeJammerDisruptorUpgrade, (d, v) => d.bBatmanHasDetectiveModeJammerDisruptorUpgrade = v),
        (d => d.bBatmanHasDetectiveModeDetectorDisruptorUpgrade, (d, v) => d.bBatmanHasDetectiveModeDetectorDisruptorUpgrade = v),
        (d => d.bBatmanHasRHDBlindDroneUpgrade, (d, v) => d.bBatmanHasRHDBlindDroneUpgrade = v),
    };

    public override void Build()
    {
        UnlimitedCharges = AddToggle("disruptor_unlimited", "UNLIMITED DISRUPTOR CHARGES",
            "The disruptor never runs out of charges, so you can sabotage every gun, mine, drone and medic in an area. The charge count stays full.")
            .Maintain(Refill);
        _allTargets = AddToggle("disruptor_all_targets", "DISRUPT ANY TARGET",
            "The disruptor works on every kind of target without buying its upgrades: drones, mines, medic packs, stun sticks, cloaked thugs, sentry dispensers and more. Turning it off goes back to the upgrades you own.",
            on => { if (!on) Safe.Run("disruptor upgrades restore", RestoreUpgrades); }).Maintain(GrantUpgrades);
        AddToggle("auto_password_hack", "AUTO PASSWORD HACK",
            "Remote hacking device password cracks solve themselves: start the hack as normal and every security level is cracked for you a moment later.")
            .Maintain(SolvePasswordHack);
    }

    public override void Shutdown()
    {
        if (_allTargets.On)
            Safe.Run("disruptor upgrades restore", RestoreUpgrades);
    }

    private static IEnumerable<RDisruptorSniper> Disruptors()
    {
        foreach (RDisruptorSniper d in World.All<RDisruptorSniper>())
        {
            if (d.IsValid && !d.bDeleteMe)
                yield return d;
        }
    }

    // ---- UNLIMITED DISRUPTOR CHARGES

    /// <summary>Tops up a disruptor that came in below full: a new save, or charges spent before the toggle went on.</summary>
    private static void Refill()
    {
        foreach (RDisruptorSniper d in Disruptors())
        {
            if (d.iAntiEquipmentMaximumAmmo > 0 && d.iAntiEquipmentRemainingAmmo < d.iAntiEquipmentMaximumAmmo)
                d.FullyRestoreAmmo();
        }
    }

    /// <summary>The only place a charge is spent. Skipped outright, so a shot never shows the count dropping.</summary>
    [Redirect(typeof(RDisruptorSniper), nameof(RDisruptorSniper.ReduceAmmo))]
    private static void ReduceAmmoRedirect(RDisruptorSniper self)
    {
        if (I.UnlimitedCharges is { On: true })
            return;

        self.ReduceAmmo();
    }

    // ---- AUTO PASSWORD HACK

    /// <summary>How long the crack has to be on screen first, so the Flash movie has started its own game.</summary>
    private const double HackSolveDelay = 0.75;

    private nint _hackSeen;
    private DateTime _hackSeenAt;

    /// <summary>
    /// Read from BmGame.upk: the Flash movie owns the passwords and the stick targets. When the last one is found it
    /// calls XI_PasswordFound, then XI_OutroComplete (which sets MinigameComplete), and the special move's
    /// Resonating.UpdateSpecialMove then runs SuccessfullActivateDisruptor: the target's SuccessfullyDisrupted, the
    /// success animation and the normal exit. This makes those two Flash calls itself and leaves the rest to the game.
    /// </summary>
    private static void SolvePasswordHack()
    {
        foreach (RSpecialMoveInstance_OmnitronPasswordCracking crack in World.All<RSpecialMoveInstance_OmnitronPasswordCracking>())
        {
            if (crack.bDeleteMe || crack.GetStateName().ToString() != "Resonating")
                continue;
            if (crack.TheOmnitronMovie is not { IsValid: true } movie || movie.MinigameComplete)
                continue;
            if (crack.PlayerPawn is not { IsValid: true } pawn || pawn.Ptr != World.PawnPtr)
                continue;

            if (I._hackSeen != crack.Ptr)
            {
                I._hackSeen = crack.Ptr;
                I._hackSeenAt = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - I._hackSeenAt).TotalSeconds < HackSolveDelay)
                continue;

            movie.XI_PasswordFound();
            movie.XI_OutroComplete();
            I._hackSeen = 0;
            Log.Info("Auto password hack: cracked " + (crack.CurrentTarget is { IsValid: true } t ? t.Name : "the target") + ".");
        }
    }

    // ---- DISRUPT ANY TARGET

    private static void GrantUpgrades()
    {
        foreach (RDisruptorSniper d in Disruptors())
        {
            foreach ((Func<RDisruptorSniper, bool> get, Action<RDisruptorSniper, bool> set) in TargetUpgrades)
            {
                if (!get(d))
                    set(d, true);
            }
        }
    }

    /// <summary>Clears every flag, then lets the game set back the ones the save has bought.</summary>
    private static void RestoreUpgrades()
    {
        foreach (RDisruptorSniper d in Disruptors())
        {
            foreach ((_, Action<RDisruptorSniper, bool> set) in TargetUpgrades)
                set(d, false);
            d.UpdateDisruptorUpgrades();
        }
    }
}

/// <summary>
/// Swaps the explosion of a disruptor-jammed gun. The tank effect only exists in memory once the game has
/// streamed in drone tanks, so a TANK pick keeps waiting for it rather than giving up.
/// </summary>
internal sealed class JammedGuns : Module
{
    public static readonly JammedGuns I = new();

    private const string Cls = "RBMWeaponRanged";
    private const string Prop = "JammedExplosionVFX";
    private const string Vanilla = "ParticleSystem'VFX_Gadgets.Disruptor.Double_Jammed_Explosion'";
    private const string TankPath = "VFX_Vehicles.Particles.Tank.HeavyTank_Explosion";
    private const double RetrySeconds = 5.0;

    private Choice _choice = null!;
    private bool _set;
    private Every _retry;

    public override void Build()
    {
        _choice = AddChoice("jammed_blast", "JAMMED GUN EXPLOSION", "What a disruptor-jammed gun looks like when it blows up. TANK uses the drone tank explosion, which is available once drone tanks are nearby.",
            new[] { "vanilla", "tank" }, new[] { "VANILLA", "TANK" }, Apply);
    }

    public override void WorldChanged() => _set = false;

    public override void Tick(float dt)
    {
        if (_choice.Index == 1 && !_set && _retry.Elapsed(RetrySeconds))
            TrySet();
    }

    public override void Shutdown()
    {
        if (_set)
            Safe.Run("jammed gun restore", () => Cvars.Restore(Cls, Prop, Vanilla));
    }

    private void Apply(int mode)
    {
        if (mode == 0)
        {
            _set = false;
            Cvars.Restore(Cls, Prop, Vanilla);
            return;
        }

        TrySet();
    }

    private void TrySet()
    {
        if (Game.FindObject<ParticleSystem>(TankPath) == null)
            return;

        Cvars.Override(Cls, Prop, "ParticleSystem'" + TankPath + "'");
        _set = true;
    }
}
