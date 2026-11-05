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

    private Toggle _anySkinBattle = null!;
    private static Toggle _tryUnverified = null!;
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
    private bool _battleLastIn;

    private void BuildBattle()
    {
        _anySkinBattle = AddToggle("batmobile_battle_any_skin", "BATTLE MODE ON ANY SKIN",
            "Battle mode with its weapons, dodges and winch on skins the game won't let transform. The car doesn't change shape; the controls and weapons do. Only skins confirmed safe are switched (so far Batman v Superman); others say so and are left alone.",
            on => { if (!on) LeaveForcedBattle("switched off"); }).EveryTick(BattleTick);
        _tryUnverified = AddToggle("batmobile_battle_unverified", "TRY UNTESTED SKINS",
            "Lets BATTLE MODE ON ANY SKIN switch skins nobody has confirmed yet. One (the 1989 car) crashed the game a second after switching, so save first; skins that work can be added to the safe list.").Pref();
    }

    private static bool InState(Actor a, string state) => string.Equals(a.GetStateName().ToString(), state, StringComparison.OrdinalIgnoreCase);

    private void BattleTick()
    {
        if (Menu.IsOpen || World.Pc is not { } pc || pc.PlayerInput is not RPlayerInput input || World.Pawn is not RVehicleBatmobileBase car)
        {
            _battleHeld = false;
            _battlePending = false;
            return;
        }

        bool inBattle = InState(car, BattleState);
        bool wasInBattle = _battleLastCar == car.Ptr ? _battleLastIn : inBattle;
        _battleLastCar = car.Ptr;
        _battleLastIn = inBattle;

        bool toggle = input.bBatmobileBattleModeToggle;
        bool held = BattleKeyHeld(input, inBattle) || (!toggle && input.aLeftTrigger > 0.5f);
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
            LogBattleSkin(car);
            if (!Verified(car))
            {
                _battleForcedAt = Clock.Now;
                _battlePending = !toggle;
                Log.Throttled("battle any skin unverified", "Battle mode on any skin: " + Archetype(car) + " isn't confirmed safe in battle mode, so it was left alone.");
                Overlay.Notice("BATTLE MODE", "NOT SAFE ON THIS SKIN YET", false);
                return;
            }

            if (!CanForceBattle(car, out string why))
            {
                Log.Throttled("battle any skin refused", "Battle mode on any skin: not now (" + why + ").");
                _battlePending = !toggle;
                return;
            }

            _battleForcedAt = Clock.Now;
            _battleForcedCar = car.Ptr;
            car.GotoState(new FName(BattleState), FName.None, false, false);
            Log.Info("Battle mode on any skin: entered battle mode (now " + car.GetStateName() + ").");
        }
        else if (!toggle && _battleForcedCar != car.Ptr)
        {
            // Holding: the game leaves its own battle mode on release; only undo what Oracle started.
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

    private static bool CanForceBattle(RVehicleBatmobileBase car, out string why)
    {
        why = car.bBattleModeLocked ? "the story hasn't unlocked battle mode yet"
            : car.bControlledBySeqAct ? "a mission is driving the car"
            : car.MatineeControlRefCount != 0 ? "a cutscene has the car"
            : car.IsUpsideDown() ? "the car is upside down"
            : car.BatmobileWinch is { IsValid: true } winch && (InState(winch, "WinchLockedOn") || InState(winch, "Homing")) ? "the winch is busy"
            : "";
        return why.Length == 0;
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
                return true;
        }

        return false;
    }

    // Skins confirmed in game to run battle mode without a transform. The 1989 Burton car crashed the game about a
    // second after entering (2026-11-05, native access violation, cause not yet pinned), so skins stay out until tried.
    private static readonly string[] VerifiedSkins = { "Batmobile_Snyder_Archetype.Batmobile_Snyder" };

    private static string Archetype(RVehicleBatmobileBase car) => car.ObjectArchetype?.GetPathName(null) ?? "?";

    /// <summary>A skin with its own transform is the game's normal battle mode; anything else must be on the list.</summary>
    private static bool Verified(RVehicleBatmobileBase car) =>
        car.TransformationAnimNode is { IsValid: true } || VerifiedSkins.Contains(Archetype(car), StringComparer.OrdinalIgnoreCase) || _tryUnverified.On;

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

            Log.Info("Battle mode on any skin: " + Archetype(car) + " (" + (Verified(car) ? "verified" : "not verified") + "), state " + car.GetStateName()
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
