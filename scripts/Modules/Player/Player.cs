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
        AddConsoleToggle("drone_invisible", "INVISIBLE TO DRONES", "Drones and sentry guns can't see you.", new ConsoleProp("RPawnPlayer", "bInvisibleToDrones", "True", "False"));
    }

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
