using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// A character the game knows how to build: the same four things its spawners hold (pawn class, character define,
/// character type, weapon). Read from what's loaded, so its meshes and animations are in memory.
/// </summary>
internal sealed class CharacterRecipe
{
    public required string Key;
    public required string Label;
    public required string Group;
    public required Class PawnClass;
    public required RCharacterDefine Define;
    public required Class Type;
    public Class? Weapon;
    public required bool Enemy;

    public bool Loaded => PawnClass.IsValid && Define.IsValid && Type.IsValid;
}

/// <summary>A vehicle archetype: the template the game's own vehicle spawns copy.</summary>
internal sealed class VehicleRecipe
{
    public required string Key;
    public required string Label;
    public required string Group;
    public required RVehicleNPC Archetype;
}

/// <summary>A mesh to make an object from, with the materials and size of the piece it was taken from.</summary>
internal sealed class MeshRecipe
{
    public required string Key;
    public required string Label;
    public required StaticMesh Mesh;
    public MaterialInterface?[] Materials = Array.Empty<MaterialInterface?>();
    public System.Numerics.Vector3 Scale = System.Numerics.Vector3.One;
}

/// <summary>A weapon class whose dropped form thugs (and Batman) can pick up.</summary>
internal sealed class WeaponRecipe
{
    public required string Key;
    public required string Label;
    public required Class Class;
}

/// <summary>
/// Finds what can be spawned right now. Everything is read from objects already in memory: live characters and the
/// level's spawners for characters, archetypes for vehicles, meshes for objects, loaded classes for weapons. Lists
/// change with where you are, so they're re-read when the menu opens (at most every few seconds) and on REFRESH.
/// </summary>
internal static class SpawnCatalog
{
    private static readonly Dictionary<nint, string> _paths = new();

    public static void Forget() => _paths.Clear();

    /// <summary>An object's path, read once per object: GetPathName allocates a string on every call.</summary>
    public static string PathOf(GameObject o)
    {
        if (!_paths.TryGetValue(o.Ptr, out string? p))
        {
            p = o.GetPathName(null);
            _paths[o.Ptr] = p;
        }

        return p;
    }

    public static bool IsChild(Class? c, Class parent)
    {
        for (Struct? s = c; s is { IsValid: true }; s = s.SuperStruct)
        {
            if (s.Ptr == parent.Ptr)
                return true;
        }

        return false;
    }

    /// <summary>"Light_Tank_Double_Barrel_Easy" -> "LIGHT TANK DOUBLE BARREL EASY"; "CombatRifle" -> "COMBAT RIFLE".</summary>
    public static string Pretty(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (ch == '_' || ch == '-')
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                continue;
            }

            bool split = i > 0 && char.IsUpper(ch) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (split && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString().Trim();
    }

    // ---- characters

    // Story-scripted pawns (bosses, cutscene stand-ins, tutorials) run on their level's Kismet; built bare they can
    // hang or break the beat they belong to.
    private static readonly string[] RefusedPawns = { "Boss", "Fake", "Tutorial", "Cinematic", "Ragdoll", "Prototype2D", "MechSuit" };

    public static string GroupOf(string definePath, bool enemy)
    {
        string[] parts = definePath.Split('.');
        string sub = parts.Length >= 3 ? parts[1] : "";
        string label = sub.ToLowerInvariant() switch
        {
            "redhood" => "MILITIA",
            "penguin" => "PENGUIN'S GANG",
            "twoface" => "TWO-FACE'S GANG",
            "harley" => "HARLEY'S GANG",
            "riddler" => "RIDDLER'S ROBOTS",
            "rioters" => "RIOTERS",
            "blackfire" => "BLACKFIRE",
            "dollotrons" => "DOLLOTRONS",
            "diner_civilians" => "CIVILIANS",
            "gcpd_cops" => "POLICE",
            "firemen" => "FIRE CREWS",
            "paramedics" => "PARAMEDICS",
            "ace_chemicals_workers" => "ACE WORKERS",
            "reporters" => "REPORTERS",
            "villain" => "VILLAINS",
            "heroes" => "HEROES",
            "neutral" => "OTHERS",
            "" => enemy ? "OTHER ENEMIES" : "OTHER PEOPLE",
            _ => Pretty(sub),
        };
        return label;
    }

    /// <summary>The recipe a live character was built from, or null when part of it isn't readable.</summary>
    public static CharacterRecipe? RecipeOf(RBMPawnAI p) =>
        Make(p.Class, p.CharacterDefine, p.CharactersType, p.WeaponType);

    private static CharacterRecipe? Make(Class? pawn, RCharacterDefine? define, Class? type, Class? weapon)
    {
        if (pawn is not { IsValid: true } || define is not { IsValid: true } || define.IsClassDefaultObject || type is not { IsValid: true })
            return null;
        if (!IsChild(pawn, RBMPawnAI.StaticClass()))
            return null;

        string pawnName = pawn.Name.ToString() ?? "";
        if (RefusedPawns.Any(r => pawnName.Contains(r, StringComparison.OrdinalIgnoreCase)))
            return null;

        string path = PathOf(define);
        bool enemy = IsChild(pawn, RPawnVillain.StaticClass());
        return new CharacterRecipe
        {
            Key = path + "@" + pawnName,
            Label = Pretty(define.Name.ToString() ?? "CHARACTER"),
            Group = GroupOf(path, enemy),
            PawnClass = pawn,
            Define = define,
            Type = type,
            Weapon = weapon is { IsValid: true } ? weapon : null,
            Enemy = enemy,
        };
    }

    /// <summary>
    /// Every character the loaded world can build: people walking around now, the level's Kismet spawners (including
    /// their New Game+ versions), and the street-population volumes' thug looks. Enemies first, then by group and name.
    /// </summary>
    public static List<CharacterRecipe> Characters()
    {
        var found = new Dictionary<string, CharacterRecipe>(StringComparer.OrdinalIgnoreCase);

        void Offer(Class? pawn, RCharacterDefine? define, Class? type, Class? weapon)
        {
            CharacterRecipe? r;
            try
            {
                r = Make(pawn, define, type, weapon);
            }
            catch (Exception ex)
            {
                if (Log.Due("spawner recipe"))
                    Log.Info("Spawner: skipped a character recipe: " + Log.Why(ex));
                return;
            }

            if (r == null)
                return;
            if (found.TryGetValue(r.Key, out CharacterRecipe? had))
                had.Weapon ??= r.Weapon;
            else
                found[r.Key] = r;
        }

        nint player = World.Player?.Ptr ?? 0;
        foreach (RBMPawnAI p in World.All<RBMPawnAI>())
        {
            if (p.Ptr != player && !p.bDeleteMe && p is not RPawnCrowdRagdoll)
                Offer(p.Class, p.CharacterDefine, p.CharactersType, p.WeaponType);
        }

        foreach (RBmPawnSpawner s in World.All<RBmPawnSpawner>())
        {
            Offer(s.PawnClass, s.CharacterDefine ?? s.CharacterDefineFallback, s.CharacterType, s.WeaponType);
            if (s.NewGamePlus_CharacterDefine is { IsValid: true })
                Offer(s.NewGamePlus_PawnClass ?? s.PawnClass, s.NewGamePlus_CharacterDefine, s.NewGamePlus_CharacterType ?? s.CharacterType, s.NewGamePlus_WeaponType ?? s.WeaponType);
        }

        // Street thugs: the volume pairs each look with a character type, and spawns them as RPawnVillainThug
        // (RVehicleNPC.SpawnVehiclePassengers does the same with these lists).
        Class thug = RPawnVillainThug.StaticClass();
        foreach (ROverworldPopulationVolume v in World.All<ROverworldPopulationVolume>())
        {
            OfferPairs(v.MultiCharacterDefines, v.MultiCharacterTypes);
            OfferPairs(v.MultiCharacterDefinesForGas, v.MultiCharacterTypesForGas);
        }

        void OfferPairs(TArray<RCharacterDefine>? defines, TArray<Class>? types)
        {
            if (defines == null || types == null)
                return;
            int n = Math.Min(defines.Count, types.Count);
            for (int i = 0; i < n; i++)
                Offer(thug, defines[i], types[i], null);
        }

        List<CharacterRecipe> list = found.Values.ToList();

        // Two recipes with the same look (one per pawn class) are told apart by their class.
        foreach (IGrouping<string, CharacterRecipe> same in list.GroupBy(r => r.Label).Where(g => g.Count() > 1))
        {
            foreach (CharacterRecipe r in same)
                r.Label += " (" + Pretty((r.PawnClass.Name.ToString() ?? "").Replace("RPawnVillain", "").Replace("RPawn", "")) + ")";
        }

        list.Sort((a, b) =>
        {
            int c = b.Enemy.CompareTo(a.Enemy);
            if (c == 0)
                c = string.CompareOrdinal(a.Group, b.Group);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    // ---- vehicles

    // Templates, story-only variants and parked set dressing: BASE archetypes are abstract parents, Hush's van and the
    // interrogation and chase cars belong to their missions, and a static tank never drives.
    private static readonly string[] RefusedVehicles = { "_BASE", "Boss.", "Static.", "Interrogation", "GordonChase", "Batmobile" };

    private static string VehicleGroup(RVehicleNPC v) => v switch
    {
        RVehicleHeavyTank => "HEAVY TANKS",
        RVehicleTank => "TANKS",
        RVehicleFlying => "DRONES",
        RVehicleWalkerBase => "WALKER TURRETS",
        RVehicleCar => "CARS AND VANS",
        _ => "OTHER",
    };

    /// <summary>The archetype a vehicle was spawned from, or null when it was placed in the level directly.</summary>
    public static VehicleRecipe? RecipeOf(RVehicleNPC v)
    {
        RVehicleNPC? arch = IsArchetype(v) ? v : v.ObjectArchetype as RVehicleNPC;
        return arch is { IsValid: true } && IsArchetype(arch) ? Make(arch) : null;
    }

    /// <summary>A template, not a vehicle in the world: archetypes live in a package, never in a Level.</summary>
    private static bool IsArchetype(Actor a) => !a.IsClassDefaultObject && a.Outer is not Level and not null;

    private static VehicleRecipe? Make(RVehicleNPC arch)
    {
        if (arch is RVehicleStaticTank or RVehicleNPCBatmobile or RVehicleHushBase)
            return null;

        string path = PathOf(arch);
        if (RefusedVehicles.Any(r => path.Contains(r, StringComparison.OrdinalIgnoreCase)))
            return null;

        return new VehicleRecipe
        {
            Key = path,
            Label = Pretty(arch.Name.ToString() ?? "VEHICLE"),
            Group = VehicleGroup(arch),
            Archetype = arch,
        };
    }

    public static List<VehicleRecipe> Vehicles()
    {
        var found = new Dictionary<string, VehicleRecipe>(StringComparer.OrdinalIgnoreCase);
        foreach (RVehicleNPC v in World.All<RVehicleNPC>())
        {
            try
            {
                if (RecipeOf(v) is { } r)
                    found.TryAdd(r.Key, r);
            }
            catch (Exception ex)
            {
                if (Log.Due("spawner vehicle"))
                    Log.Info("Spawner: skipped a vehicle archetype: " + Log.Why(ex));
            }
        }

        List<VehicleRecipe> list = found.Values.ToList();
        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Group, b.Group);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    // ---- objects

    /// <summary>The mesh, materials and size of a mesh component, or null when it has no mesh.</summary>
    public static MeshRecipe? RecipeOf(StaticMeshComponent c, Actor? owner)
    {
        if (c.StaticMesh is not { IsValid: true } mesh)
            return null;

        var mats = new List<MaterialInterface?>();
        int n = Math.Clamp(c.GetNumElements(), 0, 32);
        for (int i = 0; i < n; i++)
            mats.Add(c.GetMaterial(i) is { IsValid: true } m ? m : null);

        // An ordinary actor carries the size; a collection actor's pieces each carry their own (see PreScarecrowSky).
        System.Numerics.Vector3 scale = owner is { IsValid: true } and not StaticMeshCollectionActor
            ? owner.DrawScale3D * owner.DrawScale * c.Scale3D * c.Scale
            : c.Scale3D * c.Scale;

        return new MeshRecipe
        {
            Key = PathOf(mesh),
            Label = Pretty(mesh.Name.ToString() ?? "MESH"),
            Mesh = mesh,
            Materials = mats.ToArray(),
            Scale = scale,
        };
    }

    /// <summary>
    /// Meshes of the pieces around you that can be picked up or knocked about (loose props, movers) plus the level's
    /// own placed meshes, nearest first, one entry per mesh.
    /// </summary>
    public static List<MeshRecipe> NearbyMeshes(System.Numerics.Vector3 from, float range, int max)
    {
        var picks = new List<(float Dist, StaticMeshComponent C, Actor A)>();
        float r2 = range * range;

        void Consider(Actor a, StaticMeshComponent? c)
        {
            // Templates (not in a Level) and half torn-down actors are skipped before any of their parts are read.
            if (a.bDeleteMe || a.bPendingDelete || a.Outer is not Level || c is not { IsValid: true })
                return;
            float d2 = System.Numerics.Vector3.DistanceSquared(a.Location, from);
            if (d2 <= r2)
                picks.Add((d2, c, a));
        }

        foreach (DynamicSMActor a in World.All<DynamicSMActor>())
            Consider(a, a.StaticMeshComponent);
        foreach (StaticMeshActor a in World.All<StaticMeshActor>())
            Consider(a, a.StaticMeshComponent);

        picks.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var seen = new HashSet<nint>();
        var list = new List<MeshRecipe>();
        foreach ((_, StaticMeshComponent c, Actor a) in picks)
        {
            if (list.Count >= max)
                break;
            try
            {
                // One entry per mesh, decided before the materials are asked for (each is a script call).
                if (c.StaticMesh is not { IsValid: true } mesh || !seen.Add(mesh.Ptr))
                    continue;
                if (RecipeOf(c, a) is { } r)
                    list.Add(r);
            }
            catch (Exception ex)
            {
                if (Log.Due("spawner mesh"))
                    Log.Info("Spawner: skipped a mesh: " + Log.Why(ex));
            }
        }

        return list;
    }

    /// <summary>Every static mesh in memory whose name has one of <paramref name="words"/> (all of them when empty), by name.</summary>
    public static List<MeshRecipe> LoadedMeshes(string[] words, int max)
    {
        // Names are cheap; paths and labels are made only for the meshes that make the cut.
        var matches = new List<(string Name, StaticMesh Mesh)>();
        foreach (StaticMesh m in World.All<StaticMesh>())
        {
            string name = m.Name.ToString() ?? "";
            if (words.Length == 0 || words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                matches.Add((name, m));
        }

        matches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return matches.Take(max).Select(x => new MeshRecipe { Key = PathOf(x.Mesh), Label = Pretty(x.Name), Mesh = x.Mesh }).ToList();
    }

    // ---- weapons

    // Abstract parents, Batman's own carry/scripted pieces and tutorial copies are no use on the floor.
    private static readonly string[] RefusedWeapons = { "Batman", "ChokePoint", "SmashedSegment", "Tutorial", "UnPickupable", "Jokerfied" };
    private static readonly string[] AbstractWeapons = { "RBMWeapon", "RBMWeaponMelee", "RBMWeaponRanged", "RBMWeaponHeavyObject" };

    private static readonly Dictionary<string, string> WeaponNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RBMWeaponBat"] = "BASEBALL BAT",
        ["RBMWeaponCombatRifle"] = "ASSAULT RIFLE",
        ["RBMWeaponRiflePredFull"] = "PREDATOR RIFLE",
        ["RBMWeaponRiflePredLite"] = "PREDATOR RIFLE (LIGHT)",
        ["RBMWeaponMinigunPred"] = "MINIGUN",
        ["RBMWeaponPredatorSniperRifle"] = "SNIPER RIFLE",
        ["RBMWeaponCombatExpertSword"] = "SWORD",
        ["RBMWeaponAzraelSword"] = "AZRAEL'S SWORD",
    };

    public static WeaponRecipe RecipeOf(Class cls)
    {
        string name = cls.Name.ToString() ?? "";
        string label = WeaponNames.TryGetValue(name, out string? l)
            ? l
            : Pretty(name.Replace("RBMWeaponHeavyObject", "").Replace("RBMWeapon", "")) is { Length: > 0 } p ? p : "WEAPON";
        return new WeaponRecipe { Key = name, Label = label, Class = cls };
    }

    /// <summary>
    /// Every weapon class in memory, including ones only a level's own script package brings (crates, stun sticks,
    /// riot shields). Whether its model is loaded too is checked when it's spawned.
    /// </summary>
    public static List<WeaponRecipe> Weapons()
    {
        Class root = RBMWeapon.StaticClass();
        var found = new Dictionary<string, WeaponRecipe>(StringComparer.OrdinalIgnoreCase);
        foreach (Class c in World.All<Class>())
        {
            string name = c.Name.ToString() ?? "";
            if (!name.StartsWith("RBMWeapon", StringComparison.Ordinal) || name.EndsWith("Base", StringComparison.Ordinal)
                || AbstractWeapons.Contains(name) || RefusedWeapons.Any(r => name.Contains(r, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (IsChild(c, root))
                found.TryAdd(name, RecipeOf(c));
        }

        List<WeaponRecipe> list = found.Values.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        return list;
    }
}
