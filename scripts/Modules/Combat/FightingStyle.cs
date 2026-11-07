using System.Reflection;
using System.Runtime.InteropServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > COMBAT > FIGHTING STYLE: fight with another playable character's moves while staying who you are.
/// A character's moveset is built the way the game builds it for them when they spawn (RPawnPlayerCombat.PostBeginPlay):
/// the pawn class's default move lists (strikes, counters, takedowns, throws, weapon moves), the strikes detected
/// from the character's strike anim set, and the character's own SetupPlayerSpecificCombatAnimsets, which pairs
/// player and thug anim sets and picks the sets used against each enemy type. That last function is per character
/// and not callable on another pawn, so its statements were read from the game's script and are replayed from
/// <see cref="FightingStyleData"/>. Everything is put back exactly when the style goes off.
/// </summary>
internal sealed unsafe class FightingStyle : Module
{
    public static readonly FightingStyle I = new();

    private const string Title = "FIGHTING STYLE";
    private const string Own = "own";

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public IntPtr Data;
        public int Num;
        public int Max;
    }

    /// <summary>One of the pawn's move lists: its array header and, for elements that hold arrays themselves, where those sit.</summary>
    private sealed record ListDef(string Name, Func<RPawnPlayerCombat, IArrayInfo> Get, (int Offset, int Stride)[] Inner, bool StartEmpty = false);

    private interface IArrayInfo
    {
        IntPtr Ptr { get; }
        int Stride { get; }
    }

    private sealed record ArrayInfo(IntPtr Ptr, int Stride) : IArrayInfo;

    private static IArrayInfo Of<T>(TArray<T> array) => new ArrayInfo(array.Ptr, array.Stride);

    private static readonly (int, int)[] NoInner = Array.Empty<(int, int)>();

    // Inner arrays are the struct's own TArray fields, offsets from the generated layout: FSimultaneousCounterInfo
    // holds StartLocation (Vector3), AnimNames (FName), MeetingPointEnd/GrabWeapon*Time (float), CounterOrder (int) and
    // ImpactBone (byte enum); FWeaponDestroyInfo holds DetachBoneList (FName).
    private static readonly ListDef[] Lists =
    {
        new("AutoStrikeList", p => Of(p.AutoStrikeList), NoInner, StartEmpty: true),
        new("CounterList", p => Of(p.CounterList), NoInner),
        new("SimultaneousCounterList", p => Of(p.SimultaneousCounterList), new[] { (32, 12), (48, 8), (64, 4), (80, 4), (96, 4), (112, 4), (128, 1) }),
        new("TakedownList", p => Of(p.TakedownList), NoInner),
        new("ThrowList", p => Of(p.ThrowList), NoInner),
        new("WeaponDestroyList", p => Of(p.WeaponDestroyList), new[] { (28, 8) }),
        new("WeaponSnatchList", p => Of(p.WeaponSnatchList), NoInner),
        new("DualStrikeTypeList", p => Of(p.DualStrikeTypeList), NoInner),
        new("DualStrikeInfoList", p => Of(p.DualStrikeInfoList), NoInner),
        new("PairedAnimsets", p => Of(p.PairedAnimsets), NoInner),
    };

    /// <summary>The class-level combat moves a character brings along (Nightwing's group stun, for one).</summary>
    private static readonly string[] MoveClassProps = { "PrimaryGroupCombatMove", "SecondaryGroupCombatMove" };

    private sealed class InnerCopy
    {
        public int Element;
        public int Offset;
        public TArray<byte> Buffer = null!;
    }

    private sealed class ListCopy
    {
        public ListDef Def = null!;
        public Header Original;
        public TArray<byte>? Buffer;
        public readonly List<InnerCopy> Inner = new();
    }

    /// <summary>Everything changed on one pawn, so it can be undone in reverse.</summary>
    private sealed class Dressed
    {
        public string Key = "";
        public string Label = "";
        public RPawnPlayerCombat Pawn = null!;
        public readonly List<ListCopy> Lists = new();
        public readonly Dictionary<PropertyInfo, object?> Props = new();
        public readonly List<AnimSet> AddedSets = new();
    }

    private Choice _pick = null!;
    private Dressed? _on;
    private (string Key, nint Pawn) _failed;
    private readonly HashSet<string> _missingLogged = new(StringComparer.Ordinal);

    private readonly Held<RPawnPlayerConfig, AnimSet?> _combatSet = new("combat anim set", c => c.CombatAnimset, (c, v) => c.CombatAnimset = v!);

    private static PropertyInfo[]? _animSetProps;

    /// <summary>Every anim set the pawn picks per situation, inherited ones included: the character's choices replace them all.</summary>
    private static PropertyInfo[] AnimSetProps => _animSetProps ??= typeof(RPawnPlayerCombat)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(AnimSet) && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    public override void Build()
    {
        string[] keys = new[] { Own }.Concat(Characters.Roster.Select(d => d.Key)).ToArray();
        string[] labels = new[] { "MY OWN" }.Concat(Characters.Roster.Select(d => d.Label)).ToArray();
        _pick = AddChoice("fight_style", Title,
            "Fight with another character's moves while staying who you are: their strikes, counters, takedowns, throws and weapon moves, built the way the game builds them for that character. Batgirl, Red Hood and Harley Quinn need their story pack DLC. Picking MY OWN puts your moves back.",
            keys, labels, _ => Refresh());
        _pick.Maintain(Refresh);
    }

    public override void WorldChanged() => Forget();

    public override void LeftGame() => Forget();

    public override void Shutdown() => Restore();

    private void Refresh()
    {
        if (World.Player is not RPawnPlayerCombat { IsValid: true } pawn)
        {
            Forget();
            return;
        }

        if (_on != null && (!_on.Pawn.IsValid || _on.Pawn.Ptr != pawn.Ptr))
            Forget();

        string key = _pick.CurrentKey;
        if (key == Own)
        {
            Restore();
            return;
        }

        if (_on?.Key == key || (_failed.Key == key && _failed.Pawn == pawn.Ptr))
            return;

        Restore();
        Characters.Def def = PlayerKits.Find(key) ?? throw new InvalidOperationException("unknown character " + key);
        try
        {
            Install(pawn, def);
        }
        catch (InvalidOperationException ex)
        {
            _failed = (key, pawn.Ptr);
            Log.Info(Title + " " + def.Label + " refused: " + ex.Message + ".");
            Overlay.Notice(Title, ex.Message.ToUpperInvariant(), false);
        }
    }

    private void Install(RPawnPlayerCombat pawn, Characters.Def def)
    {
        PlayerKits.Kit kit = PlayerKits.Load(def);
        var dressed = new Dressed { Key = def.Key, Label = def.Label, Pawn = pawn };
        if (pawn.Class.Ptr == kit.PawnClass.Ptr)
        {
            // Already that character: their moves are the ones in use.
            _on = dressed;
            return;
        }

        try
        {
            // 1. The anim sets the character uses against each kind of enemy, as their pawn starts with them.
            foreach (PropertyInfo p in AnimSetProps)
                Write(dressed, p, p.GetValue(kit.Defaults));
            // A group move is a combat move class of its own, written for that character's pawn: Red Hood's fires his
            // guns, Catwoman's and Harley's crack the whip, and their natives expect those parts on the pawn. Only
            // moves from the same pawn family come along; Nightwing's group stun is known to run on Batman.
            if (SameFamily(pawn, kit))
            {
                foreach (string name in MoveClassProps)
                {
                    if (typeof(RPawnPlayerCombat).GetProperty(name) is { } p)
                        Write(dressed, p, p.GetValue(kit.Defaults));
                }
            }

            // 2. Their move lists, copied out of their class defaults into memory the game may grow and this module owns.
            foreach (ListDef def2 in Lists)
                CopyList(dressed, pawn, kit.Defaults, def2);

            // 3. The game's own setup for them, replayed on you: strikes from their strike set, then their pairings.
            if (kit.Config.StrikeAnimset is { IsValid: true } strikes)
                pawn.DetectStrikes(strikes);
            RunRecipe(dressed, pawn, kit.PawnClass);

            // 4. Their combat stance: the set the game idles and moves with between blows.
            if (kit.Config.CombatAnimset is { IsValid: true } combat)
            {
                if (pawn.AnimationConfig is { IsValid: true } config)
                    _combatSet.Set(config, combat);
                PlayerAnimSets.AddFirst(pawn, combat);
                dressed.AddedSets.Add(combat);
            }

            PlayerAnimSets.Refresh(pawn);
        }
        catch
        {
            Undo(dressed);
            throw;
        }

        _on = dressed;
        Log.Info(Title + ": " + def.Label + "'s moves on " + pawn.Class.Name + " (" + string.Join(", ", dressed.Lists.Select(l => l.Def.Name + " " + (*(Header*)l.Def.Get(pawn).Ptr).Num)) + ").");
    }

    private static bool SameFamily(RPawnPlayerCombat pawn, PlayerKits.Kit kit) =>
        kit.Def.Key == "nightwing"
        || (pawn is RPawnPlayerBmBase && kit.Defaults is RPawnPlayerBmBase)
        || (pawn is RPawnPlayerCatwomanBase && kit.Defaults is RPawnPlayerCatwomanBase)
        || pawn.Class.Ptr == kit.PawnClass.Ptr;

    private void Write(Dressed dressed, PropertyInfo p, object? value)
    {
        if (!dressed.Props.ContainsKey(p))
            dressed.Props[p] = p.GetValue(dressed.Pawn);
        p.SetValue(dressed.Pawn, value);
    }

    // ---- the recipe

    private void RunRecipe(Dressed dressed, RPawnPlayerCombat pawn, Class cls)
    {
        for (Struct? c = cls; c != null; c = c.SuperStruct)
        {
            if (FightingStyleData.Recipes.TryGetValue(c.Name.ToString(), out string[]? steps))
            {
                RunSteps(dressed, pawn, steps, 0);
                return;
            }
        }

        Log.Info(Title + ": no recipe for " + cls.Name + ", using its defaults alone.");
    }

    private void RunSteps(Dressed dressed, RPawnPlayerCombat pawn, string[] steps, int depth)
    {
        if (depth > 4)
            return;

        foreach (string step in steps)
        {
            string[] a = step.Split(' ');
            switch (a[0])
            {
                case "^":
                    if (FightingStyleData.Recipes.TryGetValue(a[1], out string[]? parent))
                        RunSteps(dressed, pawn, parent, depth + 1);
                    break;
                case "S":
                    if (Set(a[2]) is { } set)
                    {
                        PropertyInfo? p = pawn.GetType().GetProperty(a[1], BindingFlags.Public | BindingFlags.Instance);
                        if (p == null || !p.CanWrite || p.PropertyType != typeof(AnimSet))
                            Missing("property " + a[1] + " on " + pawn.Class.Name);
                        else
                            Write(dressed, p, set);
                    }
                    break;
                case "P":
                    if (Set(a[1]) is { } player && Set(a[2]) is { } villain)
                        pawn.AddPairedAnimset(null!, player, villain, null!);
                    break;
                case "D":
                    if (Set(a[1]) is { } strikes)
                        pawn.DetectStrikes(strikes);
                    break;
                case "B":
                    if (Set(a[1]) is { } beatdown)
                        pawn.DetectStrikesWithOptionalBeatdownEnds(beatdown, a[2] == "1");
                    break;
                case "T":
                    if (Set(a[2]) is { } throwSet && Set(a[3]) is { } target)
                    {
                        var info = new RPawnPlayerCombat.FThrowInfo();
                        info.ThrowAnimName = new FName(a[1]);
                        info.ThrowAnimset = throwSet;
                        info.ThrowTargetAnimset = target;
                        info.bActive = true;
                        pawn.ThrowList.Push(info);
                    }
                    break;
            }
        }
    }

    private AnimSet? Set(string path)
    {
        AnimSet? set = Game.FindObject<AnimSet>(path);
        if (set == null)
            Missing("anim set " + path);
        return set;
    }

    private void Missing(string what)
    {
        if (_missingLogged.Add(what))
            Log.Info(Title + ": " + what + " is not available, skipped.");
    }

    // ---- move lists

    /// <summary>
    /// Points the pawn's list at a copy of the character's defaults. The copy lives in game-allocated memory, so the
    /// game may grow it (detected strikes, paired counters); arrays inside the elements are copied too, because the
    /// engine frees them with the pawn and the defaults must never lose theirs.
    /// </summary>
    private static void CopyList(Dressed dressed, RPawnPlayerCombat pawn, RPawnPlayerCombat defaults, ListDef def)
    {
        IArrayInfo mine = def.Get(pawn);
        IArrayInfo theirs = def.Get(defaults);
        var copy = new ListCopy { Def = def, Original = *(Header*)mine.Ptr };
        Header from = def.StartEmpty ? default : *(Header*)theirs.Ptr;
        int stride = mine.Stride;
        Header installed = default;

        if (from.Num > 0 && from.Data != IntPtr.Zero)
        {
            int bytes = from.Num * stride;
            copy.Buffer = new TArray<byte>(bytes);
            Header* mineHeader = (Header*)copy.Buffer.Ptr;
            Buffer.MemoryCopy((void*)from.Data, (void*)mineHeader->Data, bytes, bytes);
            mineHeader->Num = bytes;

            foreach ((int offset, int innerStride) in def.Inner)
            {
                for (int e = 0; e < from.Num; e++)
                {
                    Header* inner = (Header*)(mineHeader->Data + e * stride + offset);
                    if (inner->Num <= 0 || inner->Data == IntPtr.Zero)
                    {
                        *inner = default;
                        continue;
                    }

                    int innerBytes = inner->Num * innerStride;
                    var innerCopy = new TArray<byte>(innerBytes);
                    Header* innerHeader = (Header*)innerCopy.Ptr;
                    Buffer.MemoryCopy((void*)inner->Data, (void*)innerHeader->Data, innerBytes, innerBytes);
                    innerHeader->Num = innerBytes;
                    inner->Data = innerHeader->Data;
                    inner->Max = inner->Num;
                    copy.Inner.Add(new InnerCopy { Element = e, Offset = offset, Buffer = innerCopy });
                }
            }

            installed = new Header { Data = mineHeader->Data, Num = from.Num, Max = from.Num };
        }

        *(Header*)mine.Ptr = installed;
        dressed.Lists.Add(copy);
    }

    /// <summary>Puts the pawn's own list back and frees the copy, unless the game moved it: then the game owns that memory.</summary>
    private static void RestoreList(RPawnPlayerCombat pawn, ListCopy copy)
    {
        IArrayInfo mine = copy.Def.Get(pawn);
        Header now = *(Header*)mine.Ptr;
        *(Header*)mine.Ptr = copy.Original;
        if (copy.Buffer == null)
            return;

        Header* mineHeader = (Header*)copy.Buffer.Ptr;
        if (now.Data != mineHeader->Data)
            return;

        foreach (InnerCopy inner in copy.Inner)
        {
            Header* slot = (Header*)(mineHeader->Data + inner.Element * mine.Stride + inner.Offset);
            if (slot->Data == ((Header*)inner.Buffer.Ptr)->Data)
                inner.Buffer.Dispose();
        }

        copy.Buffer.Dispose();
    }

    // ---- undo

    private void Restore()
    {
        Dressed? on = _on;
        _on = null;
        _failed = default;
        if (on == null)
            return;

        if (on.Pawn.IsValid)
        {
            Undo(on);
            Log.Info(Title + ": your own moves are back.");
        }
        else
        {
            _combatSet.Restore();
        }
    }

    private void Undo(Dressed on)
    {
        RPawnPlayerCombat pawn = on.Pawn;
        foreach (AnimSet set in on.AddedSets)
            Safe.Run("fighting style sets", () => PlayerAnimSets.Remove(pawn, set));
        on.AddedSets.Clear();
        _combatSet.Restore();

        for (int i = on.Lists.Count - 1; i >= 0; i--)
        {
            ListCopy copy = on.Lists[i];
            Safe.Run("fighting style " + copy.Def.Name, () => RestoreList(pawn, copy));
        }

        on.Lists.Clear();
        foreach ((PropertyInfo p, object? value) in on.Props)
            Safe.Run("fighting style " + p.Name, () => p.SetValue(pawn, value));
        on.Props.Clear();
        PlayerAnimSets.Refresh(pawn);
    }

    /// <summary>The pawn is gone or about to be: the engine frees what was installed on it, so nothing here may touch it again.</summary>
    private void Forget()
    {
        _on = null;
        _failed = default;
        _combatSet.Restore();
    }
}
