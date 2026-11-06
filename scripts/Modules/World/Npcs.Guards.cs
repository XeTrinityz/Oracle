using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using AimTurn = BmSDK.BmGame.RAnimUtil_PosePlayer.EAllowTurningToAim;
using FollowSpeed = BmSDK.BmGame.RBMBehaviour_Follow.MoveSpeed;

namespace CeZeY.Oracle;

/// <summary>
/// Bodyguards and gunfire for enemies on your side. A bodyguard (spawned with SIDE BODYGUARD) guards you: between
/// fights he follows you with the game's own RBMBehaviour_Follow (navmesh paths, stops within a set distance), and
/// in a fight he picks whoever threatens you most rather than whoever is nearest him. Melee guards are put into the
/// game's combat; gunmen are moved, aimed and fired by Oracle, the way the game's RBMAIAction_ShootActor does it.
/// </summary>
internal sealed partial class Npcs
{
    private const double ThinkSeconds = 0.5;
    private const double MoveSeconds = 0.5;
    private const double MakeFollowSeconds = 1.5;
    private const float WeaponReach = 180f;
    private const float FistReach = 190f;
    private const float FistStandoff = 110f;
    private const float FaceRange = 900f;
    private const double SwingGapMin = 0.25;
    private const double SwingGapMax = 0.8;
    private const float SwitchMargin = 400f;
    private const float SelfDefence = 1500f;
    private const float OnTopOfYou = 700f;
    private const float CatchUpRange = 4000f;
    private const float CatchUpInView = 8000f;

    private const float ChestHeight = 40f;
    private const double AimSettle = 0.35;
    private const double SightSeconds = 0.2;
    private const double SightGrace = 1.5;
    private const double BurstGapMin = 0.15;
    private const double BurstGapMax = 0.6;
    private const double ReloadSeconds = 1.6;
    private const float GunFar = 1800f;
    private const float GunNoSight = 350f;
    private const float GunMaxRange = 4500f;

    private static readonly FName Aiming = new("Aiming");
    private static readonly FName Relaxed = new("Relaxed");
    private static readonly FName AimName = new("OracleAim");

    private Slider _guardRange = null!;
    private Slider _guardGap = null!;
    private Toggle _keepUp = null!;
    private Toggle _huntGuards = null!;
    private Every _hunt;
    private readonly List<Pawn> _guardsNow = new();

    private const double HuntSeconds = 0.5;
    private const float HuntReach = 2000f;
    private const float HuntMargin = 200f;
    private const float HuntRelease = 400f;
    private const int HuntersPerGuard = 3;

    private void BuildGuards()
    {
        _guardRange = AddSlider("npc_guard_range", "PROTECT RANGE", "How far from you BODYGUARD spawns go after enemies. Anyone right next to a bodyguard is fought wherever you are.", 30f, 10f, 80f, 5f, 50f, Fmt.Metres).Pref();
        _guardGap = AddSlider("npc_guard_gap", "FOLLOW DISTANCE", "How close bodyguards stay to you between fights.", 3f, 1.5f, 12f, 0.5f, 6f, v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " M").Pref();
        _huntGuards = AddToggle("npc_guard_hunted", "ENEMIES FIGHT BODYGUARDS", "Enemies treat your bodyguards the way they treat you: one nearer a bodyguard than you goes for him, punching, slashing and shooting. Up to three on each bodyguard. Off: enemies only ever fight you.", on =>
        {
            if (!on)
                ReleaseTeam(t => t == Hunters);
        }, on: true).Pref();
        _keepUp = AddToggle("npc_guard_keep_up", "KEEP UP", "Bodyguards who fall far behind (you glided, grappled or drove off) catch up out of sight and appear behind you.", on: true).Pref();
    }

    /// <summary>A bodyguard who has someone to fight right now, for the spawner's marker.</summary>
    internal bool IsFighting(nint ptr) => _team.TryGetValue(ptr, out Loyal? l) && l.Guard && l.Target != 0;

    // ---- enemies who fight back

    /// <summary>
    /// Enemies pick their man the way they pick you: one nearer a standing bodyguard than you (by a margin, so he
    /// doesn't flip between you) joins the Hunters side, which Steer runs like a gang: OverrideTargetPawn on his
    /// bodyguard, melee hits landed through the same redirects, guns through Shoot. He's handed back to the game (see
    /// Steer) once the bodyguard is down or gone or you're nearer. At most a few per bodyguard, so you're never ignored.
    /// </summary>
    private void RecruitHunters(List<Pawn> near)
    {
        Pawn? me = World.Pawn;
        if (me == null)
            return;

        _guardsNow.Clear();
        int hunters = 0;
        foreach (Loyal l in _team.Values)
        {
            if (l.Team == Hunters)
                hunters++;
            else if (l.Guard && l.Team == WithYou && l.Pawn is { IsValid: true } g && Standing(g))
                _guardsNow.Add(g);
        }

        int cap = _guardsNow.Count * HuntersPerGuard;
        if (cap == 0 || hunters >= cap)
            return;

        foreach (Pawn p in near)
        {
            if (p is not RPawnVillain v || !v.IsValid || v.bDeleteMe || _team.ContainsKey(v.Ptr) || _pacified.Contains(v.Ptr) || !Standing(v))
                continue;

            float nearest = float.MaxValue;
            foreach (Pawn g in _guardsNow)
                nearest = MathF.Min(nearest, Vector3.Distance(v.Location, g.Location));
            if (nearest > HuntReach || nearest > Vector3.Distance(v.Location, me.Location) - HuntMargin || !v.IsHostile())
                continue;

            _team[v.Ptr] = new Loyal { Team = Hunters, Pawn = v };
            if (++hunters >= cap)
                break;
        }
    }

    // ---- the brain

    private void Guard(RPawnVillain v, Loyal l, List<Pawn> near, bool choose)
    {
        Pawn? me = World.Pawn;
        if (me == null)
            return;

        double now = Clock.Now;
        l.Pawn ??= v;
        // His man just went down: the next threat is picked now, not at the next think, or he'd spend that gap
        // standing down mid-fight (gun lowered, aim dropped) and come back to it late.
        RPawnVillain? target = Current(v, l, near, me);
        bool lost = target == null && l.Target != 0;
        if ((choose && now >= l.NextThink) || lost)
        {
            l.NextThink = now + ThinkSeconds;
            if (target != null)
                l.TargetScore = Score(v, target, me, Clock.Now - l.SeenAt < SightGrace);

            // Sticky with a margin, so he doesn't spin between two men at the same distance.
            RPawnVillain? best = PickThreat(v, near, me, out float bestScore);
            if (best != null && target != null && best.Ptr == target.Ptr)
                best = null;
            if (best != null && (target == null || bestScore < l.TargetScore - SwitchMargin))
            {
                target = best;
                l.TargetScore = bestScore;
            }
        }

        if (target == null)
        {
            l.Target = 0;
            Idle(v, l, me, now);
            return;
        }

        if (target.Ptr != l.Target)
        {
            l.Target = target.Ptr;
            l.LastGap = float.MaxValue;
            l.GapSince = now;
        }

        Engage(v, l, target, me, now);
    }

    private RPawnVillain? Current(RPawnVillain v, Loyal l, List<Pawn> near, Pawn me)
    {
        if (l.Target == 0)
            return null;
        foreach (Pawn q in near)
        {
            if (q.Ptr == l.Target)
                return q is RPawnVillain { IsValid: true } same && CanFight(WithYou, same) && InReach(v, same, me) ? same : null;
        }

        return null;
    }

    /// <summary>Within PROTECT RANGE of you, or close enough to the guard to be a danger to him.</summary>
    private bool InReach(Pawn v, Pawn q, Pawn me) =>
        Vector3.DistanceSquared(q.Location, me.Location) <= Sq(_guardRange.Value * Metre)
        || Vector3.DistanceSquared(q.Location, v.Location) <= SelfDefence * SelfDefence;

    private static float Sq(float f) => f * f;

    private const int SightChecks = 4;
    private const double HostileCheckSeconds = 0.5;
    private readonly List<(float Score, RPawnVillain Who)> _threats = new();

    /// <summary>
    /// The most urgent threat. Every candidate is scored without line of sight first; a gunman then traces only the
    /// best few, since a trace per enemy per guard twice a second adds up in a big fight.
    /// </summary>
    private RPawnVillain? PickThreat(RPawnVillain v, List<Pawn> near, Pawn me, out float bestScore)
    {
        bestScore = float.MaxValue;
        RPawnVillain? best = null;
        bool gun = v.CurrentGun is { IsValid: true };
        _threats.Clear();
        foreach (Pawn q in near)
        {
            if (q.Ptr == v.Ptr || q is not RPawnVillain { IsValid: true } other || !CanFight(WithYou, other) || !InReach(v, other, me))
                continue;

            float s = Score(v, other, me, gun);
            if (gun)
            {
                _threats.Add((s, other));
            }
            else if (s < bestScore)
            {
                bestScore = s;
                best = other;
            }
        }

        if (!gun)
            return best;

        _threats.Sort((a, b) => a.Score.CompareTo(b.Score));
        for (int i = 0; i < _threats.Count; i++)
        {
            (float s, RPawnVillain q) = _threats[i];
            if (i < SightChecks && !Sees(v, q))
                s += 2500f;
            else if (i >= SightChecks)
                s += 2500f; // unchecked: assumed hidden, so a man he's sure of beats him
            if (s < bestScore)
            {
                bestScore = s;
                best = q;
            }
        }

        return best;
    }

    /// <summary>Whether something else calmed him, asked of the game twice a second rather than every tick.</summary>
    private static bool CalmedElsewhere(RPawnVillain v, Loyal l, double now)
    {
        if (now < l.NextHostileCheck)
            return false;
        l.NextHostileCheck = now + HostileCheckSeconds;
        return !v.IsHostile();
    }

    /// <summary>
    /// Lower is more urgent: nearness to the guard and to you, a big pull toward anyone on top of you, gunmen before
    /// fists. A gunman wants a man he can see; fighters spread out instead of all piling on one.
    /// </summary>
    /// <param name="seen">He had this man in sight a moment ago: a glance lost behind a car doesn't count against him.</param>
    private float Score(RPawnVillain v, RPawnVillain q, Pawn me, bool seen)
    {
        float toGuard = Vector3.Distance(q.Location, v.Location);
        float toYou = Vector3.Distance(q.Location, me.Location);
        float s = toGuard * 0.6f + toYou * 0.4f;
        if (toYou < OnTopOfYou)
            s -= 1500f;
        if (q.CurrentGun is { IsValid: true })
            s -= 700f;

        if (v.CurrentGun is { IsValid: true })
        {
            if (!seen && !Sees(v, q))
                s += 2500f;
        }
        else
        {
            s += 300f * Crowding(q, v);
        }

        return s;
    }

    private int Crowding(Pawn q, Pawn v)
    {
        int n = 0;
        foreach (Loyal o in _team.Values)
        {
            if (o.Team == WithYou && o.Target == q.Ptr && o.Pawn is { } p && p.Ptr != v.Ptr)
                n++;
        }

        return n;
    }

    private static Vector3 Chest(Pawn p) => p.Location + Vector3.UnitZ * ChestHeight;

    private static Vector3 Eye(Pawn p) => p.Location + Vector3.UnitZ * MathF.Max(40f, p.BaseEyeHeight);

    /// <summary>A clear line from his eyes to the man's chest through the level (FastTrace ignores people).</summary>
    private static bool Sees(Pawn v, Pawn q) => v.FastTrace(Chest(q), Eye(v), Vector3.Zero, true);

    // ---- in a fight

    private void Engage(RPawnVillain v, Loyal l, RPawnVillain target, Pawn me, double now)
    {
        l.Fought = true;
        l.FoughtAt = now;
        if (!v.bSelfManageTargetSelection)
        {
            v.bSelfManageTargetSelection = true;
            l.SelfManaged = true;
        }

        if (v.CannotBeTargettedByPlayer is not { IsValid: true } && World.Pc is { } pc)
        {
            v.CannotBeTargettedByPlayer = pc;
            l.Marked = true;
        }

        if (v.OverrideTargetPawn?.Ptr != target.Ptr)
            v.OverrideTargetPawn = target;

        RBMAIController? ai = v.AIController is { IsValid: true } c ? c : null;
        if (v.CurrentGun is { IsValid: true })
        {
            if (l.Benched || CalmedElsewhere(v, l, now))
            {
                v.SetHostile(true, FName.None);
                l.Benched = false;
            }

            Shoot(v, l, target, ai, now);
            return;
        }

        StopShooting(v, l);
        if (ai != null)
            Brawl(v, l, ai, target, now);
    }

    /// <summary>
    /// Fists and melee weapons. The game's combat is built around Batman: in it a thug waits his turn to attack you
    /// and swings your way, so guards in it rarely went for their man and faced you when they did. So a guard stays
    /// out of it (stood down, which makes EnterCombat refuse him) and Oracle runs his fight: run up to the man, face
    /// him, and start the attack with RPawnVillain.SpawnMove, the call the game's own combat behaviours make. The
    /// move's Target comes from GetTargetPawn, which follows OverrideTargetPawn, and the move tracks its target as
    /// it swings. The hit lands through the CombatAnimHit redirects in Npcs.cs.
    /// </summary>
    private void Brawl(RPawnVillain v, Loyal l, RBMAIController ai, RPawnVillain target, double now)
    {
        if (!l.Benched && v.IsHostile())
        {
            v.SetHostile(false, Reason);
            l.Benched = true;
        }

        if (ai.bInCombat)
            ai.ExitCombat();

        float gap = Vector3.Distance(v.Location, target.Location);
        if (l.FacingAt != target.Ptr && gap < FaceRange)
        {
            v.FaceAtActor(target, FName.None);
            l.FacingAt = target.Ptr;
        }

        // Every melee attack walks its last steps in a state that asks the game's combat behaviour about the combat area
        // and quits without one (a knife from 2 m out, fists from their AttackRange), so swings start close enough to
        // go straight into the strike. The stand-in combat behaviour (see Punch) gives the strike its stance.
        bool armed = v.CurrWeapon is { IsValid: true };
        float reach = armed ? WeaponReach : FistReach;
        EnsureStandIn(ai);
        if (now >= l.NextMove)
        {
            // Out of reach: run him down. In reach: hold where he is; the swing closes the last step itself.
            l.NextMove = now + MoveSeconds;
            if (gap > reach)
                FollowTo(ai, l, target, FistStandoff, gap > 800f ? FollowSpeed.MOVESPEED_Sprint : FollowSpeed.MOVESPEED_Run, now);
            else
                FollowTo(ai, l, target, gap + 100f, FollowSpeed.MOVESPEED_Run, now);
        }

        if (gap > reach || now < l.NextSwing || v.CombatMove is { IsValid: true } || v.IsStunned())
            return;

        l.NextSwing = now + SwingGapMin + Random.Shared.NextDouble() * (SwingGapMax - SwingGapMin);
        // Asked of the pawn, not the weapon: RBMPawnAI.GetAttackMoveClass asks its weapon from inside script, where the
        // knife's own override answers. Called on the weapon from C#, BmSDK runs RBMWeapon's base version, which for a
        // knife returns nothing, so knife guards were throwing fist punches with a knife in hand.
        Class? weaponMove = armed && v.GetAttackMoveClass() is { IsValid: true } wm && wm.Ptr != RCombatMove_VillainCloseAttack.StaticClass().Ptr ? wm : null;
        if (weaponMove != null)
        {
            v.SpawnMove(weaponMove);

            // If the native target choice still handed it Batman, point it at the man before it swings.
            if (v.CombatMove is RCombatMove_VillainAttack { IsValid: true } m && m.Target?.Ptr != target.Ptr)
                m.Target = target;

            return;
        }

        Punch(v, ai, target);
    }

    private static readonly FName CombatStance = new("Combat");

    /// <summary>
    /// A knife's attack is the knife combo you counter as Batman (RCombatMove_VillainMultiAttackAndCounter). It only
    /// damages Batman (its hit needs RPawnPlayerCombat(Target).CanBeTracked()), and its hit is a state function
    /// (Attack.CombatAnimHit) that no redirect can reach, so against a thug it slashed for two seconds and never
    /// landed. That state function counts each slash in NumCombatAnimHits, so the count is watched and every new
    /// slash lands through Land. (The plain RCombatMove_VillainKnifeAttack was tried: knife thugs have no animation for it.)
    /// </summary>
    private void ComboHits(RPawnVillain v, Loyal l)
    {
        if (v.CombatMove is not RCombatMove_VillainMultiAttackAndCounter { IsValid: true } combo)
        {
            l.ComboMove = 0;
            return;
        }

        int hits = combo.NumCombatAnimHits;
        if (combo.Ptr != l.ComboMove)
        {
            l.ComboMove = combo.Ptr;
            l.ComboHits = 0;
        }

        if (hits <= l.ComboHits)
            return;
        l.ComboHits = hits;
        Land(combo, RDmgType_Knife.StaticClass, counted: true);
    }

    /// <summary>
    /// The unarmed attack (RCombatMove_VillainCloseAttack) leans on the combat behaviour a thug has in the game's
    /// combat: its walk-up state asks that behaviour every tick whether the target is in the combat area and quits
    /// when there's none, and its strike takes the behaviour's CombatMoveStance. A guard is kept out of that combat,
    /// so his punches cancelled before they started (weapon attacks hardcode their stance, so they worked). So the
    /// punch is built the way the game's rifle-butt attack builds one (RBMBehaviour_CombatRifle.UpdateAttack):
    /// bForceStraightIntoAttack skips the walk-up, with our own Follow having brought him in close; and he carries a
    /// stand-in combat behaviour, never activated, just so the strike has its stance.
    /// </summary>
    private static void EnsureStandIn(RBMAIController ai)
    {
        if (ai.CombatBehaviour is { IsValid: true })
            return;

        var stand = GameObject.ConstructObject<RBMBehaviour_CombatAI>(ai);
        if (stand.CombatMoveStance.Index == FName.None.Index)
            stand.CombatMoveStance = CombatStance;
        ai.CombatBehaviour = stand;
    }

    private static void Punch(RPawnVillain v, RBMAIController ai, RPawnVillain target)
    {
        if (v.Spawn(RCombatMove_VillainCloseAttack.StaticClass(), v, FName.None, v.Location, v.Rotation, null!, true) is not RCombatMove_VillainCloseAttack m || !m.IsValid)
            return;

        m.Villain = v;
        m.Target = target;
        m.bForceStraightIntoAttack = true;
        m.Initialise();
    }

    /// <summary>Takes away the stand-in combat behaviour, if that's what he has (never one the game gave him).</summary>
    private static void DropStandIn(RBMAIController ai)
    {
        if (!ai.bInCombat && ai.CombatBehaviour is { IsValid: true } b && b.Outer?.Ptr == ai.Ptr && !ai.HasBehaviourOfClass(b.Class, true))
            ai.CombatBehaviour = null!;
    }

    // ---- between fights

    /// <summary>Stands down (so the game never sets him on you), puts his gun away and follows you.</summary>
    private void Idle(RPawnVillain v, Loyal l, Pawn me, double now)
    {
        StopShooting(v, l);
        if (l.FacingAt != 0)
        {
            l.FacingAt = 0;
            v.StopFaceAt();
        }

        if (l.Fought && now - l.FoughtAt > StandEasyAfter && v.CombatMove is not { IsValid: true })
        {
            l.Fought = false;
            StandEasy(v);
        }

        if (v.OverrideTargetPawn is { IsValid: true })
            v.OverrideTargetPawn = null!;
        if (!l.Benched && v.IsHostile())
        {
            v.SetHostile(false, Reason);
            l.Benched = true;
        }

        if (v.AIController is not { IsValid: true } ai)
            return;
        if (ai.bInCombat)
            ai.ExitCombat();
        DropStandIn(ai);

        if (now < l.NextMove)
            return;
        l.NextMove = now + MoveSeconds;

        float d = Vector3.Distance(v.Location, me.Location);
        if (_keepUp.On && d > CatchUpRange && now >= l.NextCatchUp)
        {
            l.NextCatchUp = now + 2.0;
            if (CatchUp(v, me))
                return;
        }

        FollowSpeed speed = d > 1500f ? FollowSpeed.MOVESPEED_Sprint : d > 600f ? FollowSpeed.MOVESPEED_Run : FollowSpeed.MOVESPEED_Walk;
        FollowTo(ai, l, me, _guardGap.Value * Metre, speed, now);
    }

    private static readonly FName Standing_ = new("Standing");
    private const double StandEasyAfter = 1.5;

    /// <summary>
    /// After a fight a guard was left in his fighting stance, and still facing or aiming where the fight was, so he
    /// walked after you looking the wrong way (it read as backing up). His last attack has finished by now: he lets go
    /// of any aim, face and look target and goes back to standing, weapon relaxed.
    /// </summary>
    private static void StandEasy(RPawnVillain v)
    {
        v.StopAimAt();
        v.StopFaceAt();
        v.StopLookAt();

        if (v.GetMovementStance().Index != Standing_.Index)
            v.ChangeMovementStance(Standing_);
        if (v.GetWeaponStance().Index != Relaxed.Index)
            v.ChangeWeaponStance(Relaxed);
    }

    /// <summary>
    /// A turncoat's end of fight: out of the game's combat, no stand-in, and once his last attack is over and he's
    /// been idle a moment, no following his last man and his guard lowered (see <see cref="StandEasy"/>).
    /// </summary>
    private void StandDown(RPawnVillain v, Loyal l, double now)
    {
        if (v.AIController is { IsValid: true } ai)
        {
            if (ai.bInCombat)
                ai.ExitCombat();
            DropStandIn(ai);
        }

        if (l.FacingAt != 0)
        {
            l.FacingAt = 0;
            v.StopFaceAt();
        }

        if (l.Fought && now - l.FoughtAt > StandEasyAfter && v.CombatMove is not { IsValid: true })
        {
            l.Fought = false;
            StopFollowing(v);
            StandEasy(v);
        }
    }

    private const double AssistGapMin = 0.6;
    private const double AssistGapMax = 1.4;

    /// <summary>
    /// GANG WAR, FREE FOR ALL and enemies fighting your bodyguards stay in the game's combat (they're still enemies,
    /// and Batman can still hit them), but that combat waits for a turn against Batman, so their fights with each
    /// other were half-hearted. Within reach of their man, Oracle starts the attack itself with SpawnMove, a little
    /// less often than a bodyguard. Being in combat gives the move the combat behaviour it needs.
    /// </summary>
    private void Assist(RPawnVillain v, Loyal l, RPawnVillain target, double now)
    {
        if (now < l.NextSwing || v.CombatMove is { IsValid: true } || v.IsStunned() || v.AIController is not { IsValid: true } ai)
            return;

        float gap = Vector3.Distance(v.Location, target.Location);
        if (gap > (v.CurrWeapon is { IsValid: true } ? WeaponReach : FistReach))
            return;

        if (!ai.bInCombat)
            ai.EnterCombat(false, true, false);
        if (!ai.bInCombat || ai.CombatBehaviour is not { IsValid: true })
            return;

        l.NextSwing = now + AssistGapMin + Random.Shared.NextDouble() * (AssistGapMax - AssistGapMin);
        Class cls = v.GetAttackMoveClass() is { IsValid: true } c ? c : RCombatMove_VillainCloseAttack.StaticClass();
        v.SpawnMove(cls);
        if (v.CombatMove is RCombatMove_VillainAttack { IsValid: true } m && m.Target?.Ptr != target.Ptr)
            m.Target = target;
    }

    /// <summary>Guards out of the scan's reach (you went far) still get to follow and catch up.</summary>
    private void StragglersCatchUp()
    {
        Pawn? me = World.Pawn;
        if (me == null)
            return;

        bool any = false;
        foreach (Loyal l in _team.Values)
        {
            if (l.Guard && l.SeenTick != _steerTick)
            {
                any = true;
                break;
            }
        }

        if (!any)
            return;

        double now = Clock.Now;
        foreach (Loyal l in _team.Values.ToArray())
        {
            if (l.Team != WithYou || !l.Guard || l.SeenTick == _steerTick || l.Pawn is not { IsValid: true } v || !Standing(v))
                continue;
            l.Target = 0;
            Idle(v, l, me, now);
        }
    }

    /// <summary>
    /// Puts a guard who's fallen far behind on the ground just behind you, out of your view, as long as he isn't on
    /// screen nearby. Never during a cutscene, and only onto a floor that's there.
    /// </summary>
    private static bool CatchUp(RPawnVillain v, Pawn me)
    {
        if (World.Pc is { } pc && CameraRig.InCinematic(pc))
            return false;
        if (!WorldView.ReadCamera())
            return false;

        float d = Vector3.Distance(v.Location, me.Location);
        if (d < CatchUpInView && WorldView.ToScreen(v.Location, out Vector2 at2) && WorldView.OnScreen(at2))
            return false;

        Vector3 back = WorldView.Forward;
        back.Z = 0f;
        back = back.LengthSquared() < 0.01f ? -Geo.Facing(me.Rotation.Yaw) : -Vector3.Normalize(back);
        var side = new Vector3(-back.Y, back.X, 0f) * ((Random.Shared.NextSingle() - 0.5f) * 400f);
        Vector3 spot = me.Location + back * 350f + side;
        var top = new Vector3(spot.X, spot.Y, me.Location.Z + 200f);
        if (!Teleport.Ray(me, top, top - Vector3.UnitZ * 4000f, out Vector3 floor, out _))
            return false;

        float lift = v.CylinderComponent is { IsValid: true } cyl ? cyl.CollisionHeight + 5f : 95f;
        v.StopMoveTo();
        Edits.Put(v, floor + Vector3.UnitZ * lift, new Rotator(0f, Rotator.FromDirection(-back).Yaw, 0f));
        return true;
    }

    // ---- moving with the game's Follow behaviour

    /// <summary>
    /// Points the guard's RBMBehaviour_Follow at someone, making one if he has none: it paths on the navmesh and stops
    /// once within <paramref name="dist"/>. It's made, aimed and then started, since it quits if it starts with nobody
    /// to follow. Entering combat clears it; the next call makes another.
    /// </summary>
    private static void FollowTo(RBMAIController ai, Loyal l, Actor who, float dist, FollowSpeed speed, double now)
    {
        if (ai.GetBehaviourOfClass(RBMBehaviour_Follow.StaticClass(), true) is RBMBehaviour_Follow { IsValid: true } f)
        {
            if (f.FollowActor?.Ptr != who.Ptr)
                f.FollowActor = who;
            if (MathF.Abs(f.FollowDist - dist) > 1f)
            {
                f.FollowDist = dist;
                f.FollowDistSq = dist * dist;
            }

            if (f.Speed != speed)
            {
                f.Speed = speed;
                f.SetMoveToSpeed();
            }

            return;
        }

        if (now < l.NextMakeFollow)
            return;
        l.NextMakeFollow = now + MakeFollowSeconds;
        if (ai.CreateSpawnedBehaviour(RBMBehaviour_Follow.StaticClass()) is not RBMBehaviour_Follow made || !made.IsValid)
            return;

        made.FollowActor = who;
        made.FollowDist = dist;
        made.FollowDistSq = dist * dist;
        made.Speed = speed;
        made.bNeverAvoid = true;
        ai.ActivateSpawnedBehaviour(made);
    }

    private static void StopFollowing(RPawnVillain v)
    {
        if (v.AIController is { IsValid: true } ai && ai.GetBehaviourOfClass(RBMBehaviour_Follow.StaticClass(), true) is RBMBehaviour_Follow { IsValid: true } f)
            ai.InstantExitBehaviour(f, false);
    }

    // ---- hit effects

    private const string HitParticleName = "fx_FaceHit5_p";
    
    private const double HitParticleRetryMissing = 120.0;

    private static ParticleSystem? _hitParticle;
    private static string _hitParticlePath = "";
    private static double _hitParticleLooked = double.MinValue;

    /// <summary>
    /// A thug's swing is built to hit Batman, whose side plays the impact, so a swing landed on another thug was
    /// silent. The struck man plays it himself now: RPawnVillain.PlaySound_ImpactAI picks the impact for his own
    /// character type (head or body, punch or heavy blow, a finishing blow when it drops him) and his pained grunt,
    /// as Batman's glide kick does. The spark is the game's own thug-hit effect (RPawnVillainThug.GroupAttackHit),
    /// from the effect pool. Every optional argument is passed, since BmSDK fills a missing one with 0.
    /// </summary>
    private static void HitFx(RPawnVillain v, RPawnVillain t, RCombatMove_VillainAttack move)
    {
        bool punch = move is RCombatMove_VillainCloseAttack && v.CurrWeapon is not { IsValid: true };
        bool head = Random.Shared.Next(3) == 0;
        bool finishing = !Alive(t);
        t.PlaySound_ImpactAI(t, true, finishing, head, punch, !punch, false, true, false, false, false);

        if (HitParticle() is not { } ps || World.WorldInfo?.GRI?.ObjectPool is not { IsValid: true } pool)
            return;

        Vector3 from = Eye(v), at = head ? Eye(t) : Chest(t);
        Vector3 dir = at - from;
        Vector3 spot = at - (dir.LengthSquared() > 1f ? Vector3.Normalize(dir) : Vector3.Zero) * 20f;
        pool.SpawnEmitter(ps, spot, Rotator.FromDirection(-dir), 0, 1f);
    }

    /// <summary>
    /// Punches and blades land through the attack's own damage collision (the move's DamageCollisionBetween), which
    /// damages the man directly and never reaches the CombatAnimHit redirects, so their hits were silent. Hooking that
    /// would run on every thug attack in the game; instead, while a fighter on a team is mid-swing, his man's health is
    /// watched, and a drop plays the hit. One the redirect already played (same move, just now) isn't played twice.
    /// </summary>
    private static void WatchHits(RPawnVillain v, Loyal l)
    {
        if (l.Target == 0 || v.CurrentGun is { IsValid: true } || v.OverrideTargetPawn is not RPawnVillain t || t.Ptr != l.Target || !t.IsValid)
        {
            l.SeenHealthOf = 0;
            return;
        }

        int health = t.Health;
        if (l.SeenHealthOf != t.Ptr)
        {
            l.SeenHealthOf = t.Ptr;
            l.SeenHealth = health;
            return;
        }

        int before = l.SeenHealth;
        l.SeenHealth = health;
        if (health >= before || v.CombatMove is not RCombatMove_VillainAttack { IsValid: true } move)
            return;
        if (Vector3.DistanceSquared(t.Location, v.Location) > MeleeReach * MeleeReach)
            return;

        double now = Clock.Now;
        if (l.LandedMove == move.Ptr && now - l.LandedAt < 0.6)
            return;
        l.LandedMove = move.Ptr;
        l.LandedAt = now;
        Safe.Run("turncoat hit fx", () => HitFx(v, t, move));
    }

    /// <summary>The spark, looked up by name once (and again now and then if it wasn't loaded).</summary>
    private static ParticleSystem? HitParticle()
    {
        if (_hitParticle is { IsValid: true })
            return _hitParticle;

        // Once found, its path is a single engine lookup (StaticFindObject) whenever it's loaded again.
        if (_hitParticlePath.Length > 0)
        {
            _hitParticle = Game.FindObject<ParticleSystem>(_hitParticlePath);
            return _hitParticle is { IsValid: true } ? _hitParticle : null;
        }

        if (Clock.Now - _hitParticleLooked < HitParticleRetryMissing)
            return null;

        _hitParticleLooked = Clock.Now;
        _hitParticle = World.All<ParticleSystem>().FirstOrDefault(p => p.Name.ToString() == HitParticleName);
        if (_hitParticle != null)
            _hitParticlePath = _hitParticle.GetPathName(null);
        else
            Log.Info("Turncoat hits: the " + HitParticleName + " spark isn't loaded here; hits play their sound only.");
        return _hitParticle;
    }

    // ---- guns

    /// <summary>
    /// The game's own RBMAIAction_ShootActor, run every tick: aim at him (AimAt), SetDamageTarget, AdjustGunToHitTarget,
    /// then WeaponFire in bursts. TrackTarget is kept empty: that's the native path for tracking Batman, which aims to
    /// miss and drifts, and SetDamageTarget clears it while SetTrackTarget sets both, so setting both every tick (as
    /// before) flipped the gun between the two. The gun is raised (weapon stance Aiming), fired only with a clear line
    /// to the man, and with <paramref name="ai"/> given the gunman also moves until he has a shot.
    /// </summary>
    private void Shoot(RPawnVillain v, Loyal l, RPawnVillain target, RBMAIController? ai, double now)
    {
        RBMWeaponRanged gun = v.CurrentGun;
        float dist = Vector3.Distance(v.Location, target.Location);
        if (l.ShotAt != target.Ptr)
        {
            if (v.bFiring)
                v.WeaponStop();
            l.ShotAt = target.Ptr;
            l.AimedSince = now;
            l.NextBurst = Math.Max(l.NextBurst, now + AimSettle);
            l.NextSight = 0;
        }

        if (now >= l.NextSight)
        {
            l.Sight = Sees(v, target);
            l.NextSight = now + SightSeconds;
            if (l.Sight)
                l.SeenAt = now;
        }

        if (v.DamageTarget?.Ptr != target.Ptr)
            v.SetDamageTarget(target);
        if (v.TrackTarget is { IsValid: true })
            v.TrackTarget = null!;
        Raise(v, l, now);
        v.AimAt(Chest(target), AimName, AimTurn.ALLOWTURNINGTOAIM_Yes, 1f, default);
        v.AdjustGunToHitTarget(target);
        l.Shooting = true;

        if (ai != null && now >= l.NextMove)
        {
            // Close in until he can see his man and is within rifle range, then stand and shoot: a follow distance
            // just past where he already is leaves him standing.
            l.NextMove = now + MoveSeconds;
            float want = !l.Sight ? GunNoSight : dist > GunFar ? GunFar * 0.7f : dist + 150f;
            FollowTo(ai, l, target, want, !l.Sight || dist > GunFar * 1.5f ? FollowSpeed.MOVESPEED_Run : FollowSpeed.MOVESPEED_Walk, now);
        }

        bool ready = l.Sight && dist <= GunMaxRange && now - l.AimedSince >= AimSettle;
        if (!ready)
        {
            if (v.bFiring)
                v.WeaponStop();
            return;
        }

        if (v.bFiring || now < l.NextBurst)
            return;

        v.bFireOnceOnly = false;
        v.WeaponFire(true, false);
        v.bStopAfterBurst = true;
        float pause = gun.BurstPause > 0.05f ? gun.BurstPause : 0.8f;
        l.NextBurst = now + pause + BurstGapMin + Random.Shared.NextDouble() * (BurstGapMax - BurstGapMin);

        // WeaponFire refuses an empty clip (it calls NotifyGunEmpty instead). The game's magic reload refills it,
        // after a pause about as long as a reload.
        if (!v.bFiring)
        {
            v.ReloadGun(true);
            l.NextBurst = now + ReloadSeconds;
        }
    }

    /// <summary>
    /// Raises the gun to the shoulder, re-checked twice a second since other behaviours lower it. A rifleman can only
    /// aim on the move in the combat stance (in game 2026-11-06 he stayed Standing/Relaxed the whole time he was closing
    /// on men 20-40 m off), so he takes that too.
    /// </summary>
    private static void Raise(RPawnVillain v, Loyal l, double now)
    {
        if (now < l.NextStance)
            return;
        l.NextStance = now + 0.5;
        if (v.GetMovementStance().Index != CombatStance.Index)
            v.ChangeMovementStance(CombatStance);
        if (v.GetWeaponStance().Index != Aiming.Index)
            v.ChangeWeaponStance(Aiming);
        l.Raised = true;
    }

    /// <summary>
    /// Stops the gun and forgets the man. The damage target is always cleared: the game's gun tick keeps aiming at it,
    /// which is why gunmen went on shooting a body on the floor after the fight.
    /// </summary>
    private static void StopShooting(RPawnVillain v, Loyal l)
    {
        if (v.bFiring)
            v.WeaponStop();
        if (v.DamageTarget is RPawnVillain)
            v.SetDamageTarget(null!);
        if (v.TrackTarget is RPawnVillain)
            v.TrackTarget = null!;
        if (!l.Shooting)
            return;

        l.Shooting = false;
        l.ShotAt = 0;
        v.StopAimAt();
        if (l.Raised)
        {
            l.Raised = false;
            v.ChangeWeaponStance(Relaxed);
        }
    }
}
