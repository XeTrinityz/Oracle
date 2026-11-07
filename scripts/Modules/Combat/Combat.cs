using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// The PLAYER/COMBAT page. AUTO COUNTER and AUTO DODGE use the same calls as the game's own dev combat bot
/// (RCombatMove_VillainAttack.BotTryCounter / BotTryRedirect): TryCounter(false) on a counterable attack aimed at
/// the player, TryRedirect(attacker) on a red one. A counter window opens in CanCounterStart, which lights the icon
/// for every attack but only sets bCanCounter for ACT_Counter, ACT_Blade and ACT_Fury.
/// </summary>
internal sealed partial class Combat : Module
{
    public static readonly Combat I = new();

    private const float WatchRange = 2500f;
    private const double WatchSeconds = 0.25;
    private const double EvadeAfter = 0.25;
    private const double WindowLife = 2.0;
    private const float ChargeVault = 500f;
    private const float ChargeStop = 300f;

    /// <summary>Penalty-free resets COMBO NEVER RESETS still lets through: the fight banking its XP, and leaving on foot.</summary>
    private static readonly HashSet<string> BankingReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Combat xp spawned", "Possessed Bmbl", "Enter Batmobile", "Entering Bmbl", "Interrogating", "Interrogation",
    };

    public Toggle CounterIcons = null!;
    public Toggle AutoCounter = null!;
    public Toggle AutoDodge = null!;
    public Toggle ComboLock = null!;
    public Toggle AutoFight = null!;
    public Toggle AlwaysTakedown = null!;
    public Toggle GunMiss = null!;
    public Slider ComboMulti = null!;

    private const double StrikeGap = 0.12;
    private const float StrikeReach = 1500f;
    private static readonly Random Rng = new();
    private Every _strikeGap;

    private readonly List<RPawnVillain> _watch = new();
    private Every _rewatch;

    /// <summary>Red attacks whose counter window is open, by move, with the attacker and when it opened.</summary>
    private readonly Dictionary<nint, (RCombatMove_VillainAttack Move, RPawnVillain Villain, double Opened)> _redOpen = new();

    /// <summary>Attack moves already answered, so one charge isn't vaulted twice. Moves are reused, so entries expire.</summary>
    private readonly Dictionary<nint, double> _answered = new();

    public override void Build()
    {
        AutoCounter = AddToggle("auto_counter", "AUTO COUNTER", "Counters every attack aimed at you the moment its counter window opens, knife slashes included.",
            on => { if (!on) Forget(); }).EveryTick(CounterTick);
        AutoDodge = AddToggle("auto_dodge_red", "AUTO DODGE RED ATTACKS", "Vaults over red unblockable attacks and charging thugs. A charge that can't be vaulted is stopped, as a batarang would.",
            on => { if (!on) Forget(); }).EveryTick(DodgeTick);
        ComboLock = AddToggle("combo_lock", "COMBO NEVER RESETS", "Getting hit, missing a strike, double evading or waiting too long no longer resets your combo multiplier during a fight. It still banks normally when the fight ends.");
        ComboMulti = AddSlider("combo_multiplier", "COMBO MULTIPLIER", "Every hit adds this many to your combo instead of one, so special takedowns and the freeflow bonus come much sooner.",
            1f, 1f, 10f, 1f, 3f, Fmt.Multiplier);
        AutoFight = AddToggle("auto_fight", "AUTO FIGHT", "Batman fights by himself: he counters and dodges every attack, picks the best move from his whole moveset, and learns from every fight.",
            on => { if (!on) { Forget(); ReleaseTakedown(); } }).EveryTick(FightTick);
        Command forget = AddCommand("auto_fight_forget", "FORGET AUTO FIGHT TRAINING", "AUTO FIGHT forgets everything it has learned, including the training it ships with, and learns again from nothing. Reinstall Oracle to get the shipped training back.", ForgetTraining).Setting().Confirmed();
        forget.ConfirmPrompt = () => "FORGET EVERYTHING AUTO FIGHT HAS LEARNED?";
        GunMiss = AddToggle("gun_miss", "AUTO DODGE GUNFIRE", "Gunmen still open fire on you, but every shot misses.");

        AddToggle("one_hit_ko", "ONE HIT KO", "Every enemy near you is left on 1 health, so any hit knocks them out.").Maintain(Npcs.I.LeaveOnOneHealth);
        AddConsoleToggle("upgraded_batsuit", "UPGRADED BATSUIT", "Turns on the batsuit's combat upgrades.", new ConsoleProp("RPawnPlayerCombat", "bUpgradedBatsuit", "True", "False"));
        AlwaysTakedown = DebugFlags.AddFlag("always_takedown", "ALWAYS TAKEDOWN READY", "Special combo takedowns are always ready, no combo needed.",
            d => d.Debug_Combat_CanAlwaysTakedown, (d, v) => d.Debug_Combat_CanAlwaysTakedown = v);
        DebugFlags.AddFlag("freeflow_focus", "FREEFLOW FOCUS ALWAYS", "Freeflow focus is always available in fights.",
            d => d.Debug_Combat_AlwaysFreeflowFocusMode, (d, v) => d.Debug_Combat_AlwaysFreeflowFocusMode = v);
        CounterIcons = AddToggle("counter_icons", "COUNTER ICONS IN NG+", "Brings back the counter warning icons above attacking enemies in New Game + and Knightmare.");

        DebugFlags.AddFlag("silent_takedowns", "SILENT COMBAT TAKEDOWNS", "Combat takedowns are silent, so nearby enemies don't hear them.",
            d => d.Debug_Player_ForceSilentCombatTakedowns, (d, v) => d.Debug_Player_ForceSilentCombatTakedowns = v);
        DebugFlags.AddFlag("env_takedowns", "ENVIRONMENT TAKEDOWNS", "Batman uses environmental takedowns whenever a wall or object is near.",
            d => d.Debug_Combat_ForceEnvironmentUse, (d, v) => d.Debug_Combat_ForceEnvironmentUse = v);
        AddConsoleToggle("predator_combos", "COMBO TAKEDOWNS IN PREDATOR", "Combo takedowns work in predator encounters too.",
            new ConsoleProp("RPawnPlayerCombat", "bCanDoComboTakedownOnPredator", "True", "False"));
        AddConsoleToggle("double_shockwave", "DOUBLE SHOCKWAVE LANDING", "Shockwave landings trigger a second shockwave.",
            new ConsoleProp("RSpecialMoveConfig_ShockwaveLand", "bTriggerSecondaryShockwave", "True", "False"));
        AddConsoleToggle("heavy_counters", "HEAVY COUNTERS", "Every counter is one of the big slow-motion counters the game normally saves for special moments.",
            new ConsoleProp("RPawnPlayerCombat", "bForceHeavyCounter", "True", "False"));
        AddConsoleToggle("last_enemy_fights", "LAST ENEMY FIGHTS ON", "The last enemy standing is no longer knocked out early for a cinematic final blow: he takes as many hits as everyone else.",
            new ConsoleProp("RPawnVillain", "bQuickKOIfLastEnemy", "False", "True"));
    }

    public override void WorldChanged()
    {
        DebugFlags.Forget();
        Forget();
    }

    public override void LeftGame() => Forget();

    private void Forget()
    {
        _watch.Clear();
        _redOpen.Clear();
        _answered.Clear();
        _seen.Clear();
        _rewatch.Force();
        _specialFailed.Clear();
        _pending = null;
    }

    // ---- who might be attacking

    /// <summary>The player when on foot and able to fight; null in a vehicle or menu.</summary>
    private static RPawnPlayerCombat? Fighter() => World.InGame && World.Pawn is RPawnPlayerCombat { IsValid: true } me && !me.bDeleteMe ? me : null;

    private static bool Aims(RCombatMove_VillainAttack m, RPawnPlayerCombat me) => m.Target is { IsValid: true } t && t.Ptr == me.Ptr;

    /// <summary>Enemies close enough to attack soon. Rebuilt a few times a second; each entry is re-validated before use.</summary>
    private List<RPawnVillain> Watch(RPawnPlayerCombat me)
    {
        if (!_rewatch.Elapsed(WatchSeconds))
            return _watch;

        _watch.Clear();
        Vector3 at = me.Location;
        foreach (RPawnVillain v in World.All<RPawnVillain>())
        {
            if (!v.bDeleteMe && !v.bGoodAsDead && Vector3.DistanceSquared(v.Location, at) <= WatchRange * WatchRange)
                _watch.Add(v);
        }

        return _watch;
    }

    private bool Answered(nint move)
    {
        double now = GameNow;
        if (_answered.TryGetValue(move, out double at) && now - at < WindowLife)
            return true;

        return false;
    }

    // ---- AUTO COUNTER

    private void CounterTick() => Counter();

    /// <summary>Counters an attack aimed at the player if one is in its window. True when Batman did something.</summary>
    private bool Counter()
    {
        if (Fighter() is not { } me)
            return false;

        foreach (RPawnVillain v in Watch(me))
        {
            if (!v.IsValid || v.bDeleteMe || v.CombatMove is not { IsValid: true } move)
                continue;

            // Group (slow-mo) attacks are RCombatMove_VillainSimultaneousAttack, which derives from RCombatMove, not
            // RCombatMove_VillainAttack. Its CanCounterStart is empty: the window is per attacker in
            // VillainList[i].bCanCounter, read through its own CanCounter(), the same check TrySimultaneousCounter uses.
            string kind, cls;
            bool open, swinging;
            if (move is RCombatMove_VillainSimultaneousAttack group)
            {
                kind = "group attack";
                cls = "group attack";
                // CanCounter only goes true in the slow-motion moment, which the game skipped while Batman was mid-strike
                // and the group hit him with no try made. TryCounter(false) on a group attack in progress can't whiff, so
                // it is tried throughout.
                swinging = group.IsAttacking();
                open = group.CanCounter() || swinging;
            }
            else if (move is RCombatMove_VillainAttack m && Aims(m, me))
            {
                kind = m.AttackCounterType.ToString();
                cls = move.Class.Name;
                open = m.bCanCounter;
                swinging = m.IsAttacking();
            }
            else
                continue;

            Seen seen = Observe(move, kind, open, swinging, v);
            if (!open)
                continue;

            LearnLead(seen, cls);
            if (seen.OpenAt == 0)
                seen.OpenAt = GameNow;
            // Batman's own move still on its way: the counter waits a moment for it to land, since a counter that cuts it
            // off breaks the combo and one straight after it keeps it.
            if (move is RCombatMove_VillainAttack holdable && HoldCounter(holdable, seen))
                continue;

            // bCanFail false: never plays the whiff animation (which would also clear the combo). TryCounter hands
            // group attacks to TrySimultaneousCounter itself; each call counters the next attacker in the group.
            RPawnPlayerCombat.ETryCounterResult result;
            _defending = true;
            try
            {
                result = me.TryCounter(false, 0f);
            }
            finally
            {
                _defending = false;
            }

            seen.Tries++;
            seen.Last = result.ToString();
            seen.BatmanMove = me.CombatMove is { IsValid: true } bm ? bm.Class.Name : "none";
            if (result != RPawnPlayerCombat.ETryCounterResult.ETCR_Nothing)
            {
                BrainDefended(seen.Foe);
                seen.Countered = true;
                if (seen.Held)
                    HeldCounterLanded();
            }

            // One try per tick. TryCounter itself picks up simultaneous attackers.
            return result != RPawnPlayerCombat.ETryCounterResult.ETCR_Nothing;
        }

        return false;
    }

    // ---- missed-counter diagnostics
    // Every attack aimed at the player that auto counter sees is remembered briefly: whether its window opened and
    // what TryCounter said. When a hit still lands, DamagedBy logs the attacker against that record, so a log after a
    // fight shows which kind of attack slips through and why.

    private const double SeenLife = 4.0;
    private const int MaxHitReports = 40;
    private int _hitReports;

    private sealed class Seen
    {
        public required string Kind;

        /// <summary>The attacker's kind (thug, shield, knife...), for AUTO FIGHT's per-kind attack rates.</summary>
        public string Foe = "thug";
        public double At;

        /// <summary>When the attack was first seen aimed at Batman, and when it first swung (0 if not yet): AUTO FIGHT learns lead times from them.</summary>
        public double First, SwingFirst;

        /// <summary>When its counter window opened, and whether AUTO FIGHT held the counter for its own move to land.</summary>
        public double OpenAt;
        public bool Held, Countered, WindowClosed;
        public bool Sampled;
        public bool WindowOpened;
        public int Tries;
        public string Last = "not tried";
        public string BatmanMove = "";
    }

    private readonly Dictionary<nint, Seen> _seen = new();

    private Seen Observe(RCombatMove move, string kind, bool open, bool swinging, RPawnVillain attacker)
    {
        double now = GameNow;
        // Seen every tick while it lasts, so a gap means the move object is being reused for a new attack.
        if (!_seen.TryGetValue(move.Ptr, out Seen? s) || now - s.At > 0.3)
        {
            s = new Seen { Kind = kind, First = now, Foe = move is RCombatMove_VillainSimultaneousAttack ? "group" : KindOf(attacker) };
            _seen[move.Ptr] = s;
            LearnAttackStarted(s.Foe);
            TokenAttackStarted();
        }

        s.At = now;
        if (swinging && s.SwingFirst == 0)
            s.SwingFirst = now;
        s.WindowOpened |= open;

        if (_seen.Count > 64)
        {
            foreach (nint stale in _seen.Where(kv => now - kv.Value.At > SeenLife).Select(kv => kv.Key).ToList())
                _seen.Remove(stale);
        }

        return s;
    }

    private void ExplainHit(RPawnPlayerCombat me, RPawnCombat.FDamageInfo dmg, RPawnCombat.DamageResult result)
    {
        if (!(AutoCounter.On || AutoFight.On) || result is not (RPawnCombat.DamageResult.DMG_Hit or RPawnCombat.DamageResult.DMG_KO))
            return;

        string by = dmg.Attacker is { IsValid: true } a ? a.Class.Name : "nothing";
        BrainHit();
        _hitsThisFight++;
        _hitsBy[by] = _hitsBy.GetValueOrDefault(by) + 1;
        if (_hitReports >= MaxHitReports)
            return;

        _hitReports++;
        string type = dmg.DamageType is { IsValid: true } t ? t.Name : "?";
        string batman = me.CombatMove is { IsValid: true } bm ? bm.Class.Name : "none";
        string text = "Missed counter? Hit by " + by + " with " + type + (dmg.bCanBeBlocked ? "" : " (unblockable)")
                      + ". Batman was in " + batman + ", bCanDoCounter=" + me.bCanDoCounter + ".";

        if (dmg.Attacker is RPawnVillain { IsValid: true } v && v.CombatMove is { IsValid: true } move)
        {
            text += " Attacker move " + move.Class.Name;
            if (move is RCombatMove_VillainAttack m)
                text += " (" + m.AttackCounterType + ", aimed at you " + Aims(m, me) + ", bCanCounter now " + m.bCanCounter + ")";

            if (_seen.TryGetValue(move.Ptr, out Seen? held) && held.Held && GameNow - held.At <= SeenLife)
            {
                text += " (AUTO FIGHT had held this counter for its own move)";
                HeldCounterHit();
            }

            text += _seen.TryGetValue(move.Ptr, out Seen? s) && GameNow - s.At <= SeenLife
                ? ". Auto counter saw it: window opened " + s.WindowOpened + ", tries " + s.Tries + ", last result " + s.Last
                  + (s.Tries > 0 ? " while Batman was in " + s.BatmanMove : "") + "."
                : ". Auto counter never saw this attack.";
        }
        else if (dmg.Attacker is RPawnVillain { IsValid: true })
            text += " Attacker had no combat move.";

        Log.Info(text);
    }

    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.DamagedBy))]
    private static RPawnCombat.DamageResult DamagedByRedirect(RPawnPlayerCombat self, RPawnCombat.FDamageInfo dmgInfo)
    {
        RPawnCombat.DamageResult result = self.DamagedBy(dmgInfo);
        if (I.AutoCounter is { On: true } || I.AutoFight is { On: true })
            Safe.Run("missed counter report", () => I.ExplainHit(self, dmgInfo, result));
        return result;
    }

    // ---- AUTO FIGHT
    // Oracle's own fighter, the game's dev bot is not used. Each tick, in order:
    //   1. Defence, by reflex. Counter any attack whose window is open; knife combos count as held and end in the
    //      finishing counter. A charging enemy gets a quick batarang (the batarang's auto-aim ranks chargers at 1000);
    //      red attacks and charges that still close in are vaulted (DodgeTick).
    //   2. A pending gadget takedown fires, a running beatdown keeps going.
    //   3. Offence, by the brain (FightBrain.cs), whenever a new move can start without losing the combo: every move
    //      possible right now is scored by expected value, using timings and success rates it learns as it fights.
    //      Tough enemies (shields, stun sticks, guns, brutes, armour, electrified, ninjas) only ever get specials.
    //      ALWAYS TAKEDOWN READY is held on during fights so those specials are always there.
    // Specials, snatches and environment attacks are spawned on the chosen enemy or object directly, doing what the
    // button handlers do minus their stick-based target search. Quick gadgets aim along MovementDir, which is pointed
    // at the target only for the instant of the throw.

    private const double SpecialGap = 0.6;
    private const double SpecialRetry = 3.0;
    private const float SpecialReach = 900f;
    private const float CloseReach = 600f;
    private const double SuperDelay = 0.15;
    // Well beyond vault range (ChargeVault): a batarang at a charger 0.07 s before the vault on the same charger broke
    // the combo twice, because the vault started while the batarang still had the window shut.
    private const float BatarangMin = 800f;
    private const float BatarangMax = 2000f;
    // Short: the brain waits for each gadget to land (its learned time) before the next move anyway; a 1.5 s gap here
    // left Batman idle after gadgets.
    private const double GadgetGap = 0.5;
    private const double ComboWait = 0.5;
    // Takedowns and weapon destroys only reopen the combo (SetContinueComboTimer) when they finish, after 2-3 s.
    private const double MoveWait = 6.0;
    private const double ElectroTakedownGap = 3.0;
    private const double LastResort = 0.35;

    private nint _waitMove;
    private double _waitMoveAt;
    private const float BeatdownReach = 450f;

    /// <summary>A thug Oracle just cape-stunned to start a beatdown on, and when.</summary>
    private RPawnVillain? _beatdownTarget;
    private double _beatdownAt;
    private double _comboClosedAt;

    private const float GadgetReach = 1200f;

    /// <summary>The game's own combo numbers this fight, for the end-of-fight line: biggest combo and variation.</summary>
    private int _maxCombo;
    private int _maxVariation;

    /// <summary>Why the combo was reset this fight, by the game's reason string, and how often.</summary>
    private readonly Dictionary<string, int> _breaks = new();
    private readonly HashSet<string> _usedThisFight = new();

    /// <summary>When each kind of move may be used again (seconds of GameNow), so the moveset rotates.</summary>
    private readonly Dictionary<string, double> _moveReadyAt = new();

    /// <summary>Tough enemies whose special didn't start, by pawn, and when: they're passed over for a while.</summary>
    private readonly Dictionary<nint, double> _specialFailed = new();

    /// <summary>Charges a batarang was already thrown at.</summary>
    private readonly Dictionary<nint, double> _bataranged = new();

    /// <summary>When a quick gadget just thrown should be upgraded to its gadget takedown (0 for none).</summary>
    private double _superAt;

    /// <summary>True while AUTO FIGHT holds ALWAYS TAKEDOWN READY on for a fight.</summary>
    private bool _forcedTakedown;

    private int _hitsThisFight;
    private readonly Dictionary<string, int> _hitsBy = new();

    // Quick gadgets are listed with the brain (GadgetMoves). No quick explosive gel: it only sprays until detonated, so it
    // never lands in combo terms, and three logged combo breaks came straight after it.

    private void FightTick()
    {
        RPawnPlayerCombat? me = Fighter();
        RPlayerControllerCombat? pc = World.Pc as RPlayerControllerCombat;
        if (me == null || pc == null)
        {
            EndFight();
            return;
        }

        // 1. Defence, even in the moment before the game counts it as a fight (a stun stick landed there once).
        // Counter and dodge run here only when their own rows are off, so nothing runs twice in a tick.
        if (!AutoCounter.On && Counter())
            return;
        if (!AutoDodge.On)
            DodgeTick();

        if (!pc.IsInCombat(false))
        {
            EndFight();
            return;
        }

        ForceTakedown();
        _maxCombo = Math.Max(_maxCombo, pc.ComboSize);
        _maxVariation = Math.Max(_maxVariation, pc.MaxComboVariation);

        // The charger batarang is a move like any other: thrown while Batman's last move hadn't landed, it broke combos
        // of 36 and 25. So it waits for the combo to allow a new move; the vault still answers the charge if it can't.
        if ((pc.ComboSize <= 0 || pc.CanContinueCombo()) && BatarangCharger(pc, me))
            return;

        // 2. Follow-ups.
        if (_superAt > 0 && GameNow >= _superAt)
        {
            _superAt = 0;
            nint before = MovePtr(me);
            pc.UpgradeQuickGadgetToSuperGadget();
            if (MovePtr(me) != before)
                Used("gadget takedown");
        }

        // A cape stun Oracle started: release it at once for the quick stun (the button-release handler reaches the
        // ChargeUp state's UnLeashStun), then strike the stunned thug once the stun lands, which starts a beatdown.
        if (me.CombatMove is RCombatMove_BatmanStunStrike { IsValid: true })
            pc.BlockBreakerReleased();
        if (_beatdownTarget is { } bdt)
        {
            bool live = GameNow - _beatdownAt < 1.5 && bdt.IsValid && !bdt.bDeleteMe && !bdt.bGoodAsDead && bdt.Health > 0;
            if (!live || me.CombatMove is RCombatMove_BatmanBeatdown)
            {
                _beatdownTarget = null;
            }
            else if (pc.CanContinueCombo())
            {
                me.TryStrike(0f, bdt);
                return;
            }
        }

        if (me.CombatMove is RCombatMove_BatmanBeatdown { IsValid: true } beat)
        {
            Used("beatdown");
            // Only while the window is open (each beatdown hit reopens it): pressed after the beatdown had closed it, the
            // strike became a heavy strike that broke a combo of 7.
            if (pc.CanContinueCombo() && beat.Target is RPawnVillain { IsValid: true, bDeleteMe: false } bt)
                me.TryStrike(0f, bt);
            return;
        }

        CountExposure(me);
        ExpirePending();
        SettleReactions(force: false);
        SettlePounce();
        TokenTick(pc);

        // 3. The brain picks the next move, whenever one can start without losing the combo. The window's last moments
        // are part of its sums (waiting then costs the whole combo), so no separate last-moment rule is needed.
        if (!_strikeGap.Elapsed(StrikeGap) || !ComboOpen(pc, me))
            return;

        Think(pc, me);
    }

    /// <summary>
    /// Whether a new move can start without losing the combo. NewComboMoveStarted clears the combo ("New move started
    /// and cant continue combo") unless CanContinueCombo (bAllowCombo), then disables it until the move lands. So after
    /// a move starts, the next one waits for it to connect, however long its animation (throws take over a second).
    /// Only when Batman has been out of any move for ComboWait without the combo reopening (a gadget that missed) does
    /// he go on anyway, rather than stand idle.
    /// </summary>
    private bool ComboOpen(RPlayerControllerCombat pc, RPawnPlayerCombat me)
    {
        // A strike the instant the last hit lands is a critical strike, so strikes never wait once the combo reopens.
        if (pc.ComboSize <= 0 || pc.CanContinueCombo())
        {
            _comboClosedAt = 0;
            return true;
        }

        if (me.CombatMove is { IsValid: true } move)
        {
            // Some moves wait for the next press rather than landing on their own (the throw's grab waits for where to
            // throw him). Waiting for those to land would freeze Batman in the pose, so after MoveWait in one move he
            // acts anyway.
            _comboClosedAt = 0;
            if (move.Ptr != _waitMove)
            {
                _waitMove = move.Ptr;
                _waitMoveAt = GameNow;
            }

            if (GameNow - _waitMoveAt < MoveWait)
                return false;

            _waitMoveAt = GameNow;
            return true;
        }

        if (_comboClosedAt == 0)
            _comboClosedAt = GameNow;
        if (GameNow - _comboClosedAt < ComboWait)
            return false;

        // Waited out with Batman idle: the last move never landed (a gadget that missed). The brain counts it as a
        // failure of that move and goes on.
        ResolvePending(false, "never landed");
        return true;
    }

    // ---- defence extras

    /// <summary>
    /// A charging enemy gets a quick batarang while he's still some way off. The batarang's auto-aim ranks a charge at
    /// 1000 (GetBatarangPriority), so it goes for him; a hit sets bQuickBataranged and slows him. If it misses, the
    /// charge is vaulted when it gets close, as before.
    /// </summary>
    private bool BatarangCharger(RPlayerControllerCombat pc, RPawnPlayerCombat me)
    {
        foreach (RPawnVillain v in Watch(me))
        {
            if (!v.IsValid || v.bDeleteMe || v.CombatMove is not RCombatMove_VillainChargeAttack { IsValid: true } c)
                continue;
            // Move objects are reused for later charges, so a charge counts as answered only for a few seconds.
            if (!c.bCharging || c.bStartedGrab || c.bQuickBataranged || !Aims(c, me)
                || _bataranged.TryGetValue(c.Ptr, out double thrown) && GameNow - thrown < 4)
                continue;

            float dist = Vector3.Distance(v.Location, me.Location);
            if (dist < BatarangMin || dist > BatarangMax)
                continue;

            _bataranged[c.Ptr] = GameNow;

            if (FireGadget(pc, me, 0, v))
            {
                Used("batarang");
                return true;
            }
        }

        if (_bataranged.Count > 32)
            _bataranged.Clear();
        return false;
    }

    private bool StartBeatdown(RPawnPlayerCombat me, RPawnVillain enemy)
    {
        if (!me.TryStun(enemy, false))
            return false;

        _beatdownTarget = enemy;
        _beatdownAt = GameNow;
        return true;
    }

    // ---- the moveset

    private bool Ready(string move) => !_moveReadyAt.TryGetValue(move, out double at) || GameNow >= at;

    private void Cool(string move, double seconds) => _moveReadyAt[move] = GameNow + seconds;

    private static bool CanStartSpecial(RPlayerControllerCombat pc, RPawnPlayerCombat me) =>
        pc.CanDoCombat(false, false) && (me.CombatMove is not { IsValid: true } m || m.CanPerformNextComboMove(true, false));

    /// <summary>Runs a spawn call when Batman can start a special move; true when his move changed.</summary>
    private static bool Spawn(RPlayerControllerCombat pc, RPawnPlayerCombat me, Action spawn)
    {
        if (!CanStartSpecial(pc, me))
            return false;

        nint before = MovePtr(me);
        spawn();
        return MovePtr(me) != before;
    }

    /// <summary>Takes a bat or pipe out of an enemy's hands, or picks one up off the floor (what TrySpawnWeaponSnatch does).</summary>
    private static bool Snatch(RPlayerControllerCombat pc, RPawnPlayerCombat me, RPawnVillain? from, RBMCombatThrownObject? pickup)
    {
        if (!pc.CanDoWeaponSnatch(true))
            return false;

        return Spawn(pc, me, () =>
        {
            pc.NewComboMoveStarted();
            me.SpawnWeaponSnatch(from!, pickup!);
        });
    }

    /// <summary>A weapon lying close by, looked for toward the enemy and then around Batman.</summary>
    private RBMCombatThrownObject? FloorWeapon(RPlayerControllerCombat pc, RPawnPlayerCombat me, RPawnVillain enemy)
    {
        if (!Ready("weapon search"))
            return null;

        Cool("weapon search", 1.0);
        Vector3 toward = enemy.Location - me.Location;
        toward.Z = 0f;
        if (toward.LengthSquared() > 1f && pc.FindWeaponToPickup(Vector3.Normalize(toward), CloseReach) is { IsValid: true } ahead)
            return ahead;

        for (int i = 0; i < 4; i++)
        {
            float a = i * MathF.PI / 2f;
            if (pc.FindWeaponToPickup(new Vector3(MathF.Cos(a), MathF.Sin(a), 0f), CloseReach) is { IsValid: true } found)
                return found;
        }

        return null;
    }

    /// <summary>A gadget by EBatmanGadgetList slot. The list lives on RPawnPlayer, which derives from RPawnPlayerCombat.</summary>
    private static RInventoryGadget? Gadget(RPawnPlayerCombat me, int slot) => me is not RPawnPlayer p ? null : slot switch
    {
        0 => p.GadgetList_0,
        3 => p.GadgetList_3,
        4 => p.GadgetList_4,
        7 => p.GadgetList_7,
        8 => p.GadgetList_8,
        _ => null,
    };

    /// <summary>
    /// Quick-fires a gadget at an enemy. The throw aims along MovementDir, which is pointed at him only for this call and
    /// put back straight after, so nothing else that reads it is affected.
    /// </summary>
    private static bool FireGadget(RPlayerControllerCombat pc, RPawnPlayerCombat me, int slot, RPawnVillain at)
    {
        if (Gadget(me, slot) is not { IsValid: true } g || !g.CanQuickFire())
            return false;
        if (WouldDoubleGadget(me, g))
            return false;

        Vector3 d = at.Location - me.Location;
        d.Z = 0f;
        Vector3 saved = me.MovementDir;
        if (d.LengthSquared() > 1f)
            me.MovementDir = Vector3.Normalize(d);
        try
        {
            return pc.FireQuickGadget(g, false);
        }
        finally
        {
            me.MovementDir = saved;
        }
    }

    /// <summary>
    /// The game's own rule (RProjectileGadgetBase.TryContinueComboStartMove): when a quick gadget continues the combo, it
    /// clears it ("Double gadget", "Double REC", "Brang clearing") if Batman's last attack move is this gadget's combo move
    /// class. So a gadget that would follow its own kind of move isn't thrown at all. (REC, batarang and freeze blast
    /// 0.53 s apart broke a combo of 6 that way.)
    /// </summary>
    private static bool WouldDoubleGadget(RPawnPlayerCombat me, RInventoryGadget g)
    {
        if (g is not RProjectileGadgetBase { ComboCombatClass: { IsValid: true } cc })
            return false;

        string last = me.LastAttackMoveClassName.ToString() ?? "";
        return last.Length > 0 && string.Equals(last, cc.Name.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A quick gadget at the enemy, followed by its gadget takedown (the special combo upgrade) when that kind of gadget
    /// takedown isn't in this combo yet: it counts as a move kind of its own for the variation bonus.
    /// </summary>
    private bool QuickGadget(RPlayerControllerCombat pc, RPawnPlayerCombat me, int slot, RPawnVillain enemy, RGameInfo.EComboMoveType super)
    {
        if (!FireGadget(pc, me, slot, enemy))
            return false;

        Cool("gadget", GadgetGap);

        if (Ready("gadget takedown") && pc.CanDoGadgetSpecial() && !InCombo(pc, super))
        {
            _superAt = GameNow + SuperDelay;
            Cool("gadget takedown", 6);
        }

        return true;
    }

    // ---- tough enemies and specials

    /// <summary>A weapon weapon destroy should break: shields, stun sticks and guns, or anything that blocks strikes.</summary>
    private static bool BreakableWeapon(RPawnVillain v) =>
        v.CurrWeapon is { IsValid: true } w
        && (w.bCanBlockHoldingWeapon || w.WeaponDestroyType is RPawnPlayerCombat.EWeaponDestroyType.WDT_Shield
            or RPawnPlayerCombat.EWeaponDestroyType.WDT_StunStick or RPawnPlayerCombat.EWeaponDestroyType.WDT_Gun);

    private static bool IsBrute(RPawnVillain v) => v.CombatPawnType == RPawnCombat.ECombatPawnType.ECPT_MultiStage;

    /// <summary>An enemy that gets a special instead of strikes: breakable weapon, brute, armour, electrified, or a blocker.</summary>
    private static bool NeedsSpecial(RPawnPlayerCombat me, RPawnVillain v) =>
        // Ninjas (ECPT_Ninja, e.g. RPawnVillainCombatExpert with a sword) dodge ordinary strikes: a heavy strike at one
        // never landed and the next forced counter broke a combo of 39. A takedown they can't dodge.
        BreakableWeapon(v) || IsBrute(v) || v.bArmoured || v.ChargedUpBy is { IsValid: true }
        || v.CombatPawnType == RPawnCombat.ECombatPawnType.ECPT_Ninja
        || v.CanBlockAttack(me, RDmgType_Strike.StaticClass()) || v.CanBlockAttack(me, RDmgType_HeavyStrike.StaticClass());

    /// <summary>
    /// What SpawnTakedown does, on a chosen enemy. With <paramref name="environment"/> the game's ENVIRONMENT TAKEDOWNS
    /// switch is on for just this call, so a wall or object nearby turns it into an environment takedown.
    /// </summary>
    private void Takedown(RPlayerControllerCombat pc, RPawnPlayerCombat me, RPawnVillain v, bool environment)
    {
        RPersistentDebugData? d = environment ? DebugFlags.Data : null;
        bool was = d is { Debug_Combat_ForceEnvironmentUse: true };
        if (d != null)
            d.Debug_Combat_ForceEnvironmentUse = true;
        try
        {
            pc.MovesSinceSpecial = 0;
            pc.SpendAdrenaline();
            pc.NewComboMoveStarted();
            if (!v.SpawnOverrideTakedown(me))
                me.SpawnComboTakeDown(v);
        }
        finally
        {
            if (d != null && !was)
                d.Debug_Combat_ForceEnvironmentUse = false;
        }
    }

    private void Fail(RPawnVillain v, string why)
    {
        _specialFailed[v.Ptr] = GameNow;
    }

    // ---- ALWAYS TAKEDOWN READY, held on during an AUTO FIGHT fight

    private void ForceTakedown()
    {
        if (DebugFlags.Data is not { } d)
            return;

        if (!d.Debug_Combat_CanAlwaysTakedown)
            d.Debug_Combat_CanAlwaysTakedown = true;
        _forcedTakedown = true;
    }

    /// <summary>Gives ALWAYS TAKEDOWN READY back to its own row once AUTO FIGHT stops holding it.</summary>
    private void ReleaseTakedown()
    {
        if (!_forcedTakedown)
            return;

        _forcedTakedown = false;
        if (AlwaysTakedown is { On: false } && DebugFlags.Data is { } d)
            d.Debug_Combat_CanAlwaysTakedown = false;
    }

    private void EndFight()
    {
        ReleaseTakedown();
        if (_pending != null || _tally.Count > 0 || _model is { Dirty: true })
            BrainSummary();
        _specialFailed.Clear();
        _moveReadyAt.Clear();
        _bataranged.Clear();
        _superAt = 0;
        if (_usedThisFight.Count == 0 && _hitsThisFight == 0)
            return;

        string hits = _hitsThisFight == 0 ? "no hits taken" : _hitsThisFight + " hits taken ("
            + string.Join(", ", _hitsBy.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)) + ")";
        string breaks = _breaks.Count == 0 ? "combo never broken" : "combo broken by "
            + string.Join(", ", _breaks.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " x" + kv.Value));
        Log.Info("Auto fight: fight over, used " + _usedThisFight.Count + " kinds of move (" + string.Join(", ", _usedThisFight) + "), " + hits
                 + ". Best combo " + _maxCombo + ", game's variation " + _maxVariation + ", " + breaks + ".");
        _usedThisFight.Clear();
        _hitsThisFight = 0;
        _hitsBy.Clear();
        _breaks.Clear();
        _maxCombo = 0;
        _maxVariation = 0;
    }

    private static string Describe(RPawnVillain v)
    {
        string state = v is RPawnVillainMultiStageBase ms ? " " + ms.ThugState : "";
        string weapon = v.CurrWeapon is { IsValid: true } w ? " with " + w.Class.Name : "";
        return v.Class.Name + state + weapon;
    }

    private static nint MovePtr(RPawnPlayerCombat me) => me.CombatMove is { IsValid: true } m ? m.Ptr : 0;

    /// <summary>Counts a kind of move for the end-of-fight summary.</summary>
    private void Used(string move) => _usedThisFight.Add(move);

    // ---- AUTO DODGE RED ATTACKS

    private static bool IsRed(RCombatMove_VillainAttack m) =>
        m.AttackCounterType is RPawnVillain.AttackCounterTypes.ACT_Unblockable or RPawnVillain.AttackCounterTypes.ACT_UnblockableNoIcon;

    /// <summary>Called from the CanCounterStart redirect: remembers a red attack on the player whose window just opened.</summary>
    private void WindowOpened(RCombatMove_VillainAttack m)
    {
        if (!IsRed(m) || Fighter() is not { } me || !Aims(m, me) || m.Villain is not { IsValid: true } v)
            return;

        _redOpen[m.Ptr] = (m, v, GameNow);
        LearnRedLead(m);
    }

    /// <summary>A red attack's window opened: the brain learns how long that attack class takes to need a vault.</summary>
    private void LearnRedLead(RCombatMove_VillainAttack m)
    {
        if (_seen.TryGetValue(m.Ptr, out Seen? s) && GameNow - s.At < 0.5)
            LearnLead(s, m.Class.Name);
    }

    private void DodgeTick()
    {
        if (Fighter() is not { } me)
        {
            _redOpen.Clear();
            return;
        }

        _defending = true;
        try
        {
            AnswerRedWindows(me);
            AnswerRedPolled(me);
            AnswerCharges(me);
        }
        finally
        {
            _defending = false;
        }
    }

    /// <summary>
    /// Red attacks whose class overrides CanCounterStart (stun sticks and shields) never reach the window redirect, so
    /// they're polled: TryRedirect itself asks the attack's CanRedirect, which is false outside its vault window.
    /// </summary>
    private void AnswerRedPolled(RPawnPlayerCombat me)
    {
        foreach (RPawnVillain v in Watch(me))
        {
            if (!v.IsValid || v.bDeleteMe || v.CombatMove is not RCombatMove_VillainAttack { IsValid: true } m)
                continue;
            if (m is RCombatMove_VillainChargeAttack || !IsRed(m) || !Aims(m, me) || Answered(m.Ptr))
                continue;

            if (me.TryRedirect(v))
            {
                // Its vault window just opened (TryRedirect asks CanRedirect): that is when this attack needs answering.
                LearnRedLead(m);
                Done(m.Ptr);
            }
        }
    }

    private void AnswerRedWindows(RPawnPlayerCombat me)
    {
        if (_redOpen.Count == 0)
            return;

        double now = GameNow;
        foreach ((nint key, (RCombatMove_VillainAttack m, RPawnVillain v, double opened)) in _redOpen.ToList())
        {
            // A destroyed wrapper must not be touched, so validity comes first.
            bool live = now - opened < WindowLife && m.IsValid && v.IsValid && !v.bDeleteMe && !v.bGoodAsDead
                        && v.CombatMove is { IsValid: true } cur && cur.Ptr == m.Ptr && Aims(m, me);
            if (!live || Answered(key))
            {
                _redOpen.Remove(key);
                continue;
            }

            if (me.TryRedirect(v))
            {
                Done(key);
                continue;
            }

            // Batman couldn't break into a vault yet (mid-move). Give it a moment, then plain evade away.
            if (now - opened >= EvadeAfter)
            {
                me.SpawnEvade(Away(me, v));
                Done(key);
            }
        }
    }

    private void AnswerCharges(RPawnPlayerCombat me)
    {
        foreach (RPawnVillain v in Watch(me))
        {
            if (!v.IsValid || v.bDeleteMe || v.CombatMove is not RCombatMove_VillainChargeAttack { IsValid: true } c)
                continue;
            if (!c.bCharging || c.bStartedGrab || !Aims(c, me) || Answered(c.Ptr))
                continue;

            float dist = Vector3.Distance(v.Location, me.Location);
            if (dist > ChargeVault)
                continue;

            // Batman's own quick move still on its way with a combo to keep: the vault waits while the charger runs from
            // vault range to stop range, so the move lands first and the vault then keeps the combo (a vault 2 ms after a
            // last-moment batarang broke a combo of 36). Inside stop range he is stopped or vaulted as before.
            if (dist > ChargeStop && HoldForOwnMove())
                continue;

            // In the middle of a long protected move (a takedown, a weapon destroy) with a combo to keep, the vault would cut
            // the move short and break the combo (a combo of 16, mid weapon destroy). The charge is stopped instead, the way
            // a quick batarang hit stops one, and the move plays out.
            if (AutoFight is { On: true } && World.Pc is RPlayerControllerCombat { ComboSize: > 0 }
                && me.CombatMove is RCombatMove_BatmanComboTakedown or RCombatMove_BatmanWeaponDestroy)
            {
                c.StopCharge(false);
                Done(c.Ptr);
            }
            else if (me.TryRedirect(v))
            {
                // How long this charge took from its wind-up to needing the vault: the brain's lead time for charges. Without
                // it every charge looked about to land 0.8 s in, and Batman waited on chargers until his combo ran out.
                if (_seen.TryGetValue(c.Ptr, out Seen? cs) && GameNow - cs.At < 0.5)
                    LearnLead(cs, c.Class.Name);
                Done(c.Ptr);
            }
            else if (dist <= ChargeStop)
            {
                c.StopCharge(false);
                Done(c.Ptr);
            }
        }
    }

    private void Done(nint move)
    {
        BrainDefended(_seen.TryGetValue(move, out Seen? by) ? by.Foe : null);
        _answered[move] = GameNow;
        _redOpen.Remove(move);

        if (_answered.Count > 64)
        {
            double now = GameNow;
            foreach (nint stale in _answered.Where(kv => now - kv.Value >= WindowLife).Select(kv => kv.Key).ToList())
                _answered.Remove(stale);
        }
    }

    private static Vector3 Away(RPawnPlayerCombat me, RPawnVillain v)
    {
        Vector3 d = me.Location - v.Location;
        d.Z = 0f;
        return d.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(d);
    }

    // A knife combo (RCombatMove_VillainMultiAttackAndCounter, ACT_Blade) is one counter followed by several slashes.
    // Each slash asks PlayerIsCounteringProperly: the counter button held (PlayerInput, read in UpdateCounterHeld) and,
    // after the first slash, the stick pulled away from the attacker. Auto counter can't hold a button, so it says yes.

    [Redirect(typeof(RCombatMove_VillainMultiAttackAndCounter), nameof(RCombatMove_VillainMultiAttackAndCounter.PlayerIsCounteringProperly))]
    private static bool KnifeCounterRedirect(RCombatMove_VillainMultiAttackAndCounter self)
    {
        if ((I.AutoCounter is { On: true } || I.AutoFight is { On: true })
            && Safe.Try("knife counter", () => self.Target is { IsValid: true } t && t.Ptr == World.PawnPtr))
            return true;

        return self.PlayerIsCounteringProperly();
    }

    // When the knife combo ends, VillainPlayedSuccess gives the extreme success (a combo takedown on a brute, a
    // finishing counter on a thug) only if NumCounterReleases >= NumCombatAnimHits: one counter tap per slash. Auto
    // counter never taps a real button, so it is credited with one tap per slash; otherwise every knife combo ends as a
    // plain dodge and the attacker just starts the next one.

    [Redirect(typeof(RCombatMove_VillainMultiAttackAndCounter), nameof(RCombatMove_VillainMultiAttackAndCounter.VillainPlayedSuccess))]
    private static void KnifeSuccessRedirect(RCombatMove_VillainMultiAttackAndCounter self, RAnimUtil_PosePlayer.FTransitionId newId, float timeLeftOver)
    {
        if (I.AutoCounter is { On: true } || I.AutoFight is { On: true })
            Safe.Run("knife finish", () => I.CreditKnifeTaps(self));

        self.VillainPlayedSuccess(newId, timeLeftOver);
    }

    private void CreditKnifeTaps(RCombatMove_VillainMultiAttackAndCounter m)
    {
        if (m.Target is not { IsValid: true } t || t.Ptr != World.PawnPtr || m.bPlayerHit || m.NumCounterReleases >= m.NumCombatAnimHits)
            return;

        m.NumCounterReleases = m.NumCombatAnimHits;
    }

    // CanCounterStop: an attack's counter window shutting. Measured on attacks Batman didn't counter (they opened while he
    // was in a takedown), it tells AUTO FIGHT how long a counter may safely wait. VillainCloseAttack overrides it, so it
    // is redirected too; LearnWindow takes each attack once.

    private void WindowShut(RCombatMove_VillainAttack m)
    {
        if (Fighter() is { } me && Aims(m, me) && _seen.TryGetValue(m.Ptr, out Seen? s) && GameNow - s.At < 0.5)
            LearnWindow(s, m.Class.Name);
    }

    [Redirect(typeof(RCombatMove_VillainAttack), nameof(RCombatMove_VillainAttack.CanCounterStop))]
    private static void CanCounterStopRedirect(RCombatMove_VillainAttack self)
    {
        if (I.AutoFight is { On: true })
            Safe.Run("counter window shut", () => I.WindowShut(self));
        self.CanCounterStop();
    }

    [Redirect(typeof(RCombatMove_VillainCloseAttack), nameof(RCombatMove_VillainCloseAttack.CanCounterStop))]
    private static void CloseCanCounterStopRedirect(RCombatMove_VillainCloseAttack self)
    {
        if (I.AutoFight is { On: true })
            Safe.Run("counter window shut", () => I.WindowShut(self));
        self.CanCounterStop();
    }

    [Redirect(typeof(RCombatMove_VillainAttack), nameof(RCombatMove_VillainAttack.CanCounterStart))]
    private static void CanCounterStartRedirect(RCombatMove_VillainAttack self)
    {
        self.CanCounterStart();
        if (I.AutoDodge is { On: true } || I.AutoFight is { On: true })
            Safe.Run("auto dodge window", () => I.WindowOpened(self));
    }

    // ---- COMBO NEVER RESETS
    // Every combo reset goes through ClearCombo(Reason). The reasons are fixed strings in the callers ("Player hit",
    // "Combo timer ran out", "No target for strike", "Double evade", "Hit blocker", ...). Outside a fight, and for the
    // banking reasons, the reset goes ahead so the HUD and XP close out as usual.

    private bool KeepCombo(RPlayerControllerCombat pc, FString reason)
    {
        if (pc.ComboSize <= 0)
            return false;

        string why = reason.ToString() ?? "";
        return !BankingReasons.Contains(why) && pc.IsInCombat(false);
    }

    /// <summary>
    /// During an AUTO FIGHT fight, notes why the game wants to reset the combo: the variation bonus only counts move
    /// kinds inside one unbroken combo (ClearCombo wipes MoveTypeCount), so every break costs score.
    /// </summary>
    private void NoteBreak(RPlayerControllerCombat pc, FString reason)
    {
        // Between rounds the game doesn't count as a fight, but a combo carried over can still be lost there, so this
        // doesn't check IsInCombat (banking reasons still close a fight out quietly).
        if (AutoFight is not { On: true } || pc.ComboSize <= 0)
            return;

        string why = reason.ToString() ?? "";
        if (BankingReasons.Contains(why))
            return;

        why = why.Split(' ').Length > 4 ? string.Join(' ', why.Split(' ').Take(4)) : why;
        _breaks[why] = _breaks.GetValueOrDefault(why) + 1;
        // Always logged, not through the capped Trace: two breaks of a long fight went unrecorded once the cap was reached.
        Log.Info("Auto fight: combo of " + pc.ComboSize + " broken: " + reason + ". Batman was in "
              + (pc.CombatPawn is RPawnPlayerCombat { CombatMove: { IsValid: true } m } ? m.Class.Name : "nothing") + ". Last thought: " + _thought + ".");
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.SetContinueComboTimer))]
    private static void SetContinueComboTimerRedirect(RPlayerControllerCombat self, float comboWindow)
    {
        self.SetContinueComboTimer(comboWindow);
        if (I.AutoFight is { On: true })
            Safe.Run("fight brain landed", I.BrainLanded);
    }

    // MovePerformed(type, ...) runs as each move scores. AUTO FIGHT learns which combo move type each of its moves counts
    // as, so "a move kind not yet in this combo" is judged by the same types the Variation bonus counts.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.MovePerformed))]
    private static void MovePerformedRedirect(RPlayerControllerCombat self, RGameInfo.EComboMoveType moveType, RPawnCombat target, bool bAddScore, int comboForScore, int numOfTargets)
    {
        self.MovePerformed(moveType, target, bAddScore, comboForScore, numOfTargets);
        if (I.AutoFight is { On: true })
            Safe.Run("fight brain move type", () => I.BrainMoveType(moveType, target));
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.ClearCombo))]
    private static void ClearComboRedirect(RPlayerControllerCombat self, FString reason, bool bForceDropWeapon)
    {
        if (I.AutoFight is { On: true })
        {
            Safe.Run("combo break note", () => I.NoteBreak(self, reason));
            Safe.Run("fight brain broken", () => I.BrainBroken(reason.ToString() ?? "", self.ComboSize));
        }

        if (I.ComboLock is { On: true } && Safe.Try("combo lock", () => I.KeepCombo(self, reason)))
            return;

        self.ClearCombo(reason, bForceDropWeapon);
    }

    // ---- COMBO MULTIPLIER
    // IncCombo's IncAmount is optional and its default is filled in inside the function, so the redirect can't scale
    // the argument. It lets the game add its own amount, then adds that gain again (multiplier - 1) times.

    private void BoostCombo(RPlayerControllerCombat pc, int before)
    {
        int gain = pc.ComboSize - before;
        int extra = (int)MathF.Round(ComboMulti.Value) - 1;
        if (gain <= 0 || extra <= 0)
            return;

        pc.ComboSize += gain * extra;
        if (pc.ComboSize > pc.MaxComboSize)
            pc.MaxComboSize = pc.ComboSize;
        pc.UpdateComboOnHUD();
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.IncCombo))]
    private static void IncComboRedirect(RPlayerControllerCombat self, RPawnCombat pawnAttacked, int incAmount, int incSpecialMoveCount, bool bDoNotIncBatmobileTakedown)
    {
        bool boosting = I.ComboMulti is { Value: > 1.5f };
        int before = boosting ? self.ComboSize : 0;
        self.IncCombo(pawnAttacked, incAmount, incSpecialMoveCount, bDoNotIncBatmobileTakedown);
        if (boosting)
            Safe.Run("combo multiplier", () => I.BoostCombo(self, before));
    }

    // ---- AUTO DODGE GUNFIRE
    // GunTick_Shooting(bForceMiss, ...) plays the shot either way, then only damages DamageTarget when
    // "AmountVisible != None && !bForceMiss". Forcing the flag for shots at the player makes every one of them miss.

    private static bool ShootsPlayer(RPawnVillain v)
    {
        if (v.DamageTarget is not { IsValid: true } t)
            return false;

        return t.Ptr == World.PawnPtr || World.Player is { } p && t.Ptr == p.Ptr;
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.GunTick_Shooting))]
    private static void GunTickShootingRedirect(RPawnVillain self, bool bForceMiss, AlertInstance.VisibilityCategory amountVisible, bool bCanFire)
    {
        if (!bForceMiss && I.GunMiss is { On: true } && Safe.Try("gun miss", () => ShootsPlayer(self)))
            bForceMiss = true;

        self.GunTick_Shooting(bForceMiss, amountVisible, bCanFire);
    }
}
