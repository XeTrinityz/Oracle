using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Moves the player across the city behind a fade by flying them there above the rooftops. Each district becomes
/// the game's current one as it is crossed into, exactly as when the player glides there themselves, so the game
/// streams the next district in as a neighbour of the one being crossed. Asking the game for a far district
/// directly was never reliable: some districts never answered. Once above the target the player is put on a
/// floor that belongs to a loaded level and the landing is checked. Short moves onto loaded ground use
/// <see cref="Hop"/> instead, which is instant.
/// </summary>
internal sealed class Teleport : Module
{
    public static readonly Teleport I = new();

    /// <summary>A place to return to: where the player stood and which way they faced.</summary>
    public readonly record struct Spot(Vector3 At, float Yaw);

    /// <summary>A surface the player can stand on, and the streamed level it belongs to (null for the persistent map).</summary>
    private readonly record struct Floor(Vector3 At, string? Level);

    private const string Caption = "ORACLE // RELOCATING";
    private const string ToastTitle = "TELEPORT";
    private const string HeldKey = "teleport.held";
    private const float QuickFadeOut = 0.35f;
    private const float QuickFadeIn = 0.6f;
    private const float SlowFadeOut = 1.8f;
    private const float SlowFadeIn = 1.4f;
    private const float SettleTime = 0.5f;
    private const float TextureWait = 3f;
    private const float WatchdogSeconds = 300f;
    private const float VerifyGap = 0.6f;
    private const float ScanHeight = 40000f;
    private const float SlipRange = 1000f;
    private const float ExactAbove = 250f;
    private const float CarLift = 150f;
    private const float UnitsPerMetre = 100f;
    private const float MinUndoMove = 200f;
    private const int MaxHistory = 16;
    private const int MaxSlips = 3;
    private const float BadSpotRadius = 600f;
    private const float WalkableNormal = 0.6f;
    private const float FloorGap = 15f;
    private const float WidthScale = 0.8f;
    private const float WidthTolerance = 40f;
    private const int MaxFloors = 16;
    private const float HintRange = 400f;
    private const float BoxHalf = 4f;
    private const int CheapTraces = 4;
    private const float NoPlace = 500000f;
    private const float StandReach = 150f;
    private const float GroundSearch = 20000f;
    private const float ArrivedRange = 2500f;
    private const float CruiseAbove = 8000f;
    private const float CruiseSpeed = 100f;
    private const float ClimbSpeed = 100f;
    private const float DescendSpeed = 100f;
    private const float SlowSpeed = 40f;
    private const float LookAhead = 12000f;
    private const float BoundaryWait = 30f;
    private const float CaptionEvery = 0.5f;
    private const float FlightLogEvery = 5f;
    private const float ArriveXY = 100f;

    private enum Phase
    {
        Idle,
        FadeOut,
        Fly,
        Land,
        Settle,
        FadeIn,
    }

    private enum Stage
    {
        Climb,
        Cruise,
        Descend,
    }

    private readonly List<Vector3> _bad = new();
    private readonly List<Spot> _history = new();
    private Phase _phase;
    private Stage _stage;
    private float _timer;
    private float _total;
    private Vector3 _target;
    private bool _exact;
    private Rotator? _face;
    private bool _undoing;
    private bool _cancelled;
    private nint _actor;
    private Vector3 _origin;
    private float _originYaw;
    private string _result = "";
    private bool _ok;
    private bool _moved;
    private bool _audioFaded;
    private bool _pausedAnims;
    private AkEvent? _fallLoop;
    private bool _frozen;
    private bool _recover = Shared.GetValue(HeldKey, false);
    private Vector3 _pos;
    private Vector3 _dir;
    private float _cruiseZ;
    private string? _cell;
    private string? _waitCell;
    private float _waitFor;
    private float _caption;
    private float _flightLog;
    private Vector3 _descendTo;
    private Vector3 _landed;
    private bool _checking;
    private int _slips;
    private Choice _fade = null!;
    private Slider _arrival = null!;
    private Toggle _faceTravel = null!;
    private Slider _noFade = null!;

    public bool Busy => _phase != Phase.Idle;

    private bool Slow => _fade.Index == 1;

    public override void Build()
    {
        AddCommand("teleport_waypoint", "TELEPORT TO WAYPOINT", "Set a waypoint on the map (Tab), then run this. Works across islands and in the Batmobile: the screen fades to black while the city loads.", GoToWaypoint).Moves();
        _fade = AddChoice("teleport_fade", "TELEPORT FADE", "QUICK cuts to black at once. SLOW FADE dims the screen to black gently before the trip and fades back in slower.",
            new[] { "quick", "slow" }, new[] { "QUICK", "SLOW FADE" }).Pref();
        _arrival = AddSlider("teleport_arrival_height", "ARRIVAL HEIGHT", "Arrive this high above the ground, ready to glide. OFF lands you on your feet. Not for quick moves or the Batmobile.",
            0f, 0f, 100f, 5f, 30f, Fmt.MetresOrOff).Pref();
        _faceTravel = AddToggle("teleport_face", "FACE TRAVEL DIRECTION", "After a teleport to a place, turn to face the way you travelled instead of the way you were facing.").Pref();
        _noFade = AddSlider("teleport_no_fade_range", "NO FADE RANGE", "Teleports closer than this happen instantly if the ground there is loaded, and keep the menu open. Further ones fade to black for the trip. OFF always fades.",
            150f, 0f, 500f, 25f, 300f, Fmt.MetresOrOff).Pref();
        AddCommand("teleport_back", "GO BACK", "Returns to where you were before your last teleport, facing the same way. Remembers the last 16 and forgets them when a new world loads.", Back).Moves();
        AddCommand("teleport_cancel", "CANCEL TELEPORT", "Stops a long teleport that is still running and sets you down on the nearest ground below. Bind it to a key: the menu closes while teleporting.", Cancel);
    }

    public override void WorldChanged()
    {
        if (_phase != Phase.Idle)
            Safe.Run("transition music", Music.I.EndTransition);
        _phase = Phase.Idle;
        _frozen = false;
        _history.Clear();
        Districts.Forget();
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

        // In the air, cancelling means landing right here.
        if (_phase == Phase.Fly && _stage != Stage.Descend && World.Pawn is { } flier && flier.Ptr == _actor)
        {
            _cancelled = true;
            _target = _pos;
            _exact = false;
            _dir = Vector3.Zero;
            Log.Info("Teleport cancelled by the player, landing here.");
            BeginDescent(flier, 0f);
            return;
        }

        if (World.Pawn is { } mover && mover.Ptr == _actor)
            Retreat(mover);

        Log.Info("Teleport cancelled by the player.");
        Finish("CANCELLED", false);
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
        Settle(mover);

        if (remember)
            Remember(from);

        Log.Info("Hopped to " + Fmt.Position(at) + " from " + Fmt.Position(from.At) + ".");
    }

    /// <summary>
    /// Moves the player to a target. Nearby ground that is already loaded needs no fade; anything further is a
    /// flight behind a fade. An exact target keeps its height, so it can land indoors or under a roof; otherwise
    /// only X and Y count, and the player lands on the highest ground there.
    /// </summary>
    public void Go(Vector3 target, bool exact, Rotator? face = null, bool undoing = false)
    {
        Pawn mover = RequireMover();

        // Some missions park their marker at a dummy point like (-999999, -999999) until it has a real place.
        if (MathF.Abs(target.X) > NoPlace || MathF.Abs(target.Y) > NoPlace)
            throw new InvalidOperationException("that marker has no real location yet");

        _target = target;
        _exact = exact;
        _face = face;
        _undoing = undoing;
        _actor = mover.Ptr;
        _origin = mover.Location;
        _originYaw = mover.Rotation.Yaw;
        _slips = 0;
        _bad.Clear();
        if (ArriveNow(mover))
            return;

        if (World.Ri is { } ri && !InCityMap(_origin))
        {
            Log.Info("Teleport refused: " + Fmt.Position(_origin) + " in " + ri.CurrentLevel + " is outside every city district (streaming offset "
                + Fmt.Position(ri.CurrentLevelStreamingOffset) + ").");
            throw new InvalidOperationException("can't teleport out of this building, leave through its exit first");
        }

        _moved = false;
        _frozen = false;
        _checking = false;
        _cancelled = false;
        _total = 0f;
        _timer = 0f;
        _result = "";
        _phase = Phase.FadeOut;
        Menu.Close();
        float fade = Slow ? SlowFadeOut : QuickFadeOut;
        Fader.Out(fade, Slow, Caption);
        // The game's audio fade silences everything, the teleport sound included, so it only runs without one.
        bool music = Safe.Try("transition music", Music.I.BeginTransition);
        _audioFaded = !music;
        if (_audioFaded)
            Safe.Run("audio fade", () => AudioFade(false, fade));
        Log.Info("Teleport to " + Fmt.Position(_target) + " from " + Fmt.Position(_origin) + ".");
    }

    /// <summary>Skips the fade when the target is within range and the ground there is already loaded.</summary>
    private bool ArriveNow(Pawn mover)
    {
        float range = _noFade.Value * UnitsPerMetre;
        if (range <= 0f || Vector3.Distance(_origin, _target) > range)
            return false;

        if (!TargetGround(mover, out Floor ground))
            return false;

        Hop(ground.At + new Vector3(0f, 0f, Lift(mover)), null, !_undoing);
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
        if (_recover)
            Recover();

        if (_phase == Phase.Idle)
            return;

        Pawn? mover = World.Pawn;
        bool valid = mover != null && mover.Ptr == _actor;
        if (!valid && _phase != Phase.FadeIn)
        {
            Log.Info("Teleport cancelled: the player changed.");
            Finish("", false);
            return;
        }

        _timer += dt;
        _total += dt;
        if (_total > WatchdogSeconds && _phase != Phase.FadeIn)
        {
            Log.Info("Teleport watchdog: stopped after " + (int)_total + " s in " + _phase + ".");
            Retreat(mover!);
            Finish("AS CLOSE AS POSSIBLE", false);
            return;
        }

        switch (_phase)
        {
            case Phase.FadeOut:
                if (Fader.IsDark)
                {
                    _origin = mover!.Location;
                    _originYaw = mover.Rotation.Yaw;
                    Safe.Run("teleport start", () => BeginFlight(mover));
                }

                break;

            case Phase.Fly:
                FlyStep(mover!, dt);
                break;

            case Phase.Land:
                LandStep(mover!);
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
                {
                    _phase = Phase.Idle;
                    Safe.Run("transition music", Music.I.EndTransition);
                }

                break;
        }
    }

    /// <summary>
    /// A hot reload in the middle of a teleport loses this state while the player is still held in mid-air with
    /// physics off. The hold is noted outside the assembly, so the fresh instance can let them fall normally.
    /// </summary>
    private void Recover()
    {
        if (World.Pawn is not { IsValid: true } pawn)
            return;

        _recover = false;
        Shared.Set(HeldKey, false);
        if (pawn is RVehicleBatmobileBase)
            return;

        Log.Info("Teleport: a reload interrupted a teleport, giving the player their physics back.");
        Safe.Run("teleport recover", () => pawn.SetPhysics(Actor.EPhysics.PHYS_Falling, false));
        if (pawn.Mesh is { IsValid: true } mesh)
            Safe.Run("teleport recover", () => mesh.bPauseAnims = false);
    }

    private void Finish(string result, bool ok)
    {
        if (_cancelled && ok)
            result = "CANCELLED";

        _result = result;
        _ok = ok;
        _timer = 0f;
        if (ok)
            _phase = Phase.Settle;
        else
            FadeBack();
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
        float fade = Slow ? SlowFadeIn : QuickFadeIn;
        Fader.In(fade, ToastTitle, _result, _ok);
        if (_audioFaded)
            Safe.Run("audio fade", () => AudioFade(true, fade));
        _audioFaded = false;
    }

    /// <summary>Silences the game behind the black screen, through the same fade its cutscenes use.</summary>
    private static void AudioFade(bool back, float seconds)
    {
        if (World.Ri?.FadeManager is not { IsValid: true } fade)
            return;

        if (back)
            fade.StartAudioFadeIn(seconds);
        else
            fade.StartAudioFadeOut(seconds);
    }

    // ---- flying there

    private void BeginFlight(Pawn mover)
    {
        _moved = true;
        _phase = Phase.Fly;
        _stage = Stage.Climb;
        _timer = 0f;
        _pos = _origin;
        _cruiseZ = Math.Max(_origin.Z, _target.Z) + CruiseAbove;
        _cell = Districts.At(_origin);
        _waitCell = null;
        _waitFor = 0f;
        _caption = 0f;
        _flightLog = 0f;
        var travelled = new Vector3(_target.X - _origin.X, _target.Y - _origin.Y, 0f);
        _dir = travelled.LengthSquared() > 1f ? Vector3.Normalize(travelled) : Vector3.Zero;
        Log.Info("Teleport: flying " + (int)(Geo.DistXY(_origin, _target) / UnitsPerMetre) + " m from " + (_cell ?? "no district")
            + ", cruising at " + (int)_cruiseZ + ".");
    }

    private void FlyStep(Pawn mover, float dt)
    {
        float yaw = _dir == Vector3.Zero ? mover.Rotation.Yaw : Rotator.FromDirection(_dir).Yaw;
        _caption += dt;
        if (_caption >= CaptionEvery)
        {
            _caption = 0f;
            Fader.Cut(Caption + " // " + ((int)(Geo.DistXY(_pos, _target) / UnitsPerMetre)).ToString(CultureInfo.InvariantCulture) + " M"
                + (_waitCell != null ? " // LOADING " + _waitCell.ToUpperInvariant() : ""));
        }

        switch (_stage)
        {
            case Stage.Climb:
                _pos.Z = Math.Min(_cruiseZ, _pos.Z + ClimbSpeed * UnitsPerMetre * dt);
                Fly(mover, _pos, yaw);
                if (_pos.Z >= _cruiseZ)
                    _stage = Stage.Cruise;
                break;

            case Stage.Cruise:
                Cruise(mover, dt, yaw);
                break;

            case Stage.Descend:
                _pos.Z = Math.Max(_descendTo.Z, _pos.Z - DescendSpeed * UnitsPerMetre * dt);
                Fly(mover, _pos, yaw);
                if (_pos.Z <= _descendTo.Z)
                {
                    _target = _descendTo;
                    StartLand();
                }

                break;
        }
    }

    /// <summary>
    /// Flies straight at the target, entering each district as its edge is crossed. The district ahead gets a
    /// slower approach while it loads, and a wait at its edge if it still isn't in.
    /// </summary>
    private void Cruise(Pawn mover, float dt, float yaw)
    {
        float left = Geo.DistXY(_pos, _target);
        if (left <= ArriveXY)
        {
            _pos.X = _target.X;
            _pos.Y = _target.Y;
            Fly(mover, _pos, yaw);
            BeginDescent(mover, dt);
            return;
        }

        float step = Math.Min(CruiseSpeed * UnitsPerMetre * dt, left);
        Vector3 next = _pos + _dir * step;
        string? cell = Districts.At(next);
        if (cell != null && !cell.Equals(_cell, StringComparison.OrdinalIgnoreCase))
        {
            if (!Ready(mover, cell, next))
            {
                Wait(cell, dt);
                if (_waitFor < BoundaryWait)
                    return;

                Log.Info("Teleport: " + cell + " still isn't loaded after " + Seconds(_waitFor) + ", crossing into it anyway.");
            }
            else if (_waitCell != null)
            {
                Log.Info("Teleport: " + cell + " loaded after a wait of " + Seconds(_waitFor) + ".");
            }

            _waitCell = null;
            _waitFor = 0f;
            _cell = cell;
            Safe.Run("district entry", () => Enter(cell));
            Log.Info("Teleport: crossed into " + cell + ", " + (int)(left / UnitsPerMetre) + " m to go.");
        }
        else
        {
            string? ahead = Districts.At(_pos + _dir * LookAhead);
            if (ahead != null && !ahead.Equals(_cell, StringComparison.OrdinalIgnoreCase) && World.Ri is { } ri && !Loaded(ri, ahead))
                step = Math.Min(step, SlowSpeed * UnitsPerMetre * dt);
        }

        _pos += _dir * step;
        Fly(mover, _pos, yaw);

        _flightLog += dt;
        if (_flightLog >= FlightLogEvery)
        {
            _flightLog = 0f;
            Log.Info("Teleport: en route, " + (int)(Geo.DistXY(_pos, _target) / UnitsPerMetre) + " m to go, over " + (_cell ?? "no district") + ".");
        }
    }

    /// <summary>Holds position at a district's edge until the game has it loaded.</summary>
    private void Wait(string cell, float dt)
    {
        if (_waitCell == null || !_waitCell.Equals(cell, StringComparison.OrdinalIgnoreCase))
        {
            _waitCell = cell;
            _waitFor = 0f;
            Log.Info("Teleport: waiting at the edge of " + cell + " for it to load.");
        }

        _waitFor += dt;
    }

    private static bool Loaded(RGameRI ri, string cell)
    {
        var name = new FName(cell);
        return ri.IsLevelLoaded(name, true) || ri.IsLevelVisible(name, true);
    }

    /// <summary>True when a district can be flown into: the game says it is in, or there is ground at the crossing point.</summary>
    private bool Ready(Pawn mover, string cell, Vector3 at) =>
        World.Ri is not { } ri || Loaded(ri, cell) || GroundAt(mover, at.X, at.Y, out _);

    private void BeginDescent(Pawn mover, float dt)
    {
        float lift = Lift(mover);
        if (TargetGround(mover, out Floor floor) || NearestGround(mover, out floor))
        {
            _descendTo = floor.At + new Vector3(0f, 0f, lift);
            _stage = Stage.Descend;
            _waitCell = null;
            Log.Info("Teleport: above the target, descending to " + Fmt.Position(floor.At) + ".");
            return;
        }

        // No ground yet: the target's own district may still be loading, so hover and give it time.
        string cell = _cell ?? Districts.At(_target) ?? "";
        if (cell.Length > 0)
            Wait(cell, dt);
        else
            _waitFor += dt;

        if (_waitFor < BoundaryWait)
            return;

        Log.Info("Teleport: no ground appeared under the target after " + Seconds(_waitFor) + ".");
        Retreat(mover);
        Finish("NO GROUND THERE", false);
    }

    /// <summary>Moves the flying pawn to a point, facing the way it flies. Physics is off for the whole flight.</summary>
    private void Fly(Pawn mover, Vector3 at, float yaw)
    {
        var rot = new Rotator(0, yaw, 0);
        if (!_frozen)
        {
            _frozen = true;
            Shared.Set(HeldKey, true);
            mover.SetPhysics(Actor.EPhysics.PHYS_None, false);
            // A still Batman makes no cape or foley sound on the way; the animation comes back on landing.
            if (mover is RPawnPlayer && mover.Mesh is { IsValid: true } mesh && !mesh.bPauseAnims)
            {
                mesh.bPauseAnims = true;
                _pausedAnims = true;
            }

            // The game's falling state puts the wind-rush and cape loop back on him every tick it finds it
            // missing, so the loop is taken off and the sound it would use is blanked until he lands.
            if (mover is RPawnPlayer combat && combat.FallLoopSound is { IsValid: true } loop)
            {
                _fallLoop = loop;
                Safe.Run("fall loop", () => combat.RemoveLoopingAudioEvent(loop));
                combat.FallLoopSound = null!;
            }
        }

        if (mover is RVehicleBatmobileBase car)
        {
            car.SetLocation(at);
            car.SetRotation(rot);
            car.Velocity = Vector3.Zero;
            if (car.Mesh is { IsValid: true } mesh)
            {
                mesh.SetRBPosition(at);
                mesh.SetRBRotation(rot);
            }

            return;
        }

        Place(mover, at);
        mover.SetRotation(rot);
    }

    private static string Seconds(float t) => t.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// Tells the game the player is in a district, the way its own volumes do when the player glides across a
    /// boundary: the district becomes current, its neighbours stream in, and the old one is let go.
    /// </summary>
    private static void Enter(string? level)
    {
        if (World.Ri is not { } ri || level == null || !IsCity(level) || ri.CurrentLevel.ToString().Equals(level, StringComparison.OrdinalIgnoreCase))
            return;

        RLevelVolumeOW? volume = Districts.Volume(level);
        RLevelVolume? old = ri.CurrentLevelVolume is { IsValid: true } v ? v : null;
        if (volume != null)
        {
            ri.RemovePredictedLevel();
            volume.EnteredLevelVolume();
            if (old != null && old.Ptr != volume.Ptr)
                old.LevelVolumeDeactivated();
        }
        else
        {
            ri.SetCurrentLevel(new FName(level), ri, false);
        }

        Log.Detail("Teleport: entered " + level + (volume != null ? " through its volume." : "."));
    }

    private static bool IsCity(string level) => level.StartsWith("City", StringComparison.OrdinalIgnoreCase);

    // ---- landing

    private void StartLand()
    {
        _phase = Phase.Land;
        _timer = 0f;
        _checking = false;
    }

    /// <summary>
    /// Puts the player on a floor at the target, or the nearest one, then checks a moment later that it held.
    /// Every floor comes from a loaded level, and the one landed on becomes the player's district.
    /// </summary>
    private void LandStep(Pawn mover)
    {
        if (_checking)
        {
            if (_timer < VerifyGap)
                return;

            _checking = false;
            bool slipped = Geo.DistXY(mover.Location, _landed) > SlipRange || mover.Location.Z < _landed.Z - 150f;
            if (!slipped && (mover is RVehicleBatmobileBase || Standing(mover)))
            {
                float off = Geo.DistXY(_landed, _target);
                Log.Info("Teleported onto " + Fmt.Position(_landed) + " after " + Seconds(_total) + ", " + (int)off + " units from the target.");
                Finish(off <= ArrivedRange ? "DONE" : "AS CLOSE AS POSSIBLE", true);
                return;
            }

            _slips++;
            _bad.Add(_landed);
            Log.Info("Teleport: the floor at " + Fmt.Position(_landed) + " didn't hold (" + (slipped ? "slipped" : "nothing underfoot") + ").");
            if (_slips >= MaxSlips)
            {
                Retreat(mover);
                Finish("NO GROUND THERE", false);
                return;
            }
        }

        float lift = Lift(mover);
        if (TargetGround(mover, out Floor floor) && !IsBad(floor.At))
        {
            Land(mover, floor, lift);
            return;
        }

        if (NearestGround(mover, out Floor near))
        {
            Log.Info("Teleport: no floor right at the target, landing on the nearest ground at " + Fmt.Position(near.At) + ".");
            Land(mover, near, lift);
            return;
        }

        Log.Info("Teleport: no ground loaded anywhere near the target, stopping on the nearest ground there is.");
        Retreat(mover);
        Finish("NO GROUND THERE", false);
    }

    private void Land(Pawn mover, Floor floor, float lift)
    {
        Vector3 at = floor.At + new Vector3(0f, 0f, lift);
        Release(mover);
        Place(mover, at);
        _moved = true;
        _landed = at;
        _checking = true;
        _timer = 0f;
        Safe.Run("district entry", () => Enter(floor.Level ?? _cell));
        Settle(mover);
    }

    /// <summary>
    /// What the game's own checkpoints do after moving the player: a clean standing pose and a fresh start.
    /// Without it, a player moved (or frozen and let go) mid-move can be left in a pose where WASD does nothing.
    /// <paramref name="moved"/> false is for a player let go where he was, who only needs the pose.
    /// </summary>
    public static void Settle(Pawn mover, bool moved = true)
    {
        if (mover is RPawnPlayer batman)
            Safe.Run("player pose", () => batman.ForceChangePose(new FName("Standing"), new FName("Relaxed"), default, false, default, default, default, 0.001f));
        if (moved)
            Safe.Run("player moved", () => World.Pc?.OnPlayerHasBeenMoved());
    }

    /// <summary>
    /// Puts the player back on solid ground after a failure. Before they have moved that is simply the start.
    /// After, the start's district may already be unloading, so the nearest loaded ground wins.
    /// </summary>
    private void Retreat(Pawn mover)
    {
        Release(mover);
        if (!_moved)
        {
            Place(mover, _origin);
            Settle(mover);
            return;
        }

        float lift = Lift(mover);
        Vector3 at = mover.Location;
        if (GroundAt(mover, at.X, at.Y, out Floor under) || NearestGround(mover, out under))
        {
            Place(mover, under.At + new Vector3(0f, 0f, lift));
            Settle(mover);
            Log.Info("Teleport: stopped on the nearest loaded ground at " + Fmt.Position(under.At) + ".");
            return;
        }

        Log.Info("Teleport: no loaded ground anywhere near, back to the start.");
        Place(mover, _origin);
        Settle(mover);
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

    /// <summary>True once the game has streamed in the full-detail textures around the player.</summary>
    private static bool TexturesDone() =>
        World.Ri is not { } ri || Safe.Try("texture streaming", ri.HasAllTextureStreamingCompleted, fallback: true);

    // ---- finding floors

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
    private bool TargetGround(Pawn mover, out Floor hit)
    {
        List<Floor> all = Floors(mover, _target.X, _target.Y, ScanTop(mover), ScanBottom(mover), MaxFloors, MaxFloors * 2);
        List<Floor> fit = all.FindAll(f => Fits(mover, f.At));
        List<Floor> floors = fit.Count > 0 ? fit : all;
        hit = default;
        if (floors.Count == 0)
            return false;

        hit = _exact ? FloorUnder(floors, _target.Z) : FloorNear(floors, _target.Z);
        return true;
    }

    /// <summary>The nearest floor at or just below a height, or the lowest one above it if there is none.</summary>
    private static Floor FloorUnder(List<Floor> floors, float z)
    {
        foreach (Floor f in floors)
        {
            if (f.At.Z <= z + ExactAbove)
                return f;
        }

        return floors[^1];
    }

    /// <summary>The floor closest to a height when one is close to it, otherwise the highest.</summary>
    private static Floor FloorNear(List<Floor> floors, float z)
    {
        Floor best = floors[0];
        float bestGap = float.MaxValue;
        foreach (Floor f in floors)
        {
            float gap = MathF.Abs(f.At.Z - z);
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
    private bool GroundAt(Pawn mover, float x, float y, out Floor hit)
    {
        // The ring searches call this thousands of times a step, so it may only look past a few surfaces.
        List<Floor> floors = Floors(mover, x, y, ScanTop(mover), ScanBottom(mover), 1, CheapTraces);
        hit = floors.Count > 0 ? floors[0] : default;
        return floors.Count > 0;
    }

    /// <summary>Rings of ever wider radius around the target, for the closest ground when the spot itself has none.</summary>
    private bool NearestGround(Pawn mover, out Floor ground)
    {
        for (float r = 500f; r <= GroundSearch; r += r < 9000f ? 500f : 1500f)
        {
            int steps = Math.Clamp((int)(r / 250f), 8, 96);
            for (int i = 0; i < steps; i++)
            {
                float a = MathF.Tau * i / steps;
                if (GroundAt(mover, _target.X + MathF.Cos(a) * r, _target.Y + MathF.Sin(a) * r, out ground) && !IsBad(ground.At))
                    return true;
            }
        }

        ground = default;
        return false;
    }

    /// <summary>
    /// Surfaces on a vertical line, top first, each with the level it belongs to. Looks past what can't be
    /// stood on: invisible blocking volumes, which the city uses to fence off roofs and the sky, other pawns,
    /// walls or slopes that are too steep, and the low-detail stand-in scenery of districts not fully loaded.
    /// </summary>
    private static List<Floor> Floors(Pawn mover, float x, float y, float top, float bottom, int max, int traces)
    {
        var found = new List<Floor>();
        float z = top;
        for (int i = 0; i < traces && found.Count < max && z > bottom; i++)
        {
            Actor? what = mover.Trace(out Vector3 hit, out Vector3 normal, new Vector3(x, y, bottom), new Vector3(x, y, z),
                Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
            if (what == null || hit == Vector3.Zero)
                break;

            // A trace that starts inside solid geometry can stop where it began, so step well clear of it.
            z = hit.Z >= z - 1f ? z - 200f : hit.Z - FloorGap;
            if (what is Volume || what is Pawn || normal.Z < WalkableNormal)
                continue;

            string? level = LevelOf(what);
            if (IsStandIn(what, level))
                continue;

            found.Add(new Floor(hit, level));
        }

        return found;
    }

    /// <summary>The streamed level an actor was loaded with, or null for the persistent map.</summary>
    private static string? LevelOf(Actor what)
    {
        try
        {
            if (World.Ri is not { } ri)
                return null;

            LevelStreaming? ls = ri.GetLevelForObject(what);
            return ls is { IsValid: true } ? ls.PackageName.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True for the low-detail stand-in scenery the game shows for districts that aren't fully loaded. It lives
    /// in levels named like CityZ_19_Static_LOD1; landing on it means blurry surroundings and no real collision.
    /// </summary>
    private static bool IsStandIn(Actor what, string? level)
    {
        if (level != null)
            return level.Contains("_LOD", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>True when there is something solid just under the pawn's feet, not just a hold in mid-air.</summary>
    private static bool Standing(Pawn mover)
    {
        Vector3 at = mover.Location;
        return Ray(mover, at, at - new Vector3(0f, 0f, mover.GetCollisionHeight() + StandReach), out _, out _);
    }

    // ---- moving the pawn

    /// <summary>
    /// True when the player is in the open city's own map. Interiors like the GCPD lockup are separate persistent
    /// maps at their own offset, outside every city district, and only the game's own doors switch back from one.
    /// </summary>
    internal static bool InCityMap(Vector3 at) => Districts.At(at) != null;

    /// <summary>
    /// How far the game has shifted the whole city right now. After an interior like the GCPD the city can sit
    /// 100000 units higher than its own coordinates. Map places, districts and saved spots are kept unshifted,
    /// so they add this before use and take it off when stored.
    /// </summary>
    public static Vector3 Offset => World.Ri?.CurrentLevelStreamingOffset ?? Vector3.Zero;

    /// <summary>Gives a flying player normal physics back, falling, so they settle onto whatever is below.</summary>
    private void Release(Pawn mover)
    {
        if (!_frozen)
            return;

        _frozen = false;
        Shared.Set(HeldKey, false);
        if (_pausedAnims)
        {
            _pausedAnims = false;
            if (mover.IsValid && mover.Mesh is { IsValid: true } mesh)
                Safe.Run("teleport anims", () => mesh.bPauseAnims = false);
        }

        if (_fallLoop is { } fall)
        {
            _fallLoop = null;
            if (mover.IsValid && mover is RPawnPlayer combat && fall.IsValid)
                Safe.Run("fall loop", () => combat.FallLoopSound = fall);
        }

        if (mover is not RVehicleBatmobileBase && mover.IsValid)
            Safe.Run("teleport release", () => mover.SetPhysics(Actor.EPhysics.PHYS_Falling, false));
    }

    /// <summary>
    /// Moves a pawn and stops it dead. A character is moved the way the game's own checkpoints move it, ignoring
    /// collision so a tight spot can't refuse the move. A vehicle's rigid body has to be moved too or it snaps
    /// straight back.
    /// </summary>
    public static void Place(Pawn mover, Vector3 at, Rotator? facing = null)
    {
        if (mover is RVehicleBatmobileBase car)
        {
            PlaceCar(car, at, facing ?? car.Rotation);
            return;
        }

        if (mover is RPawn pawn)
            pawn.SetLocationIgnoringCollision(at);
        else
            mover.SetLocation(at);

        mover.Velocity = Vector3.Zero;
    }

    private static void PlaceCar(RVehicleBatmobileBase car, Vector3 at, Rotator facing)
    {
        var upright = new Rotator(0, facing.Yaw, 0);
        car.SetPhysics(Actor.EPhysics.PHYS_None, false);
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

        car.SetPhysics(Actor.EPhysics.PHYS_RigidBody, false);
        car.TeleportCleanup(at, upright);
    }
}
