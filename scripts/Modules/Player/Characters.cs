using System.Collections;
using System.Numerics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Plays as other characters by driving the game's own switch sequence action. Saving is blocked meanwhile,
/// because a save made as someone else can't be loaded properly as the story's Batman.
/// </summary>
internal sealed class Characters : Module
{
    public static readonly Characters I = new();

    private sealed record Def(string Key, string Label, string Packages, string PlayerClass, string Name, bool DiveBoost, bool InstantCar);

    private static readonly Def[] Roster =
    {
        new("batman", "BATMAN", "Playable_Batman_Std_SF", "Playable_Batman", "Batman", false, false),
        new("nightwing", "NIGHTWING", "Playable_Nightwing0_SF|Playable_Nightwing_SF", "Playable_Nightwing0", "Nightwing0", false, false),
        new("robin", "ROBIN", "Playable_Robin0_SF|Playable_Robin_SF", "Playable_Robin0", "Robin0", true, false),
        new("catwoman", "CATWOMAN", "Playable_Catwoman0_SF|Playable_Catwoman_SF", "Playable_Catwoman0", "Catwoman0", false, false),
        new("batgirl", "BATGIRL", "Playable_DLCBatgirl_SF", "Playable_DLCBatgirl", "DLCBatgirl", false, true),
        new("redhood", "RED HOOD", "Playable_DLCRedHood_SF", "Playable_DLCRedHood", "DLCRedHood", false, true),
        new("harley", "HARLEY QUINN", "Playable_DLCHarleyQuinn", "Playable_DLCHarleyQuinn", "DLCHarleyQuinn", false, true),
    };

    private const string CurrentKey = "Character";
    private const string SpawnedKey = "SpawnedClasses";
    private const string Caption = "ORACLE // SWITCHING CHARACTER";
    private const float FadeOutSeconds = 1.8f;
    private const float FadeInSeconds = 1.4f;
    private const float CarFadeSeconds = 0.45f;
    private const float SpawnSettle = 4f;
    private const float SpawnGiveUp = 12f;
    private const float CallReach = 1400f;
    private const float DoorReach = 900f;
    private const float SaveNoticeGap = 30f;
    private const float CleanupDelay = 6f;

    private enum Phase
    {
        Idle,
        FadeOut,
        Spawning,
    }

    private Choice _pick = null!;
    private Phase _phase;
    private float _timer;
    private int _target;
    private nint _oldPawn;
    private bool _recovering;
    private bool _protecting;
    private bool _guardDone;
    private bool _aliasesDone;
    private float _saveNoticeLeft;
    private float _upkeepLeft;
    private float _cleanupLeft;
    private RSeqAct_SwitchPlayerCharacter? _action;
    private PlayerStart? _start;

    private static int Current
    {
        get => Shared.GetValue(CurrentKey, 0);
        set => Shared.Set(CurrentKey, value);
    }

    private static string SpawnedClasses
    {
        get => Shared.Get<string>(SpawnedKey) ?? ";";
        set => Shared.Set(SpawnedKey, value);
    }

    public bool Busy => _phase != Phase.Idle;

    public bool PlayingOther => Current != 0;

    private static bool IsBatman(Pawn? p) => p is RPawnPlayerBm;

    public override void Build()
    {
        _pick = AddChoice("play_as", "PLAY AS", "Pick a character, then run SWITCH CHARACTER. Batgirl, Red Hood and Harley Quinn need their story pack DLC.",
            Roster.Select(d => d.Key).ToArray(), Roster.Select(d => d.Label).ToArray()).Pref();
        Command sw = AddCommand("switch_character", "SWITCH CHARACTER", "Fades to black and swaps you to the character above, right where you stand. Saving pauses until you are Batman again.", StartSwitch).Confirmed();
        sw.ConfirmPrompt = () => "SWITCH TO " + Roster[_pick.Index].Label + "?";
        AddCommand("back_to_batman", "BACK TO BATMAN", "Swaps back to Batman right where you stand. Saving works again after this.", () =>
        {
            _pick.ResetValue();
            StartSwitch();
        });

        AddCommand("call_batmobile", "CALL / ENTER BATMOBILE", "Any character: gets in the Batmobile when it is close, otherwise calls it to you. As another character the game's own E key (LB on a controller) gets you in too.", CarAction);
    }

    public override void WorldChanged()
    {
        if (Busy)
            return;

        NoteLeftAsOther();
        Reset();
    }

    public override void LeftGame()
    {
        NoteLeftAsOther();
        if (PlayingOther || Busy)
            Reset();
    }

    private void NoteLeftAsOther()
    {
        if (PlayingOther)
            Log.Info("Left the game as " + Roster[Current].Label + ", Batman comes back on the next load.");
        _guardDone = false;
    }

    private void StartSwitch()
    {
        int target = _pick.Index;
        Transition.RequireIdle();
        if (World.Pawn is not RPawnPlayer pawn)
            throw new InvalidOperationException("get out of the Batmobile first");

        if (target == Current && (target != 0 || IsBatman(pawn)))
        {
            Overlay.Notice("CHARACTER", "ALREADY " + Roster[target].Label, false);
            return;
        }

        Begin(target);
    }

    /// <summary>Switches in the character being played again, so a suit just picked for them loads with them.</summary>
    public void Reload()
    {
        if (!PlayingOther)
            throw new InvalidOperationException("not playing as a switched character");
        Transition.RequireIdle();
        if (World.Pawn is not RPawnPlayer)
            throw new InvalidOperationException("get out of the Batmobile first");

        Begin(Current);
    }

    private void Begin(int target)
    {
        _target = target;
        _recovering = false;
        _protecting = false;
        _phase = Phase.FadeOut;
        Menu.Close();
        Fader.Out(FadeOutSeconds, true, Caption);
        Log.Info("Switching character to " + Roster[target].Label + ".");
    }

    public override void Tick(float dt)
    {
        if (_saveNoticeLeft > 0f)
            _saveNoticeLeft -= dt;

        switch (_phase)
        {
            case Phase.FadeOut:
                if (!Fader.IsDark)
                    return;

                if (!BeginSwitch(_target))
                {
                    Fail("COULD NOT SWITCH");
                    return;
                }

                _timer = 0f;
                _phase = Phase.Spawning;
                return;

            case Phase.Spawning:
                _timer += dt;
                if (!Fader.IsDark)
                    Fader.Cut(Caption);
                WaitForSpawn();
                return;
        }

        if (PlayingOther)
            Upkeep(dt);

        if (_cleanupLeft > 0f)
        {
            _cleanupLeft -= dt;
            if (_cleanupLeft <= 0f)
                CollectAfterSwitch();
        }
    }

    private bool BeginSwitch(int index)
    {
        Def def = Roster[index];
        RPlayerController? pc = World.Pc;
        RGameInfo? gi = World.Info;
        WorldInfo? wi = World.WorldInfo;
        if (pc == null || gi == null || wi == null)
            return false;

        Sequence seq = wi.GetGameSequence();
        if (seq is not { IsValid: true })
        {
            Log.Info("Character switch: no game sequence.");
            return false;
        }

        RegisterPawnAliases();

        foreach (string package in def.Packages.Split('|'))
        {
            string name = package;
            Safe.Run("package " + name, () => Game.LoadPackage(name));
        }

        gi.LoadPC(def.PlayerClass);

        _oldPawn = World.PawnPtr;
        _start = Game.SpawnActor(PlayerStart.StaticClass(), default, pc.Location, pc.Rotation) as PlayerStart;
        if (_start == null)
        {
            Log.Info("Character switch: could not place a start point.");
            return false;
        }

        _action = new RSeqAct_SwitchPlayerCharacter(seq);
        _action.ParentSequence = seq;
        _action.CharacterName = def.Name;
        _action.PlayerStartPoint = _start;
        _action.SwitchToThisPawn = null!;

        pc.PrepareForPlayerSwitch();
        _action.ForceActivateInput(0);
        Log.Info("Character switch to " + def.Label + " sent to the game.");
        return true;
    }

    private void WaitForSpawn()
    {
        Pawn? pawn = World.Pawn;
        if (_timer >= SpawnSettle && pawn != null && pawn.Ptr != _oldPawn)
        {
            Finish(pawn);
            return;
        }

        if (_timer < SpawnGiveUp)
            return;

        _action = null;
        _start = null;
        if (_target != 0 && !_recovering)
        {
            Log.Info("Character switch: " + Roster[_target].Label + " did not appear, going back to Batman.");
            _recovering = true;
            _target = 0;
            _timer = 0f;
            if (BeginSwitch(0))
                return;
        }

        Current = 0;
        Fail(_recovering ? "NOT AVAILABLE" : "SWITCH FAILED");
    }

    private void Finish(Pawn pawn)
    {
        Def def = Roster[_target];
        _action = null;
        _start = null;
        _phase = Phase.Idle;
        Current = _target;

        if (_target != 0)
        {
            string cls = pawn.Class.GetPathName(null);
            if (!SpawnedClasses.Contains(";" + cls + ";", StringComparison.OrdinalIgnoreCase))
                SpawnedClasses += cls + ";";
            if (World.Pc is { } pc)
                pc.bCanGetInBatmobile = true;
        }

        _upkeepLeft = 0f;
        _cleanupLeft = CleanupDelay;
        _pick.Index = _target;
        _pick.Saved = _pick.CurrentKey;

        string title = _protecting ? "SAVE PROTECTED" : _recovering ? "CHARACTER" : "NOW PLAYING AS";
        Fader.In(FadeInSeconds, title, _protecting || _recovering ? "BACK TO BATMAN" : def.Label);
        _protecting = false;
        Log.Info("Now playing as " + def.Label + " (" + pawn.GetPathName(null) + ").");
    }

    private void Fail(string why)
    {
        Log.Info("Character switch failed: " + why);
        _phase = Phase.Idle;
        _action = null;
        _start = null;
        Fader.In(FadeInSeconds, "CHARACTER", why, false);
    }

    private void Upkeep(float dt)
    {
        _upkeepLeft -= dt;
        if (_upkeepLeft > 0f || World.Pc is not { } pc)
            return;

        _upkeepLeft = 1f;
        if (!pc.bCanGetInBatmobile)
            pc.bCanGetInBatmobile = true;
        if (Roster[Current].DiveBoost && pc.Pawn is RPawnPlayer p)
            SetBool(p, "bCanDiveBoost", true);
    }

    private static void SetBool(object target, string name, bool value)
    {
        PropertyInfo? pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (pi != null && pi.PropertyType == typeof(bool) && pi.CanWrite && (bool)pi.GetValue(target)! != value)
            pi.SetValue(target, value);
    }

    private bool TryEnterCar(float reach)
    {
        RPlayerController? pc = World.Pc;
        if (pc == null || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl() || pc.Pawn is not RPawnPlayer pawn)
            return false;

        RVehicleBatmobileBase? car = Batmobile.I.Car();
        if (car is not { IsValid: true } || car.Driver != null || Vector3.Distance(car.Location, pawn.Location) > reach)
            return false;
        if (car is RVehicleBatmobile rb && !rb.CanEnterVehicle(pawn))
            return false;

        if (Roster[Current].InstantCar)
        {
            Fader.Cut("");
            Fader.In(CarFadeSeconds);
            car.GetInBatmobileInstant(pawn, false);
        }
        else
            car.GetInBatmobile(pawn);

        Log.Info("Entered the Batmobile as " + Roster[Current].Label + ".");
        return true;
    }

    /// <summary>Handles E for non-Batman characters, who have no prompt of their own at the car.</summary>
    public bool EntryKey(Keys key) => key == Keys.E && EnterNearby();

    public bool EnterNearby()
    {
        if (!PlayingOther || Menu.IsOpen || Busy)
            return false;

        return Safe.Try("Batmobile entry", () => TryEnterCar(DoorReach));
    }

    private void CarAction()
    {
        RPlayerController? pc = World.Pc;
        if (pc == null || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl())
            return;

        pc.bCanGetInBatmobile = true;
        if (TryEnterCar(CallReach))
            return;

        var mode = pc.CallBatmobile();
        bool coming = mode != RVehicleBatmobileBase.EBatmobilePickupMode.BPM_None;
        Overlay.Notice("BATMOBILE", coming ? "ON ITS WAY" : "CAN'T REACH YOU HERE", coming);
        Log.Info("Batmobile called: " + mode + ".");
    }

    private void CollectAfterSwitch()
    {
        RPlayerController? pc = World.Pc;
        if (pc == null || World.Pawn == null || pc.IsInBatmobile() || pc.IsBatmobileUnderRemoteControl() || Transition.Busy)
            return;

        Safe.Run("cleanup after switch", () => World.WorldInfo?.ForceGarbageCollection(true));
    }

    /// <summary>
    /// Some playable pawn classes exist only as BmScript paths with an MPawnPlayer...Base wrapper in BmSDK. Without
    /// a mapping BmSDK wraps them as a plain pawn and every player feature silently stops applying.
    /// </summary>
    private void RegisterPawnAliases()
    {
        if (_aliasesDone)
            return;
        _aliasesDone = true;

        Safe.Run("character class aliases", () =>
        {
            Type? init = typeof(Game).Assembly.GetType("BmSDK.Framework.StaticInit", false);
            if (init?.GetField("_classPathToManagedTypeMap", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is not IDictionary map)
                return;

            Type[] types;
            try
            {
                types = typeof(Game).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = Array.FindAll(ex.Types, t => t != null)!;
            }

            foreach (Type t in types)
            {
                if (!t.Name.StartsWith("MPawnPlayer", StringComparison.Ordinal) || !t.Name.EndsWith("Base", StringComparison.Ordinal))
                    continue;

                string path = "BmScript." + t.Name.Substring(0, t.Name.Length - 4);
                if (!map.Contains(path))
                    map[path] = t;
            }
        });
    }

    /// <summary>
    /// If the story loads as a character Oracle switched to, puts Batman back before anything can be saved.
    /// Returns true on the tick it takes over.
    /// </summary>
    public bool GuardLoadedCharacter()
    {
        if (_guardDone || Busy || SpawnedClasses.Length <= 1)
            return false;

        // App only asks in a playable world, so this is never the main menu's pawn.
        if (World.Pawn is not { } pawn)
            return false;

        _guardDone = true;
        RGameInfo? gi = World.Info;
        if (gi == null || !gi.IsCoreStory() || gi.IsChallengeMode() || IsBatman(pawn) || PlayingOther)
            return false;
        if (!SpawnedClasses.Contains(";" + pawn.Class.GetPathName(null) + ";", StringComparison.OrdinalIgnoreCase))
            return false;

        Log.Info("Loaded the story as " + pawn.GetPathName(null) + " instead of Batman, switching back to keep the save safe.");
        _target = 0;
        _recovering = false;
        _protecting = true;
        _phase = Phase.FadeOut;
        Fader.Cut(Caption);
        return true;
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        Current = 0;
        _action = null;
        _start = null;
        _cleanupLeft = 0f;
        _pick.ResetValue();
    }

    private bool SaveBlocked()
    {
        if (!PlayingOther && !Busy)
            return false;

        Log.Info("Save skipped: playing as " + Roster[Current].Label + ".");
        if (_saveNoticeLeft <= 0f)
        {
            _saveNoticeLeft = SaveNoticeGap;
            Overlay.Notice("SAVING PAUSED", "SWITCH BACK TO BATMAN TO SAVE", false);
        }

        return true;
    }

    // Failing closed: if the check can't run, assume the save is unsafe rather than risk writing a bad one.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SaveGame))]
    private static void SaveGameRedirect(RPlayerController self, FString name, bool a, bool b)
    {
        if (!Safe.Try("save check", I.SaveBlocked, fallback: true))
            self.SaveGame(new FString(name.ToString() ?? ""), a, b);
    }
}
