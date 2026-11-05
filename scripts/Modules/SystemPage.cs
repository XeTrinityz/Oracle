using BmSDK.Engine;
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

        AddToggle("run_unfocused", "KEEP RUNNING IN BACKGROUND",
            "Stops the game pausing and opening the pause menu when you click or alt-tab to another window. Oracle's keys still only work while the game is in front.",
            on => { _runUnfocused = on; ApplyFocusPause(); }).Setting();

        BuildRadial();

        AddCommand("all_off", "TURN EVERYTHING OFF", "Switches every cheat off and puts the game back to normal.", Catalog.TurnAllOff).Setting();

        AddCredit("credit_original", "ZEIDCEZEY",
            "Created Oracle: the original mod, its menu and its first features (versions 1.0 to 2.0). Opens the original on Nexus Mods.",
            OriginalUrl);
        AddCredit("credit_trinity", "XETRINITYZ",
            "Trinity Edition: rebuilt Oracle from the ground up and added auto fight, the teleport pages, NPC control, the Batmobile tabs, Riddler tools, suits and more. Opens the project on GitHub.",
            ProjectUrl);
    }

    // The game ships bPauseOnLossOfFocus=TRUE in BmEngine.ini (the engine default is FALSE). The native focus-loss
    // handler reads it each time the window loses focus, so clearing it at runtime is enough; Held puts the
    // game's own value back when the toggle goes off or the mod unloads.
    private static readonly Held<_Engine, bool> FocusPause =
        new("pause on focus loss", e => e.bPauseOnLossOfFocus, (e, v) => e.bPauseOnLossOfFocus = v);

    private static bool _runUnfocused;
    private static Every _focusCheck;

    private static void ApplyFocusPause()
    {
        if (!_runUnfocused)
        {
            FocusPause.Restore();
            return;
        }

        if (World.GameEngine is { } engine)
            FocusPause.Set(engine, false);
    }

    // Settings apply before the engine object may be reachable, and a config reload could set the flag again.
    public override void Frame()
    {
        if (_runUnfocused && _focusCheck.Elapsed(1.0))
            ApplyFocusPause();
    }

    public override void Shutdown() => FocusPause.Restore();

    /// <summary>SYSTEM > RADIAL: the favourites wheel's keys, look and layout.</summary>
    private static void BuildRadial()
    {
        AddToggle("radial_enabled", "FAVOURITES RADIAL",
            "A wheel of your favourite features. Hold the radial key, point at a slot and let go to use it. Put any row on it with HOME (Y on a controller).",
            on => Radial.Enabled = on, on: true).Setting();
        AddCommand("radial_arrange", "ARRANGE RADIAL",
            "Opens the radial to rearrange it: pick a favourite up with ENTER and put it in another slot or page, or take it off with DELETE.",
            () => Radial.BeginArrange(null)).Setting();
        AddCommand("radial_clear", "CLEAR RADIAL", "Takes every favourite off the radial.", Favourites.ClearAll).Setting().Confirmed();

        AddChoice("radial_key", "RADIAL KEY", "Hold this to open the radial over the game. MOUSE 4 and 5 (the side buttons), Z, M and CAPS LOCK do nothing in the game by default; the middle button does.",
            Radial.KeyIds, Radial.KeyNames, Radial.SetKey).Setting();
        AddChoice("radial_mode", "RADIAL MODE",
            "HOLD: let go of the radial key to use the slot you're pointing at. TAP: the radial stays open until you press the key again, and a click uses a slot.",
            new[] { "hold", "tap" }, new[] { "HOLD", "TAP" }, i => Radial.HoldMode = i == 0).Setting();
        AddSlider("radial_mouse", "POINTER SPEED", "How far the mouse moves the radial's pointer. WASD and the arrow keys point too, and the number keys use a slot at once.",
            1f, 0.25f, 3f, 0.25f, 1f, Fmt.Times, v => Radial.MouseSpeed = v).Setting();
        AddChoice("radial_pad", "RADIAL BUTTON",
            "Hold this on a controller to open the radial, and point with either stick. R3 or L3 belongs to the radial while it's set here, so the game no longer sees that click.",
            Radial.PadIds, Radial.PadNames, i => Radial.PadButton = Radial.PadIds[Math.Clamp(i, 0, Radial.PadIds.Length - 1)])
            .Setting().Stored("gamepad", "radial_button");

        AddSlider("radial_slots", "SLOTS PER PAGE", "How many favourites fit on one page of the radial. Your favourites keep their order when it changes; past a page, they flow onto the next.",
            8f, Favourites.MinPerPage, Favourites.MaxPerPage, 1f, 8f, v => ((int)v).ToString(), v => Favourites.PerPage = Math.Clamp((int)v, Favourites.MinPerPage, Favourites.MaxPerPage)).Setting();
        AddSlider("radial_size", "RADIAL SIZE", "How large the radial is drawn.", 1f, 0.6f, 1.4f, 0.05f, 1f, Fmt.Percent, v => Radial.Size = v).Setting();
    }

    public const string OriginalUrl ="https://www.nexusmods.com/batmanarkhamknight/mods/3597";
    public const string ProjectUrl = "https://github.com/XeTrinityz/Oracle";

    /// <summary>A credits row: selecting it opens that person's page in the browser.</summary>
    private static void AddCredit(string id, string name, string help, string url) =>
        AddCommand(id, name, help, () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })).Setting();
}
