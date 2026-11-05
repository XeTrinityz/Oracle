using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using CMT = BmSDK.BmGame.RGameInfo.EComboMoveType;

namespace CeZeY.Oracle;

// ---- AUTO FIGHT's brain
// Oracle's own fighting AI. Defence (counters, vaults, the charger batarang) stays reflexive and lives in Combat.cs. This
// file decides the offence: each time a new move can start without losing the combo, every move Batman could make right
// now is scored, and he makes the best one or waits.
//
//   expected value = P(success) x (value + stake) - stake          (minus the cost of a hit, for moves he can't counter out of)
//   P(success)     = P(the move works here) x P(no defence has to cut in before it lands) x its self-correction
//   stake          = what breaking the combo would lose: grows faster than the combo, plus the move kinds already in it
//   value          = the move's worth times the combo (as challenge points are), a bonus for a move kind new to this
//                    combo, the target's priority
//
// Waiting is worth a little more than nothing, or -stake when the combo window is about to run out with no counter
// likely to refresh it in time. With no combo nothing is at stake, so Batman is fully aggressive and tries his less proven
// moves. Defence can wait a moment for his own move to land: counters up to a learned hold, charge vaults while the
// charger closes in.
//
// Everything is timed in game seconds and learned as he fights, kept in fight_brain.txt between sessions:
//   land, works, cut        how long each move takes to land, how often it works (overall, per enemy kind, when it is
//                           a repeat or aimed at an enemy a gadget just hit), how often a protected move is cut anyway
//   lead, swing, window     per enemy attack class: start (and swing) to needing an answer, and counter window length
//   gaps, overlap, rate     the combat manager's rationing of attacks, and per-kind attack rates and leads
//   react, pounce           enemies answering his moves at once, or pouncing as a takedown ends
//   hold, calib             how long a counter may wait, and each move's (and the last-moment wait's) self-correction
internal sealed partial class Combat
{
    // ---- the brain's clock: game time
    // Everything AUTO FIGHT times (moves landing, attack lead times, gaps, the counter hold, the combo window) is in game
    // seconds, which run faster or slower with GAME SPEED and the game's slow-motion moments. On the wall clock, a challenge
    // played at 2x made every learned timing twice too long and four combos broke in that minute. The game's clock restarts
    // with a level (or a challenge restart), so it is kept running forward from where it was.
    private static double _rawGameLast = double.NaN, _gameOffset;

    private static double GameNow
    {
        get
        {
            if (World.Info?.WorldInfo is not { IsValid: true } wi)
                return double.IsNaN(_rawGameLast) ? Clock.Now : _rawGameLast + _gameOffset;

            double raw = wi.TimeSeconds;
            if (!double.IsNaN(_rawGameLast) && raw < _rawGameLast)
                _gameOffset += _rawGameLast - raw;
            else if (double.IsNaN(_rawGameLast))
                _gameOffset = 0;
            _rawGameLast = raw;
            return raw + _gameOffset;
        }
    }

    private const string BrainFile = "fight_brain.txt";
    private const double LeadPrior = 0.8;
    private const double SwingLeadPrior = 0.35;
    private const double RatePrior = 0.12;
    private const float ChargeSpeed = 800f;
    private const double NoveltyValue = 50;
    private const double GadgetNoveltyValue = 30;
    private const double HitCost = 600;
    private const double PendingLife = 3.0;
    private const double ProtectedLife = 6.0;
    private const float RiskReach = 1500f;
    private const float GroundReach = 1500f;
    private const double HoldStart = 0.2;
    private const double ReactWindow = 0.6;
    private const double PounceWindow = 0.6;
    private const int GapKeep = 120;
    private const double AgainWithin = 2.5;

    /// <summary>How long a charge's vault can wait: the charger running from vault range to stop range.</summary>
    private static double ChargeHold => (ChargeVault - ChargeStop) / ChargeSpeed;
    // Small: the "same gadget again" odds now handle the repeats this was for, and a bigger margin added to the hesitation.
    private const double StakeMargin = 0.01;
    private const float ReactReach = 700f;
    private const double HoldMax = 0.5;

    // ---- the moves

    /// <summary>
    /// One kind of move. Land is the prior time to land by distance bucket (0-350, 350-700, 700-1100, 1100+), from logs.
    /// Protected moves are ones the game shields (enemies hold off), so the attack model doesn't apply. Exposed moves can't
    /// be countered out of: an attack arriving mid-move is a hit, not just a broken combo. Experiments are only made when
    /// little is at stake.
    /// </summary>
    private sealed record MoveSpec(string Name, CMT Type, double[] Land, double PriorOk, double PriorBad, double Value,
        bool Protected = false, bool Exposed = false, bool Gadget = false, bool Experiment = false, double Cooldown = 0);

    private static readonly double[] QuickLand = { 0.35, 0.6, 0.9, 1.2 };

    private static readonly MoveSpec MStrike = new("strike", CMT.ECMT_Strike, QuickLand, 19, 1, 12);
    private static readonly MoveSpec MBeatdown = new("beatdown", CMT.ECMT_BeatdownStart, new[] { 0.5, 0.8, 1.1, 1.4 }, 6, 2, 18, Cooldown: 8);
    private static readonly MoveSpec MAerial = new("aerial attack", CMT.ECMT_AerialAttack, new[] { 0.8, 0.9, 1.1, 1.3 }, 5, 2, 12, Cooldown: 3);
    private static readonly MoveSpec MSnatch = new("weapon snatch", CMT.ECMT_BMWeaponSnatch, new[] { 0.9, 1.1, 1.3, 1.5 }, 4, 2, 14, Cooldown: 4);
    private static readonly MoveSpec MPickup = new("weapon pickup", CMT.ECMT_GroundPickup, new[] { 1.0, 1.2, 1.4, 1.6 }, 3, 2, 10, Cooldown: 4);
    private static readonly MoveSpec MEnvAttack = new("environment attack", CMT.ECMT_PhysicsImpact, new[] { 1.2, 1.4, 1.6, 1.8 }, 4, 2, 16, Cooldown: 6);
    private static readonly MoveSpec MTakedown = new("special takedown", CMT.ECMT_Takedown, new[] { 1.8, 1.9, 2.0, 2.2 }, 9, 1, 30, Protected: true);
    private static readonly MoveSpec MEnvTakedown = new("environment takedown", CMT.ECMT_EnvironmentTakedown, new[] { 2.0, 2.1, 2.2, 2.4 }, 8, 1, 30, Protected: true);
    private static readonly MoveSpec MDestroy = new("weapon destroy", CMT.ECMT_BMDisarmAndDestroy, new[] { 2.3, 2.4, 2.5, 2.7 }, 9, 1, 30, Protected: true);
    private static readonly MoveSpec MGround = new("ground takedown", CMT.ECMT_GroundTakedown, new[] { 2.0, 2.2, 2.4, 2.6 }, 5, 2, 25, Exposed: true, Cooldown: 1);
    private static readonly MoveSpec MThrow = new("throw", CMT.ECMT_Strike, new[] { 1.4, 1.6, 1.8, 2.0 }, 1, 3, 10, Experiment: true, Cooldown: 6);

    private static readonly MoveSpec[] GadgetMoves =
    {
        new("batarang", CMT.ECMT_Batarang, new[] { 0.6, 0.8, 1.0, 1.2 }, 5, 2, 14, Gadget: true),
        new("REC", CMT.ECMT_REC, new[] { 0.8, 1.0, 1.1, 1.3 }, 5, 2, 14, Gadget: true),
        new("freeze blast", CMT.ECMT_Freeze, new[] { 0.8, 1.0, 1.2, 1.4 }, 5, 2, 14, Gadget: true),
        // The quick batclaw missed in two sessions; with the model it is only tried when little is at stake, and learns.
        new("batclaw", CMT.ECMT_Batclaw, new[] { 0.8, 1.0, 1.2, 1.4 }, 1, 2, 12, Gadget: true, Experiment: true),
    };

    /// <summary>EBatmanGadgetList slot for each gadget move, and the gadget takedown it upgrades to.</summary>
    private static readonly Dictionary<string, (int Slot, CMT Super)> GadgetInfo = new()
    {
        ["batarang"] = (0, CMT.ECMT_SuperComboBatarang),
        ["REC"] = (7, CMT.ECMT_SuperComboREC),
        ["freeze blast"] = (8, CMT.ECMT_SuperComboFreeze),
        ["batclaw"] = (3, CMT.ECMT_SuperComboBatclaw),
    };

    /// <summary>Combo move types that come from defence, so they never teach which type an offensive move produces.</summary>
    private static readonly HashSet<CMT> DefenceTypes = new()
    {
        CMT.ECMT_Counter, CMT.ECMT_CriticalCounter, CMT.ECMT_Redirect, CMT.ECMT_Evade, CMT.ECMT_ProjectileCounter,
        CMT.ECMT_BladeComboCounter, CMT.ECMT_SuperBladeComboCounter, CMT.ECMT_ChargerRedirect, CMT.ECMT_DodgedStrike,
        CMT.ECMT_BatarangCharger,
    };

    // ---- the model

    private sealed class Stat
    {
        public double Mean, Dev;
        public int N;

        public void Add(double x)
        {
            if (N == 0)
            {
                Mean = x;
                Dev = x * 0.25;
            }
            else
            {
                double a = Math.Max(0.15, 1.0 / (N + 1));
                double d = x - Mean;
                Mean += a * d;
                Dev += a * (Math.Abs(d) - Dev);
            }

            N++;
        }

        public double Low => Math.Max(0.15, Mean - 1.5 * Dev);
    }

    private sealed class Odds
    {
        public double Ok, Bad;
        public double P => Ok / Math.Max(1e-6, Ok + Bad);
        public double N => Ok + Bad;
    }

    private sealed class Model
    {
        public readonly Dictionary<string, Stat> Land = new();
        public readonly Dictionary<string, Odds> Works = new();
        public readonly Dictionary<string, Odds> Cut = new();
        public readonly Dictionary<string, Stat> Lead = new();
        public readonly Dictionary<string, Stat> SwingLead = new();
        public readonly Dictionary<string, Stat> Window = new();

        /// <summary>Per kind of enemy: attacks started and enemy-seconds of exposure (krate), and lead times (klead).</summary>
        public readonly Dictionary<string, double[]> KindRate = new();
        public readonly Dictionary<string, Stat> KindLead = new();

        /// <summary>Per kind of enemy: how often one close by attacked within ReactWindow of Batman starting a move.</summary>
        public readonly Dictionary<string, Odds> React = new();

        /// <summary>
        /// Per move: the success chances Batman predicted when he made it, added up, and how many really worked (both decay
        /// slowly), so a move he keeps over- or under-rating gets corrected. Beatdowns were judged 76-88% safe and were cut
        /// off by a counter 4 times in 13.
        /// </summary>
        public readonly Dictionary<string, double[]> Calib = new();
        public readonly Dictionary<string, int> Types = new();
        public double Attacks, EnemySeconds;
        public double Hold = HoldStart;

        /// <summary>How often a defence was needed within PounceWindow of a protected move (takedown, multi special) ending.</summary>
        public Odds Pounce = new() { Ok = 2, Bad = 3 };

        /// <summary>
        /// The combat manager's attack rationing: seconds from its attacker count dropping to 0 to the next attack
        /// starting (the most recent GapKeep of them), and attacks that started while one was already running.
        /// </summary>
        public readonly List<double> Gaps = new();
        public double Overlaps, BusySeconds;
        public bool Dirty;
    }

    private Model? _model;

    private Model Brain => _model ??= LoadModel();

    private static string BrainPath => Path.Combine(App.Dir, BrainFile);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Model LoadModel()
    {
        var m = new Model();
        // fight_brain.txt ships with Oracle's own training and then learns from the player's fights; an update replaces it.
        const string file = BrainFile;
        try
        {
            if (!File.Exists(BrainPath))
                return m;

            foreach (string raw in File.ReadAllLines(BrainPath))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                // Fields are tab separated: names have spaces in them.
                string[] p = raw.Split('\t');
                switch (p[0])
                {
                    case "land" when p.Length == 5: m.Land[p[1]] = new Stat { Mean = D(p[2]), Dev = D(p[3]), N = int.Parse(p[4]) }; break;
                    case "lead" when p.Length == 5: m.Lead[p[1]] = new Stat { Mean = D(p[2]), Dev = D(p[3]), N = int.Parse(p[4]) }; break;
                    case "window" when p.Length == 5: m.Window[p[1]] = new Stat { Mean = D(p[2]), Dev = D(p[3]), N = int.Parse(p[4]) }; break;
                    case "swing" when p.Length == 5: m.SwingLead[p[1]] = new Stat { Mean = D(p[2]), Dev = D(p[3]), N = int.Parse(p[4]) }; break;
                    case "works" when p.Length == 4: m.Works[p[1]] = new Odds { Ok = D(p[2]), Bad = D(p[3]) }; break;
                    case "cut" when p.Length == 4: m.Cut[p[1]] = new Odds { Ok = D(p[2]), Bad = D(p[3]) }; break;
                    case "type" when p.Length == 3: m.Types[p[1]] = int.Parse(p[2]); break;
                    case "rate" when p.Length == 3: m.Attacks = D(p[1]); m.EnemySeconds = D(p[2]); break;
                    case "hold" when p.Length == 2: m.Hold = D(p[1]); break;
                    case "gaps" when p.Length == 2: m.Gaps.AddRange(p[1].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(D)); break;
                    case "overlap" when p.Length == 3: m.Overlaps = D(p[1]); m.BusySeconds = D(p[2]); break;
                    case "pounce" when p.Length == 3: m.Pounce = new Odds { Ok = D(p[1]), Bad = D(p[2]) }; break;
                    case "calib" when p.Length == 4: m.Calib[p[1]] = new[] { D(p[2]), D(p[3]) }; break;
                    case "react" when p.Length == 4: m.React[p[1]] = new Odds { Ok = D(p[2]), Bad = D(p[3]) }; break;
                    case "krate" when p.Length == 4: m.KindRate[p[1]] = new[] { D(p[2]), D(p[3]) }; break;
                    case "klead" when p.Length == 5: m.KindLead[p[1]] = new Stat { Mean = D(p[2]), Dev = D(p[3]), N = int.Parse(p[4]) }; break;
                }
            }

            Log.Info("Auto fight: loaded what it has learned from " + file + " (" + m.Land.Count + " move timings, "
                     + m.Lead.Count + " attack timings, " + m.Works.Count + " success rates).");
        }
        catch (Exception ex)
        {
            Log.Info("Auto fight: could not read " + file + ", starting fresh: " + ex.Message);
            return new Model();
        }

        return m;
    }

    private void SaveModel()
    {
        if (_model is not { Dirty: true } m)
            return;

        try
        {
            var lines = new List<string>
            {
                "# What Oracle's AUTO FIGHT has learned. It ships trained and keeps learning from your fights. FORGET AUTO FIGHT TRAINING (SYSTEM > GENERAL), or deleting this file, starts from nothing; reinstalling Oracle brings the shipped training back.",
                "rate\t" + F(m.Attacks) + "\t" + F(m.EnemySeconds),
                "hold\t" + F(m.Hold),
                "pounce\t" + F(m.Pounce.Ok) + "\t" + F(m.Pounce.Bad),
                "gaps\t" + string.Join(";", m.Gaps.Select(F)),
                "overlap\t" + F(m.Overlaps) + "\t" + F(m.BusySeconds),
            };
            lines.AddRange(m.Land.OrderBy(kv => kv.Key).Select(kv => "land\t" + kv.Key + "\t" + F(kv.Value.Mean) + "\t" + F(kv.Value.Dev) + "\t" + kv.Value.N));
            lines.AddRange(m.Lead.OrderBy(kv => kv.Key).Select(kv => "lead\t" + kv.Key + "\t" + F(kv.Value.Mean) + "\t" + F(kv.Value.Dev) + "\t" + kv.Value.N));
            lines.AddRange(m.SwingLead.OrderBy(kv => kv.Key).Select(kv => "swing\t" + kv.Key + "\t" + F(kv.Value.Mean) + "\t" + F(kv.Value.Dev) + "\t" + kv.Value.N));
            lines.AddRange(m.Calib.OrderBy(kv => kv.Key).Select(kv => "calib\t" + kv.Key + "\t" + F(kv.Value[0]) + "\t" + F(kv.Value[1])));
            lines.AddRange(m.React.OrderBy(kv => kv.Key).Select(kv => "react\t" + kv.Key + "\t" + F(kv.Value.Ok) + "\t" + F(kv.Value.Bad)));
            lines.AddRange(m.KindRate.OrderBy(kv => kv.Key).Select(kv => "krate\t" + kv.Key + "\t" + F(kv.Value[0]) + "\t" + F(kv.Value[1])));
            lines.AddRange(m.KindLead.OrderBy(kv => kv.Key).Select(kv => "klead\t" + kv.Key + "\t" + F(kv.Value.Mean) + "\t" + F(kv.Value.Dev) + "\t" + kv.Value.N));
            lines.AddRange(m.Window.OrderBy(kv => kv.Key).Select(kv => "window\t" + kv.Key + "\t" + F(kv.Value.Mean) + "\t" + F(kv.Value.Dev) + "\t" + kv.Value.N));
            lines.AddRange(m.Works.OrderBy(kv => kv.Key).Select(kv => "works\t" + kv.Key + "\t" + F(kv.Value.Ok) + "\t" + F(kv.Value.Bad)));
            lines.AddRange(m.Cut.OrderBy(kv => kv.Key).Select(kv => "cut\t" + kv.Key + "\t" + F(kv.Value.Ok) + "\t" + F(kv.Value.Bad)));
            lines.AddRange(m.Types.OrderBy(kv => kv.Key).Select(kv => "type\t" + kv.Key + "\t" + kv.Value));
            File.WriteAllLines(BrainPath, lines);
            m.Dirty = false;
        }
        catch (Exception ex)
        {
            Log.Info("Auto fight: could not save " + BrainFile + ": " + ex.Message);
        }
    }

    /// <summary>Drops everything AUTO FIGHT has learned, shipped training included: it starts again from nothing.</summary>
    private void ForgetTraining()
    {
        try
        {
            if (File.Exists(BrainPath))
                File.Delete(BrainPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not delete " + BrainFile + ": " + ex.Message);
        }

        _model = null;
        Log.Info("Auto fight: training forgotten; learning again from nothing.");
    }

    private static int Bucket(float dist) => dist < 350f ? 0 : dist < 700f ? 1 : dist < 1100f ? 2 : 3;

    private double LandTime(MoveSpec s, float dist)
    {
        int b = Bucket(dist);
        // Conservative: the learned mean plus most of its spread, so a move that sometimes lands late is treated as late.
        return Brain.Land.TryGetValue(s.Name + "/" + b, out Stat? st) && st.N >= 2 ? st.Mean + st.Dev : s.Land[b];
    }

    private Odds WorksOdds(string key, MoveSpec s)
    {
        if (!Brain.Works.TryGetValue(key, out Odds? o))
        {
            o = new Odds { Ok = s.PriorOk, Bad = s.PriorBad };
            Brain.Works[key] = o;
        }

        return o;
    }

    /// <summary>How often a move works: the general rate for the move, pulled toward its rate against this kind of enemy.</summary>
    private double WorksP(MoveSpec s, string kind)
    {
        Odds all = WorksOdds(s.Name, s);
        if (!Brain.Works.TryGetValue(s.Name + "|" + kind, out Odds? k) || k.N < 1)
            return all.P;

        double w = k.N / (k.N + 4);
        return w * (k.Ok / k.N) + (1 - w) * all.P;
    }

    private Odds CutOdds(MoveSpec s)
    {
        if (!Brain.Cut.TryGetValue(s.Name, out Odds? o))
        {
            o = new Odds { Ok = 19, Bad = 1 };
            Brain.Cut[s.Name] = o;
        }

        return o;
    }

    private static Stat? LeadStat(Dictionary<string, Stat> d, string cls) => d.TryGetValue(cls, out Stat? s) && s.N >= 2 ? s : null;

    private double AttackRate => (Brain.Attacks + RatePrior * 20) / (Brain.EnemySeconds + 20);

    /// <summary>
    /// How soon a typical new attack (of the quickest single-attack class) needs answering after it starts: a new attack
    /// can't cut in sooner. Group attacks are left out: they are only seen once already swinging, so their lead reads 0.
    /// </summary>
    private double QuickLead()
    {
        double low = LeadPrior;
        foreach ((string cls, Stat s) in Brain.Lead)
        {
            if (s.N >= 3 && cls != "group attack")
                low = Math.Min(low, s.Mean - 0.5 * s.Dev);
        }

        return Math.Max(0.2, low);
    }

    /// <summary>
    /// How long a counter may wait after its window opens, so Batman's own move lands first. It grows with each held
    /// counter that went in fine and halves after a hit, and never comes within 0.15 s of the shortest counter window
    /// measured (attacks that ran their whole window while Batman was in a takedown).
    /// </summary>
    private double HoldBudget
    {
        get
        {
            double cap = HoldMax;
            foreach (Stat w in Brain.Window.Values)
            {
                if (w.N >= 3)
                    cap = Math.Min(cap, w.Low - 0.15);
            }

            return Math.Clamp(Math.Min(Brain.Hold, cap), 0.0, HoldMax);
        }
    }

    /// <summary>An attack's counter window shut with no counter from Batman: how long that class's window lasts.</summary>
    private void LearnWindow(Seen s, string cls)
    {
        if (s.WindowClosed || s.OpenAt <= 0 || s.Countered || AutoFight is not { On: true })
            return;

        s.WindowClosed = true;
        double length = GameNow - s.OpenAt;
        if (length < 0.05 || length > 3)
            return;

        if (!Brain.Window.TryGetValue(cls, out Stat? w))
            Brain.Window[cls] = w = new Stat();
        w.Add(length);
        Brain.Dirty = true;
        Report("window " + cls, "Auto fight: measured a " + cls + " counter window of " + length.ToString("0.00") + " s.");
    }

    // ---- learning from attacks (called from the defence code)

    /// <summary>An attack's counter or vault window just opened: how long it took from the attack starting, and from its swing.</summary>
    private void LearnLead(Seen s, string cls)
    {
        if (s.Sampled || AutoFight is not { On: true })
            return;

        s.Sampled = true;
        double now = GameNow;
        if (!Brain.Lead.TryGetValue(cls, out Stat? lead))
            Brain.Lead[cls] = lead = new Stat();
        lead.Add(now - s.First);
        if (!Brain.KindLead.TryGetValue(s.Foe, out Stat? kl))
            Brain.KindLead[s.Foe] = kl = new Stat();
        kl.Add(now - s.First);
        if (s.SwingFirst > 0)
        {
            if (!Brain.SwingLead.TryGetValue(cls, out Stat? swing))
                Brain.SwingLead[cls] = swing = new Stat();
            swing.Add(now - s.SwingFirst);
        }

        Brain.Dirty = true;
    }

    /// <summary>A new attack aimed at Batman started: counts toward the attack rate.</summary>
    private void LearnAttackStarted(string foe)
    {
        if (AutoFight is not { On: true } || World.Pc is not RPlayerControllerCombat pc || !pc.IsInCombat(false))
            return;

        Brain.Attacks++;
        KindRateOf(foe)[0]++;
    }

    private double[] KindRateOf(string kind)
    {
        if (!Brain.KindRate.TryGetValue(kind, out double[]? r))
            Brain.KindRate[kind] = r = new double[2];
        return r;
    }

    /// <summary>Attacks per second one enemy of this kind starts, pulled toward the overall rate until there's data.</summary>
    private double RateOf(string kind) =>
        Brain.KindRate.TryGetValue(kind, out double[]? r) ? (r[0] + AttackRate * 5) / (r[1] + 5) : AttackRate;

    /// <summary>How soon an attack by this kind of enemy needs answering after it starts (shield bashes: at once).</summary>
    private double LeadOf(string kind) =>
        Brain.KindLead.TryGetValue(kind, out Stat? s) && s.N >= 2 ? Math.Max(0, s.Mean - 0.5 * s.Dev) : QuickLead();

    /// <summary>Kinds whose attacks are red (vault, not counter), so a held counter is no help against them.</summary>
    private static bool RedKind(string kind) => kind is "shield" or "stun stick";

    /// <summary>
    /// Every enemy near Batman who isn't attacking yet, as a hazard: how often their kind starts an attack, and how
    /// soon such an attack needs answering. A move landing in L seconds is cut off by one with probability
    /// 1 - exp(-rate x (L - lead - hold)).
    /// </summary>
    /// <summary>The chance an enemy of this kind close by attacks the moment Batman starts a move (prior 1 in 10).</summary>
    private double ReactP(string kind) => Brain.React.TryGetValue(kind, out Odds? o) ? (o.Ok + 1) / (o.N + 10) : 0.1;

    /// <summary>Kinds of enemy within ReactReach when Batman's last move started, and whether each kind answered it.</summary>
    private double _reactFrom;

    /// <summary>When a protected move last landed (0 when not watching): enemies held off during it pounce as it ends.</summary>
    private double _pounceFrom;

    private void WatchPounce() => _pounceFrom = GameNow;

    /// <summary>A defence was needed: if just after a protected move ended, that is a pounce.</summary>
    private void Pounced()
    {
        if (_pounceFrom > 0 && GameNow - _pounceFrom <= PounceWindow)
        {
            Brain.Pounce.Ok++;
            Brain.Dirty = true;
            _pounceFrom = 0;
        }
    }

    private void SettlePounce()
    {
        if (_pounceFrom > 0 && GameNow - _pounceFrom > PounceWindow)
        {
            Brain.Pounce.Bad++;
            Brain.Dirty = true;
            _pounceFrom = 0;
        }
    }

    /// <summary>The chance enemies pounce on a slow move started now, just after a protected move ended.</summary>
    private double PounceRisk() => _pounceFrom > 0 && GameNow - _pounceFrom <= PounceWindow ? Brain.Pounce.P : 0;
    private readonly Dictionary<string, int> _reactNear = new();
    private readonly HashSet<string> _reacted = new();

    /// <summary>Batman just started a move: note who was close enough to answer it.</summary>
    private void WatchReactions(List<Foe> foes)
    {
        SettleReactions();
        _reactFrom = GameNow;
        foreach (Foe f in foes)
        {
            if (!f.Down && f.Dist <= ReactReach)
                _reactNear[f.Kind] = _reactNear.GetValueOrDefault(f.Kind) + 1;
        }
    }

    /// <summary>An attack by this kind needed answering: within the reaction window, it counts as answering Batman's move.</summary>
    private void Reacted(string kind)
    {
        if (_reactFrom > 0 && GameNow - _reactFrom <= ReactWindow)
            _reacted.Add(kind);
    }

    /// <summary>Once the window has passed: each kind that was close either answered the move or didn't.</summary>
    private void SettleReactions(bool force = true)
    {
        if (_reactFrom <= 0 || !force && GameNow - _reactFrom <= ReactWindow)
            return;

        foreach ((string kind, int n) in _reactNear)
        {
            if (!Brain.React.TryGetValue(kind, out Odds? o))
                Brain.React[kind] = o = new Odds();
            // One answer per kind per move; the rest of that kind close by count as not answering.
            if (_reacted.Contains(kind))
            {
                o.Ok++;
                o.Bad += n - 1;
            }
            else
                o.Bad += n;
        }

        if (_reactNear.Count > 0)
            Brain.Dirty = true;
        _reactNear.Clear();
        _reacted.Clear();
        _reactFrom = 0;
    }

    /// <summary>
    /// The chance a move taking <paramref name="land"/> seconds is cut off by an enemy close by answering it: each one
    /// answers at his kind's learned rate. Ordinary attacks can wait for the hold budget; red ones (shields, stun sticks)
    /// can't, which is how a batarang 16 ms before a shield bash broke a combo.
    /// </summary>
    private double ReactCut(List<Foe> foes, RPawnPlayerCombat me, double land)
    {
        double none = 1, hold = HoldBudget;
        foreach (Foe f in foes)
        {
            if (f.Down || f.Dist > ReactReach)
                continue;
            if (f.V.CombatMove is RCombatMove_VillainSimultaneousAttack || f.V.CombatMove is RCombatMove_VillainAttack a && Aims(a, me))
                continue;
            if (land - (RedKind(f.Kind) ? 0 : hold) <= 0.05)
                continue;
            none *= 1 - ReactP(f.Kind);
        }

        return 1 - none;
    }

    // ---- the combat manager's attack rationing
    // From a recorded challenge: the manager lets one enemy attack Batman at a time (its count is 0 or 1, 2 only in a
    // group attack), and after an attack ends the next starts no sooner than about 0.7-1.0 s later (usually 2-3 s). So the
    // chance a new attack cuts a move off depends on how long the count has been 0, not on how many enemies stand around.

    private int _attackers;
    private double _attackersZeroSince;
    private double _lastTokenTick;

    /// <summary>Each fight tick: the attacker count, gaps between attacks, and time spent with an attack already running.</summary>
    private void TokenTick(RPlayerControllerCombat pc)
    {
        double now = GameNow;
        double dt = _lastTokenTick > 0 ? Math.Min(0.1, now - _lastTokenTick) : 0;
        _lastTokenTick = now;
        if (pc.CombatManager is not { IsValid: true } cm)
            return;

        int n = cm.GetNumAttackers();
        if (n > 0 && _attackers == 0 && _attackersZeroSince > 0)
        {
            Brain.Gaps.Add(now - _attackersZeroSince);
            if (Brain.Gaps.Count > GapKeep)
                Brain.Gaps.RemoveAt(0);
            Brain.Dirty = true;
        }
        else if (n == 0 && _attackers > 0)
            _attackersZeroSince = now;

        if (n > 0)
            Brain.BusySeconds += dt;
        _attackers = n;
    }

    /// <summary>A new attack began (Observe): while another was already running, it counts as an overlap.</summary>
    private void TokenAttackStarted()
    {
        if (_attackers > 0 && AutoFight is { On: true })
            Brain.Overlaps++;
    }

    /// <summary>
    /// The chance a new attack starts within <paramref name="within"/> seconds. With nobody attacking, from the learned
    /// gaps given how long the count has been 0 (falling back to the per-enemy rates until there are enough); with an
    /// attack already running, from how often a second one has joined in.
    /// </summary>
    private double NewAttackWithin(double within, double fallback)
    {
        if (within <= 0)
            return 0;
        if (_attackers > 0)
        {
            double rate = (Brain.Overlaps + 1) / (Brain.BusySeconds + 10);
            return 1 - Math.Exp(-rate * within);
        }

        if (Brain.Gaps.Count < 8 || _attackersZeroSince <= 0)
            return fallback;

        double since = GameNow - _attackersZeroSince;
        int past = 0, inside = 0;
        foreach (double g in Brain.Gaps)
        {
            if (g <= since)
                continue;
            past++;
            if (g <= since + within)
                inside++;
        }

        // Longer than (nearly) every gap seen: the gaps say little any more. Late in a round with a few enemies left far
        // away, treating that as "overdue, could come any moment" froze Batman until the combo timer ran out, twice. The
        // per-enemy rates, which scale with how many are left, take over.
        if (past == 0)
            return fallback;
        // A little smoothing so no gap is ever certain or impossible, blended toward the per-enemy rates while few gaps
        // are left to go on.
        double fromGaps = (inside + 0.25) / (past + 0.5);
        double w = Math.Min(1, past / 4.0);
        return w * fromGaps + (1 - w) * fallback;
    }

    /// <summary>For the summary: the shortest gaps seen, so the log shows how long Batman is safe after an attack.</summary>
    private string GapSummary()
    {
        if (Brain.Gaps.Count == 0)
            return "no gaps between attacks seen yet";
        var sorted = Brain.Gaps.OrderBy(g => g).ToList();
        double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
        return "after an attack the next starts " + At(0).ToString("0.00") + " s later at the soonest, " + At(0.1).ToString("0.00")
               + " s for the quickest tenth, " + At(0.5).ToString("0.00") + " s typically (" + sorted.Count + " seen); a second attack joins one already running "
               + ((Brain.Overlaps + 1) / (Brain.BusySeconds + 10)).ToString("0.00") + " times a second";
    }

    private List<(double Rate, double Lead, bool Red)> Hazards(RPawnPlayerCombat me, List<Foe> foes)
    {
        var list = new List<(double, double, bool)>();
        foreach (Foe f in foes)
        {
            // Enemies on the floor can't start an attack soon. Counting them made the ground takedown (which can't be
            // countered out of) look too risky, and two stunned thugs were left on the floor to the end of a round.
            if (f.Dist > RiskReach || f.Down)
                continue;
            if (f.V.CombatMove is RCombatMove_VillainSimultaneousAttack || f.V.CombatMove is RCombatMove_VillainAttack a && Aims(a, me))
                continue;
            list.Add((RateOf(f.Kind), LeadOf(f.Kind), RedKind(f.Kind)));
        }

        return list;
    }

    // ---- what Batman sees

    private sealed class Foe
    {
        public required RPawnVillain V;
        public float Dist;
        public required string Kind;
        public bool Special;
        public bool Hittable;
        public bool Downed;

        /// <summary>On the floor, ragdolled or not ready to fight: no threat for now, and a ground takedown target.</summary>
        public bool Down;
        public bool Ragdoll;
    }

    private static string KindOf(RPawnVillain v)
    {
        if (v.ChargedUpBy is { IsValid: true })
            return "electrified";
        if (v is RPawnVillainThugMedic)
            return "medic";
        if (v.CombatPawnType == RPawnCombat.ECombatPawnType.ECPT_Ninja)
            return "ninja";
        if (IsBrute(v))
            return "brute";
        if (v.bArmoured)
            return "armour";
        if (v.CurrWeapon is { IsValid: true } w)
        {
            switch (w.WeaponDestroyType)
            {
                case RPawnPlayerCombat.EWeaponDestroyType.WDT_Shield: return "shield";
                case RPawnPlayerCombat.EWeaponDestroyType.WDT_StunStick: return "stun stick";
                case RPawnPlayerCombat.EWeaponDestroyType.WDT_Gun: return "gunman";
                case RPawnPlayerCombat.EWeaponDestroyType.WDT_Knife: return "knife";
            }
        }

        return "thug";
    }

    private static double KindBonus(string kind) => kind switch
    {
        "medic" => 20, "gunman" => 15, "electrified" => 12, "stun stick" => 10, "shield" => 8, "brute" => 8, "ninja" => 8,
        "knife" => 5, _ => 0,
    };

    private List<Foe> Perceive(RPlayerControllerCombat pc, RPawnPlayerCombat me)
    {
        var foes = new List<Foe>();
        // With a combo to protect only enemies ready to fight count: a strike at one still entering the arena hung for
        // 6 s and lost a combo carried over between rounds.
        bool protect = pc.ComboSize > 0;
        foreach (RPawnVillain v in Watch(me))
        {
            if (!v.IsValid || v.bDeleteMe || v.bGoodAsDead || v.Health <= 0)
                continue;

            foes.Add(new Foe
            {
                V = v,
                Dist = Vector3.Distance(v.Location, me.Location),
                Kind = KindOf(v),
                Special = NeedsSpecial(me, v),
                Hittable = !protect || v.bIsValidCombatant && v.bCanBeHitInCombat && v.ReadyForCombat(),
                Downed = v.bCanBeTakenDownOnFloor && v.IsFloorStrikeable(),
                Ragdoll = v.IsRagdoll(),
            });
            Foe f = foes[^1];
            f.Down = f.Downed || f.Ragdoll || !v.ReadyForCombat();
        }

        return foes;
    }

    /// <summary>
    /// An attack under way that Batman will have to answer. Either its timing is known (Eta: seconds until it needs a
    /// counter or vault, from a charge's distance or an open window), or it is predicted from how long its class has
    /// taken before: a normal spread of lead times (Mu, Sigma), given that Since seconds have passed without it opening.
    /// A holdable attack is an ordinary counterable one, whose counter may wait up to the hold budget after it opens.
    /// </summary>
    private sealed class Threat
    {
        public required string What;
        public bool Holdable;
        public bool Known;
        public double Eta, Mu, Sigma, Since;

        /// <summary>A hold of its own (a charge's vault can wait while he runs from vault range to stop range), or -1.</summary>
        public double OwnHold = -1;

        /// <summary>The chance this attack needs answering within <paramref name="within"/> seconds.</summary>
        public double P(double within, double hold)
        {
            if (OwnHold >= 0)
                within -= Math.Min(hold, OwnHold);
            else if (Holdable)
                within -= hold;
            if (Known)
                return 1 / (1 + Math.Exp((Eta - within) / 0.08));
            if (within <= 0)
                return 0;

            double before = Phi((Since - Mu) / Sigma);
            double by = Phi((Since + within - Mu) / Sigma);
            return Math.Clamp((by - before) / Math.Max(1e-3, 1 - before), 0, 1);
        }

        /// <summary>Rough seconds until it needs answering, for the log.</summary>
        public double Guess => Known ? Math.Max(0, Eta) : Math.Max(0, Mu - Since);
    }

    /// <summary>Standard normal cumulative distribution (Abramowitz and Stegun 7.1.26 through erf).</summary>
    private static double Phi(double z)
    {
        double x = Math.Abs(z) / Math.Sqrt(2);
        double t = 1 / (1 + 0.3275911 * x);
        double erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    private List<Threat> Threats(RPawnPlayerCombat me, List<Foe> foes)
    {
        double now = GameNow;
        var list = new List<Threat>();
        foreach (Foe f in foes)
        {
            if (f.V.CombatMove is not { IsValid: true } move)
                continue;

            string what = f.Kind + " " + move.Class.Name;
            if (move is RCombatMove_VillainChargeAttack c)
            {
                // From the wind-up on, not only once bCharging is set: a charge that started 474 units away (inside vault
                // range) was ignored while it wound up, and the batarang thrown meanwhile was cut off by its vault (a combo
                // of 19). Running, its timing comes from the distance; winding up, from how long charges have taken before.
                if (!c.bStartedGrab && Aims(c, me))
                {
                    if (c.bCharging)
                        list.Add(new Threat { What = what, Known = true, Eta = Math.Max(0, (f.Dist - ChargeVault) / ChargeSpeed), OwnHold = ChargeHold });
                    else if (Predicted(move, move.Class.Name, what, false, now) is { } wind)
                    {
                        wind.OwnHold = ChargeHold;
                        list.Add(wind);
                    }
                }
            }
            else if (move is RCombatMove_VillainSimultaneousAttack g)
            {
                if (g.IsAttacking() || g.CanCounter())
                    list.Add(new Threat { What = what, Known = true, Eta = 0 });
                else if (Predicted(move, "group attack", what, false, now) is { } t)
                    list.Add(t);
            }
            else if (move is RCombatMove_VillainAttack a && Aims(a, me))
            {
                bool holdable = CanHold(a);
                if (a.bCanCounter || _seen.TryGetValue(move.Ptr, out Seen? s) && s.WindowOpened && now - s.At < 0.5)
                {
                    // Open now. A holdable one may still wait: until hold after it opened.
                    double openedAgo = _seen.TryGetValue(move.Ptr, out Seen? so) && so.OpenAt > 0 ? now - so.OpenAt : 0;
                    list.Add(new Threat { What = what, Known = true, Holdable = holdable, Eta = -openedAgo });
                }
                else if (Predicted(move, move.Class.Name, what, holdable, now) is { } t)
                    list.Add(t);
            }
        }

        return list;
    }

    private Threat? Predicted(RCombatMove move, string cls, string what, bool holdable, double now)
    {
        if (!_seen.TryGetValue(move.Ptr, out Seen? s) || now - s.At > 0.5)
            return new Threat { What = what, Holdable = holdable, Mu = LeadPrior, Sigma = 0.25, Since = 0 };

        // Swinging: its swing timing says more than its start.
        if (s.SwingFirst > 0)
        {
            Stat? swing = LeadStat(Brain.SwingLead, cls);
            return new Threat
            {
                What = what, Holdable = holdable, Mu = swing?.Mean ?? SwingLeadPrior, Sigma = Math.Max(0.08, (swing?.Dev ?? 0.1) * 1.25),
                Since = now - s.SwingFirst,
            };
        }

        Stat? lead = LeadStat(Brain.Lead, cls);
        double mu = lead?.Mean ?? LeadPrior, sigma = Math.Max(0.08, (lead?.Dev ?? 0.2) * 1.25), since = now - s.First;
        // Far past its usual time and not swinging: an enemy still walking in. It counts again once it swings.
        if (since > Math.Max(1.5, mu + 2.5 * sigma))
            return null;

        return new Threat { What = what, Holdable = holdable, Mu = mu, Sigma = sigma, Since = since };
    }

    /// <summary>The chance that some attack needs answering within <paramref name="within"/> seconds.</summary>
    private static double AnyWithin(List<Threat> threats, double within, double hold)
    {
        double none = 1;
        foreach (Threat t in threats)
            none *= 1 - t.P(within, hold);
        return 1 - none;
    }

    /// <summary>
    /// Ordinary counterable attacks (not red, not knife combos, not group attacks) whose counter can wait a moment for
    /// Batman's own move to land: a counter starting after it lands keeps the combo, one cutting it off breaks it.
    /// </summary>
    private static bool CanHold(RCombatMove_VillainAttack a) =>
        !IsRed(a) && a is not RCombatMove_VillainMultiAttackAndCounter && a is not RCombatMove_VillainChargeAttack;

    /// <summary>For the idle report: enemies up, down (and how), within reach.</summary>
    private string FoeCounts(RPlayerControllerCombat pc, RPawnPlayerCombat me)
    {
        List<Foe> foes = Perceive(pc, me);
        int up = foes.Count(f => !f.Down), floor = foes.Count(f => f.Downed), rag = foes.Count(f => f.Ragdoll);
        int notReady = foes.Count(f => !f.V.ReadyForCombat());
        string nearest = foes.Where(f => f.Down).OrderBy(f => f.Dist).FirstOrDefault() is { } d ? ", nearest down " + d.Dist.ToString("0") + " away" : "";
        return up + " enemies up, " + (foes.Count - up) + " down (" + floor + " on the floor, " + rag + " ragdolled, " + notReady + " not ready" + nearest + ")";
    }

    private static int VariationKinds(RPlayerControllerCombat pc)
    {
        int n = 0, max = Math.Min(pc.MoveTypeCount.Count, (int)CMT.ECMT_Max);
        for (int i = 0; i < max; i++)
        {
            if (pc.MoveTypeCount[i] > 0)
                n++;
        }

        return n;
    }

    private static bool InCombo(RPlayerControllerCombat pc, CMT t) =>
        (int)t < pc.MoveTypeCount.Count && pc.MoveTypeCount[(int)t] > 0;

    /// <summary>What breaking the combo now would lose: its size, and the move kinds already in it (the Variation bonus).</summary>
    // Uncapped, and growing faster than the combo: losing a combo of C costs the multiplier on every hit until it is built
    // back, which grows with C squared, while a single hit is worth C times its base. Capped at 400, a move's worth (which
    // grows with the combo) overtook the stake past ~60, and a beatdown with a 50% chance of being cut off lost a combo of 101.
    private static double Stake(RPlayerControllerCombat pc) =>
        pc.ComboSize <= 0 ? 0 : (10 + pc.ComboSize * 1.5 + VariationKinds(pc) * 20) * Math.Max(1, pc.ComboSize / 30.0);

    // ---- options

    private sealed class Option
    {
        public required MoveSpec Spec;
        public RPawnVillain? Target;
        public string Kind = "";

        /// <summary>The same gadget was fired less than AgainWithin ago: judged by how repeats have gone.</summary>
        public bool Again;

        /// <summary>A gadget at an enemy who took a gadget less than AgainWithin ago (stunned or frozen, maybe no target any more).</summary>
        public bool Gadgeted;

        /// <summary>The success chance Batman predicted when he chose this move, for calibration.</summary>
        public double Predicted;
        public float Dist;
        public required Func<bool> Do;
        public double Land, Works, Cut, Ev, Value;

        public string Describe() => Spec.Name + (Target is { IsValid: true } t ? " on " + Combat.Describe(t) : "")
                                    + " (EV " + Ev.ToString("0") + ", value " + Value.ToString("0") + ", works " + Works.ToString("0.00")
                                    + ", cut " + Cut.ToString("0.00") + ", lands in " + Land.ToString("0.00") + " s)";
    }

    private List<Option> Options(RPlayerControllerCombat pc, RPawnPlayerCombat me, List<Foe> foes)
    {
        var list = new List<Option>();
        double now = GameNow;
        bool canSpecial = CanStartSpecial(pc, me) && me.CombatMove is not (RCombatMove_BatmanComboTakedown or RCombatMove_BatmanWeaponDestroy);
        bool emptyHanded = me.GetWeaponGrabbed(false) is not { IsValid: true } && me.GetWeaponGrabbed(true) is not { IsValid: true };
        int standingNear = foes.Count(f => f.Dist <= RiskReach && !f.Down);
        Foe? nearest = null;

        Option? Add(MoveSpec s, Foe? f, Func<bool> doIt)
        {
            if (s.Cooldown > 0 && !Ready(s.Name))
                return null;
            if (!Ready(s.Name + " refused"))
                return null;
            var o = new Option { Spec = s, Target = f?.V, Kind = f?.Kind ?? "", Dist = f?.Dist ?? 0, Do = doIt };
            list.Add(o);
            return o;
        }

        // (Downed is only used to aim the ground takedown: the floor flags it reads aren't proven in game, and leaving
        // possibly-downed enemies out of everything else could leave Batman with no target at all.)
        foreach (Foe f in foes)
        {
            if (f.Dist > StrikeReach || !f.Hittable)
                continue;

            RPawnVillain v = f.V;
            if (f.Special)
            {
                // Tough enemies get a special or nothing: a strike only gets blocked and resets the combo.
                bool putAside = _specialFailed.TryGetValue(v.Ptr, out double at) && now - at < SpecialRetry;
                if (putAside || f.Dist > SpecialReach || !canSpecial || !Ready("special"))
                    continue;

                // An electrified brute shocked Batman in the middle of a takedown ("Damaged", RDmgType_Electricity), so he gets
                // nothing but defence until the charge is gone.
                if (f.Kind == "electrified" && IsBrute(v))
                    continue;

                if (f.Kind == "electrified")
                {
                    // A takedown at most every ElectroTakedownGap: the medic keeps making more of them.
                    if (Ready("electro takedown") && pc.CanDoCombatTakedown(true) && v.bCanBeTakenDown)
                    {
                        Add(MTakedown, f, () => SpecialOn(pc, me, v, false));
                        Add(MEnvTakedown, f, () => SpecialOn(pc, me, v, true));
                    }

                    continue;
                }

                if (BreakableWeapon(v) && pc.CanDoWeaponDestroy(true))
                    Add(MDestroy, f, () => DestroyOn(pc, me, v));
                if (pc.CanDoCombatTakedown(true) && v.bCanBeTakenDown)
                {
                    Add(MTakedown, f, () => SpecialOn(pc, me, v, false));
                    Add(MEnvTakedown, f, () => SpecialOn(pc, me, v, true));
                }

                continue;
            }

            // Not at an enemy still flying from a counter: a strike straight after a group counter "Exited strike without
            // hitting" and broke a combo of 40. (Enemies on the floor get ground takedowns instead.)
            if (f.Ragdoll)
                continue;

            if (nearest == null || f.Dist < nearest.Dist)
                nearest = f;

            Add(MStrike, f, () => me.TryStrike(0f, v));

            // Beatdown: a quick cape stun, then strikes while he's stunned (the follow-up is in FightTick). Not on the last
            // one standing, not on a thug already stunned, and not straight after a gadget (a REC had just stunned the
            // thug and the cape stun found "No target for strike").
            // Nor on a thug Batman hit in the last moment (a counter, a strike, a gadget): still reeling, the cape stun
            // finds "No target for strike" (a beatdown 0.02 s after a counter on the same thug broke a combo of 7). Only that
            // thug: a 1.5 s ban on every beatdown after any gadget left Batman idle.
            if (f.Dist <= BeatdownReach && v.bCanBeatdown && v.ReadyForCombat() && !v.IsStunned() && standingNear >= 2
                && !RecentlyHit(v, now))
                Add(MBeatdown, f, () => StartBeatdown(me, v));

            if (emptyHanded && f.Dist <= CloseReach && v.CurrWeapon is { IsValid: true } w
                && w.WeaponDestroyType is RPawnPlayerCombat.EWeaponDestroyType.WDT_Bat or RPawnPlayerCombat.EWeaponDestroyType.WDT_Pipe)
                Add(MSnatch, f, () => Snatch(pc, me, v, null));

            // Gadgets share one cooldown: thrown back to back only one really flies.
            // Not at a ragdolling enemy: a batarang at a thug the multi special had just floored found "Multi brang no targets".
            if (Ready("gadget") && f.Dist <= GadgetReach && canSpecial && !f.Ragdoll)
            {
                foreach (MoveSpec g in GadgetMoves)
                {
                    (int slot, CMT super) = GadgetInfo[g.Name];
                    if (Gadget(me, slot) is { IsValid: true } gadget && gadget.CanQuickFire())
                    {
                        MoveSpec gs = g;
                        if (Add(gs, f, () => QuickGadget(pc, me, slot, v, super)) is { } go)
                        {
                            go.Again = _gadgetFiredAt.TryGetValue(gs.Name, out double fired) && now - fired < AgainWithin;
                            go.Gadgeted = _gadgetHitAt.TryGetValue(v.Ptr, out double hit) && now - hit < AgainWithin;
                        }
                    }
                }
            }
        }

        if (nearest != null)
        {
            // These pick their own target (the game's stick-based search), so they're scored against the nearest enemy.
            Add(MAerial, nearest, () => pc.TryAirStrike(false));
            // (No last-moment evade: tried in game, it never reopened the combo window, and a combo of 104 broke on it.)
            // No multi special (the multi ground takedown): it counts as landed in the game's tally, but in game it often
            // had no effect on anyone, and a batarang straight after it twice found "Multi brang no targets".
            if (nearest.Dist <= 300f && emptyHanded)
                Add(MThrow, nearest, pc.TryThrow);
            if (nearest.Dist <= CloseReach && pc.CombatManager is { IsValid: true } cm && cm.CurrTakedownObject is { IsValid: true } env)
                Add(MEnvAttack, nearest, () => Spawn(pc, me, () => me.SpawnEnvironmentAttack(env)));
            if (emptyHanded && FloorWeapon(pc, me, nearest.V) is { } pickup)
                Add(MPickup, nearest, () => Snatch(pc, me, null, pickup));
        }

        // Enemies on the floor get a ground takedown. Batman can't counter out of one (a hit landed during one), so it is
        // exposed: the model only picks it when no attack can arrive before it lands.
        // Only enemies really on the floor: one merely "not ready" got a ground takedown that whiffed and broke a combo of 12.
        if (foes.Where(f => f.Downed && f.Dist <= GroundReach && !(_specialFailed.TryGetValue(f.V.Ptr, out double gf) && GameNow - gf < SpecialRetry))
                .OrderBy(f => f.Dist).FirstOrDefault() is { } down)
            Add(MGround, down, () => pc.TriggerGroundTakedown(false));
        // (No target-less ground takedown: with nobody on the floor it played the fail move, "Batman fail move timer",
        // and broke a combo of 8.)

        return list;
    }

    private bool SpecialOn(RPlayerControllerCombat pc, RPawnPlayerCombat me, RPawnVillain v, bool environment)
    {
        nint before = MovePtr(me);
        bool electrified = v.ChargedUpBy is { IsValid: true };
        Takedown(pc, me, v, environment);
        bool started = MovePtr(me) != before;
        Cool("special", SpecialGap);
        if (electrified)
            Cool("electro takedown", ElectroTakedownGap);
        if (!started)
            Fail(v, (environment ? "environment takedown" : "special takedown") + " didn't start");
        return started;
    }

    private bool DestroyOn(RPlayerControllerCombat pc, RPawnPlayerCombat me, RPawnVillain v)
    {
        nint before = MovePtr(me);
        pc.SpawnWeaponDestroy(false, v);
        bool started = MovePtr(me) != before;
        Cool("special", SpecialGap);
        if (!started)
            Fail(v, "weapon destroy didn't start");
        return started;
    }

    private void Evaluate(Option o, RPlayerControllerCombat pc, double stake, List<Threat> threats, List<(double Rate, double Lead, bool Red)> hazards)
    {
        MoveSpec s = o.Spec;
        o.Land = LandTime(s, o.Dist);
        o.Works = WorksP(s, o.Kind);
        // The same gadget again soon has its own record: the game resets the combo for some repeats ("Double REC" 0.53 s
        // after a REC, "Brang clearing" 0.53 s after a batarang, four times in one challenge) while the gadget's overall
        // rate stayed at 95%.
        if (o.Again)
            o.Works = Math.Min(o.Works, AgainOdds(s.Name).P);
        // An enemy a gadget just stunned or froze may not be a target any more: REC, batarang and freeze thrown 0.5 s apart
        // at the same thugs broke combos with "Multi brang no targets" and "REC no target".
        if (o.Gadgeted)
            o.Works = Math.Min(o.Works, AgainOdds("gadgeted target").P);

        if (s.Protected)
            o.Cut = 1 - CutOdds(s).P;
        else
        {
            // An attack already under way that needs answering before this lands cuts it off; an ordinary counter can
            // wait up to the hold budget for it to land.
            double hold = HoldBudget;
            double fromKnown = AnyWithin(threats, o.Land + 0.05, hold);
            // A new attack can start any moment, from each enemy at his kind's rate, and needs his kind's lead before it
            // must be answered. (A shield bash needs a vault the moment it is seen: a REC was cut off that way.)
            double hazard = 0;
            double soonest = double.PositiveInfinity;
            foreach ((double rate, double lead, bool red) in hazards)
            {
                hazard += rate * Math.Max(0, o.Land - lead - (red ? 0 : hold));
                soonest = Math.Min(soonest, lead + (red ? 0 : hold));
            }
            double perEnemy = 1 - Math.Exp(-hazard);
            // The manager rations attacks, so the real question is whether it lets a new one start soon enough to need
            // answering before this lands: the quickest kind nearby needs its lead (plus any hold) after starting.
            double fromNew = hazards.Count == 0 ? 0 : NewAttackWithin(o.Land - (double.IsInfinity(soonest) ? 0 : soonest), perEnemy);
            double fromReact = _thinkFoes != null && _thinkMe != null ? ReactCut(_thinkFoes, _thinkMe, o.Land) : 0;
            // Just after a takedown or multi special, enemies who held off pounce: four gadgets thrown in that moment were
            // all cut off by a counter or vault within 0.3 s.
            double fromPounce = PounceRisk();
            o.Cut = 1 - (1 - fromKnown) * (1 - fromNew) * (1 - fromReact) * (1 - fromPounce);
        }

        CMT type = Brain.Types.TryGetValue(s.Name, out int learned) ? (CMT)learned : s.Type;
        // Not new if one was fired moments ago and the game hasn't counted it yet: a second freeze blast 0.53 s after the
        // first still looked new (worth 97) and broke the combo with "Double gadget".
        bool novel = !InCombo(pc, type) && !(_movedAt.TryGetValue(s.Name, out double movedAt) && GameNow - movedAt < AgainWithin);
        // A gadget already in the combo adds no variety, and a strike does as much for the combo: worth half. (A REC worth
        // 12-16 was picked again and again at a high combo, just for being quick.)
        // In a challenge every hit scores its base points times the current combo (AddScoreForMove), so a move's worth grows
        // with the combo just as the stake does. With a fixed worth against a growing stake, any risk above ~4% looked
        // worse than waiting at a combo of 30, and Batman stood between counters for 42 s of one challenge.
        double tempo = Math.Max(1, pc.ComboSize / 5.0);
        double value = (s.Gadget && !novel ? s.Value * 0.5 : s.Value) * tempo + KindBonus(o.Kind) - o.Dist / 250.0;
        if (novel)
            value += NoveltyValue + (s.Gadget ? GadgetNoveltyValue : 0);

        // Little at stake: try what has been tried least, so the model learns.
        if (stake < 20)
            value += 15 / Math.Sqrt(1 + Math.Max(0, WorksOdds(s.Name, s).N - s.PriorOk - s.PriorBad));
        o.Value = value;

        double p = Math.Min(0.99, o.Works * (1 - o.Cut) * CalibFactor(s.Name));
        o.Predicted = p;
        o.Ev = p * (value + stake) - stake - (s.Exposed ? o.Cut * HitCost : 0);

        // Experiments that are expected to fail only happen with nothing much to lose.
        if (s.Experiment && stake >= 20)
            o.Ev = double.NegativeInfinity;
    }

    // ---- deciding

    private sealed class Pending
    {
        public required Option O;
        public double At;
        public bool Done;

        /// <summary>For a gadget: the game scored its hit (MovePerformed with its type), so it really found a target.</summary>
        public bool Hit;
    }

    /// <summary>
    /// Gadgets whose throw has finished (the combo window reopened) but whose hit the game hasn't scored yet, and when the
    /// window reopened. A REC whose hit was never scored reset the combo 10 s later with "REC no target", while Oracle had
    /// counted it as landed (the window reopens about 0.35 s after every quick gadget, hit or miss), so success rates for
    /// gadgets are settled here, by whether the hit is scored within HitGrace.
    /// </summary>
    private readonly List<(Option O, double At)> _awaitHit = new();

    private const double HitGrace = 1.0;

    private Pending? _pending;

    private const string UrgentWait = "last-moment wait";

    /// <summary>
    /// A last-moment wait under way: when it began and the chance Batman gave a counter or vault of arriving in time. It
    /// is settled by the combo window reopening (the wait worked) or the combo breaking (it didn't).
    /// </summary>
    private double _urgentWaitFrom, _urgentWaitPredicted;

    private void SettleUrgentWait(bool kept)
    {
        if (_urgentWaitFrom == 0)
            return;
        _urgentWaitFrom = 0;
        if (_urgentWaitPredicted <= 0)
            return;
        if (!Brain.Calib.TryGetValue(UrgentWait, out double[]? c))
            Brain.Calib[UrgentWait] = c = new double[2];
        c[0] = c[0] * 0.98 + _urgentWaitPredicted;
        c[1] = c[1] * 0.98 + (kept ? 1 : 0);
        Brain.Dirty = true;
    }


    private static readonly FName ComboTimerName = new("ContinueComboTimerComplete");

    /// <summary>
    /// Seconds left on the combo window, read from the game's own timer (ContinueComboTimerComplete, whose end is "Combo
    /// timer ran out"); infinite with no combo or no timer running. The game's timer, not a copy kept from
    /// SetContinueComboTimer: the timer is also set in ways that call doesn't show, and a wrong copy let combos run out.
    /// </summary>
    private double WindowLeft(RPlayerControllerCombat pc)
    {
        if (pc.ComboSize <= 0 || !pc.IsTimerActive(ComboTimerName, pc))
            return double.PositiveInfinity;

        return pc.GetRemainingTimeForTimer(ComboTimerName, pc);
    }

    /// <summary>Counts fights, so once-per-fight log lines can be keyed by fight.</summary>
    private int _fightNumber;

    /// <summary>When each kind of move was last made, and when each enemy last had a gadget thrown at them.</summary>
    private readonly Dictionary<string, double> _movedAt = new();
    private readonly Dictionary<nint, double> _gadgetHitAt = new();

    /// <summary>When each gadget was last fired by the brain, for the "same gadget again soon" situation.</summary>
    private readonly Dictionary<string, double> _gadgetFiredAt = new();

    /// <summary>How repeats of a gadget within AgainWithin have gone (prior: 1 in 3 works). Kept as "name|again".</summary>
    private Odds AgainOdds(string gadget)
    {
        if (!Brain.Works.TryGetValue(gadget + "|again", out Odds? o))
            Brain.Works[gadget + "|again"] = o = new Odds { Ok = 1, Bad = 2 };
        return o;
    }
    private List<Foe>? _thinkFoes;
    private RPawnPlayerCombat? _thinkMe;

    /// <summary>True while Oracle's own counter or vault call runs, so a combo reset inside it is booked as a cut.</summary>
    private bool _defending;

    private int _heldCounters;
    private string _thought = "nothing yet";
    private double _lastThinkAt;

    /// <summary>Per move this fight: tries, landed, cut by a defence, failed (with why), total land time.</summary>
    private sealed class Tally
    {
        public int Tries, Landed, Cut, Failed;
        public double LandSum;
        public readonly Dictionary<string, int> Why = new();
        public readonly HashSet<string> Types = new();
    }

    private readonly Dictionary<string, Tally> _tally = new();

    private Tally TallyOf(string name)
    {
        if (!_tally.TryGetValue(name, out Tally? t))
            _tally[name] = t = new Tally();
        return t;
    }

    /// <summary>Enemy-seconds of exposure for the attack rate. Runs every fight tick, whatever Batman is doing.</summary>
    private void CountExposure(RPawnPlayerCombat me)
    {
        double now = GameNow;
        double dt = Math.Min(0.1, now - _lastThinkAt);
        _lastThinkAt = now;
        if (dt <= 0)
            return;

        int near = 0;
        foreach (RPawnVillain v in Watch(me))
        {
            if (v.IsValid && !v.bDeleteMe && !v.bGoodAsDead && v.Health > 0
                && Vector3.DistanceSquared(v.Location, me.Location) <= RiskReach * RiskReach)
            {
                near++;
                KindRateOf(KindOf(v))[1] += dt;
            }
        }

        Brain.EnemySeconds += dt * near;

        // Flow: time spent in no move at all, and stretches of 0.3 s or more (what looks like standing around).
        if (me.CombatMove is { IsValid: true })
        {
            if (_stillFor >= 0.3)
                _stillStretches++;
            _stillFor = 0;
        }
        else
        {
            _stillFor += dt;
            _stillTotal += dt;
        }
    }

    private double _stillFor, _stillTotal;
    private int _stillStretches;

    /// <summary>Picks and makes Batman's next move, or decides to wait. Called only when a new move can start.</summary>
    private void Think(RPlayerControllerCombat pc, RPawnPlayerCombat me)
    {
        double now = GameNow;
        List<Foe> foes = Perceive(pc, me);
        int standingNear = foes.Count(f => f.Dist <= RiskReach);

        // The last move still on its way: let it land before the next, even with no combo yet (a strike 0.25 s after a
        // freeze blast left the freeze never landing, which also taught the model that freezes fail).
        // Never past the combo window's last moment, though.
        double windowLeft = WindowLeft(pc);
        if (_pending is { Done: false } p && now - p.At < LandTime(p.O.Spec, p.O.Dist) + 0.15 && windowLeft >= LastResort)
        {
            _thought = "waiting for " + p.O.Spec.Name + " to land";
            return;
        }

        List<Threat> threats = Threats(me, foes);
        List<(double Rate, double Lead, bool Red)> hazards = Hazards(me, foes);
        _thinkFoes = foes;
        _thinkMe = me;
        Threat? soonest = threats.OrderBy(t => t.Guess).FirstOrDefault();
        string threat = soonest == null ? "no attack under way" : "next attack " + soonest.What + " in about " + soonest.Guess.ToString("0.00") + " s";
        double stake = Stake(pc);
        double left = windowLeft;
        bool urgent = left < LastResort;

        // A counter or vault that starts while the window is open continues the combo, so with one coming in time,
        // waiting is safe even at the last moment. A move of Batman's would shut the window first and the defence
        // starting after it would break the combo (a strike 0.016 s before a vault did exactly that).
        // (A blanket "an attack is swinging, wait for its counter" rule used to stand here. An attack that looked like it was
        // swinging but never needed answering let a combo of 6 run out of time; the sums below already weigh how likely a
        // counter is to come in time.)

        List<Option> options = Options(pc, me, foes);
        Option? best = null;
        foreach (Option o in options)
        {
            Evaluate(o, pc, stake, threats, hazards);
            if (best == null || o.Ev > best.Ev)
                best = o;
        }

        // Waiting at the last moment loses the combo, unless a counter or vault starts before the window shuts and
        // refreshes it. (A REC thrown with 0.34 s left and a counter due in 0.1 s was cut off and broke a combo of 15.)
        // Corrected by how last-moment waits have really gone: a batarang chosen over waiting at 0.23 s left was cut off by a
        // vault 16 ms later, which would have kept the combo of 293 going had he waited.
        double defenceInTime = urgent ? Math.Min(0.99, AnyWithin(threats, left - 0.05, 0) * CalibFactor(UrgentWait)) : 0;
        // Outside the last moment a move has to beat waiting by a margin that grows with the stake: a batarang worth 12
        // with 210 at stake, judged a near-certain success, still ended in a "Brang clearing" break.
        double waitEv = urgent ? -stake * (1 - defenceInTime) : StakeMargin * stake;
        if (best == null || best.Ev <= waitEv || double.IsNegativeInfinity(best.Ev))
        {
            if (urgent && pc.ComboSize > 0 && _urgentWaitFrom == 0)
            {
                _urgentWaitFrom = GameNow;
                _urgentWaitPredicted = defenceInTime;
            }

            _thought = "waiting: " + (best == null ? "no move available" : "best was " + best.Describe())
                       + ", stake " + stake.ToString("0") + (urgent ? ", window " + left.ToString("0.00") + " s, counter in time " + defenceInTime.ToString("0.00") : "")
                       + ", " + threat + ", " + threats.Count + " attacks under way, " + options.Count + " options";
            return;
        }

        // Near ties: a little randomness keeps the moveset rotating.
        Option pick = best;
        var close = options.Where(o => o != best && !double.IsNegativeInfinity(o.Ev) && o.Ev > waitEv && best.Ev - o.Ev < 3).ToList();
        if (close.Count > 0 && Rng.NextDouble() < 0.5)
            pick = close[Rng.Next(close.Count)];

        Execute(me, pick, stake, urgent ? left : -1);
    }

    private void Execute(RPawnPlayerCombat me, Option o, double stake, double urgentLeft)
    {
        ResolvePending(false, "never landed");

        nint before = MovePtr(me);
        bool did = o.Do();
        if (!did && MovePtr(me) == before)
        {
            // Refused: nothing started, nothing at risk. Rest it briefly so the next best gets a turn.
            Cool(o.Spec.Name + " refused", 0.4);
            if (urgentLeft >= 0 && _reported.Add("urgent refused " + o.Spec.Name + " " + _fightNumber))
                Trace("the game refused " + o.Describe() + " with " + urgentLeft.ToString("0.00") + " s of combo window left.");
            _thought = "the game refused " + o.Describe();
            return;
        }

        if (o.Spec.Cooldown > 0)
            Cool(o.Spec.Name, o.Spec.Cooldown);
        _urgentWaitFrom = 0;
        _pending = new Pending { O = o, At = GameNow };
        _movedAt[o.Spec.Name] = GameNow;
        if (o.Spec.Gadget)
        {
            _gadgetFiredAt[o.Spec.Name] = GameNow;
            if (o.Target is { IsValid: true } gt)
                _gadgetHitAt[gt.Ptr] = GameNow;
        }
        if (_thinkFoes != null)
            WatchReactions(_thinkFoes);
        TallyOf(o.Spec.Name).Tries++;
        Used(o.Spec.Name);
        _thought = "made " + o.Describe();
        if (o.Spec != MStrike || urgentLeft >= 0)
            Trace(o.Describe() + ", stake " + stake.ToString("0")
                  + (urgentLeft >= 0 ? ", with " + urgentLeft.ToString("0.00") + " s of combo window left" : "") + ".");
    }

    /// <summary>The pending move's outcome. Landed teaches its timing; failures teach how often it works.</summary>
    private void ResolvePending(bool landed, string why, bool cut = false)
    {
        if (_pending is not { Done: false } p)
            return;

        p.Done = true;
        _pending = null;
        MoveSpec s = p.O.Spec;
        Tally t = TallyOf(s.Name);
        double took = GameNow - p.At;
        Brain.Dirty = true;

        if (cut)
        {
            Calibrate(p.O, false);
            t.Cut++;
            if (s.Protected)
                CutOdds(s).Bad++;
            return;
        }

        // A gadget whose hit hasn't been scored yet: its timing counts now, its success once the hit is scored or not.
        bool deferWorks = landed && s.Gadget && !p.Hit;
        if (deferWorks)
            _awaitHit.Add((p.O, GameNow));
        else
            CountWorks(p.O, landed);

        if (landed)
        {
            t.Landed++;
            t.LandSum += took;
            if (s.Protected)
            {
                CutOdds(s).Ok++;
                WatchPounce();
            }
            if (took > 0.05)
            {
                string key = s.Name + "/" + Bucket(p.O.Dist);
                if (!Brain.Land.TryGetValue(key, out Stat? st))
                    Brain.Land[key] = st = new Stat();
                st.Add(took);
            }
        }
        else
        {
            t.Failed++;
            t.Why[why] = t.Why.GetValueOrDefault(why) + 1;
        }
    }

    /// <summary>How much a move's predicted success chance needs correcting: what really worked over what was predicted.</summary>
    private double CalibFactor(string move)
    {
        if (!Brain.Calib.TryGetValue(move, out double[]? c))
            return 1;
        return Math.Clamp((c[1] + 3) / (c[0] + 3), 0.5, 1.5);
    }

    private void Calibrate(Option o, bool ok)
    {
        if (o.Predicted <= 0)
            return;
        if (!Brain.Calib.TryGetValue(o.Spec.Name, out double[]? c))
            Brain.Calib[o.Spec.Name] = c = new double[2];
        // Recent fights count most.
        c[0] = c[0] * 0.98 + o.Predicted;
        c[1] = c[1] * 0.98 + (ok ? 1 : 0);
        Brain.Dirty = true;
    }

    /// <summary>Whether a move worked: its general rate, its rate against this kind of enemy, and its situation rates.</summary>
    private void CountWorks(Option o, bool ok)
    {
        Calibrate(o, ok);
        MoveSpec s = o.Spec;
        Odds all = WorksOdds(s.Name, s);
        Odds kind = WorksOdds(s.Name + "|" + o.Kind, s with { PriorOk = 0, PriorBad = 0 });
        var odds = new List<Odds> { all, kind };
        if (o.Again)
            odds.Add(AgainOdds(s.Name));
        if (o.Gadgeted)
            odds.Add(AgainOdds("gadgeted target"));
        foreach (Odds x in odds)
        {
            if (ok)
                x.Ok++;
            else
                x.Bad++;
        }

        Brain.Dirty = true;
    }

    /// <summary>A scored hit settles the oldest gadget of that kind still waiting for one.</summary>
    private void GadgetHitScored(string gadget)
    {
        int i = _awaitHit.FindIndex(a => a.O.Spec.Name == gadget);
        if (i < 0)
            return;
        CountWorks(_awaitHit[i].O, true);
        _awaitHit.RemoveAt(i);
    }

    /// <summary>Gadgets whose hit was never scored: they missed, whatever the combo window did.</summary>
    private void SettleGadgetHits()
    {
        double now = GameNow;
        for (int i = _awaitHit.Count - 1; i >= 0; i--)
        {
            if (now - _awaitHit[i].At <= HitGrace)
                continue;
            (Option o, _) = _awaitHit[i];
            CountWorks(o, false);
            Tally t = TallyOf(o.Spec.Name);
            t.Why["no hit scored"] = t.Why.GetValueOrDefault("no hit scored") + 1;
            _awaitHit.RemoveAt(i);
        }
    }

    /// <summary>The gadget a combo move type belongs to (its own hit or its gadget takedown), or null.</summary>
    private static string? GadgetOf(CMT type)
    {
        foreach (MoveSpec g in GadgetMoves)
        {
            if (g.Type == type || GadgetInfo[g.Name].Super == type)
                return g.Name;
        }

        return null;
    }

    // ---- hooks from the game's combo events and the defence code

    /// <summary>SetContinueComboTimer: the last move landed and the combo window reopened.</summary>
    private void BrainLanded()
    {
        SettleUrgentWait(true);
        if (_pending is { Done: false })
            ResolvePending(true, "");
    }

    /// <summary>
    /// ClearCombo: the pending move broke the combo, unless a defence cut in first (already resolved then). With no combo
    /// running only resets that name a failed move count, in case the game clears an empty combo for other reasons.
    /// </summary>
    private void BrainBroken(string reason, int combo)
    {
        if (combo > 0 && !BankingReasons.Contains(reason))
            SettleUrgentWait(false);
        bool moveFailed = reason.Contains("strike", StringComparison.OrdinalIgnoreCase) || reason.Contains("target", StringComparison.OrdinalIgnoreCase)
                          || reason.Contains("blocker", StringComparison.OrdinalIgnoreCase) || reason.Contains("miss", StringComparison.OrdinalIgnoreCase)
                          || reason.Contains("fail", StringComparison.OrdinalIgnoreCase);
        // A reset from inside a counter or vault is the defence cutting the move off (ClearCombo runs before the
        // counter call returns, so this is the only place to tell).
        if (_pending is { Done: false } && _defending && !BankingReasons.Contains(reason))
            ResolvePending(false, "", cut: true);
        else if (_pending is { Done: false } && !BankingReasons.Contains(reason) && (combo > 0 || moveFailed))
            ResolvePending(false, reason.Split(' ').Length > 4 ? string.Join(' ', reason.Split(' ').Take(4)) : reason);
    }

    /// <summary>
    /// Whether a counter whose window is open should wait: only during AUTO FIGHT, for an ordinary counterable attack,
    /// while Batman's own (unprotected) move is still on its way with a combo to keep, and only up to the hold budget.
    /// </summary>
    private bool HoldCounter(RCombatMove_VillainAttack a, Seen s)
    {
        if (AutoFight is not { On: true } || !CanHold(a) || _pending is not { Done: false } p || p.O.Spec.Protected)
            return false;
        if (World.Pc is not RPlayerControllerCombat pc || pc.ComboSize <= 0 || pc.CanContinueCombo())
            return false;
        if (GameNow - s.OpenAt >= HoldBudget)
            return false;

        if (!s.Held)
        {
            s.Held = true;
            _heldCounters++;
            Report("counter hold", "Auto fight: holding a counter for Batman's own move to land first (up to " + HoldBudget.ToString("0.00") + " s).");
        }

        return true;
    }

    /// <summary>Batman's own unprotected move is still on its way with a combo to keep, so a vault may wait a moment.</summary>
    private bool HoldForOwnMove() =>
        AutoFight is { On: true } && _pending is { Done: false } p && !p.O.Spec.Protected
        && World.Pc is RPlayerControllerCombat { ComboSize: > 0 } pc && !pc.CanContinueCombo();

    /// <summary>A held counter still went in fine: the budget creeps up.</summary>
    private void HeldCounterLanded()
    {
        Brain.Hold = Math.Min(HoldMax, Brain.Hold + 0.02);
        Brain.Dirty = true;
    }

    /// <summary>Hit by an attack whose counter was held: the hold was too long, so the budget halves.</summary>
    private void HeldCounterHit()
    {
        Brain.Hold *= 0.5;
        Brain.Dirty = true;
        Log.Info("Auto fight: hit while holding a counter, so counters now wait at most " + HoldBudget.ToString("0.00") + " s.");
    }

    /// <summary>A counter or vault started: a move still on its way is cut off by it.</summary>
    private void BrainDefended(string? foe = null)
    {
        Pounced();
        if (foe != null)
            Reacted(foe);
        if (_pending is { Done: false } p && GameNow - p.At > 0.02)
            ResolvePending(false, "", cut: true);
    }

    private void BrainHit()
    {
        if (_pending is { Done: false } p)
        {
            Tally t = TallyOf(p.O.Spec.Name);
            t.Why["hit during it"] = t.Why.GetValueOrDefault("hit during it") + 1;
            ResolvePending(false, "", cut: true);
        }
    }

    /// <summary>Enemies by pawn and when one of Batman's moves last hit them (from MovePerformed).</summary>
    private readonly Dictionary<nint, double> _lastHitAt = new();

    private const double ReelTime = 1.2;

    private bool RecentlyHit(RPawnVillain v, double now) => _lastHitAt.TryGetValue(v.Ptr, out double at) && now - at < ReelTime;

    /// <summary>MovePerformed: which combo move type the pending move scored as, so novelty is judged by the game's own types.</summary>
    private void BrainMoveType(CMT type, RPawnCombat? target)
    {
        if (target is { IsValid: true })
        {
            double now = GameNow;
            _lastHitAt[target.Ptr] = now;
            if (_lastHitAt.Count > 64)
            {
                foreach (nint old in _lastHitAt.Where(kv => now - kv.Value > ReelTime).Select(kv => kv.Key).ToList())
                    _lastHitAt.Remove(old);
            }
        }

        if (GadgetOf(type) is { } gadget)
        {
            if (_pending is { Done: false } gp && gp.O.Spec.Name == gadget)
                gp.Hit = true;
            else
                GadgetHitScored(gadget);
        }

        if (_pending is not { Done: false } p || DefenceTypes.Contains(type) || p.O.Spec == MStrike)
            return;

        TallyOf(p.O.Spec.Name).Types.Add(type.ToString().Replace("ECMT_", ""));
        if (!Brain.Types.ContainsKey(p.O.Spec.Name))
        {
            Brain.Types[p.O.Spec.Name] = (int)type;
            Brain.Dirty = true;
        }
    }

    /// <summary>A move that never resolved: gadgets that miss leave the combo hanging, takedowns take a few seconds.</summary>
    private void ExpirePending()
    {
        SettleGadgetHits();
        if (_pending is { Done: false } p && GameNow - p.At > (p.O.Spec.Protected ? ProtectedLife : PendingLife))
            ResolvePending(false, "never landed");
    }

    /// <summary>End of fight: what each move did, what the model now believes, and the model saved.</summary>
    private void BrainSummary()
    {
        _fightNumber++;
        ResolvePending(false, "fight ended");
        SettleReactions();
        foreach ((Option o, double _) in _awaitHit)
            CountWorks(o, false);
        _awaitHit.Clear();
        if (_tally.Count > 0)
        {
            string moves = string.Join("; ", _tally.OrderByDescending(kv => kv.Value.Tries).Select(kv =>
            {
                Tally t = kv.Value;
                string s = kv.Key + " " + t.Tries + " (landed " + t.Landed;
                if (t.Landed > 0)
                    s += " in " + (t.LandSum / t.Landed).ToString("0.00") + " s";
                if (t.Cut > 0)
                    s += ", cut " + t.Cut;
                if (t.Failed > 0)
                    s += ", failed " + t.Failed;
                if (t.Why.Count > 0)
                    s += ": " + string.Join(", ", t.Why.Select(w => w.Key + " x" + w.Value));
                if (t.Types.Count > 0)
                    s += ", scored as " + string.Join("/", t.Types);
                return s + ")";
            }));
            Log.Info("Auto fight brain: " + moves + ".");
            Log.Info("Auto fight brain: enemies start " + AttackRate.ToString("0.00") + " attacks a second each; a quick attack needs answering "
                     + QuickLead().ToString("0.00") + " s after it starts; counters wait up to " + HoldBudget.ToString("0.00")
                     + " s for Batman's move to land (" + _heldCounters + " held this fight). Enemies answering his moves at once: "
                     + string.Join(", ", Brain.React.Where(kv => kv.Value.N >= 3).OrderByDescending(kv => ReactP(kv.Key))
                         .Select(kv => kv.Key + " " + (ReactP(kv.Key) * 100).ToString("0") + "%"))
                     + ". Enemies pounce as a takedown or multi special ends " + (Brain.Pounce.P * 100).ToString("0") + "% of the time ("
                     + Brain.Pounce.N.ToString("0") + " seen). Attack rationing: " + GapSummary() + ". Self-correction: "
                     + string.Join(", ", Brain.Calib.Where(kv => kv.Value[0] >= 3).OrderBy(kv => kv.Key)
                         .Select(kv => kv.Key + " x" + CalibFactor(kv.Key).ToString("0.00"))) + ".");
            _heldCounters = 0;
        }

        if (_stillTotal > 0)
            Log.Info("Auto fight brain: stood still " + _stillTotal.ToString("0.0") + " s in all, " + _stillStretches + " times for 0.3 s or more.");
        _stillTotal = 0;
        _stillFor = 0;
        _stillStretches = 0;
        _tally.Clear();
        _pending = null;
        SaveModel();
    }
}
