using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static BmSDK.BmGame.RGameInfo;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Finishes Riddler items anywhere in the city, loaded or not. A real pickup only records named flags and
/// updates the puzzle grid, so doing the same from BmGame.ini's item list needs no actor. Follows
/// RPickup_Riddler.PickedUp and RRiddleSolution.RiddleFound, minus their messages and sounds.
/// </summary>
internal sealed class Riddler : Module
{
    public static readonly Riddler I = new();

    private const string Title = "RIDDLER";
    private const string PickedUpPrefix = "PickedUp_";
    private const string RevealedPrefix = "Riddler_";
    private const string Trophy = "RiddlerPickup";
    private const string Riddle = "RiddlerCameo";
    private const string Bomb = "RiddlerBomb";
    private const string Breakable = "RiddlerBreakable";

    // Trophies from 100 up are the other spot of a trophy that moves. They have flags but no grid piece.
    private const int FirstAlternateId = 100;

    // RiddlerPuzzleSetPieceStateByIndex treats this as every piece of the puzzle.
    private const byte AllPieces = 255;

    // RiddlerPuzzleGetPieceStateCount's script default for "any puzzle". BmSDK passes 0 for an omitted
    // optional argument instead of the script default, which counts only puzzle 0 and so finds nothing.
    private const byte AnyPuzzle = 255;

    // Solved from the Batmobile, so RVehicleBatmobileBase hard-codes it and the map list leaves it out.
    private const string BatmobileRiddle = "CityY_Riddler_23";

    private static readonly Dictionary<string, ERiddlerLocationName> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CityX"] = ERiddlerLocationName.RiddlerLoc_CityX,
        ["CityY"] = ERiddlerLocationName.RiddlerLoc_CityY,
        ["CityZ"] = ERiddlerLocationName.RiddlerLoc_CityZ,
        ["Film"] = ERiddlerLocationName.RiddlerLoc_Film,
        ["Stagg"] = ERiddlerLocationName.RiddlerLoc_Stagg,
        ["HideOut"] = ERiddlerLocationName.RiddlerLoc_HideOut,
    };

    private static readonly Dictionary<string, ERiddlerCollection> Collections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MilitiaShield"] = ERiddlerCollection.RiddlerCollection_Shields,
        ["JackInTheBox"] = ERiddlerCollection.RiddlerCollection_Jacks,
        ["MiniDrone"] = ERiddlerCollection.RiddlerCollection_SpiderBots,
        ["InsectCrate"] = ERiddlerCollection.RiddlerCollection_Insects,
    };

    public override void Build()
    {
        AddSaveCommand("riddler_finale", "SKIP TO RIDDLER FINALE", "Collects all 243 trophies, riddles, bombs and breakables, then plays out every Riddler challenge, race and the first mech fight through the game's own script, leaving only the final battle. Needs story chapter 5 or later, when the GCPD can take Riddler. Run it outside in the city.", StartFinale);
    }

    // ---- skip to the finale
    // Riddler's Revenge lives in the city's Riddler_Unlock Kismet. Each challenge has an unlock event (marker,
    // progress text) and a completion event (percentage, marker removed), all guarded by their own flags, so
    // firing them in story order replays the mission exactly. One step per interval lets each chain finish.

    private const float FinaleStepSeconds = 0.5f;
    private const string FinalFightEvent = "SS_Riddler_FinalMechFight(External)";

    // (unlock event, completion event) in the order the game reveals them (progress letters B to L).
    private static readonly (string Unlock, string Done)[] Challenges =
    {
        ("Orphan_Unlock_Map_Orphan_C6", "Orphan_A1_Cave_C6_Done"),
        ("Orphan_Unlock_Map_Orphan_C2", "Orphan_A1_Cave_C2_Done"),
        ("Orphan_Unlock_Map_Orphan_01", "KeyCollected_in_01"),
        ("Orphan_Unlock_Map_Orphan_C3", "Orphan_A1_Cave_C3_Done"),
        ("Orphan_Unlock_Map_Orphan_C8", "Orphan_A1_Cave_C8_Done"),
        ("Orphan_Unlock_Map_Orphan_04", "Orphan_04_Finished"),
        ("Orphan_Unlock_Map_Orphan_C4", "Orphan_A1_Cave_C4_Done"),
        ("Orphan_Unlock_Map_Orphan_09", "NRiddler_09_Done"),
        ("Orphan_Unlock_Map_Orphan_C7", "Orphan_A1_Cave_C7_Done"),
        ("Orphan_Unlock_Map_Orphan_15", "NRiddler_3A_Key_Collar_Done"),
        ("Orphan_Unlock_Map_Orphan_C1", "Orphan_A1_Cave_C1_Done"),
    };

    // Set inside the challenge levels themselves, which the city script never sees.
    private static readonly string[] ChallengeLevelFlags =
    {
        "All_Trophies_Collected",
        "Orphan_01_Complete", "Orphan_04_Complete", "Orphan_09_Complete", "Orphan_15_Complete",
        "Orphan_A1_Key_Cage_Set_C2_Done", "Orphan_A1_Key_Cage_Set_C3_Done", "Orphan_A1_Key_Cage_Set_C4_Done",
        "Orphan_A1_Key_Cage_Set_C6_Done", "Orphan_A1_Key_Cage_Set_C7_Done", "Orphan_A1_Key_Cage_Set_C8_Done",
        "Riddler_All_Puzzle_Rooms_Done",
    };

    // What the Orphanage's own Mech_Fight_1 ending sets. Without Orphan_CW_Freedom_Done, Catwoman is never
    // spawned and the trophy plinth plays her radio lines instead of starting the final fight.
    private static readonly string[] MechFightOneFlags =
    {
        "Orphan_A1_Mech1_Intro_Done", "Orphan_A1_Mech1_Retreat", "Orphan_A1_Mech_Fight_1_Done",
        "Riddler_PostMechFight1_Search", "Orphan_CW_Freedom_Done",
    };

    private const string MechFightOneUnlock = "Orphan_Unlock_Map_Orphan_A1";

    // Set while the player is in the Orphanage, where its own script is loaded and would react to the events.
    private const string InsideOrphanageFlag = "InsideOrphanage";

    // Set by the story's first Orphanage visit, where Riddler takes Catwoman. Before it his missions don't exist yet.
    private const string RiddlerStoryStartedFlag = "Orphan_A1_Intro_Robots_Done";

    // Logged when the skip refuses, to show where a save's Riddler story stands.
    private static readonly string[] StoryFlags =
    {
        "Ch3c_Riddlers_Hideout_Open", "Ch3b_CityX_16_Head_To_Riddler_Cave", "RiddlerStarted", "RiddlerOrphangeActive",
        "Orphan_C6_Unlocked", "Orphan_A1_Intro_Robots_Done", "All_Trophies_Collected",
    };

    private readonly Queue<(string Label, Action<RGameInfo, RFlagManager> Run)> _finale = new();
    private float _finaleWait;

    private static void StartFinale()
    {
        if (!World.InGame)
            throw new InvalidOperationException("load into the game first");
        if (I._finale.Count > 0)
            throw new InvalidOperationException("already running");

        RGameInfo game = World.Info ?? throw new InvalidOperationException("no game info");
        RFlagManager flags = Flags(game);
        if (!flags.GetGlobalFlag(new FString(RiddlerStoryStartedFlag)))
        {
            Log.Info("Riddler finale: refused, story flags " + string.Join(", ", StoryFlags.Select(f => f + "=" + flags.GetGlobalFlag(new FString(f)))) + ".");
            throw new InvalidOperationException("Riddler's story hasn't started: play his first Orphanage visit first");
        }

        int chapter = game.GetChapterNumber(), sub = game.GetSubChapterNumber();
        if (!GcpdTakesRiddler(chapter, sub))
        {
            Log.Info("Riddler finale: refused at chapter " + chapter + ", sub-chapter " + sub + "; the GCPD can't take Riddler yet.");
            throw new InvalidOperationException("too early in the story: the GCPD can't take Riddler until chapter 5, play on first");
        }

        bool inside = flags.GetGlobalFlag(new FString(InsideOrphanageFlag));
        if (!inside && !World.All<SeqEvent_RemoteEvent>().Any(e => e.EventName.ToString() == FinalFightEvent))
            throw new InvalidOperationException("go outside into the city first");

        CollectEverything();
        SetAll(flags, ChallengeLevelFlags);

        if (inside)
        {
            // The city's events have run before, or will on the next visit; here only the state is needed.
            SetAll(flags, MechFightOneFlags);
            Log.Info("Riddler finale: inside the Orphanage, set its state only.");
            Overlay.Notice(Title, "READY, USE THE TROPHY PODIUM", true);
            return;
        }

        foreach (var (unlock, done) in Challenges)
        {
            I._finale.Enqueue((unlock, Fire(unlock)));
            I._finale.Enqueue((done, Fire(done)));
        }

        I._finale.Enqueue((MechFightOneUnlock, Fire(MechFightOneUnlock)));
        I._finale.Enqueue(("first mech fight won", (g, f) => SetAll(f, MechFightOneFlags)));
        I._finale.Enqueue((FinalFightEvent, Fire(FinalFightEvent)));
        I._finaleWait = 0f;
        Log.Info("Riddler finale: " + I._finale.Count + " steps queued.");
        Overlay.Notice(Title, "SKIPPING TO THE FINALE, STAY PUT", true);
    }

    // Beating Riddler ends with driving him to the GCPD, and that drop-off only streams in from chapter 5c
    // (CityZ_T17_Cine_Ch5c6789_Riddler_GCPD_Lockup). Before it the car holds him with no marker and won't let
    // Batman out, a soft lock. The sub-chapter may count a as 0 or 1, so 3 means c or later either way.
    private const int DeliveryChapter = 5;
    private const int DeliverySubChapter = 3;

    private static bool GcpdTakesRiddler(int chapter, int sub) =>
        chapter > DeliveryChapter || (chapter == DeliveryChapter && sub >= DeliverySubChapter);

    private static void SetAll(RFlagManager flags, string[] names)
    {
        foreach (string name in names)
            flags.SetGlobalFlag(new FString(name), true);
    }

    private static Action<RGameInfo, RFlagManager> Fire(string remoteEvent) => (g, _) => g.ActivateRemoteEvent(new FName(remoteEvent));

    public override void Tick(float dt)
    {
        if (_finale.Count == 0)
            return;

        _finaleWait -= dt;
        if (_finaleWait > 0f)
            return;

        _finaleWait = FinaleStepSeconds;
        if (World.Info is not { } game)
            return;

        var (label, run) = _finale.Dequeue();
        Safe.Run("riddler finale " + label, () => run(game, Flags(game)));
        Log.Info("Riddler finale: " + label + " (" + _finale.Count + " left).");
        if (_finale.Count == 0)
        {
            Log.Info("Riddler finale: done. The final fight should now be on the map and mission wheel.");
            Overlay.Notice(Title, "FINAL BATTLE READY, CHECK THE MAP", true);
        }
    }

    // ---- GCPD delivery rescue
    // Delivering Riddler plays Lockup_B1_Cine_Ch5c6789_Delivering_Riddler_GCPD, which first links its cinematic
    // proxy to the named pawn GCPD_AaronCash. On a chapter 9 save Cash was not there, so that latent op waited
    // forever behind a black screen. Cash's spawners are spawn-once, so one used earlier in the story ignores a
    // new request until its spawned mark is cleared. Once Cash exists, restarting the link step lets the
    // cutscene play with him in it; skipping the link is the last resort and leaves his cutscene double empty.

    private const string LockupCinematicFlag = "Lockup_B1_InCinematic";
    private const string DeliveryLevel = "Delivering_Riddler";
    private const string CashName = "GCPD_AaronCash";
    private const string CashLevel = "Lockup_B1.";
    private const float DeliveryCheckSeconds = 1f;
    private const int DeliveryGrace = 3;
    private const int SkipLinkAfter = 14;

    private Every _deliveryCheck;
    private int _deliveryStuck;
    private int _cashAttempt;
    private bool _cashLinked;

    // Frame, not Tick: the game can stop ticking mods while a cutscene holds the screen.
    public override void Frame()
    {
        if (_cellCheck.Elapsed(CellCheckSeconds))
            Safe.Run("gcpd cells", RefillCells);

        if (!_deliveryCheck.Elapsed(DeliveryCheckSeconds))
            return;

        Safe.Run("riddler delivery", () =>
        {
            RSeqAct_LinkCinematicProxyToInGameActor? link = StuckDeliveryLink();
            if (link == null)
            {
                _deliveryStuck = 0;
                _cashAttempt = 0;
                _cashLinked = false;
                return;
            }

            _deliveryStuck++;
            if (_deliveryStuck < DeliveryGrace)
                return;

            if (_deliveryStuck >= SkipLinkAfter)
            {
                Log.Info("Riddler delivery: Aaron Cash could not be brought in, moving the cutscene past him.");
                link.ForceActivateOutput(0);
                _deliveryStuck = 0;
                return;
            }

            if (CashPawn() is { } cash)
            {
                if (!_cashLinked)
                {
                    _cashLinked = true;
                    Log.Info("Riddler delivery: Aaron Cash is here (" + cash.GetPathName(null) + "), restarting the cutscene's link step.");
                    link.ForceActivateInput(0);
                }

                return;
            }

            SpawnCash(_cashAttempt++);
        });
    }

    private static RSeqAct_LinkCinematicProxyToInGameActor? StuckDeliveryLink()
    {
        if (World.Info is not { } game || !Flags(game).GetGlobalFlag(new FString(LockupCinematicFlag)))
            return null;

        return World.All<RSeqAct_LinkCinematicProxyToInGameActor>()
            .FirstOrDefault(l => l.bActive && l.GetPathName(null).Contains(DeliveryLevel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Cash's spawners, the GCPD's own first, then the chapter 9 ones.</summary>
    private static List<RBmPawnSpawner> CashSpawners() => World.All<RBmPawnSpawner>()
        .Where(s => s.PawnReferenceName.ToString() == CashName)
        .OrderBy(s => s.GetPathName(null).StartsWith(CashLevel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ToList();

    private static Pawn? CashPawn() => CashSpawners().Select(s => s.SpawnedPawn).FirstOrDefault(p => p is { IsValid: true } && !p.bDeleteMe);

    /// <summary>Two tries per spawner: the script's own input, then the spawner's SpawnPawn.</summary>
    private static void SpawnCash(int attempt)
    {
        List<RBmPawnSpawner> spawners = CashSpawners();
        if (spawners.Count == 0)
        {
            if (attempt == 0)
                Log.Info("Riddler delivery: no Aaron Cash spawner is loaded.");
            return;
        }

        RBmPawnSpawner spawner = spawners[attempt / 2 % spawners.Count];
        string name = spawner.GetPathName(null);
        if (attempt % 2 == 0)
        {
            Log.Info("Riddler delivery: the cutscene is waiting for Aaron Cash. Spawner " + name + " had bSpawned=" + spawner.bSpawned
                + ", bSpawnOnceOnly=" + spawner.bSpawnOnceOnly + ", bSpawnHidden=" + spawner.bSpawnHidden + "; clearing and firing it.");
            spawner.bSpawned = false;
            spawner.ForceActivateInput(0);
        }
        else
        {
            Log.Info("Riddler delivery: no Cash from " + name + " yet, calling its SpawnPawn directly.");
            spawner.bSpawned = false;
            spawner.SpawnPawn();
        }
    }

    // ---- GCPD cells
    // Entering the GCPD activates every captured villain's cell spawner at once. Seen on a chapter 9 save: that
    // first activation runs before the characters are ready and spawns nobody. A second pass later respawns the
    // villains with repeatable spawners, but most cells are spawn-once and stay empty for the visit. A spawner the
    // script already activated (so its villain is locked up) that still has no pawn is fired again, once other
    // cells are filled to show the GCPD has finished loading.

    private const string CellSequence = "Lockup_B1.TheWorld:PersistentLevel:Sequence_112:";

    // The cells RSeqAct_SwitchFlags_0 starts from each villain's locked-up flag. Other sub-sequences here, such as
    // the Joker hallucinations, spawn people who are meant to leave again, so they are never touched.
    private static readonly HashSet<string> Cells = new(StringComparer.OrdinalIgnoreCase)
    {
        "Blackfire", "firefly", "penguin", "Pyg", "Riddler", "TwoFace", "Azrael", "ManBat", "FireCrew", "Deathstroke",
    };
    private const string CellSwitch = CellSequence + "RSeqAct_SwitchFlags_0";
    private const float CellCheckSeconds = 2f;
    private const float CellFindSeconds = 10f;
    private const float CellWindowSeconds = 120f;
    private const int CellTries = 2;

    private Every _cellCheck;
    private Every _cellFind;
    private RSeqAct_SwitchFlags? _cellSwitch;
    private List<RBmPawnSpawner> _cells = new();
    private readonly Dictionary<nint, int> _cellTries = new();
    private readonly HashSet<nint> _cellsSeenFilled = new();

    /// <summary>The GCPD's cell switch while the cell block is loaded. Outside it, one cheap lookup every 10 s.</summary>
    private bool CellBlockLoaded()
    {
        if (_cellSwitch is { IsValid: true })
            return true;

        _cellSwitch = null;
        _cells.Clear();
        if (!_cellFind.Elapsed(CellFindSeconds))
            return false;

        _cellSwitch = World.All<RSeqAct_SwitchFlags>().FirstOrDefault(s => s.GetPathName(null) == CellSwitch);
        if (_cellSwitch == null)
            return false;

        _cells = World.All<RBmPawnSpawner>()
            .Where(s => s.GetPathName(null) is { } path && path.StartsWith(CellSequence, StringComparison.Ordinal)
                && path.Substring(CellSequence.Length).Split(':') is { Length: 2 } parts && Cells.Contains(parts[0]))
            .ToList();
        return true;
    }

    private void RefillCells()
    {
        if (!CellBlockLoaded() || World.Info is not { } game)
            return;

        List<RBmPawnSpawner> cells = _cells.Where(c => c.IsValid).ToList();
        foreach (RBmPawnSpawner cell in cells.Where(Filled))
            _cellsSeenFilled.Add(cell.Ptr);
        if (!cells.Any(Filled))
            return;

        float now = game.WorldInfo.TimeSeconds;
        foreach (RBmPawnSpawner cell in cells)
        {
            // Only a cell the game started recently that never had anyone in it this visit: a villain the story
            // removed on purpose has been seen filled, and an old activation is not this arrival.
            if (Filled(cell) || cell.bActive || _cellsSeenFilled.Contains(cell.Ptr)
                || cell.LatentActivationTime <= 0f || now - cell.LatentActivationTime > CellWindowSeconds)
                continue;

            _cellTries.TryGetValue(cell.Ptr, out int tries);
            if (tries >= CellTries)
                continue;

            _cellTries[cell.Ptr] = tries + 1;
            string name = cell.GetPathName(null).Substring(CellSequence.Length);
            cell.bSpawned = false;
            if (tries == 0)
            {
                Log.Info("GCPD cells: " + name + " was activated but never spawned, firing it again.");
                cell.ForceActivateInput(0);
            }
            else
            {
                Log.Info("GCPD cells: " + name + " still empty, calling its SpawnPawn directly.");
                cell.SpawnPawn();
            }
        }
    }

    private static bool Filled(RBmPawnSpawner s) => s.SpawnedPawn is { IsValid: true } p && !p.bDeleteMe;

    public override void WorldChanged()
    {
        _cellTries.Clear();
        _cellsSeenFilled.Clear();
        _cellSwitch = null;
        _cells.Clear();
        if (_finale.Count > 0)
            Log.Info("Riddler finale: the world changed with " + _finale.Count + " steps left; run SKIP TO RIDDLER FINALE again.");
        _finale.Clear();
    }

    private static int Count(RGameInfo game, ERiddlerState from, ERiddlerState to) =>
        game.RiddlerPuzzleGetPieceStateCount(from, to, ERiddlerCategory.RiddlerCategory_Unknown, ERiddlerPiece.RiddlerPiece_None, AnyPuzzle);

    private static RFlagManager Flags(RGameInfo game)
    {
        RFlagManager? flags = (game.WorldInfo.GRI as RGameRI)?.FlagManager;
        return flags is { IsValid: true } ? flags : throw new InvalidOperationException("no flag manager");
    }

    /// <summary>Finishes all 243 items. Already-collected ones are skipped, so running it again is harmless.</summary>
    private static void CollectEverything()
    {
        if (!World.InGame)
            throw new InvalidOperationException("load into the game first");

        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("no player");
        RGameInfo game = World.Info ?? throw new InvalidOperationException("no game info");
        RPersistentShared? shared = game.PersistentShared;
        if (shared is not { IsValid: true })
            throw new InvalidOperationException("no persistent data");
        RFlagManager flags = Flags(game);

        // Riddler's story checks refuse to run until this is on.
        World.RequireCheats().UnlockRiddlerScreen();

        var rows = MapPlaces.RiddlerRows();
        rows.Add((Riddle, BatmobileRiddle));

        int finished = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string type, string key) in rows)
        {
            if (!seen.Add(key))
                continue;

            FString pickedUp = new(PickedUpPrefix + key);
            if (flags.GetGlobalFlag(pickedUp) || !Finish(game, shared, pc, type, key))
                continue;

            FString revealed = new(RevealedPrefix + key);
            flags.SetGlobalFlag(revealed, true);
            shared.SetSharedFlag(revealed, true);
            flags.SetGlobalFlag(pickedUp, true);
            shared.SetSharedFlag(pickedUp, true);
            finished++;
        }

        SolveWholeGrid(game);

        RemoveLoadedPickups();

        int solved = Count(game, ERiddlerState.RiddlerState_Solved, ERiddlerState.RiddlerState_Complete);
        int left = Count(game, ERiddlerState.RiddlerState_Unknown, ERiddlerState.RiddlerState_Normal);
        Log.Info("Riddler: finished " + finished + " items, grid now " + solved + " solved, " + left + " left.");
        pc.RiddlerProgressAdvanced();
    }

    /// <summary>Records one item in the grid and the game's counters. Returns false if its name can't be read.</summary>
    private static bool Finish(RGameInfo game, RPersistentShared shared, RPlayerController pc, string type, string key)
    {
        string[] parts = key.Split('_');
        if (parts.Length != 3 || !Zones.TryGetValue(parts[0], out ERiddlerLocationName zone) || !byte.TryParse(parts[2], out byte id))
        {
            Log.Info("Riddler: skipped unknown item " + key + ".");
            return false;
        }

        ERiddlerCategory category = game.RiddlerZoneToCategory(zone);
        switch (type)
        {
            case Trophy when id >= FirstAlternateId:
                break;

            case Trophy:
                game.RiddlerPuzzleSolve(category, ERiddlerPiece.RiddlerPiece_Trophy, id, ERiddlerCollection.RiddlerCollection_Pickups);
                shared.IncreasePickupCount(zone);
                break;

            case Bomb:
                game.RiddlerPuzzleSolve(category, ERiddlerPiece.RiddlerPiece_Bomb, id, ERiddlerCollection.RiddlerCollection_Pickups);
                shared.IncreasePickupCount(zone);
                break;

            case Riddle:
                game.RiddlerPuzzleSolve(category, ERiddlerPiece.RiddlerPiece_Cameo, id, ERiddlerCollection.RiddlerCollection_Cameos);
                pc.UnlockCityStory(id, true);
                break;

            case Breakable:
                if (!Collections.TryGetValue(parts[1], out ERiddlerCollection collection))
                {
                    Log.Info("Riddler: skipped unknown breakable " + key + ".");
                    return false;
                }

                game.RiddlerPuzzleCollect(category, collection, 1);
                break;
        }

        return true;
    }

    /// <summary>Catches any piece an older save left unsolved although its item is already flagged.</summary>
    private static void SolveWholeGrid(RGameInfo game)
    {
        int puzzles = game.RiddlerPuzzles.Count;
        for (int i = 0; i < puzzles; i++)
            game.RiddlerPuzzleSetPieceStateByIndex((byte)i, AllPieces, ERiddlerState.RiddlerState_Solved, true);
    }

    /// <summary>Loaded pickups only check their flag when they spawn, so ask again to clear the ones around the player.</summary>
    private static void RemoveLoadedPickups()
    {
        foreach (RPickupBase pickup in World.All<RPickupBase>().ToList())
        {
            if (!pickup.bDeleteMe && pickup.EarlyDestroy())
                pickup.InitialFlagCheck();
        }
    }
}
