using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using EDistrict = BmSDK.BmGame.RGameInfo.EDistrict;
using EThreatType = BmSDK.BmGame.RPopulationManager.EThreatType;
using EVehicleCombatEncounterType = BmSDK.BmGame.RVehicleNPC.EVehicleCombatEncounterType;
using VehicleScenarioType = BmSDK.BmGame.RGameInfo.VehicleScenarioType;

namespace CeZeY.Oracle;

/// <summary>
/// The open-world population: an empty city, the game's own APC, mob and getaway-car spawners, and a readout
/// of the militia still holding your district. Everything goes through RPopulationManager's script entry
/// points; the per-district arrays are left alone because their structs may not match the native layout.
/// </summary>
internal sealed class Population : Module
{
    public static readonly Population I = new();

    private const string NoticeTitle = "CITY";
    private const double ReadoutSeconds = 1.0;

    private static readonly (EThreatType Type, string Label)[] Threats =
    {
        (EThreatType.ETHREAT_Watchtower, "WATCHTOWERS"),
        (EThreatType.ETHREAT_CheckPoint, "CHECKPOINTS"),
        (EThreatType.ETHREAT_Beacons, "BEACONS"),
        (EThreatType.ETHREAT_AerialDrone, "AIR DRONES"),
        (EThreatType.ETHREAT_LightDrone, "DRONE TANKS"),
        (EThreatType.ETHREAT_HeavyDrone, "HEAVY TANKS"),
        (EThreatType.ETHREAT_Humvee, "HUMVEES"),
    };

    private readonly Held<RPopulationManager, bool> _disabled = new("population disable", p => p.bForceDisable, (p, v) => p.bForceDisable = v);
    private Toggle _empty = null!;
    private Toggle _readout = null!;
    private nint _clearedIn;
    private Every _refresh;
    private string _island = "";
    private string _cell = "";
    private string _threats = "";
    private string _threats2 = "";

    public override bool HasHud => _readout.On && World.InGame;

    public override void Build()
    {
        _empty = AddToggle("city_empty", "EMPTY STREETS", "Clears the roaming thugs, rioters, militia patrols and drones off the streets and stops new ones arriving, so the city is quiet. Mission enemies and militia bases stay. Switch it off and the city fills up again.",
            on => { if (!on) Release(); }).Maintain(KeepEmpty);

        AddCommand("city_apc", "SPAWN APC", "Sends one of the militia's armoured APCs out into your district, the ones you chase down with the Batmobile, and marks it. Only works once the APC hunt has started in the story.", SpawnApc);
        AddCommand("city_batmobile_mob", "THUG MOB ON BATMOBILE", "A gang of thugs comes out of the streets around the Batmobile and goes for it, as they do when you leave it parked. Use the Batmobile's thug zapper or fight them off.", MobBatmobile);
        AddCommand("city_getaway_car", "SPAWN GETAWAY CAR", "Starts a car chase near you on the roads: police after a fleeing car if the district has one, else a thug car. Chase it down.", SpawnGetaway);

        _readout = AddToggle("city_threats", "SHOW DISTRICT THREATS", "Shows which island you're on and how many watchtowers, checkpoints, drones and tanks the militia still has there.").Pref();
    }

    public override void WorldChanged()
    {
        _clearedIn = 0;
        _refresh.Force();
    }

    public override void Shutdown() => Safe.Run("population restore", Release);

    private static RPopulationManager? Manager()
    {
        RPopulationManager? pm = World.Info?.PopulationManager;
        return pm is { IsValid: true } ? pm : null;
    }

    private static RPopulationManager RequireManager()
    {
        Transition.RequireIdle();
        if (World.Info is { } gi && gi.IsChallengeMode())
            throw new InvalidOperationException("not in AR challenges");
        return Manager() ?? throw new InvalidOperationException("there's no city population here");
    }

    // ---- empty streets

    private void KeepEmpty()
    {
        if (Manager() is not { } pm)
            return;

        _disabled.Set(pm, true);
        if (_clearedIn == pm.Ptr)
            return;

        // The game's own deferred clear-out: what it does when an AR challenge or Most Wanted mission starts.
        _clearedIn = pm.Ptr;
        pm.bRemovePopulationOnNextTick = true;
    }

    private void Release()
    {
        _disabled.Restore();
        _clearedIn = 0;
    }

    // ---- spawns

    /// <summary>
    /// The game's own APC hunt. ForceSpawnAPC gives nothing when the native CanSpawnAPC refuses, and it only picks
    /// a district that still has APCs, so the refusal says which of those it is. This is story state: it isn't forced.
    /// </summary>
    private static void SpawnApc()
    {
        RPopulationManager pm = RequireManager();
        EDistrict where = pm.ForceSpawnAPC();
        if (where != EDistrict.EDISTRICT_None)
        {
            Overlay.Notice(NoticeTitle, "APC OUT IN " + Island(where), true);
            return;
        }

        // One already out: mark it instead of refusing, the way ForceSpawnAPC itself marks one it finds.
        if (pm.GetCurrentAPC() is { IsValid: true, bDeleteMe: false } apc)
        {
            Track(apc);
            Overlay.Notice(NoticeTitle, "AN APC IS ALREADY OUT, MARKED " + (int)(DistanceTo(apc) / 100f) + " M AWAY", true);
            return;
        }

        int left = 0;
        for (int d = 1; d <= 3; d++)
            left += Math.Max(0, pm.GetAPCThreatForDistrictID(d, (EDistrict)d));

        if (!pm.APCSideStoryEnabled())
            throw new InvalidOperationException("the APC hunt isn't open in your story");
        if (left == 0)
            throw new InvalidOperationException("the militia has no APCs left");
        throw new InvalidOperationException("the game isn't ready to send the next APC yet");
    }

    /// <summary>
    /// The mob itself, without TrySpawnBatmobileMob's waiting (it retries until the Batmobile is out of sight, you're
    /// away from it, riots aren't paused and the area is past the gas). 3 or 4 thugs come from street-level ground
    /// 6 to 10 m around the car, taunting and then going for it.
    /// </summary>
    private void MobBatmobile()
    {
        RPopulationManager pm = RequireManager();
        if (_empty.On)
            throw new InvalidOperationException("EMPTY STREETS is on");

        // The live car, found the way the Batmobile page finds it. The first RVehicleBatmobileBase in memory can be the
        // template the game spawns from, at the origin with no street around it: that was every "no street" so far.
        RVehicleBatmobileBase? car = Batmobile.I.Car() is { IsValid: true, bDeleteMe: false } live ? live : null;
        if (car == null)
            throw new InvalidOperationException("the Batmobile isn't nearby");

        // Each thug gets 10 random tries at a street-level spot 6 to 10 m out, and a narrow street misses most of
        // them, so one call often brings nobody (2026-11-05: "no street" with streets all around). Keep asking until
        // a proper mob has turned up, counting only real thugs (an empty pool adds None).
        // As the game's TrySpawnBatmobileMob does: the car counts as long parked. IsCombatThreat calls a Batmobile
        // that moved in the last 20 s a threat, and threatened thugs retreat from it (they all ran, 2026-11-05).
        car.LastMobileTime = -100f;

        var known = new HashSet<nint>();
        var list = pm.CurrentBatmobileMobThugs;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is { IsValid: true } t)
                known.Add(t.Ptr);
        }

        int before = LiveMobThugs(pm);
        int came = 0;
        int tries = 0;
        for (; tries < MobAttempts && came < MobWanted; tries++)
        {
            pm.SpawnBatmobileMob(car);
            came = LiveMobThugs(pm) - before;
        }

        // The game's ForceIntoCombat does this too: they hold their ground even with Batman at the wheel or close by.
        list = pm.CurrentBatmobileMobThugs;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is RPawnVillain { IsValid: true, bDeleteMe: false } thug && !known.Contains(thug.Ptr))
            {
                thug.bIgnoreBatmobileThreat = true;
                if (thug.AIController is { IsValid: true } ai && ai.RiotBrain is { IsValid: true } brain)
                {
                    brain.bRiotWantsToFight = true;
                    brain.bRiotHiding = false;
                    brain.bForcedToFleeByKismet = false;
                }
            }
        }

        if (came <= 0)
            throw new InvalidOperationException("no free street right around the Batmobile for thugs to come from");

        car.MakeReadyForThugZapper();
        if (World.WorldInfo is { } wi)
            pm.LastBatmobileMobTime = wi.TimeSeconds;
        Overlay.Notice(NoticeTitle, Fmt.Plural(came, "THUG") + " GOING FOR THE BATMOBILE", true);
    }

    private const int MobAttempts = 8;
    private const int MobWanted = 3;

    private static int LiveMobThugs(RPopulationManager pm)
    {
        var mob = pm.CurrentBatmobileMobThugs;
        int n = 0;
        for (int i = 0; i < mob.Count; i++)
        {
            if (mob[i] is { IsValid: true, bDeleteMe: false })
                n++;
        }

        return n;
    }

    /// <summary>
    /// The game's road-aware vehicle spawner first: a cop chase (police after a fleeing car), else a thug car. Both
    /// come from the district's own scenarios on the roads near you. Failing those, the debug chase car straight
    /// ahead, which needs 15 m of clear space in front of you.
    /// </summary>
    private static void SpawnGetaway()
    {
        RPopulationManager pm = RequireManager();
        EVehicleCombatEncounterType encounter = World.Info?.VehicleCombatManager is { IsValid: true } vcm
            ? vcm.GetCurrentEncounterType() : EVehicleCombatEncounterType.EVCE_NoEncounter;

        foreach ((VehicleScenarioType type, string what) in new[] { (VehicleScenarioType.VehicleScenario_CopChase, "COP CHASE"), (VehicleScenarioType.VehicleScenario_Thug, "THUG CAR") })
        {
            var before = new HashSet<nint>(World.All<RVehicleNPC>().Select(v => v.Ptr));
            pm.SpawnVehicleScenario(type, encounter, false);
            var fresh = World.All<RVehicleNPC>().Where(v => !v.bDeleteMe && !before.Contains(v.Ptr)).ToList();

            // The car to chase: an ordinary car among what arrived (the cop chase also brings the police).
            RVehicleCar? car = fresh.OfType<RVehicleCar>().Where(c => c.Wheels.Count <= 4).OrderBy(DistanceTo).FirstOrDefault();
            if (car == null)
                continue;

            Track(car);
            Overlay.Notice(NoticeTitle, what + " " + (int)(DistanceTo(car) / 100f) + " M AWAY, MARKED", true);
            return;
        }

        RPawnPlayer me = World.Player ?? throw new InvalidOperationException("the game is still loading");
        GameObject arch = CarArchetype(me) ?? throw new InvalidOperationException("the district has no car to send and none is loaded nearby");
        if (me.SpawnChaseCar(arch) is not { IsValid: true } chase)
            throw new InvalidOperationException("the district has no car to send and there's no room ahead of you");

        Track(chase);
        Overlay.Notice(NoticeTitle, "GETAWAY CAR SPOTTED, MARKED", true);
    }

    private static float DistanceTo(Actor a) => World.Pawn is { } me ? Vector3.Distance(me.Location, a.Location) : 0f;

    /// <summary>
    /// The Batmobile's vehicle tracker on it: the marker and through-wall outline the game puts on an APC. Every
    /// argument is passed, because BmSDK fills a missing one with 0, not the script's default (these mirror
    /// ForceSpawnAPC's call). The signal drops after 60 s out of range.
    /// </summary>
    private static void Track(RVehicle v)
    {
        if (World.Pc?.CurrentForensicsDevice is { IsValid: true } device)
            device.TrackVehicle(v, true, true, 3000f, false, new FString(""), 60f);
    }

    /// <summary>The game's chase car if it has one, else the template of an ordinary car already in the world (not an APC).</summary>
    private static GameObject? CarArchetype(RPawnPlayer me)
    {
        if (me.ChaseVehicleArchetype is { IsValid: true } set)
            return set;

        foreach (RVehicleCar car in World.All<RVehicleCar>())
        {
            if (car.bDeleteMe || car.Wheels.Count > 4)
                continue;
            if (car.ObjectArchetype is RVehicleCar { IsValid: true, IsClassDefaultObject: false } template)
                return template;
        }

        return null;
    }

    // ---- readout

    private static string Island(EDistrict d) => d switch
    {
        EDistrict.EDISTRICT_CityX => "MIAGANI",
        EDistrict.EDISTRICT_CityY => "FOUNDERS",
        EDistrict.EDISTRICT_CityZ => "BLEAKE",
        _ => "GOTHAM",
    };

    private void Refresh()
    {
        if (Manager() is not { } pm || World.Pawn is not { } me)
        {
            _island = "";
            return;
        }

        Vector3 at = me.Location;
        EDistrict d = pm.GetDistrictForLocation(at);
        _island = d == EDistrict.EDISTRICT_None ? "OFF THE MAP" : Island(d) + " ISLAND";
        _cell = Districts.At(at) ?? "";
        if (d == EDistrict.EDISTRICT_None)
        {
            _threats = _threats2 = "";
            return;
        }

        var parts = new List<string>();
        foreach ((EThreatType type, string label) in Threats)
        {
            int n = pm.GetThreatLevel(d, type, false);
            if (n > 0)
                parts.Add(label + " " + n);
        }

        if (parts.Count == 0)
        {
            _threats = "MILITIA CLEARED";
            _threats2 = "";
            return;
        }

        int half = (parts.Count + 1) / 2;
        _threats = string.Join("   ", parts.Take(half));
        _threats2 = string.Join("   ", parts.Skip(half));
    }

    public override void Draw(Canvas c)
    {
        if (_refresh.Elapsed(ReadoutSeconds))
            Safe.Run("district readout", Refresh);
        if (_island.Length == 0)
            return;

        float x = Gfx.S(28f);
        float y = Gfx.S(290f);
        float width = Math.Max(250f, 28f + Math.Max(Gfx.TextW(_threats, 0.6f), Gfx.TextW(_threats2, 0.6f)) / Math.Max(Gfx.Scale, 0.01f));
        float height = _threats2.Length > 0 ? 68f : 50f;
        Gfx.Panel(x, y, Gfx.S(width), Gfx.S(height));
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(9f), _island + (_cell.Length > 0 ? "  /  " + _cell.ToUpperInvariant() : ""), Theme.Hi, 0.66f);
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(27f), _threats, Theme.Mid, 0.6f);
        if (_threats2.Length > 0)
            Gfx.Text(x + Gfx.S(14f), y + Gfx.S(45f), _threats2, Theme.Mid, 0.6f);
    }
}
