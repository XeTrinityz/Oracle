using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// THREAT ESP: a marker over every militia threat that is loaded around you, the same things SHOW DISTRICT THREATS
/// counts. The counts cover the whole island, but only what has streamed in nearby exists as an actor, so far-off ones
/// show up as you get closer. Each kind is found the way the game itself keeps it:
/// - watchtowers are RTurret_WatchtowerBase, gone for good once their destroyedFlagString global flag is set;
/// - checkpoints are ROverworldPopulationVolumes that hold an RCheckpointAsset (ThreatAsset);
/// - beacons are RCommandBeaconBase (IsBeaconDestroyed);
/// - air drones are RVehicleFlying, drone tanks RVehicleTank, heavy tanks RVehicleHeavyTank;
/// - humvees and APCs are RVehicleCars built from the Car_Archetypes.Humvee / .APC templates.
/// RVehicleStaticTank is parked set dressing, and a tank with no behaviour is asleep (or held by a mission): both are
/// shown dimmed, so a tank that never moves can be told apart from a live one.
/// </summary>
internal sealed partial class Population
{
    private const float Metre = 100f;
    private const double ScanSeconds = 1.0;
    private const double FlagSeconds = 5.0;
    private const float LabelRange = 25000f;
    private const float NearDist = 1500f;
    private const float FarDist = 30000f;

    private enum Kind { Watchtower, Checkpoint, Beacon, AirDrone, DroneTank, HeavyTank, Humvee, Apc }

    private readonly record struct Mark(Actor Actor, Kind Kind, bool Dim, string Note, float Lift);

    private static readonly Rgba ColWatchtower = new(255, 196, 64);
    private static readonly Rgba ColCheckpoint = new(255, 120, 48);
    private static readonly Rgba ColBeacon = new(190, 120, 255);
    private static readonly Rgba ColAir = new(80, 210, 255);
    private static readonly Rgba ColTank = new(255, 72, 72);
    private static readonly Rgba ColHeavy = new(220, 30, 60);
    private static readonly Rgba ColCar = new(240, 230, 110);
    private static readonly Rgba ColDim = new(120, 130, 132);

    private Toggle _esp = null!;
    private Slider _espRange = null!;
    private readonly List<Mark> _marks = new();
    private readonly Dictionary<nint, Kind?> _carKinds = new();
    private readonly Dictionary<nint, (bool Gone, double At)> _towerGone = new();
    private Every _scan;

    private void BuildEsp()
    {
        _esp = AddToggle("city_threat_esp", "THREAT ESP", "A marker over each militia threat loaded around you: watchtowers, checkpoints, beacons, air drones, drone tanks, heavy tanks, humvees and APCs, with its distance and, for vehicles, its health. Far-off ones appear as the city streams them in. Grey ones are parked or asleep and won't fight until the game wakes them.",
            on => { if (!on) ForgetEsp(); });
        _espRange = AddSlider("city_threat_esp_range", "THREAT ESP RANGE", "How far away THREAT ESP marks things.", 400f, 50f, 1500f, 50f, 400f, Fmt.Metres).Pref();
    }

    private void ForgetEsp()
    {
        _marks.Clear();
        _carKinds.Clear();
        _towerGone.Clear();
        _scan.Force();
    }

    // ---- finding them

    private void ScanEsp()
    {
        _marks.Clear();
        if (World.Pawn is not { } me)
            return;

        Vector3 at = me.Location;
        float range = _espRange.Value * Metre;
        bool Near(Actor a) => Vector3.Distance(a.Location, at) <= range;

        RFlagManager? flags = (World.Info?.WorldInfo.GRI as RGameRI)?.FlagManager;
        foreach (RTurret_WatchtowerBase t in World.All<RTurret_WatchtowerBase>())
        {
            if (t.bDeleteMe || t.bHidden || !Near(t) || TowerGone(t, flags))
                continue;
            bool jammed = t.bDisabled || t.bJammed;
            _marks.Add(new Mark(t, Kind.Watchtower, jammed, jammed ? "JAMMED" : "", 120f));
        }

        foreach (RCommandBeaconBase b in World.All<RCommandBeaconBase>())
        {
            if (b.bDeleteMe || b.bHidden || !Near(b))
                continue;
            if (Safe.Try("beacon state", () => b.IsBeaconDestroyed()))
                continue;
            _marks.Add(new Mark(b, Kind.Beacon, false, "", 450f));
        }

        foreach (ROverworldPopulationVolume v in World.All<ROverworldPopulationVolume>())
        {
            if (v.bDeleteMe || v.ThreatAsset is not { IsValid: true } asset || asset.bDoesntCountTowardsThreat || !Near(v))
                continue;
            if (Safe.Try("checkpoint state", () => asset.IsDestroyed()))
                continue;
            _marks.Add(new Mark(v, Kind.Checkpoint, false, "", 300f));
        }

        foreach (RVehicleNPC v in World.All<RVehicleNPC>())
        {
            if (v.bDeleteMe || v.bHidden || v.Health <= 0 || v.DroneHacked || v.IsFriendly || v.CivilianVehicle || !Near(v))
                continue;
            // A wreck the game has hidden (HideTankAndDisableCollisionOnDeath hides the mesh, not the actor), or a leftover
            // with no visible body: invisible markers in thin air were seen after a spawning session (2026-11-06).
            if (v.bIsTemporarilyHidden || v.Mesh is not { IsValid: true, HiddenGame: false, bAttached: true })
                continue;
            if (VehicleKind(v) is not { } kind)
                continue;

            bool parked = v is RVehicleStaticTank;
            bool asleep = !parked && kind is Kind.DroneTank or Kind.HeavyTank && v.CurrentBehaviour is not { IsValid: true };
            _marks.Add(new Mark(v, kind, parked || asleep, parked ? "PARKED" : asleep ? "ASLEEP" : "", kind == Kind.AirDrone ? 150f : 320f));
        }
    }

    /// <summary>A destroyed watchtower stays in the world; its global flag says it's gone. Asked every few seconds per tower.</summary>
    private bool TowerGone(RTurret_WatchtowerBase t, RFlagManager? flags)
    {
        if (flags is not { IsValid: true })
            return false;
        if (_towerGone.TryGetValue(t.Ptr, out var known) && Clock.Now - known.At < FlagSeconds)
            return known.Gone;

        string name = t.destroyedFlagString.ToString() ?? "";
        bool gone = name.Length > 0 && Safe.Try("watchtower flag", () => flags.GetGlobalFlag(new FString(name)));
        _towerGone[t.Ptr] = (gone, Clock.Now);
        return gone;
    }

    private Kind? VehicleKind(RVehicleNPC v)
    {
        switch (v)
        {
            case RVehicleHeavyTank:
                return Kind.HeavyTank;
            case RVehicleTank:
                return Kind.DroneTank;
            case RVehicleFlying:
                return Kind.AirDrone;
            case RVehicleCar car:
                if (_carKinds.TryGetValue(car.Ptr, out Kind? known))
                    return known;
                // The template's FName, read once per car: Humvee, Humvee_Interrogation, APC...
                string arch = car.ObjectArchetype is { IsValid: true } a ? a.Name.ToString() : "";
                Kind? kind = arch.StartsWith("Humvee", StringComparison.OrdinalIgnoreCase) ? Kind.Humvee
                    : arch.StartsWith("APC", StringComparison.OrdinalIgnoreCase) ? Kind.Apc : null;
                _carKinds[car.Ptr] = kind;
                return kind;
            default:
                return null;
        }
    }

    // ---- drawing

    private void DrawEsp(Canvas c)
    {
        if (_scan.Elapsed(ScanSeconds))
            ScanEsp();
        if (_marks.Count == 0)
            return;

        WorldView.Begin(c);
        if (!WorldView.Ready || World.Pawn is not { } me)
            return;
        if (World.Pc is { } pc && CameraRig.InCinematic(pc))
            return;

        Alpha = 1f;
        foreach (Mark m in _marks)
        {
            Actor a = m.Actor;
            if (!a.IsValid || a.bDeleteMe || a.bHidden)
                continue;
            if (a is Pawn { Health: <= 0 })
                continue;
            DrawMark(m, a, me);
        }
    }

    private static void DrawMark(Mark m, Actor a, Pawn me)
    {
        Vector3 top = a.Location + Vector3.UnitZ * m.Lift;
        if (!WorldView.ToScreen(top, out Vector2 p) || !WorldView.OnScreen(p, -S(12)))
            return;

        float dist = Vector3.Distance(top, WorldView.Eye);
        float near = Math.Clamp((FarDist - dist) / (FarDist - NearDist), 0f, 1f);
        float size = S(5f + 6f * near);
        Rgba col = m.Dim ? ColDim : Colour(m.Kind);

        Spawner.Chevron(p.X, p.Y, size, col);
        float y = p.Y - size * 1.15f - S(4);

        if (a is Pawn { HealthMax: > 0 } v && !m.Dim)
        {
            float w = size * 2.4f, h = MathF.Max(2f, S(3f));
            y -= h + S(3);
            Rect(p.X - w * 0.5f - S(1), y - S(1), w + S(2), h + S(2), Spawner.MarkerEdge, 0.85f);
            Rect(p.X - w * 0.5f, y, w * Math.Clamp(v.Health / (float)v.HealthMax, 0f, 1f), h, col, 1f);
        }

        if (dist > LabelRange)
            return;
        string label = Label(m.Kind) + (m.Note.Length > 0 ? " / " + m.Note : "");
        TextCentre(p.X, y - S(14), label, col, 0.5f);
        TextCentre(p.X, p.Y + S(3), Fmt.Metres(Vector3.Distance(a.Location, me.Location) / Metre), col, 0.42f, 0.8f);
    }

    private static Rgba Colour(Kind k) => k switch
    {
        Kind.Watchtower => ColWatchtower,
        Kind.Checkpoint => ColCheckpoint,
        Kind.Beacon => ColBeacon,
        Kind.AirDrone => ColAir,
        Kind.DroneTank => ColTank,
        Kind.HeavyTank => ColHeavy,
        _ => ColCar,
    };

    private static string Label(Kind k) => k switch
    {
        Kind.Watchtower => "WATCHTOWER",
        Kind.Checkpoint => "CHECKPOINT",
        Kind.Beacon => "BEACON",
        Kind.AirDrone => "AIR DRONE",
        Kind.DroneTank => "DRONE TANK",
        Kind.HeavyTank => "HEAVY TANK",
        Kind.Humvee => "HUMVEE",
        _ => "APC",
    };
}
