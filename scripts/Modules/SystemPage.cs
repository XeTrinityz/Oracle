using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>The SYSTEM page: how Oracle itself behaves.</summary>
internal sealed class SystemPage : Module
{
    public static readonly SystemPage I = new();

    public override void Build()
    {
        AddToggle("remember_cheats", "REMEMBER CHEATS", "Turns your cheats back on every time a save loads.", on => Settings.Remember = on, on: true).Setting();
        AddSlider("apply_delay", "DELAY AFTER LOADING", "Seconds to wait after a load before your saved cheats switch back on.", 4f, 0f, 15f, 0.5f, 4f,
            Fmt.Tenths, v => Settings.ApplyDelay = v).Setting();

        AddToggle("active_list", "ACTIVE CHEATS LIST", "Shows a small list of running cheats in the corner while you play.", on => Overlay.ShowRunningList = on).Setting();

        AddSlider("menu_size", "MENU SIZE", "How large the menu is drawn.", 0.75f, 0.5f, 1.5f, 0.05f, 0.75f, Fmt.Percent, v => Theme.MenuSize = v).Setting();
        Choice color = AddChoice("menu_color", "MENU COLOUR", "Colour of the Oracle menu, pop-ups and teleport screen. RAINBOW cycles through every colour.", Theme.Keys, Theme.Names, i => Theme.Index = i).Setting();
        color.Swatch = Theme.Swatch;
        AddSlider("rainbow_speed", "RAINBOW SPEED", "How fast the RAINBOW colour cycles. Only used when the menu colour is RAINBOW.", 1f, 0.25f, 3f, 0.25f, 1f, Fmt.Times, v => Theme.RainbowSpeed = v).Setting();
        AddChoice("menu_position", "MENU POSITION", "Which side of the screen the menu sits on. On the right it grows leftward as tabs and rows open.",
            new[] { "left", "centre", "right" }, new[] { "LEFT", "CENTRE", "RIGHT" }, i => MenuView.Position = i).Setting();
        AddToggle("menu_preview", "PREVIEW PAGES", "Shows a page's tabs and rows, dimmed, while you move through the page list, so you can see what's on a page before opening it.",
            on => MenuView.Preview = on).Setting();
        AddToggle("menu_effects", "MENU EFFECTS", "Animates the menu: titles decode, rows cascade in, light sweeps the panel edges, the selection pulses and changed rows flash.",
            on => MenuView.Effects = on, on: true).Setting();
        AddToggle("low_end_mode", "LOW-END MODE", "Lighter menu for slower PCs: the rainbow steps 15 times a second and the menu draws fewer effects.", on => Theme.LowEnd = on).Setting();

        AddToggle("pad_enabled", "CONTROLLER SUPPORT", "Use an Xbox controller in the menu: hold the open button to open it, D-pad or left stick to move, A to pick, X to reset, LB / RB for pages, B to close.", on => Pad.Enabled = on, on: true)
            .Setting().Stored("gamepad", "enabled");
        AddSlider("pad_hold", "HOLD TO OPEN", "Seconds to hold the controller's open button (set in settings.toml, BACK by default). A shorter tap on BACK still opens the map.", 0.5f, 0f, 1.5f, 0.1f, 0.5f,
            v => v <= 0f ? "PRESS" : Fmt.Tenths(v), v => Pad.OpenHold = v).Setting().Stored("gamepad", "open_hold");

        AddCommand("all_off", "TURN EVERYTHING OFF", "Switches every cheat off and puts the game back to normal.", Catalog.TurnAllOff).Setting();

        AddCredit("credit_original", "ZEIDCEZEY",
            "Created Oracle: the original mod, its menu and its first features (versions 1.0 to 2.0). Opens the original on Nexus Mods.",
            OriginalUrl);
        AddCredit("credit_trinity", "XETRINITYZ",
            "Trinity Edition: rebuilt Oracle from the ground up and added auto fight, the teleport pages, NPC control, the Batmobile tabs, Riddler tools, suits and more. Opens the project on GitHub.",
            ProjectUrl);
    }

    public const string OriginalUrl = "https://www.nexusmods.com/batmanarkhamknight/mods/3597";
    public const string ProjectUrl = "https://github.com/XeTrinityz/Oracle";

    /// <summary>A credits row: selecting it opens that person's page in the browser.</summary>
    private static void AddCredit(string id, string name, string help, string url) =>
        AddCommand(id, name, help, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })).Setting();
}
