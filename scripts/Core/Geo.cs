using System.Numerics;

namespace CeZeY.Oracle;

/// <summary>World-space helpers. Unreal units are centimetres and rotations are in degrees.</summary>
internal static class Geo
{
    /// <summary>Distance on the ground plane, ignoring height.</summary>
    public static float DistXY(Vector3 a, Vector3 b) => MathF.Sqrt(DistXYSq(a, b));

    public static float DistXYSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>An angle brought into -180..180 degrees.</summary>
    public static float WrapDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)
            degrees -= 360f;
        else if (degrees < -180f)
            degrees += 360f;
        return degrees;
    }

    /// <summary>The flat unit direction a yaw faces.</summary>
    public static Vector3 Facing(float yawDegrees)
    {
        float r = yawDegrees * MathF.PI / 180f;
        return new Vector3(MathF.Cos(r), MathF.Sin(r), 0f);
    }
}
