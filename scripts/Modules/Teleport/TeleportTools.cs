using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Everything on the TELEPORT page beyond the waypoint itself. <see cref="Teleport"/> does the moving.</summary>
internal sealed class TeleportTools : Module
{
    public static readonly TeleportTools I = new();

    private const string NoticeTitle = "TELEPORT";

    // A waypoint the player places by hand uses this as both its type and its name. "BatSymbol" reads as an objective.
    private const string ManualWaypoint = "UserMarker";
    private const float Metre = 100f;
    private const float WaypointMoved = 100f;
    private const float WallGap = 60f;
    private const float FloorNormal = 0.5f;
    private const float GroundReach = 3000f;
    private const float RoofScan = 40000f;
    private const float FloorScan = 60000f;
    private const float CarAhead = 700f;
    private const float CarSide = 450f;
    private const float PlaceNameRange = 6000f;
    private const double WaypointPollSeconds = 0.25;

    private readonly Random _random = new();
    private Choice _slot = null!;
    private Choice _district = null!;
    private Choice _place = null!;
    private Choice _category = null!;
    private Slider _range = null!;
    private Slider _dash = null!;
    private Slider _sky = null!;
    private Toggle _instant = null!;
    private Toggle _coords = null!;
    private Every _waypointPoll;
    private Vector3 _waypoint;
    private bool _waypointOn;
    private bool _waypointKnown;
    private bool _pending;

    public override bool HasHud => _coords.On && World.Pawn != null;

    public override void Build()
    {
        AddCommand("teleport_objective", "TELEPORT TO OBJECTIVE", "Goes to the mission marker currently on your map and compass. Works across islands.", ToObjective).Moves();
        _instant = AddToggle("teleport_instant", "TELEPORT ON WAYPOINT", "Teleports as soon as you close the map after placing a waypoint, with no need to run anything.").Pref();
        AddCommand("waypoint_crosshair", "WAYPOINT AT CROSSHAIR", "Places your map waypoint wherever you are aiming.", MarkCrosshair);
        AddCommand("waypoint_clear", "CLEAR WAYPOINT", "Removes your map waypoint.", ClearWaypoint);

        _district = AddChoice("teleport_district", "DISTRICT", "Pick a block of the city, then run TELEPORT TO DISTRICT. These are the game's own internal blocks, named as it names them.",
            new[] { "" }, new[] { "OPEN MENU IN GAME" }).Pref();
        _district.HoldToScroll = true;
        AddCommand("teleport_to_district", "TELEPORT TO DISTRICT", "Goes to the middle of the district above and lands on the highest ground there.", ToDistrict).Moves();
        _category = AddChoice("teleport_place_group", "CATEGORY", "Which part of the game's world map to browse. Everything is listed from the start, including places you haven't found yet.",
            MapPlaces.Groups.Select(g => g.Key).ToArray(), MapPlaces.Groups.Select(g => g.Label).ToArray(), _ => RefreshPlaces()).Pref();
        _place = AddChoice("teleport_place", "PLACE", "Hold left or right to scroll. Lists come from the game's map data, so collectibles you already have are still shown.",
            new[] { "" }, new[] { "OPEN MENU IN GAME" }).Pref();
        _place.HoldToScroll = true;
        AddCommand("teleport_to_place", "TELEPORT TO PLACE", "Goes to the place above. Works across islands.", ToPlace).Moves();
        AddCommand("teleport_place_near", "NEAREST IN CATEGORY", "Goes to the closest place in the category above, wherever you are. Collected ones count too, so use GO BACK and pick another if it's already done.", ToNearestPlace).Moves();
        AddCommand("teleport_place_waypoint", "WAYPOINT TO PLACE", "Places your map waypoint on the place above, to drive or glide there yourself.", WaypointPlace);
        AddCommand("teleport_random", "RANDOM LOCATION", "Drops you somewhere random in the city.", ToRandom).Moves();

        _slot = AddChoice("teleport_slot", "SAVED SLOT", "Pick one of 8 slots. They are kept between sessions in saved_spots.txt.", SlotKeys(), SlotKeys()).Pref();
        AddCommand("teleport_slot_go", "GO TO SLOT", "Teleports to the slot above and faces the way you did when you saved it.", GoSlot).Moves();
        Command save = AddCommand("teleport_slot_save", "SAVE HERE", "Stores where you are standing in the slot above.", SaveSlot).Confirmed();
        save.ConfirmPrompt = () => SavedSpots.Get(_slot.Index) == null ? "SAVE TO SLOT " + (_slot.Index + 1) + "?" : "OVERWRITE SLOT " + (_slot.Index + 1) + "?";
        AddCommand("teleport_slot_waypoint", "WAYPOINT TO SLOT", "Places your map waypoint on the slot above, to drive or glide there yourself.", WaypointSlot);
        Command clear = AddCommand("teleport_slot_clear", "CLEAR SLOT", "Empties the slot above.", ClearSlot).Confirmed();
        clear.ConfirmPrompt = () => "CLEAR SLOT " + (_slot.Index + 1) + "?";

        AddCommand("teleport_crosshair", "BLINK TO CROSSHAIR", "Jumps straight to where you are aiming. Best on a hotkey.", ToCrosshair).Moves();
        AddCommand("teleport_dash", "BLINK FORWARD", "Jumps ahead the way the camera faces, straight through walls, and lands on whatever is there.", Dash).Moves();
        AddCommand("teleport_up", "SKY DROP", "Throws you high above where you are standing, ready to glide.", SkyDrop).Moves();
        AddCommand("teleport_ground", "DROP TO GROUND", "Puts you on the first surface below you.", ToGround).Moves();
        AddCommand("teleport_roof", "TO THE ROOFTOP", "Puts you on the highest surface above where you are.", ToRoof).Moves();
        _range = AddSlider("teleport_blink_range", "BLINK RANGE", "How far BLINK TO CROSSHAIR reaches. Beyond it, or if the aim hits nothing, it teleports to the ground below that point.",
            300f, 50f, 1000f, 50f, 1000f, Fmt.Metres).Pref();
        _dash = AddSlider("teleport_dash_length", "BLINK FORWARD LENGTH", "How far BLINK FORWARD jumps.", 15f, 5f, 100f, 5f, 50f, Fmt.Metres).Pref();
        _sky = AddSlider("teleport_sky_height", "SKY DROP HEIGHT", "How high SKY DROP throws you.", 150f, 20f, 500f, 10f, 300f, Fmt.Metres).Pref();

        AddCommand("teleport_batmobile", "TELEPORT TO BATMOBILE", "Goes to wherever the Batmobile is parked, even across the city.", ToBatmobile).Moves();
        AddCommand("teleport_bring_batmobile", "BRING BATMOBILE HERE", "Puts the Batmobile right in front of you, including places it could never drive to.", BringBatmobile);
        AddCommand("teleport_trophy", "TELEPORT TO NEAREST TROPHY", "Goes to the closest uncollected Riddler trophy in the part of the city that is loaded. Some still need their puzzle solved.", ToTrophy).Moves();

        _coords = AddToggle("teleport_coords", "SHOW COORDINATES", "Shows your position, district and heading on screen.").Pref();
        AddCommand("teleport_log_position", "LOG POSITION", "Writes your exact position to Oracle.log.", LogPosition);
    }

    private static string[] SlotKeys() => Enumerable.Range(1, SavedSpots.Slots).Select(i => "slot" + i).ToArray();

    public override void Started()
    {
        SavedSpots.Load();
        RefreshSlots();
    }

    public override void WorldChanged()
    {
        _waypointKnown = false;
        _pending = false;
    }

    public override void MenuOpening()
    {
        RefreshSlots();
        RefreshDistricts();
        RefreshPlaces();
    }

    public override void Tick(float dt)
    {
        if (!_instant.On)
        {
            _waypointKnown = false;
            _pending = false;
        }
        else if (_waypointPoll.Elapsed(WaypointPollSeconds))
            Safe.Run("waypoint watch", WatchWaypoint);
    }

    /// <summary>Fires only on a change, so a waypoint already set when this is switched on or a game loads stays put.</summary>
    private void WatchWaypoint()
    {
        RPersistentData? pd = World.Info?.PersistentData;
        if (pd is not { IsValid: true })
            return;

        bool on = pd.GetCustomWaypointEnabled();
        Vector3 at = on ? pd.GetCustomWaypoint(false) : default;
        bool changed = on && (!_waypointOn || Vector3.DistanceSquared(at, _waypoint) > WaypointMoved * WaypointMoved);
        bool known = _waypointKnown;
        _waypointKnown = true;
        _waypointOn = on;
        _waypoint = at;
        if (!on)
            _pending = false;
        else if (known && changed)
            _pending = true;

        if (!_pending || World.Paused || Menu.IsOpen || Transition.Busy)
            return;

        _pending = false;
        Teleport.I.GoToWaypoint();
    }

    private void RefreshSlots()
    {
        string[] labels = new string[SavedSpots.Slots];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = (i + 1) + ": " + (SavedSpots.Get(i)?.Name ?? "EMPTY");

        _slot.SetOptions(SlotKeys(), labels, _slot.Index);
    }

    private void RefreshDistricts()
    {
        string[] names = DistrictNames().ToArray();
        if (names.Length > 0)
            _district.SetOptions(names, names.Select(DistrictLabel).ToArray());
    }

    private void RefreshPlaces()
    {
        MapPlaces.Ensure();
        List<MapPlaces.Place> items = PlaceGroup.Items;
        if (items.Count == 0)
        {
            _place.SetOptions(new[] { "" }, new[] { "MAP DATA NOT FOUND" });
            return;
        }

        _place.SetOptions(items.Select(p => p.Key).ToArray(), items.Select(p => p.Label).ToArray());
    }

    private MapPlaces.Group PlaceGroup => MapPlaces.Groups[Math.Clamp(_category.Index, 0, MapPlaces.Groups.Count - 1)];

    /// <summary>The ordinary numbered districts, without the special cells like CityX_98 and CityZ_T01.</summary>
    private static IEnumerable<string> DistrictNames() => Districts.All.Keys
        .Where(n => char.IsDigit(n[^1]) && char.IsDigit(n[^2]) && n[^2] != '9' && n[^3] == '_')
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    private static string DistrictLabel(string name) => name.Replace("City", "CITY ", StringComparison.OrdinalIgnoreCase).Replace('_', ' ').ToUpperInvariant();

    private static string DistrictOf(Vector3 at)
    {
        string? name = Districts.At(at);
        return name == null ? "INDOORS" : DistrictLabel(name);
    }

    private static string MapName() => World.WorldInfo?.GetMapName(false).ToString() ?? throw new InvalidOperationException("the game is still loading");

    private static Vector3 Up(float z) => new(0f, 0f, z);

    /// <summary>Where the camera looks, as a start point and a flat-free direction.</summary>
    private static (Vector3 From, Vector3 Dir, Rotator Rotation) View()
    {
        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        pc.GetPlayerViewPoint(out Vector3 from, out Rotator rotation);
        return (from, Vector3.Normalize(rotation.ToDirection()), rotation);
    }

    /// <summary>The nearest floor to a point, looking a little above it first.</summary>
    private static bool FloorNear(Pawn mover, Vector3 at, float above, out Vector3 floor) =>
        Teleport.Ray(mover, at + Up(above), at - Up(GroundReach), out floor, out _);

    // ---- map targets

    private static void ToObjective()
    {
        RGameInfo info = World.Info ?? throw new InvalidOperationException("the game is still loading");
        if (Objective(info) is { } at)
        {
            Teleport.I.Go(at, true);
            return;
        }

        throw new InvalidOperationException("no objective is active");
    }

    /// <summary>The active mission marker in world space, or null. AUTOPILOT TO OBJECTIVE drives to it too.</summary>
    internal static Vector3? Objective(RGameInfo info)
    {
        var markers = info.ObjectiveMarkers;
        int picked = info.HighestPriorityNonCustomObjectiveMarker;
        if (picked >= 0 && picked < markers.Count && Usable(markers[picked]))
            return markers[picked].Location;

        RObjectiveMarker? best = null;
        foreach (RObjectiveMarker m in markers)
        {
            if (Usable(m) && (best == null || m.Priority > best.Priority))
                best = m;
        }

        if (best != null)
            return best.Location;

        Vector3 stored = info.PersistentData is { IsValid: true } pd ? pd.vObjectiveLocation : default;
        return stored == Vector3.Zero ? null : stored;
    }

    private static bool Usable(RObjectiveMarker? m) => m is { IsValid: true, bDeleteMe: false, IsMarkerActive: true, IsCustomWaypoint: false };

    /// <summary>Sets the map waypoint. Places and slots are unshifted; a point seen in the world (inWorld) is shifted.</summary>
    private void SetWaypoint(Vector3 at, bool inWorld = false)
    {
        RPersistentData pd = World.Info?.PersistentData ?? throw new InvalidOperationException("the game is still loading");
        pd.CustomWaypoint_Type = RPersistentData.ECustomWayPointType.CustomWayType_Proximity;
        pd.SetCustomWaypointTypeName(new FString(ManualWaypoint));
        pd.SetCustomWaypointName(new FString(ManualWaypoint));
        pd.SetCustomWaypoint(at, true, inWorld);
        World.Info?.UpdateObjectiveMarkerVisibility(true);
        World.Pc?.UpdatePlayerWaypoint();

        // Oracle placed this one itself, so TELEPORT ON WAYPOINT mustn't treat it as the player's.
        _waypoint = pd.GetCustomWaypoint(false);
        _waypointOn = true;
        Overlay.Notice(NoticeTitle, "WAYPOINT SET", true);
    }

    private void MarkCrosshair()
    {
        Pawn mover = Teleport.RequireMover();
        (Vector3 from, Vector3 dir, _) = View();
        Vector3 end = from + dir * (_range.Value * Metre);
        SetWaypoint(Teleport.Ray(mover, from, end, out Vector3 hit, out _) ? hit : end, true);
    }

    private void ClearWaypoint()
    {
        RPersistentData pd = World.Info?.PersistentData ?? throw new InvalidOperationException("the game is still loading");
        pd.SetCustomWaypointEnabled(false);
        World.Info?.UpdateObjectiveMarkerVisibility(false);
        Overlay.Notice(NoticeTitle, "WAYPOINT CLEARED", true);
    }

    // ---- places

    private void ToDistrict()
    {
        if (!Districts.All.TryGetValue(_district.CurrentKey, out Districts.Cell cell))
            throw new InvalidOperationException("pick a district first");

        Teleport.I.Go(new Vector3((cell.Min.X + cell.Max.X) * 0.5f, (cell.Min.Y + cell.Max.Y) * 0.5f, 0f), false);
    }

    private void ToPlace()
    {
        Travel(Picked());
    }

    private MapPlaces.Place Picked() =>
        PlaceGroup.Items.Find(p => p.Key == _place.CurrentKey) ?? throw new InvalidOperationException("pick a place first");

    private static void Travel(MapPlaces.Place place) => Teleport.I.Go(place.At + Teleport.Offset, !place.Surface);

    private void ToNearestPlace()
    {
        Pawn mover = Teleport.RequireMover();
        Travel(MapPlaces.Closest(PlaceGroup, mover.Location - Teleport.Offset) ?? throw new InvalidOperationException("nothing in that category"));
    }

    private void WaypointPlace() => SetWaypoint(Picked().At);

    private void ToRandom()
    {
        string[] names = DistrictNames().ToArray();
        if (names.Length == 0)
            throw new InvalidOperationException("the city map isn't known yet");

        Districts.Cell cell = Districts.All[names[_random.Next(names.Length)]];
        float t = 0.25f + 0.5f * _random.NextSingle();
        float u = 0.25f + 0.5f * _random.NextSingle();
        Teleport.I.Go(new Vector3(cell.Min.X + (cell.Max.X - cell.Min.X) * t, cell.Min.Y + (cell.Max.Y - cell.Min.Y) * u, 0f), false);
    }

    // ---- saved slots

    private SavedSpots.Entry Filled()
    {
        SavedSpots.Entry entry = SavedSpots.Get(_slot.Index) ?? throw new InvalidOperationException("slot " + (_slot.Index + 1) + " is empty");
        if (!entry.Map.Equals(MapName(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("that slot was saved on another map");

        return entry;
    }

    private void GoSlot()
    {
        SavedSpots.Entry entry = Filled();
        Teleport.I.Go(entry.At + Teleport.Offset, true, new Rotator(0, entry.Yaw, 0));
    }

    private void SaveSlot()
    {
        // Slots are kept unshifted, like the map's places, so they stay right whatever the city's offset is later.
        Pawn mover = Teleport.RequireMover();
        Vector3 at = mover.Location - Teleport.Offset;
        string name = MapPlaces.Near(at, PlaceNameRange) ?? DistrictOf(at);
        SavedSpots.Set(_slot.Index, new SavedSpots.Entry(MapName(), at, mover.Rotation.Yaw, name));
        RefreshSlots();
        Overlay.Notice(NoticeTitle, "SAVED TO SLOT " + (_slot.Index + 1), true);
    }

    private void WaypointSlot() => SetWaypoint(Filled().At);

    private void ClearSlot()
    {
        SavedSpots.Set(_slot.Index, null);
        RefreshSlots();
    }

    // ---- quick moves

    private void ToCrosshair()
    {
        Pawn mover = Teleport.RequireMover();
        (Vector3 from, Vector3 dir, Rotator rotation) = View();
        Vector3 end = from + dir * (_range.Value * Metre);
        if (!Teleport.Ray(mover, from, end, out Vector3 hit, out Vector3 normal))
        {
            // Open sky: stay instant if the ground under that point is loaded, otherwise it needs the long trip.
            if (Teleport.Ray(mover, end + Up(RoofScan), end - Up(FloorScan), out Vector3 ground, out _))
                Teleport.I.Hop(ground + Up(Teleport.Lift(mover)), new Rotator(0, rotation.Yaw, 0));
            else
                Teleport.I.Go(end, false);
            return;
        }

        float lift = Teleport.Lift(mover);
        Vector3 spot = normal.Z >= FloorNormal ? hit + Up(lift) : hit + normal * WallGap;
        if (normal.Z < FloorNormal && FloorNear(mover, spot, 0f, out Vector3 floor))
            spot = floor + Up(lift);

        Teleport.I.Hop(spot, new Rotator(0, rotation.Yaw, 0));
    }

    private void Dash()
    {
        Pawn mover = Teleport.RequireMover();
        (_, Vector3 dir, Rotator rotation) = View();
        Vector3 flat = new(dir.X, dir.Y, 0f);
        if (flat.LengthSquared() < 0.001f)
            throw new InvalidOperationException("look ahead, not straight up or down");

        Vector3 spot = mover.Location + Vector3.Normalize(flat) * (_dash.Value * Metre);
        Teleport.I.Hop(FloorNear(mover, spot, GroundReach, out Vector3 floor) ? floor + Up(Teleport.Lift(mover)) : spot, new Rotator(0, rotation.Yaw, 0));
    }

    private void SkyDrop()
    {
        Pawn mover = Teleport.RequireMover();
        Teleport.I.Hop(mover.Location + Up(_sky.Value * Metre));
    }

    private static void ToGround()
    {
        Pawn mover = Teleport.RequireMover();
        if (!Teleport.Ray(mover, mover.Location, mover.Location - Up(FloorScan), out Vector3 floor, out _))
            throw new InvalidOperationException("no ground below you");

        Teleport.I.Hop(floor + Up(Teleport.Lift(mover)));
    }

    private static void ToRoof()
    {
        Pawn mover = Teleport.RequireMover();
        if (!Teleport.Ray(mover, mover.Location + Up(RoofScan), mover.Location - Up(FloorScan), out Vector3 top, out _))
            throw new InvalidOperationException("nothing to stand on here");

        Teleport.I.Hop(top + Up(Teleport.Lift(mover)));
    }

    // ---- things in the world

    private static RVehicleBatmobileBase ParkedCar(Pawn mover)
    {
        if (mover is RVehicleBatmobileBase)
            throw new InvalidOperationException("you are already driving it");

        return Batmobile.I.Car() ?? throw new InvalidOperationException("the Batmobile isn't available here");
    }

    private static void ToBatmobile()
    {
        Pawn mover = Teleport.RequireMover();
        RVehicleBatmobileBase car = ParkedCar(mover);
        // Beside the car: a quarter turn from the way it faces.
        Teleport.I.Go(car.Location + Geo.Facing(car.Rotation.Yaw + 90f) * CarSide, true);
    }

    private static void BringBatmobile()
    {
        Pawn mover = Teleport.RequireMover();
        RVehicleBatmobileBase car = ParkedCar(mover);
        if (car.Driver != null)
            throw new InvalidOperationException("the Batmobile is occupied");

        Vector3 spot = mover.Location + Geo.Facing(mover.Rotation.Yaw) * CarAhead;
        if (FloorNear(mover, spot, GroundReach, out Vector3 floor))
            spot = floor + Up(Teleport.Lift(car));

        Teleport.Place(car, spot, new Rotator(0, mover.Rotation.Yaw, 0));
        Log.Info("Batmobile brought to " + Fmt.Position(spot) + ".");
    }

    private static void ToTrophy()
    {
        Pawn mover = Teleport.RequireMover();
        RPickup_Riddler? nearest = World.All<RPickup_Riddler>()
            .Where(p => !p.bDeleteMe && !p.HasBeenPickedUp())
            .OrderBy(p => Vector3.DistanceSquared(p.Location, mover.Location))
            .FirstOrDefault();
        if (nearest == null)
            throw new InvalidOperationException("no trophies loaded nearby");

        Teleport.I.Go(nearest.Location, true);
    }

    // ---- readouts

    private static void LogPosition()
    {
        Pawn mover = World.RequirePawn();
        Log.Info("Position: " + Fmt.Position(mover.Location) + ", yaw " + mover.Rotation.Yaw.ToString("0", CultureInfo.InvariantCulture) + ", " + DistrictOf(mover.Location) + ".");
        Overlay.Notice(NoticeTitle, "WRITTEN TO ORACLE.LOG", true);
    }

    public override void Draw(Canvas c)
    {
        if (World.Pawn is not { } pawn)
            return;

        const float width = 250f;
        const float height = 68f;
        float heading = ((pawn.Rotation.Yaw % 360f) + 360f) % 360f;
        float x = Gfx.S(28f);
        float y = Gfx.S(150f);
        Gfx.Panel(x, y, Gfx.S(width), Gfx.S(height));
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(9f), DistrictOf(pawn.Location), Theme.Accent, 0.66f);
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(27f), Fmt.Position(pawn.Location), Theme.Hi, 0.66f);
        Gfx.Text(x + Gfx.S(14f), y + Gfx.S(45f), "HEADING " + heading.ToString("0", CultureInfo.InvariantCulture), Theme.Mid, 0.66f);
    }
}
