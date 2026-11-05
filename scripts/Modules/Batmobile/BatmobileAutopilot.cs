using System.Globalization;
using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using PickupMode = BmSDK.BmGame.RVehicleBatmobileBase.EBatmobilePickupMode;
using SelfDrive = BmSDK.BmGame.RVehicle.ESelfDriveMode;

namespace CeZeY.Oracle;

/// <summary>
/// AUTOPILOT: the Batmobile drives you along the roads, using the game's own route planner and self-drive, the
/// ones that bring the car when you call it. PlanBatmobileRoute only plans for an empty car (Driver == None), so
/// the driver is cleared for that one call. ExecuteBatmobileRoute then builds the spans and racing line and starts
/// the drive, and bControlledBySeqAct marks it the way the game's own Kismet drive-route action does.
/// </summary>
internal sealed partial class Batmobile
{
    // How far from the destination the route may end on a road, tried in turn (units: 30 m, 100 m, 300 m).
    private static readonly float[] RoadSearch = { 3000f, 10000f, 30000f };
    private const float AnyDistance = 1000000f;
    private const float ExactHeightRange = 2000f;
    private const float ArrivedWithin = 4000f;
    private const double StartGrace = 1.0;
    private const double ReportGap = 3.0;

    // Pulling up: self-drive lets go 15-20 m short of the route's end without braking, so Oracle brakes the car to a
    // stop there itself, hard enough to stop by the end (between these, in units per second squared).
    private const float BrakeMin = 800f;
    private const float BrakeMax = 3000f;
    private const float BrakeRoomMin = 300f;
    private const float StoppedSpeed = 60f;
    private const double BrakeFor = 4.0;

    private readonly Held<RVehicleBatmobileBase, bool> _pilotBoost = new("autopilot boost", b => b.SelfDriveAllowBoost, (b, v) => b.SelfDriveAllowBoost = v);

    private Toggle _autoBoost = null!;
    private RVehicleBatmobileBase? _pilotCar;
    private Vector3 _pilotGoal;
    private string _pilotTarget = "";
    private double _pilotSince;
    private Every _pilotReport;
    private RVehicleBatmobileBase? _brakeCar;
    private double _brakeSince;

    private bool Piloting => _pilotCar is { IsValid: true };

    private void BuildAutopilot()
    {
        AddCommand("autopilot_waypoint", "AUTOPILOT TO WAYPOINT", "The Batmobile drives you to your map waypoint along the roads, the way it drives itself when you call it. Run it again, or STOP AUTOPILOT, to take the wheel back.",
            () => ToggleAutopilot("WAYPOINT", Waypoint));
        AddCommand("autopilot_objective", "AUTOPILOT TO OBJECTIVE", "The Batmobile drives you to the current mission marker along the roads. Run it again, or STOP AUTOPILOT, to take the wheel back.",
            () => ToggleAutopilot("OBJECTIVE", ObjectiveSpot));
        AddCommand("autopilot_stop", "STOP AUTOPILOT", "Takes the wheel back from the autopilot.", () =>
        {
            if (!Piloting)
                throw new InvalidOperationException("the autopilot isn't driving");
            StopAutopilot("YOU HAVE THE WHEEL");
        });
        _autoBoost = AddToggle("autopilot_boost", "AUTOPILOT CAN BOOST", "Lets the autopilot use the afterburner on the straights.").Pref();
    }

    private void ToggleAutopilot(string what, Func<(Vector3 At, bool Exact)> target)
    {
        if (Piloting)
        {
            StopAutopilot("YOU HAVE THE WHEEL");
            return;
        }

        StartAutopilot(what, target());
    }

    private static (Vector3 At, bool Exact) Waypoint()
    {
        RPersistentData pd = World.Info?.PersistentData ?? throw new InvalidOperationException("the game is still loading");
        if (!pd.IsValid || !pd.GetCustomWaypointEnabled())
            throw new InvalidOperationException("no waypoint is set, pick one on the map first");

        // A pin dropped on the map has a made-up height; any other waypoint sits on the real spot.
        return (pd.GetCustomWaypoint(true), pd.CustomWaypoint_Type != RPersistentData.ECustomWayPointType.CustomWayType_Proximity);
    }

    private static (Vector3 At, bool Exact) ObjectiveSpot()
    {
        RGameInfo info = World.Info ?? throw new InvalidOperationException("the game is still loading");
        return (TeleportTools.Objective(info) ?? throw new InvalidOperationException("no objective is active"), true);
    }

    private void StartAutopilot(string what, (Vector3 At, bool Exact) target)
    {
        if (World.Pawn is not RVehicleBatmobileBase car)
            throw new InvalidOperationException("get in the Batmobile first");
        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        if (car.bControlledBySeqAct || car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
            throw new InvalidOperationException("the game is already driving the Batmobile");
        if (Safe.Try("battle mode check", car.IsInBattleMode))
            throw new InvalidOperationException("switch out of battle mode first");

        float heightRange = target.Exact ? ExactHeightRange : AnyDistance;
        int start = -1;
        float reach = 0f;
        Vector3 end = default;
        Pawn? driver = car.Driver;
        try
        {
            car.Driver = null!;
            foreach (float search in RoadSearch)
            {
                reach = search;
                start = pc.PlanBatmobileRoute(target.At, false, AnyDistance, out end, search, heightRange, false, Vector3.Zero, null!);
                // Only "no road near the destination" can get better with a wider search.
                if (start != -1)
                    break;
            }
        }
        finally
        {
            car.Driver = driver!;
        }

        Log.Info("AUTOPILOT: planning to the " + what.ToLowerInvariant() + " at " + Fmt.Position(target.At) + (target.Exact ? "" : " (map pin)") +
            " gave " + start + " with a road search of " + Fmt.Metres(reach / 100f).ToLowerInvariant() + ".");
        if (start < 0)
            throw new InvalidOperationException(RouteRefusal(start));

        pc.ExecuteBatmobileRoute(start, end, PickupMode.BPM_None, false);
        if (car.SelfDriveMode == SelfDrive.SELFDRIVE_None)
            throw new InvalidOperationException("the game wouldn't start driving");

        car.bControlledBySeqAct = true;
        if (_autoBoost.On)
            _pilotBoost.Set(car, true);

        _brakeCar = null;
        _pilotCar = car;
        _pilotGoal = end;
        _pilotTarget = what;
        _pilotSince = Clock.Now;
        _pilotReport = default;
        Log.Info("AUTOPILOT: driving, " + car.SelfDriveRouteSpans.Count + " route spans, the route ends at " + Fmt.Position(end) + ", " +
            Distance(Geo.DistXY(car.Location, end)).ToLowerInvariant() + " away in a straight line and " + Distance(Geo.DistXY(end, target.At)).ToLowerInvariant() + " from the " + what.ToLowerInvariant() + ".");
        Overlay.Notice("AUTOPILOT", "DRIVING TO " + what, true);
    }

    /// <summary>PlanBatmobileRoute's failure codes, read from its bytecode.</summary>
    private static string RouteRefusal(int code) => code switch
    {
        -1 => "there's no road near the Batmobile, or within 300 m of the destination",
        -2 => "the roads don't connect to there",
        -3 => "the winch is holding the Batmobile",
        -4 => "the roads don't come close enough to the destination",
        -5 => "the destination is inside a heavy tank's zone",
        _ => "the game couldn't plan a route (" + code + ")",
    };

    private void AutopilotTick(float dt)
    {
        if (_brakeCar != null)
            BrakeTick(dt);
        if (_pilotCar == null)
            return;

        RVehicleBatmobileBase car = _pilotCar;
        if (!car.IsValid || World.Pawn is not { } pawn || pawn.Ptr != car.Ptr)
        {
            StopAutopilot(null);
            Log.Info("AUTOPILOT: stopped, you left the Batmobile.");
            return;
        }

        float left = Geo.DistXY(car.Location, _pilotGoal);
        if (car.SelfDriveMode == SelfDrive.SELFDRIVE_None && Clock.Now - _pilotSince > StartGrace)
        {
            Log.Info("AUTOPILOT: the game finished driving to the " + _pilotTarget.ToLowerInvariant() + ", " + Distance(left).ToLowerInvariant() + " from the end of the route.");
            bool arrived = left < ArrivedWithin;
            StopAutopilot(arrived ? "ARRIVED" : "STOPPED SHORT");
            if (arrived)
                StartBrake(car);
            return;
        }

        if (Safe.Try("battle mode check", car.IsInBattleMode))
        {
            StopAutopilot("BATTLE MODE, YOU HAVE THE WHEEL");
            return;
        }

        if (_pilotReport.Elapsed(ReportGap))
        {
            Log.Info("AUTOPILOT: " + Speed(car.Velocity.Length()).ToLowerInvariant() + ", " + car.SelfDriveMode + ", span " + car.SelfDriveNextSpan + " of " +
                car.SelfDriveRouteSpans.Count + ", " + Distance(left).ToLowerInvariant() + " to go.");
        }
    }

    private void StartBrake(RVehicleBatmobileBase car)
    {
        _brakeCar = car;
        _brakeSince = Clock.Now;
        // The game's own hard stop: brake lights and its timed brake force, when the car is on the ground and moving fast.
        Safe.Run("emergency brake", car.EmergencyBrake);
        Log.Info("AUTOPILOT: pulling up from " + Speed(car.Velocity.Length()).ToLowerInvariant() + ".");
    }

    /// <summary>Slows the car along its own direction of travel, so it stops at the end of the route instead of coasting past.</summary>
    private void BrakeTick(float dt)
    {
        RVehicleBatmobileBase? car = _brakeCar;
        if (car is not { IsValid: true } || World.Pawn is not { } pawn || pawn.Ptr != car.Ptr || Clock.Now - _brakeSince > BrakeFor ||
            car.CollisionComponent is not { IsValid: true } body)
        {
            _brakeCar = null;
            return;
        }

        Vector3 flat = new(car.Velocity.X, car.Velocity.Y, 0f);
        float speed = flat.Length();
        if (speed < StoppedSpeed)
        {
            Log.Info("AUTOPILOT: stopped " + Distance(Geo.DistXY(car.Location, _pilotGoal)).ToLowerInvariant() + " from the end of the route.");
            _brakeCar = null;
            return;
        }

        // Stop by the end of the route; once past it, stop as hard as allowed.
        Vector3 toGoal = _pilotGoal - car.Location;
        bool ahead = Vector3.Dot(flat, new Vector3(toGoal.X, toGoal.Y, 0f)) > 0f;
        float room = MathF.Max(Geo.DistXY(car.Location, _pilotGoal), BrakeRoomMin);
        float decel = ahead ? Math.Clamp(speed * speed / (2f * room), BrakeMin, BrakeMax) : BrakeMax;
        body.SetRBLinearVelocity(-flat / speed * MathF.Min(decel * dt, speed), true);
    }

    private void StopAutopilot(string? notice)
    {
        RVehicleBatmobileBase? car = _pilotCar;
        _pilotCar = null;
        _brakeCar = null;
        _pilotBoost.Restore();
        if (car is { IsValid: true })
        {
            Safe.Run("autopilot stop", () =>
            {
                if (car.SelfDriveMode != SelfDrive.SELFDRIVE_None)
                    car.CancelSelfDrive();
                car.bControlledBySeqAct = false;
            });
        }

        if (notice == null)
            return;

        Log.Info("AUTOPILOT: " + notice.ToLowerInvariant() + ".");
        Overlay.Notice("AUTOPILOT", notice, notice == "ARRIVED");
    }

    /// <summary>What's left to drive, for the speedometer title.</summary>
    private string AutopilotTitle(RVehicleBatmobileBase car) => "AUTOPILOT " + Distance(Geo.DistXY(car.Location, _pilotGoal));

    /// <summary>A distance in the speedometer's unit family: miles with MPH, kilometres with KM/H, metres when close.</summary>
    private string Distance(float units)
    {
        bool kmh = _units.Index == 1;
        float metres = units / 100f;
        if (kmh)
            return metres < 1000f ? MathF.Round(metres).ToString(CultureInfo.InvariantCulture) + " M" : (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " KM";

        float miles = metres / 1609.344f;
        return miles < 0.1f ? MathF.Round(metres * 1.0936f).ToString(CultureInfo.InvariantCulture) + " YD" : miles.ToString("0.0", CultureInfo.InvariantCulture) + " MI";
    }
}
