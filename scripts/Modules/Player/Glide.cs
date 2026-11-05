using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class GlideMod : Module
{
    public static readonly GlideMod I = new();

    private const string PitchKey = "GlideMaxPitch";
    private const float LevelSink = -45f;
    private const float DivePitch = -5f;
    private const float Floor = -1500f;
    private const float RotUnitsPerDegree = 65536f / 360f;

    private Slider _climb = null!;
    private nint _pc;
    private nint _pawn;
    private float _holdZ = float.NaN;
    private float _lastSpeed = -1f;

    public override void Build()
    {
        AddToggle("infinite_glide", "INFINITE GLIDE", "Glide as long as you hold glide. Look up to climb, look ahead to fly level, look down to dive. Batman keeps his glide pose and the speed GLIDE SPEED sets.",
            on => { if (!on) Restore(); }).EveryTick(GlideTick);
        _climb = AddSlider("glide_climb", "GLIDE CLIMB ANGLE", "How steeply Batman climbs when you look up during INFINITE GLIDE. Higher climbs faster.", 22f, 8f, 40f, 1f, 30f, Fmt.Degrees).Pref();
    }

    public override void Shutdown() => Restore();

    private void GlideTick()
    {
        RPlayerController? pc = World.Pc;
        if (pc == null)
            return;

        if (pc.Ptr != _pc)
        {
            _pc = pc.Ptr;
            ResetHold();
        }

        if (Shared.GetValue(PitchKey, int.MinValue) == int.MinValue)
            Shared.Set(PitchKey, pc.CapeGlideMaxPitch);

        int want = (int)MathF.Round(_climb.Value * RotUnitsPerDegree);
        if (pc.CapeGlideMaxPitch != want)
            pc.CapeGlideMaxPitch = want;

        if (pc.Pawn is not RPawnPlayer p || !p.IsValid || !p.bGliding || p.Physics != Actor.EPhysics.PHYS_Flying)
        {
            ResetHold();
            return;
        }

        if (p.Ptr != _pawn)
        {
            _pawn = p.Ptr;
            ResetHold();
        }

        float pitch = pc.GlideRotator.Pitch;
        float speed = pc.CapeGlideSpeed;
        if (pitch > DivePitch && _lastSpeed > 0f && speed > _lastSpeed + 0.01f)
        {
            speed = MathF.Max(_lastSpeed, pc.CapeGlideMinSpeed);
            pc.CapeGlideSpeed = speed;
        }

        _lastSpeed = speed;

        System.Numerics.Vector3 at = p.Location;
        float vz = p.Velocity.Z;
        bool level = vz < 0f && vz > LevelSink && pitch > DivePitch && at.Z > Floor;
        if (!level || float.IsNaN(_holdZ) || at.Z > _holdZ)
        {
            _holdZ = at.Z;
            return;
        }

        if (at.Z < _holdZ - 0.1f)
            p.SetLocation(new System.Numerics.Vector3(at.X, at.Y, _holdZ));
    }

    private void ResetHold()
    {
        _holdZ = float.NaN;
        _lastSpeed = -1f;
    }

    private void Restore()
    {
        ResetHold();
        int vanilla = Shared.GetValue(PitchKey, int.MinValue);
        Safe.Run("glide restore", () =>
        {
            if (vanilla != int.MinValue && World.Pc is { } pc && pc.CapeGlideMaxPitch != vanilla)
                pc.CapeGlideMaxPitch = vanilla;
        });
    }
}
