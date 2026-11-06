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
    private const float PlateRange = 12000f;
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

    private Toggle _esp = null!;
    private Slider _espRange = null!;
    private readonly List<Mark> _marks = new();
    private readonly HashSet<nint> _marked = new();
    private readonly Dictionary<nint, Kind?> _carKinds = new();
    private readonly Dictionary<nint, (bool Gone, double At)> _towerGone = new();
    private Every _scan;

    private void BuildEsp()
    {
        _esp = AddToggle("city_threat_esp", "THREAT ESP", "A round marker over each militia threat loaded around you, with a code in the middle: WT watchtower, CP checkpoint, CB command beacon, AD air drone, DT drone tank, HT heavy tank, HV humvee, APC. On vehicles the ring is their health. Live ones have turning ticks; grey ones are parked or asleep and won't fight until the game wakes them. Aim at one for its card. Far-off ones appear as the city streams them in.",
            on => { if (!on) ForgetEsp(); });
        _espRange = AddSlider("city_threat_esp_range", "THREAT ESP RANGE", "How far away THREAT ESP marks things.", 400f, 50f, 1500f, 50f, 400f, Fmt.Metres).Pref();
    }

    private void ForgetEsp()
    {
        _marks.Clear();
        _marked.Clear();
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

    private static readonly List<Pawn> _crew = new();
    private static readonly List<(float X, float Y, bool Hostile)> _pips = new();

    /// <summary>Adds everyone riding in a vehicle THREAT ESP marks, so the editor's ESP folds them into its marker.</summary>
    public void AddCrews(HashSet<nint> into)
    {
        if (!_esp.On)
            return;
        foreach (Mark m in _marks)
        {
            if (m.Actor is not Vehicle || Crew.Of(m.Actor, _crew) == 0)
                continue;
            foreach (Pawn p in _crew)
                into.Add(p.Ptr);
        }
    }

    /// <summary>Marked by THREAT ESP right now, so the editor's ESP leaves it to this marker.</summary>
    public bool Marks(nint ptr) => _esp.On && _marked.Contains(ptr);

    private void DrawEsp(Canvas c)
    {
        if (_scan.Elapsed(ScanSeconds))
        {
            ScanEsp();
            _marked.Clear();
            foreach (Mark m in _marks)
                _marked.Add(m.Actor.Ptr);
        }

        if (_marks.Count == 0)
            return;

        WorldView.Begin(c);
        if (!WorldView.Ready || World.Pawn is not { } me)
            return;
        if (World.Pc is { } pc && CameraRig.InCinematic(pc))
            return;

        Alpha = 1f;
        Lens.Discard();
        Mark? focus = null;
        Vector2 focusAt = default;
        float focusR = 0f;
        foreach (Mark m in _marks)
        {
            Actor a = m.Actor;
            if (!a.IsValid || a.bDeleteMe || a.bHidden)
                continue;
            if (a is Pawn { Health: <= 0 })
                continue;
            if (DrawMark(m, a, me, out Vector2 at, out float r))
            {
                focus = m;
                focusAt = at;
                focusR = r;
            }
        }

        Crew.FlushPips(_pips);
        if (focus is { } f)
            DrawFocus(f, focusAt, focusR, me);
    }

    /// <summary>Draws one threat's marker. True when it has the focus, so its card is drawn last, over the rest.</summary>
    private static bool DrawMark(Mark m, Actor a, Pawn me, out Vector2 p, out float r)
    {
        r = 0f;
        Vector3 top = a.Location + Vector3.UnitZ * m.Lift;
        if (!WorldView.ToScreen(top, out p) || !WorldView.OnScreen(p, -S(12)))
            return false;

        float dist = Vector3.Distance(top, WorldView.Eye);
        float near = Math.Clamp((FarDist - dist) / (FarDist - NearDist), 0f, 1f);
        float settle = Lens.Settle(Lens.Age(a.Ptr));
        r = Lens.L(7f + 7f * near) * (0.6f + 0.4f * settle);
        Rgba col = m.Dim ? Lens.Dormant : Colour(m.Kind);
        float health = a is Pawn { HealthMax: > 0 } v ? Math.Clamp(v.Health / (float)v.HealthMax, 0f, 1f) : -1f;

        Lens.Offer(a.Ptr, p, r + Lens.L(4));
        Lens.Beacon(p.X, p.Y, r, col, Code(m.Kind), health, !m.Dim);

        Lens.Tier tier = Lens.TierOf(a.Ptr, dist, PlateRange, LabelRange);
        if (tier == Lens.Tier.Card)
            return true;

        float below = p.Y + r + Lens.L(9);
        if (Crew.Of(a, _crew) > 0)
        {
            Crew.Pips(_crew, p.X, below, _pips);
            below += Lens.L(10);
        }

        string metres = Lens.Metres(Vector3.Distance(a.Location, me.Location));
        if (tier == Lens.Tier.Full)
        {
            string name = Label(m.Kind) + (m.Note.Length > 0 ? "  " + m.Note : "");
            float h = TextH(Lens.NameScale * Lens.Size) + Lens.L(3);
            Lens.Plate(p.X, below + h, name, metres, col, settle);
        }
        else if (tier == Lens.Tier.Name)
        {
            Lens.Caption(p.X, below, metres, Rgba.Mix(col, Theme.Hi, 0.4f));
        }

        return false;
    }

    private static readonly List<(string, Rgba)> _cardLines = new();

    private static void DrawFocus(Mark m, Vector2 p, float r, Pawn me)
    {
        Actor a = m.Actor;
        Rgba col = m.Dim ? Lens.Dormant : Colour(m.Kind);
        float pad = Lens.L(5);
        Lens.Lock(p.X - r, p.Y - r, p.X + r, p.Y + r, Theme.Accent, Lens.FocusAge);

        _cardLines.Clear();
        string where = Lens.Metres(Vector3.Distance(a.Location, me.Location)) + " AWAY";
        float health = -1f, trail = -1f;
        if (a is Pawn { HealthMax: > 0 } v)
        {
            where += "   " + Math.Max(0, v.Health) + " / " + v.HealthMax + " HP";
            health = Math.Clamp(v.Health / (float)v.HealthMax, 0f, 1f);
            trail = Lens.Trail(a.Ptr, health);
        }

        _cardLines.Add((where, Theme.Mid));
        if (Crew.Of(a, _crew) > 0)
            Crew.Lines(_crew, _cardLines);
        _cardLines.Add((m.Note switch
        {
            "PARKED" => "PARKED: SET DRESSING, IT WON'T FIGHT",
            "ASLEEP" => "ASLEEP UNTIL THE GAME WAKES IT",
            "JAMMED" => "JAMMED: IT CAN'T SEE YOU",
            _ => "ACTIVE",
        }, m.Dim ? Theme.Low : Theme.Signal));

        Lens.Card(p.X, p.Y, p.X + r + pad, p.Y - r - pad, "MILITIA THREAT", Label(m.Kind), _cardLines, col, health, trail, Lens.FocusAge);
    }

    /// <summary>The code in the middle of each marker, like a tactical map's.</summary>
    private static string Code(Kind k) => k switch
    {
        Kind.Watchtower => "WT",
        Kind.Checkpoint => "CP",
        Kind.Beacon => "CB",
        Kind.AirDrone => "AD",
        Kind.DroneTank => "DT",
        Kind.HeavyTank => "HT",
        Kind.Humvee => "HV",
        _ => "APC",
    };

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
