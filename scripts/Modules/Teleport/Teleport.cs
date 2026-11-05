using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Moves the player across the city behind a fade: it hops toward the waypoint on ground that really exists,
/// asking the game to stream each district in on the way, and checks every landing held before moving on.
/// Short moves that stay in loaded ground use <see cref="Hop"/> instead, which is instant.
/// </summary>
internal sealed class Teleport : Module
{
    public static readonly Teleport I = new();

    /// <summary>A place to return to: where the player stood and which way they faced.</summary>
    public readonly record struct Spot(Vector3 At, float Yaw);

    private const string Caption = "ORACLE // RELOCATING";
    private const string ToastTitle = "TELEPORT";
    private const float QuickFadeOut = 0.35f;
    private const float QuickFadeIn = 0.6f;
    private const float FullFadeDistance = 60000f;
    private const float MinFadeScale = 0.4f;
    private const float SlowFadeOut = 1.8f;
    private const float SlowFadeIn = 1.4f;
    private const float SettleTime = 1f;
    private const float GiveUpSeconds = 60f;
    private const float WatchdogSeconds = 120f;
    private const float StepGap = 0.5f;
    private const float VerifyGap = 0.6f;
    private const float StepLength = 1000f;
    private const float MinHop = 1500f;
    private const float ScanHeight = 40000f;
    private const float ArrivedRange = 2500f;
    private const float AdjacentRange = 13000f;
    private const float WaterWait = 3f;
    private const float BadSpotRadius = 600f;
    private const float SlipRange = 1000f;
    private const float ExactAbove = 250f;
    private const float CarLift = 150f;
    private const float UnitsPerMetre = 100f;
    private const float MinUndoMove = 200f;
    private const int MaxIdleSteps = 40;
    private const int MaxHistory = 16;
    private const int MaxFinalSlips = 3;
    private const float StreamTimeout = 12f;
    private const float StreamReassert = 1f;
    private const float StreamSettle = 0.25f;
    private const float HoldAbove = 300f;
    private const float WalkableNormal = 0.6f;
    private const float FloorGap = 15f;
    private const float WidthScale = 0.8f;
    private const float WidthTolerance = 40f;
    private const int MaxFloors = 16;
    private const float HintRange = 400f;
    private const float BoxHalf = 4f;
    private const int CheapTraces = 4;
    private const float NoPlace = 500000f;
    private const float RouteStep = 1500f;
    private const float RouteAbove = 5000f;
    private const float LegTimeout = 8f;
    private const float LegSwitchTimeout = 3f;
    private const float LegPass = 1f;
    private const float StandReach = 150f;
    private const float LoadingTimeout = 45f;
    private const float StallTimeout = 8f;
    private const float HoldDrift = 50f;
    private const float ProgressEvery = 2f;
    private const float TextureWait = 4f;
    private const float StreamSearch = 20000f;

    private enum Phase
    {
        Idle,
        FadeOut,
        Stream,
        Travel,
        Settle,
        FadeIn,
    }

    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Vector3> _bad = new();
    private readonly List<Spot> _history = new();
    private Phase _phase;
    private float _timer;
    private float _left;
    private float _total;
    private int _hops;
    private float _targetLoaded;
    private int _idleSteps;
    private Vector3 _target;
    private Vector3 _origin;
    private Vector3 _safe;
    private Vector3 _landed;
    private bool _checking;
    private bool _final;
    private nint _actor;
    private string _result = "";
    private bool _ok;
    private bool _exact;
    private int _finalSlips;
    private bool _undoing;
    private float _fadeScale = 1f;
    private Slider _noFade = null!;
    private Rotator? _face;
    private float _originYaw;
    private Choice _fade = null!;
    private Slider _arrival = null!;
    private Toggle _faceTravel = null!;
    private string? _level;
    private string _originLevel = "";
    private Vector3 _hold;
    private bool _streamed;
    private bool _grounded;
    private float _reassert;
    private float _visibleFor;
    private readonly List<(string Cell, Vector3 At)> _route = new();
    private int _leg;
    private bool _stallBacked;
    private bool _frozen;
    private float _progress;

    public bool Busy => _phase != Phase.Idle;

    private bool Slow => _fade.Index == 1;

    public override void Build()
    {
        AddCommand("teleport_waypoint", "TELEPORT TO WAYPOINT", "Set a waypoint on the map (Tab), then run this. Works across islands and in the Batmobile: the screen fades to black while the city loads.", GoToWaypoint).Moves();
        _fade = AddChoice("teleport_fade", "TELEPORT FADE", "QUICK cuts to black at once. SLOW FADE dims the screen to black gently before the jump and fades back in slower.",
            new[] { "quick", "slow" }, new[] { "QUICK", "SLOW FADE" }).Pref();
        _arrival = AddSlider("teleport_arrival_height", "ARRIVAL HEIGHT", "Arrive this high above the ground, ready to glide. OFF lands you on your feet. Not for quick moves or the Batmobile.",
            0f, 0f, 100f, 5f, 30f, Fmt.MetresOrOff).Pref();
        _faceTravel = AddToggle("teleport_face", "FACE TRAVEL DIRECTION", "After a teleport to a place, turn to face the way you travelled instead of the way you were facing.").Pref();
        _noFade = AddSlider("teleport_no_fade_range", "NO FADE RANGE", "Teleports closer than this happen instantly if the ground there is loaded, and keep the menu open. Further ones fade, for longer the further you go. OFF always fades.",
            150f, 0f, 500f, 25f, 300f, Fmt.MetresOrOff).Pref();
        AddCommand("teleport_back", "GO BACK", "Returns to where you were before your last teleport, facing the same way. Remembers the last 16 and forgets them when a new world loads.", Back).Moves();
        AddCommand("teleport_cancel", "CANCEL TELEPORT", "Stops a long teleport that is still running and brings you back on the last safe ground. Bind it to a key: the menu closes while teleporting.", Cancel);
    }

    public override void WorldChanged()
    {
        _phase = Phase.Idle;
        _history.Clear();
    }

    /// <summary>The pawn that gets moved, or a refusal saying why nothing can move right now.</summary>
    public static Pawn RequireMover()
    {
        Transition.RequireIdle();

        return World.Pawn switch
        {
            RPawnPlayer player => player,
            RVehicleBatmobileBase car => car,
            _ => throw new InvalidOperationException("you can't be moved right now"),
        };
    }

    public void GoToWaypoint()
    {
        RPersistentData pd = Game.GetPersistentData();
        if (pd is not { IsValid: true } || !pd.GetCustomWaypointEnabled())
            throw new InvalidOperationException("no waypoint is set, pick one on the map first");

        // The game's own default is offset space: the stored point plus the city's current offset, which is
        // where the marker really is in the world. Both are logged when they differ, to spot a wrong guess.
        Vector3 world = pd.GetCustomWaypoint(true);
        Vector3 stored = pd.GetCustomWaypoint(false);
        if (Vector3.DistanceSquared(world, stored) > 1f)
            Log.Info("Teleport: waypoint " + Fmt.Position(world) + " in world space, " + Fmt.Position(stored) + " as stored.");

        // Only a pin dropped on the map (Proximity) has a made-up height. A waypoint the game set for a mission,
        // Riddler puzzle, side story or challenge sits on the real spot, so it lands there exactly, even underground.
        bool exact = pd.CustomWaypoint_Type != RPersistentData.ECustomWayPointType.CustomWayType_Proximity;
        Log.Info("Teleport: waypoint type " + pd.CustomWaypoint_Type + (exact ? ", landing exactly." : "."));
        Go(world, exact);
    }

    private void Back()
    {
        if (_history.Count == 0)
            throw new InvalidOperationException("nowhere to go back to");

        Pawn mover = RequireMover();
        Spot spot = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Go(spot.At + Offset, true, new Rotator(0, spot.Yaw, 0), true);
        Log.Detail("Teleport: going back from " + Fmt.Position(mover.Location) + ".");
    }

    private void Cancel()
    {
        if (_phase == Phase.Idle || _phase == Phase.FadeIn)
            throw new InvalidOperationException("no teleport to cancel");

        if (World.Pawn is { } mover && mover.Ptr == _actor)
            Retreat(mover);

        _ok = false;
        _result = "CANCELLED";
        Log.Info("Teleport cancelled by the player.");
        FadeBack();
    }

    /// <summary>
    /// Instantly moves the player within loaded ground, with no fade. The caller has already checked the spot,
    /// because nothing here waits for streaming or verifies the landing.
    /// </summary>
    public void Hop(Vector3 at, Rotator? face = null, bool remember = true)
    {
        Pawn mover = RequireMover();
        var from = new Spot(mover.Location, mover.Rotation.Yaw);
        Place(mover, at);
        if (face is { } rotation)
            Turn(mover, rotation);

        if (remember)
            Remember(from);

        Log.Info("Hopped to " + Fmt.Position(at) + " from " + Fmt.Position(from.At) + ".");
    }

    /// <summary>
    /// Moves the player to a target, with as little fade as the distance allows: nearby ground that is already
    /// loaded needs none, and further trips fade for longer. An exact target keeps its height, so it can land
    /// indoors or under a roof; otherwise only X and Y count, and the player lands on the highest ground there.
    /// </summary>
    public void Go(Vector3 target, bool exact, Rotator? face = null, bool undoing = false)
    {
        Pawn mover = RequireMover();

        // Some missions park their marker at a dummy point like (-999999, -999999) until it has a real place.
        if (MathF.Abs(target.X) > NoPlace || MathF.Abs(target.Y) > NoPlace)
            throw new InvalidOperationException("that marker has no real location yet");

        _target = target;
        _exact = exact;
        _finalSlips = 0;
        _face = face;
        _undoing = undoing;
        _actor = mover.Ptr;
        _origin = mover.Location;
        _originYaw = mover.Rotation.Yaw;
        _safe = _origin;
        if (ArriveNow(mover))
            return;

        if (World.Ri is { } ri && !InCityMap(ri, _origin))
        {
            Log.Info("Teleport refused: " + Fmt.Position(_origin) + " in " + ri.CurrentLevel + " is outside every city district (streaming offset "
                + Fmt.Position(ri.CurrentLevelStreamingOffset) + ").");
            throw new InvalidOperationException("can't teleport out of this building, leave through its exit first");
        }

        _hops = 0;
        _level = null;
        _stallBacked = false;
        _streamed = false;
        _grounded = false;
        _tried.Clear();
        _targetLoaded = 0f;
        _idleSteps = 0;
        _bad.Clear();
        _checking = false;
        _final = false;
        _left = GiveUpSeconds;
        _total = 0f;
        _timer = 0f;
        _result = "";
        _phase = Phase.FadeOut;
        _fadeScale = Math.Clamp(Vector3.Distance(_origin, _target) / FullFadeDistance, MinFadeScale, 1f);
        Menu.Close();
        Fader.Out((Slow ? SlowFadeOut : QuickFadeOut) * _fadeScale, Slow, Caption);
        Log.Info("Teleport to " + Fmt.Position(_target) + " from " + Fmt.Position(_origin) + ".");
    }

    /// <summary>Skips the fade when the target is within range and the ground there is already loaded.</summary>
    private bool ArriveNow(Pawn mover)
    {
        float range = _noFade.Value * UnitsPerMetre;
        if (range <= 0f || Vector3.Distance(_origin, _target) > range)
            return false;

        Safe.Try("level list", () => CityCells.ReadLevels(false));
        if (!TargetGround(mover, out Vector3 ground) || !CityCells.Ready(ground))
            return false;

        Hop(ground + new Vector3(0f, 0f, Lift(mover)), null, !_undoing);
        Safe.Run("teleport arrival", Arrive);
        return true;
    }

    private void Remember(Spot spot)
    {
        // Kept unshifted, so GO BACK still lands right if the city's offset changes in between.
        _history.Add(spot with { At = spot.At - Offset });
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    /// <summary>Applies the arrival options once a long teleport has landed for good.</summary>
    private void Arrive()
    {
        if (World.Pawn is not { } mover || mover.Ptr != _actor)
            return;

        Rotator? face = _face;
        var travelled = new Vector3(_target.X - _origin.X, _target.Y - _origin.Y, 0f);
        if (face == null && _faceTravel.On && travelled.LengthSquared() > 1f)
            face = new Rotator(0, Rotator.FromDirection(travelled).Yaw, 0);

        if (face is { } rotation)
            Turn(mover, rotation);

        if (_arrival.Value > 0f && mover is RPawnPlayer)
            Place(mover, mover.Location + new Vector3(0f, 0f, _arrival.Value * UnitsPerMetre));
    }

    private static void Turn(Pawn mover, Rotator rotation)
    {
        if (mover is not RVehicleBatmobileBase)
            mover.SetRotation(new Rotator(0, rotation.Yaw, 0));

        World.Pc?.ClientSetRotation(rotation, true);
    }

    public override void Tick(float dt)
    {
        CityCells.Tick();
        if (_phase == Phase.Idle)
            return;

        Pawn? mover = World.Pawn;
        bool valid = mover != null && mover.Ptr == _actor;
        if (!valid && _phase != Phase.FadeIn)
        {
            Log.Info("Teleport cancelled: the player changed.");
            _result = "";
            FadeBack();
        }

        _timer += dt;
        _total += dt;
        if (_total > WatchdogSeconds && _phase != Phase.FadeIn)
        {
            if (valid)
                Retreat(mover!);
            Log.Info("Teleport watchdog: stopped after " + (int)_total + " s in " + _phase + ".");
            _result = "AS CLOSE AS POSSIBLE";
            _ok = false;
            FadeBack();
        }

        switch (_phase)
        {
            case Phase.FadeOut:
                if (Fader.IsDark)
                {
                    _origin = mover!.Location;
                    _originYaw = mover.Rotation.Yaw;
                    _safe = _origin;
                    _timer = StepGap;
                    _phase = Safe.Try("teleport stream", () => BeginStream(mover)) ? Phase.Stream : Phase.Travel;
                }

                break;

            case Phase.Stream:
                StreamStep(mover!, dt);
                break;

            case Phase.Travel:
                _left -= dt;
                if (_timer >= (_checking ? VerifyGap : StepGap))
                {
                    _timer = 0f;
                    TravelStep(mover!);
                }

                break;

            case Phase.Settle:
                if (_timer >= SettleTime && (_timer >= SettleTime + TextureWait || TexturesDone()))
                {
                    Safe.Run("teleport arrival", Arrive);
                    FadeBack();
                }

                break;

            case Phase.FadeIn:
                if (Fader.IsClear)
                    _phase = Phase.Idle;
                break;
        }
    }

    private void FadeBack()
    {
        if (World.Pawn is { } held && held.Ptr == _actor)
            Release(held);
        else
            _frozen = false;

        if (!_undoing && World.Pawn is { } mover && mover.Ptr == _actor && Vector3.DistanceSquared(mover.Location, _origin) > MinUndoMove * MinUndoMove)
            Remember(new Spot(_origin, _originYaw));

        _phase = Phase.FadeIn;
        _timer = 0f;
        Fader.In((Slow ? SlowFadeIn : QuickFadeIn) * _fadeScale, ToastTitle, _result, _ok);
    }

    private void Finish(string result, bool ok)
    {
        _result = result;
        _ok = ok;
        _timer = 0f;
        if (ok)
            _phase = Phase.Settle;
        else
            FadeBack();
    }

    private void TravelStep(Pawn mover)
    {
        if (_checking)
        {
            _checking = false;
            bool slipped = Geo.DistXY(mover.Location, _landed) > SlipRange;
            if (slipped || mover.Location.Z < _landed.Z - 150f)
            {
                // Straight after a stream there is no proven ground nearby: the old safe spot is in a district
                // that has since unloaded, so wait above the target again instead of falling into the void there.
                Vector3 back = _streamed && !_grounded ? _hold : _safe;
                Place(mover, back);
                _bad.Add(_landed);
                if (_final)
                    _finalSlips++;
                Log.Info("Teleport: ground at " + Fmt.Position(_landed) + " wasn't solid yet, back to " + Fmt.Position(back) + ".");
            }
            else
            {
                _safe = mover.Location;
                _grounded = true;
                bool switched = MakeCurrent(mover, _safe);
                if (_final)
                {
                    Log.Info("Teleported onto " + Fmt.Position(_landed) + " after " + _hops + " hops.");
                    Finish("DONE", true);
                    return;
                }

                if (switched)
                    return;

                float left = Geo.DistXY(_safe, _target);
                if (left <= ArrivedRange && !GroundAt(mover, _target.X, _target.Y, out _))
                {
                    Log.Info("Teleport: waypoint has no ground, stopped on the nearest ground " + (int)left + " units away.");
                    Finish("NEAREST GROUND", true);
                    return;
                }
            }
        }

        if (_left <= 0f || _idleSteps >= MaxIdleSteps)
        {
            bool moved = Vector3.DistanceSquared(_safe, _origin) > 100f * 100f;
            Log.Info("Teleport: gave up, " + (moved ? "stopped as close as possible." : "no route to the waypoint."));
            Finish(moved ? "AS CLOSE AS POSSIBLE" : "NO GROUND THERE", false);
            return;
        }

        float lift = Lift(mover);
        Safe.Try("level list", () => CityCells.ReadLevels(false));
        bool targetReady = CityCells.Ready(_target);
        if (TargetGround(mover, out Vector3 hit) && (_streamed || CityCells.Ready(hit)))
        {
            // A few slips can be ground still streaming in. More means the spot is too small to stand on.
            if (_finalSlips < MaxFinalSlips)
            {
                MakeCurrent(mover, hit);
                Land(mover, hit + new Vector3(0f, 0f, lift), true);
                return;
            }

            if (NearestGround(mover, out Vector3 beside))
            {
                Log.Info("Teleport: can't stand at the target, landing beside it at " + Fmt.Position(beside) + ".");
                Land(mover, beside + new Vector3(0f, 0f, lift), true);
                return;
            }
        }

        if (MakeCurrent(mover, _target))
            return;

        if (targetReady && TargetCellLoaded())
        {
            _targetLoaded += StepGap;
            if (_targetLoaded >= WaterWait && NearestGround(mover, out Vector3 shore))
            {
                Log.Info("Teleport: waypoint is over water or a gap, landing on the nearest ground at " + Fmt.Position(shore) + ".");
                Land(mover, shore + new Vector3(0f, 0f, lift), true);
                return;
            }
        }

        Vector3 from = mover.Location;
        float dist = Geo.DistXY(from, _target);
        if (dist <= ArrivedRange && !_checking)
        {
            _targetLoaded += StepGap;

            // Held in the air after a stream, the player isn't standing on anything yet, so "already there" would
            // drop them. Look around for ground, and if there is none go back rather than fall.
            if (_streamed && !Standing(mover))
            {
                if (_targetLoaded < WaterWait)
                    return;
                if (NearestGround(mover, out Vector3 near))
                {
                    Log.Info("Teleport: no floor right at the target, landing on the nearest ground at " + Fmt.Position(near) + ".");
                    Land(mover, near + new Vector3(0f, 0f, lift), true);
                    return;
                }

                Log.Info("Teleport: no ground loaded around the target, going back.");
                Retreat(mover);
                Finish("NO GROUND THERE", false);
                return;
            }

            if (_targetLoaded >= 1f)
            {
                Log.Info("Teleport: already on the nearest ground, " + (int)dist + " units from the waypoint.");
                Finish(_hops > 0 ? "NEAREST GROUND" : "ALREADY THERE", true);
                return;
            }
        }

        for (float d = dist - StepLength; d >= MinHop; d -= StepLength)
        {
            float t = d / dist;
            float x = from.X + (_target.X - from.X) * t;
            float y = from.Y + (_target.Y - from.Y) * t;
            if (!GroundAt(mover, x, y, out Vector3 step) || IsBad(step) || !CityCells.Ready(step))
                continue;

            _hops++;
            _tried.Clear();
            Log.Detail("Teleport: hop " + _hops + " to " + Fmt.Position(step) + ", " + (int)(dist - d) + " units from the waypoint.");
            Land(mover, step + new Vector3(0f, 0f, lift), false);
            return;
        }

        float stayFrom = dist - MinHop;
        if (targetReady && stayFrom > MinHop && ClosestToTarget(mover, stayFrom, out Vector3 around))
        {
            _hops++;
            _tried.Clear();
            Log.Detail("Teleport: hop " + _hops + " around a gap to " + Fmt.Position(around) + ".");
            Land(mover, around + new Vector3(0f, 0f, lift), false);
            return;
        }

        if (WalkCells(mover))
            _idleSteps = 0;
        else
            _idleSteps++;
    }

    private bool IsBad(Vector3 p)
    {
        foreach (Vector3 b in _bad)
        {
            if (Geo.DistXYSq(b, p) < BadSpotRadius * BadSpotRadius)
                return true;
        }

        return false;
    }

    private static bool NearPlayer(Pawn mover, Vector3 at)
    {
        string? name = CityCells.At(at);
        if (name != null)
            return CityCells.All[name].DistanceXY(mover.Location) <= AdjacentRange;
        return Geo.DistXY(at, mover.Location) <= AdjacentRange + 12288f;
    }

    private bool ClosestToTarget(Pawn mover, float maxRadius, out Vector3 best)
    {
        best = default;
        for (float r = 1500f; r <= maxRadius; r += 1500f)
        {
            int steps = Math.Clamp((int)(r / 400f), 12, 160);
            float bestD = float.MaxValue;
            bool found = false;
            for (int i = 0; i < steps; i++)
            {
                float a = MathF.Tau * i / steps;
                if (!GroundAt(mover, _target.X + MathF.Cos(a) * r, _target.Y + MathF.Sin(a) * r, out Vector3 g) || IsBad(g) || !CityCells.Ready(g))
                    continue;

                float d = Geo.DistXY(g, _target);
                if (d < bestD)
                {
                    bestD = d;
                    best = g;
                    found = true;
                }
            }

            if (found)
                return true;
        }

        return false;
    }

    private bool TargetCellLoaded()
    {
        foreach (RLevelVolumeOW v in World.All<RLevelVolumeOW>())
        {
            if (v.EncompassesPoint(_target))
                return true;
        }

        return false;
    }

    private bool NearestGround(Pawn mover, out Vector3 ground)
    {
        float reach = _streamed ? StreamSearch : 9000f;
        for (float r = 500f; r <= reach; r += r < 9000f ? 500f : 1500f)
        {
            int steps = Math.Clamp((int)(r / 250f), 8, 96);
            for (int i = 0; i < steps; i++)
            {
                float a = MathF.Tau * i / steps;
                if (GroundAt(mover, _target.X + MathF.Cos(a) * r, _target.Y + MathF.Sin(a) * r, out ground) && !IsBad(ground) && (_streamed || CityCells.Ready(ground)))
                    return true;
            }
        }

        ground = default;
        return false;
    }

    /// <summary>Asks the game to stream the district at a point. Returns true if a switch was requested.</summary>
    private bool MakeCurrent(Pawn mover, Vector3 at)
    {
        if (World.Ri is not { } ri)
            return false;

        // After a direct stream the level that holds the target is already current. Switching to the district
        // around it (a tunnel sits inside an overworld cell) would unload the very ground about to be landed on.
        if (_streamed && _level != null && ri.CurrentLevel.ToString().Equals(_level, StringComparison.OrdinalIgnoreCase)
            && Geo.DistXY(at, _target) <= ArrivedRange)
            return false;

        foreach (RLevelVolumeOW v in World.All<RLevelVolumeOW>())
        {
            if (!v.EncompassesPoint(at))
                continue;

            string level = v.Level.ToString();
            if (level.Length == 0 || level == "None" || ri.LastStreamingLevel.ToString() == level || !NearPlayer(mover, at))
                return false;

            ri.SetCurrentLevel(v.Level, mover, false);
            _idleSteps = 0;
            Log.Detail("Teleport: streaming city cell " + level + ".");
            return true;
        }

        string? cached = CityCells.At(at);
        if (cached == null || ri.LastStreamingLevel.ToString().Equals(cached, StringComparison.OrdinalIgnoreCase))
            return false;
        if (CityCells.All[cached].DistanceXY(mover.Location) > AdjacentRange)
            return false;

        return SwitchCell(ri, mover, cached);
    }

    private bool SwitchCell(RGameRI ri, Pawn mover, string name)
    {
        if (!_tried.Add(name) || CityCells.All[name].DistanceXY(mover.Location) > AdjacentRange)
            return false;

        ri.SetCurrentLevel(new FName(name), mover, false);
        _idleSteps = 0;
        Log.Detail("Teleport: streaming city cell " + name + " (remembered).");
        return true;
    }

    private bool WalkCells(Pawn mover)
    {
        if (World.Ri is not { } ri)
            return false;

        string current = ri.LastStreamingLevel.ToString();
        Vector3 from = mover.Location;
        float dist = Vector3.Distance(from, _target);
        for (float d = 0f; d <= dist; d += 1500f)
        {
            Vector3 p = Vector3.Lerp(from, _target, dist <= 0f ? 1f : d / dist);
            string? name = CityCells.At(p);
            if (name == null || name.Equals(current, StringComparison.OrdinalIgnoreCase) || _tried.Contains(name))
                continue;
            if (CityCells.All[name].DistanceXY(from) > AdjacentRange)
                break;

            return SwitchCell(ri, mover, name);
        }

        return false;
    }

    /// <summary>Casts a ray against world geometry. Works on anything loaded, whatever the player is doing.</summary>
    public static bool Ray(Actor from, Vector3 start, Vector3 end, out Vector3 hit, out Vector3 normal)
    {
        Actor? hitActor = from.Trace(out hit, out normal, end, start, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
        return hitActor != null && hit != Vector3.Zero;
    }

    /// <summary>How far above a surface the pawn's centre sits when placed on it.</summary>
    public static float Lift(Pawn mover)
    {
        float height = mover.GetCollisionHeight();
        return (mover is RVehicleBatmobileBase ? Math.Max(height, CarLift) : height) + 40f;
    }

    /// <summary>
    /// The floor to land on at the target. Every surface on the line is considered, not just the first one hit,
    /// and only ones the pawn really fits on count. An exact target takes the floor it stands on (the nearest
    /// one at or below it), so it can stay indoors or under a bridge. Otherwise a floor near the target's own
    /// height wins, and the highest one is used when its height says nothing.
    /// </summary>
    private bool TargetGround(Pawn mover, out Vector3 hit)
    {
        List<Vector3> all = Floors(mover, _target.X, _target.Y, ScanTop(mover), ScanBottom(mover), MaxFloors, MaxFloors * 2);
        List<Vector3> fit = all.FindAll(f => Fits(mover, f));
        List<Vector3> floors = fit.Count > 0 ? fit : all;
        hit = default;
        if (floors.Count == 0)
            return false;

        hit = _exact ? FloorUnder(floors, _target.Z) : FloorNear(floors, _target.Z);
        return true;
    }

    /// <summary>The nearest floor at or just below a height, or the lowest one above it if there is none.</summary>
    private static Vector3 FloorUnder(List<Vector3> floors, float z)
    {
        foreach (Vector3 f in floors)
        {
            if (f.Z <= z + ExactAbove)
                return f;
        }

        return floors[^1];
    }

    /// <summary>The floor closest to a height when one is close to it, otherwise the highest.</summary>
    private static Vector3 FloorNear(List<Vector3> floors, float z)
    {
        Vector3 best = floors[0];
        float bestGap = float.MaxValue;
        foreach (Vector3 f in floors)
        {
            float gap = MathF.Abs(f.Z - z);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = f;
            }
        }

        return bestGap <= HintRange ? best : floors[0];
    }

    private float ScanTop(Pawn mover) => Math.Max(_target.Z, mover.Location.Z) + ScanHeight;

    private float ScanBottom(Pawn mover) => Math.Min(_target.Z, mover.Location.Z) - ScanHeight;

    /// <summary>The first floor at a point, for the cheap checks made many times a step.</summary>
    private bool GroundAt(Pawn mover, float x, float y, out Vector3 hit)
    {
        // The ring searches call this thousands of times a step, so it may only look past a few surfaces.
        List<Vector3> floors = Floors(mover, x, y, ScanTop(mover), ScanBottom(mover), 1, CheapTraces);
        hit = floors.Count > 0 ? floors[0] : default;
        return floors.Count > 0;
    }

    /// <summary>
    /// Surfaces on a vertical line, top first. Looks past what can't be stood on: invisible blocking volumes,
    /// which the city uses to fence off roofs and the sky, other pawns, and walls or slopes that are too steep.
    /// </summary>
    private static List<Vector3> Floors(Pawn mover, float x, float y, float top, float bottom, int max, int traces)
    {
        var found = new List<Vector3>();
        float z = top;
        for (int i = 0; i < traces && found.Count < max && z > bottom; i++)
        {
            Actor? what = mover.Trace(out Vector3 hit, out Vector3 normal, new Vector3(x, y, bottom), new Vector3(x, y, z),
                Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
            if (what == null || hit == Vector3.Zero)
                break;

            // A trace that starts inside solid geometry can stop where it began, so step well clear of it.
            z = hit.Z >= z - 1f ? z - 200f : hit.Z - FloorGap;
            if (what is Volume || what is Pawn || normal.Z < WalkableNormal || IsStandIn(what))
                continue;

            found.Add(hit);
        }

        return found;
    }

    /// <summary>
    /// True when the pawn fits standing on a surface: there is head room above it, and a body-wide box comes
    /// to rest on it rather than on a railing, ledge or wall beside it.
    /// </summary>
    private static bool Fits(Pawn mover, Vector3 surface)
    {
        float height = mover.GetCollisionHeight();
        Vector3 foot = surface + new Vector3(0f, 0f, 5f);
        Vector3 head = surface + new Vector3(0f, 0f, height * 2f + 20f);
        if (Ray(mover, foot, head, out _, out _))
            return false;
        if (mover is RVehicleBatmobileBase)
            return true;

        float radius = mover.GetCollisionRadius() * WidthScale;
        var box = new Vector3(radius, radius, BoxHalf);
        Actor? what = mover.Trace(out Vector3 hit, out _, surface - new Vector3(0f, 0f, WidthTolerance), head,
            Component.ECollisionFilter.COF_TraceWorld, box, out _, 0);
        if (what == null || hit == Vector3.Zero)
            return true;

        return MathF.Abs(hit.Z - BoxHalf - surface.Z) <= WidthTolerance;
    }

    /// <summary>
    /// The direct route. The game only switches the current district in the background to one next to the one
    /// it is on, and a blocking switch to a far one can hang it. So this walks the districts between here and
    /// the target behind the fade: the player is held in the air inside each in turn while it streams in, and
    /// the last is the target's own. Returns false when the target is already loaded or has no known district,
    /// so the hop route runs instead.
    /// </summary>
    private bool BeginStream(Pawn mover)
    {
        if (World.Ri is not { } ri)
            return false;

        _originLevel = ri.CurrentLevel.ToString();
        CityCells.ReadLevels(false);
        // Loaded ground only counts for an exact target when it is the floor at the target's height, not the
        // street above a tunnel that hasn't streamed in yet.
        if (TargetGround(mover, out Vector3 near) && CityCells.Ready(near) && (!_exact || near.Z <= _target.Z + ExactAbove))
            return false;

        // A floor right at an exact target's height is real loaded collision, even where the district's flags
        // say hidden: indoors, like the GCPD, the city cell around you is hidden but you're standing in it.
        if (_exact && Vector3.Distance(near, _target) <= ExactAbove * 2f && near != default)
            return false;

        string? level = ResolveLevel(ri, mover);
        if (level == null || level == "None")
        {
            Log.Info("Teleport: no district found at the target, hopping there instead.");
            return false;
        }

        _level = level;
        _streamed = true;
        _hold = _exact
            ? _target + new Vector3(0f, 0f, Lift(mover))
            : new Vector3(_target.X, _target.Y, Math.Max(_target.Z, _origin.Z) + HoldAbove);
        BuildRoute(ri, level);
        Log.Info("Teleport: streaming " + string.Join(" > ", _route.Select(r => r.Cell)) + " (was " + _originLevel + ").");
        StartLeg(mover, ri, 0);
        return true;
    }

    /// <summary>
    /// The districts a straight line crosses from here to the target, each with a point to wait at inside it,
    /// ending with the target's own level and the hold point above the target.
    /// </summary>
    private void BuildRoute(RGameRI ri, string final)
    {
        _route.Clear();
        Vector3 shift = ri.CurrentLevelStreamingOffset;
        Vector3 a = _origin - shift;
        Vector3 b = _target - shift;
        float dist = Geo.DistXY(a, b);
        float high = Math.Max(_origin.Z, _target.Z) + RouteAbove;
        string? last = CityCells.At(a);
        Vector3 first = default;
        for (float d = 0f; ; d += RouteStep)
        {
            bool end = d >= dist;
            Vector3 p = Vector3.Lerp(a, b, dist <= 0f || end ? 1f : d / dist);
            string? name = CityCells.At(p);
            if (name != null && !name.Equals(last, StringComparison.OrdinalIgnoreCase))
            {
                last = name;
                first = p;
                _route.Add((name, default));
            }

            // Wait halfway along the stretch of line inside the district, well clear of its edges.
            if (name != null && _route.Count > 0 && name.Equals(_route[^1].Cell, StringComparison.OrdinalIgnoreCase))
            {
                Vector3 mid = (first + p) * 0.5f + shift;
                _route[^1] = (name, new Vector3(mid.X, mid.Y, high));
            }

            if (end)
                break;
        }

        if (_route.Count > 0 && _route[^1].Cell.Equals(final, StringComparison.OrdinalIgnoreCase))
            _route[^1] = (final, _hold);
        else
            _route.Add((final, _hold));

        // The district the game is already on needs no wait.
        if (_route.Count > 1 && _route[0].Cell.Equals(_originLevel, StringComparison.OrdinalIgnoreCase))
            _route.RemoveAt(0);
    }

    private void StartLeg(Pawn mover, RGameRI ri, int leg)
    {
        _leg = leg;
        _timer = 0f;
        _reassert = 0f;
        _visibleFor = 0f;
        _progress = 0f;
        Place(mover, _route[leg].At);
        ri.SetCurrentLevel(new FName(_route[leg].Cell), mover, false);
    }

    /// <summary>
    /// True when the player is in the open city's own map. Interiors like the GCPD lockup are separate persistent
    /// maps at their own offset, outside every city district, and only the game's own doors switch back from one.
    /// Asking for a city district from there waits forever and hangs the game.
    /// </summary>
    internal static bool InCityMap(RGameRI ri, Vector3 at)
    {
        CityCells.Ensure();
        return CityCells.At(at - ri.CurrentLevelStreamingOffset) != null;
    }

    /// <summary>
    /// How far the game has shifted the whole city right now. After an interior like the GCPD the city can sit
    /// 100000 units higher than its own coordinates. Map places, districts and saved spots are kept unshifted,
    /// so they add this before use and take it off when stored.
    /// </summary>
    public static Vector3 Offset => World.Ri?.CurrentLevelStreamingOffset ?? Vector3.Zero;

    /// <summary>The district at the target, from the game's own lookup first and the cached cells after.</summary>
    private string? ResolveLevel(RGameRI ri, Pawn mover)
    {
        bool car = mover is RVehicleBatmobileBase;
        Vector3[] probes =
        {
            _target,
            new(_target.X, _target.Y, Math.Max(_target.Z, _origin.Z)),
        };

        string? district = CityCells.At(_target - ri.CurrentLevelStreamingOffset);
        foreach (Vector3 probe in probes)
        {
            string? name = CityCells.LevelName(ri.GetLevelVolumeForPoint(probe, 0f, false, false, car, -1, -1, -1));
            if (name == null)
                continue;

            // The game's own level comes first, interiors too: the Pinkney Orphanage's grounds belong to Orphan_A1,
            // and its district CityX_13 never loads ground by the door. If it never shows up, StreamStep falls
            // back to the district around the target, and with no floor right at the target the nearest one wins.
            if (!name.StartsWith("City", StringComparison.OrdinalIgnoreCase))
                Log.Info("Teleport: the target is in " + name + ", an interior.");
            return name;
        }

        return district;
    }

    /// <summary>Holds the player in the current district of the route until it is visible, then moves on.</summary>
    private void StreamStep(Pawn mover, float dt)
    {
        if (World.Ri is not { } ri || _level == null || _leg >= _route.Count)
        {
            _phase = Phase.Travel;
            return;
        }

        (string cell, Vector3 at) = _route[_leg];
        bool lastLeg = _leg == _route.Count - 1;
        Hold(mover, at);

        var level = new FName(cell);
        _reassert += dt;
        if (_reassert >= StreamReassert && !ri.CurrentLevel.ToString().Equals(cell, StringComparison.OrdinalIgnoreCase))
        {
            _reassert = 0f;
            ri.SetCurrentLevel(level, mover, false);
            Log.Detail("Teleport: asked again for " + cell + ", the game was on " + ri.CurrentLevel + ".");
        }

        // Ready means the level that was asked for, which for a tunnel is not the district around it.
        Safe.Try("level list", () => CityCells.ReadLevels(false));
        // The game's own flags for some districts (CityX_28, CityX_13) never turn on although their ground is
        // there, so solid ground where the player waits counts as loaded too. A landing that isn't really solid
        // is still caught by the slip check.
        bool current = ri.CurrentLevel.ToString().Equals(cell, StringComparison.OrdinalIgnoreCase);
        bool loading = ri.IsLevelLoading(level);
        bool visible = ri.IsLevelVisible(level, true) || CityCells.Shown(cell) == true
            || (!loading && (lastLeg ? TargetGround(mover, out _) : current && GroundAt(mover, at.X, at.Y, out _)));
        _visibleFor = visible ? _visibleFor + dt : 0f;

        // The game only switches to a district next to its current one. When it won't even switch, the route
        // has a gap the line can't see, usually water between islands. Hop the rest over real ground (bridges)
        // from the last district that did load, instead of waiting on one that never will.
        bool switched = ri.CurrentLevel.ToString().Equals(cell, StringComparison.OrdinalIgnoreCase);
        if (!visible && !switched && _timer >= LegSwitchTimeout)
        {
            Log.Info("Teleport: the game won't switch to " + cell + " from " + ri.CurrentLevel + ", hopping the rest of the way.");
            if (_grounded)
                Place(mover, _safe);
            _phase = Phase.Travel;
            _timer = StepGap;
            return;
        }

        // A district on the way only has to be the game's current one, so the next counts as its neighbour.
        // Waiting for it to look loaded too cost 8 s a district on the way to the orphanage.
        bool passed = !lastLeg && current && _timer >= LegPass;
        // Some districts never finish at a point, like the Cauldron's map label on the edge of CityZ_19, which
        // only has low-detail scenery there. Go back to the district before it and land on its nearest real ground.
        if (lastLeg && loading && _leg > 0 && !_stallBacked && _timer >= StallTimeout)
        {
            string back = _route[_leg - 1].Cell;
            Log.Info("Teleport: " + cell + " is still loading after " + _timer.ToString("0.0", CultureInfo.InvariantCulture)
                + " s, using the district before it, " + back + ", and its nearest ground.");
            _stallBacked = true;
            _level = back;
            _route[_leg] = (back, _hold);
            StartLeg(mover, ri, _leg);
            return;
        }

        // After that fallback the district only has to be current and settled; the target spot itself may be empty.
        if (lastLeg && _stallBacked && current && !loading && _timer >= LegPass)
            _visibleFor = StreamSettle;

        _progress += dt;
        if (lastLeg && _progress >= ProgressEvery)
        {
            _progress = 0f;
            Log.Info("Teleport: waiting on " + cell + " for " + _timer.ToString("0", CultureInfo.InvariantCulture) + " s: game on "
                + ri.CurrentLevel + ", loading " + loading + ", loaded " + ri.IsLevelLoaded(level, true) + ", visible "
                + ri.IsLevelVisible(level, true) + ", at " + Fmt.Position(mover.Location) + ".");
        }

        float limit = lastLeg ? (loading ? LoadingTimeout : StreamTimeout) : LegTimeout;
        if (!passed && _visibleFor < (lastLeg ? StreamSettle : 0.01f) && _timer < limit)
            return;

        string took = _timer.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (!lastLeg)
        {
            Log.Info("Teleport: " + cell + (visible ? " ready after " : passed ? " current after " : " still not visible after ") + took + ", moving on.");

            // A district that loaded is real ground to fall back on: cancelling, a slip or the hop route use it.
            if (visible && GroundAt(mover, at.X, at.Y, out Vector3 ground))
            {
                _safe = ground + new Vector3(0f, 0f, Lift(mover));
                _grounded = true;
            }

            StartLeg(mover, ri, _leg + 1);
            return;
        }

        if (_visibleFor >= StreamSettle)
        {
            Log.Info("Teleport: " + cell + " ready after " + took + ".");
        }
        else if (CityCells.At(_target - ri.CurrentLevelStreamingOffset) is { } district && !district.Equals(cell, StringComparison.OrdinalIgnoreCase))
        {
            // The level the game picked never showed up: try the overworld district around the target once.
            Log.Info("Teleport: " + cell + " never reported visible, trying the district " + district + " instead.");
            _level = district;
            _route[_leg] = (district, _hold);
            StartLeg(mover, ri, _leg);
            return;
        }
        else
        {
            Log.Info("Teleport: " + cell + " never reported visible (game on " + ri.CurrentLevel + ", loaded "
                + ri.IsLevelLoaded(level, true) + ", loading " + ri.IsLevelLoading(level) + "), looking for ground anyway.");
        }

        _phase = Phase.Travel;
        _timer = StepGap;
    }

    /// <summary>
    /// Puts the player back on the last ground that held. Before the first landing after a stream that is the
    /// start, whose district has to be loaded back in first.
    /// </summary>
    private void Retreat(Pawn mover)
    {
        Release(mover);
        // After a stream the start's district has unloaded and can't be switched back to from here, so going
        // back there would mean falling. Stand on whatever ground is loaded nearby instead.
        if (_streamed)
        {
            float lift = Lift(mover);
            Vector3 at = mover.Location;
            if (GroundAt(mover, at.X, at.Y, out Vector3 under) || NearestGround(mover, out under))
            {
                Place(mover, under + new Vector3(0f, 0f, lift));
                Log.Info("Teleport: stopped on the nearest loaded ground at " + Fmt.Position(under) + ".");
                return;
            }

            // The last district on the way that had ground may still be loaded: only go there if it is.
            if (_grounded && GroundAt(mover, _safe.X, _safe.Y, out Vector3 back))
            {
                Place(mover, back + new Vector3(0f, 0f, lift));
                Log.Info("Teleport: no ground near the target, back to the last loaded ground at " + Fmt.Position(back) + ".");
                return;
            }

            Log.Info("Teleport: no loaded ground anywhere near, back to the start.");
        }

        Place(mover, _safe);
    }

    /// <summary>True once the game has streamed in the full-detail textures around the player.</summary>
    private static bool TexturesDone() =>
        World.Ri is not { } ri || Safe.Try("texture streaming", ri.HasAllTextureStreamingCompleted, fallback: true);

    /// <summary>
    /// True for the low-detail stand-in scenery the game shows for districts that aren't fully loaded. It lives
    /// in packages named like CityZ_19_Static_LOD1; landing on it means blurry surroundings and no collision.
    /// </summary>
    private static bool IsStandIn(Actor what)
    {
        try
        {
            string path = what.GetPathName(null);
            int dot = path.IndexOf('.');
            string package = dot > 0 ? path[..dot] : path;
            return package.Contains("_LOD", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the player still in mid-air while a district streams. Physics goes off so nothing falls, and the
    /// player is only moved again on drifting: placing them every frame kept the game from ever finishing a load.
    /// </summary>
    private void Hold(Pawn mover, Vector3 at)
    {
        bool drifted = Vector3.DistanceSquared(mover.Location, at) > HoldDrift * HoldDrift;
        if (mover is RVehicleBatmobileBase)
        {
            if (drifted)
                Place(mover, at);
            return;
        }

        if (!_frozen)
        {
            _frozen = true;
            mover.SetPhysics(Actor.EPhysics.PHYS_None, false);
            Place(mover, at);
            return;
        }

        if (drifted)
            Place(mover, at);
    }

    /// <summary>Gives a held player normal physics back, falling, so they settle onto whatever is below.</summary>
    private void Release(Pawn mover)
    {
        if (!_frozen)
            return;

        _frozen = false;
        if (mover is not RVehicleBatmobileBase && mover.IsValid)
            Safe.Run("teleport release", () => mover.SetPhysics(Actor.EPhysics.PHYS_Falling, false));
    }

    /// <summary>True when there is something solid just under the pawn's feet, not just a hold in mid-air.</summary>
    private static bool Standing(Pawn mover)
    {
        Vector3 at = mover.Location;
        return Ray(mover, at, at - new Vector3(0f, 0f, mover.GetCollisionHeight() + StandReach), out _, out _);
    }

    private void Land(Pawn mover, Vector3 at, bool final)
    {
        Place(mover, at);
        Release(mover);
        _idleSteps = 0;
        _landed = at;
        _checking = true;
        _final = final;
    }

    /// <summary>Moves a pawn and stops it dead. A vehicle's rigid body has to be moved too or it snaps straight back.</summary>
    public static void Place(Pawn mover, Vector3 at, Rotator? facing = null)
    {
        if (mover is RVehicleBatmobileBase car)
        {
            PlaceCar(car, at, facing ?? car.Rotation);
            return;
        }

        mover.SetLocation(at);
        mover.Velocity = Vector3.Zero;
    }

    private static void PlaceCar(RVehicleBatmobileBase car, Vector3 at, Rotator facing)
    {
        var upright = new Rotator(0, facing.Yaw, 0);
        car.SetLocation(at);
        car.SetRotation(upright);
        car.Velocity = Vector3.Zero;
        car.AngularVelocity = Vector3.Zero;
        if (car.Mesh is { IsValid: true } mesh)
        {
            mesh.SetRBPosition(at);
            mesh.SetRBRotation(upright);
            mesh.SetRBLinearVelocity(Vector3.Zero);
            mesh.SetRBAngularVelocity(Vector3.Zero);
            mesh.WakeRigidBody();
        }

        car.TeleportCleanup(at, upright);
    }
}
