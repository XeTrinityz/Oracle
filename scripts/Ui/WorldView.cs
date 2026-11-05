using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// World-to-screen projection for drawing over the game world. The camera comes from GetPlayerViewPoint once a
/// frame and every point is projected here in C#, so a box's eight corners cost nothing. The scale is calibrated
/// each frame from one Canvas.Project call on a probe point, so it follows the game's real field of view and
/// aspect; if that call ever gives nonsense, the camera's FOV angle stands in.
/// </summary>
internal static class WorldView
{
    private const float ProbeAhead = 1000f;
    private const float ProbeSide = 250f;
    private const float NearPlane = 10f;

    private static float _cx, _cy, _kx, _ky;
    private static bool _reported;

    /// <summary>True once this frame's camera is known. Stays true (with last frame's camera) between frames.</summary>
    public static bool Ready { get; private set; }

    public static Vector3 Eye { get; private set; }
    public static Vector3 Forward { get; private set; }
    public static Vector3 Right { get; private set; }
    public static Vector3 Up { get; private set; }
    public static Rotator Rotation { get; private set; }

    public static float Width => _cx * 2f;
    public static float Height => _cy * 2f;

    /// <summary>Reads the camera for this frame. Call once at the start of drawing.</summary>
    public static void Begin(Canvas canvas)
    {
        Ready = false;
        if (World.Pc is not { } pc)
            return;

        if (!ReadCamera(pc))
            return;

        _cx = canvas.ClipX * 0.5f;
        _cy = canvas.ClipY * 0.5f;
        if (_cx <= 0f || _cy <= 0f)
            return;

        // The probe sits ahead of the camera, a quarter of the way out to the right and up, so its screen offset
        // from the centre is the projection scale times 0.25 on each axis.
        float ratio = ProbeSide / ProbeAhead;
        Vector3 s = canvas.Project(Eye + Forward * ProbeAhead + Right * ProbeSide + Up * ProbeSide);
        float kx = (s.X - _cx) / ratio;
        float ky = (_cy - s.Y) / ratio;
        if (float.IsFinite(kx) && float.IsFinite(ky) && kx > 1f && ky > 1f && kx < _cx * 50f && ky < _cx * 50f)
        {
            _kx = kx;
            _ky = ky;
        }
        else
        {
            float fov = Math.Clamp(pc.GetFOVAngle(), 5f, 170f);
            _kx = _ky = _cx / MathF.Tan(fov * 0.5f * MathF.PI / 180f);
            if (!_reported)
            {
                _reported = true;
                Log.Info("WorldView: Canvas.Project gave " + s + ", projecting from the FOV (" + fov + ") instead.");
            }
        }

        Ready = true;
    }

    /// <summary>The camera, outside drawing (commands and ticks). Returns false while there is no player.</summary>
    public static bool ReadCamera() => World.Pc is { } pc && ReadCamera(pc);

    private static bool ReadCamera(PlayerController pc)
    {
        pc.GetPlayerViewPoint(out Vector3 eye, out Rotator rot);
        Eye = eye;
        Rotation = rot;

        // UE3's FRotationMatrix axes: X forward, Y right, Z up.
        float d = MathF.PI / 180f;
        float sp = MathF.Sin(rot.Pitch * d), cp = MathF.Cos(rot.Pitch * d);
        float sy = MathF.Sin(rot.Yaw * d), cy = MathF.Cos(rot.Yaw * d);
        float sr = MathF.Sin(rot.Roll * d), cr = MathF.Cos(rot.Roll * d);
        Forward = new Vector3(cp * cy, cp * sy, sp);
        Right = new Vector3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        Up = new Vector3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
        return float.IsFinite(Forward.X);
    }

    /// <summary>Projects a world point. False when it's behind the camera; the point may still be off screen.</summary>
    public static bool ToScreen(Vector3 world, out Vector2 screen)
    {
        Vector3 v = world - Eye;
        float z = Vector3.Dot(v, Forward);
        if (!Ready || z < NearPlane)
        {
            screen = default;
            return false;
        }

        screen = new Vector2(_cx + _kx * Vector3.Dot(v, Right) / z, _cy - _ky * Vector3.Dot(v, Up) / z);
        return true;
    }

    public static bool OnScreen(Vector2 p, float margin = 0f) =>
        p.X >= -margin && p.Y >= -margin && p.X <= Width + margin && p.Y <= Height + margin;

    /// <summary>The eight corners of a world box, projected. False if any corner is behind the camera.</summary>
    public static bool Corners(Vector3 min, Vector3 max, Span<Vector2> eight)
    {
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            if (!ToScreen(c, out eight[i]))
                return false;
        }

        return true;
    }

    /// <summary>The screen rectangle around a world box. False if it's behind the camera or entirely off screen.</summary>
    public static bool Bounds(Vector3 min, Vector3 max, out float x0, out float y0, out float x1, out float y1)
    {
        Span<Vector2> c = stackalloc Vector2[8];
        x0 = y0 = float.MaxValue;
        x1 = y1 = float.MinValue;
        if (!Corners(min, max, c))
            return false;

        foreach (Vector2 p in c)
        {
            x0 = MathF.Min(x0, p.X);
            y0 = MathF.Min(y0, p.Y);
            x1 = MathF.Max(x1, p.X);
            y1 = MathF.Max(y1, p.Y);
        }

        return x1 >= 0f && y1 >= 0f && x0 <= Width && y0 <= Height;
    }

    /// <summary>The angle in degrees between the view direction and a point, for picking what the crosshair is on.</summary>
    public static float AngleTo(Vector3 world)
    {
        Vector3 v = world - Eye;
        float len = v.Length();
        if (len < 1f)
            return 0f;

        float cos = Math.Clamp(Vector3.Dot(v / len, Forward), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }
}
