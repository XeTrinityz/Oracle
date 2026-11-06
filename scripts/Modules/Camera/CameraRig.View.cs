using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using Pov = BmSDK.GameObject.FTPOV;

namespace CeZeY.Oracle;

/// <summary>
/// The finished picture. A redirect on the camera's UpdateCamera lets the game work out its view as usual, then
/// changes the result in the camera cache, which is what the screen, the sound and GetPlayerViewPoint read.
/// Before the next update the game's own view goes back into the cache, so nothing the game works out from last
/// frame's view sees Oracle's change, and switching anything off is instant.
///
/// Field of view is done here, not in the camera's per-state FOV values as before. Several states save the live
/// FOV back into their own value when they end (Walk, Look, and every aim and zoom state, which all run Look's
/// EndState), so rewriting those values compounded: each aim took an already changed FOV as vanilla and changed
/// it again. Offsets ease in and out, so a state change or a cutscene camera never pops.
/// </summary>
internal sealed partial class CameraRig
{
    private const float MinFov = 5f;
    private const float MaxFov = 150f;
    private const float EaseRate = 4f;
    private const int CatFoot = 0;
    private const int CatGlide = 1;
    private const int CatCar = 2;
    private const int CatAim = 3;

    private static readonly HashSet<string> AimStates = new(StringComparer.OrdinalIgnoreCase) { "Batarang", "LineLauncherAim", "HarpoonAim", "MagneticBlastAim", "RHDAim" };

    private static readonly (string Key, string Label, float Ratio)[] Bars =
    {
        ("off", "OFF", 0f), ("185", "1.85 : 1", 1.85f), ("200", "2 : 1", 2f), ("239", "2.39 : 1", 2.39f), ("276", "2.76 : 1", 2.76f),
    };

    private Slider _fov = null!, _aim = null!, _glideFov = null!, _carFov = null!, _tilt = null!, _fpForward = null!;
    private Toggle _collision = null!, _firstPerson = null!, _hideHud = null!, _readout = null!;
    private Choice _bars = null!;

    // ---- the picture
    private FrameTimer _frame;
    private float _add, _mult = 1f, _roll;
    private bool _povWritten;
    private nint _povCam;
    private Pov _povGame, _povMine;
    private float _gameFov, _shownFov;
    private Vector3 _shownAt;
    private int _shownCat;
    private int _stateIndex = -1;
    private int _stateCat;
    private string _stateName = "";

    // ---- CAMERA THROUGH WALLS: what the game wants, to put back
    private bool _collisionHeld, _collisionGame;
    private nint _collisionCam;

    // ---- first person hides the cape, and puts back what it was
    private bool _capeHeld, _capeWas;
    private nint _capePawn;

    // ---- HIDE GAME HUD
    private bool _hudHidden;
    private nint _hudPtr;
    private Every _hudEvery;

    public override bool HasHud => _readout.On || _bars.Index > 0;

    /// <summary>Bars stay up under the open menu, so their look can be picked with the menu open.</summary>
    public override bool HudWithMenu => _bars.Index > 0;

    private void BuildView()
    {
        _fov = AddSlider("fov_world", "FIELD OF VIEW", "Widens or narrows the view everywhere you play: on foot, in fights and predator, gliding and in the Batmobile. It's added to the game's own view, so its zooms and speed effects still work. Cutscenes keep their framing.",
            0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _aim = AddSlider("fov_aim", "AIM & ZOOM FOV", "Field of view while aiming a gadget (batarang, line launcher, remote claw, hacking device, disruptor) and while zooming. Lower zooms in further, higher is wider.",
            1f, 0.5f, 1.6f, 0.05f, 1.3f, Fmt.Percent);
        _glideFov = AddSlider("glide_fov", "GLIDE FOV", "Extra field of view while gliding and dive-bombing, on top of FIELD OF VIEW.", 0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _carFov = AddSlider("car_fov", "BATMOBILE FOV", "Extra field of view while driving and in battle mode, on top of FIELD OF VIEW.", 0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _tilt = AddSlider("cam_tilt", "CAMERA TILT", "Rolls the view sideways for a slanted, film-style angle. Only the picture tilts; steering and aiming stay the same.",
            0f, -30f, 30f, 1f, 10f, v => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + Fmt.Degrees(v));
        _collision = AddToggle("cam_no_collision", "CAMERA THROUGH WALLS", "Stops walls and scenery pushing the camera in towards you, so it keeps its distance and can look through walls.");
        _firstPerson = AddToggle("cam_first_person", "FIRST PERSON", "Puts the camera in your character's eyes when on foot (EXPERIMENTAL). The cape is hidden while it's on. Steering and gadgets work as normal; the Batmobile and cutscenes keep their own camera.");
        _fpForward = AddSlider("cam_fp_forward", "FIRST PERSON EYE", "Moves the first person eye forwards out of the cowl, or back. Raise it if you see the inside of the head.",
            12f, -10f, 40f, 2f, 24f, Fmt.Count).Pref();
        _hideHud = AddToggle("cam_hide_hud", "HIDE GAME HUD", "Hides the game's HUD: health, minimap, prompts, markers and objectives. Oracle's menu and notices still show.");
        _bars = AddChoice("cam_bars", "CINEMATIC BARS", "Black bars across the top and bottom for a widescreen film look. Pick the shape of the picture.",
            Bars.Select(b => b.Key).ToArray(), Bars.Select(b => b.Label).ToArray());
        _readout = AddToggle("fov_readout", "FOV READOUT", "Shows the live field of view (on screen and the game's own), the camera mode and state, and the camera distance in the bottom left corner.");
    }

    public override void Frame() => Safe.Run("hide hud", HudTick);

    private void LeftGameView()
    {
        _povWritten = false;
        _collisionHeld = false;
        _capeHeld = false;
        _hudHidden = false;
        _stateIndex = -1;
    }

    private void ShutdownView()
    {
        Safe.Run("camera view restore", () =>
        {
            R3rdPersonCamera? cam = PlayerCam();
            if (cam != null && _povWritten && _povCam == cam.Ptr)
            {
                ref Camera.FTCameraCache cache = ref cam.CameraCache;
                if (SamePov(cache.POV, _povMine))
                    cache.POV = _povGame;
            }

            _povWritten = false;
            if (cam != null && _collisionHeld && _collisionCam == cam.Ptr && cam.bDisableCameraCollision != _collisionGame)
                cam.bDisableCameraCollision = _collisionGame;
            _collisionHeld = false;
        });
        Safe.Run("cape restore", ReleaseCape);
        Safe.Run("hud restore", () =>
        {
            if (_hudHidden && World.Pc?.ModularHud is { IsValid: true } hud && hud.Ptr == _hudPtr)
                hud.ManuallySetHudVisibility(true);
            _hudHidden = false;
        });
    }

    // ---- the camera update

    [Redirect(typeof(R3rdPersonCamera), nameof(R3rdPersonCamera.UpdateCamera))]
    private static void UpdateCameraRedirect(R3rdPersonCamera self, float deltaTime)
    {
        CameraRig rig = I;
        PlayerController? pc = null;
        bool ours = false;
        try
        {
            ours = rig.BeforeUpdate(self, out pc);
        }
        catch (Exception ex)
        {
            Log.Error("camera before", "Camera update (before)", ex);
        }

        self.UpdateCamera(deltaTime);

        if (!ours || pc == null)
            return;

        try
        {
            rig.AfterUpdate(self, pc);
        }
        catch (Exception ex)
        {
            Log.Error("camera after", "Camera update (after)", ex);
        }
    }

    private bool BeforeUpdate(R3rdPersonCamera self, out PlayerController? pc)
    {
        pc = null;
        if (_readout == null)
            return false;

        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null || cam.Ptr != self.Ptr || World.Pc is not { } player)
        {
            _povWritten = false;
            return false;
        }

        pc = player;
        if (_povWritten && _povCam == cam.Ptr)
        {
            ref Camera.FTCameraCache cache = ref cam.CameraCache;
            if (SamePov(cache.POV, _povMine))
                cache.POV = _povGame;
        }

        _povWritten = false;
        if (_noShake.On)
            ClearShakes(cam);
        if (_steady.On && cam.bSwayWithPelvis)
            cam.bSwayWithPelvis = false;
        PinCollision(cam);
        FreeCam.I.BeforeCamera(cam, player);
        return true;
    }

    private void AfterUpdate(R3rdPersonCamera cam, PlayerController pc)
    {
        float dt = _frame.Step(0.1f);
        ref Camera.FTCameraCache cache = ref cam.CameraCache;
        Pov game = cache.POV;
        Pov pov = game;

        bool changed = Lens(cam, pc, ref pov, dt, FreeCam.I.Active);
        changed |= FreeCam.I.View(cam, pc, game, ref pov, dt);

        _gameFov = game.FOV;
        _shownFov = pov.FOV;
        _shownAt = pov.Location;
        if (!changed)
            return;

        cache.POV = pov;
        _povGame = game;
        _povMine = pov;
        _povCam = cam.Ptr;
        _povWritten = true;
    }

    private static bool SamePov(in Pov a, in Pov b)
    {
        Rotator ra = a.Rotation, rb = b.Rotation;
        return Vector3.DistanceSquared(a.Location, b.Location) < 0.01f && MathF.Abs(a.FOV - b.FOV) < 0.01f
            && MathF.Abs(ra.Pitch - rb.Pitch) < 0.01f && MathF.Abs(ra.Yaw - rb.Yaw) < 0.01f && MathF.Abs(ra.Roll - rb.Roll) < 0.01f;
    }

    /// <summary>True while the camera shows the player's own view, not a cutscene or a special move's camera.</summary>
    internal static bool IsPlayerView(Camera cam, PlayerController pc)
    {
        if (!Mine(cam.ViewTarget.Target, pc))
            return false;

        Actor? pending = cam.PendingViewTarget.Target;
        return pending is not { IsValid: true } || Mine(pending, pc);
    }

    private static bool Mine(Actor? target, PlayerController pc) => target is { IsValid: true } && (target.Ptr == pc.Ptr || target is Pawn);

    internal static bool InCinematic(PlayerController pc) =>
        pc.bCinematicMode || pc is RPlayerControllerBase { ActiveCinematicMode: { IsValid: true } };

    // ---- field of view, tilt and first person

    private static int CategoryOf(string state)
    {
        if (state.Contains("Glid", StringComparison.OrdinalIgnoreCase) || state.Contains("Dive", StringComparison.OrdinalIgnoreCase))
            return CatGlide;
        if (AimStates.Contains(state) || state.Contains("Aim", StringComparison.OrdinalIgnoreCase) || state.Contains("Zoom", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Sniper", StringComparison.OrdinalIgnoreCase))
            return CatAim;
        if (state.Contains("Driv", StringComparison.OrdinalIgnoreCase) || state.Contains("Batmobile", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Battle", StringComparison.OrdinalIgnoreCase) || state.Contains("Boosting", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Chasing", StringComparison.OrdinalIgnoreCase) || state.Contains("Vehicle", StringComparison.OrdinalIgnoreCase))
            return CatCar;
        return CatFoot;
    }

    private int Category(R3rdPersonCamera cam)
    {
        FName state = cam.GetStateName();
        if (state.Index != _stateIndex)
        {
            _stateIndex = state.Index;
            _stateName = state.ToString() ?? "";
            _stateCat = CategoryOf(_stateName);
        }

        // The Z zoom can run inside a walking state.
        return _stateCat == CatFoot && cam.bZoomed ? CatAim : _stateCat;
    }

    private bool Lens(R3rdPersonCamera cam, PlayerController pc, ref Pov pov, float dt, bool free)
    {
        int cat = Category(cam);
        _shownCat = cat;
        bool player = IsPlayerView(cam, pc) && !InCinematic(pc);

        float add = 0f, mult = 1f, roll = 0f;
        if (player)
        {
            switch (cat)
            {
                case CatAim:
                    mult = _aim.Value;
                    break;
                case CatGlide:
                    add = _fov.Value + _glideFov.Value;
                    break;
                case CatCar:
                    add = _fov.Value + _carFov.Value;
                    break;
                default:
                    add = _fov.Value;
                    break;
            }

            roll = _tilt.Value;
        }

        float k = Ease.Follow(EaseRate, dt);
        _add = Settle(_add, add, k, 0.01f);
        _mult = Settle(_mult, mult, k, 0.001f);
        _roll = Settle(_roll, roll, k, 0.01f);

        bool eyes = !free && player && _firstPerson.On && cat != CatCar && FirstPerson(ref pov);
        if (!eyes)
            ReleaseCape();

        bool fov = MathF.Abs(_add) > 0.01f || MathF.Abs(_mult - 1f) > 0.001f;
        bool tilt = MathF.Abs(_roll) > 0.01f;
        if (fov && pov.FOV > 0f)
            pov.FOV = Math.Clamp(pov.FOV * _mult + _add, MinFov, MaxFov);
        if (tilt)
        {
            Rotator r = pov.Rotation;
            pov.Rotation = new Rotator(r.Pitch, r.Yaw, r.Roll + _roll);
        }

        return fov || tilt || eyes;
    }

    private static float Settle(float value, float target, float k, float snap)
    {
        value += (target - value) * k;
        return MathF.Abs(target - value) < snap ? target : value;
    }

    /// <summary>Moves the view into the character's eyes, a little forward so the cowl doesn't fill the screen.</summary>
    private bool FirstPerson(ref Pov pov)
    {
        if (World.Pawn is not RPawnPlayer p || !p.IsValid)
            return false;

        Vector3 eye = p.GetPawnViewLocation();
        if (!float.IsFinite(eye.X) || eye == Vector3.Zero)
            return false;

        float yaw = pov.Rotation.Yaw * (MathF.PI / 180f);
        pov.Location = eye + new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f) * _fpForward.Value;
        HoldCape(p);
        return true;
    }

    private void HoldCape(RPawnPlayer p)
    {
        if (p is not RPawnPlayerBmBase bm || bm.CapeComponent is not { IsValid: true } cape)
            return;

        if (!_capeHeld || _capePawn != p.Ptr)
        {
            ReleaseCape();
            _capeHeld = true;
            _capePawn = p.Ptr;
            _capeWas = cape.IsCapeHiddenGame;
        }

        if (!cape.IsCapeHiddenGame)
            cape.CapeSetHiddenGame(true);
    }

    private void ReleaseCape()
    {
        if (!_capeHeld)
            return;

        _capeHeld = false;
        if (World.Pawn is RPawnPlayerBmBase bm && bm.Ptr == _capePawn && bm.CapeComponent is { IsValid: true } cape && cape.IsCapeHiddenGame != _capeWas)
            cape.CapeSetHiddenGame(_capeWas);
    }

    // ---- effects run before each update

    private static void ClearShakes(R3rdPersonCamera cam)
    {
        RBMScreenShakeModifier shake = cam.ScreenShakeModifier;
        if (shake is { IsValid: true } && shake.Shakes.Count > 0)
            shake.Reset();
    }

    /// <summary>
    /// Holds camera collision off. A few first person states switch it themselves, so what the game last set is
    /// tracked and put back when the toggle goes off.
    /// </summary>
    private void PinCollision(R3rdPersonCamera cam)
    {
        if (_collisionHeld && _collisionCam != cam.Ptr)
            _collisionHeld = false;

        if (_collision.On)
        {
            bool now = cam.bDisableCameraCollision;
            if (!_collisionHeld)
            {
                _collisionHeld = true;
                _collisionCam = cam.Ptr;
                _collisionGame = now;
            }
            else if (!now)
            {
                _collisionGame = false;
            }

            if (!now)
                cam.bDisableCameraCollision = true;
        }
        else if (_collisionHeld)
        {
            _collisionHeld = false;
            if (cam.bDisableCameraCollision != _collisionGame)
                cam.bDisableCameraCollision = _collisionGame;
        }
    }

    // ---- HIDE GAME HUD

    /// <summary>
    /// Uses the switch photo mode uses to hide every HUD movie. The game turns it back on at times (menus,
    /// cutscenes), so it is asked again a few times a second while it should be hidden.
    /// </summary>
    private void HudTick()
    {
        bool want = _hideHud.On || FreeCam.I.HidesHud;
        if (!want && !_hudHidden)
            return;

        RGFxMovieModularHud? hud = World.Pc?.ModularHud is { IsValid: true } h ? h : null;
        if (want)
        {
            if (hud == null || (_hudHidden && hud.Ptr == _hudPtr && !_hudEvery.Elapsed(0.25)))
                return;

            hud.ManuallySetHudVisibility(false);
            _hudHidden = true;
            _hudPtr = hud.Ptr;
            return;
        }

        _hudHidden = false;
        if (hud != null && hud.Ptr == _hudPtr)
            hud.ManuallySetHudVisibility(true);
    }

    // ---- drawing

    public override void Draw(Canvas c)
    {
        DrawBars();
        if (_readout.On)
            DrawReadout();
    }

    private void DrawBars()
    {
        float ratio = Bars[Math.Clamp(_bars.Index, 0, Bars.Length - 1)].Ratio;
        if (ratio <= 0f)
            return;

        float bar = (Gfx.H - Gfx.W / ratio) * 0.5f;
        if (bar < 1f)
            return;

        Gfx.Rect(0f, 0f, Gfx.W, bar, Theme.Black);
        Gfx.Rect(0f, Gfx.H - bar, Gfx.W, bar, Theme.Black);
    }

    private void DrawReadout()
    {
        if (PlayerCam() == null)
            return;

        Pawn? pawn = World.Pawn;
        float dist = pawn != null ? Vector3.Distance(_shownAt, pawn.Location) : 0f;
        string mode = FreeCam.I.Active ? FreeCam.I.ModeLabel : _shownCat switch { CatGlide => "GLIDE", CatCar => "BATMOBILE", CatAim => "AIM", _ => "ON FOOT" };
        string line1 = "FOV " + _shownFov.ToString("0.0", CultureInfo.InvariantCulture) + "  ON SCREEN";
        string line2 = "GAME " + _gameFov.ToString("0.0", CultureInfo.InvariantCulture) + "  " + mode + "  " + _stateName.ToUpperInvariant();
        string line3 = "DISTANCE " + dist.ToString("0", CultureInfo.InvariantCulture);

        const float scale = 0.72f;
        float lh = Gfx.S(20);
        float w = Math.Max(Gfx.TextW(line2, scale), Gfx.TextW(line1, 0.85f)) + Gfx.S(32);
        float h = Gfx.S(22) + lh * 3f;
        float x = Gfx.S(24);
        float y = Gfx.H - h - Gfx.S(120);
        Gfx.Panel(x, y, w, h, 0.75f, 2f);
        float cy = y + Gfx.S(10) + lh * 0.5f;
        Gfx.TextMid(x + Gfx.S(14), cy, line1, Theme.Accent, 0.85f);
        Gfx.TextMid(x + Gfx.S(14), cy + lh, line2, Theme.Mid, scale);
        Gfx.TextMid(x + Gfx.S(14), cy + lh * 2f, line3, Theme.Mid, scale);
    }
}
