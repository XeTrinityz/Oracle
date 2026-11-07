using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > CHARACTER > MOVEMENT STYLE: move like another playable character while staying who you are. Player
/// animations are found by name through the pawn's anim sets in order (RPawnPlayerAnim.AddMovementMovesToWeaponConfig
/// builds that list from the character's config: character-specific, climbing, movement and gadget sets), so putting
/// another character's movement, climbing and glide sets first makes every shared name (idles, walks, runs, turns)
/// resolve to theirs, and what they don't have stays yours. The idle overlays and turn sets the game reads from the
/// animation config are swapped on the config itself and put back afterwards.
/// </summary>
internal sealed class MovementStyle : Module
{
    public static readonly MovementStyle I = new();

    private const string Title = "MOVEMENT STYLE";
    private const string Own = "own";

    private Choice _pick = null!;
    private RPawnPlayerCombat? _pawn;
    private string _key = Own;
    private (string Key, nint Pawn) _failed;
    private readonly List<AnimSet> _added = new();

    private readonly Held<RPawnPlayerConfig, AnimSet?>[] _configSets =
    {
        Field("default idle overlay", c => c.DefaultIdleOverlayAnimSet, (c, v) => c.DefaultIdleOverlayAnimSet = v!),
        Field("crouch idle overlay", c => c.CrouchIdleOverlayAnimSet, (c, v) => c.CrouchIdleOverlayAnimSet = v!),
        Field("cover idle overlay", c => c.CoverIdleOverlayAnimSet, (c, v) => c.CoverIdleOverlayAnimSet = v!),
        Field("dangle idle overlay", c => c.DangleIdleOverlayAnimSet, (c, v) => c.DangleIdleOverlayAnimSet = v!),
        Field("hang idle overlay", c => c.HangIdleOverlayAnimSet, (c, v) => c.HangIdleOverlayAnimSet = v!),
        Field("predator idle overlay", c => c.SilentPredatorIdleOverlayAnimSet, (c, v) => c.SilentPredatorIdleOverlayAnimSet = v!),
        Field("combat idles", c => c.CombatIdlesAnimSet, (c, v) => c.CombatIdlesAnimSet = v!),
        Field("default turns", c => c.DefaultTurnAnimSet, (c, v) => c.DefaultTurnAnimSet = v!),
        Field("crouch turns", c => c.CrouchTurnAnimSet, (c, v) => c.CrouchTurnAnimSet = v!),
        Field("gadget turns", c => c.GadgetTurnAnimSet, (c, v) => c.GadgetTurnAnimSet = v!),
        Field("grate walk turns", c => c.GrateWalkTurnAnimSet, (c, v) => c.GrateWalkTurnAnimSet = v!),
        Field("railing turns", c => c.RailingTurnAnimSet, (c, v) => c.RailingTurnAnimSet = v!),
        Field("wire turns", c => c.WireTurnAnimSet, (c, v) => c.WireTurnAnimSet = v!),
    };

    private static Held<RPawnPlayerConfig, AnimSet?> Field(string what, Func<RPawnPlayerConfig, AnimSet?> get, Action<RPawnPlayerConfig, AnimSet?> set) => new(what, get, set);

    public override void Build()
    {
        string[] keys = new[] { Own }.Concat(Characters.Roster.Select(d => d.Key)).ToArray();
        string[] labels = new[] { "MY OWN" }.Concat(Characters.Roster.Select(d => d.Label)).ToArray();
        _pick = AddChoice("move_style", Title,
            "Move like another character while staying who you are: their idles, walks, runs, turns, climbs and glides, with your own filling in whatever they lack. Pairs well with DISGUISE. Batgirl, Red Hood and Harley Quinn need their story pack DLC. MY OWN puts your animations back.",
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

        if (_pawn != null && (!_pawn.IsValid || _pawn.Ptr != pawn.Ptr))
            Forget();

        string key = _pick.CurrentKey;
        if (key == Own)
        {
            Restore();
            return;
        }

        if ((_pawn != null && _key == key) || (_failed.Key == key && _failed.Pawn == pawn.Ptr))
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
        _pawn = pawn;
        _key = def.Key;
        if (pawn.Class.Ptr == kit.PawnClass.Ptr)
            return;

        RPawnPlayerConfig theirs = kit.Config;
        RPawnPlayerConfig? mine = pawn.AnimationConfig is { IsValid: true } c ? c : null;

        // Their sets go first, in the order their own pawn would list them, so the first keeps winning.
        var wanted = new List<AnimSet>();
        foreach (TArray<AnimSet> list in new[] { theirs.CharacterSpecificAnimSets, theirs.ClimbingAnimSets, theirs.MovementAnimSets })
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is { IsValid: true } set && !wanted.Any(w => w.Ptr == set.Ptr) && (mine == null || !OwnSet(mine, set)))
                    wanted.Add(set);
            }
        }

        for (int i = wanted.Count - 1; i >= 0; i--)
        {
            PlayerAnimSets.AddFirst(pawn, wanted[i]);
            _added.Add(wanted[i]);
        }

        if (mine != null)
        {
            foreach (Held<RPawnPlayerConfig, AnimSet?> field in _configSets)
            {
                AnimSet? value = ReadTheirs(field, theirs);
                if (value is { IsValid: true })
                    field.Set(mine, value);
            }
        }

        PlayerAnimSets.Refresh(pawn);
        Log.Info(Title + ": " + def.Label + "'s movement on " + pawn.Class.Name + " (" + wanted.Count + " anim sets first).");
    }

    /// <summary>Reads the character's value of the same config field a Held manages: Held knows the getter, nothing else does.</summary>
    private static AnimSet? ReadTheirs(Held<RPawnPlayerConfig, AnimSet?> field, RPawnPlayerConfig theirs) => field.Peek(theirs);

    private static bool OwnSet(RPawnPlayerConfig mine, AnimSet set)
    {
        foreach (TArray<AnimSet> list in new[] { mine.CharacterSpecificAnimSets, mine.ClimbingAnimSets, mine.MovementAnimSets, mine.GadgetAnimSets })
        {
            if (PlayerAnimSets.Contains(list, set))
                return true;
        }

        return false;
    }

    private void Restore()
    {
        RPawnPlayerCombat? pawn = _pawn;
        bool had = _added.Count > 0;
        _pawn = null;
        _key = Own;
        _failed = default;
        foreach (Held<RPawnPlayerConfig, AnimSet?> field in _configSets)
            field.Restore();

        if (pawn is { IsValid: true })
        {
            foreach (AnimSet set in _added)
                Safe.Run("movement style set", () => PlayerAnimSets.Remove(pawn, set));
            if (had)
            {
                PlayerAnimSets.Refresh(pawn);
                Log.Info(Title + ": your own movement is back.");
            }
        }

        _added.Clear();
    }

    private void Forget()
    {
        _pawn = null;
        _key = Own;
        _failed = default;
        _added.Clear();
        foreach (Held<RPawnPlayerConfig, AnimSet?> field in _configSets)
            field.Restore();
    }
}
