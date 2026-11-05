using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class Player : Module
{
    public static readonly Player I = new();

    public Toggle God = null!;

    public override void Build()
    {
        God = AddToggle("god_mode", "GOD MODE", "You take no damage and your health stays full. Works for every playable character.", ApplyGod);
        God.Maintain(() => ApplyGod(true));
        AddCommand("restore_health", "RESTORE HEALTH", "Refills your health right now.", () => World.RequireCheats().RestoreHealth());

        Slider speed = AddSlider("move_speed", "RUN SPEED", "Multiplies how fast you walk and run.", 1f, 0.5f, 3f, 0.05f, 1.6f, Fmt.Times, ApplyMoveSpeed);
        speed.Maintain(() => ApplyMoveSpeed(speed.Value));

        AddConsoleToggle("gadget_no_cooldown", "NO GADGET COOLDOWN", "Gadgets can be equipped and used again while they recharge.",
            new ConsoleProp("RInventoryGadget", "bCanEquipWhileRecharging", "True", "False"));
        AddConsoleToggle("gadget_anywhere", "GADGETS ANYWHERE", "Use gadgets from vents, corner cover, vantage points and silent predator.",
            new ConsoleProp("RInventoryGadget", "bUsableFromCrawlSpace", "True", "False"),
            new ConsoleProp("RInventoryGadget", "bUsableFromCornerCover", "True", "False"),
            new ConsoleProp("RInventoryGadget", "bUsableFromVantagePoints", "True", "False"),
            new ConsoleProp("RInventoryGadget", "bCanUseFromSilentPredator", "True", "False"));
        AddConsoleSlider("smoke_duration", "SMOKE PELLET DURATION", "How long Batman's smoke pellet cloud lasts.", "RSmokeScreenBm", "SmokeScreenDuration", 10f, 5f, 60f, 5f, 30f, Fmt.Secs);
        AddCommand("give_gadgets", "GIVE ALL GADGETS", "Adds every gadget to the gadget wheel.", () => World.RequireCheats().DebugGiveAllGadgets());

        AddConsoleToggle("silent_running", "SILENT RUNNING", "Your footsteps make no noise.", new ConsoleProp("RPawnPlayer", "bSilentRunning", "True", "False"));
        DroneInvisible = AddToggle("drone_invisible", "INVISIBLE TO DRONES", "Sentry guns, flying drones and helicopter searchlights can't see you. Thugs, and the small drones they pilot, still can.");
        ThugInvisible = AddToggle("thug_invisible", "INVISIBLE TO THUGS", "Thugs, gunmen and snipers can't see you, even right in front of them, and ones already hunting you lose you. Street thugs already fighting you break off and run, as they do while you interrogate someone. They can still hear noise, and story fights keep going.", on =>
        {
            if (!on)
                RestoreSight();
        });
    }

    // ---- invisible to drones
    // RPawnPlayer.bInvisibleToDrones does nothing: no script reads it and setting it changed nothing in game. What the
    // game really asks:
    // - Sentry guns: the native ARSentryGunBase::UpdatePlayerVis calls the event RPlayerController.ScriptIsHidden
    //   with no Checker before anything else, and a hidden player isn't seen at all. Thugs pass themselves as the
    //   Checker, so answering only the Checker-less call leaves them alone.
    // - Flying drones and helicopters (RHelicopterBase): the native vision asks the event IsPlayerInvisible, which is
    //   how the game hides you during conversations and door transitions. The small thug-piloted mini drones are the
    //   same class but spot you some other way, so they still see you.

    // ---- invisible to thugs
    // Thug sight is native: RBMAIController.CalcVisibilityFor and ProcessSpottableAlerts build each thug's alert on
    // you from his RBMPawnAI sight ranges (Instant, Focused, Peripheral, Extended). Answering IsHidden alone did
    // nothing in game (2026-11-05). So every tick each villain's ranges are held at 1 unit. The game rewrites them
    // itself (snipers' SetLongFOV/SetShortFOV, CanSeeTargetCombatPawn's temporary ExtendedSightRange), so any other
    // value seen is taken as the game's latest wish, remembered, and put back when the toggle goes off.
    //
    // RPlayerController.IsHidden (script: in a cinematic, or in a vent entered unseen) is still answered for thug
    // checkers, which covers the script-side checks (CanSeeTargetCombatPawn, RBMBehaviour_CombatAI.AwareOfPlayer,
    // AlertAndAddAttacker). Story fight starts ask with no Checker (RAEC_Attack_Base.ForceStartAttack) and are left alone.

    private const float BlindRange = 1f;

    private readonly Dictionary<nint, (RPawnVillain Pawn, float Instant, float Focused, float Peripheral, float Extended)> _sight = new();

    public override void Tick(float dt)
    {
        if (ThugInvisible is { On: true })
            Blind();
    }

    public override void WorldChanged()
    {
        _sight.Clear();
    }

    private void Blind()
    {
        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (v.bDeleteMe)
                continue;

            if (!_sight.TryGetValue(v.Ptr, out var s))
                s = (v, v.InstantSightRange, v.FocusedSightRange, v.PeripheralSightRange, v.ExtendedSightRange);

            s = (v, Wish(v.InstantSightRange, s.Instant), Wish(v.FocusedSightRange, s.Focused), Wish(v.PeripheralSightRange, s.Peripheral), Wish(v.ExtendedSightRange, s.Extended));
            _sight[v.Ptr] = s;

            v.InstantSightRange = BlindRange;
            v.FocusedSightRange = BlindRange;
            v.PeripheralSightRange = BlindRange;
            v.ExtendedSightRange = BlindRange;
            Forget(v);
        }
    }

    // A thug who already spotted you keeps an AlertInstance on you with a SightLevel that only decays slowly.
    // RBMRoomAIState.PreASyncGameInfoTick keeps the hunt on while any thug's level is above 0, and once every one
    // is at 0 it fires FireSightLostEvents (the room's Batman-lost Kismet event and bark), which sends them
    // searching. Zeroing the level each tick lets the game lose you its own way; TimeSinceSeen keeps counting up.
    // Scripted re-spots (thermal checks, shots at a gargoyle) that set the level to SpottedLimit are undone the same tick.
    private static void Forget(RPawnVillain v)
    {
        if (v.AIController is not { IsValid: true } ai || ai.FindAlertForPlayer() is not { IsValid: true } alert)
            return;

        if (alert.SightLevel > 0f)
            alert.SightLevel = 0f;
        if (alert.bVisible)
            alert.bVisible = false;
    }

    /// <summary>The value the game wants: what it just wrote, unless that is still Oracle's own.</summary>
    private static float Wish(float now, float remembered) => MathF.Abs(now - BlindRange) < 0.001f ? remembered : now;

    private void RestoreSight()
    {
        foreach (var s in _sight.Values)
        {
            RPawnVillain v = s.Pawn;
            if (!v.IsValid || v.bDeleteMe)
                continue;

            Safe.Run("thug sight restore", () =>
            {
                v.InstantSightRange = s.Instant;
                v.FocusedSightRange = s.Focused;
                v.PeripheralSightRange = s.Peripheral;
                v.ExtendedSightRange = s.Extended;
            });
        }

        _sight.Clear();
    }

    // The redirects can run before Build, so the toggles may still be null.
    public Toggle? DroneInvisible;
    public Toggle? ThugInvisible;

    private static bool Hidden => I.DroneInvisible is { On: true };

    private static bool HiddenFrom(RBMPawnAI checker) => checker is { IsValid: true } ? I.ThugInvisible is { On: true } : Hidden;

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.ScriptIsHidden))]
    private static bool ScriptIsHiddenRedirect(RPlayerController self, RBMPawnAI checker, bool combatCheck) =>
        HiddenFrom(checker) || self.ScriptIsHidden(checker, combatCheck);

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.IsHidden))]
    private static bool IsHiddenRedirect(RPlayerController self, RBMPawnAI checker, bool combatCheck, bool ignoreCinematicCheck) =>
        (I.ThugInvisible is { On: true } && checker is { IsValid: true }) || self.IsHidden(checker, combatCheck, ignoreCinematicCheck);

    // Ending a fight. Answering IsCurrentCombatantWhileTaunting with false (the "Batman got away" check the combat
    // manager polls) was tried and did nothing: it was asked ~90 times a second and thugs stayed in combat
    // (Oracle.log, 2026-11-05). What does break open-world thugs out is RBMBehaviour_Combat.Tick ->
    // WandererHandleSpookedBy: for militia, wanderers, rioters and car or riot spawns, a Batman interrogation
    // (InterrogationInProgress = player bInInterrogation) makes RioterWillBreak true and forces a flee even mid-move.
    // The behaviour then runs ExitCombat, InstantExitBehaviour(self) from its own state, and its run-away behaviour,
    // so the game does the state work. Story-only thugs aren't in those groups and keep fighting. Confirmed in game 2026-11-05.
    [Redirect(typeof(RBMBehaviour_Combat), nameof(RBMBehaviour_Combat.InterrogationInProgress))]
    private static bool InterrogationRedirect(RBMBehaviour_Combat self)
    {
        return I.ThugInvisible is { On: true } || self.InterrogationInProgress();
    }

    [Redirect(typeof(RHelicopterBase), nameof(RHelicopterBase.IsPlayerInvisible))]
    private static bool HelicopterInvisibleRedirect(RHelicopterBase self) => Hidden || self.IsPlayerInvisible();

    public override void Shutdown()
    {
        if (God.On)
            Safe.Run("god mode restore", () => ApplyGod(false));
        RestoreSight();
    }

    private static void ApplyGod(bool on)
    {
        RPlayerController? pc = World.Pc;
        if (pc != null && pc.bGodMode != on)
            pc.bGodMode = on;

        if (World.Cheats is { } cm && cm.bIgnoreDamage != on)
            cm.bIgnoreDamage = on;

        RPawnPlayer? p = on ? World.Player : null;
        if (p != null && p.Health > 0 && p.Health < p.HealthMax)
            p.Health = p.HealthMax;
    }

    private static void ApplyMoveSpeed(float v)
    {
        RPawnPlayer? p = World.Player;
        if (p != null && MathF.Abs(p.MovementSpeedModifier - v) > 0.001f)
            p.MovementSpeedModifier = v;
    }
}
