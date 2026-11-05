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
                Log.Throttled("editor scan", "Editor: skipped " + (a.IsValid ? a.Name.ToString() : "a dead object") + ": " + Log.Why(ex));
            }
        }

        void Take(Actor a, ThingKind kind)
        {
            if (a.Ptr == me || a.Ptr == player || a.bDeleteMe)
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

        // Never call Actor.GetComponentsBoundingBox: its out FBox overran BmSDK's buffer and crashed the game in
        // coreclr (2026-11-05). This is the same sum done here from properties the engine keeps up to date: the
        // bounds of every attached component that collides (UE3's AActor::GetComponentsBoundingBox). Only when none
        // does is the main component's bounds used on its own.
        // A vehicle's component list couldn't be read in game (2026-11-05: every RVehicleCar threw), and its skeletal
        // mesh covers the whole vehicle anyway, so vehicles skip straight to the mesh below.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;
        if (a is not Vehicle)
        {
            try
            {
                foreach (ActorComponent c in a.Components)
                {
                    if (c is PrimitiveComponent { IsValid: true, bAttached: true, CollideActors: true } prim)
                        any |= Add(prim, ref min, ref max);
                }
            }
            catch (Exception)
            {
                // Unreadable parts: fall back to the main component below.
                min = new Vector3(float.MaxValue);
                max = new Vector3(float.MinValue);
                any = false;
            }
        }

        if (!any)
        {
            // Some RVehicleCars throw from the Mesh getter itself (2026-11-05, every few seconds in the log), so a
            // part that can't be read leaves the default box instead of skipping the whole thing.
            PrimitiveComponent? main;
            try
            {
                main = a switch
                {
                    Vehicle v when v.Mesh is { IsValid: true } m => m,
                    SkeletalMeshActor k when k.SkeletalMeshComponent is { IsValid: true } sk => sk,
                    DynamicSMActor d when d.StaticMeshComponent is { IsValid: true } s => s,
                    _ => a.CollisionComponent,
                };
            }
            catch (Exception)
            {
                main = null;
            }

            if (main is { IsValid: true })
                any = Add(main, ref min, ref max);
        }

        Vector3 size = max - min;
        if (any && size.X > 1f && size.Y > 1f && size.Z > 1f && size.X < MaxBox && size.Y < MaxBox && size.Z < MaxBox)
        {
            Vector3 at = a.Location;
            t.Min = min - at;
            t.Max = max - at;
            return;
        }

        t.Min = new Vector3(-DefaultHalf);
        t.Max = new Vector3(DefaultHalf);
    }

    /// <summary>Grows a box to take in one component's bounds. Ignores a component that has no size yet.</summary>
    private static bool Add(PrimitiveComponent c, ref Vector3 min, ref Vector3 max)
    {
        GameObject.FBoxSphereBounds b = c.Bounds;
        Vector3 e = b.BoxExtent;
        if (!(e.X > 0f && e.Y > 0f && e.Z > 0f) || !float.IsFinite(b.Origin.X))
            return false;

        min = Vector3.Min(min, b.Origin - e);
        max = Vector3.Max(max, b.Origin + e);
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
