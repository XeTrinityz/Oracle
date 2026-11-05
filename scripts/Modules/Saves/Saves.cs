using System.Globalization;
using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SYSTEM > SAVES: browse every save on disk (the slot's three rotating files, the game's hidden backups and
/// Oracle's kept copies), load any of them in game, and keep copies the game will never rotate away.
///
/// Loading works the way the game's own slot screen does it: the file's bytes go to
/// RPlayerController.SetupActiveSave(slot, file number, data), which makes them the active save, and the pause
/// menu's restart (RGameInfo.Game_Restart, a "LoadGame" console command) then loads the active save.
/// </summary>
internal sealed class Saves : Module
{
    public static readonly Saves I = new();

    private const string SlotKey = "SaveSlot";
    private const float KeepWait = 30f;
    private const float KeepPoll = 0.5f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string PickHelp = "Every save Oracle can find, newest first. Hold left or right to scroll. Details of the selected save show here.";

    private Choice _show = null!;
    private Choice _story = null!;
    private int _listStory;
    private Choice _pick = null!;
    private List<SaveFiles.Entry> _all = new();
    private List<SaveFiles.Entry> _shown = new();
    private readonly Dictionary<int, string> _places = new();
    private Dictionary<string, string>? _placeNames;
    private bool _slotLogged;

    // SAVE NOW AND KEEP A COPY: waiting for the autosave to land on disk.
    private float _keepLeft;
    private float _keepPoll;
    private DateTime _keepAfterUtc;
    private long _keepSize = -1;

    public override void Build()
    {
        _show = AddChoice("save_show", "SHOW", "THIS SLOT lists the slot you're playing (or last played), SLOT 1, SLOT 2... list one slot each, ALL SLOTS lists every slot and KEPT COPIES only the copies Oracle has kept.",
            new[] { "slot", "all", "kept" }, new[] { "THIS SLOT", "ALL SLOTS", "KEPT COPIES" }, _ => Filter()).Setting();
        _story = AddChoice("save_story", "STORY",
            "Every save holds the main story and any New Game + run, as the main menu's CONTINUE STORY and CONTINUE STORY+ show. This picks which one LOAD THIS SAVE resumes. CURRENT keeps the one you're playing; NEW GAME + lists only saves that have a New Game + run.",
            new[] { "current", "main", "plus" }, new[] { "CURRENT", "MAIN STORY", "NEW GAME +" }, _ => Filter()).Setting();
        _pick = AddChoice("save_pick", "SAVE", PickHelp, new[] { "" }, new[] { "OPEN THE MENU TO LIST" }, _ => Describe()).Setting().Unsaved();
        _pick.HoldToScroll = true;
        AddCommand("save_refresh", "REFRESH LIST", "Reads the save folders again. The list also refreshes every time the menu opens.", Refresh).Setting();

        Command load = AddCommand("save_load", "LOAD THIS SAVE",
            "Restarts from the selected save, as the pause menu restarts from a checkpoint. The save you're leaving is kept first, so nothing is lost. The game carries on saving into the save's slot.",
            Load).Setting().Confirmed();
        load.ConfirmPrompt = () => Selected is { } e ? "LOAD " + Short(e) + (TargetStory() == PlusStory ? " IN NEW GAME +" : "") + "?" : "LOAD THIS SAVE?";
        AddCommand("save_keep", "KEEP A COPY", "Copies the selected save into Oracle's saves folder. The game never rotates or deletes those, so it stays loadable for good.", KeepSelected).Setting();
        Command delete = AddCommand("save_delete", "DELETE KEPT COPY", "Deletes the selected save if it's one of Oracle's kept copies. The game's own files and backups are never deleted.", DeleteSelected)
            .Setting().Confirmed();
        delete.ConfirmPrompt = () => Selected is { } e ? "DELETE KEPT " + Short(e) + "?" : "DELETE KEPT COPY?";

        AddCommand("save_now_keep", "SAVE NOW AND KEEP A COPY",
            "Autosaves right now, then keeps a copy of that save once the game has written it: a save you can always come back to. Batman reloads at the nearest start point.",
            SaveAndKeep);
        AddCommand("save_restart", "RESTART FROM CHECKPOINT", "Reloads the last save, as the pause menu's restart does. Handy on a hotkey.", Restart);

        AddCommand("save_open_kept", "OPEN KEPT COPIES FOLDER", "Opens Oracle's saves folder in Windows Explorer.", () => OpenFolder(SaveFiles.KeptDir)).Setting();
        AddCommand("save_open_backups", "OPEN GAME BACKUPS FOLDER", "Opens the folder where the game backs up each save, in Documents\\WB Games.", () => OpenFolder(SaveFiles.BackupDir)).Setting();
    }

    public override void Started() => Safe.Run("save folders", SaveFiles.Locate);

    public override void MenuOpening() => Refresh();

    public override void WorldChanged() => _slotLogged = false;

    // Quitting or loading abandons any autosave still being waited on, so stop waiting rather than report a failure later.
    public override void LeftGame() => _keepLeft = 0f;

    /// <summary>True while SAVE NOW AND KEEP A COPY waits for its autosave to land.</summary>
    public bool Saving => _keepLeft > 0f;

    private SaveFiles.Entry? Selected
    {
        get
        {
            string key = _pick.CurrentKey;
            return key.Length == 0 ? null : _shown.Find(e => e.Path == key);
        }
    }

    // ---- the list

    private void Refresh()
    {
        _all = SaveFiles.Scan();
        ShowOptions();
        Filter();
    }

    /// <summary>
    /// THIS SLOT, then one option per slot that has a save ("slot1" is the game's slot 0), then ALL SLOTS and
    /// KEPT COPIES. A saved pick of a slot with no files falls back to THIS SLOT but is kept for when it returns.
    /// </summary>
    private void ShowOptions()
    {
        int active = ActiveSlot();
        var keys = new List<string> { "slot" };
        var labels = new List<string> { active >= 0 ? "THIS SLOT (" + (active + 1) + ")" : "THIS SLOT" };
        foreach (int slot in _all.Select(e => e.Slot).Distinct().Order())
        {
            // The slot's newest save gives its completion, as the game's slot screen shows it.
            SaveFiles.Entry newest = _all.Find(e => e.Slot == slot)!;
            keys.Add("slot" + (slot + 1));
            labels.Add("SLOT " + (slot + 1) + (newest.Percent >= 0 ? ", " + newest.Percent + "%" : ""));
        }

        keys.Add("all");
        labels.Add("ALL SLOTS");
        keys.Add("kept");
        labels.Add("KEPT COPIES");
        _show.SetOptions(keys.ToArray(), labels.ToArray());

        int current = CurrentStory();
        string now = current == PlusStory ? "CURRENT (NEW GAME +)" : current == MainStory ? "CURRENT (MAIN STORY)" : "CURRENT";
        _story.SetOptions(_story.Keys, new[] { now, "MAIN STORY", "NEW GAME +" }, _story.Index);
    }

    // RSaveGameManager.StorySlots: the main story is 0, New Game + is 2 (3 to 8 are the Arkham Episodes).
    private const int MainStory = 0;
    private const int PlusStory = 2;

    /// <summary>The story being played, or -1 outside a game.</summary>
    private static int CurrentStory()
    {
        int id = -1;
        Safe.Run("story slot", () =>
        {
            if (World.InGame && World.Ri?.SaveGameManager is { IsValid: true } sgm)
                id = (int)sgm.GetActiveStorySlotID();
        });
        return id;
    }

    /// <summary>The story a load resumes: the STORY pick, or the one being played.</summary>
    private int TargetStory() => _story.CurrentKey switch
    {
        "main" => MainStory,
        "plus" => PlusStory,
        _ => CurrentStory(),
    };

    private void Filter()
    {
        string show = _show.CurrentKey;
        int slot = show == "slot" ? ActiveSlot() : show.StartsWith("slot", StringComparison.Ordinal) && int.TryParse(show.AsSpan(4), out int n) ? n - 1 : -1;
        _shown = show switch
        {
            "kept" => _all.FindAll(e => e.Kind == SaveFiles.Kind.Kept),
            _ when slot >= 0 => _all.FindAll(e => e.Slot == slot),
            _ => new List<SaveFiles.Entry>(_all),
        };

        _listStory = TargetStory();
        if (_listStory == PlusStory)
            _shown.RemoveAll(e => !e.HasPlus);

        string was = _pick.CurrentKey;
        if (_shown.Count == 0)
        {
            _pick.SetOptions(new[] { "" }, new[] { _listStory == PlusStory ? "NO SAVES WITH NEW GAME +" : _show.CurrentKey == "kept" ? "NO KEPT COPIES YET" : "NO SAVES FOUND" });
        }
        else
        {
            _pick.Saved = was;
            _pick.SetOptions(_shown.Select(e => e.Path).ToArray(), _shown.Select(Label).ToArray());
        }

        Describe();
    }

    /// <summary>"5 NOV 20:08, SLOT 1, 87%, LATEST": when first, so the list reads as a timeline.</summary>
    private string Label(SaveFiles.Entry e)
    {
        string text = Date(e.When) + ", SLOT " + (e.Slot + 1);
        if (_listStory == PlusStory)
            text += ", NG+ " + (int)e.StoryPlus + "%";
        else if (e.Percent >= 0)
            text += ", " + e.Percent + "%";
        return text + ", " + Tag(e);
    }

    private string Tag(SaveFiles.Entry e) => e.Kind switch
    {
        SaveFiles.Kind.Kept => "KEPT",
        SaveFiles.Kind.Backup => "BACKUP",
        _ => IsLatest(e) ? "LATEST" : "OLDER",
    };

    private bool IsLatest(SaveFiles.Entry e) =>
        e.Kind == SaveFiles.Kind.Current && !_all.Exists(o => o.Kind == SaveFiles.Kind.Current && o.Slot == e.Slot && o.When > e.When);

    private static string Date(DateTime t) =>
        t.ToString(t.Year == DateTime.Now.Year ? "d MMM HH:mm" : "d MMM yyyy HH:mm", Inv).ToUpperInvariant();

    private static string Short(SaveFiles.Entry e) => "SLOT " + (e.Slot + 1) + " FROM " + Date(e.When);

    /// <summary>Puts the selected save's details in the row's help, which the menu shows under the list.</summary>
    private void Describe()
    {
        if (Selected is not { } e)
        {
            _pick.Help = PickHelp;
            return;
        }

        if (!e.Valid)
        {
            _pick.Help = "This file isn't a save this version of the game can read, so it can't be loaded.";
            return;
        }

        var what = new List<string> { e.When.ToString("dddd d MMMM yyyy, HH:mm:ss", Inv) };
        string place = PlaceName(e.LocationId);
        if (place.Length > 0)
            what.Add(place);
        if (e.Difficulty.Length > 0)
            what.Add(e.Difficulty.ToUpperInvariant());
        if (e.Percent >= 0)
            what.Add(e.Percent + "% COMPLETE");
        if (e.HasPlus)
            what.Add("NEW GAME + " + (int)e.StoryPlus + "%");

        string origin = e.Kind switch
        {
            SaveFiles.Kind.Kept => "A copy Oracle kept. ",
            SaveFiles.Kind.Backup => "A backup the game made of file " + (e.XId + 1) + ". ",
            _ => IsLatest(e) ? "The slot's newest file: loading it is the same as restarting from the checkpoint. " : "One of the three files the game rotates through, older than the slot's newest. ",
        };

        _pick.Help = "SLOT " + (e.Slot + 1) + ", saved " + string.Join(", ", what) + ". " + origin + "Hold left or right to scroll.";
    }

    /// <summary>The location the game's slot screen shows, through its own id table and the GFxUI [Map] text.</summary>
    private string PlaceName(int id)
    {
        if (id < 0)
            return "";
        if (_places.TryGetValue(id, out string? known))
            return known;

        string name = "";
        Safe.Run("save location", () =>
        {
            RSaveGameManager? sgm = World.Ri?.SaveGameManager;
            if (sgm is not { IsValid: true })
                return;

            string key = sgm.GetStringForLocationID(id).ToString() ?? "";
            if (key.Length == 0)
                return;

            _placeNames ??= MapPlaces.Names(Path.GetFullPath(Path.Combine(App.Dir, "..", "..", "Localization", "INT", "GFxUI.int")), "Map");
            name = _placeNames.TryGetValue(key, out string? text) ? text : key.ToUpperInvariant();
            _places[id] = name;
        });
        return name;
    }

    // ---- which slot is in play

    /// <summary>
    /// The slot being played. The live options carry the loaded save's timestamp (the slot screen reads it from
    /// there), so the file with the same stamp is the one in play. Otherwise the slot Oracle last loaded, otherwise
    /// the slot with the newest file. -1 when there are no saves.
    /// </summary>
    private int ActiveSlot()
    {
        long live = 0;
        Safe.Run("save stamp", () =>
        {
            if (World.InGame && World.Ri?.PersistentOptions is { IsValid: true } po)
                live = (long)po.TimeStamp.Uid;
        });

        SaveFiles.Entry? match = live != 0 ? _all.Find(e => e.Stamp == live) : null;
        if (match != null)
        {
            Shared.Set(SlotKey, match.Slot);
            LogMatch(match);
            return match.Slot;
        }

        if (World.InGame && !_slotLogged && _all.Count > 0)
        {
            _slotLogged = true;
            Safe.Run("save check", () =>
            {
                RPersistentOptions? po = World.Ri?.PersistentOptions;
                Log.Detail("Saves: no file matches the game's save stamp " + live + (live != 0 ? " (" + DateTime.FromFileTimeUtc(live).ToLocalTime().ToString("u", Inv) + ")" : "")
                    + "; options " + (po is { IsValid: true } ? po.PercentageCompleted + "%, location " + po.LocationID + ", " + po.Day + "/" + po.Month + "/" + po.Year : "unavailable")
                    + ". Newest file: " + Path.GetFileName(_all[0].Path) + " stamp " + _all[0].Stamp + ".");
            });
        }

        int remembered = Shared.GetValue(SlotKey, -1);
        if (remembered >= 0)
            return remembered;

        return _all.Find(e => e.Kind == SaveFiles.Kind.Current)?.Slot ?? -1;
    }

    // Once per world: checks the header reading against what the game itself holds for the loaded save.
    private void LogMatch(SaveFiles.Entry e)
    {
        if (_slotLogged)
            return;
        _slotLogged = true;

        Safe.Run("save check", () =>
        {
            if (World.Ri?.PersistentOptions is not { IsValid: true } po)
                return;
            Log.Detail("Saves: playing slot " + (e.Slot + 1) + " (" + Path.GetFileName(e.Path) + "). File says " + e.Percent + "%, location " + e.LocationId + ", " + e.Difficulty
                + "; the game says " + po.PercentageCompleted + "%, location " + po.LocationID + ", " + po.DifficultyString + ".");
        });
    }

    // ---- commands

    private void Load()
    {
        SaveFiles.Entry e = Selected ?? throw new InvalidOperationException("pick a save first");
        if (!e.Valid)
            throw new InvalidOperationException("that file isn't a readable save");
        if (!World.InGame)
            throw new InvalidOperationException("load into the game first, then pick the save");

        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        RGameInfo game = World.Info ?? throw new InvalidOperationException("the game is still loading");
        if (game.GetIsSaveInProgress() || Saving)
            throw new InvalidOperationException("the game is saving, try again in a moment");
        if (Characters.I.PlayingOther || Characters.I.Busy)
            throw new InvalidOperationException("switch back to Batman first");

        int story = TargetStory(), now = CurrentStory();
        if (story == PlusStory && !e.HasPlus)
            throw new InvalidOperationException("this save has no New Game + run");
        RSaveGameManager sgm = World.Ri?.SaveGameManager is { IsValid: true } m ? m : throw new InvalidOperationException("the save system isn't ready");

        byte[] data = SaveFiles.ReadAll(e.Path);
        KeepCurrentSlot("before loading");

        TArray<byte> arr = SaveFiles.ToGameArray(data);
        try
        {
            pc.SetupActiveSave(e.Slot, e.XId, arr);
        }
        finally
        {
            arr.Dispose();
        }

        // Switching story is what the main menu's CONTINUE STORY / CONTINUE STORY+ do before loading
        // (RGFxMovieUI_MainMenu.GotoContinue2 / GotoContinuePlus2): pick the story slot, then unpack the save for it.
        // Staying in the same story keeps the plain path, which works as it is.
        if (story >= 0 && story != now)
        {
            sgm.SetActiveStorySlotID((RSaveGameManager.StorySlots)story);
            sgm.DecompressSavedGame(false);
        }

        Shared.Set(SlotKey, e.Slot);
        Log.Info("Saves: loading " + e.Path + " (" + data.Length + " bytes) as slot " + (e.Slot + 1) + " file " + (e.XId + 1) + ", story " + story + (story != now ? " (was " + now + ")" : "")
            + ", saved " + e.When.ToString("u", Inv) + ".");
        Menu.Close();
        game.Game_Restart();
    }

    /// <summary>Keeps the newest file of the slot being left, so loading an older save can't cost any progress.</summary>
    /// <summary>
    /// Keeps the newest file of the slot in play, so a load or a New Game + start can't cost any progress. Reads
    /// the folders first, since the list is only filled when the menu opens. Never throws.
    /// </summary>
    public void KeepCurrentSlot(string why)
    {
        Safe.Run("keep " + why, () =>
        {
            _all = SaveFiles.Scan();
            int slot = ActiveSlot();
            SaveFiles.Entry? newest = _all.Find(o => o.Kind == SaveFiles.Kind.Current && o.Slot == slot && o.Valid);
            if (newest != null && SaveFiles.Keep(newest, out string dest))
                Log.Info("Saves: kept " + dest + " " + why + ".");
        });
    }

    private void KeepSelected()
    {
        SaveFiles.Entry e = Selected ?? throw new InvalidOperationException("pick a save first");
        if (e.Kind == SaveFiles.Kind.Kept)
            throw new InvalidOperationException("that save is already a kept copy");

        bool made = SaveFiles.Keep(e, out string dest);
        Log.Info("Saves: " + (made ? "kept " : "already kept ") + dest + ".");
        Overlay.Notice("KEEP A COPY", made ? "KEPT " + Short(e) : "ALREADY KEPT", true);
        Refresh();
    }

    private void DeleteSelected()
    {
        SaveFiles.Entry e = Selected ?? throw new InvalidOperationException("pick a save first");
        if (e.Kind != SaveFiles.Kind.Kept)
            throw new InvalidOperationException("only Oracle's kept copies can be deleted here");

        File.Delete(e.Path);
        Log.Info("Saves: deleted " + e.Path + ".");
        Refresh();
    }

    private static void Restart()
    {
        RGameInfo game = World.Info ?? throw new InvalidOperationException("load into the game first");
        if (game.GetIsSaveInProgress() || I.Saving)
            throw new InvalidOperationException("the game is saving, try again in a moment");
        Menu.Close();
        game.Game_Restart();
    }

    private void SaveAndKeep()
    {
        if (_keepLeft > 0f)
            throw new InvalidOperationException("still waiting for the last save");
        if (SaveFiles.RemoteDir.Length == 0)
            throw new InvalidOperationException("the game's save folder wasn't found");

        RGameInfo game = World.Info ?? throw new InvalidOperationException("load into the game first");
        if (game.GetIsSaveInProgress())
            throw new InvalidOperationException("a save is already in progress");

        // File times are only as fine as the file system's, so allow a second's slack.
        _keepAfterUtc = DateTime.UtcNow.AddSeconds(-1);
        _keepLeft = KeepWait;
        _keepPoll = KeepPoll;
        _keepSize = -1;
        World.RequireCheats().AutoSaveGame();
        Log.Info("Saves: autosaving, a copy is kept once it's written.");
    }

    public override void Tick(float dt)
    {
        if (_keepLeft <= 0f)
            return;

        _keepLeft -= dt;
        _keepPoll -= dt;
        if (_keepPoll > 0f && _keepLeft > 0f)
            return;
        _keepPoll = KeepPoll;

        FileInfo? written = NewWrite();
        if (written != null && World.Info is { } game && !game.GetIsSaveInProgress())
        {
            // Copy only once the size holds still across two polls, so a file mid-write isn't kept.
            if (written.Length == _keepSize)
            {
                _keepLeft = 0f;
                Refresh();
                SaveFiles.Entry? e = _all.Find(o => o.Path.Equals(written.FullName, StringComparison.OrdinalIgnoreCase));
                if (e is { Valid: true } && SaveFiles.Keep(e, out string dest))
                {
                    Log.Info("Saves: kept " + dest + ".");
                    Overlay.Notice("SAVE NOW AND KEEP A COPY", "KEPT " + Short(e), true);
                    Refresh();
                }
                else
                {
                    Overlay.Notice("SAVE NOW AND KEEP A COPY", "SAVED, BUT THE COPY FAILED", false);
                }

                return;
            }

            _keepSize = written.Length;
        }

        if (_keepLeft <= 0f)
        {
            Log.Info("Saves: no save was written within " + KeepWait + " s, nothing kept.");
            Overlay.Notice("SAVE NOW AND KEEP A COPY", "THE GAME DIDN'T SAVE", false);
        }
    }

    private FileInfo? NewWrite()
    {
        try
        {
            return new DirectoryInfo(SaveFiles.RemoteDir).EnumerateFiles("BAK1Save*.sgd")
                .Where(f => f.LastWriteTimeUtc >= _keepAfterUtc)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Throttled("save watch", "Saves: could not read the save folder: " + ex.Message);
            return null;
        }
    }

    private static void OpenFolder(string dir)
    {
        if (dir.Length == 0)
            throw new InvalidOperationException("folder not found");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }
}
