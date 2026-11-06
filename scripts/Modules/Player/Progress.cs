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
        AddSaveCommand("give_xp", "+5000 XP", "Adds 5000 experience, as a fight would. Enough of it levels you up and earns upgrade points.", () => GiveXp(5000));
        _xpScale = AddSlider("xp_multiplier", "XP MULTIPLIER", "Multiplies the experience you earn from fights, predator rooms, Batmobile battles and everything else.", 1f, 1f, 10f, 0.5f, 2f, Fmt.Times);
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

    // ---- experience
    // Every XP award goes through RExperienceSystem.AddUnbankedXP (fights, predator and Batmobile bonuses, XP
    // sequences, the AddXP cheat); BankXP later adds the unbanked total to RPersistentShared.Xp and levels you up.
    // XP also heals in combat (RPawnPlayer.ExperienceAwarded), so the multiplier heals more too.

    // The redirect can run before Build, so the slider may still be null.
    private Slider? _xpScale;
    private bool _granting;

    private int ScaleXp(int xp)
    {
        float scale = _xpScale?.Value ?? 1f;
        return _granting || xp <= 0 || scale <= 1.001f ? xp : (int)MathF.Round(xp * scale);
    }

    [Redirect(typeof(RExperienceSystem), nameof(RExperienceSystem.AddUnbankedXP))]
    private static void AddUnbankedXPRedirect(RExperienceSystem self, int xpAwarded, bool teeth)
    {
        int xp = xpAwarded;
        Safe.Run("xp multiplier", () => xp = I.ScaleXp(xpAwarded));
        self.AddUnbankedXP(xp, teeth);
    }

    // RCheatManager.AddXP adds unbanked XP and banks it at once. Its 500 default is script only, so pass the amount.
    private void GiveXp(int xp)
    {
        RCheatManager cheats = World.RequireCheats();
        _granting = true;
        try
        {
            cheats.AddXP(xp);
        }
        finally
        {
            _granting = false;
        }
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

    // START NEW GAME + NOW: set (as a UTC tick count) when Oracle quits to the main menu to start New Game + there.
    private const string PendingKey = "NgpPending";
    private const double PendingSeconds = 120;
    private const float MenuSettle = 1.5f;

    private Toggle _setting = null!;
    private float _menuSeen;
    private Every _menuLook;

    // The main menu is built once, so the choice is fixed for a game session even if the setting changes.
    private static bool ThisSession => Shared.GetValue(BootKey, false);

    public override void Build()
    {
        _setting = AddToggle("new_game_plus_plus", "NEW GAME ++", "Adds NEW GAME ++ to the main menu once your save has a New Game + run: play New Game + again from the start. Restart the game after switching.").Setting();

        Command now = AddCommand("ngp_start_now", "START NEW GAME + NOW",
            "Starts a fresh New Game + run straight from your game: Oracle keeps a copy of your save, quits to the main menu and starts New Game + there, as the menu's own button does. Any New Game + run in this slot starts over; the main story is untouched. Needs the story finished.",
            StartNow).Confirmed();
        now.ConfirmPrompt = () => Safe.Try("ng+ check", () => World.Ri?.PersistentOptions is { IsValid: true } po && po.NewGamePlusStarted != 0)
            ? "WIPE YOUR NEW GAME + RUN AND START OVER?"
            : "START NEW GAME +?";
    }

    // ---- START NEW GAME + NOW
    // The main menu starts New Game + with SetActiveStorySlotID(2), Live_InvalidateStoryData and a full
    // "start batentry" load (RGFxMovieUI_MainMenu.OnFadeCompleted_Callback, fade 7). Doing that over a live story
    // world risks carrying its state into the new run, so Oracle goes to the main menu and presses the game's
    // own confirmed button there: PopupType NewGamePlus, then PopupRequester_Callback(none, 1), exactly what
    // answering YES to "overwrite story progress?" runs.

    private static void StartNow()
    {
        if (!World.InGame)
            throw new InvalidOperationException("load into the game first");

        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        RGameInfo game = World.Info ?? throw new InvalidOperationException("the game is still loading");
        RPersistentOptions po = World.Ri?.PersistentOptions is { IsValid: true } o ? o : throw new InvalidOperationException("the save isn't loaded");
        if (po.NewGamePlusEnabled == 0)
            throw new InvalidOperationException("finish the story first to unlock New Game +");
        if (game.GetIsSaveInProgress() || Saves.I.Saving)
            throw new InvalidOperationException("the game is saving, try again in a moment");
        if (Characters.I.PlayingOther || Characters.I.Busy)
            throw new InvalidOperationException("switch back to Batman first");

        Saves.I.KeepCurrentSlot("before New Game +");
        Shared.Set(PendingKey, DateTime.UtcNow.Ticks);
        Log.Info("NEW GAME +: quitting to the main menu to start a fresh run there.");
        Menu.Close();
        pc.FE_ExitToFrontend();
    }

    public override void Frame()
    {
        long since = Shared.GetValue(PendingKey, 0L);
        if (since == 0)
            return;

        if ((DateTime.UtcNow - new DateTime(since, DateTimeKind.Utc)).TotalSeconds > PendingSeconds)
        {
            Shared.Set(PendingKey, null);
            Log.Info("NEW GAME +: the main menu never came up, nothing started.");
            Overlay.Notice("START NEW GAME + NOW", "THE MAIN MENU NEVER CAME UP", false);
            return;
        }

        if (!World.InFrontend)
        {
            _menuSeen = 0f;
            return;
        }

        if (!_menuLook.Elapsed(0.25))
            return;

        // Wait for the main menu to sit still with nothing in front of it, then press its New Game + button.
        RGFxMovieUI_MainMenu? menu = World.All<RGFxMovieUI_MainMenu>().FirstOrDefault(m => m.IsValid);
        if (menu == null || menu.bInputBlocked || menu.ActivePopup is { IsValid: true })
        {
            _menuSeen = 0f;
            return;
        }

        _menuSeen += 0.25f;
        if (_menuSeen < MenuSettle)
            return;

        Shared.Set(PendingKey, null);
        if (!menu.IsNewGamePlusUnlocked())
        {
            Log.Info("NEW GAME +: the main menu says New Game + isn't unlocked, nothing started.");
            Overlay.Notice("START NEW GAME + NOW", "NEW GAME + ISN'T UNLOCKED", false);
            return;
        }

        Log.Info("NEW GAME +: starting a fresh New Game + run from the main menu.");
        menu.PopupType = RGFxMovieUI_MainMenu.MMPopTypes.MMPopType_NewGamePlus;
        menu.PopupRequester_Callback(null!, 1);
    }

    public override void Started()
    {
        if (Shared.Get<object>(BootKey) == null)
            Shared.Set(BootKey, _setting.On);
    }

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
        return !shown && Combat.I.CounterIcons.On && self.DisableCounterIconSequenceAction is not { IsValid: true };
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
