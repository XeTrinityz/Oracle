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

    // The redirects can run before Build, so the toggle may still be null.
    public Toggle? DroneInvisible;

    private static bool Hidden => I.DroneInvisible is { On: true };

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.ScriptIsHidden))]
    private static bool ScriptIsHiddenRedirect(RPlayerController self, RBMPawnAI checker, bool combatCheck)
    {
        if (Hidden && checker is not { IsValid: true })
            return true;

        return self.ScriptIsHidden(checker, combatCheck);
    }

    [Redirect(typeof(RHelicopterBase), nameof(RHelicopterBase.IsPlayerInvisible))]
    private static bool HelicopterInvisibleRedirect(RHelicopterBase self) => Hidden || self.IsPlayerInvisible();

    public override void Shutdown()
    {
        if (God.On)
            Safe.Run("god mode restore", () => ApplyGod(false));
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
