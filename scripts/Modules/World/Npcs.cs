using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// The WORLD/ENEMIES and WORLD/FRIENDLIES pages. Enemies are <see cref="RPawnVillain"/>. Friendly is every other
/// AI-driven pawn (police, fire crews, crowds, Man-Bat, hallucinations) plus an AI-controlled partner in dual play.
/// The player is never included. Each side has its own CONTROL and MOVE rows, so both can run at once and a hotkey
/// always means the same people.
/// </summary>
internal sealed partial class Npcs : Module
{
    public static readonly Npcs I = new();

    private const string NoticeTitle = "NPCS";
    private const float Range = 9000f;
    private const double ScanSeconds = 0.5;
    private const float MoveTolerance = 4f;
    private const float RespawnDistance = 2000f;
    private const float Behind = 170f;
    private const float Metre = 100f;
    private const float PullRing = 320f;
    private const float PullSpacing = 90f;
    private const float LaunchUp = 1800f;
    private const float LaunchSide = 350f;

    /// <summary>One tab's targets and the per-pawn state its running rows keep, so turning one off undoes only its own.</summary>
    private sealed class Side
    {
        public required bool Enemies;
        public required string Many;
        public required string One;
        public readonly Dictionary<nint, Vector3> FrozenAt = new();
        public readonly HashSet<nint> Resized = new();
        public readonly Dictionary<nint, int> FullHealth = new();
        public Toggle Freeze = null!;
        public Slider Size = null!;

        public bool Picks(Pawn p) => (p is RPawnVillain) == Enemies;

        public string Nobody => "no " + Many.ToLowerInvariant() + " nearby";

        public void Forget()
        {
            FrozenAt.Clear();
            Resized.Clear();
            FullHealth.Clear();
        }
    }

    private readonly Side _enemies = new() { Enemies = true, Many = "ENEMIES", One = "ENEMY" };
    private readonly Side _friendlies = new() { Enemies = false, Many = "FRIENDLIES", One = "FRIENDLY" };
    private readonly List<Pawn> _near = new();
    private readonly HashSet<nint> _ragdolled = new();
    private readonly HashSet<nint> _noRevive = new();
    private readonly Dictionary<nint, float> _attackDelay = new();
    private bool _blockedChargeUp;
    private Every _scan;
    private Slider _moveRange = null!;
    private Slider _moveMax = null!;
    private Toggle _counter = null!;

    // Allegiance. A team is kept per pawn: WithYou turncoats fight every other enemy, the two gangs fight each other.
    private const int WithYou = 1;
    private const int GangA = 2;
    private const int GangB = 3;
    private const int Hunters = 4; // enemies going after your bodyguards (Npcs.Guards.cs)
    private const int Loner = 100; // FREE FOR ALL: every pawn gets a team of its own from here up.
    private const float MeleeStandoff = 140f;
    private const float GunStandoff = 700f;
    private const double AllegianceSeconds = 0.25;
    private static readonly FName Reason = new("Oracle");
    private static readonly FName HackTimeout = new("BatmobileHackTimeout");
    private readonly HashSet<nint> _pacified = new();
    private readonly Dictionary<nint, Loyal> _team = new();
    private Every _allegiance;

    /// <summary>One turned pawn: its side, who it is on, and what Oracle changed so release can put it back.</summary>
    private sealed class Loyal
    {
        public required int Team;
        public nint Target;
        public bool SelfManaged;
        public bool Marked;
        public bool Benched;
        public bool Shooting;
        public nint ShotAt;
        public double AimedSince;
        public double NextBurst;

        // ---- the bodyguard brain and gun handling (Npcs.Guards.cs)
        public RPawnVillain? Pawn;
        public bool Guard;
        public int SeenTick;
        public float TargetScore;
        public double NextThink;
        public double NextMove;
        public double NextSight;
        public double NextStance;
        public double NextCatchUp;
        public bool Sight;
        public bool Raised;
        public float LastGap = float.MaxValue;
        public double GapSince;
        public double NextMakeFollow;
        public double NextSwing;
        public nint FacingAt;
        public nint LandedMove;
        public double LandedAt;
        public nint SeenHealthOf;
        public int SeenHealth;
        public nint ComboMove;
        public int ComboHits;
        public bool Fought;
        public double FoughtAt;
        public double SeenAt;
        public double NextHostileCheck;
    }
    private int _nextGang;
    private Toggle _gangWar = null!;
    private Toggle _freeForAll = null!;
    private int _nextLoner = Loner;
    private Slider _hitsToKo = null!;
    private Choice _weapon = null!;
    private const float MeleeReach = 320f;

    /// <summary>The weapons ARM can hand out: the game's own weapon classes, which RSeqAct_GiveWeapon also spawns.</summary>
    private static readonly (string Key, string Label, Func<Class> Cls)[] Weapons =
    {
        ("pipe", "PIPE", () => BmSDK.BmScript.RBMWeaponPipe.StaticClass()),
        ("bat", "BASEBALL BAT", () => BmSDK.BmScript.RBMWeaponBat.StaticClass()),
        ("baton", "BATON", () => BmSDK.BmScript.RBMWeaponBaton.StaticClass()),
        ("bottle", "BOTTLE", () => BmSDK.BmScript.RBMWeaponBottle.StaticClass()),
        ("knife", "KNIFE", () => BmSDK.BmScript.RBMWeaponKnife.StaticClass()),
        ("machete", "MACHETE", () => BmSDK.BmScript.RBMWeaponMachete.StaticClass()),
        ("stun_stick", "STUN STICK", () => RBMWeaponStunStickBase.StaticClass()),
        ("shield", "SHIELD", () => BmSDK.BmScript.RBMWeaponShield.StaticClass()),
        ("shotgun", "SHOTGUN", () => BmSDK.BmScript.RBMWeaponShotgun.StaticClass()),
        ("rifle", "ASSAULT RIFLE", () => RBMWeaponCombatRifle.StaticClass()),
    };

    public override bool HasHud => _counter.On && World.InGame;

    public override void Build()
    {
        // ---- per side. The enemy ids are the ones these rows had before the pages split, so saved settings keep.
        BuildSide(_enemies, "freeze_enemies", "enemy_size", "npc_full_health", "npc_disarm", "npc_revive", "teleport_enemy", "teleport_pull_enemies", "npc_launch",
            "Every enemy near you freezes in place and can't attack. Turn off to release them.");
        BuildSide(_friendlies, "friendly_freeze", "friendly_size", "friendly_full_health", "friendly_disarm", "friendly_revive", "friendly_teleport", "friendly_pull", "friendly_launch",
            "Every friendly near you, police, fire crews and an AI partner, freezes in place. Turn off to release them.");

        _moveRange = AddSlider("npc_pull_range", "PULL AND LAUNCH RANGE", "How far away PULL and LAUNCH reach, on both the ENEMIES and FRIENDLIES tabs.", 50f, 10f, 300f, 10f, 150f, Fmt.Metres).Pref();
        _moveMax = AddSlider("npc_pull_max", "PULL AND LAUNCH COUNT", "The most characters PULL and LAUNCH move at once, on both tabs. The pull ring widens to fit them.", 12f, 1f, 50f, 1f, 30f, Fmt.Count).Pref();

        // ---- enemy behaviour
        Slider health = AddSlider("enemy_health", "ENEMY HEALTH", "Health of enemies that show up from now on. Low values make them go down fast.", 1f, 0.1f, 3f, 0.1f, 0.3f, Fmt.Percent, ApplyHealth);
        health.Maintain(() => ApplyHealth(health.Value));
        AddToggle("npc_aggressive", "AGGRESSIVE ENEMIES", "Enemies near you attack as soon as they get the chance instead of waiting their turn.",
            on => { if (!on) ReleaseAggression(); }).Maintain(HoldAggression);
        AddToggle("npc_ragdoll_hits", "RAGDOLL ON EVERY HIT", "Enemies near you go flying from any hit.",
            on => { if (!on) Release(_ragdolled, v => v.bAlwaysRagdollWhenHit = false); }).Maintain(() => Hold(_ragdolled, v => !v.bAlwaysRagdollWhenHit, v => v.bAlwaysRagdollWhenHit = true));
        AddToggle("npc_no_revives", "NO MEDIC REVIVES", "Medics can't wake up enemies you knocked out near you.",
            on => { if (!on) Release(_noRevive, v => v.bCanBeRevived = true); }).Maintain(() => Hold(_noRevive, v => v.bCanBeRevived, v => v.bCanBeRevived = false));
        AddToggle("npc_no_charge_up", "NO MEDIC ELECTRIFYING", "Medics can't electrify thugs. Thugs near you who are already electrified lose it.",
            on => { if (!on) AllowChargeUp(); }).Maintain(BlockChargeUp);
        DebugFlags.AddFlag("npc_helmets_off", "KNOCK OFF HELMETS", "Every strike knocks the helmet off an armoured enemy.",
            d => d.Debug_Combat_bAlwaysRemoveHelmets, (d, v) => d.Debug_Combat_bAlwaysRemoveHelmets = v);
        DebugFlags.AddFlag("npc_no_dda", "NO DYNAMIC DIFFICULTY", "Stops the game quietly easing fights after you struggle, so enemies stay as tough as the difficulty says.",
            d => d.Debug_AI_IgnoreDDA, (d, v) => d.Debug_AI_IgnoreDDA = v);
        DebugFlags.AddFlag("npc_group_slowmo", "SLOW-MO GROUP ATTACKS", "Time slows every time several enemies attack you at once.",
            d => d.Debug_Combat_AlwaysSlowdownSimultaneousAttacks, (d, v) => d.Debug_Combat_AlwaysSlowdownSimultaneousAttacks = v);
        DebugFlags.AddFlag("npc_no_stun_stars", "HIDE STUN STARS", "Stunned enemies don't show the circling stars.",
            d => d.Debug_Combat_DisableStunStars, (d, v) => d.Debug_Combat_DisableStunStars = v);
        AddConsoleToggle("disarm_first_hit", "DISARM ON FIRST HIT", "Thugs drop their melee weapons as soon as they are hit.", new ConsoleProp("RBMWeapon", "DropOnFirstHit", "True", "False"));
        AddConsoleToggle("force_riots", "FORCE RIOTS", "Street riots break out across the city.", new ConsoleProp("RPopulationManager", "bForceRiots", "True", "False"));
        AddConsoleToggle("rioters_fight", "RIOTERS ALWAYS FIGHT", "Rioting thugs pick fights instead of just smashing things.", new ConsoleProp("RRiotBrain", "bRiotWantsToFight", "True", "False"));

        // ---- enemy allegiance
        AddToggle("npc_pacify", "PACIFY ENEMIES", "Enemies near you stand down and leave the fight, the same switch the game uses to calm them for scenes. Turn off and they fight again.",
            on => { if (!on) ReleasePacified(); }).Maintain(HoldPacified);
        AddCommand("npc_turncoat", "TURN NEAREST ENEMY", "EXPERIMENTAL. The closest enemy switches sides and goes after the other enemies instead of you. They stand down when nobody is left to fight. RELEASE TURNCOATS undoes it.", TurnNearest);
        _gangWar = AddToggle("npc_gang_war", "GANG WAR", "EXPERIMENTAL. Enemies near you split into two gangs and go after each other instead of you. Turn off to end it.",
            on => { if (on) EndMode(_freeForAll); else ReleaseTeam(t => t is GangA or GangB); });
        _freeForAll = AddToggle("npc_free_for_all", "FREE FOR ALL", "EXPERIMENTAL. Every enemy near you fights whoever is closest, and nobody comes after you. Turn off to end it.",
            on => { if (on) EndMode(_gangWar); else ReleaseTeam(t => t >= Loner); });
        AddCommand("npc_release_turncoats", "RELEASE TURNCOATS", "Every enemy you turned goes back to fighting you.", ReleaseTurncoats);
        _hitsToKo = AddSlider("npc_turncoat_hits", "TURNCOAT HITS TO KO", "How many hits from a turncoat or gang member it takes to knock another enemy out, when the game's own punch doesn't.", 4f, 1f, 10f, 1f, 2f, Fmt.Count).Pref();
        BuildGuards();

        // ---- enemy weapons
        _weapon = AddChoice("npc_weapon_kind", "WEAPON TO GIVE", "The weapon ARM ENEMIES and ARM TURNCOATS hand out. EXPERIMENTAL: a weapon this part of the game never uses may not have its model loaded.",
            Weapons.Select(w => w.Key).ToArray(), Weapons.Select(w => w.Label).ToArray()).Pref();
        AddCommand("npc_arm", "ARM ENEMIES", "Gives the nearest enemies the chosen weapon, replacing what they hold. PULL AND LAUNCH RANGE and COUNT set how far and how many.", () => Arm(false));
        AddCommand("npc_arm_turncoats", "ARM TURNCOATS", "Gives every turncoat and gang member near you the chosen weapon.", () => Arm(true));

        // ---- partner
        DebugFlags.AddFlag("friendly_dual_takedowns", "ALWAYS ALLOW DUAL TAKEDOWNS", "With a partner fighting beside you, dual-play takedowns are always on offer.",
            d => d.Debug_Combat_CanAlwaysDualPlay, (d, v) => d.Debug_Combat_CanAlwaysDualPlay = v);

        // ---- enemy instant
        AddCommand("ko_all", "KO ALL ENEMIES", "Knocks out every enemy in the area.", () => World.RequireCheats().DebugKillAllEnemies());
        AddCommand("ko_all_but_one", "KO ALL BUT ONE", "Leaves exactly one enemy standing.", () => World.RequireCheats().DebugKillAllButOneEnemyInRoom());
        AddCommand("destroy_weapons", "DESTROY ALL WEAPONS", "Removes guns and melee weapons from the area.", () => World.RequireCheats().DebugDestroyAllWeapons());
        // ---- tanks on your side
        _hackTime = AddSlider("npc_hack_time", "HACK DURATION", "How long HACK NEARBY TANKS keeps them on your side. The top step is FOREVER.", 60f, 15f, HackForever, 15f, HackForever, HackLabel).Pref();
        AddCommand("npc_hack_tanks", "HACK NEARBY TANKS", "Every militia tank within 150 m switches sides and fights the others for you, with the Batmobile Drone Hacker's own effect. Unlike the Drone Hacker there's no limit on how many.", HackTanks);
        AddCommand("npc_release_tanks", "RELEASE HACKED TANKS", "Every friendly militia tank in the area goes back to the militia, including ones hacked before a reload.", ReleaseTanks);

        AddCommand("kill_tanks", "DESTROY ALL TANKS", "Destroys every active militia tank in the loaded part of the city, from the common drone tanks to the heavy ones. Parked tanks are left alone.", DestroyTanks);
        AddCommand("npc_kill_sentries", "DESTROY SENTRY GUNS", "Blows up every militia sentry gun in the loaded part of the city, the same way a destroyed one goes, and they stay destroyed. Watchtower and airship turrets aren't touched: use DISABLE ALL TURRETS on those.", DestroySentries);
        AddCommand("npc_disable_turrets", "DISABLE ALL TURRETS", "Switches off every sentry gun and turret in the loaded part of the city, watchtowers and airship guns included. Sentry guns shut down, turrets go dead the way they do after a disruptor hit. They stay off.", DisableTurrets);
        AddCommand("npc_flip_cars", "FLIP ALL CARS", "Turns every car upside down, not just the militia's: police cars and the Batmobile flip too.", () => World.RequireCheats().FlipThugCars());
        AddCommand("npc_clear_thugs", "CLEAR STREET THUGS", "Sends the thugs roaming the streets away. Mission and story enemies stay.", ClearStreetThugs);

        // ---- info
        _counter = AddToggle("npc_count", "SHOW NPC COUNT", "Shows how many enemies are standing or down, and how many friendlies, within 90 m.");
    }

    private void BuildSide(Side s, string freeze, string size, string healthy, string disarm, string revive, string teleport, string pull, string launch, string freezeHelp)
    {
        string many = s.Many.ToLowerInvariant();

        s.Freeze = AddToggle(freeze, "FREEZE " + s.Many, freezeHelp, on => { if (!on) Unfreeze(s); }).EveryTick(() => FreezeTick(s));
        s.Size = AddSlider(size, s.One + " SIZE", "Shrinks or grows every " + s.One.ToLowerInvariant() + " near you. Purely visual.", 1f, 0.5f, 2f, 0.05f, 1.4f, Fmt.Times, v => ApplySize(s, v));
        s.Size.Maintain(() => ApplySize(s, s.Size.Value));
        AddToggle(healthy, "KEEP " + s.Many + " HEALTHY", "All " + many + " near you keep healing back to full. A single big hit can still knock them out.",
            on => { if (!on) s.FullHealth.Clear(); }).Maintain(() => KeepHealthy(s));
        AddCommand(disarm, "DISARM " + s.Many, "All " + many + " near you drop the weapon in their hands.", () => Disarm(s));
        AddCommand(revive, "REVIVE " + s.Many, "All " + many + " near you who were knocked out get back up with full health, the same way a medic revives them.", () => Revive(s));

        AddCommand(teleport, "TELEPORT TO NEAREST " + s.One, "Puts you right behind the closest standing " + s.One.ToLowerInvariant() + ", facing them.", () => ToNearest(s)).Moves();
        AddCommand(pull, "PULL " + s.Many + " TO ME", "Brings the nearest " + many + " into a ring around you. PULL AND LAUNCH RANGE and COUNT set how far and how many.", () => PullToMe(s));
        AddCommand(launch, "LAUNCH " + s.Many, "Throws the nearest " + many + " high into the air. PULL AND LAUNCH RANGE and COUNT set how far and how many.", () => Launch(s));
    }

    public override void WorldChanged()
    {
        _enemies.Forget();
        _friendlies.Forget();
        _near.Clear();
        _ragdolled.Clear();
        _noRevive.Clear();
        _attackDelay.Clear();
        _blockedChargeUp = false;
        _pacified.Clear();
        _team.Clear();
        _hackedTanks.Clear();
        _scan.Force();
    }

    public override void Shutdown()
    {
        foreach (Side s in new[] { _enemies, _friendlies })
        {
            Safe.Run("unfreeze", () => Unfreeze(s));
            Safe.Run("npc size", () => ApplySize(s, 1f));
        }

        Safe.Run("ragdoll hits", () => Release(_ragdolled, v => v.bAlwaysRagdollWhenHit = false));
        Safe.Run("medic revives", () => Release(_noRevive, v => v.bCanBeRevived = true));
        Safe.Run("aggression", ReleaseAggression);
        Safe.Run("medic charge-up", AllowChargeUp);
        Safe.Run("pacify", ReleasePacified);
        Safe.Run("allegiance", () => ReleaseTeam(_ => true));
    }

    // ---- who counts

    private static bool IsPartner(Pawn p) => p is RPawnPlayer && p.Ptr != World.PawnPtr && p.Ptr != (World.Player?.Ptr ?? 0) && p.Controller is AIController { IsValid: true };

    private static bool Alive(Pawn p) => p.Health > 0 && p is not RPawnVillain { bGoodAsDead: true };

    private static bool Standing(Pawn p) => !p.bDeleteMe && !p.bHidden && Alive(p);

    /// <summary>Every AI character in the loaded world, enemy or not. Ragdoll stand-ins are props, not people.</summary>
    private static IEnumerable<Pawn> AllNpcs()
    {
        foreach (RBMPawnAI p in World.All<RBMPawnAI>())
        {
            if (p is not RPawnCrowdRagdoll)
                yield return p;
        }

        foreach (RPawnPlayer p in World.All<RPawnPlayer>())
        {
            if (IsPartner(p))
                yield return p;
        }
    }

    private static IEnumerable<Pawn> ByDistance(Side s, Vector3 from) => AllNpcs()
        .Where(p => s.Picks(p) && Standing(p))
        .OrderBy(p => Vector3.DistanceSquared(p.Location, from));

    /// <summary>The nearest of this side within the shared range, at most the shared count of them.</summary>
    private Pawn[] InMoveRange(Side s, Vector3 at)
    {
        float reach = _moveRange.Value * Metre;
        Pawn[] found = ByDistance(s, at)
            .Where(p => Vector3.DistanceSquared(p.Location, at) <= reach * reach)
            .Take(Math.Max(1, (int)_moveMax.Value))
            .ToArray();
        return found.Length > 0 ? found : throw new InvalidOperationException(s.Nobody);
    }

    /// <summary>Every NPC near the player, of either side. Shared by the running features so they only scan once.</summary>
    private List<Pawn> Near()
    {
        if (!_scan.Elapsed(ScanSeconds))
            return _near;

        _near.Clear();
        Pawn? player = World.Pawn;
        if (player == null)
            return _near;

        Vector3 at = player.Location;
        foreach (Pawn p in AllNpcs())
        {
            if (!p.bDeleteMe && Vector3.DistanceSquared(p.Location, at) <= Range * Range)
                _near.Add(p);
        }

        return _near;
    }

    /// <summary>ONE HIT KO on the combat page.</summary>
    public void LeaveOnOneHealth()
    {
        foreach (Pawn p in Near())
        {
            if (p is RPawnVillain v && v.IsValid && !v.bGoodAsDead && v.Health > 1 && TeamOf(v) != WithYou)
                v.Health = 1;
        }
    }

    // ---- freeze

    private void FreezeTick(Side s)
    {
        if (s.Enemies)
            WriteCanAttack(false);

        foreach (Pawn p in Near())
        {
            if (!p.IsValid || !s.Picks(p))
                continue;

            if (!Alive(p))
            {
                if (s.FrozenAt.Remove(p.Ptr))
                    SetFrozen(p, false);
                continue;
            }

            if (!s.FrozenAt.TryGetValue(p.Ptr, out Vector3 at) || Vector3.DistanceSquared(at, p.Location) > RespawnDistance * RespawnDistance)
            {
                s.FrozenAt[p.Ptr] = p.Location;
                SetFrozen(p, true);
                continue;
            }

            SetFrozen(p, true);
            if (Vector3.DistanceSquared(at, p.Location) > MoveTolerance * MoveTolerance)
                p.SetLocation(at);
        }
    }

    private static void WriteCanAttack(bool can)
    {
        if (DebugFlags.Data is { } d && d.Debug_Combat_CanAttack != can)
            d.Debug_Combat_CanAttack = can;
    }

    /// <summary>Stops or restarts one pawn: its tick, its controller's and its animation. The world editor uses it too.</summary>
    internal static void SetFrozen(Pawn p, bool frozen)
    {
        if (p.bTickIsDisabled != frozen)
            p.SetTickIsDisabled(frozen);

        Controller c = p.Controller;
        if (c is { IsValid: true } && c.bTickIsDisabled != frozen)
            c.SetTickIsDisabled(frozen);

        SkeletalMeshComponent mesh = p.Mesh;
        if (mesh is { IsValid: true } && mesh.bPauseAnims != frozen)
            mesh.bPauseAnims = frozen;

        if (frozen)
            p.Velocity = Vector3.Zero;
    }

    private static void Unfreeze(Side s)
    {
        if (s.FrozenAt.Count > 0)
        {
            foreach (RPawnCombat p in World.All<RPawnCombat>())
            {
                if (s.FrozenAt.ContainsKey(p.Ptr))
                    SetFrozen(p, false);
            }
        }

        s.FrozenAt.Clear();
        if (s.Enemies)
            WriteCanAttack(true);
    }

    // ---- size, health, disarm, revive

    private static void ApplyHealth(float v)
    {
        RBMCombatManager? cm = World.Info?.CombatManager;
        if (cm is { IsValid: true } && MathF.Abs(cm.PawnHealthMultiplier - v) > 0.001f)
            cm.PawnHealthMultiplier = v;
    }

    private void ApplySize(Side s, float v)
    {
        if (MathF.Abs(v - 1f) < 0.001f)
        {
            ResetSizes(s);
            return;
        }

        foreach (Pawn p in Near())
        {
            if (!p.IsValid || !s.Picks(p))
                continue;
            if (MathF.Abs(p.DrawScale - v) > 0.001f)
                p.SetDrawScale(v);
            s.Resized.Add(p.Ptr);
        }
    }

    private static void ResetSizes(Side s)
    {
        if (s.Resized.Count == 0)
            return;

        foreach (RPawnCombat p in World.All<RPawnCombat>())
        {
            if (s.Resized.Contains(p.Ptr) && MathF.Abs(p.DrawScale - 1f) > 0.001f)
                p.SetDrawScale(1f);
        }

        s.Resized.Clear();
    }

    /// <summary>Heals back to the most health each one has been seen with, so it works whatever their maximum is.</summary>
    private void KeepHealthy(Side s)
    {
        foreach (Pawn p in Near())
        {
            if (!p.IsValid || !s.Picks(p) || !Alive(p))
                continue;

            int full = Math.Max(p.Health, p.HealthMax);
            if (s.FullHealth.TryGetValue(p.Ptr, out int seen))
                full = Math.Max(full, seen);
            s.FullHealth[p.Ptr] = full;

            if (p.Health < full)
                p.Health = full;
        }
    }

    /// <summary>The game's own disarm, the same one a batarang or Batclaw pull uses: the weapon drops as a pickup.</summary>
    private void Disarm(Side s)
    {
        int count = 0;
        foreach (Pawn p in Near())
        {
            if (p is RBMPawnAI ai && ai.IsValid && s.Picks(p) && Standing(p) && ai.CurrWeapon is { IsValid: true })
            {
                ai.ForceDisarm();
                count++;
            }
        }

        if (count == 0)
            throw new InvalidOperationException("no armed " + s.Many.ToLowerInvariant() + " nearby");

        Overlay.Notice(NoticeTitle, count + " DISARMED", true);
    }

    /// <summary>
    /// The game's own wake-up, which the Riddler mech's mass revive also uses. On an enemy it resets health and the
    /// KO state, puts them back in the room's hostile count and plays the get-up. Hidden ones are pooled, not down.
    /// </summary>
    private void Revive(Side s)
    {
        _scan.Force();
        int count = 0;
        foreach (Pawn p in Near().ToArray())
        {
            if (p is RBMPawnAI ai && ai.IsValid && !ai.bDeleteMe && !ai.bHidden && s.Picks(p) && !Alive(p))
            {
                ai.WakeFromDeadWithDefaults();
                count++;
            }
        }

        if (count == 0)
            throw new InvalidOperationException("no knocked out " + s.Many.ToLowerInvariant() + " nearby");

        Overlay.Notice(NoticeTitle, count + " REVIVED", true);
    }

    // ---- enemy behaviour

    /// <summary>Keeps a per-enemy property set on every enemy near you, remembering who was changed so it can be undone.</summary>
    private void Hold(HashSet<nint> changed, Func<RPawnVillain, bool> needs, Action<RPawnVillain> apply)
    {
        foreach (Pawn p in Near())
        {
            if (p is not RPawnVillain v || !v.IsValid || v.bDeleteMe || !needs(v))
                continue;

            apply(v);
            changed.Add(v.Ptr);
        }
    }

    private static void Release(HashSet<nint> changed, Action<RPawnVillain> undo)
    {
        if (changed.Count == 0)
            return;

        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (changed.Contains(v.Ptr))
                undo(v);
        }

        changed.Clear();
    }

    /// <summary>bAttackASAP jumps the attack queue; AttackDelay is how long each one waits, kept so it can go back.</summary>
    private void HoldAggression()
    {
        foreach (Pawn p in Near())
        {
            if (p is not RPawnVillain v || !v.IsValid || v.bDeleteMe)
                continue;

            if (!_attackDelay.ContainsKey(v.Ptr))
                _attackDelay[v.Ptr] = v.AttackDelay;
            if (!v.bAttackASAP)
                v.bAttackASAP = true;
            if (v.AttackDelay != 0f)
                v.AttackDelay = 0f;
        }
    }

    private void ReleaseAggression()
    {
        if (_attackDelay.Count == 0)
            return;

        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (_attackDelay.TryGetValue(v.Ptr, out float delay))
            {
                v.bAttackASAP = false;
                v.AttackDelay = delay;
            }
        }

        _attackDelay.Clear();
    }

    /// <summary>
    /// The combat manager's own switch, the one mission scripts use, which stops medics looking for a thug to charge.
    /// Kept applied because those scripts can reset it. Charges already running are ended with the game's ChargeUpOver.
    /// </summary>
    private void BlockChargeUp()
    {
        RBMCombatManager? cm = World.Info?.CombatManager;
        if (cm is { IsValid: true } && !cm.bDisableMedicChargeUpThug)
        {
            cm.bDisableMedicChargeUpThug = true;
            _blockedChargeUp = true;
        }

        foreach (Pawn p in Near())
        {
            if (p is RPawnVillain v && v.IsValid && !v.bDeleteMe && v.ChargedUpBy is { IsValid: true })
                v.ChargeUpOver();
        }
    }

    /// <summary>Only clears the switch if Oracle set it, so a mission that turned charging off keeps it off.</summary>
    private void AllowChargeUp()
    {
        if (!_blockedChargeUp)
            return;

        RBMCombatManager? cm = World.Info?.CombatManager;
        if (cm is { IsValid: true } && cm.bDisableMedicChargeUpThug)
            cm.bDisableMedicChargeUpThug = false;
        _blockedChargeUp = false;
    }

    private const float HackForever = 615f;
    private const float HackRange = 15000f;
    private const float ForeverSeconds = 10_000_000f;
    private Slider _hackTime = null!;
    private readonly HashSet<nint> _hackedTanks = new();

    private static string HackLabel(float v) => v >= HackForever ? "FOREVER" : v.ToString("0", CultureInfo.InvariantCulture) + " S";

    /// <summary>
    /// RVehicleNPC.SwitchAllegiance is what the Batmobile's Drone Hacker does once its lock-on completes: a timer for
    /// BatmobileHackTimeout, SetAsFriendlyVehicle(true) and the hacked effect. Everything else (Batmobile weapons not
    /// targeting it, the radar, tank AI picking enemies) reads IsFriendly. The Drone Hacker also self-destructs extra
    /// drones over its upgrade limit (SelfDestructHackedDrones); Oracle doesn't, so there's no cap.
    /// </summary>
    private void HackTanks()
    {
        Pawn me = World.RequirePawn();
        float seconds = _hackTime.Value >= HackForever ? ForeverSeconds : _hackTime.Value;
        int started = 0, skipped = 0;
        foreach (RVehicleTank tank in World.All<RVehicleTank>().ToArray())
        {
            if (tank.bDeleteMe || tank.bHidden || tank.Health <= 0 || tank.IsFriendly)
                continue;
            if (Vector3.DistanceSquared(tank.Location, me.Location) > HackRange * HackRange)
                continue;
            if (_pendingHacks.ContainsKey(tank.Ptr))
                continue;

            // Static tanks are set dressing with no AI or driving sim, and a tank with no running behaviour is asleep
            // or held by a mission script. Behaviours in _unhackable have been seen to break: the native
            // SetAsFriendlyVehicle dropped their AI without giving them a friendly one, leaving them frozen.
            RVehicleBehaviour? ai = tank.CurrentBehaviour;
            if (tank is RVehicleStaticTank || ai is not { IsValid: true } || tank.DroneHacked || _unhackable.Contains(ai.Class.Name))
            {
                skipped++;
                continue;
            }

            // Written before the call, so if the game ever dies inside it the log's last line names the tank.
            Log.Info("hacking " + tank.Class.Name + " " + tank.Name + ", behaviour " + ai.Class.Name
                + (tank.SpawningKismet is { IsValid: true } ? ", mission spawned" : ""));
            _pendingHacks[tank.Ptr] = new PendingHack(ai, ai.Class, ai.Class.Name, Clock.Now + HackCheckSeconds);
            _attemptedTanks.Add(tank.Ptr);
            tank.SwitchAllegiance(seconds);
            started++;
        }

        if (started == 0)
            throw new InvalidOperationException(skipped > 0 ? "no hackable tanks within 150 m (" + skipped + " parked, scripted or unhackable)" : "no militia tanks within 150 m");

        // The switch finishes over the next moments, so success is checked in CheckHacks, not here.
        Overlay.Notice(NoticeTitle, "HACKING " + Fmt.Plural(started, "TANK"), true);
    }

    private const double HackCheckSeconds = 1.0;

    /// <summary>What a tank was running before Oracle hacked it, so a hack that half-fails can be undone.</summary>
    private sealed record PendingHack(RVehicleBehaviour Behaviour, Class BehaviourClass, string BehaviourName, double CheckAt);

    private readonly Dictionary<nint, PendingHack> _pendingHacks = new();

    // Kept for the whole session on purpose: tanks are pooled, so a tank broken before a checkpoint reload comes back
    // broken after it, and only a restart rebuilds them.
    private readonly HashSet<nint> _attemptedTanks = new();
    private readonly HashSet<string> _unhackable = new(StringComparer.Ordinal) { "RVehicleBehaviour_TankStrafe" };

    /// <summary>
    /// A second after a hack, every tank is either friendly (hacked) or put back the way it was. On the bomb-disarm
    /// mission's strafing tanks the switch left them not friendly and with no AI: frozen, still shooting from the
    /// turret, and missing from the game's enemy list. Their behaviour class is then never hacked again this session.
    /// </summary>
    private void CheckHacks()
    {
        double now = Clock.Now;
        if (!_pendingHacks.Values.Any(h => now >= h.CheckAt))
            return;

        int hacked = 0, restored = 0;
        foreach (RVehicleTank tank in World.All<RVehicleTank>().ToArray())
        {
            if (!_pendingHacks.TryGetValue(tank.Ptr, out PendingHack? h) || now < h.CheckAt)
                continue;

            _pendingHacks.Remove(tank.Ptr);
            if (tank.bDeleteMe || tank.Health <= 0)
                continue;

            if (tank.IsFriendly)
            {
                _hackedTanks.Add(tank.Ptr);
                hacked++;
                continue;
            }

            Log.Info("hack failed on " + tank.Name + " (" + h.BehaviourName + "): behaviour now "
                + (tank.CurrentBehaviour is { IsValid: true } cur ? cur.Class.Name : "none") + ", restoring");
            _unhackable.Add(h.BehaviourName);
            RestoreTank(tank, h.Behaviour, h.BehaviourClass);
            restored++;
        }

        // Anything left whose tank vanished (pooled or destroyed) is dropped.
        foreach (nint gone in _pendingHacks.Where(kv => now >= kv.Value.CheckAt).Select(kv => kv.Key).ToArray())
            _pendingHacks.Remove(gone);

        if (hacked + restored == 0)
            return;

        Log.Info("hack result: " + hacked + " hacked, " + restored + " restored");
        Overlay.Notice(NoticeTitle, Fmt.Plural(hacked, "TANK") + " HACKED"
            + (restored > 0 ? ", " + restored + " COULDN'T BE AND WERE PUT BACK" : ""), hacked > 0);
    }

    /// <summary>Undoes a half-finished switch: no hack timer, out of the friendly list, and its own AI running again.</summary>
    private static void RestoreTank(RVehicleTank tank, RVehicleBehaviour? behaviour, Class? behaviourClass)
    {
        tank.ClearTimer(HackTimeout, null!);
        if (tank.DroneHacked)
            tank.DroneHacked = false;
        if (World.Info?.VehicleCombatManager is { IsValid: true } vcm)
            vcm.SetVehicleAsFriendly(tank, false);

        if (behaviour is { IsValid: true } && tank.CurrentBehaviour?.Ptr != behaviour.Ptr)
            tank.GotoBehaviour(behaviour, false);
        if (tank.CurrentBehaviour is not { IsValid: true })
        {
            Class? cls = behaviourClass ?? tank.DefaultCombatBehaviour ?? tank.GetDefaultBehaviourClass();
            if (cls != null)
                tank.GotoBehaviourClass(cls, false);
        }
    }

    /// <summary>
    /// The game's own timeout, which is what ends a Drone Hacker hack. Every friendly militia tank is released, not
    /// only the ones remembered here: the list is lost on a reload, and militia tanks are only ever friendly when hacked.
    /// Tanks an earlier hack left frozen (no AI, not friendly) are given their combat AI back too.
    /// </summary>
    private void ReleaseTanks()
    {
        int count = 0, repaired = 0;
        foreach (RVehicleTank tank in World.All<RVehicleTank>().ToArray())
        {
            if (tank.bDeleteMe || tank.Health <= 0)
                continue;

            if (tank.IsFriendly)
            {
                tank.ClearTimer(HackTimeout, null!);
                tank.BatmobileHackTimeout();
                count++;
            }
            else if (_attemptedTanks.Contains(tank.Ptr) && !tank.bHidden && tank is not RVehicleStaticTank && tank.CurrentBehaviour is not { IsValid: true })
            {
                RestoreTank(tank, null, tank.DefaultCombatBehaviour);
                repaired++;
            }
        }

        _hackedTanks.Clear();
        _pendingHacks.Clear();
        if (count + repaired == 0)
            throw new InvalidOperationException("no hacked tanks");

        Overlay.Notice(NoticeTitle, Fmt.Plural(count, "TANK") + " RELEASED" + (repaired > 0 ? ", " + repaired + " UNFROZEN" : ""), true);
    }

    /// <summary>
    /// The game's KillHeavyTanks only hits RVehicleHeavyTank, so the common drone tanks survived it. RVehicleTank is
    /// the base of every tank; this deals the same RDmgType_DebugKill damage the game's own vehicle cheats use.
    /// Hidden tanks are pooled, waiting to spawn.
    /// </summary>
    private static void DestroyTanks()
    {
        const int Lethal = 1_000_000;
        Controller? by = World.Pc;
        int count = 0;
        foreach (RVehicleTank tank in World.All<RVehicleTank>().ToArray())
        {
            if (tank.bDeleteMe || tank.bHidden || tank.Health <= 0)
                continue;

            // Parked (static) tanks and ones whose AI is asleep took the lethal damage without running their death
            // sequence and were left frozen at 0 health around the map until a reload. The game's own DebugKillAll
            // only hits its live VehicleEnemyList, so only awake tanks are destroyed here too.
            if (tank is RVehicleStaticTank || tank.CurrentBehaviour is not { IsValid: true })
                continue;

            tank.TakeDamage(Lethal, by!, tank.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass(), default, null!);
            count++;
        }

        if (count == 0)
            throw new InvalidOperationException("no tanks nearby");

        Overlay.Notice(NoticeTitle, Fmt.Plural(count, "TANK") + " DESTROYED", true);
    }

    // ---- turrets

    /// <summary>
    /// RSentryGunBase.Explode is the game's own destroy: DisableSentryGun, the explosion, then GoPhysics, which is
    /// the only place the gun's destroyedFlagString gets set, so it stays gone. Explode may skip a gun that was
    /// already disabled, so one that didn't fall off its mount gets GoPhysicsAndTriggerEvent, the takedown ending.
    /// Hidden guns are packed in a thug's sentry bag and not deployed yet. Watchtower turrets are left alone: their
    /// destroyed state is handled natively and may count toward Occupy Gotham.
    /// </summary>
    /// <summary>
    /// The game's PoolAllPawns cheat loops until the thug pool is empty, so a thug whose return is deferred (one
    /// mid-speech waits for the line to end) spins it forever. This returns each pooled thug once.
    /// </summary>
    private static void ClearStreetThugs()
    {
        RPopulationManager pm = World.Info?.PopulationManager is { IsValid: true } found ? found : throw new InvalidOperationException("there's no city population here");
        RPawnPool pool = pm.ThugPool;
        if (pool is not { IsValid: true })
            throw new InvalidOperationException("there's no city population here");

        var thugs = new List<RBMPawnAI>();
        var active = pool.ActivePawns;
        for (int i = 0; i < active.Count; i++)
        {
            if (active[i] is { IsValid: true, bDeleteMe: false } thug)
                thugs.Add(thug);
        }

        if (thugs.Count == 0)
            throw new InvalidOperationException("no street thugs around");

        foreach (RBMPawnAI thug in thugs)
        {
            if (thug.IsValid && !thug.bDeleteMe)
                thug.DestroyOrReturnToPool(false, false);
        }

        Overlay.Notice(NoticeTitle, Fmt.Plural(thugs.Count, "THUG") + " SENT AWAY", true);
    }

    private static void DestroySentries()
    {
        int count = 0;
        foreach (RSentryGunBase gun in World.All<RSentryGunBase>().ToArray())
        {
            if (gun.bDeleteMe || gun.bHidden || gun.bGoneRagdoll)
                continue;

            gun.Explode(true);
            if (!gun.bGoneRagdoll)
                gun.GoPhysicsAndTriggerEvent();
            count++;
        }

        if (count == 0)
            throw new InvalidOperationException("no sentry guns nearby");

        Overlay.Notice(NoticeTitle, Fmt.Plural(count, "SENTRY GUN") + " DESTROYED", true);
    }

    /// <summary>
    /// Sentry guns: DisableSentryGun, the shutdown every destroy and takedown starts with (bDisabled, lights off,
    /// sound off, no interaction prompt). Turrets: RTurretBase.Disrupted, what a disruptor charge does to them.
    /// Neither has a script path that turns them back on.
    /// </summary>
    private static void DisableTurrets()
    {
        int count = 0;
        foreach (RSentryGunBase gun in World.All<RSentryGunBase>().ToArray())
        {
            if (gun.bDeleteMe || gun.bHidden || gun.bDisabled || gun.bGoneRagdoll)
                continue;

            gun.DisableSentryGun();
            count++;
        }

        foreach (RTurretBase turret in World.All<RTurretBase>().ToArray())
        {
            if (turret.bDeleteMe || turret.bHidden || turret.bDisabled)
                continue;

            turret.Disrupted();
            count++;
        }

        if (count == 0)
            throw new InvalidOperationException("no working turrets nearby");

        Overlay.Notice(NoticeTitle, Fmt.Plural(count, "TURRET") + " DISABLED", true);
    }

    // ---- allegiance

    private int TeamOf(Pawn p) => _team.TryGetValue(p.Ptr, out Loyal? l) ? l.Team : 0;

    /// <summary>
    /// SetHostile(false) is the game's own stand-down: it clears bCanBeHitInCombat and bIsValidCombatant and takes the
    /// pawn out of the combat manager, and RBMAIController.EnterCombat refuses a pawn that isn't hostile.
    /// Only pawns Oracle calmed are remembered, so turning it off never riles one a mission had calmed.
    /// </summary>
    private void HoldPacified()
    {
        foreach (Pawn p in Near())
        {
            if (p is not RPawnVillain v || !v.IsValid || v.bDeleteMe || !Alive(v) || _team.ContainsKey(v.Ptr) || !v.IsHostile())
                continue;

            v.SetHostile(false, Reason);
            _pacified.Add(v.Ptr);
        }
    }

    private void ReleasePacified()
    {
        if (_pacified.Count == 0)
            return;

        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (_pacified.Contains(v.Ptr) && !v.bDeleteMe && !v.IsHostile())
                v.SetHostile(true, FName.None);
        }

        _pacified.Clear();
    }

    private void TurnNearest()
    {
        Pawn player = World.RequirePawn();
        RPawnVillain v = ByDistance(_enemies, player.Location)
            .OfType<RPawnVillain>()
            .FirstOrDefault(v => TeamOf(v) != WithYou && (v.IsHostile() || _pacified.Contains(v.Ptr)))
            ?? throw new InvalidOperationException("no enemy nearby to turn");

        Turn(v);
    }

    /// <summary>
    /// One enemy switches sides, as TURN NEAREST ENEMY does. The world editor's TURN uses it on its selection. A
    /// <paramref name="bodyguard"/> (the spawner's BODYGUARD side) also follows you and protects you (Npcs.Guards.cs).
    /// </summary>
    internal void Turn(RPawnVillain v, bool bodyguard = false)
    {
        // An entry left by someone deleted at this address (the game reuses them) isn't him.
        if (_team.TryGetValue(v.Ptr, out Loyal? stale) && !ReferenceEquals(stale.Pawn, v))
            _team.Remove(v.Ptr);
        if (TeamOf(v) == WithYou)
            throw new InvalidOperationException("already on your side");
        if (!Standing(v))
            throw new InvalidOperationException("they're down");

        if (_pacified.Remove(v.Ptr) && !v.IsHostile())
            v.SetHostile(true, FName.None);
        _team[v.Ptr] = new Loyal { Team = WithYou, Pawn = v, Guard = bodyguard };
        _allegiance.Force();

        bool anyone = Near().OfType<RPawnVillain>().Any(q => q.Ptr != v.Ptr && q.IsValid && CanFight(WithYou, q));
        Overlay.Notice(NoticeTitle, anyone ? "ENEMY TURNED" : "TURNED, BUT NOBODY TO FIGHT" + (_pacified.Count > 0 ? " (PACIFY IS ON)" : ""), true);
    }

    private void ReleaseTurncoats()
    {
        if (!_team.Values.Any(l => l.Team == WithYou))
            throw new InvalidOperationException("no turncoats");

        ReleaseTeam(t => t == WithYou);
        Overlay.Notice(NoticeTitle, "TURNCOATS RELEASED", true);
    }

    /// <summary>Hands every matching pawn back to the game: no override target, its own target choice, hostile and targetable again.</summary>
    private void ReleaseTeam(Func<int, bool> which)
    {
        if (_team.Count == 0)
            return;

        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (_team.TryGetValue(v.Ptr, out Loyal? l) && which(l.Team))
                ReleaseOne(v, l);
        }

        foreach (nint gone in _team.Where(kv => which(kv.Value.Team)).Select(kv => kv.Key).ToArray())
            _team.Remove(gone);
    }

    /// <summary>Hands one pawn back to the game, as <see cref="ReleaseTeam"/> does for a whole side.</summary>
    private void ReleaseOne(RPawnVillain v, Loyal l)
    {
        _team.Remove(v.Ptr);
        if (v.bDeleteMe)
            return;

        if (v.OverrideTargetPawn is { IsValid: true })
            v.OverrideTargetPawn = null!;
        StopShooting(v, l);
        if (v.DamageTarget is RPawnVillain)
            v.SetDamageTarget(null!);
        Safe.Run("release follow", () => StopFollowing(v));
        if (v.AIController is { IsValid: true } releasedAi)
            Safe.Run("release stand-in", () => DropStandIn(releasedAi));
        if (l.SelfManaged)
            v.bSelfManageTargetSelection = false;
        if (l.Marked && v.CannotBeTargettedByPlayer is { IsValid: true })
            v.CannotBeTargettedByPlayer = null!;
        if (!v.IsHostile())
            v.SetHostile(true, FName.None);
    }

    /// <summary>
    /// Target choice runs a few times a second, but the override is put back every tick: the game re-picks a target
    /// natively (SetTargetPlayer, from DamagedBy and when they get up), and a single frame on Batman starts an attack on him.
    /// </summary>
    public override void Tick(float dt)
    {
        if (_pendingHacks.Count > 0 && World.InGame)
            Safe.Run("hack check", CheckHacks);

        if ((_team.Count == 0 && !_gangWar.On && !_freeForAll.On) || !World.InGame)
            return;

        List<Pawn> near = Near();
        bool choose = _allegiance.Elapsed(AllegianceSeconds);
        if (choose && _gangWar.On)
        {
            foreach (Pawn p in near)
            {
                if (p is RPawnVillain v && v.IsValid && Standing(v) && !_team.ContainsKey(v.Ptr) && !_pacified.Contains(v.Ptr) && v.IsHostile())
                    _team[v.Ptr] = new Loyal { Team = _nextGang++ % 2 == 0 ? GangA : GangB, Pawn = v };
            }
        }

        if (choose && _freeForAll.On)
        {
            foreach (Pawn p in near)
            {
                if (p is RPawnVillain v && v.IsValid && Standing(v) && !_team.ContainsKey(v.Ptr) && !_pacified.Contains(v.Ptr) && v.IsHostile())
                    _team[v.Ptr] = new Loyal { Team = _nextLoner++, Pawn = v };
            }
        }

        if (_huntGuards.On && _hunt.Elapsed(HuntSeconds))
            Safe.Run("bodyguard hunters", () => RecruitHunters(near));

        _steerTick++;
        foreach (Pawn p in near)
        {
            if (p is RPawnVillain v && v.IsValid && !v.bDeleteMe && _team.TryGetValue(v.Ptr, out Loyal? l))
            {
                if (l.Pawn != null && !ReferenceEquals(l.Pawn, v))
                {
                    _team.Remove(v.Ptr);
                    continue;
                }

                l.SeenTick = _steerTick;

                // Knocked out, or knocked over (run over, ragdolled): his gun is stopped, or the game's gun tick kept
                // firing it at his last man from the floor, with the gun already dropped.
                if (!Standing(v) || v.IsIncapacitated())
                {
                    StopShooting(v, l);
                    continue;
                }

                if (l.Team == WithYou && l.Guard)
                    Guard(v, l, near, choose);
                else
                    Steer(v, l, near, choose);
                WatchHits(v, l);
                ComboHits(v, l);
            }
        }

        if (_prune.Elapsed(PruneSeconds))
            PruneTeam();

        // Bodyguards left far behind (you drove or glided off) are out of the scan; they still catch up.
        Safe.Run("bodyguard catch-up", _stragglersStep ??= StragglersCatchUp);
    }

    private int _steerTick;
    private Action? _stragglersStep; // made once: a method group passed as an Action is a new delegate every tick
    private Every _prune;
    private const double PruneSeconds = 2.0;

    /// <summary>Drops entries for pawns that no longer exist, so a new pawn at a reused address isn't mistaken for one.</summary>
    private void PruneTeam()
    {
        foreach (nint gone in _team.Where(kv => kv.Value.Pawn is { } p && !p.IsValid).Select(kv => kv.Key).ToArray())
            _team.Remove(gone);
    }

    /// <summary>Forgets a pawn that's about to be deleted (a spawn), without touching it.</summary>
    internal void Forget(nint ptr)
    {
        _team.Remove(ptr);
        _pacified.Remove(ptr);
    }

    /// <summary>
    /// OverrideTargetPawn is read only by native targeting (GetTargetPawn is native), which in game does turn melee
    /// thugs on each other. bSelfManageTargetSelection is the switch RPawnVillain.DamagedBy and CanChangeTarget check
    /// before re-targeting the player who hit them. Turncoats get CannotBeTargettedByPlayer, the dual-play switch that
    /// takes a thug out of your strikes, counters and gadget targeting. bCanBeHitInCombat is left alone: clearing it
    /// is part of the stand-down, and turncoats with it cleared just walked away. Gunmen also get DamageTarget, which
    /// GunTick_Shooting really damages.
    /// </summary>
    private void Steer(RPawnVillain v, Loyal l, List<Pawn> near, bool choose)
    {
        RPawnVillain? target = null;
        if (l.Target != 0)
        {
            foreach (Pawn q in near)
            {
                if (q.Ptr == l.Target && q is RPawnVillain { IsValid: true } same && CanFight(l.Team, same))
                {
                    target = same;
                    break;
                }
            }
        }

        // Sticky: keeps its man until he is down, so it isn't pulled across the fight every time someone else is closer.
        if (target == null)
        {
            if (!choose && l.Target == 0)
                return;

            float best = float.MaxValue;
            foreach (Pawn q in near)
            {
                if (q.Ptr == v.Ptr || q is not RPawnVillain { IsValid: true } other || !CanFight(l.Team, other))
                    continue;

                float d = Vector3.DistanceSquared(other.Location, v.Location);
                if (d < best)
                {
                    best = d;
                    target = other;
                }
            }

            l.Target = target?.Ptr ?? 0;
        }

        // A hunter goes back to fighting you once his bodyguard is down or gone, or once you're the nearer of the two.
        if (l.Team == Hunters && (target == null || World.Pawn is { } me
            && Vector3.Distance(v.Location, me.Location) < Vector3.Distance(v.Location, target.Location) - HuntRelease))
        {
            ReleaseOne(v, l);
            return;
        }

        double clock = Clock.Now;
        if (target == null)
        {
            // Nobody left on the other side: stand down rather than go back to fighting you.
            if (v.OverrideTargetPawn is { IsValid: true })
                v.OverrideTargetPawn = null!;
            StopShooting(v, l);
            if (!l.Benched && v.IsHostile())
            {
                v.SetHostile(false, Reason);
                l.Benched = true;
            }

            // A turncoat ends his fight the way a bodyguard does (out of combat, guard lowered), without following you.
            if (l.Team == WithYou)
                Safe.Run("turncoat stand down", () => StandDown(v, l, clock));
            return;
        }

        // Turncoats (TURN NEAREST ENEMY, the editor's TURN, spawned ON YOUR SIDE) fight the way bodyguards do: melee run
        // by Oracle out of the game's Batman-centred combat, gunmen moving until they have a shot. Only whom they pick
        // (the nearest, kept until he's down) and what they do afterwards differ.
        if (l.Team == WithYou && World.Pawn is { } you)
        {
            Engage(v, l, target, you, clock);
            return;
        }

        if (l.Benched || CalmedElsewhere(v, l, clock))
        {
            v.SetHostile(true, FName.None);
            l.Benched = false;
        }

        if (!v.bSelfManageTargetSelection)
        {
            v.bSelfManageTargetSelection = true;
            l.SelfManaged = true;
        }

        if (l.Team == WithYou && v.CannotBeTargettedByPlayer is not { IsValid: true } && World.Pc is { } pc)
        {
            v.CannotBeTargettedByPlayer = pc;
            l.Marked = true;
        }

        nint now = v.OverrideTargetPawn is { IsValid: true } o ? o.Ptr : 0;
        if (now != target.Ptr)
            v.OverrideTargetPawn = target;

        if (v.CurrentGun is { IsValid: true })
        {
            Shoot(v, l, target, null, clock);
        }
        else
        {
            StopShooting(v, l);
            Assist(v, l, target, clock);
        }
    }

    // ---- turncoat hits
    // A thug's hit only lands through his damage proxy colliding with the target, and that path is built for hitting
    // the player (DamagePawn plays the player's hurt emote and the player-impact sounds), so thug-on-thug punches whiffed.
    // CombatAnimHit is where each attack opens its hit window; for a turned attacker Oracle lands the hit itself.

    [Redirect(typeof(RCombatMove_VillainCloseAttack), nameof(RCombatMove_VillainCloseAttack.CombatAnimHit))]
    private static void CloseHitRedirect(RCombatMove_VillainCloseAttack self)
    {
        self.CombatAnimHit();
        Landed(self, RDmgType_Strike.StaticClass);
    }

    // A punch's hit can come through ForceHitTarget (the move pushing its hit onto the target a couple of frames after
    // the anim notify) rather than CombatAnimHit. The game's own damage may land there; the sound and spark don't.
    [Redirect(typeof(RCombatMove_VillainCloseAttack), nameof(RCombatMove_VillainCloseAttack.ForceHitTarget))]
    private static void ForceHitRedirect(RCombatMove_VillainCloseAttack self)
    {
        if (I._team.Count == 0)
        {
            self.ForceHitTarget();
            return;
        }

        // ForceHitTarget only strikes once the fist's damage proxy has a position; otherwise it waits a frame.
        int before = -1;
        bool strikes = false;
        try
        {
            strikes = self.bForceHitTarget && self.Target is { IsValid: true } && self.Villain?.DamageProxy is { IsValid: true } proxy && proxy.LocationHistory.Count >= 1;
            before = strikes ? self.Target!.Health : -1;
        }
        catch (Exception ex)
        {
            Log.Throttled("turncoat force hit", "turncoat force hit check failed: " + ex.Message);
        }

        self.ForceHitTarget();
        if (strikes)
            Safe.Run("turncoat hit", () => I.Land(self, RDmgType_Strike.StaticClass, before));
    }

    // The plain knife slash and the shield bash open each strike with CombatAnimHit_Left / _Right, called by the
    // animation directly, so they never reached the CombatAnimHit redirects: a strike only landed if the weapon's
    // collision touched the man. A strike within reach now lands, and its damage collision is switched off so the
    // game's own hit can't land it again. (Knife thugs themselves use the knife combo: see ComboHits.)
    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit_Left))]
    private static void KnifeLeftRedirect(RCombatMove_VillainKnifeAttack self)
    {
        self.CombatAnimHit_Left();
        Slashed(self, RDmgType_Knife.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit_Right))]
    private static void KnifeRightRedirect(RCombatMove_VillainKnifeAttack self)
    {
        self.CombatAnimHit_Right();
        Slashed(self, RDmgType_Knife.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit_Left))]
    private static void ShieldLeftRedirect(RCombatMove_VillainShieldAttack self)
    {
        self.CombatAnimHit_Left();
        Slashed(self, RDmgType_ShieldBash.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit_Right))]
    private static void ShieldRightRedirect(RCombatMove_VillainShieldAttack self)
    {
        self.CombatAnimHit_Right();
        Slashed(self, RDmgType_ShieldBash.StaticClass);
    }

    /// <summary>Runs on every knife and shield slash in the game, so it bails at once unless someone is turned.</summary>
    private static void Slashed(RCombatMove_VillainAttack move, Func<Class> damageType)
    {
        if (I._team.Count == 0)
            return;

        Safe.Run("turncoat slash", () =>
        {
            if (I.Land(move, damageType) && move.Villain?.DamageProxy is { IsValid: true } proxy)
                proxy.Deactivate();
        });
    }

    [Redirect(typeof(RCombatMove_VillainPipeAttack), nameof(RCombatMove_VillainPipeAttack.CombatAnimHit))]
    private static void PipeHitRedirect(RCombatMove_VillainPipeAttack self)
    {
        self.CombatAnimHit();
        Landed(self, RDmgType_Pipe.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit))]
    private static void KnifeHitRedirect(RCombatMove_VillainKnifeAttack self)
    {
        self.CombatAnimHit();
        Landed(self, RDmgType_Knife.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit))]
    private static void ShieldHitRedirect(RCombatMove_VillainShieldAttack self)
    {
        self.CombatAnimHit();
        Landed(self, RDmgType_ShieldBash.StaticClass);
    }

    [Redirect(typeof(RCombatMove_VillainHeavySmashAttack), nameof(RCombatMove_VillainHeavySmashAttack.CombatAnimHit))]
    private static void SmashHitRedirect(RCombatMove_VillainHeavySmashAttack self)
    {
        self.CombatAnimHit();
        Landed(self, RDmgType_SledgeHammer.StaticClass);
    }

    /// <summary>Runs on every thug attack in the game, so it bails at once unless someone is turned.</summary>
    private static void Landed(RCombatMove_VillainAttack move, Func<Class> damageType)
    {
        if (I._team.Count == 0)
            return;

        Safe.Run("turncoat hit", () => I.Land(move, damageType));
    }

    /// <summary>
    /// A punch goes through the game's own DamagePawn first, so it gets the real hit reaction and damage. Weapon
    /// swings, and punches DamagePawn doesn't hurt, deal TakeDamage sized by TURNCOAT HITS TO KO.
    /// </summary>
    /// <param name="healthBefore">The man's health before the game's own hit, when it may already have landed; -1 when not.</param>
    /// <returns>True when a hit landed (Oracle's, or the game's with its effects added).</returns>
    private bool Land(RCombatMove_VillainAttack move, Func<Class> damageType, int healthBefore = -1, bool counted = false)
    {
        RPawnVillain v = move.Villain;
        if (v is not { IsValid: true } || !_team.TryGetValue(v.Ptr, out Loyal? l) || l.Target == 0)
            return false;

        if (v.OverrideTargetPawn is not RPawnVillain t || t.Ptr != l.Target || !t.IsValid || !Standing(t))
            return false;
        if (Vector3.DistanceSquared(t.Location, v.Location) > MeleeReach * MeleeReach)
            return false;

        // One hit per strike, however many ways the move reports it. A knife's left and right slashes are separate.
        double now = Clock.Now;
        if (!counted && l.LandedMove == move.Ptr && now - l.LandedAt < 0.25)
            return false;
        l.LandedMove = move.Ptr;
        l.LandedAt = now;

        // The game's hit already hurt him: only the sound and spark are missing.
        if (healthBefore >= 0 && t.Health < healthBefore)
        {
            Safe.Run("turncoat hit fx", () => HitFx(v, t, move));
            return true;
        }

        int before = t.Health;
        if (move is RCombatMove_VillainCloseAttack close)
            close.DamagePawn(t);

        if (t.Health >= before && Alive(t))
        {
            int max = Math.Max(Math.Max(t.HealthMax, before), 1);
            int amount = Math.Max(1, (int)MathF.Ceiling(max / Math.Max(1f, _hitsToKo.Value)));
            Vector3 push = Vector3.Normalize(t.Location - v.Location) * 400f;
            t.TakeDamage(amount, v.Controller, t.Location, push, damageType(), default, v);
        }

        Safe.Run("turncoat hit fx", () => HitFx(v, t, move));
        return true;
    }

    // ---- weapons

    /// <summary>
    /// RBMPawnAI.SpawnWeapon is how the game itself arms a pawn (RSeqAct_GiveWeapon, lockers, vehicles). Whatever they
    /// hold first disappears with the game's DisappearDisarm, so nothing is left lying around twice.
    /// </summary>
    private void Arm(bool turnedOnly)
    {
        var pick = Weapons[Math.Clamp(_weapon.Index, 0, Weapons.Length - 1)];
        Class cls = pick.Cls();
        Pawn player = World.RequirePawn();

        IEnumerable<Pawn> who = turnedOnly
            ? Near().Where(p => p.IsValid && Standing(p) && _team.ContainsKey(p.Ptr))
            : InMoveRange(_enemies, player.Location);

        int armed = 0, failed = 0;
        foreach (Pawn p in who.ToArray())
        {
            if (p is not RBMPawnAI ai || !ai.IsValid)
                continue;

            if (ai.CurrWeapon is { IsValid: true } held)
            {
                if (held.Class.Ptr == cls.Ptr)
                {
                    armed++;
                    continue;
                }

                ai.DisappearDisarm();
            }

            RBMWeapon? got = ai.SpawnWeapon(cls, true, false);
            if (got is { IsValid: true })
                armed++;
            else
                failed++;
        }

        if (armed + failed == 0)
            throw new InvalidOperationException(turnedOnly ? "no turncoats nearby" : "no enemies nearby");

        Log.Info("arm: " + pick.Label + " given to " + armed + (failed > 0 ? ", " + failed + " failed" : ""));
        if (armed == 0)
            throw new InvalidOperationException(pick.Label.ToLowerInvariant() + " isn't loaded here");

        Overlay.Notice(NoticeTitle, armed + " ARMED WITH " + pick.Label + (failed > 0 ? ", " + failed + " FAILED" : ""), true);
    }

    private bool CanFight(int mine, RPawnVillain q) => Standing(q) && !_pacified.Contains(q.Ptr) && Opposed(mine, TeamOf(q));

    /// <summary>GANG WAR and FREE FOR ALL split the same enemies, so turning one on ends the other.</summary>
    private static void EndMode(Toggle other)
    {
        if (!other.On)
            return;

        other.On = false;
        Catalog.Commit(other, "mode switch");
    }

    // ---- turned movement
    // While waiting their turn, thugs move by a sum of forces; Batman's (RPawnPlayerCombat.GetForceToGiveToPawn,
    // native) pushes them back to keep a ring round him, so turned thugs kept backing away from you. For a turned
    // thug with a target, the whole force is replaced by one that closes on his target and holds there.

    [Redirect(typeof(RBMBehaviour_CombatAI), nameof(RBMBehaviour_CombatAI.GetWaitForCombatMovementForce))]
    private static Vector3 WaitForceRedirect(RBMBehaviour_CombatAI self)
    {
        Vector3 force = self.GetWaitForCombatMovementForce();
        if (I._team.Count == 0)
            return force;

        try
        {
            return I.TurnedForce(self, force);
        }
        catch (Exception ex)
        {
            Log.Throttled("turned movement", "turned movement failed: " + ex.Message);
            return force;
        }
    }

    private Vector3 TurnedForce(RBMBehaviour_CombatAI self, Vector3 force)
    {
        RPawnVillain? v = self.Villain;
        if (v is not { IsValid: true } || !_team.TryGetValue(v.Ptr, out Loyal? l) || l.Target == 0)
            return force;
        if (v.OverrideTargetPawn is not { IsValid: true } t || t.Ptr != l.Target)
            return force;

        Vector3 to = t.Location - v.Location;
        to.Z = 0f;
        float dist = to.Length();
        float hold = v.CurrentGun is { IsValid: true } ? GunStandoff : MeleeStandoff;
        float scale = MathF.Max(force.Length(), self.MaxMoveSpeedForce);

        Vector3 turned;
        if (dist < 1f)
            turned = Vector3.Zero;
        else if (dist > hold)
            turned = to / dist * scale;
        else if (dist < hold * 0.6f)
            turned = -to / dist * scale * 0.5f;
        else
            turned = Vector3.Zero;

        return turned;
    }

    /// <summary>Turncoats fight everyone not on their side; gang members only fight the other gang and turncoats.</summary>
    private static bool Opposed(int mine, int theirs) => mine switch
    {
        >= Loner => theirs != mine,
        WithYou => theirs != WithYou,
        GangA => theirs is GangB or WithYou,
        GangB => theirs is GangA or WithYou,
        Hunters => theirs == WithYou,
        _ => false,
    };

    // ---- move

    private static void ToNearest(Side s)
    {
        Pawn mover = Teleport.RequireMover();
        Pawn target = ByDistance(s, mover.Location).FirstOrDefault() ?? throw new InvalidOperationException(s.Nobody);
        Teleport.I.Hop(target.Location - Geo.Facing(target.Rotation.Yaw) * Behind, new Rotator(0, target.Rotation.Yaw, 0));
    }

    private void PullToMe(Side s)
    {
        Pawn mover = Teleport.RequireMover();
        Vector3 at = mover.Location;
        Pawn[] near = InMoveRange(s, at);

        // Keeps about a body's width between neighbours, so a big pull doesn't stack them inside each other.
        float ring = MathF.Max(PullRing, near.Length * PullSpacing / MathF.Tau);
        for (int i = 0; i < near.Length; i++)
        {
            float angle = MathF.Tau * i / near.Length;
            near[i].SetLocation(at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * ring);
            near[i].Velocity = Vector3.Zero;
        }

        Overlay.Notice(NoticeTitle, near.Length + " PULLED", true);
    }

    /// <summary>Switches each one to falling so the upward push isn't snapped back to the ground by walking physics.</summary>
    private void Launch(Side s)
    {
        Pawn player = World.RequirePawn();
        Pawn[] near = InMoveRange(s, player.Location);
        foreach (Pawn p in near)
        {
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            p.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
            p.Velocity = new Vector3(MathF.Cos(angle) * LaunchSide, MathF.Sin(angle) * LaunchSide, LaunchUp);
        }

        Overlay.Notice(NoticeTitle, near.Length + " LAUNCHED", true);
    }

    // ---- info

    public override void Draw(Canvas c)
    {
        int up = 0, down = 0, friends = 0, turned = 0;
        foreach (Pawn p in Near())
        {
            if (!p.IsValid || p.bDeleteMe || p.bHidden)
                continue;
            if (TeamOf(p) == WithYou && Alive(p))
                turned++;
            else if (!_enemies.Picks(p))
                friends += Alive(p) ? 1 : 0;
            else if (Alive(p))
                up++;
            else
                down++;
        }

        const float width = 250f;
        const float height = 50f;
        float x = Gfx.S(28f);
        float y = Gfx.S(230f);
        Gfx.Panel(x, y, Gfx.S(width), Gfx.S(height));
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(9f), "ENEMIES " + up + " UP, " + down + " DOWN", Theme.Hi, 0.66f);
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(27f), "FRIENDLIES " + friends + (turned > 0 ? ", TURNED " + turned : ""), Theme.Mid, 0.66f);
    }
}
