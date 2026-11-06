using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SPAWNER/VEHICLES. A vehicle is spawned from its archetype, as RPawnPlayer.SpawnChaseCar and
/// RGameInfo.SpawnBatmobile do (Spawn(archetype.Class, ..., archetype)), then given a behaviour the way the chase
/// car is (GotoBehaviourClass) or the combat manager's own entry points (GotoCombatBehaviour, GotoGuardBehaviour).
/// </summary>
internal sealed partial class Spawner
{
    private const float VehicleFootprint = 450f;
    private const float VehicleLift = 250f;
    private const float AllyHackSeconds = 100000f;
    private const float FarAway = 1e17f;

    private Choice _vehGroup = null!;
    private Choice _vehWho = null!;
    private Choice _vehSide = null!;
    private Choice _vehBehaviour = null!;
    private Slider _vehHealth = null!;

    private List<VehicleRecipe> _vehicles = new();

    private void BuildVehicles()
    {
        _vehGroup = AddChoice("veh_group", "GROUP", "Which kind of vehicle the VEHICLE list shows.", new[] { "" }, new[] { "EVERYTHING" }, _ => ListVehicles()).Pref();
        _vehWho = AddChoice("veh_who", "VEHICLE", "Hold to scroll. Every vehicle template the game has loaded: militia tanks and drones are loaded across the city, cars follow the district you're in.",
            new[] { "" }, new[] { "OPEN THE MENU IN GAME" }).Pref();
        _vehWho.HoldToScroll = true;
        AddCommand("veh_refresh", "REFRESH LIST", "Reads again which vehicles can be spawned here.", () => RefreshVehicles(true)).Setting();

        _vehSide = AddChoice("veh_side", "SIDE", "HOSTILE vehicles are the militia's. ON YOUR SIDE hacks them the way the drone hacker does, so they fight the militia; the hack lasts until you delete them.",
            new[] { "hostile", "ally" }, new[] { "HOSTILE", "ON YOUR SIDE" }).Pref();
        _vehBehaviour = AddChoice("veh_behaviour", "BEHAVIOUR", "THEIR OWN is whatever that vehicle does by default. ATTACK sends it at you (or at enemies when on your side). GUARD holds its ground and shoots. PARKED keeps it still. WANDER and RUN AWAY are for cars.",
            new[] { "own", "attack", "guard", "parked", "wander", "runaway" }, new[] { "THEIR OWN", "ATTACK", "GUARD", "PARKED", "WANDER", "RUN AWAY" }).Pref();
        _vehHealth = AddSlider("veh_health", "HEALTH", "How much health spawned vehicles get, times their normal amount.", 1f, 0.25f, 10f, 0.25f, 3f, Fmt.Times).Pref();
        AddCommand("veh_spawn", "SPAWN VEHICLE", "Spawns the vehicle above, HOW MANY times, the way this tab and PLACE say. Drones appear in the air.", () => SpawnVehicles(PickedVehicle()));
    }

    private void ListVehicles()
    {
        string[] groups = _vehicles.Select(r => r.Group).Distinct().ToArray();
        _vehGroup.SetOptions(new[] { "" }.Concat(groups).ToArray(), new[] { "EVERYTHING" }.Concat(groups).ToArray());

        string group = _vehGroup.CurrentKey;
        List<VehicleRecipe> shown = group.Length == 0 ? _vehicles : _vehicles.Where(r => r.Group == group).ToList();
        if (shown.Count == 0)
            _vehWho.SetOptions(new[] { "" }, new[] { World.InGame ? "NO VEHICLES LOADED HERE" : "OPEN THE MENU IN GAME" });
        else
            _vehWho.SetOptions(shown.Select(r => r.Key).ToArray(), shown.Select(r => r.Label).ToArray());
    }

    private VehicleRecipe PickedVehicle()
    {
        if (_vehicles.Count == 0)
            RefreshVehicles(false);
        string key = _vehWho.CurrentKey;
        VehicleRecipe r = _vehicles.Find(v => v.Key == key) ?? throw new InvalidOperationException("no vehicles loaded here: REFRESH LIST");
        if (!r.Archetype.IsValid)
        {
            RefreshVehicles(false);
            throw new InvalidOperationException("that vehicle has unloaded: pick again");
        }

        return r;
    }

    private void SpawnVehicles(VehicleRecipe r)
    {
        Pawn me = RequireReady();
        RVehicleNPC arch = r.Archetype;
        bool flying = arch is RVehicleFlying;
        int n = Room(Wanted);
        var made = new List<Spawn>();
        int failed = 0;
        List<Spot> spots = Spots(me, n, VehicleFootprint);
        Starting(n, r.Label, r.Key + ", " + _vehSide.State + ", " + _vehBehaviour.State);
        foreach (Spot spot in spots)
        {
            Vector3 at = spot.Floor + Vector3.UnitZ * (flying ? VehicleLift * 4f : VehicleLift);
            if (Game.SpawnActor(arch.Class, default, at, spot.Facing, arch) is not RVehicleNPC v || !v.IsValid)
            {
                failed++;
                continue;
            }

            Spawn s = Track(v, SpawnKind.Vehicle, r.Label, at);
            Safe.Run("spawn vehicle setup", () => SetUp(v));
            s.Behaviour = _vehBehaviour.CurrentKey;
            s.Ally = _vehSide.CurrentKey == "ally";
            s.Pending = s.Behaviour != "own" || s.Ally;
            made.Add(s);
        }

        Finish(made, r.Label, failed, "the game wouldn't place it here (blocked?)");
    }

    private void SetUp(RVehicleNPC v)
    {
        if (MathF.Abs(_vehHealth.Value - 1f) > 0.01f && v.HealthMax > 0)
        {
            v.HealthMax = Math.Max(1, (int)MathF.Round(v.HealthMax * _vehHealth.Value));
            v.Health = v.HealthMax;
        }

        // Far from the player a vehicle's behaviour switches off (the chase car lifts the range the same way).
        v.BehaviourDeactivateRange = FarAway;
    }

    /// <summary>
    /// The chosen behaviour and side. RVehicleNPC.PostBeginPlay starts the vehicle's own behaviour on a 0.25 s timer
    /// (SwitchToAssignedBehaviour), which would undo anything set at spawn, so this runs once that has happened.
    /// </summary>
    private static void Steer(RVehicleNPC v, string behaviour, bool ally)
    {
        switch (behaviour)
        {
            case "attack":
                v.GotoCombatBehaviour();
                break;
            case "guard":
                v.GotoGuardBehaviour();
                break;
            case "parked":
                v.GotoBehaviourClass(RVehicleBehaviour_Idle.StaticClass(), false);
                break;
            case "wander":
                v.GotoBehaviourClass(RVehicleBehaviour_Wander.StaticClass(), false);
                break;
            case "runaway":
                v.GotoBehaviourClass(RVehicleBehaviour_RunAway.StaticClass(), false);
                break;
        }

        if (ally && !v.SwitchAllegiance(AllyHackSeconds))
            Log.Info("Spawner: " + v.Name + " refused the hack (SwitchAllegiance returned false).");
    }
}
