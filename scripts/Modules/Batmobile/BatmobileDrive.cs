using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using EjectType = BmSDK.BmGame.RVehicleBatmobileBase.EBatmobileEjectType;
using TyreType = BmSDK.Engine.SVehicleWheel.ETyreType;

namespace CeZeY.Oracle;

/// <summary>
/// Handling, protection and stunts for the Batmobile you are driving. The car's physics is native, so speed, jumps
/// and gravity act on its rigid body directly (the way the game's own SlamBatmobile cheat moves it), and the
/// native tuning values are only scaled from what the game had.
/// </summary>
internal sealed partial class Batmobile
{
    // ENGINE POWER: extra acceleration per 1.00x above stock, and the cruising speed it scales (about 100 mph).
    // Tuned by feel; the native engine model isn't readable.
    private const float EngineAccel = 1500f;
    private const float EngineCruise = 4500f;
    private const float EngineSpeedLimit = 18000f;

    // BATMOBILE JUMP at 1.00x, in units per second straight up.
    private const float JumpSpeed = 1200f;
    private const double JumpGroundGrace = 0.3;

    // AUTO-RIGHT: how far over counts as tipped, how long it must stay that way, and the gap between tries.
    private const float TippedDegrees = 75f;
    private const float TippedSpeed = 400f;
    private const float RightAfter = 1.2f;
    private const double RightGap = 2.0;
    private const float RightLift = 150f;

    private readonly Held<RVehicleSimBatmobile, float> _topSpeed = new("top speed", s => s.ScaleTopSpeed, (s, v) => s.ScaleTopSpeed = v);
    private readonly Held<RVehicleSimBatmobile, float> _battleTopSpeed = new("battle mode speed", s => s.BattleModeMaxSpeed, (s, v) => s.BattleModeMaxSpeed = v);
    private readonly Held<RVehicleBatmobileBase, EjectType> _eject = new("super eject", b => b.MaxEjectType, (b, v) => b.MaxEjectType = v);
    private readonly Held<RVehicleBatmobileBase, bool> _doubleDodge = new("double dodge", b => b.bDoubleDodgeUpgrade, (b, v) => b.bDoubleDodgeUpgrade = v);
    private readonly Held<RVehicleBatmobileBase, bool> _empVulnerable = new("EMP", b => b.VulnerableToEMP, (b, v) => b.VulnerableToEMP = v);

    private Toggle _empProof = null!;
    private Toggle _selfRight = null!;
    private Toggle _superEject = null!;
    private Toggle _dodgeTwice = null!;
    private Slider _engine = null!;
    private Slider _grip = null!;
    private Slider _battleSpeed = null!;
    private Slider _jumpPower = null!;
    private Slider _airGravity = null!;

    private float _gripApplied = 1f;
    private float _gravity;
    private float _tippedFor;
    private double _rightedAt;
    private double _groundedAt;

    private void BuildDrive()
    {
        AddCommand("batmobile_repair", "REPAIR AND RECHARGE", "Fixes all of the Batmobile's damage and fills its weapon generator, once.", Repair);

        _empProof = AddToggle("batmobile_emp_proof", "EMP-PROOF", "EMP can't shut the Batmobile down, so it keeps driving and can always be called.",
            on => { if (!on) _empVulnerable.Restore(); });
        _selfRight = AddToggle("batmobile_self_right", "AUTO-RIGHT WHEN FLIPPED", "When the Batmobile lands on its roof or side and stays there, it is put back on its wheels a second later.");

        _engine = AddSlider("batmobile_engine", "ENGINE POWER", "Harder acceleration and a higher top speed while driving, boost included. 1.00x is the game's own engine.",
            1f, 1f, 4f, 0.25f, 2f, Fmt.Times, v => { if (v <= 1f) _topSpeed.Restore(); });
        _grip = AddSlider("batmobile_grip", "TYRE GRIP", "How hard the tyres hold the road. Lower slides and drifts more, higher corners flat. Handbrake turns and drifts keep their own grip.",
            1f, 0.25f, 3f, 0.25f, 2f, Fmt.Times, v => { if (MathF.Abs(v - _gripApplied) > 0.001f) SetGrip(v); });

        _battleSpeed = AddSlider("batmobile_battle_speed", "BATTLE MODE SPEED", "How fast the Batmobile moves in battle mode. 1.00x is the game's own speed.",
            1f, 1f, 3f, 0.25f, 2f, Fmt.Times, v => { if (v <= 1f) _battleTopSpeed.Restore(); });
        _dodgeTwice = AddToggle("batmobile_double_dodge", "DOUBLE DODGE", "Battle mode's dodge can be used twice in a row, as with its upgrade, without buying it.",
            on => { if (!on) _doubleDodge.Restore(); });

        AddCommand("batmobile_jump", "BATMOBILE JUMP", "Launches the Batmobile into the air. Bind it to a key. It needs its wheels on the ground, so it can't climb the sky.", Jump);
        _jumpPower = AddSlider("batmobile_jump_power", "JUMP POWER", "How high BATMOBILE JUMP launches the car.", 1f, 0.5f, 4f, 0.25f, 2f, Fmt.Times).Pref();
        _airGravity = AddSlider("batmobile_air_gravity", "AIR GRAVITY", "How strongly the Batmobile falls once its wheels leave the ground, for longer, floatier jumps. Driving on the road is unchanged.",
            1f, 0.1f, 1f, 0.05f, 0.4f, Fmt.Percent);
        _superEject = AddToggle("batmobile_super_eject", "MAX SUPER EJECT", "Holding the eject button always builds up to the highest super eject, as if fully upgraded.",
            on => { if (!on) _eject.Restore(); });
    }

    private void DriveTick(float dt)
    {
        if (World.Pawn is not RVehicleBatmobileBase car)
        {
            _tippedFor = 0f;
            return;
        }

        RVehicleSimBatmobile? sim = car.SimObj is RVehicleSimBatmobile { IsValid: true } found ? found : null;
        bool grounded = car.HasWheelsOnGround();
        if (grounded)
            _groundedAt = Clock.Now;

        if (_empProof.On)
        {
            _empVulnerable.Set(car, false);
            if (car.DisabledByEMP)
                car.SetDisabledByEMP(false);
        }

        if (_superEject.On)
            _eject.Set(car, EjectType.EBET_SuperEject2);
        if (_dodgeTwice.On)
            _doubleDodge.Set(car, true);

        // Scale only real values: a zero would mean the game doesn't use it.
        if (sim != null && _engine.Value > 1f)
            _topSpeed.Set(sim, o => o > 0.01f ? o * _engine.Value : o);
        if (sim != null && _battleSpeed.Value > 1f)
            _battleTopSpeed.Set(sim, o => o > 0.01f ? o * _battleSpeed.Value : o);

        // The winch can hold the car up a wall with no wheels down; leave that to the game.
        bool winched = sim is { Abseiling: true };
        // The autopilot sets its own pace for the corners ahead, so the engine doesn't push it.
        if (_engine.Value > 1f && grounded && !Piloting && _brakeCar == null)
            Push(car, dt);
        if (_airGravity.Value < 1f && !grounded && !winched)
            Float(car, dt);
        if (_selfRight.On && !winched)
            SelfRight(car, dt);
    }

    private void RestoreDrive()
    {
        _topSpeed.Restore();
        _battleTopSpeed.Restore();
        _eject.Restore();
        _doubleDodge.Restore();
        _empVulnerable.Restore();
        _tippedFor = 0f;
        _gravity = 0f;
    }

    private void Repair()
    {
        RVehicleBatmobileBase car = Car() ?? throw new InvalidOperationException("there's no Batmobile in this area");
        car.GiveMaxHealth();
        car.GiveMaxEnergy();
    }

    // ---- engine

    /// <summary>Adds speed along the car's nose while the throttle is held, up to a cruise speed the slider scales.</summary>
    private void Push(RVehicleBatmobileBase car, float dt)
    {
        float throttle = car.Throttle;
        if (throttle <= 0.05f || car.CollisionComponent is not { IsValid: true } body || Safe.Try("battle mode check", car.IsInBattleMode))
            return;

        Vector3 forward = car.Rotation.ToDirection();
        float along = Vector3.Dot(car.Velocity, forward);
        float limit = MathF.Min(EngineCruise * _engine.Value, EngineSpeedLimit);
        if (along < -50f || along >= limit)
            return;

        float gain = MathF.Min((_engine.Value - 1f) * EngineAccel * throttle * dt, limit - along);
        body.SetRBLinearVelocity(forward * gain, true);
    }

    // ---- grip

    /// <summary>
    /// The cheat manager's own BatmobileSetTireFrictionMod, on the Batmobile's normal and battle mode tyres. It is a
    /// static native: the mod belongs to the tyre type, not to one car, so it outlasts a new car or a reload.
    /// </summary>
    private void SetGrip(float mod)
    {
        RVehicleBatmobileBase.DebugSetTireFrictionMod(TyreType.ETyreType_Batmobile, mod);
        RVehicleBatmobileBase.DebugSetTireFrictionMod(TyreType.ETyreType_BatmobileBattleMode, mod);
        _gripApplied = mod;
    }

    private void RestoreGrip()
    {
        if (MathF.Abs(_gripApplied - 1f) > 0.001f)
            Safe.Run("grip restore", () => SetGrip(1f));
    }

    // ---- air

    private void Jump()
    {
        if (World.Pawn is not RVehicleBatmobileBase car)
            throw new InvalidOperationException("you are not driving the Batmobile");
        if (!car.HasWheelsOnGround() && Clock.Now - _groundedAt > JumpGroundGrace)
            throw new InvalidOperationException("the Batmobile is already in the air");
        if (car.CollisionComponent is not { IsValid: true } body)
            throw new InvalidOperationException("the Batmobile has no physics body right now");

        body.WakeRigidBody(FName.None);
        body.SetRBLinearVelocity(new Vector3(0f, 0f, JumpSpeed * _jumpPower.Value), true);
    }

    /// <summary>Cancels part of gravity while airborne, so jumps hang longer.</summary>
    private void Float(RVehicleBatmobileBase car, float dt)
    {
        if (car.CollisionComponent is not { IsValid: true } body)
            return;

        if (_gravity <= 0f)
        {
            if (World.WorldInfo is not { } wi)
                return;

            float scale = wi.RBPhysicsGravityScaling > 0f ? wi.RBPhysicsGravityScaling : 1f;
            _gravity = -wi.GetGravityZ() * scale;
            if (_gravity <= 0f)
                return;
        }

        body.SetRBLinearVelocity(new Vector3(0f, 0f, _gravity * (1f - _airGravity.Value) * dt), true);
    }

    // ---- auto-right

    /// <summary>Sets a car that has sat tipped over back on its wheels, the way the game's own FlipBatmobile cheat moves it.</summary>
    private void SelfRight(RVehicleBatmobileBase car, float dt)
    {
        Rotator r = car.Rotation;
        bool tipped = MathF.Abs(Wrap(r.Roll)) > TippedDegrees || MathF.Abs(Wrap(r.Pitch)) > TippedDegrees;
        bool stuck = tipped && car.Velocity.Length() < TippedSpeed && car.GetNumWheelsOnGround() < 2;
        _tippedFor = stuck ? _tippedFor + dt : 0f;
        if (_tippedFor < RightAfter || Clock.Now - _rightedAt < RightGap || car.Mesh is not { IsValid: true } mesh)
            return;

        _tippedFor = 0f;
        _rightedAt = Clock.Now;
        mesh.SetRBRotation(new Rotator(0f, r.Yaw, 0f), FName.None);
        mesh.SetRBPosition(car.Location + new Vector3(0f, 0f, RightLift), FName.None);
        mesh.SetRBAngularVelocity(Vector3.Zero, false);
        Log.Info("AUTO-RIGHT: the Batmobile was tipped over (roll " + Wrap(r.Roll).ToString("0", CultureInfo.InvariantCulture) + ", pitch " +
            Wrap(r.Pitch).ToString("0", CultureInfo.InvariantCulture) + "), put back on its wheels.");
    }

    private static float Wrap(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)
            degrees -= 360f;
        else if (degrees < -180f)
            degrees += 360f;
        return degrees;
    }
}
