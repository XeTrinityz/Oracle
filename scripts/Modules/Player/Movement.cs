using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// PLAYER > MOVEMENT speeds and gravity. Each slider scales values the game's own movement code reads, from what
/// the game had, so 1.00x is always stock:
/// - Glide: the native glide update (CapeGlideMovement) eases CapeGlideSpeed toward a target between
///   CapeGlideMinSpeed and CapeGlideMaxSpeed at CapeGlideAccel / CapeGlideDecel, clamps it, and copies it into the
///   pawn's AirSpeed. Scaling the limits and both rates scales cruise, dive and how quickly each is reached.
/// - Grapple: native grapple movement clamps GrappleSpeed between MinGrappleSpeed and MaxGrappleSpeed.
/// - Grapple boost: GrappleMovement.GetGrappleBonusSpeed/Height read the gun's MaxGrappleBoostExtra* per upgrade tier.
/// - Line launcher: RLineLauncher.IncremementZipSpeed accelerates CurrentZipSpeed up to ZipForwardMaxSpeed.
/// - Fall gravity: RPawnPlayer's native GetGravityZ returns OverrideFallingGravity whenever it isn't 0.
/// - Tunnels: StealthMoveMode sets DesiredMovementScale to TunnelSpeedScale while Batman is forced to crouch.
/// </summary>
internal sealed class Movement : Module
{
    public static readonly Movement I = new();

    private readonly Scaled<RPlayerController> _glide = new(
        Field<RPlayerController>("glide min speed", c => c.CapeGlideMinSpeed, (c, v) => c.CapeGlideMinSpeed = v),
        Field<RPlayerController>("glide max speed", c => c.CapeGlideMaxSpeed, (c, v) => c.CapeGlideMaxSpeed = v),
        Field<RPlayerController>("glide city min speed", c => c.CapeGlideOverworldMinSpeed, (c, v) => c.CapeGlideOverworldMinSpeed = v),
        Field<RPlayerController>("glide soaring speed", c => c.CapeSoaringMinSpeed, (c, v) => c.CapeSoaringMinSpeed = v),
        Field<RPlayerController>("glide accel", c => c.CapeGlideAccel, (c, v) => c.CapeGlideAccel = v),
        Field<RPlayerController>("glide decel", c => c.CapeGlideDecel, (c, v) => c.CapeGlideDecel = v));

    private readonly Scaled<RPawnPlayer> _grapple = new(
        Field<RPawnPlayer>("grapple min speed", p => p.MinGrappleSpeed, (p, v) => p.MinGrappleSpeed = v),
        Field<RPawnPlayer>("grapple max speed", p => p.MaxGrappleSpeed, (p, v) => p.MaxGrappleSpeed = v));

    private readonly Scaled<RGrappleGun> _boost = new(
        Field<RGrappleGun>("boost speed", g => g.MaxGrappleBoostExtraSpeed, (g, v) => g.MaxGrappleBoostExtraSpeed = v),
        Field<RGrappleGun>("boost speed mk3", g => g.MaxGrappleBoostExtraSpeedMk3, (g, v) => g.MaxGrappleBoostExtraSpeedMk3 = v),
        Field<RGrappleGun>("boost speed mk4", g => g.MaxGrappleBoostExtraSpeedMk4, (g, v) => g.MaxGrappleBoostExtraSpeedMk4 = v),
        Field<RGrappleGun>("boost height", g => g.MaxGrappleBoostExtraHeight, (g, v) => g.MaxGrappleBoostExtraHeight = v),
        Field<RGrappleGun>("boost height mk3", g => g.MaxGrappleBoostExtraHeightMk3, (g, v) => g.MaxGrappleBoostExtraHeightMk3 = v),
        Field<RGrappleGun>("boost height mk4", g => g.MaxGrappleBoostExtraHeightMk4, (g, v) => g.MaxGrappleBoostExtraHeightMk4 = v));

    private readonly Scaled<RLineLauncher> _zip = new(
        Field<RLineLauncher>("line launcher max speed", l => l.ZipForwardMaxSpeed, (l, v) => l.ZipForwardMaxSpeed = v),
        Field<RLineLauncher>("line launcher accel", l => l.ZipForwardAccel, (l, v) => l.ZipForwardAccel = v));

    private readonly Scaled<RPawnPlayer> _tunnel = new(
        Field<RPawnPlayer>("tunnel speed", p => p.TunnelSpeedScale, (p, v) => p.TunnelSpeedScale = v));

    private Slider _glideSpeed = null!;
    private Slider _grappleSpeed = null!;
    private Slider _boostPower = null!;
    private Slider _zipSpeed = null!;
    private Slider _gravity = null!;
    private Slider _tunnelSpeed = null!;

    // FALL GRAVITY writes the pawn's gravity override, which the game also uses (Catwoman's whip rappel), so it
    // only writes while the override is free or still holds Oracle's last value.
    private RPawnPlayer? _gravityPawn;
    private float _gravityWritten;

    public override void Build()
    {
        _glideSpeed = AddSlider("glide_speed", "GLIDE SPEED", "How fast you glide: cruising, diving and dive boosts all scale, and so does how quickly you reach them. 1.00x is the game's own speed.",
            1f, 0.5f, 4f, 0.25f, 2f, Fmt.Times, _ => ApplyGlide());
        _glideSpeed.Maintain(ApplyGlide);

        _grappleSpeed = AddSlider("grapple_speed", "GRAPPLE SPEED", "How fast the grapple gun pulls you up to a ledge.",
            1f, 0.5f, 3f, 0.25f, 2f, Fmt.Times, _ => ApplyGrapple());
        _grappleSpeed.Maintain(ApplyGrapple);

        _boostPower = AddSlider("grapple_boost", "GRAPPLE BOOST POWER", "How far and how high a grapple boost throws you off the ledge, at every upgrade level.",
            1f, 0.5f, 3f, 0.25f, 2f, Fmt.Times, _ => ApplyBoost());
        _boostPower.Maintain(ApplyBoost);

        _zipSpeed = AddSlider("line_launcher_speed", "LINE LAUNCHER SPEED", "How fast the line launcher carries you along its line, and how quickly it gets there.",
            1f, 0.5f, 3f, 0.25f, 2f, Fmt.Times, _ => ApplyZip());
        _zipSpeed.Maintain(ApplyZip);

        _gravity = AddSlider("fall_gravity", "FALL GRAVITY", "How strongly you fall after a jump or a drop. Lower falls slower and floatier. Gliding is unchanged.",
            1f, 0.1f, 2f, 0.05f, 0.4f, Fmt.Percent, _ => ApplyGravity());
        _gravity.Maintain(ApplyGravity);

        _tunnelSpeed = AddSlider("tunnel_speed", "LOW TUNNEL SPEED", "How fast you move through low passages where you are forced to crouch.",
            1f, 0.5f, 3f, 0.25f, 2f, Fmt.Times, _ => ApplyTunnel());
        _tunnelSpeed.Maintain(ApplyTunnel);
    }

    public override void Shutdown()
    {
        _glide.Restore();
        _grapple.Restore();
        _boost.Restore();
        _zip.Restore();
        _tunnel.Restore();
        ClearGravity();
    }

    private void ApplyGlide() => _glide.Apply(World.Pc, _glideSpeed.Value);

    private void ApplyGrapple() => _grapple.Apply(World.Player, _grappleSpeed.Value);

    private void ApplyBoost() => _boost.Apply(World.Player?.GrappleGun, _boostPower.Value);

    private void ApplyZip() => _zip.Apply(World.Player?.LineLauncher, _zipSpeed.Value);

    private void ApplyTunnel() => _tunnel.Apply(World.Player, _tunnelSpeed.Value);

    private void ApplyGravity()
    {
        float scale = _gravity.Value;
        if (Scaled<RPawnPlayer>.IsStock(scale))
        {
            ClearGravity();
            return;
        }

        RPawnPlayer? p = World.Player;
        if (p == null)
            return;

        if (_gravityPawn is { IsValid: true } old && old.Ptr != p.Ptr)
            ClearGravity();

        float now = p.OverrideFallingGravity;
        if (now != 0f && now != _gravityWritten)
            return;

        float world = WorldGravity(p);
        if (world >= 0f)
            return;

        float want = world * scale;
        if (now != want)
            p.OverrideFallingGravity = want;
        _gravityPawn = p;
        _gravityWritten = want;
    }

    private void ClearGravity()
    {
        RPawnPlayer? p = _gravityPawn;
        float written = _gravityWritten;
        _gravityPawn = null;
        _gravityWritten = 0f;
        if (p is { IsValid: true } && written != 0f)
            Safe.Run("fall gravity restore", () => { if (p.OverrideFallingGravity == written) p.OverrideFallingGravity = 0f; });
    }

    /// <summary>The gravity the pawn would fall with untouched: its physics volume's, else the world's.</summary>
    private static float WorldGravity(Pawn p)
    {
        if (p.PhysicsVolume is { IsValid: true } volume)
        {
            float g = volume.GetGravityZ();
            if (g < 0f)
                return g;
        }

        WorldInfo? wi = World.WorldInfo;
        if (wi == null)
            return 0f;
        return wi.WorldGravityZ < 0f ? wi.WorldGravityZ : wi.DefaultGravityZ;
    }

    private static Held<T, float> Field<T>(string what, Func<T, float> get, Action<T, float> set) where T : GameObject => new(what, get, set);

    /// <summary>Several game values on one object, all scaled by one slider and all restored together.</summary>
    private sealed class Scaled<T> where T : GameObject
    {
        private readonly Held<T, float>[] _fields;

        public Scaled(params Held<T, float>[] fields) => _fields = fields;

        public static bool IsStock(float scale) => MathF.Abs(scale - 1f) < 0.001f;

        /// <summary>Scales every value on <paramref name="obj"/>, or puts them back at 1.00x or with no object.</summary>
        public void Apply(T? obj, float scale)
        {
            if (obj is not { IsValid: true } || IsStock(scale))
            {
                Restore();
                return;
            }

            Func<float, float> scaled = v => v * scale;
            foreach (Held<T, float> f in _fields)
                f.Set(obj, scaled);
        }

        public void Restore()
        {
            foreach (Held<T, float> f in _fields)
                f.Restore();
        }
    }
}
