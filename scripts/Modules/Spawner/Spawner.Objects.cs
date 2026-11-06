using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SPAWNER/OBJECTS. Any static mesh in memory becomes an object: a KActorSpawnable when it should fall and be knocked
/// about, a DynamicSMActor_Spawnable (what PRE-SCARECROW ERA's sky uses) when it should stay put. A mesh with no
/// collision shape can't be a physics object; one that drops through the floor is caught and held where it spawned.
/// </summary>
internal sealed partial class Spawner
{
    private const float ObjectFootprint = 120f;
    private const float ObjectLift = 60f;
    private const float NearbyRange = 150f * 100f;
    private const int NearbyMax = 200;
    private const int LoadedMax = 3000;
    private const double SettleSeconds = 4.0;
    private const float FellTooFar = 2500f;

    private static readonly (string Key, string Label, string[] Words)[] MeshFilters =
    {
        ("any", "ANYTHING", Array.Empty<string>()),
        ("crates", "CRATES AND BOXES", new[] { "crate", "box", "pallet", "case", "container" }),
        ("barrels", "BARRELS AND BINS", new[] { "barrel", "bin", "drum", "dumpster", "trash", "skip" }),
        ("vehicles", "CARS AND WRECKS", new[] { "car", "van", "truck", "bus", "taxi", "vehicle", "wreck" }),
        ("street", "STREET FURNITURE", new[] { "lamp", "bench", "hydrant", "cone", "barrier", "sign", "bollard", "post", "mailbox", "phone" }),
        ("fences", "FENCES AND WALLS", new[] { "fence", "wall", "rail", "gate", "barricade" }),
        ("plants", "TREES AND PLANTS", new[] { "tree", "plant", "bush", "shrub", "pot" }),
        ("furniture", "FURNITURE", new[] { "chair", "table", "desk", "sofa", "shelf", "cabinet", "bed", "stool" }),
    };

    private Choice _objSource = null!;
    private Choice _objFilter = null!;
    private Choice _objWho = null!;
    private Toggle _objPhysics = null!;
    private Toggle _objCollision = null!;
    private Slider _objSize = null!;

    private List<MeshRecipe> _meshes = new();

    private void BuildObjects()
    {
        _objSource = AddChoice("obj_source", "FROM", "NEARBY lists the meshes of things around you, nearest first. EVERYTHING LOADED lists every mesh in memory, by name: use SHOW to narrow it.",
            new[] { "nearby", "loaded" }, new[] { "NEARBY", "EVERYTHING LOADED" }, _ => { ReadMeshes(); ListMeshes(); }).Pref();
        _objFilter = AddChoice("obj_filter", "SHOW", "Only meshes whose names suggest this kind of thing.",
            MeshFilters.Select(f => f.Key).ToArray(), MeshFilters.Select(f => f.Label).ToArray(), _ => { ReadMeshes(); ListMeshes(); }).Pref();
        _objWho = AddChoice("obj_who", "OBJECT", "Hold to scroll. The mesh to make objects from.", new[] { "" }, new[] { "OPEN THE MENU IN GAME" }).Pref();
        _objWho.HoldToScroll = true;
        AddCommand("obj_refresh", "REFRESH LIST", "Reads the meshes again, around where you are now.", () => { ReadMeshes(); ListMeshes(); Overlay.Notice(NoticeTitle, Fmt.Plural(_meshes.Count, "MESH") + " LISTED", true); }).Setting();

        _objPhysics = AddToggle("obj_physics", "PHYSICS", "On: the object falls, tumbles and can be knocked over or thrown with the editor's GRAB. Off: it stays exactly where it's put.", on: true).Pref();
        _objCollision = AddToggle("obj_collision", "SOLID", "Off: you and everything else pass through it. Physics objects need it on.", on: true).Pref();
        _objSize = AddSlider("obj_size", "SIZE", "How big spawned objects are, times the size of the piece they were taken from.", 1f, 0.1f, 10f, 0.1f, 2f, Fmt.Times).Pref();
        AddCommand("obj_spawn", "SPAWN OBJECT", "Spawns the object above, HOW MANY times, the way this tab and PLACE say.", () => SpawnObjects(PickedMesh()));
        AddCommand("obj_copy_aimed", "SPAWN WHAT YOU AIM AT", "Copies the piece of the world under your crosshair, whatever it is: a bench, a wrecked car, a lamp post, a chunk of building. Its materials and size come too.", SpawnAimed);
    }

    private void ReadMeshes()
    {
        if (!World.InGame)
            return;
        string[] words = MeshFilters[Math.Clamp(_objFilter.Index, 0, MeshFilters.Length - 1)].Words;
        if (_objSource.Index == 1)
        {
            _meshes = SpawnCatalog.LoadedMeshes(words, LoadedMax);
            return;
        }

        Vector3 from = World.Pawn?.Location ?? Vector3.Zero;
        List<MeshRecipe> near = SpawnCatalog.NearbyMeshes(from, NearbyRange, NearbyMax * 4);
        _meshes = (words.Length == 0 ? near : near.Where(m => words.Any(w => m.Label.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList())
            .Take(NearbyMax).ToList();
    }

    private void ListMeshes()
    {
        if (_meshes.Count == 0)
            _objWho.SetOptions(new[] { "" }, new[] { World.InGame ? "NOTHING MATCHES" : "OPEN THE MENU IN GAME" });
        else
            _objWho.SetOptions(_meshes.Select(m => m.Key).ToArray(), _meshes.Select(m => m.Label).ToArray());
    }

    private MeshRecipe PickedMesh()
    {
        if (_meshes.Count == 0)
            ReadMeshes();
        string key = _objWho.CurrentKey;
        MeshRecipe r = _meshes.Find(m => m.Key == key) ?? throw new InvalidOperationException("no mesh picked: REFRESH LIST");
        if (!r.Mesh.IsValid)
        {
            ReadMeshes();
            ListMeshes();
            throw new InvalidOperationException("that mesh has unloaded: pick again");
        }

        return r;
    }

    /// <summary>The mesh of an actor the editor has selected, or null when it isn't drawn with a static mesh.</summary>
    private static MeshRecipe? MeshRecipeOf(Actor a) => a switch
    {
        DynamicSMActor d when d.StaticMeshComponent is { IsValid: true } c => SpawnCatalog.RecipeOf(c, d),
        StaticMeshActor s when s.StaticMeshComponent is { IsValid: true } c => SpawnCatalog.RecipeOf(c, s),
        _ => null,
    };

    /// <summary>
    /// Whatever mesh the crosshair's ray hits. The hit component is used, not the actor, so a piece of a
    /// StaticMeshCollectionActor (most of the city's scenery) is copied on its own.
    /// </summary>
    private void SpawnAimed()
    {
        Pawn me = RequireReady();
        if (!WorldView.ReadCamera())
            throw new InvalidOperationException("the camera can't be read right now");

        Vector3 eye = WorldView.Eye;
        me.Trace(out _, out _, eye + WorldView.Forward * CrosshairReach, eye, Component.ECollisionFilter.COF_TraceAll, Vector3.Zero, out Actor.FTraceHitInfo info, 0);
        if (info.HitComponent is not StaticMeshComponent { IsValid: true } c)
            throw new InvalidOperationException("aim at an object made of a mesh");

        Actor? owner = c.Owner is { IsValid: true } o ? o : c.Outer as Actor;
        MeshRecipe r = SpawnCatalog.RecipeOf(c, owner) ?? throw new InvalidOperationException("it has no mesh to copy");
        SpawnObjects(r);
    }

    private void SpawnObjects(MeshRecipe r)
    {
        Pawn me = RequireReady();
        bool physics = _objPhysics.On && _objCollision.On;
        int n = Room(Wanted);
        var made = new List<Spawn>();
        int failed = 0;
        List<Spot> spots = Spots(me, n, ObjectFootprint * MathF.Max(1f, _objSize.Value));
        Starting(n, r.Label, r.Key + (physics ? ", physics" : ", static"));
        foreach (Spot spot in spots)
        {
            Vector3 at = spot.Floor + Vector3.UnitZ * ObjectLift * MathF.Max(1f, _objSize.Value);
            Class cls = physics ? KActorSpawnable.StaticClass() : DynamicSMActor_Spawnable.StaticClass();
            if (Game.SpawnActor(cls, default, at, spot.Facing) is not DynamicSMActor a || !a.IsValid)
            {
                failed++;
                continue;
            }

            Spawn s = Track(a, SpawnKind.Object, r.Label, at);
            try
            {
                Dress(a, r, physics);
                s.Settling = physics;
                if (!physics)
                    Safe.Run("spawn object floor", () => SitOnFloor(a, spot.Floor));
                made.Add(s);
            }
            catch (Exception ex)
            {
                Log.Info("Spawner: " + r.Label + " couldn't be made: " + Log.Why(ex));
                _spawns.Remove(s);
                Safe.Run("spawn object discard", () => a.Destroy());
                failed++;
            }
        }

        Finish(made, r.Label, failed, "the game wouldn't place it here");
    }

    /// <summary>Mesh, materials, size and collision, then physics: a KActor only simulates once it has a mesh.</summary>
    private void Dress(DynamicSMActor a, MeshRecipe r, bool physics)
    {
        StaticMeshComponent c = a.StaticMeshComponent ?? throw new InvalidOperationException("no mesh component");
        if (!c.SetStaticMesh(r.Mesh, true))
            Log.Detail("Spawner: SetStaticMesh returned false for " + r.Label + ".");
        for (int i = 0; i < r.Materials.Length; i++)
        {
            if (r.Materials[i] is { IsValid: true } m)
                c.SetMaterial(i, m);
        }

        a.SetDrawScale3D(r.Scale * _objSize.Value);
        if (!_objCollision.On)
        {
            a.SetCollision(false, false, false);
            return;
        }

        a.SetCollision(true, true, false);
        if (physics)
        {
            a.SetPhysics(Actor.EPhysics.PHYS_RigidBody, true);
            c.WakeRigidBody(default);
        }
    }

    /// <summary>A static object is set down with the bottom of its box on the floor, once its box is known.</summary>
    private static void SitOnFloor(Actor a, Vector3 floor)
    {
        Thing t = Things.Wrap(a);
        Things.Measure(t);
        if (t.Max.Z - t.Min.Z < 1f)
            return;
        Vector3 at = a.Location;
        at.Z = floor.Z - t.Min.Z + 2f;
        a.SetLocation(at);
    }

    /// <summary>
    /// A physics object whose mesh has no collision shape falls through the world. In its first seconds one that has
    /// dropped far below where it spawned is taken out of physics and put back, so it's not lost under the city.
    /// </summary>
    private void Settle(Spawn s)
    {
        Actor a = s.Actor;
        if (Clock.Now - s.Born > SettleSeconds)
        {
            s.Settling = false;
            return;
        }

        if (a.Location.Z > s.Origin.Z - FellTooFar)
            return;

        s.Settling = false;
        a.SetPhysics(Actor.EPhysics.PHYS_None, false);
        a.SetLocation(s.Origin);
        Log.Info("Spawner: " + s.Label + " fell through the floor (no collision shape); held where it spawned.");
        Overlay.Notice(NoticeTitle, s.Label + " HAS NO PHYSICS SHAPE: HELD IN PLACE", false);
    }
}
