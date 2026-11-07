using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > COMBAT > BATCLAW ZIP KICK: the Batclaw does Robin's zip kick on thugs. The game already has the move:
/// RHarpoonGun.FireHarpoon hands a thug to HarpoonThugMove when the gun has one (Robin's claw does, Batman's doesn't),
/// and that move, RSpecialMoveConfig_RobinBatClawAttack, flies the player in and kicks. So Batman's gun gets Robin's
/// move, his pose list gets the ZipKickFlight stance from Robin's pose config, and his anim sets get Robin's zip kick
/// animations. The move reads its damage from Robin's claw class, which other claws aren't, so the hit is dealt here
/// with Robin's value instead. Works for every character who carries a Batclaw.
/// </summary>
internal sealed class ZipKick : Module
{
    public static readonly ZipKick I = new();

    private const string Title = "ZIP KICK";
    private const string ConfigPath = "Robin_Special_Moves.BatClawAttack";
    private const string ClawClassPath = "BmScript.RRobinBatClaw";
    private const string PoseName = "OraclePoseConfig_ZipKick";
    private const int FallbackDamage = 2;
    private static readonly FName FlightStance = new("ZipKickFlight");
    private static readonly FName Spine = new("Bip01_Spine2");

    private Toggle _on = null!;
    private RSpecialMoveConfig_RobinBatClawAttack? _config;
    private AnimSet? _kickSet;
    private RPoseConfig? _poses;
    private int _damage = FallbackDamage;
    private bool _loadFailed;

    private readonly Held<RHarpoonGun, RSpecialMoveConfig?> _thugMove = new("batclaw thug move", g => g.HarpoonThugMove, (g, v) => g.HarpoonThugMove = v!);
    private RPawnPlayerCombat? _dressed;
    private readonly List<TArray<RPoseConfig>> _poseLists = new();

    public override void Build()
    {
        _on = AddToggle("zip_kick", "BATCLAW ZIP KICK",
            "Your Batclaw does Robin's zip kick: fire it at a thug and you zip in with a flying kick instead of pulling him to you. Hits count for your combo. Any character with a Batclaw can use it.",
            on => { if (!on) Restore(); }).Maintain(Keep);
    }

    public override void WorldChanged() => Forget();

    public override void LeftGame() => Forget();

    public override void Shutdown() => Restore();

    private void Keep()
    {
        if (World.Player is not RPawnPlayerCombat { IsValid: true } pawn || pawn is not RPawnPlayer player)
            return;

        if (_dressed != null && (!_dressed.IsValid || _dressed.Ptr != pawn.Ptr))
            Forget();

        if (!EnsureLoaded() || _config == null)
            return;

        RHarpoonGun? gun = player.HarpoonGun;
        if (gun is not { IsValid: true } || gun is RRobinBatClaw)
            return;

        _thugMove.Set(gun, _config);
        if (_dressed == null)
            Dress(pawn);
    }

    /// <summary>Finds Robin's move, animations and flight poses, loading his package once.</summary>
    private bool EnsureLoaded()
    {
        if (_config is { IsValid: true } && _kickSet is { IsValid: true } && _poses is { IsValid: true })
            return true;
        if (_loadFailed)
            return false;

        try
        {
            Characters.Def robin = PlayerKits.Find("robin") ?? throw new InvalidOperationException("Robin is not in the roster");
            PlayerKits.Kit kit = PlayerKits.Load(robin);
            _config = Game.FindObject<RSpecialMoveConfig_RobinBatClawAttack>(ConfigPath) ?? throw new InvalidOperationException("Robin's zip kick move is missing");
            _config.AddToRoot();
            _kickSet = _config.PlayerAnimset is { IsValid: true } set ? set : throw new InvalidOperationException("Robin's zip kick animations are missing");
            _kickSet.AddToRoot();
            _poses = FlightPoses(kit.Config) ?? throw new InvalidOperationException("Robin's zip kick poses are missing");
            if (Game.FindObject<Class>(ClawClassPath)?.DefaultObject is RRobinBatClaw claw && claw.IsValid && claw.DamageGiven > 0)
                _damage = claw.DamageGiven;
            Log.Info(Title + ": Robin's move " + _config.GetPathName(null) + ", " + _kickSet.Name + ", " + _poses.Poses.Count + " flight poses, damage " + _damage + ".");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _loadFailed = true;
            Log.Info(Title + " unavailable: " + ex.Message + ".");
            Overlay.Notice(Title, ex.Message.ToUpperInvariant(), false);
            return false;
        }
    }

    /// <summary>A pose config holding only Robin's ZipKickFlight poses and transitions, copied from the one that has them.</summary>
    private static RPoseConfig? FlightPoses(RPawnPlayerConfig config)
    {
        // A copy from before a hot reload is reused rather than made again.
        foreach (RPoseConfig made in World.All<RPoseConfig>())
        {
            if (made.Name.ToString() == PoseName)
                return made;
        }

        TArray<RPoseConfig> configs = config.PoseConfigs;
        for (int i = 0; i < configs.Count; i++)
        {
            RPoseConfig? source = configs[i];
            if (source is not { IsValid: true } || !HasFlight(source))
                continue;

            var copy = new RPoseConfig(source.Outer, PoseName, 0, source);
            TArray<RPoseConfig.FPose> poses = copy.Poses;
            for (int p = poses.Count - 1; p >= 0; p--)
            {
                if (poses[p].MovementStance.Index != FlightStance.Index)
                    poses.RemoveAt(p);
            }

            TArray<RPoseConfig.FTransition> transitions = copy.Transitions;
            for (int t = transitions.Count - 1; t >= 0; t--)
            {
                RPoseConfig.FTransition tr = transitions[t];
                if (tr.MovementStanceA.Index != FlightStance.Index && tr.MovementStanceB.Index != FlightStance.Index)
                    transitions.RemoveAt(t);
            }

            copy.AddToRoot();
            return copy;
        }

        return null;
    }

    private static bool HasFlight(RPoseConfig config)
    {
        TArray<RPoseConfig.FPose> poses = config.Poses;
        for (int i = 0; i < poses.Count; i++)
        {
            if (poses[i].MovementStance.Index == FlightStance.Index)
                return true;
        }

        return false;
    }

    /// <summary>Gives the pawn the flight poses and the kick animations.</summary>
    private void Dress(RPawnPlayerCombat pawn)
    {
        if (_poses == null || _kickSet == null)
            return;

        _dressed = pawn;
        foreach (TArray<RPoseConfig> list in PoseLists(pawn))
        {
            if (!Has(list, _poses))
            {
                list.Push(_poses);
                _poseLists.Add(list);
            }
        }

        // Zip kick names the game can already play from your own sets would win over Robin's if his set went last,
        // and his would win over yours if it went first. Only put it first when nothing clashes.
        List<string> clashes = PlayerAnimSets.Clashes(pawn, _kickSet);
        if (clashes.Count == 0)
            PlayerAnimSets.AddFirst(pawn, _kickSet);
        else
        {
            PlayerAnimSets.AddLast(pawn, _kickSet);
            Log.Info(Title + ": " + clashes.Count + " of Robin's kick animations share names with yours (" + string.Join(", ", clashes.Take(6)) + "); yours are kept.");
        }

        PlayerAnimSets.Refresh(pawn);
    }

    private static IEnumerable<TArray<RPoseConfig>> PoseLists(RPawnPlayerCombat pawn)
    {
        if (pawn.AnimationConfig is { IsValid: true } config)
            yield return config.PoseConfigs;

        var seen = new HashSet<nint>();
        var pending = new Stack<RWeaponConfig?>();
        pending.Push(pawn.UnarmedWeaponConfig);
        pending.Push(pawn.OverrideUnarmedWeaponConfig);
        while (pending.Count > 0)
        {
            RWeaponConfig? weapon = pending.Pop();
            if (weapon is not { IsValid: true } || !seen.Add(weapon.Ptr))
                continue;

            yield return weapon.PoseConfigs;
            TArray<RWeaponConfig> extends = weapon.Extends;
            for (int i = 0; i < extends.Count; i++)
                pending.Push(extends[i]);
        }
    }

    private static bool Has(TArray<RPoseConfig> list, RPoseConfig config)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is { } c && c.Ptr == config.Ptr)
                return true;
        }

        return false;
    }

    private void Restore()
    {
        _thugMove.Restore();
        // The pawn that was dressed, or whoever is played now: a world change forgets the pawn but the shared
        // animation config keeps the poses until they are taken out here.
        RPawnPlayerCombat? pawn = _dressed is { IsValid: true } ? _dressed : World.Player;
        _dressed = null;
        if (pawn is { IsValid: true } && _poses != null && _kickSet != null)
        {
            RPoseConfig poses = _poses;
            AnimSet kick = _kickSet;
            Safe.Run("zip kick poses", () =>
            {
                foreach (TArray<RPoseConfig> list in PoseLists(pawn))
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] is { } c && c.Ptr == poses.Ptr)
                            list.RemoveAt(i);
                    }
                }
            });
            Safe.Run("zip kick anims", () => PlayerAnimSets.Remove(pawn, kick));
            PlayerAnimSets.Refresh(pawn);
        }

        _poseLists.Clear();
    }

    private void Forget()
    {
        _dressed = null;
        _poseLists.Clear();
        _thugMove.Restore();
    }

    // ---- the hit

    /// <summary>
    /// The move's own hit reads DamageGiven from Robin's claw class; on any other claw that is None and the kick
    /// would do nothing. This deals the same hit the script does, with Robin's damage.
    /// </summary>
    [Redirect(typeof(RSpecialMoveInstance_RobinBatClawAttack), nameof(RSpecialMoveInstance_RobinBatClawAttack.TakeDownVillain))]
    private static void TakeDownVillainRedirect(RSpecialMoveInstance_RobinBatClawAttack self)
    {
        if (!Safe.Try("zip kick hit", () => I.Hit(self)))
            self.TakeDownVillain();
    }

    private bool Hit(RSpecialMoveInstance_RobinBatClawAttack self)
    {
        if (!_on.On || self.PlayerPawn is not RPawnPlayer { IsValid: true } pawn || pawn.HarpoonGun is RRobinBatClaw)
            return false;

        // Blocked, or no thug: the game's own version does nothing either.
        if (self.TargetPawn is not RPawnVillain { IsValid: true } villain || self.VillainBlocked)
            return true;

        bool close = (int)self.KickAnimType == 0;
        Vector3 velocity = pawn.Velocity;
        Vector3 direction = velocity.LengthSquared() > 1f ? Vector3.Normalize(velocity) : Vector3.Zero;

        var info = new RPawnCombat.FDamageInfo();
        info.Attacker = pawn;
        info.DamageAmount = _damage;
        info.BoneName = Spine;
        info.Impulse = (direction + new Vector3(0f, 0f, 0.8f)) * 3000f;
        info.DamageType = close ? RDmgType_Ragdoll.StaticClass() : RDmgType_ZipKick.StaticClass();
        info.bWillKnockOverOtherPawns = !close;
        info.bForceQuickMotor = true;
        info.StunTime = 5f;

        int result = (int)villain.DamagedBy(info);
        if ((result == 1 || result == 2) && pawn.Controller is RPlayerControllerCombat pc && pc.IsValid && pc.IsInCombat(false))
        {
            pc.IncCombo(villain, 1, 0, false);
            pc.MovePerformed((RGameInfo.EComboMoveType)36, villain, false, 0, 0);
        }

        pawn.PlaySound_Impact(villain, true, _damage > 999, false, true, true, false, false, false, false, false);
        return true;
    }
}
