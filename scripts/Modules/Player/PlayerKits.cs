using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Another playable character's data without switching to them: the Playable_X asset the game builds the character
/// from (RAddContentPlayerCharacter), its animation config and its pawn class, whose class defaults hold the
/// character's move lists. The package is loaded on first use and kept for the session, as the game keeps every
/// playable character it has shown. Borrowed by FIGHTING STYLE, MOVEMENT STYLE and ZIP KICK.
/// </summary>
internal static class PlayerKits
{
    internal sealed class Kit
    {
        public Characters.Def Def = null!;
        public RAddContentPlayerCharacter Asset = null!;
        public RPawnPlayerConfig Config = null!;
        public Class PawnClass = null!;

        /// <summary>The pawn class's defaults: the move lists and anim set choices every pawn of that class starts from.</summary>
        public RPawnPlayerCombat Defaults = null!;

        public bool Alive => Asset.IsValid && Config.IsValid && PawnClass.IsValid && Defaults.IsValid;
    }

    private static readonly Dictionary<string, Kit> _kits = new(StringComparer.OrdinalIgnoreCase);

    public static Characters.Def? Find(string key) => Array.Find(Characters.Roster, d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads the character's package if needed. Throws, with a reason the player can read, when it can't be had.</summary>
    public static Kit Load(Characters.Def def)
    {
        if (_kits.TryGetValue(def.Key, out Kit? kit) && kit.Alive)
            return kit;

        RAddContentPlayerCharacter? asset = FindAsset(def.PlayerClass);
        if (asset == null)
        {
            // The roster names some packages without the _SF the cooked file carries (the game's own loader adds it).
            foreach (string package in def.Packages.Split('|'))
            {
                Package? loaded = null;
                foreach (string name in package.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? new[] { package } : new[] { package, package + "_SF" })
                {
                    Safe.Run("package " + name, () => loaded = Game.LoadPackage(name));
                    if (loaded is { IsValid: true })
                        break;
                }

                if (loaded is { IsValid: true })
                    loaded.AddToRoot();
                else
                    Log.Info("Character kit: package " + package + " did not load.");
            }

            asset = FindAsset(def.PlayerClass);
        }

        if (asset == null)
            throw new InvalidOperationException(def.Label + (def.Dlc ? " needs their story pack DLC" : " did not load"));

        RPawnPlayerConfig? config = asset.AnimationConfig;
        Class? cls = asset.PlayerClass;
        if (config is not { IsValid: true } || cls is not { IsValid: true })
            throw new InvalidOperationException(def.Label + "'s character data is incomplete");
        if (cls.DefaultObject is not RPawnPlayerCombat defaults || !defaults.IsValid)
            throw new InvalidOperationException(def.Label + "'s pawn class has no defaults");

        asset.AddToRoot();
        config.AddToRoot();
        kit = new Kit { Def = def, Asset = asset, Config = config, PawnClass = cls, Defaults = defaults };
        _kits[def.Key] = kit;
        Log.Info("Loaded " + def.Label + "'s character kit: " + asset.GetPathName(null) + ", pawn class " + cls.Name + ".");
        return kit;
    }

    private static RAddContentPlayerCharacter? FindAsset(string name)
    {
        foreach (RAddContentPlayerCharacter asset in World.All<RAddContentPlayerCharacter>())
        {
            if (asset.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                return asset;
        }

        return null;
    }
}

/// <summary>
/// The animation sets a player pawn resolves animation names from: its mesh and its weapon configs (the unarmed one,
/// the override and everything they extend). The game looks a name up set by set, so a set put first wins over a
/// later one with the same names, and a set put last only fills in names nobody else has.
/// </summary>
internal static class PlayerAnimSets
{
    public static IEnumerable<TArray<AnimSet>> Lists(RPawnPlayerCombat pawn)
    {
        if (pawn.Mesh is { IsValid: true } mesh)
            yield return mesh.AnimSets;

        var seen = new HashSet<nint>();
        var pending = new Stack<RWeaponConfig?>();
        pending.Push(pawn.UnarmedWeaponConfig);
        pending.Push(pawn.OverrideUnarmedWeaponConfig);
        while (pending.Count > 0)
        {
            RWeaponConfig? config = pending.Pop();
            if (config is not { IsValid: true } || !seen.Add(config.Ptr))
                continue;

            yield return config.AnimSets;
            TArray<RWeaponConfig> extends = config.Extends;
            for (int i = 0; i < extends.Count; i++)
                pending.Push(extends[i]);
        }
    }

    public static bool Contains(TArray<AnimSet> sets, AnimSet set)
    {
        for (int i = 0; i < sets.Count; i++)
        {
            if (sets[i] is { } s && s.Ptr == set.Ptr)
                return true;
        }

        return false;
    }

    /// <summary>Puts a set first everywhere, so its animations override same-named ones.</summary>
    public static void AddFirst(RPawnPlayerCombat pawn, AnimSet set)
    {
        foreach (TArray<AnimSet> sets in Lists(pawn))
        {
            if (!Contains(sets, set))
                sets.Insert(0, set);
        }
    }

    /// <summary>Puts a set last everywhere, so it only supplies animations nobody else has.</summary>
    public static void AddLast(RPawnPlayerCombat pawn, AnimSet set)
    {
        foreach (TArray<AnimSet> sets in Lists(pawn))
        {
            if (!Contains(sets, set))
                sets.Push(set);
        }
    }

    public static void Remove(RPawnPlayerCombat pawn, AnimSet set)
    {
        foreach (TArray<AnimSet> sets in Lists(pawn))
        {
            for (int i = sets.Count - 1; i >= 0; i--)
            {
                if (sets[i] is { } s && s.Ptr == set.Ptr)
                    sets.RemoveAt(i);
            }
        }
    }

    /// <summary>Animation names in <paramref name="set"/> the pawn can already play from its own sets.</summary>
    public static List<string> Clashes(RPawnPlayerCombat pawn, AnimSet set)
    {
        var clashes = new List<string>();
        if (pawn.Mesh is not { IsValid: true } mesh)
            return clashes;

        TArray<AnimSequence> sequences = set.Sequences;
        for (int i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] is not { IsValid: true } sequence)
                continue;
            FName name = sequence.SequenceName;
            if (mesh.FindAnimSequence(name) is { IsValid: true })
                clashes.Add(name.ToString());
        }

        return clashes;
    }

    /// <summary>Tells the mesh its sets changed. Needed after any edit, or the running animations keep the old lookup.</summary>
    public static void Refresh(RPawnPlayerCombat pawn)
    {
        if (pawn.Mesh is { IsValid: true } mesh)
            Safe.Run("anim set refresh", mesh.UpdateAnimations);
    }
}
