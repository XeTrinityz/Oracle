using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// The winch aims from a 'WinchOrigin' socket on the car's mesh (UpdateValidTargets) and fires, pulls and reels in
/// from 'WinchAttach'. The DLC skins' meshes (1966, 1989, both Tumblers, Batman v Superman, Arkham Asylum) have
/// neither, so every target fails from the world origin and the winch never offers itself. With BATTLE MODE ON ANY
/// SKIN on, Oracle adds both sockets to such a mesh, just ahead of the front wheels on its root bone.
/// </summary>
internal sealed unsafe partial class Batmobile
{
    private static readonly string[] WinchSockets = { "WinchOrigin", "WinchAttach" };

    // Ahead of the front axle and up from it, in units: roughly where the Arkham Knight car's winch door is.
    private const float WinchAhead = 140f;
    private const float WinchUp = 60f;

    private nint _winchMeshDone;

    /// <summary>Adds the winch sockets once per mesh, if they are missing.</summary>
    private void EnsureWinchSockets(RVehicleBatmobileBase car)
    {
        if (car.Mesh is not { IsValid: true } comp || comp.SkeletalMesh is not { IsValid: true } mesh || mesh.Ptr == _winchMeshDone)
            return;

        _winchMeshDone = mesh.Ptr;
        Safe.Run("winch sockets", () =>
        {
            var missing = WinchSockets.Where(n => comp.GetSocketByName(new FName(n)) is not { IsValid: true }).ToList();
            if (missing.Count == 0)
                return;

            if (!WinchPoint(car, comp, out FName bone, out Vector3 local, out string how))
            {
                Log.Info("Winch sockets: couldn't place them on " + mesh.GetPathName(null) + " (" + how + ").");
                return;
            }

            foreach (string name in missing)
            {
                var socket = new SkeletalMeshSocket(mesh, "Oracle" + name, default, null!);
                socket.SocketName = new FName(name);
                socket.BoneName = bone;
                socket.RelativeLocation = local;
                socket.RelativeRotation = default;
                socket.RelativeScale = Vector3.One;
                mesh.Sockets.Add(socket);
            }

            Log.Info("Winch sockets: added " + string.Join(", ", missing) + " to " + mesh.GetPathName(null) + " on bone " + bone + " at " + Fmt.Position(local) + " (" + how + ").");
        });
    }

    /// <summary>
    /// The root bone, and the point ahead of the front wheels in that bone's own space. Everything is read from memory
    /// (LocalToWorld, SpaceBases) rather than through GetBoneQuaternion and friends: one read per bone, no call into
    /// the game. (Those calls crashed stock BmSDK; the fork fixed that, so this is now only about speed.)
    /// </summary>
    private static bool WinchPoint(RVehicleBatmobileBase car, SkeletalMeshComponent comp, out FName bone, out Vector3 local, out string how)
    {
        local = default;
        bone = comp.GetBoneName(0);
        if (bone.Index == 0 || !Atom(comp, 0, out Quaternion rootRot, out Vector3 rootAt, out float rootScale))
        {
            how = "no root bone transform";
            return false;
        }

        // LocalToWorld is a ref into the component: 16 floats, rows X, Y, Z, W (UE3 row vectors, like System.Numerics).
        float* m = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref comp.LocalToWorld);
        var toWorld = new Matrix4x4(
            m[0], m[1], m[2], 0f,
            m[4], m[5], m[6], 0f,
            m[8], m[9], m[10], 0f,
            m[12], m[13], m[14], 1f);
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toComp))
        {
            how = "the mesh transform can't be inverted";
            return false;
        }

        Vector3 forwardWorld = car.Rotation.ToDirection() with { Z = 0f };
        if (forwardWorld.LengthSquared() < 0.01f)
            forwardWorld = Vector3.UnitX;
        Vector3 forward = SafeNormal(Vector3.TransformNormal(Vector3.Normalize(forwardWorld), toComp));
        Vector3 up = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, toComp));

        // The two wheels furthest forward give the front axle; failing that, half the car's length ahead.
        var wheels = new List<Vector3>();
        foreach (SVehicleWheel wheel in car.Wheels)
        {
            if (wheel is { IsValid: true } && wheel.BoneName.Index != 0 && comp.MatchRefBone(wheel.BoneName) is >= 0 and var index && Atom(comp, index, out _, out Vector3 at, out _))
                wheels.Add(at);
        }

        Vector3 front;
        if (wheels.Count >= 2)
        {
            wheels.Sort((a, b) => Vector3.Dot(b, forward).CompareTo(Vector3.Dot(a, forward)));
            front = (wheels[0] + wheels[1]) * 0.5f;
            how = "front wheels";
        }
        else
        {
            front = Vector3.Transform(car.Location, toComp) + forward * MathF.Max(car.PursuitModeLength * 0.5f, 200f);
            how = "half the car's length";
        }

        Vector3 point = front + forward * WinchAhead + up * WinchUp;
        local = Vector3.Transform(point - rootAt, Quaternion.Inverse(rootRot)) / (rootScale > 0.01f ? rootScale : 1f);
        return true;
    }

    private static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitX;

    /// <summary>One bone's component-space transform, read raw: FBoneAtom is a 32-byte quat, translation, scale (as the game indexes it).</summary>
    private static bool Atom(SkeletalMeshComponent comp, int index, out Quaternion rot, out Vector3 at, out float scale)
    {
        rot = Quaternion.Identity;
        at = default;
        scale = 1f;
        nint array = comp.SpaceBases.Ptr;
        if (array == 0 || index < 0)
            return false;

        float* data = *(float**)array;
        int count = *(int*)(array + 8);
        if (data == null || index >= count)
            return false;

        float* atom = data + index * 8;
        rot = new Quaternion(atom[0], atom[1], atom[2], atom[3]);
        at = new Vector3(atom[4], atom[5], atom[6]);
        scale = atom[7];
        if (rot.LengthSquared() < 0.5f || float.IsNaN(rot.W))
            return false;
        rot = Quaternion.Normalize(rot);
        return true;
    }
}
