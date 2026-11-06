using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD/SPAWNER/CHARACTERS. Characters are built by the population manager's SpawnPawn, the call the game's street
/// population uses: pawn class, character define and character type in, a pawn with its AI controller out. The
/// recipes are read from what's loaded (see <see cref="SpawnCatalog.Characters"/>), so the list follows where you are.
/// </summary>
internal sealed partial class Spawner
{
    private const float CharacterFootprint = 60f;
    private const float CharacterLift = 100f;
    private const float FollowGap = 250f;

    private Choice _chrGroup = null!;
    private Choice _chrWho = null!;
    private Choice _chrSide = null!;
    private Choice _chrBehaviour = null!;
    private Choice _chrWeapon = null!;
    private Slider _chrHealth = null!;
    private Slider _chrSize = null!;

    private List<CharacterRecipe> _characters = new();
    private string _shownTab = "";

    private enum Side
    {
        Hostile,
        Passive,
        Ally,
        Bodyguard,
    }

    private enum Act
    {
        Fight,
        Stand,
        Follow,
        Wander,
        Frozen,
    }

    private void BuildCharacters()
    {
        _chrGroup = AddChoice("chr_group", "GROUP", "Which kind of character the WHO list shows.", new[] { "" }, new[] { "EVERYONE" }, _ => ListCharacters()).Pref();
        _chrWho = AddChoice("chr_who", "WHO", "Hold to scroll. Everyone this part of the game can build: people around you and the ones its missions and streets spawn. The list follows where you are.",
            new[] { "" }, new[] { "OPEN THE MENU IN GAME" }).Pref();
        _chrWho.HoldToScroll = true;
        AddCommand("chr_refresh", "REFRESH LIST", "Reads again who can be spawned here. Do it after travelling somewhere new.", () => RefreshCharacters(true)).Setting();

        _chrSide = AddChoice("chr_side", "SIDE", "Who they fight. HOSTILE: you. PASSIVE: nobody, even when hit. ON YOUR SIDE: the other enemies. BODYGUARD: follows you everywhere and protects you.",
            new[] { "hostile", "passive", "ally", "guard" }, new[] { "HOSTILE", "PASSIVE", "ON YOUR SIDE", "BODYGUARD" }).Pref();
        _chrBehaviour = AddChoice("chr_behaviour", "BEHAVIOUR", "What they do once spawned. FROZEN holds them still until the editor's RESTORE. WANDER is experimental.",
            new[] { "fight", "stand", "follow", "wander", "frozen" }, new[] { "FIGHT", "STAND", "FOLLOW YOU", "WANDER", "FROZEN" })
            .DisabledWhen(() => _chrSide.CurrentKey == "guard" ? "SET BY BODYGUARD" : null).Pref();
        _chrWeapon = AddChoice("chr_weapon", "WEAPON", "THEIR OWN is what the game gives that character. Pick a weapon to hand them one instead (the WEAPONS tab's list), or UNARMED to take it away.",
            new[] { "own", "none" }, new[] { "THEIR OWN", "UNARMED" }).Pref();
        _chrHealth = AddSlider("chr_health", "HEALTH", "How much health spawned characters get, times their normal amount.", 1f, 0.25f, 10f, 0.25f, 3f, Fmt.Times).Pref();
        _chrSize = AddSlider("chr_size", "SIZE", "How big spawned characters look. Their collision stays normal size.", 1f, 0.3f, 3f, 0.1f, 1.5f, Fmt.Times).Pref();
        AddCommand("chr_spawn", "SPAWN CHARACTER", "Spawns the character above, HOW MANY times, the way this tab and PLACE say.", () => SpawnCharacters(PickedCharacter()));
    }

    private void ForgetCatalogs()
    {
        SpawnCatalog.Forget();
        _characters.Clear();
        _vehicles.Clear();
        _meshes.Clear();
        _weapons.Clear();
        _shownTab = "";
    }

    /// <summary>
    /// A SPAWNER tab was opened: its list is read now. The reads walk thousands of objects (every spawner, every
    /// loaded class, every mesh actor nearby), so they happen on opening a tab and on REFRESH, never on a timer.
    /// </summary>
    private void RefreshTab(string tab)
    {
        switch (tab)
        {
            case "CHARACTERS":
                RefreshCharacters(false);
                RefreshWeapons(false);
                break;
            case "VEHICLES":
                RefreshVehicles(false);
                break;
            case "WEAPONS":
                RefreshWeapons(false);
                break;
            case "OBJECTS":
                if (_objSource.Index == 0 || _meshes.Count == 0)
                {
                    ReadMeshes();
                    ListMeshes();
                }
                break;
        }
    }

    private static bool InGameOrRefuse(bool tell) =>
        World.InGame || (tell ? throw new InvalidOperationException("only in game") : false);

    private void RefreshCharacters(bool tell)
    {
        if (!InGameOrRefuse(tell))
            return;
        Safe.Run("spawner characters", () => _characters = SpawnCatalog.Characters());
        ListCharacters();
        if (tell)
            Overlay.Notice(NoticeTitle, Fmt.Plural(_characters.Count, "CHARACTER") + " LISTED", true);
    }

    private void RefreshVehicles(bool tell)
    {
        if (!InGameOrRefuse(tell))
            return;
        Safe.Run("spawner vehicles", () => _vehicles = SpawnCatalog.Vehicles());
        ListVehicles();
        if (tell)
            Overlay.Notice(NoticeTitle, Fmt.Plural(_vehicles.Count, "VEHICLE") + " LISTED", true);
    }

    /// <summary>Loaded weapon classes barely change within an area, so they're read once per world unless asked.</summary>
    private void RefreshWeapons(bool tell, bool force = false)
    {
        if (!InGameOrRefuse(tell) || (!tell && !force && _weapons.Count > 0))
            return;
        Safe.Run("spawner weapons", () => _weapons = SpawnCatalog.Weapons());
        ListWeapons();
        if (tell)
            Overlay.Notice(NoticeTitle, Fmt.Plural(_weapons.Count, "WEAPON") + " LISTED", true);
    }

    private void ListCharacters()
    {
        string[] groups = _characters.Select(r => r.Group).Distinct().ToArray();
        _chrGroup.SetOptions(new[] { "" }.Concat(groups).ToArray(), new[] { "EVERYONE" }.Concat(groups).ToArray());

        string group = _chrGroup.CurrentKey;
        List<CharacterRecipe> shown = group.Length == 0 ? _characters : _characters.Where(r => r.Group == group).ToList();
        if (shown.Count == 0)
            _chrWho.SetOptions(new[] { "" }, new[] { World.InGame ? "NOBODY LOADED HERE" : "OPEN THE MENU IN GAME" });
        else
            _chrWho.SetOptions(shown.Select(r => r.Key).ToArray(), shown.Select(r => group.Length == 0 ? r.Label + "  (" + r.Group + ")" : r.Label).ToArray());
    }

    private CharacterRecipe PickedCharacter()
    {
        if (_characters.Count == 0)
            RefreshCharacters(false);
        string key = _chrWho.CurrentKey;
        CharacterRecipe r = _characters.Find(c => c.Key == key) ?? throw new InvalidOperationException("nobody to spawn here: REFRESH LIST near some people");
        if (!r.Loaded)
        {
            RefreshCharacters(false);
            throw new InvalidOperationException("that character has unloaded: pick again");
        }

        return r;
    }

    private void SpawnCharacters(CharacterRecipe r)
    {
        Pawn me = RequireReady();
        RPopulationManager pm = World.Info?.PopulationManager is { IsValid: true } found
            ? found
            : throw new InvalidOperationException("characters can't be spawned in this area");

        int n = Room(Wanted);
        var made = new List<Spawn>();
        int failed = 0;
        string why = "the game wouldn't build them here";
        List<Spot> spots = Spots(me, n, CharacterFootprint);
        Starting(n, r.Label, r.PawnClass.Name + ", " + SpawnCatalog.PathOf(r.Define) + ", " + r.Type.Name);
        foreach (Spot spot in spots)
        {
            Vector3 at = spot.Floor + Vector3.UnitZ * CharacterLift;
            RBMPawnAI? p = pm.SpawnPawn(r.PawnClass, r.Define, r.Type, at, spot.Facing);
            if (p is not { IsValid: true } || p.bDeleteMe)
            {
                failed++;
                continue;
            }

            if (p.Controller is not { IsValid: true })
            {
                // Without a controller it can't think or move: a mannequin nobody can clean up properly.
                Log.Info("Spawner: " + r.Label + " came without an AI controller; removed it.");
                Safe.Run("spawn discard", () => p.DestroyOrReturnToPool(true, false));
                why = "they came without a brain";
                failed++;
                continue;
            }

            Spawn s = Track(p, SpawnKind.Character, r.Label, at);
            Safe.Run("spawn character setup", () => SetUp(p, r, s));
            made.Add(s);
        }

        Finish(made, r.Label, failed, why);
    }

    /// <summary>Weapon, health, size, side and behaviour, in that order: the side decides whether FIGHT can start.</summary>
    private void SetUp(RBMPawnAI p, CharacterRecipe r, Spawn s)
    {
        var side = (Side)Math.Clamp(_chrSide.Index, 0, 3);
        var act = (Act)Math.Clamp(_chrBehaviour.Index, 0, 4);
        RBMAIController? ai = p.AIController is { IsValid: true } c ? c : null;

        // Out in the city the game marks its own street spawns as overworld pawns (SpawnBatmobileMobThug does).
        if (Teleport.InCityMap(p.Location))
        {
            p.bInOverworld = true;
            p.bSpawnedInRiot = true;
        }

        if (ai?.RiotBrain is { IsValid: true } brain)
        {
            brain.bWanderer = act == Act.Wander && side != Side.Bodyguard;
            brain.bRiotWantsToFight = act == Act.Fight && side is Side.Hostile or Side.Ally;
        }

        Safe.Run("spawn weapon", () => Arm(p, r));

        // A villain's health is set from the difficulty when he's built (RPawnVillain.PostInitCharacter: MaxHealth from
        // the class default, then the native SetHealth), so it starts below HealthMax. What he really starts with is his
        // full health: HEALTH scales that, and both maxima are set to it so the marker and HEAL agree.
        int start = p.Health > 0 ? p.Health : Math.Max(1, p.HealthMax);
        int full = Math.Max(1, (int)MathF.Round(start * _chrHealth.Value));
        p.Health = full;
        p.HealthMax = full;
        if (p is RPawnCombat combatant)
            combatant.MaxHealth = full;

        if (MathF.Abs(_chrSize.Value - 1f) > 0.01f)
            p.SetDrawScale(_chrSize.Value);

        if (p is RPawnVillain v)
        {
            if (side == Side.Passive)
            {
                v.SetHostile(false, Reason);
                s.Passive = true;
            }
            else if (side == Side.Ally)
            {
                Safe.Run("spawn ally", () => Npcs.I.Turn(v));
            }
            else if (side == Side.Bodyguard)
            {
                // The bodyguard brain decides when to follow and when to fight, so BEHAVIOUR is left out.
                Safe.Run("spawn bodyguard", () =>
                {
                    Npcs.I.Turn(v, bodyguard: true);
                    s.Bodyguard = true;
                });
                return;
            }
        }

        switch (act)
        {
            case Act.Fight when ai != null && p is RPawnVillain && side == Side.Hostile:
                ai.EnterCombat(false, true, false);
                break;
            case Act.Follow when ai != null:
                Follow(ai);
                break;
            case Act.Wander when ai != null:
                ai.SpawnBehaviour(RBMBehaviour_Wanderers.StaticClass(), null!);
                break;
            case Act.Frozen:
                Edits.SetFrozen(Edits.Begin(Things.Wrap(p)), true);
                break;
        }
    }

    /// <summary>
    /// RBMBehaviour_Follow leaves at once if it activates with nobody to follow, so it's made, aimed, then started:
    /// the same two steps SpawnBehaviour takes, with FollowActor set in between.
    /// </summary>
    private static void Follow(RBMAIController ai)
    {
        if (World.Pawn is not { } me)
            return;
        if (ai.CreateSpawnedBehaviour(RBMBehaviour_Follow.StaticClass()) is not RBMBehaviour_Follow f || !f.IsValid)
            throw new InvalidOperationException("the follow behaviour couldn't be made");

        f.FollowActor = me;
        f.FollowDist = FollowGap;
        f.FollowDistSq = FollowGap * FollowGap;
        ai.ActivateSpawnedBehaviour(f);
    }

    private void Arm(RBMPawnAI p, CharacterRecipe r)
    {
        string key = _chrWeapon.CurrentKey;
        if (key == "none")
        {
            if (p.CurrWeapon is { IsValid: true })
                p.DisappearDisarm();
            return;
        }

        Class? cls = key == "own" || key.Length == 0
            ? (p.CurrWeapon is { IsValid: true } ? null : r.Weapon)
            : _weapons.Find(w => w.Key == key)?.Class;
        if (cls is not { IsValid: true })
            return;

        if (p.CurrWeapon is { IsValid: true } held)
        {
            if (held.Class.Ptr == cls.Ptr)
                return;
            p.DisappearDisarm();
        }

        p.SpawnWeapon(cls, true, false);
    }
}
