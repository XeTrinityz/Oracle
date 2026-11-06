using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// BATTLE MODE ON ANY SKIN. Every way into battle mode (the toggle, the held trigger, takedowns) ends in the native
/// TryToChangeMode, which refuses unless the car has a TransformationAnimNode and IsReadyToTransform passes
/// (not mid-transform, winch not locked on or homing, weapons willing). It then just calls GotoState('BattleMode'),
/// and BattleMode.BeginState does the real work: battle drive mode, battle weapons, battle key binds, HUD. Several
/// winchables call GotoState('BattleMode') on the car directly, which is how a skin without battle mode can end up
/// in it. Oracle does the same: when the player asks for battle mode and the game doesn't switch, it switches the
/// state itself, and switches back when they let go (or press again, with the toggle option).
/// </summary>
internal sealed partial class Batmobile
{
    private const string BattleState = "BattleMode";
    private const string PursuitState = "PursuitMode";

    // How long the game gets to switch on its own before Oracle does it.
    private const double BattleGrace = 0.2;
    private const double BattleRetry = 1.0;
    private const double BattleKeysRefresh = 0.5;

    // After a load, a cutscene (mod ticks stop) or getting in, input reads can be stale: wait this long first.
    private const double BattleSettle = 2.0;

    private Toggle _anySkinBattle = null!;
    private HashSet<int> _battleKeys = new();
    private nint _battleKeysInput;
    private double _battleKeysAt = -1;
    private bool _battleHeld;
    private bool _battleWant;
    private bool _battlePending;
    private double _battleWantAt;
    private double _battleForcedAt = -10;
    private nint _battleForcedCar;
    private nint _battleLoggedCar;
    private nint _battleLastCar;
    private double _battleCarSince;
    private double _battleTickAt;
    private string _battleWhy = "";
    private string _battleKeyName = "";
    private bool _battleLastIn;

    private void BuildBattle()
    {
        _anySkinBattle = AddToggle("batmobile_battle_any_skin", "BATTLE MODE ON ANY SKIN",
            "Battle mode with its weapons, winch and camera on skins the game won't let transform. The car doesn't change shape. Skins whose wheels can't turn sideways (1989, Arkham Asylum and others) keep normal driving in battle mode instead of strafing, which would crash the game.",
            on => { if (!on) LeaveForcedBattle("switched off"); }).EveryTick(BattleTick);
    }

    private static bool InState(Actor a, string state) => string.Equals(a.GetStateName().ToString(), state, StringComparison.OrdinalIgnoreCase);

    private void BattleTick()
    {
        if (World.Pawn is not RVehicleBatmobileBase && _battleForcedCar != 0)
            ReleaseLeftCar();

        if (Menu.IsOpen || World.Pc is not { } pc || pc.PlayerInput is not RPlayerInput input || World.Pawn is not RVehicleBatmobileBase car)
        {
            _battleHeld = false;
            _battlePending = false;
            return;
        }

        EnsureWinchSockets(car);
        bool inBattle = InState(car, BattleState);
        if (inBattle)
            KeepSafeDriveMode(car);

        // A gap in ticks means a cutscene or a load; a new pawn means the player just got in.
        if (_battleLastCar != car.Ptr || Clock.Now - _battleTickAt > 0.5)
            _battleCarSince = Clock.Now;
        _battleTickAt = Clock.Now;
        bool wasInBattle = _battleLastCar == car.Ptr ? _battleLastIn : inBattle;
        _battleLastCar = car.Ptr;
        _battleLastIn = inBattle;

        bool toggle = input.bBatmobileBattleModeToggle;
        bool keyHeld = BattleKeyHeld(input, inBattle);
        bool triggerHeld = !toggle && input.aLeftTrigger > 0.5f;
        bool held = keyHeld || triggerHeld;
        if (held)
            _battleWhy = keyHeld ? "battle mode key " + _battleKeyName : "left trigger " + input.aLeftTrigger.ToString("0.00");
        bool pressed = held && !_battleHeld;
        _battleHeld = held;

        if (toggle)
        {
            // The game may already have reacted to this press, so judge it by the state before it.
            if (pressed)
            {
                _battleWant = !wasInBattle;
                _battleWantAt = Clock.Now;
                _battlePending = true;
            }
        }
        else
        {
            if (held != _battleWant)
                _battleWantAt = Clock.Now;
            _battleWant = held;
            _battlePending = true;
        }

        if (!_battlePending || inBattle == _battleWant)
        {
            if (toggle)
                _battlePending = false;
            return;
        }

        if (Clock.Now - _battleWantAt < BattleGrace || Clock.Now - _battleForcedAt < BattleRetry)
            return;

        if (_battleWant)
        {
            if (Clock.Now - _battleCarSince < BattleSettle || pc.bCinematicMode || Safe.Try("move input check", pc.IsMoveInputIgnored))
            {
                // Not a press the player meant: the game is in a cutscene, has movement switched off, or just loaded.
                _battlePending = !toggle;
                _battleForcedAt = Clock.Now;
                return;
            }

            LogBattleSkin(car);
            if (!CanForceBattle(car, out string why))
            {
                Log.Throttled("battle any skin refused", "Battle mode on any skin: not now (" + why + ").");
                _battlePending = !toggle;
                return;
            }

            _battleForcedAt = Clock.Now;
            _battleForcedCar = car.Ptr;
            car.GotoState(new FName(BattleState), FName.None, false, false);
            bool strafe = KeepSafeDriveMode(car);
            Log.Info("Battle mode on any skin: entered battle mode on " + _battleWhy + " (now " + car.GetStateName() + ", " + (strafe ? "battle driving" : "normal driving: the wheels have no wheel controls") + ").");
        }
        else if (!toggle && _battleForcedCar != car.Ptr)
        {
            // Holding: the game leaves its own battle mode on release; only undo what Oracle started.
            return;
        }
        else if (WinchBusy(car))
        {
            // The game never switches modes while the winch is homing or locked on (IsReadyToTransform), and leaving
            // here strands the winch on in pursuit mode. Stay until it lets go; the release is still pending.
            return;
        }
        else
        {
            // A toggle press the game couldn't act on (a skin with no transform can't leave either), or a release.
            _battleForcedAt = Clock.Now;
            _battleForcedCar = 0;
            LeaveBattle(car, toggle ? "pressed again" : "released");
        }

        if (toggle)
            _battlePending = false;
    }

    /// <summary>The winch is flying or holding on: the states IsReadyToTransform refuses a mode switch in.</summary>
    private static bool WinchBusy(RVehicleBatmobileBase car) =>
        car.BatmobileWinch is { IsValid: true } winch && (InState(winch, "WinchLockedOn") || InState(winch, "Homing"));

    private static bool CanForceBattle(RVehicleBatmobileBase car, out string why)
    {
        why = car.bBattleModeLocked ? "the story hasn't unlocked battle mode yet"
            : car.bControlledBySeqAct ? "a mission is driving the car"
            : car.MatineeControlRefCount != 0 ? "a cutscene has the car"
            : car.IsUpsideDown() ? "the car is upside down"
            : WinchBusy(car) ? "the winch is busy"
            : "";
        return why.Length == 0;
    }

    /// <summary>
    /// The player got out of a car Oracle put in battle mode. On the way out the game only removes the battle keys
    /// (RemoveBatmobileBindsAndSetBatmanBinds) and leaves Batman's own to BattleMode.EndState, which a skin with no
    /// transform never reaches: its TryToChangeMode('PursuitMode') always fails. Leaving the state here runs EndState
    /// with no driver, which binds Batman's keys again.
    /// </summary>
    private void ReleaseLeftCar()
    {
        nint forced = _battleForcedCar;
        RVehicleBatmobileBase? car = Batmobile.I.Car();
        // Some puzzles have the car hold the winch while Batman walks off: wait for it to let go, then leave.
        if (car is { IsValid: true } && car.Ptr == forced && WinchBusy(car))
            return;

        _battleForcedCar = 0;
        if (car is not { IsValid: true } || car.Ptr != forced || car.Driver != null || car.bRemoteDrive)
            return;

        Safe.Run("battle any skin exit", () =>
        {
            if (!InState(car, BattleState))
                return;
            car.GotoState(new FName(PursuitState), FName.None, false, false);
            Log.Info("Battle mode on any skin: the player got out, so the car left battle mode (now " + car.GetStateName() + ").");
        });
    }

    /// <summary>Takes the car out of a battle mode Oracle put it in, when switched off or unloaded.</summary>
    private void LeaveForcedBattle(string reason)
    {
        nint forced = _battleForcedCar;
        _battleForcedCar = 0;
        if (forced != 0 && World.Pawn is RVehicleBatmobileBase car && car.Ptr == forced)
            LeaveBattle(car, reason);
    }

    private static void LeaveBattle(RVehicleBatmobileBase car, string reason)
    {
        Safe.Run("battle any skin leave", () =>
        {
            if (!InState(car, BattleState))
                return;

            // Let the game leave when it can (its own exit plays the transform back); otherwise switch the state.
            if (!car.TryToChangeMode(new FName(PursuitState)))
                car.GotoState(new FName(PursuitState), FName.None, false, false);
            Log.Info("Battle mode on any skin: left battle mode (" + reason + ", now " + car.GetStateName() + ").");
        });
    }

    /// <summary>
    /// Whether a key bound to ToggleBattleMode is down. Battle mode swaps in a key map without that binding, so the
    /// keys are learnt in pursuit mode and kept; PressedKeys lists every held key, bound or not.
    /// </summary>
    private bool BattleKeyHeld(RPlayerInput input, bool inBattle)
    {
        if (!inBattle && (input.Ptr != _battleKeysInput || Clock.Now - _battleKeysAt > BattleKeysRefresh))
        {
            HashSet<int> found = GameKeys.KeysFor(input, "ToggleBattleMode");
            if (found.Count > 0 || input.Ptr != _battleKeysInput)
                _battleKeys = found;
            _battleKeysInput = input.Ptr;
            _battleKeysAt = Clock.Now;
        }

        if (_battleKeys.Count == 0)
            return false;

        foreach (FName key in input.PressedKeys)
        {
            if (_battleKeys.Contains(key.Index))
            {
                _battleKeyName = key.ToString();
                return true;
            }
        }

        return false;
    }

    // Driving a car that can't strafe natively: Oracle moves it along the battle mode move direction.
    // The game's own battle speed (1200 on the Asylum car) felt very slow without the battle drive mode's push, so
    // Oracle runs at twice it (user, 2026-11-05). BATTLE MODE SPEED scales the sim value above 1.00x.
    private const float BattleDriveSpeed = 2400f;
    private const float BattleDriveSpeedScale = 2f;
    private const float BattleDriveAccel = 6000f;
    private const float BattleDriveBrake = 5000f;

    /// <summary>
    /// A car held on normal driving in battle mode gets no throttle: battle input goes into the sim's
    /// AbsoluteMoveDirection, which only the (crashing) battle drive mode reads. So Oracle steers its horizontal
    /// velocity toward that direction itself, like the strafing it stands in for, and brakes when the stick is let go.
    /// </summary>
    private void BattleDriveTick(float dt)
    {
        if (!_anySkinBattle.On || dt <= 0f || Menu.IsOpen || World.Pawn is not RVehicleBatmobileBase car || !InState(car, BattleState) || HasWheelControls(car))
            return;
        if (car.SimObj is not RVehicleSimBatmobile { IsValid: true } sim || car.CollisionComponent is not { IsValid: true } body || !car.HasWheelsOnGround())
            return;
        Vector3 move = sim.AbsoluteMoveDirection;
        move.Z = 0f;
        if (move.LengthSquared() < 0.0001f && World.Pc is { PlayerInput: { } input } pc)
        {
            // Fall back to the sticks, relative to the camera, the way the game's battle strafe check reads them.
            float yaw = pc.Rotation.Yaw * MathF.PI / 180f;
            var fwd = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
            var right = new Vector3(-fwd.Y, fwd.X, 0f);
            move = fwd * Math.Clamp(input.aBaseY, -1f, 1f) + right * Math.Clamp(input.aStrafe, -1f, 1f);
        }

        float amount = Math.Min(move.Length(), 1f);
        float maxSpeed = sim.BattleModeMaxSpeed is > 300f and < 8000f ? sim.BattleModeMaxSpeed * BattleDriveSpeedScale : BattleDriveSpeed;
        Vector3 flat = new(car.Velocity.X, car.Velocity.Y, 0f);
        Vector3 target = amount > 0.05f ? Vector3.Normalize(move) * maxSpeed * amount : Vector3.Zero;
        Vector3 change = target - flat;
        float step = (amount > 0.05f ? BattleDriveAccel : BattleDriveBrake) * dt;
        if (change.LengthSquared() < 1f)
            return;
        if (change.Length() > step)
            change = Vector3.Normalize(change) * step;

        // Drive the wheels along the nose too, reversing for backward moves: with no throttle the car brakes
        // against being pushed backwards. Sideways is left to the push.
        float nose = amount > 0.05f ? Vector3.Dot(Vector3.Normalize(move) * amount, car.Rotation.ToDirection() with { Z = 0f }) : 0f;
        car.Throttle = Math.Clamp(nose, -1f, 1f);

        body.WakeRigidBody(FName.None);
        body.SetRBLinearVelocity(change, true);
    }

    private static string Archetype(RVehicleBatmobileBase car) => car.ObjectArchetype?.GetPathName(null) ?? "?";

    /// <summary>
    /// Battle drive mode (strafing) moves the car through each wheel's RSkelControlBatmobileWheel, and the native code reads it
    /// without a null check once the car moves (exe 0x140067d2c, WheelControl+0x164). Skins whose meshes have no wheel
    /// controls (1989, Arkham Asylum; crashed 2026-11-05) only drive safely in BMDM_Drive, which skips that code.
    /// </summary>
    private static bool HasWheelControls(RVehicleBatmobileBase car)
    {
        int n = 0;
        foreach (SVehicleWheel wheel in car.Wheels)
        {
            if (wheel is not { IsValid: true } || !IsWheelControl(wheel.WheelControl))
                return false;
            n++;
        }

        return n > 0;
    }

    /// <summary>
    /// The native loop drops any control that isn't an RSkelControlBatmobileWheel (the class it looks up in BmGame), then
    /// reads it anyway. The 1966 and Arkham Asylum cars have plain SkelControlWheels and crashed 2026-11-05. Tested on
    /// the class chain the way the native does; read in C# because ClassIsChildOf is a static native.
    /// </summary>
    private static bool IsWheelControl(GameObject? control)
    {
        if (control is not { IsValid: true } || control.Class is not { } cls)
            return false;

        nint wanted = RSkelControlBatmobileWheel.StaticClass().Ptr;
        foreach (Struct s in cls.EnumerateSupersAndSelf())
        {
            if (s.Ptr == wanted)
                return true;
        }

        return false;
    }

    private static string WheelControls(RVehicleBatmobileBase car)
    {
        try
        {
            return string.Join("/", car.Wheels.Select(w => w is { IsValid: true } && w.WheelControl is { IsValid: true } c ? c.Class?.GetPathName(null) ?? "?" : "none"));
        }
        catch (Exception ex)
        {
            return "? (" + ex.GetType().Name + ")";
        }
    }

    /// <summary>In battle mode, puts a car without wheel controls back on normal driving. Returns true when it may strafe.</summary>
    private static bool KeepSafeDriveMode(RVehicleBatmobileBase car)
    {
        if (HasWheelControls(car))
            return true;

        if (car.SimObj is RVehicleSimBatmobile { IsValid: true } sim && sim.DriveMode == RVehicleSimBatmobile.EBatmobileDriveMode.BMDM_BattleMode)
            sim.SetDriveMode(RVehicleSimBatmobile.EBatmobileDriveMode.BMDM_Drive);
        return false;
    }

    private static string Has(GameObject? o) => o is { IsValid: true } ? "y" : "-";

    /// <summary>Once per car: the pieces battle mode drives, so a skin that crashes can be compared with one that works.</summary>
    private void LogBattleSkin(RVehicleBatmobileBase car)
    {
        if (_battleLoggedCar == car.Ptr)
            return;

        _battleLoggedCar = car.Ptr;
        Safe.Run("battle any skin report", () =>
        {
            var pitch = car.TurretPitchController;
            int pitchSet = 0;
            for (int i = 0; i < pitch.Count; i++)
            {
                if (pitch[i] is { IsValid: true })
                    pitchSet++;
            }

            Log.Info("Battle mode on any skin: " + Archetype(car) + " (" + (HasWheelControls(car) ? "wheel controls " : "no wheel controls ") + WheelControls(car) + "), state " + car.GetStateName()
                + ", ready " + car.IsReadyToTransform()
                + ", transform " + Has(car.TransformationAnimNode) + ", turret " + Has(car.TurretController) + ", pitch " + pitchSet + "/" + pitch.Count
                + ", armour blend " + Has(car.ArmourBlendNode) + ", cockpit " + Has(car.CockpitAnimNode) + ", air brakes " + Has(car.AirBrakeLeftNode) + Has(car.AirBrakeRightNode)
                + ", afterburner " + Has(car.AfterburnerBlendNode) + Has(car.AfterburnerAnimNode) + ", boost blend " + Has(car.BoostBlendNode)
                + ", battle collision " + Has(car.BatmanCollisionMeshBattleMode) + ", view mesh " + Has(car.BatmobileViewMesh)
                + ", winch " + Has(car.BatmobileWinch) + ", secondary " + Has(car.SecondaryWeapon)
                + ", battle gun " + (car.BattleModePrimaryWeapon?.GetPathName(null) ?? "none") + ".");
        });
    }
}
