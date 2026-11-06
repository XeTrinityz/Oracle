using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// What an object was like before the editor first touched it, and what the editor has done to it since. RESTORE
/// puts back exactly the parts that were changed, so an object the game moved on its own isn't dragged back
/// unless the editor moved it too.
/// </summary>
internal sealed class Edit
{
    public required Thing Thing;

    // ---- as found
    public Vector3 Location;
    public Rotator Rotation;
    public float DrawScale;
    public bool Hidden;
    public bool CollideActors;
    public bool BlockActors;
    public bool IgnoreEncroachers;
    public Actor.EPhysics Physics;
    public bool TickDisabled;

    // ---- changed
    public bool Moved;
    public float Forward, Side, Up;
    public float Yaw, Pitch, Roll;
    public float Scale = 1f;
    public bool Frozen;
    public Vector3 FrozenAt;
    public Rotator FrozenRotation;
    public bool Removed;
    public bool Ghost;

    public bool Changed => Moved || Scale != 1f || Frozen || Removed || Ghost;
}

/// <summary>The editor's undo journal and the moves every kind of object shares.</summary>
internal static class Edits
{
    private const float HoldTolerance = 4f;

    private static readonly Dictionary<nint, Edit> _edits = new();

    public static int Count => _edits.Values.Count(e => e.Changed);

    /// <summary>True while any object has been changed, for the editor's per-tick checks.</summary>
    public static bool HasChanges
    {
        get
        {
            foreach (Edit e in _edits.Values)
            {
                if (e.Changed)
                    return true;
            }

            return false;
        }
    }

    public static IReadOnlyCollection<nint> Pointers => _edits.Keys;

    public static Edit? Find(Thing t) => _edits.TryGetValue(t.Ptr, out Edit? e) && e.Thing == t ? e : null;

    /// <summary>The journal entry for an object, recording how it is now if this is the first edit.</summary>
    public static Edit Begin(Thing t)
    {
        if (Find(t) is { } found)
            return found;

        Actor a = t.Actor;
        var e = new Edit
        {
            Thing = t,
            Location = a.Location,
            Rotation = a.Rotation,
            DrawScale = a.DrawScale,
            Hidden = a.bHidden,
            CollideActors = a.bCollideActors,
            BlockActors = a.bBlockActors,
            IgnoreEncroachers = a.bIgnoreEncroachers,
            Physics = a.Physics,
            TickDisabled = a.bTickIsDisabled,
        };
        _edits[t.Ptr] = e;
        return e;
    }

    /// <summary>Drops every entry without touching the game. For a world change, when the objects are gone anyway.</summary>
    public static void Forget() => _edits.Clear();

    // ---- kinds of body

    /// <summary>Simulated by PhysX: vehicles and loose props. Moving one means moving its rigid body too.</summary>
    public static bool IsRigid(Actor a) => a is SVehicle || a.Physics == Actor.EPhysics.PHYS_RigidBody;

    /// <summary>A character on foot, as opposed to a vehicle (which is a pawn too).</summary>
    public static bool IsCharacter(Actor a) => a is Pawn and not Vehicle;

    private static PrimitiveComponent? Body(Actor a)
    {
        PrimitiveComponent? body = a is Vehicle v && v.Mesh is { IsValid: true } mesh ? mesh : a.CollisionComponent;
        return body is { IsValid: true } ? body : null;
    }

    // ---- moving

    /// <summary>
    /// Moves any object and stops it dead. A rigid body is moved with it or it snaps straight back; the Batmobile goes
    /// through the teleport's own placement, which also resets its driving state. SetLocation refuses a spot that
    /// overlaps something, so a refused move is tried again with collision briefly off.
    /// </summary>
    public static void Put(Actor a, Vector3 at, Rotator rot)
    {
        if (a is RVehicleBatmobileBase car)
        {
            Teleport.Place(car, at, rot);
            return;
        }

        a.SetRotation(rot);
        if (!a.SetLocation(at))
        {
            bool collide = a.bCollideActors, block = a.bBlockActors, ignore = a.bIgnoreEncroachers;
            a.SetCollision(false, false, ignore);
            a.SetLocation(at);
            a.SetCollision(collide, block, ignore);
        }

        a.Velocity = Vector3.Zero;
        if (IsRigid(a) && Body(a) is { } body)
        {
            body.SetRBPosition(at, default);
            body.SetRBRotation(rot, default);
            body.SetRBLinearVelocity(Vector3.Zero, false);
            body.SetRBAngularVelocity(Vector3.Zero, false);
            body.WakeRigidBody(default);
        }
    }

    /// <summary>Sets an object moving: a character falls through the air, a rigid body is given the velocity.</summary>
    public static void Fling(Actor a, Vector3 velocity, Vector3 spin)
    {
        if (IsCharacter(a))
        {
            a.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
            a.Velocity = velocity;
            return;
        }

        if (IsRigid(a) && Body(a) is { } body)
        {
            body.WakeRigidBody(default);
            body.SetRBLinearVelocity(velocity, false);
            body.SetRBAngularVelocity(spin, false);
            return;
        }

        throw new InvalidOperationException("it isn't a physics object");
    }

    // ---- state

    /// <summary>
    /// Stops an object thinking and moving. Characters and vehicles stop ticking (a character's AI and animation
    /// too) and are held where they are. A loose prop is taken out of physics so it hangs in place.
    /// </summary>
    public static void SetFrozen(Edit e, bool frozen)
    {
        Actor a = e.Thing.Actor;
        if (frozen == e.Frozen)
            return;

        e.Frozen = frozen;
        if (frozen)
        {
            e.FrozenAt = a.Location;
            e.FrozenRotation = a.Rotation;
        }

        // A removed object is already stopped, and must stay stopped until it's restored.
        if (e.Removed)
            return;

        if (a is Pawn p)
            Npcs.SetFrozen(p, frozen);
        else if (a.Physics == Actor.EPhysics.PHYS_RigidBody || (!frozen && e.Physics == Actor.EPhysics.PHYS_RigidBody))
        {
            a.SetPhysics(frozen ? Actor.EPhysics.PHYS_None : e.Physics, true);
        }
        else
        {
            a.SetTickIsDisabled(frozen || e.TickDisabled);
        }

        if (frozen)
            return;

        // A vehicle that was removed while frozen came back out of physics; it goes back in now.
        if (a.Physics != e.Physics && e.Physics == Actor.EPhysics.PHYS_RigidBody)
            a.SetPhysics(e.Physics, true);
        if (IsRigid(a) && Body(a) is { } body)
            body.WakeRigidBody(default);
    }

    /// <summary>Called every tick: keeps frozen objects where they were frozen.</summary>
    public static void Hold()
    {
        foreach (Edit e in _edits.Values)
        {
            if (!e.Frozen || e.Removed || !e.Thing.Alive)
                continue;

            Actor a = e.Thing.Actor;
            if (a is Pawn p)
                Npcs.SetFrozen(p, true);
            if (Vector3.DistanceSquared(a.Location, e.FrozenAt) > HoldTolerance * HoldTolerance)
                Put(a, e.FrozenAt, e.FrozenRotation);
        }
    }

    /// <summary>
    /// REMOVE: hidden, no collision, not thinking and out of physics. Nothing is destroyed, so a mission that still
    /// refers to the object can't break, and RESTORE brings it back as it was.
    /// </summary>
    public static void SetRemoved(Edit e, bool removed)
    {
        Actor a = e.Thing.Actor;
        if (removed == e.Removed)
            return;

        e.Removed = removed;
        if (removed)
        {
            if (a is Pawn p)
                Npcs.SetFrozen(p, true);
            else
                a.SetTickIsDisabled(true);
            if (!IsCharacter(a) && a.Physics == Actor.EPhysics.PHYS_RigidBody)
                a.SetPhysics(Actor.EPhysics.PHYS_None, false);
            a.SetCollision(false, false, e.IgnoreEncroachers);
            a.SetHidden(true);
            return;
        }

        a.SetHidden(e.Hidden);
        if (!e.Ghost)
            a.SetCollision(e.CollideActors, e.BlockActors, e.IgnoreEncroachers);
        if (!e.Frozen)
        {
            if (a is Pawn p)
                Npcs.SetFrozen(p, false);
            else
                a.SetTickIsDisabled(e.TickDisabled);
            if (a.Physics != e.Physics && e.Physics == Actor.EPhysics.PHYS_RigidBody)
                a.SetPhysics(e.Physics, true);
        }
    }

    /// <summary>GHOST: no collision, so you and everything else pass through it. It stays visible and keeps working.</summary>
    public static void SetGhost(Edit e, bool ghost)
    {
        if (ghost == e.Ghost)
            return;

        e.Ghost = ghost;
        if (!e.Removed)
        {
            Actor a = e.Thing.Actor;
            if (ghost)
                a.SetCollision(false, false, e.IgnoreEncroachers);
            else
                a.SetCollision(e.CollideActors, e.BlockActors, e.IgnoreEncroachers);
        }
    }

    // ---- undo

    /// <summary>Puts one object back the way it was found. Returns false if it had nothing to undo.</summary>
    public static bool Restore(Edit e)
    {
        _edits.Remove(e.Thing.Ptr);
        if (!e.Changed || !e.Thing.Alive)
            return false;

        Actor a = e.Thing.Actor;
        SetRemoved(e, false);
        SetGhost(e, false);
        SetFrozen(e, false);
        if (e.Scale != 1f)
            a.SetDrawScale(e.DrawScale);
        if (e.Moved)
            Put(a, e.Location, e.Rotation);
        return true;
    }

    /// <summary>Puts every edited object back. Returns how many there were.</summary>
    public static int RestoreAll()
    {
        int n = 0;
        foreach (Edit e in _edits.Values.ToArray())
        {
            try
            {
                if (Restore(e))
                    n++;
            }
            catch (Exception ex)
            {
                Log.Info("Editor: restoring " + e.Thing.Name + " failed: " + ex.Message);
            }
        }

        _edits.Clear();
        return n;
    }
}
