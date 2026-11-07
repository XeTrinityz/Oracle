using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using Pov = BmSDK.GameObject.FTPOV;

namespace CeZeY.Oracle;

/// <summary>
/// FREE CAMERA and TRIPOD. Both take over the finished picture in CameraRig's camera update, so the game's own
/// camera keeps running underneath: it still turns with the mouse or right stick, and each frame's turn is passed
/// on, which keeps the player's own sensitivity and invert settings. The game's debug and photo cameras swap the
/// player controller out, which would leave Oracle without a player, so they aren't used.
///
/// FREE CAMERA holds Batman still (his update and animation stop, as in EDITOR MODE) and mutes the game's keys the
/// way the menu does, so WASD flies the camera instead of walking him. TRIPOD leaves the camera where it is and
/// gives Batman back, optionally turning to keep him in shot.
/// </summary>
internal sealed class FreeCam : Module
{
    public static readonly FreeCam I = new();

    private enum Mode
    {
        Off,
        Fly,
        Tripod,
    }

    private const float Metre = 100f;
    private const float PitchLimit = 89f;
    private const int GamePitchLimit = 15000;
    private const float RollSpeed = 45f;
    private const float ZoomSpeed = 1.2f;
    private const float MinFov = 5f;
    private const float MaxFov = 120f;
    private const float BlendSeconds = 0.4f;
    private const double HintSeconds = 8.0;
    private const float GroundReach = 5000f;
    private const float ChestHeight = 40f;
    private const float Deg = 180f / MathF.PI;

    // Virtual-key codes, read straight from the keyboard.
    private const int VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkSpace = 0x20;

    /// <summary>Every key flying reads. Oracle hotkeys on these wait while flying.</summary>
    private static readonly int[] FlyKeys = { 'W', 'A', 'S', 'D', 'Q', 'E', 'Z', 'X', 'C', 'T', 'B', 'P', 'H', VkSpace, VkControl, VkShift, VkAlt };

    private static readonly (string, string)[] Hint =
    {
        ("WASD", "FLY"), ("SPACE / CTRL", "UP / DOWN"), ("SHIFT", "FAST"), ("ALT", "SLOW"), ("MOUSE", "LOOK"),
        ("Q / E", "TILT"), ("Z / X", "ZOOM"), ("C", "STRAIGHTEN"), ("T", "TRIPOD HERE"), ("B", "PUT BATMAN HERE"),
        ("P", "FREEZE WORLD"), ("H", "HIDE THESE KEYS"),
    };

    private Toggle _fly = null!, _tripod = null!, _track = null!, _freeze = null!, _hud = null!;
    private Slider _speed = null!, _smooth = null!, _look = null!;

    private Mode _mode;
    private Vector3 _pos, _vel;
    private float _yaw, _pitch, _roll, _fov, _startFov;
    private float _viewYaw, _viewPitch;
    private bool _haveLast;
    private float _lastYaw, _lastPitch;
    private readonly bool[] _was = new bool[256];
    private double _hintUntil;
    private bool _muted;

    // ---- Batman, held still while flying
    private nint _pawn;
    private bool _held, _wasTickOff, _wasAnimsPaused, _wasGrounded;

    // ---- the game camera's pitch limits, opened up while flying so the mouse can look straight up or down
    private nint _pinCam;
    private bool _pinned;
    private int _minPitch, _maxPitch;

    private bool _worldFrozen;
    private float _blend;
    private Pov _blendFrom;
    private string? _ended;

    public bool Active => _mode != Mode.Off;

    public bool HidesHud => _mode != Mode.Off && _hud.On;

    public string ModeLabel => _mode == Mode.Tripod ? "TRIPOD" : "FREE CAMERA";

    public override bool HasHud => _mode == Mode.Fly && Clock.Now < _hintUntil;

    public override void Build()
    {
        _fly = AddToggle("cam_free", "FREE CAMERA",
            "Detach the camera and fly it anywhere. WASD fly, SPACE and CTRL go up and down, SHIFT is faster and ALT slower, the mouse looks. Q and E tilt, Z and X zoom, C straightens and resets the zoom. T leaves the camera there as a TRIPOD, B puts Batman down under the camera, P freezes the world, H shows the keys. The city only loads around Batman, so far away looks rough.",
            OnFly).Unsaved();
        _speed = AddSlider("cam_free_speed", "FLY SPEED", "How fast FREE CAMERA flies. SHIFT is four times this, ALT a quarter.", 8f, 1f, 50f, 1f, 20f, v => Fmt.Count(v) + " M/S").Pref();
        _smooth = AddSlider("cam_free_smooth", "SMOOTH MOVES", "Eases the free camera into and out of its moves and turns, for smooth film-style shots. 0% starts and stops dead.",
            0.3f, 0f, 1f, 0.05f, 0.75f, Fmt.Percent).Pref();
        _look = AddSlider("cam_free_look", "LOOK SPEED", "How fast the free camera turns, on top of the game's own mouse and stick sensitivity.", 1f, 0.25f, 3f, 0.05f, 0.5f, Fmt.Times).Pref();
        _freeze = AddToggle("cam_free_freeze", "HOLD BATMAN STILL", "Freezes Batman mid-move while you fly, mid-glide and mid-fight included. Off, he keeps moving on his own but his controls stay with the camera.",
            on: true).Pref();
        _hud = AddToggle("cam_free_hud", "HIDE HUD WHILE ON", "Hides the game's HUD while FREE CAMERA or TRIPOD is on.", on: true).Pref();
        _tripod = AddToggle("cam_tripod", "TRIPOD",
            "Leaves the camera where it is and gives you Batman back, like a fixed film camera. Press T in FREE CAMERA to choose the spot, or turn this on to keep the current view.",
            OnTripod).Unsaved();
        _track = AddToggle("cam_tripod_track", "FOLLOW BATMAN", "The tripod turns to keep Batman in shot.", on: true).Pref();
    }

    /// <summary>An Oracle hotkey on a key flying uses waits, unless it's one that turns the camera off.</summary>
    public bool Claims(Keys key, IReadOnlyList<Feature> features) =>
        _mode == Mode.Fly && !Menu.IsOpen && Array.IndexOf(FlyKeys, (int)key & 0xFF) >= 0 && !features.Contains(_fly) && !features.Contains(_tripod);

    public override void WorldChanged() => Safe.Run("free camera", () => Stop(false));

    public override void LeftGame() => Safe.Run("free camera", () => Stop(false));

    public override void Shutdown() => Safe.Run("free camera", () => Stop(false));

    public override void Frame()
    {
        if (_ended == null)
            return;

        Overlay.Notice("FREE CAMERA", _ended, false);
        _ended = null;
    }

    // ---- turning on and off

    private void OnFly(bool on)
    {
        if (on)
            Try(_fly, StartFly, () => _fly.On = _mode == Mode.Fly);
        else if (_mode == Mode.Fly)
            Stop(true);
    }

    private void OnTripod(bool on)
    {
        if (on)
            Try(_tripod, StartTripod, () => _tripod.On = _mode == Mode.Tripod);
        else if (_mode == Mode.Tripod)
            Stop(true);
    }

    private static void Try(Feature row, Action change, Action revert)
    {
        try
        {
            change();
        }
        catch (Exception ex)
        {
            Safe.Run("free camera revert", revert);
            Log.Info(row.Label + " failed: " + ex.Message);
            Overlay.Notice(row.Label, ex.Message.ToUpperInvariant(), false);
        }
    }

    private static (PlayerController Pc, R3rdPersonCamera Cam) Require()
    {
        Transition.RequireIdle();
        if (WorldEditor.I.ModeOn)
            throw new InvalidOperationException("turn off EDITOR MODE first");

        PlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        R3rdPersonCamera cam = CameraRig.PlayerCam() ?? throw new InvalidOperationException("no camera yet");
        if (!CameraRig.IsPlayerView(cam, pc) || CameraRig.InCinematic(pc))
            throw new InvalidOperationException("not during a cutscene");

        return (pc, cam);
    }

    private void StartFly()
    {
        if (_mode == Mode.Fly)
            return;

        if (_mode == Mode.Off)
        {
            (_, R3rdPersonCamera cam) = Require();
            TakePose(cam);
        }
        else if (WorldEditor.I.ModeOn)
        {
            throw new InvalidOperationException("turn off EDITOR MODE first");
        }

        _mode = Mode.Fly;
        _fly.On = true;
        _tripod.On = false;
        _vel = Vector3.Zero;
        _haveLast = false;
        _blend = 0f;
        if (_freeze.On)
            Hold(World.Pawn);
        if (World.Pc is { } pc)
            MuteKeys(pc);

        // Keys already down (the hotkey that started this) count only once they're let go.
        foreach (int vk in FlyKeys)
            _was[vk] = true;
        _hintUntil = Clock.Now + HintSeconds;
    }

    private void StartTripod()
    {
        if (_mode == Mode.Tripod)
            return;

        if (_mode == Mode.Fly)
        {
            GiveBack();
        }
        else
        {
            (_, R3rdPersonCamera cam) = Require();
            TakePose(cam);
        }

        _mode = Mode.Tripod;
        _tripod.On = true;
        _fly.On = false;
        _blend = 0f;
    }

    /// <summary>Ends either mode. With <paramref name="blend"/> the view glides back to the game's camera.</summary>
    private void Stop(bool blend)
    {
        Mode was = _mode;
        _mode = Mode.Off;
        _fly.On = false;
        _tripod.On = false;
        GiveBack();
        _blend = 0f;
        if (blend && was != Mode.Off)
        {
            _blendFrom = Pose();
            _blend = BlendSeconds;
        }
    }

    /// <summary>Hands Batman, the keys, the camera's pitch limits and the world back to the game.</summary>
    private void GiveBack()
    {
        Release();
        Unmute();
        Unpin();
        Thaw();
    }

    private void End(string why)
    {
        Stop(true);
        _ended = why;
    }

    private void TakePose(R3rdPersonCamera cam)
    {
        Pov pov = cam.CameraCache.POV;
        Rotator r = pov.Rotation;
        _pos = pov.Location;
        _yaw = _viewYaw = r.Yaw;
        _pitch = _viewPitch = Math.Clamp(Geo.WrapDegrees(r.Pitch), -PitchLimit, PitchLimit);
        _roll = Geo.WrapDegrees(r.Roll);
        _fov = _startFov = pov.FOV > 1f ? pov.FOV : 70f;
    }

    private Pov Pose() => new() { Location = _pos, Rotation = new Rotator(_viewPitch, _viewYaw, _roll), FOV = _fov };

    // ---- Batman, keys, pitch limits, world

    private void Hold(Pawn? pawn)
    {
        // The Batmobile isn't frozen, only its controls are taken.
        if (pawn is not RPawnPlayer p)
            return;

        _pawn = p.Ptr;
        _held = true;
        _wasTickOff = p.bTickIsDisabled;
        _wasAnimsPaused = p.Mesh is { IsValid: true } mesh && mesh.bPauseAnims;
        _wasGrounded = p.Physics == Actor.EPhysics.PHYS_Walking;
        Still(p);
    }

    /// <summary>
    /// Stops Batman's own update and animation. AK ignores IgnoreMoveInput, and a pad's stick isn't a key, so
    /// muting keys alone would still let him walk.
    /// </summary>
    private static void Still(RPawnPlayer p)
    {
        if (!p.bTickIsDisabled)
            p.SetTickIsDisabled(true);
        if (p.Mesh is { IsValid: true } mesh && !mesh.bPauseAnims)
            mesh.bPauseAnims = true;
    }

    private void Release()
    {
        if (!_held)
            return;

        _held = false;
        Safe.Run("free camera release", () =>
        {
            if (World.Pawn is not RPawnPlayer p || p.Ptr != _pawn)
                return;

            if (p.bTickIsDisabled != _wasTickOff)
                p.SetTickIsDisabled(_wasTickOff);
            if (p.Mesh is { IsValid: true } mesh && mesh.bPauseAnims != _wasAnimsPaused)
                mesh.bPauseAnims = _wasAnimsPaused;

            // Frozen mid-move, he can wake up in a pose where WASD does nothing. Mid-air he is left alone, so a
            // glide or a fall carries on; landing gives him a fresh pose anyway.
            if (_wasGrounded)
                Teleport.Settle(p, moved: false);
        });
    }

    /// <summary>The menu and radial unmute every key when they close, so flying takes its keys back after them.</summary>
    private void MuteKeys(PlayerController pc)
    {
        _muted = true;
        if (!GameKeys.IsMuted && !Menu.IsOpen && !Radial.IsLive)
            GameKeys.Mute(editor: true);
    }

    private void Unmute()
    {
        if (!_muted)
            return;

        _muted = false;
        if (!Menu.IsOpen && !Radial.IsLive)
            Safe.Run("free camera keys", GameKeys.Unmute);
    }

    private void Pin(R3rdPersonCamera cam)
    {
        if (!_pinned || _pinCam != cam.Ptr)
        {
            _pinned = true;
            _pinCam = cam.Ptr;
            _minPitch = cam.MinPitch;
            _maxPitch = cam.maxPitch;
        }

        if (cam.MinPitch != -GamePitchLimit)
            cam.MinPitch = -GamePitchLimit;
        if (cam.maxPitch != GamePitchLimit)
            cam.maxPitch = GamePitchLimit;
    }

    private void Unpin()
    {
        if (!_pinned)
            return;

        _pinned = false;
        Safe.Run("free camera pitch", () =>
        {
            if (CameraRig.PlayerCam() is not { } cam || cam.Ptr != _pinCam)
                return;

            if (cam.MinPitch == -GamePitchLimit)
                cam.MinPitch = _minPitch;
            if (cam.maxPitch == GamePitchLimit)
                cam.maxPitch = _maxPitch;
        });
    }

    /// <summary>
    /// Freezes everything but the player, the way the game's debug camera does. Mods stop ticking while it's on
    /// (Oracle's menu and hotkeys keep going from the render hook), so flying runs in the camera update.
    /// </summary>
    private void ToggleWorld()
    {
        if (_worldFrozen)
        {
            Thaw();
        }
        else
        {
            WorldInfo wi = World.WorldInfo ?? throw new InvalidOperationException("the game is still loading");
            if (wi.bPlayersOnly)
                throw new InvalidOperationException("the game already has the world frozen");

            wi.bPlayersOnly = true;
            _worldFrozen = true;
        }

        Overlay.Toast("FREEZE WORLD", _worldFrozen ? "ON" : "OFF", _worldFrozen);
    }

    private void Thaw()
    {
        if (!_worldFrozen)
            return;

        _worldFrozen = false;
        Safe.Run("world unfreeze", () =>
        {
            if (World.WorldInfo is { bPlayersOnly: true } wi)
                wi.bPlayersOnly = false;
        });
    }

    /// <summary>Puts Batman on the ground under the camera, facing the way it looks, and ends flying.</summary>
    private void BringBatman()
    {
        if (World.Pawn is not RPawnPlayer p)
            throw new InvalidOperationException("get out of the Batmobile first");

        if (!Teleport.Ray(p, _pos, _pos - Vector3.UnitZ * GroundReach, out Vector3 floor, out _))
            throw new InvalidOperationException("no ground under the camera");

        Release();
        Teleport.Place(p, floor + Vector3.UnitZ * Teleport.Lift(p), new Rotator(0f, _yaw, 0f));
        p.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
        Teleport.Settle(p);
        Stop(false);
    }

    // ---- each frame, from the camera update

    /// <summary>Before the game's camera update: keep Batman still, the keys muted and the pitch limits open.</summary>
    public void BeforeCamera(R3rdPersonCamera cam, PlayerController pc)
    {
        if (_mode != Mode.Fly)
            return;

        try
        {
            Pin(cam);
            MuteKeys(pc);
            if (_held)
            {
                if (World.Pawn is RPawnPlayer p && p.Ptr == _pawn)
                    Still(p);
                else
                    End("ENDED: BATMAN CHANGED");
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// After the game's camera update: replaces the view. <paramref name="game"/> is the game's own view this
    /// frame (its turn drives the free camera's), <paramref name="pov"/> what the screen shows otherwise.
    /// </summary>
    public bool View(R3rdPersonCamera cam, PlayerController pc, in Pov game, ref Pov pov, float dt)
    {
        if (_mode == Mode.Off)
            return BlendBack(ref pov, dt);

        try
        {
            if (CameraRig.InCinematic(pc))
            {
                End("ENDED FOR A CUTSCENE");
                return BlendBack(ref pov, dt);
            }

            if (_mode == Mode.Fly)
            {
                // A special move or scripted camera took over (Batman is held, so it's the game's doing).
                if (!CameraRig.IsPlayerView(cam, pc))
                {
                    End("ENDED: THE GAME TOOK THE CAMERA");
                    return BlendBack(ref pov, dt);
                }

                FlyStep(game.Rotation, dt);
            }
            else
            {
                TripodStep(dt);
            }

            if (_mode == Mode.Off)
                return BlendBack(ref pov, dt);

            pov = Pose();
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    /// <summary>After a mode ends, glides the view from where the free camera was back to the game's.</summary>
    private bool BlendBack(ref Pov pov, float dt)
    {
        if (_blend <= 0f)
            return false;

        _blend -= dt;
        pov = Lerp(_blendFrom, pov, Ease.Smooth(1f - Math.Max(_blend, 0f) / BlendSeconds));
        return true;
    }

    private void Fail(Exception ex)
    {
        Log.Info("Free camera stopped: " + Log.Why(ex));
        Safe.Run("free camera stop", () => Stop(false));
        _ended = "STOPPED: " + ex.Message.ToUpperInvariant();
    }

    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True on the frame a key goes down.</summary>
    private bool Pressed(int vk)
    {
        bool down = Down(vk);
        bool was = _was[vk];
        _was[vk] = down;
        return down && !was;
    }

    private void FlyStep(Rotator game, float dt)
    {
        bool listening = !Menu.IsOpen && !Radial.IsLive && Native.GameInFront() && !World.Paused;

        // The game's camera turned this much from the mouse or stick; the free camera turns the same.
        if (_haveLast && listening)
        {
            _yaw += Geo.WrapDegrees(game.Yaw - _lastYaw) * _look.Value;
            _pitch = Math.Clamp(_pitch + Geo.WrapDegrees(game.Pitch - _lastPitch) * _look.Value, -PitchLimit, PitchLimit);
        }

        _lastYaw = game.Yaw;
        _lastPitch = game.Pitch;
        _haveLast = true;

        Vector3 move = Vector3.Zero;
        float boost = 1f;
        if (listening)
        {
            Vector3 forward = Forward(_yaw, _pitch);
            Vector3 right = Right(_yaw);
            if (Down('W')) move += forward;
            if (Down('S')) move -= forward;
            if (Down('D')) move += right;
            if (Down('A')) move -= right;
            if (Down(VkSpace)) move += Vector3.UnitZ;
            if (Down(VkControl)) move -= Vector3.UnitZ;
            boost = Down(VkShift) ? 4f : Down(VkAlt) ? 0.25f : 1f;

            if (Down('Q')) _roll = Geo.WrapDegrees(_roll - RollSpeed * dt);
            if (Down('E')) _roll = Geo.WrapDegrees(_roll + RollSpeed * dt);
            if (Down('Z')) _fov = Math.Clamp(_fov * MathF.Exp(-ZoomSpeed * dt), MinFov, MaxFov);
            if (Down('X')) _fov = Math.Clamp(_fov * MathF.Exp(ZoomSpeed * dt), MinFov, MaxFov);
        }
        else
        {
            // Nothing pressed while the menu had the keys counts once it closes.
            foreach (int vk in FlyKeys)
                _was[vk] = true;
        }

        Vector3 want = move.LengthSquared() > 0.0001f ? Vector3.Normalize(move) * (_speed.Value * Metre * boost) : Vector3.Zero;
        float s = _smooth.Value;
        float k = s <= 0.001f ? 1f : Ease.Follow(25f + (1.5f - 25f) * s, dt);
        _vel += (want - _vel) * k;
        _pos += _vel * dt;
        Turn(dt, 30f + (3f - 30f) * s);

        if (!listening)
            return;

        if (Pressed('C'))
        {
            _roll = 0f;
            _fov = _startFov;
        }

        if (Pressed('H'))
            _hintUntil = Clock.Now < _hintUntil ? 0.0 : double.MaxValue;
        if (Pressed('P'))
            Act("FREEZE WORLD", ToggleWorld);
        if (Pressed('T'))
            Act("TRIPOD", StartTripod);
        else if (Pressed('B'))
            Act("PUT BATMAN HERE", BringBatman);
    }

    private void TripodStep(float dt)
    {
        if (_track.On && World.Pawn is { IsValid: true } p)
        {
            Vector3 d = p.Location + Vector3.UnitZ * ChestHeight - _pos;
            float flat = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
            if (flat > 1f || MathF.Abs(d.Z) > 1f)
            {
                _yaw = _viewYaw + Geo.WrapDegrees(MathF.Atan2(d.Y, d.X) * Deg - _viewYaw);
                _pitch = Math.Clamp(MathF.Atan2(d.Z, flat) * Deg, -PitchLimit, PitchLimit);
            }
        }

        Turn(dt, 12f + (2f - 12f) * _smooth.Value);
    }

    /// <summary>The shown view follows where the camera is aimed; instantly when the rate is high.</summary>
    private void Turn(float dt, float rate)
    {
        float k = rate >= 29f ? 1f : Ease.Follow(rate, dt);
        _viewYaw += Geo.WrapDegrees(_yaw - _viewYaw) * k;
        _viewPitch += (_pitch - _viewPitch) * k;
    }

    private void Act(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Overlay.Notice(what, ex.Message.ToUpperInvariant(), false);
        }
    }

    // ---- maths

    private static Vector3 Forward(float yaw, float pitch)
    {
        float y = yaw / Deg, p = pitch / Deg;
        return new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p));
    }

    private static Vector3 Right(float yaw)
    {
        float y = yaw / Deg;
        return new Vector3(-MathF.Sin(y), MathF.Cos(y), 0f);
    }

    private static Pov Lerp(in Pov a, in Pov b, float t)
    {
        Rotator ra = a.Rotation, rb = b.Rotation;
        return new Pov
        {
            Location = Vector3.Lerp(a.Location, b.Location, t),
            Rotation = new Rotator(ra.Pitch + Geo.WrapDegrees(rb.Pitch - ra.Pitch) * t, ra.Yaw + Geo.WrapDegrees(rb.Yaw - ra.Yaw) * t, ra.Roll + Geo.WrapDegrees(rb.Roll - ra.Roll) * t),
            FOV = a.FOV + (b.FOV - a.FOV) * t,
        };
    }

    // ---- the keys hint

    /// <summary>The free camera's keys as chips, at the top of the left dock while it's flying.</summary>
    public override void Draw(Canvas c)
    {
        float w = Hud.U(Hud.CardW + 60f);
        float inner = w - Hud.U(Hud.Pad) * 2f;
        float h = Hud.Height(Hud.Keys(0f, 0f, inner, Hint, false));
        var (x, y, age) = Hud.Place("freecam", Hud.Side.Left, -1, w, h);
        float top = Hud.Card(x, y, w, h, "FREE CAMERA", _worldFrozen ? "WORLD FROZEN" : "", Theme.Signal, age);
        Hud.Keys(x + Hud.U(Hud.Pad), top, inner, Hint);
    }
}
