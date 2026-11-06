using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>The editor's categories, in menu order.</summary>
internal enum ThingKind
{
    Enemy,
    Friendly,
    Vehicle,
    Turret,
    Prop,
    Mover,
    Pickup,

    /// <summary>Anything else, picked by aiming at it: doors, scenery, interactables. Not marked by the ESP.</summary>
    Other,
}

/// <summary>
/// One object the editor can see and select. Kept across scans by pointer, so its name is worked out once and
/// its box only every few seconds. The box is relative to the actor's location, so it follows the actor between scans.
/// </summary>
internal sealed class Thing
{
    public required Actor Actor;
    public required nint Ptr;
    public required nint ClassPtr;
    public required ThingKind Kind;
    public required string Name;
    public Vector3 Min;
    public Vector3 Max;
    public double BoxAt = double.MinValue;
    public float Distance;

    /// <summary>Still the same live object. A freed pointer can be reused by another object, so the class is checked too.</summary>
    public bool Alive => Actor is { IsValid: true, bDeleteMe: false } && Actor.Class is { } c && c.Ptr == ClassPtr;

    public Vector3 Centre => Actor.Location + (Min + Max) * 0.5f;

    /// <summary>Half the box's widest flat side: how far something must stand off to be beside it, not inside it.</summary>
    public float Radius => MathF.Max(Max.X - Min.X, Max.Y - Min.Y) * 0.5f;

    /// <summary>Static or unmovable actors are part of the level; they don't move even when their transform is written.</summary>
    public bool Fixed => Actor.bStatic || !Actor.bMovable;

    public string Key => Ptr.ToString("X");

    public string KindLabel => Things.Labels[(int)Kind];
}

/// <summary>Finds and names the objects in each editor category.</summary>
internal static class Things
{
    /// <summary>The kinds the ESP marks and scans for; <see cref="ThingKind.Other"/> comes after them.</summary>
    public const int KindCount = 7;
    private const double BoxSeconds = 2.0;
    private const float MaxBox = 6000f;

    /// <summary>The biggest a vehicle's box may be on any side: a heavy tank is about 12 m long.</summary>
    private const float MaxVehicleBox = 2000f;
    private const float DefaultHalf = 50f;

    public static readonly string[] Labels = { "ENEMY", "FRIENDLY", "VEHICLE", "TURRET", "PROP", "SCENERY", "PICKUP", "OBJECT" };
    public static readonly string[] Plural = { "ENEMIES", "FRIENDLIES", "VEHICLES", "TURRETS", "PROPS", "MOVABLE SCENERY", "PICKUPS", "OTHER OBJECTS" };

    public static readonly Rgba[] Colors =
    {
        new(255, 86, 76),    // enemies: red
        new(84, 230, 140),   // friendlies: green
        new(255, 170, 60),   // vehicles: orange
        new(255, 90, 200),   // turrets: magenta
        new(110, 190, 255),  // props: blue
        new(190, 150, 255),  // movable scenery: violet
        new(255, 226, 90),   // pickups: gold
        new(200, 210, 210),  // other: grey
    };

    private static readonly Dictionary<nint, Thing> _known = new();

    /// <summary>Forgets every object. Called on a world change, when every pointer goes stale.</summary>
    public static void Forget() => _known.Clear();

    /// <summary>
    /// Fills <paramref name="into"/> with every live object of the wanted kinds within range, nearest first, at most
    /// <paramref name="max"/>. Objects in <paramref name="keep"/> (selected or edited) are kept whatever their range
    /// or visibility, so a hidden edit can still be found and restored.
    /// </summary>
    public static void Scan(List<Thing> into, Vector3 from, float range, bool[] wanted, IReadOnlySet<nint> keep, int max)
    {
        into.Clear();
        float r2 = range * range;
        double now = Clock.Now;
        nint me = World.PawnPtr;
        nint player = World.Player?.Ptr ?? 0;

        // One object that can't be read (half streamed in, being torn down) is skipped, not the whole scan.
        void Consider(Actor a, ThingKind kind)
        {
            try
            {
                Take(a, kind);
            }
            catch (Exception ex)
            {
                if (Log.Due("editor scan"))
                    Log.Info("Editor: skipped " + (a.IsValid ? a.Name.ToString() : "a dead object") + ": " + Log.Why(ex));
            }
        }

        void Take(Actor a, ThingKind kind)
        {
            // bPendingDelete is set while Destroy runs, before bDeleteMe: its parts may already be gone.
            if (a.Ptr == me || a.Ptr == player || a.bDeleteMe || a.bPendingDelete)
                return;

            bool kept = keep.Contains(a.Ptr);
            if (!kept && (!wanted[(int)kind] || a.bHidden || Vector3.DistanceSquared(a.Location, from) > r2))
                return;

            Thing t = Know(a, kind, now);
            t.Distance = Vector3.Distance(a.Location, from);
            into.Add(t);
        }

        foreach (RBMPawnAI p in World.All<RBMPawnAI>())
        {
            if (p is not RPawnCrowdRagdoll)
                Consider(p, p is RPawnVillain ? ThingKind.Enemy : ThingKind.Friendly);
        }

        foreach (RPawnPlayer p in World.All<RPawnPlayer>())
        {
            if (p.Controller is AIController { IsValid: true })
                Consider(p, ThingKind.Friendly);
        }

        foreach (RVehicle v in World.All<RVehicle>())
            Consider(v, ThingKind.Vehicle);

        // Parked and wrecked cars are animated mesh props, not driving vehicles, but they read as vehicles.
        foreach (RAbandonedVehicle v in World.All<RAbandonedVehicle>())
            Consider(v, ThingKind.Vehicle);
        foreach (RDeadVehicle v in World.All<RDeadVehicle>())
            Consider(v, ThingKind.Vehicle);
        foreach (RSentryGunBase g in World.All<RSentryGunBase>())
            Consider(g, ThingKind.Turret);
        foreach (RTurretBase g in World.All<RTurretBase>())
            Consider(g, ThingKind.Turret);
        foreach (KActor k in World.All<KActor>())
            Consider(k, ThingKind.Prop);
        foreach (ApexDestructibleActor d in World.All<ApexDestructibleActor>())
            Consider(d, ThingKind.Prop);
        foreach (InterpActor m in World.All<InterpActor>())
            Consider(m, ThingKind.Mover);
        foreach (RPickupBaseBase p in World.All<RPickupBaseBase>())
            Consider(p, ThingKind.Pickup);
        foreach (DroppedPickup p in World.All<DroppedPickup>())
            Consider(p, ThingKind.Pickup);

        // Kept objects no scan above finds (one picked by aiming at it) come along too.
        foreach (nint ptr in keep)
        {
            if (_known.TryGetValue(ptr, out Thing? k) && k.Alive && !into.Contains(k))
            {
                k.Distance = Vector3.Distance(k.Actor.Location, from);
                into.Add(k);
            }
        }

        into.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (into.Count > max)
        {
            // Trim the furthest, but never a kept object.
            var trimmed = into.Take(max).ToList();
            trimmed.AddRange(into.Skip(max).Where(t => keep.Contains(t.Ptr)));
            into.Clear();
            into.AddRange(trimmed);
        }

        // Aiming around records scenery too, so the cache is emptied now and then. A selected or edited object is never
        // dropped: its edits point at that record.
        if (_known.Count > 4096)
        {
            foreach (nint gone in _known.Keys.Where(p => !keep.Contains(p)).ToArray())
                _known.Remove(gone);
        }
    }

    /// <summary>The category an actor belongs in, for one picked by aiming rather than found by a scan.</summary>
    public static ThingKind KindOf(Actor a) => a switch
    {
        RPawnVillain => ThingKind.Enemy,
        RBMPawnAI or RPawnPlayer => ThingKind.Friendly,
        RVehicle or RAbandonedVehicle or RDeadVehicle => ThingKind.Vehicle,
        RSentryGunBase or RTurretBase => ThingKind.Turret,
        KActor or ApexDestructibleActor => ThingKind.Prop,
        InterpActor => ThingKind.Mover,
        RPickupBaseBase or DroppedPickup => ThingKind.Pickup,
        _ => ThingKind.Other,
    };

    /// <summary>The editor's record of any actor, the same one a scan would give.</summary>
    public static Thing Wrap(Actor a) => Know(a, KindOf(a), Clock.Now);

    private static Thing Know(Actor a, ThingKind kind, double now)
    {
        nint cls = a.Class?.Ptr ?? 0;
        if (!_known.TryGetValue(a.Ptr, out Thing? t) || t.ClassPtr != cls)
        {
            t = new Thing { Actor = a, Ptr = a.Ptr, ClassPtr = cls, Kind = kind, Name = NameOf(a, kind) };
            _known[a.Ptr] = t;
        }

        t.Kind = kind;
        if (now - t.BoxAt >= BoxSeconds)
        {
            t.BoxAt = now;
            Measure(t);
        }

        return t;
    }

    /// <summary>
    /// A pawn's box is its collision cylinder, which is centred on its location. Anything else uses its components'
    /// bounds; a box that is missing or absurdly large (a component far off in the level) becomes a metre cube.
    /// </summary>
    public static void Measure(Thing t)
    {
        Actor a = t.Actor;
        if (a is Pawn p and not Vehicle && p.CylinderComponent is { IsValid: true } cyl)
        {
            float r = cyl.CollisionRadius, h = cyl.CollisionHeight;
            if (r > 1f && h > 1f)
            {
                t.Min = new Vector3(-r, -r, -h);
                t.Max = new Vector3(r, r, h);
                return;
            }
        }

        // The box is summed here from the components' own bounds rather than taken from Actor.GetComponentsBoundingBox,
        // because the editor wants a different answer from the engine's (see below: what you can see wins, and only
        // touching parts count). The engine's box is the fallback when no part can be read.
        // A vehicle's component list couldn't be read in game (2026-11-05: every RVehicleCar threw), and its skeletal
        // mesh covers the whole vehicle anyway, so vehicles skip straight to the mesh below.
        // What you can see wins over what collides: an interaction trigger can be far bigger than the thing it belongs
        // to (an upside-down abandoned car got a box several times its size, 2026-11-06). Colliding parts only count
        // when nothing visible does. Then only parts that touch each other count, starting from the one the actor
        // stands in, so a piece lying metres away doesn't stretch the box (upside-down cars again, 2026-11-06).
        Vector3 at = a.Location;
        float limit = t.Kind == ThingKind.Vehicle ? MaxVehicleBox : MaxBox;
        _meshParts.Clear();
        _colParts.Clear();
        bool partsReadable = false;
        if (a is not Vehicle)
        {
            try
            {
                foreach (ActorComponent c in a.Components)
                {
                    if (c is MeshComponent { IsValid: true, bAttached: true, HiddenGame: false } mesh)
                        Collect(mesh, at, limit, _meshParts);
                    else if (c is PrimitiveComponent { IsValid: true, bAttached: true, CollideActors: true } prim)
                        Collect(prim, at, limit, _colParts);
                }

                partsReadable = true;
            }
            catch (Exception)
            {
                // Unreadable parts: fall back to the main component below.
                _meshParts.Clear();
                _colParts.Clear();
            }
        }

        List<Part> parts = _meshParts.Count > 0 ? _meshParts : _colParts;
        if (parts.Count == 0)
        {
            // Some RVehicleCars throw from the Mesh getter itself (2026-11-05, every few seconds in the log), so a
            // part that can't be read leaves the default box instead of skipping the whole thing.
            // No generic CollisionComponent fallback: an actor with no attached colliding part is usually being torn
            // down, and reading its CollisionComponent then hit a freed object and crashed the game (2026-11-05, NG+
            // opening scene, an access violation no try/catch can stop). Such actors get the default box.
            PrimitiveComponent? main;
            try
            {
                main = a switch
                {
                    Vehicle v when v.Mesh is { IsValid: true } m => m,
                    SkeletalMeshActor k when k.SkeletalMeshComponent is { IsValid: true } sk => sk,
                    DynamicSMActor d when d.StaticMeshComponent is { IsValid: true } s => s,
                    _ => null,
                };
            }
            catch (Exception)
            {
                main = null;
            }

            if (main is { IsValid: true })
                Collect(main, at, limit, parts);
        }

        if (Cluster(parts, at, out Vector3 min, out Vector3 max) && Sane(max - min, limit))
        {
            t.Min = min - at;
            t.Max = max - at;
            return;
        }

        // The engine's own box (every attached colliding component) when nothing above gave one. Only for an actor
        // whose component list was readable: one being torn down may hold freed components, and this is a native
        // call into them. Needs the BmSDK fork (the call's aligned buffer); it crashed stock BmSDK.
        if (parts.Count == 0 && partsReadable)
        {
            try
            {
                a.GetComponentsBoundingBox(out GameObject.FBox box);
                if (box.IsValid != 0 && Sane(box.Max - box.Min, limit))
                {
                    t.Min = box.Min - at;
                    t.Max = box.Max - at;
                    return;
                }
            }
            catch (Exception)
            {
                // The default box below.
            }
        }

        t.Min = new Vector3(-DefaultHalf);
        t.Max = new Vector3(DefaultHalf);
    }

    /// <summary>A box bigger than a point and smaller than the limit on every side.</summary>
    private static bool Sane(Vector3 size, float limit) =>
        size.X > 1f && size.Y > 1f && size.Z > 1f && size.X < limit && size.Y < limit && size.Z < limit;

    private readonly record struct Part(Vector3 Min, Vector3 Max);

    private static readonly List<Part> _meshParts = new();
    private static readonly List<Part> _colParts = new();

    /// <summary>How close two parts must come to count as one object, in world units.</summary>
    private const float Touch = 60f;

    /// <summary>
    /// Keeps one component's bounds as a part. Ignores a component that has no size yet, and one bigger than
    /// <paramref name="limit"/> or centred further than that from the actor: a dead vehicle's parts once added up to
    /// a box that filled the screen from 50 m away (2026-11-06).
    /// </summary>
    private static void Collect(PrimitiveComponent c, Vector3 at, float limit, List<Part> into)
    {
        GameObject.FBoxSphereBounds b = c.Bounds;
        Vector3 e = b.BoxExtent;
        if (!(e.X > 0f && e.Y > 0f && e.Z > 0f) || !float.IsFinite(b.Origin.X))
            return;

        Vector3 lo = b.Origin - e, hi = b.Origin + e;
        if (c is SkeletalMeshComponent sk && Posed(sk, out Vector3 plo, out Vector3 phi) && Volume(phi - plo) < Volume(hi - lo))
            (lo, hi) = (plo, phi);

        Vector3 size = hi - lo;
        if (size.X > limit || size.Y > limit || size.Z > limit || Vector3.Distance((lo + hi) * 0.5f, at) > limit)
            return;

        into.Add(new Part(lo, hi));
    }

    private static float Volume(Vector3 s) => s.X * s.Y * s.Z;

    /// <summary>
    /// A skeletal mesh's own box as built (the asset's rest-pose bounds) turned and placed by the component's transform.
    /// The bounds the engine keeps for a skeletal mesh come from its physics bodies and can be far bigger than the
    /// model: two abandoned cars reported 6.6 x 14.6 m and 7.5 x 10.5 m, each from its one mesh (2026-11-06).
    /// LocalToWorld is read from memory: natives returning an FMatrix can crash (see BatmobileWinch).
    /// </summary>
    private static unsafe bool Posed(SkeletalMeshComponent c, out Vector3 lo, out Vector3 hi)
    {
        lo = hi = default;
        if (c.SkeletalMesh is not { IsValid: true } mesh)
            return false;

        GameObject.FBoxSphereBounds b = mesh.Bounds;
        Vector3 e = b.BoxExtent;
        if (!(e.X > 0f && e.Y > 0f && e.Z > 0f) || !float.IsFinite(b.Origin.X))
            return false;

        // Rows X, Y, Z, W: UE3 row vectors, like System.Numerics.
        float* m = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref c.LocalToWorld);
        var toWorld = new Matrix4x4(
            m[0], m[1], m[2], 0f,
            m[4], m[5], m[6], 0f,
            m[8], m[9], m[10], 0f,
            m[12], m[13], m[14], 1f);

        lo = new Vector3(float.MaxValue);
        hi = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = b.Origin + new Vector3((i & 1) == 0 ? -e.X : e.X, (i & 2) == 0 ? -e.Y : e.Y, (i & 4) == 0 ? -e.Z : e.Z);
            Vector3 w = Vector3.Transform(corner, toWorld);
            lo = Vector3.Min(lo, w);
            hi = Vector3.Max(hi, w);
        }

        Vector3 size = hi - lo;
        return float.IsFinite(size.X) && float.IsFinite(size.Y) && float.IsFinite(size.Z) && size.X > 1f && size.Y > 1f && size.Z > 1f;
    }

    /// <summary>
    /// The box around the parts that hang together: it starts from the part the actor stands in (or the nearest) and
    /// takes in every part that touches it, until nothing more does.
    /// </summary>
    private static bool Cluster(List<Part> parts, Vector3 at, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        if (parts.Count == 0)
            return false;

        int seed = 0;
        float best = float.MaxValue;
        for (int i = 0; i < parts.Count; i++)
        {
            float d = Vector3.Distance(at, Vector3.Clamp(at, parts[i].Min, parts[i].Max));
            if (d < best)
            {
                best = d;
                seed = i;
            }
        }

        Span<bool> taken = parts.Count <= 128 ? stackalloc bool[parts.Count] : new bool[parts.Count];
        taken[seed] = true;
        min = parts[seed].Min;
        max = parts[seed].Max;
        var touch = new Vector3(Touch);
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i < parts.Count; i++)
            {
                if (taken[i])
                    continue;
                Part p = parts[i];
                Vector3 lo = min - touch, hi = max + touch;
                if (p.Max.X < lo.X || p.Max.Y < lo.Y || p.Max.Z < lo.Z || p.Min.X > hi.X || p.Min.Y > hi.Y || p.Min.Z > hi.Z)
                    continue;

                taken[i] = true;
                min = Vector3.Min(min, p.Min);
                max = Vector3.Max(max, p.Max);
                grew = true;
            }
        }

        return true;
    }

    // ---- names

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "R", "SM", "SK", "SKM", "SKEL", "SKMESH", "MESH", "CHAR", "BASE", "DEFAULT", "BM", "LOD", "PAWN", "ACTOR", "KACTOR", "ARCHETYPE", "ARCH",
    };

    /// <summary>
    /// A short readable name. Props and characters are named after their mesh, which says what they are (a crate,
    /// a medic); a class name only says how they work. Vehicles, turrets and pickups are named after their class.
    /// </summary>
    private static string NameOf(Actor a, ThingKind kind)
    {
        string raw = "";
        try
        {
            raw = kind switch
            {
                ThingKind.Prop or ThingKind.Mover => (a as DynamicSMActor)?.StaticMeshComponent?.StaticMesh?.Name.ToString() ?? "",
                ThingKind.Other => a switch
                {
                    DynamicSMActor d => d.StaticMeshComponent?.StaticMesh?.Name.ToString() ?? "",
                    StaticMeshActor s => s.StaticMeshComponent?.StaticMesh?.Name.ToString() ?? "",
                    SkeletalMeshActor k => k.SkeletalMeshComponent?.SkeletalMesh?.Name.ToString() ?? "",
                    _ => "",
                },
                ThingKind.Enemy or ThingKind.Friendly => (a as Pawn)?.Mesh?.SkeletalMesh?.Name.ToString() ?? "",
                _ => "",
            };
        }
        catch (Exception)
        {
            raw = "";
        }

        string name = Pretty(raw);
        if (name.Length == 0)
            name = Pretty(a.Class?.Name.ToString() ?? "");
        return name.Length == 0 ? Labels[(int)kind] : name;
    }

    /// <summary>Splits an asset or class name into upper-case words, dropping prefixes like SM_ and R that say nothing.</summary>
    public static string Pretty(string raw)
    {
        if (raw.Length == 0)
            return "";

        var words = new List<string>();
        var word = new StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c is '_' or ' ' or '-' or '.')
            {
                Flush();
                continue;
            }

            // A new word at a lower-to-upper change, or where letters meet digits.
            if (word.Length > 0)
            {
                char prev = word[^1];
                bool upperAfterLower = char.IsUpper(c) && char.IsLower(prev);
                bool upperRun = char.IsUpper(c) && char.IsUpper(prev) && i + 1 < raw.Length && char.IsLower(raw[i + 1]);
                bool digitEdge = char.IsDigit(c) != char.IsDigit(prev);
                if (upperAfterLower || upperRun || digitEdge)
                    Flush();
            }

            word.Append(c);
        }

        Flush();

        // A class prefix: "RVehicleTank" reads as R + Vehicle + Tank.
        while (words.Count > 1 && Noise.Contains(words[0]))
            words.RemoveAt(0);
        words.RemoveAll(w => Noise.Contains(w) && words.Count > 1);

        string name = string.Join(' ', words).ToUpperInvariant();
        return name.Length > 26 ? name.Substring(0, 26).TrimEnd() : name;

        void Flush()
        {
            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }
    }
}
