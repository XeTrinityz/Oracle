using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class Progress : Module
{
    public static readonly Progress I = new();

    public override void Build()
    {
        AddSaveCommand("upgrade_points", "+10 UPGRADE POINTS", "Adds 10 points to spend on upgrades.", () => GivePoints(10));
        AddSaveCommand("unlock_upgrades", "UNLOCK ALL UPGRADES", "Unlocks every upgrade for Batman and the Batmobile.", () => World.RequireCheats().UnlockAllUpgrades(true));
        AddSaveCommand("riddler_map", "REVEAL RIDDLER MAP", "Reveals every Riddler trophy and puzzle on the map.", RevealRiddlerMap);
        AddSaveCommand("unlock_all", "UNLOCK EVERYTHING", "The game's own unlock-all cheat: upgrades, gadgets, extras and more.", () => World.RequireCheats().UnlockAll());
        Command achievements = AddSaveCommand("unlock_achievements", "UNLOCK ALL ACHIEVEMENTS", "Unlocks every achievement on your Steam account, DLC ones included. This is permanent: Oracle can't take them back.", UnlockAchievements);
        achievements.ConfirmPrompt = () => "UNLOCK EVERY STEAM ACHIEVEMENT FOR GOOD?";
        AddCommand("save_now", "SAVE GAME NOW", "Autosaves right now, as the game does at story points. Batman reloads at the nearest start point, not the exact spot. The game has no manual save, so this is the only way to keep where you are.", SaveNow);
    }

    // RCheatManager.AutoSaveGame calls RGameRI.AutoSaveAtNearestStartPoint, the same native autosave the story uses.
    private static void SaveNow()
    {
        RGameInfo game = World.Info ?? throw new InvalidOperationException("load into the game first");
        if (game.GetIsSaveInProgress())
            throw new InvalidOperationException("a save is already in progress");

        World.RequireCheats().AutoSaveGame();
        Log.Info("Asked the game to autosave.");
    }

    // RCheatManager.UnlockAllAchievements loops ids 0..130 through RPlayerController.UnlockAchievement, which skips
    // any already earned and sends the rest to Steam. Its float argument defaults to 100 (fully done) in script,
    // and the cheat passes it as an empty optional, so the script default applies; calling
    // RPlayerController.UnlockAchievement from C# directly would send 0 instead (see BmSDK optional parameters).
    private static void UnlockAchievements()
    {
        World.RequireCheats().UnlockAllAchievements();
        Log.Info("Asked the game to unlock every achievement.");
    }

    // RiddlerRevealItems walks the whole map item list and sets the Riddler_<item> reveal flag (and its _Shared copy)
    // for every Riddler item whose name contains MapName; empty means all of them. BmSDK passes 0 or empty for an
    // omitted argument rather than the script default, so the empty name is passed on purpose. The similar
    // RiddlerRevealItemsOnlyValid only covers the 999 nearest items on CityZ. GiveRiddlerMap adds the current
    // map's informant flag on top.
    private static void RevealRiddlerMap()
    {
        RCheatManager cheats = World.RequireCheats();
        cheats.RiddlerRevealItems(new FString(""));
        cheats.GiveRiddlerMap();
    }

    private static void GivePoints(int n)
    {
        RPersistentShared? ps = World.Info?.PersistentShared;
        if (ps is not { IsValid: true })
            throw new InvalidOperationException("no persistent data");
        ps.UpgradePointsAvailable += n;
    }
}

/// <summary>Adds NEW GAME ++ to the main menu, and brings back counter icons in New Game +.</summary>
internal sealed class NewGamePlus : Module
{
    public static readonly NewGamePlus I = new();

    private const int ButtonId = 79;
    private const string BootKey = "NgppAtBoot";
    private const string ButtonLabel = "NEW GAME ++";

    private Toggle _setting = null!;
    private bool _askLogged;

    // The main menu is built once, so the choice is fixed for a game session even if the setting changes.
    private static bool ThisSession => Shared.GetValue(BootKey, false);

    public override void Build()
    {
        _setting = AddToggle("new_game_plus_plus", "NEW GAME ++", "Adds NEW GAME ++ to the main menu once your save has a New Game + run: play New Game + again from the start. Restart the game after switching.").Setting();
    }

    public override void Started()
    {
        if (Shared.Get<object>(BootKey) == null)
            Shared.Set(BootKey, _setting.On);
    }

    public override void WorldChanged() => _askLogged = false;

    private void AddButton(RGFxMovieUI self)
    {
        if (ThisSession && self is RGFxMovieUI_MainMenu && self.IsNewGamePlusStarted())
        {
            self.UC_AddButton(ButtonId, new FString(ButtonLabel), true, false, false, new FString(""));
            Log.Info("Added NEW GAME ++ to the main menu.");
        }
    }

    private bool ForceIcons(RBMCombatManager self, bool shown)
    {
        bool forced = !shown && Combat.I.CounterIcons.On && self.DisableCounterIconSequenceAction is not { IsValid: true };
        if (!_askLogged)
        {
            _askLogged = true;
            Log.Detail("Counter icons: game says " + (shown ? "show" : "hide") + (forced ? ", Oracle shows them anyway." : "."));
        }

        return forced;
    }

    [Redirect(typeof(RGFxMovieUI), nameof(RGFxMovieUI.IsNewGamePlusUnlocked))]
    private static bool IsNewGamePlusUnlockedRedirect(RGFxMovieUI self)
    {
        bool unlocked = self.IsNewGamePlusUnlocked();
        if (unlocked)
            Safe.Run("NEW GAME ++ button", () => I.AddButton(self));
        return unlocked;
    }

    [Redirect(typeof(RGFxMovieUI_MainMenu), nameof(RGFxMovieUI_MainMenu.XI_OnClicked))]
    private static void MainMenuClickedRedirect(RGFxMovieUI_MainMenu self, int id)
    {
        if (id != ButtonId || !ThisSession)
        {
            self.XI_OnClicked(id);
            return;
        }

        Log.Info("NEW GAME ++ picked: starting a fresh New Game + run.");
        self.GotoNewGamePlus();
    }

    [Redirect(typeof(RBMCombatManager), nameof(RBMCombatManager.CanShowCounterIcons))]
    private static bool CanShowCounterIconsRedirect(RBMCombatManager self)
    {
        bool shown = self.CanShowCounterIcons();
        return shown || Safe.Try("counter icons", () => I.ForceIcons(self, shown));
    }
}
