using System.Globalization;
using System.Numerics;
using System.Reflection;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using CamConfig = BmSDK.BmGame.R3rdPersonCamera.FFreeCameraConfig;

namespace CeZeY.Oracle;

/// <summary>
/// Rewrites the camera's many per-state values. The game also changes them, so each write records what it wrote:
/// a value that no longer matches was changed by the game and becomes the new vanilla baseline.
/// </summary>
internal sealed class CameraRig : Module
{
    public static readonly CameraRig I = new();

    private const double RecheckSeconds = 1.0;
    private const string MemoryKey = "CameraMemory";
    private const float MinFov = 5f;
    private const float MinAimFov = 3f;
    private const float MaxFov = 150f;
    private const int CatFoot = 0;
    private const int CatGlide = 1;
    private const int CatCar = 2;
    private const int CatAim = 3;

    private delegate ref CamConfig CfgRef(R3rdPersonCamera cam);
    private delegate ref Vector3 VecRef(R3rdPersonCamera cam);

    private readonly record struct Rig(float Fov, float Height, float Shoulder, float Pull, float Pivot, float Aim,
        float GlideFov, float GlideKick, float GlideDist, float CarFov, float CarKick, float CarDist, bool Steady, bool NoShake);

    private static readonly Rig Vanilla = new(0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f, false, false);

    private sealed record Preset(string Key, string Label, float Fov, float Height, float Shoulder, float Pull, float Pivot, float GlideFov, float GlideDist, float CarFov, float CarDist,
        float GlideKick = 1f, float CarKick = 1f);

    private static readonly Preset[] Presets =
    {
        new("pick", "CHOOSE", 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f),
        new("vanilla", "VANILLA", 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f),
        new("wide", "WIDE", 10f, 0f, 1f, 40f, 0f, 5f, 1.1f, 5f, 1.1f),
        new("ultra", "ULTRA WIDE", 20f, 0f, 1f, 30f, 0f, 10f, 1.1f, 10f, 1.1f),
        new("cinematic", "CINEMATIC", -5f, 15f, 0.8f, 120f, 10f, 0f, 1.3f, 0f, 1.25f),
        new("shoulder", "OVER SHOULDER", 0f, -5f, 1.3f, -40f, 0f, 0f, 0.9f, 0f, 1f),
        new("centred", "CENTRED", 5f, 10f, 0f, 60f, 0f, 0f, 1f, 0f, 1f),
        new("action", "ACTION", 15f, -10f, 1.15f, -30f, -5f, 10f, 0.9f, 10f, 0.95f, 1.75f, 1.75f),
        new("tactical", "TACTICAL", 10f, 80f, 0.5f, 220f, 50f, 5f, 1.6f, 5f, 1.5f, 0.5f, 0.75f),
        new("noir", "NOIR", -12f, -20f, 1.25f, 260f, -10f, -5f, 1.4f, -5f, 1.35f, 0.5f, 0.5f),
    };

    private static readonly string[] FootFovs =
    {
        "WalkStateCameraFOVAngle", "WalkCanisterStateCameraFOVAngle", "AlternativeWalkCamFOV", "RunStateCameraFOVAngle", "BoostRunFOVAngle",
        "StealthStateCameraFOVAngle", "OverworldStealthStateCameraFOVAngle", "TunnelStateCameraFOVAngle", "FallingStateCameraFOVAngle",
        "CorridorCombatStateCameraFOVAngle", "HighCombatStateCameraFOVAngle", "NightwingBossFightCameraFOVAngle", "LookStateCameraFOVAngle",
        "GrappleCameraFOVAngle", "CoverRightCameraFOVAngle", "AlcoveCameraFOVAngle", "SilentPredCameraFOVAngle", "SilentPredOverworldCameraFOVAngle",
        "PerchLedgeCameraFOVAngle", "PerchLedgeSwingCameraFOVAngle", "HangCameraFOVAngle", "ZipLineCameraFOVAngle", "HarpoonPullCameraFOVAngle",
        "SwingingFromAboveCameraFOVAngle", "CeilingClimbCamFOVAngle", "RaftPullFOVAngle", "FearTakedownFOVAngle", "LookDownVantageCamFOVAngle",
        "SlideStateCameraFOVAngle", "CornerStateFOVAngle", "OmnitronCornerStateFOVAngle", "RHDCornerStateFOVAngle", "VoiceSynthCornerStateFOVAngle",
        "WallClimbStartCameraFOVAngle", "PounceCameraFOVAngle", "UsingSpotlightStateCameraFOVAngle", "ForensicsScanStateCameraFOVAngle",
        "HelicopterCameraFOVAngle", "ScarecrowTakedownCameraFOVAngle", "PairedAnimationStateCameraFOVAngle", "DialogueStateCameraFOVAngle",
        "ThugCameraFOVAngle", "JokerFPSFov",
    };

    private static readonly string[] GlideFovs = { "GlidingCameraFOVAngle", "BoostGlidingCameraFOVAngle", "EdgeSwingCameraFOVAngle" };

    private static readonly string[] CarFovs =
    {
        "DrivingCamFOV", "DrivingCamFOV1", "DrivingCamFOV2", "BoostingCamFOV", "BoostingCamFOV1", "BoostingCamFOV2", "BattleModeCamFOV",
        "BattleModeWinchCamFOV", "AntiPersonelBattleModeCamFOV", "ChasingCamFOV", "RidingCarCamFOV", "DiveToBatmobilePickupFOVAngle",
    };

    private static readonly string[] AimFovs =
    {
        "BatarangAimCameraFOVAngle", "BatTrackerAimCameraFOVAngle", "FreezeJammerAimCameraFOVAngle", "GrappleAimCameraFOVAngle", "HarpoonAimCameraFOVAngle",
        "JokerGunAimCameraFOVAngle", "JokerGunZoomAimCameraFOVAngle", "JokerGunZoomRoom3AimCameraFOVAngle", "LineLauncherAimCameraFOVAngle",
        "MagneticBlastAimCameraFOVAngle", "OmnitronAimCameraFOVAngle", "RHDAimCameraFOVAngle", "DisruptorSniperFOVAngle", "DisruptorSniperZoomedFOVAngle",
        "ZoomStateCameraFOVAngle", "ThugZoomCameraFOVAngle", "ZoomFOV", "ZoomedFOVAngle", "ResonatorFOVAngle", "ForensicsFilterStateCameraFOVAngle",
        "CopCameraZoomFOV", "JokerFPSZoomFov", "GauntletCameraFOVAngle",
    };

    private static readonly HashSet<string> AimStates = new(StringComparer.OrdinalIgnoreCase) { "Batarang", "LineLauncherAim", "HarpoonAim", "MagneticBlastAim", "RHDAim" };
    private static readonly string[] GlideKicks = { "GlidingCameraExtraFOVAngle" };
    private static readonly string[] CarKicks = { "DrivingCamMaxExtraFOV", "DrivingCamMaxExtraFOV1", "DrivingCamMaxExtraFOV2" };
    private static readonly string[] SwayFloats = { "PelvisSway", "DefaultPelvisSway", "RunStatePelvisSway", "CurrentPelvisSway" };
    private static readonly string[] GlideOffsets = { "GlidingCameraOffset", "BoostGlidingCameraOffset" };

    private static readonly string[] FootCfgs =
    {
        "WalkCamConfig", "WalkCanisterCamConfig", "AgilityWalkCamConfig", "AlternativeWalkCamConfig", "RunCamConfig", "RooftopRunCamConfig",
        "StealthCamConfig", "CombatWalkCamConfig", "CorridorCombatCamConfig", "HighCombatCamConfig", "DemonTrialsCamConfig", "RailingCamConfig",
        "GrateCameraConfig", "LedgeSwingCamConfig", "GrappleCamConfig", "HqGrappleStartCamConfig", "PerchLedgeCamConfig", "SilentPredatorCamConfig",
        "VantageClimbCamConfig", "IntroCamConfig", "FloorAttackCamConfig", "ThugCamConfig",
    };

    private static readonly string[] CarCfgs =
    {
        "DrivingCamConfig", "DrivingCamConfig1", "DrivingCamConfig2", "BoostingCamConfig", "BoostingCamConfig1", "BoostingCamConfig2",
        "BattleModeCamConfig", "BattleModeWinchCamConfig",
    };

    /// <summary>What has been written, kept in BCL and BmSDK types so a hot reload can restore the true vanilla values.</summary>
    private sealed class Memory
    {
        private readonly object[] _slots;

        public Memory(object[] slots) => _slots = slots;

        public static object[] Make() => new object[]
        {
            new Dictionary<string, float>(StringComparer.Ordinal), new Dictionary<string, float>(StringComparer.Ordinal),
            new Dictionary<string, Vector3>(StringComparer.Ordinal), new Dictionary<string, Vector3>(StringComparer.Ordinal),
            new Dictionary<string, CamConfig>(StringComparer.Ordinal), new Dictionary<string, CamConfig>(StringComparer.Ordinal),
            new long[3], new float[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 1f, 0f, 0f },
        };

        public Dictionary<string, float> FloatOrig => (Dictionary<string, float>)_slots[0];
        public Dictionary<string, float> FloatWritten => (Dictionary<string, float>)_slots[1];
        public Dictionary<string, Vector3> VecOrig => (Dictionary<string, Vector3>)_slots[2];
        public Dictionary<string, Vector3> VecWritten => (Dictionary<string, Vector3>)_slots[3];
        public Dictionary<string, CamConfig> CfgOrig => (Dictionary<string, CamConfig>)_slots[4];
        public Dictionary<string, CamConfig> CfgWritten => (Dictionary<string, CamConfig>)_slots[5];
        private long[] Meta => (long[])_slots[6];
        private float[] Values => (float[])_slots[7];

        public nint Ptr
        {
            get => (nint)Meta[0];
            set => Meta[0] = value;
        }

        public bool Live
        {
            get => Meta[1] != 0;
            set => Meta[1] = value ? 1 : 0;
        }

        /// <summary>The camera's original sway flag: 0 unknown, 1 off, 2 on.</summary>
        public long Sway
        {
            get => Meta[2];
            set => Meta[2] = value;
        }

        public Rig Applied
        {
            get
            {
                float[] a = Values;
                return new Rig(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12] > 0.5f, a[13] > 0.5f);
            }
            set
            {
                float[] a = Values;
                (a[0], a[1], a[2], a[3], a[4], a[5], a[6]) = (value.Fov, value.Height, value.Shoulder, value.Pull, value.Pivot, value.Aim, value.GlideFov);
                (a[7], a[8], a[9], a[10], a[11]) = (value.GlideKick, value.GlideDist, value.CarFov, value.CarKick, value.CarDist);
                (a[12], a[13]) = (value.Steady ? 1f : 0f, value.NoShake ? 1f : 0f);
            }
        }

        public void Clear()
        {
            FloatOrig.Clear();
            FloatWritten.Clear();
            VecOrig.Clear();
            VecWritten.Clear();
            CfgOrig.Clear();
            CfgWritten.Clear();
            Ptr = 0;
            Live = false;
            Sway = 0;
            Applied = Vanilla;
        }
    }

    private readonly Dictionary<string, (Func<R3rdPersonCamera, float> Get, Action<R3rdPersonCamera, float> Set)?> _floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CfgRef?> _cfgs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VecRef?> _vecs = new(StringComparer.Ordinal);
    private Memory _mem = null!;
    private Every _recheck;
    private string _stateName = "";
    private int _stateCat;
    private string _aimState = "";
    private bool _aimOn;
    private float _aimBase;
    private float _aimWritten;
    private float _aimPrevFov;

    private Slider _fov = null!, _height = null!, _shoulder = null!, _pull = null!, _pivot = null!, _aim = null!;
    private Slider _glideFov = null!, _glideKick = null!, _glideDist = null!, _carFov = null!, _carKick = null!, _carDist = null!;
    private Toggle _steady = null!, _noShake = null!, _readout = null!;
    private Choice _preset = null!;

    public override bool HasHud => _readout.On;

    private Rig Wanted => new(_fov.Value, _height.Value, _shoulder.Value, _pull.Value, _pivot.Value, _aim.Value,
        _glideFov.Value, _glideKick.Value, _glideDist.Value, _carFov.Value, _carKick.Value, _carDist.Value, _steady.On, _noShake.On);

    public override void Build()
    {
        _fov = AddSlider("fov_world", "FIELD OF VIEW", "Widens or narrows the camera everywhere: walking, running, fights, predator, gliding and the Batmobile. Value is added to every camera's own FOV, so the game's zoom moments still work.",
            0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _height = AddSlider("cam_height", "CAMERA HEIGHT", "Raises or lowers where the camera sits behind your character when on foot.", 0f, -60f, 120f, 5f, 30f, Fmt.Signed);
        _shoulder = AddSlider("cam_shoulder", "SHOULDER OFFSET", "How far the camera sits to the side. CENTRED puts your character in the middle, negative values swap to the left shoulder.", 1f, -1.5f, 2f, 0.1f, 0f, Fmt.Side);
        _aim = AddSlider("fov_aim", "AIM & ZOOM FOV", "Field of view while aiming (batarang, line launcher, remote claw, hacking device, disruptor) and when zooming with Z. Lower = more zoom, higher = wider.", 1f, 0.5f, 1.6f, 0.05f, 1.3f, Fmt.Percent);

        _pull = AddSlider("cam_distance", "CAMERA DISTANCE", "Pulls the camera further back from your character on foot, or closer in with negative values.", 0f, -60f, 400f, 10f, 150f, Fmt.Signed);
        _pivot = AddSlider("cam_pivot", "PIVOT HEIGHT", "Moves the point the camera orbits around and looks at up or down. Higher looks over your character's head.", 0f, -50f, 100f, 5f, 30f, Fmt.Signed);

        _glideFov = AddSlider("glide_fov", "GLIDE FOV", "Extra field of view while gliding and dive-bombing, on top of FIELD OF VIEW.", 0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _glideKick = AddSlider("glide_kick", "DIVE SPEED KICK", "How much the view widens as you pick up speed while gliding. 0% keeps it steady.", 1f, 0f, 3f, 0.25f, 2f, Fmt.Percent);
        _glideDist = AddSlider("glide_distance", "GLIDE CAMERA DISTANCE", "How far behind you the camera trails while gliding.", 1f, 0.5f, 2.5f, 0.1f, 1.5f, Fmt.Times);

        _carFov = AddSlider("car_fov", "BATMOBILE FOV", "Extra field of view while driving and in battle mode, on top of FIELD OF VIEW.", 0f, -20f, 40f, 1f, 15f, Fmt.Signed);
        _carKick = AddSlider("car_kick", "SPEED FOV KICK", "How much the view widens at high speed and when boosting.", 1f, 0f, 3f, 0.25f, 2f, Fmt.Percent);
        _carDist = AddSlider("car_distance", "BATMOBILE CAMERA DISTANCE", "How far behind the Batmobile the camera sits, in car and battle mode.", 1f, 0.5f, 2.5f, 0.1f, 1.4f, Fmt.Times);

        _preset = AddChoice("fov_preset", "CAMERA PRESET", "Sets every camera slider at once. Tweak them afterwards as you like. VANILLA puts everything back to the game's camera.",
            Presets.Select(p => p.Key).ToArray(), Presets.Select(p => p.Label).ToArray(), ApplyPreset).Pref();
        _readout = AddToggle("fov_readout", "FOV READOUT", "Shows the live field of view (on screen and the game's own value), the camera mode and the camera distance in the bottom left corner.").Pref();
        _steady = AddToggle("cam_steady", "STEADY CAMERA", "Stops the camera swaying with your character's hips when walking and running.");
        _noShake = AddToggle("cam_no_shake", "NO SCREEN SHAKE", "Turns off camera shakes from explosions, landings, gliding and the Batmobile.");
        AddCommand("cam_reset", "RESET CAMERA", "Puts every camera and FOV setting on this page back to the game's default.", ResetAll);
    }

    public override void Started() => _mem = new Memory(Shared.Keep(MemoryKey, Memory.Make));

    public override void LeftGame()
    {
        if (_mem.Ptr != 0)
            Forget();
    }

    public override void Shutdown()
    {
        Safe.Run("camera restore", () =>
        {
            R3rdPersonCamera? cam = PlayerCam();
            if (cam != null && cam.Ptr == _mem.Ptr && (_mem.Live || _mem.Applied != Vanilla))
                Apply(cam, Vanilla);
        });
    }

    private void ApplyPreset(int i)
    {
        if (i <= 0 || i >= Presets.Length)
            return;

        Preset p = Presets[i];
        Set(_fov, p.Fov);
        Set(_height, p.Height);
        Set(_shoulder, p.Shoulder);
        Set(_pull, p.Pull);
        Set(_pivot, p.Pivot);
        Set(_glideFov, p.GlideFov);
        Set(_glideDist, p.GlideDist);
        Set(_carFov, p.CarFov);
        Set(_carDist, p.CarDist);
        Set(_glideKick, p.GlideKick);
        Set(_carKick, p.CarKick);
        if (p.Key == "vanilla")
            Set(_aim, 1f);
    }

    private static void Set(Slider f, float v)
    {
        v = Math.Clamp(v, f.Min, f.Max);
        if (MathF.Abs(f.Value - v) < 0.0001f)
            return;

        f.Value = v;
        Settings.MarkDirty();
    }

    private void ResetAll()
    {
        foreach (Slider f in new[] { _fov, _height, _shoulder, _pull, _pivot, _aim, _glideFov, _glideKick, _glideDist, _carFov, _carKick, _carDist })
            Set(f, f.Default);

        _steady.On = false;
        _noShake.On = false;
        _preset.ResetValue();
        Settings.MarkDirty();
    }

    private static R3rdPersonCamera? PlayerCam() =>
        World.Pc?.PlayerCamera is R3rdPersonCamera { IsValid: true, IsClassDefaultObject: false } cam ? cam : null;

    public override void Tick(float dt)
    {
        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null)
            return;

        if (cam.Ptr != _mem.Ptr)
        {
            Forget();
            _mem.Ptr = cam.Ptr;
        }

        Rig want = Wanted;
        if (want.NoShake)
            ClearShakes(cam);

        AimTick(cam, want.Aim);

        Rig applied = _mem.Applied;
        bool recheck = _recheck.Elapsed(RecheckSeconds);
        if (want == applied && !recheck)
            return;

        if (want == Vanilla && applied == Vanilla && !_mem.Live)
            return;

        Apply(cam, want);
    }

    private static bool IsAimState(string state)
    {
        if (state.Length == 0 || state.Equals("Walk", StringComparison.OrdinalIgnoreCase))
            return false;
        if (AimStates.Contains(state) || state.Contains("Aim", StringComparison.OrdinalIgnoreCase))
            return true;

        return Safe.Try("gadget camera", () =>
        {
            RInventoryGadget? gadget = World.Pc?.GetCurrentGadget();
            return gadget is { IsValid: true } && state.Equals(gadget.GetGadgetCamera(false).ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    private void AimTick(R3rdPersonCamera cam, float aim)
    {
        if (!_aimOn && MathF.Abs(aim - 1f) < 0.001f)
        {
            _aimState = "";
            return;
        }

        string state = StateName(cam);
        float fov = cam.CameraFOVAngle;
        if (state != _aimState)
        {
            if (_aimOn)
            {
                if (MathF.Abs(fov - _aimWritten) < 0.01f)
                {
                    cam.CameraFOVAngle = _aimBase;
                    fov = _aimBase;
                }

                _aimOn = false;
            }

            bool inherits = _aimState.Length > 0 && MathF.Abs(fov - _aimPrevFov) < 0.01f;
            _aimState = state;
            if (inherits && fov > 0f && MathF.Abs(aim - 1f) >= 0.001f && IsAimState(state))
            {
                _aimOn = true;
                _aimBase = fov;
                _aimWritten = -1f;
            }
        }

        if (_aimOn)
        {
            if (_aimWritten >= 0f && MathF.Abs(fov - _aimWritten) > 0.01f)
                _aimBase = fov;

            float next = Math.Clamp(_aimBase * aim, MinAimFov, MaxFov);
            if (MathF.Abs(next - fov) > 0.01f)
                cam.CameraFOVAngle = next;
            _aimWritten = next;

            if (MathF.Abs(aim - 1f) < 0.001f)
            {
                cam.CameraFOVAngle = _aimBase;
                _aimOn = false;
            }
        }

        _aimPrevFov = cam.CameraFOVAngle;
    }

    private void Forget()
    {
        _mem.Clear();
        _stateName = "";
        _aimState = "";
        _aimOn = false;
    }

    private void Apply(R3rdPersonCamera cam, Rig s)
    {
        Rig old = _mem.Applied;

        foreach (string n in FootFovs)
            MapFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov, MinFov, MaxFov));
        foreach (string n in GlideFovs)
            MapFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov + s.GlideFov, MinFov, MaxFov));
        foreach (string n in CarFovs)
            MapFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v + s.Fov + s.CarFov, MinFov, MaxFov));
        foreach (string n in AimFovs)
            MapFloat(cam, n, v => v <= 0f ? v : Math.Clamp(v * s.Aim, MinAimFov, MaxFov));
        foreach (string n in GlideKicks)
            MapFloat(cam, n, v => v * s.GlideKick);
        foreach (string n in CarKicks)
            MapFloat(cam, n, v => v * s.CarKick);
        foreach (string n in SwayFloats)
            MapFloat(cam, n, v => s.Steady ? 0f : v);
        foreach (string n in GlideOffsets)
            MapVec(cam, n, v => v * s.GlideDist);

        bool swayNow = cam.bSwayWithPelvis;
        if (s.Steady)
        {
            if (_mem.Sway == 0)
                _mem.Sway = swayNow ? 2 : 1;
            if (swayNow)
                cam.bSwayWithPelvis = false;
        }
        else if (_mem.Sway != 0)
        {
            bool sway = _mem.Sway == 2;
            if (swayNow != sway)
                cam.bSwayWithPelvis = sway;
            _mem.Sway = 0;
        }

        ApplyShake(cam, s.NoShake, old.NoShake);

        CamConfig current = cam.CurrentCameraConfig;
        string? liveCfg = null;
        CamConfig liveNew = default;
        foreach (string n in FootCfgs)
        {
            if (MapConfig(cam, n, c => FootConfig(c, s), current, out CamConfig made) && liveCfg == null)
            {
                liveCfg = n;
                liveNew = made;
            }
        }

        foreach (string n in CarCfgs)
        {
            if (MapConfig(cam, n, c => CarConfig(c, s), current, out CamConfig made) && liveCfg == null)
            {
                liveCfg = n;
                liveNew = made;
            }
        }

        if (liveCfg != null)
            cam.SetFreeCameraConfig(liveNew);

        if (old != s)
            LiveFov(cam, old, s);

        _mem.Applied = s;
        _mem.Live = s != Vanilla;
    }

    private static int Category(string state)
    {
        if (state.Contains("Glid", StringComparison.OrdinalIgnoreCase) || state.Contains("Dive", StringComparison.OrdinalIgnoreCase))
            return CatGlide;
        if (AimStates.Contains(state) || state.Contains("Aim", StringComparison.OrdinalIgnoreCase) || state.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || state.Contains("Sniper", StringComparison.OrdinalIgnoreCase))
            return CatAim;
        if (state.Contains("Driv", StringComparison.OrdinalIgnoreCase) || state.Contains("Batmobile", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Battle", StringComparison.OrdinalIgnoreCase) || state.Contains("Vehicle", StringComparison.OrdinalIgnoreCase))
            return CatCar;
        return CatFoot;
    }

    public static string StateName(R3rdPersonCamera cam)
    {
        try
        {
            return cam.GetStateName().ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static float FovFor(int cat, float vanilla, Rig s) => cat switch
    {
        CatGlide => vanilla + s.Fov + s.GlideFov,
        CatCar => vanilla + s.Fov + s.CarFov,
        CatAim => vanilla * s.Aim,
        _ => vanilla + s.Fov,
    };

    private static float FovBack(int cat, float shown, Rig s) => cat switch
    {
        CatGlide => shown - s.Fov - s.GlideFov,
        CatCar => shown - s.Fov - s.CarFov,
        CatAim => s.Aim > 0.01f ? shown / s.Aim : shown,
        _ => shown - s.Fov,
    };

    private void LiveFov(R3rdPersonCamera cam, Rig old, Rig s)
    {
        if (_aimOn)
        {
            _aimBase = FovFor(CatFoot, FovBack(CatFoot, _aimBase, old), s);
            return;
        }

        int cat = Category(StateName(cam));
        float shown = cam.CameraFOVAngle;
        if (shown <= 0f)
            return;

        float next = Math.Clamp(FovFor(cat, FovBack(cat, shown, old), s), MinAimFov, MaxFov);
        if (MathF.Abs(next - shown) > 0.01f)
            cam.CameraFOVAngle = next;
    }

    private static Vector3 SitMove(Vector3 v, Rig s) => new(v.X, v.Y * s.Shoulder, v.Z + s.Height);

    private static CamConfig FootConfig(CamConfig c, Rig s)
    {
        c.StateFreeCameraSitOffsetMin = SitMove(c.StateFreeCameraSitOffsetMin, s);
        c.StateFreeCameraSitOffsetMax = SitMove(c.StateFreeCameraSitOffsetMax, s);
        if (c.UseStateFreeCameraSitOffsetUp)
            c.StateFreeCameraSitOffsetUp = SitMove(c.StateFreeCameraSitOffsetUp, s);
        if (c.bUseSeparate43Settings)
        {
            c.StateFreeCamera43SitOffsetMin = SitMove(c.StateFreeCamera43SitOffsetMin, s);
            c.StateFreeCamera43SitOffsetMax = SitMove(c.StateFreeCamera43SitOffsetMax, s);
            c.StateFreeCamera43SitOffsetLookUp = SitMove(c.StateFreeCamera43SitOffsetLookUp, s);
        }

        Vector3 pivot = c.CameraPivotOffset;
        c.CameraPivotOffset = new Vector3(pivot.X, pivot.Y * s.Shoulder, pivot.Z + s.Pivot);
        c.MinFreeCameraDistance = MathF.Max(5f, c.MinFreeCameraDistance + s.Pull);
        c.MaxFreeCameraDistance = MathF.Max(5f, c.MaxFreeCameraDistance + s.Pull);
        return c;
    }

    private static CamConfig CarConfig(CamConfig c, Rig s)
    {
        float k = s.CarDist;
        c.StateFreeCameraSitOffsetMin *= k;
        c.StateFreeCameraSitOffsetMax *= k;
        if (c.UseStateFreeCameraSitOffsetUp)
            c.StateFreeCameraSitOffsetUp *= k;
        c.MinFreeCameraDistance *= k;
        c.MaxFreeCameraDistance *= k;
        return c;
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 0.0001f;

    private static bool SameConfig(in CamConfig a, in CamConfig b)
    {
        return Near(a.StateFreeCameraSitOffsetMin, b.StateFreeCameraSitOffsetMin) && Near(a.StateFreeCameraSitOffsetMax, b.StateFreeCameraSitOffsetMax)
            && Near(a.StateFreeCameraSitOffsetUp, b.StateFreeCameraSitOffsetUp) && Near(a.CameraPivotOffset, b.CameraPivotOffset)
            && Near(a.StateFreeCamera43SitOffsetMin, b.StateFreeCamera43SitOffsetMin) && Near(a.StateFreeCamera43SitOffsetMax, b.StateFreeCamera43SitOffsetMax)
            && Near(a.StateFreeCamera43SitOffsetLookUp, b.StateFreeCamera43SitOffsetLookUp)
            && MathF.Abs(a.MinFreeCameraDistance - b.MinFreeCameraDistance) < 0.01f && MathF.Abs(a.MaxFreeCameraDistance - b.MaxFreeCameraDistance) < 0.01f
            && MathF.Abs(a.maxPitch - b.maxPitch) < 0.5f && MathF.Abs(a.ShortCamSpringConst - b.ShortCamSpringConst) < 0.01f;
    }

    private bool MapConfig(R3rdPersonCamera cam, string name, Func<CamConfig, CamConfig> map, in CamConfig current, out CamConfig made)
    {
        made = default;
        CfgRef? get = Bind(_cfgs, name);
        if (get == null)
            return false;

        ref CamConfig slot = ref get(cam);
        CamConfig now = slot;
        if (!_mem.CfgWritten.TryGetValue(name, out CamConfig written) || !SameConfig(now, written))
        {
            _mem.CfgOrig[name] = now;
            written = now;
        }

        made = map(_mem.CfgOrig[name]);
        bool changed = !SameConfig(now, made);
        if (changed)
            slot = made;
        _mem.CfgWritten[name] = made;

        return changed && SameConfig(current, written);
    }

    private void MapVec(R3rdPersonCamera cam, string name, Func<Vector3, Vector3> map)
    {
        VecRef? get = Bind(_vecs, name);
        if (get == null)
            return;

        ref Vector3 slot = ref get(cam);
        Vector3 now = slot;
        if (!_mem.VecWritten.TryGetValue(name, out Vector3 written) || !Near(now, written))
            _mem.VecOrig[name] = now;

        Vector3 v = map(_mem.VecOrig[name]);
        if (!Near(now, v))
            slot = v;
        _mem.VecWritten[name] = v;
    }

    private void MapFloat(R3rdPersonCamera cam, string name, Func<float, float> map)
    {
        if (!_floats.TryGetValue(name, out var access))
        {
            PropertyInfo? p = typeof(R3rdPersonCamera).GetProperty(name);
            if (p != null && p.PropertyType == typeof(float) && p.CanWrite)
                access = (p.GetGetMethod()!.CreateDelegate<Func<R3rdPersonCamera, float>>(), p.GetSetMethod()!.CreateDelegate<Action<R3rdPersonCamera, float>>());
            else
                Log.Info("Camera value missing: " + name);
            _floats[name] = access;
        }

        if (access is not { } a)
            return;

        float now = a.Get(cam);
        if (!_mem.FloatWritten.TryGetValue(name, out float written) || MathF.Abs(now - written) > 0.001f)
            _mem.FloatOrig[name] = now;

        float v = map(_mem.FloatOrig[name]);
        if (MathF.Abs(now - v) > 0.0001f)
            a.Set(cam, v);
        _mem.FloatWritten[name] = v;
    }

    private static T? Bind<T>(Dictionary<string, T?> cache, string name) where T : Delegate
    {
        if (cache.TryGetValue(name, out T? bound))
            return bound;

        MethodInfo? m = typeof(R3rdPersonCamera).GetProperty(name)?.GetGetMethod();
        bound = m?.CreateDelegate<T>();
        if (bound == null)
            Log.Info("Camera entry missing: " + name);
        cache[name] = bound;
        return bound;
    }

    private static void ApplyShake(R3rdPersonCamera cam, bool off, bool wasOff)
    {
        CameraModifier m = cam.CameraShakeCamMod;
        if (m is not { IsValid: true })
            return;

        if (off && !m.bDisabled)
            m.DisableModifier(true);
        else if (!off && wasOff && m.bDisabled)
            m.EnableModifier();
    }

    private static void ClearShakes(R3rdPersonCamera cam)
    {
        RBMScreenShakeModifier shake = cam.ScreenShakeModifier;
        if (shake is { IsValid: true } && shake.Shakes.Count > 0)
            shake.Reset();
    }

    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.SetCombatFOV))]
    private static void SetCombatFovRedirect(RPawnPlayerCombat self, float minFov, int minEnemies, float maxFov, int maxEnemies, int enemies)
    {
        Memory? mem = I._mem;
        float add = mem is { Ptr: not 0 } ? mem.Applied.Fov : 0f;
        if (add != 0f)
        {
            minFov = minFov > 0f ? Math.Clamp(minFov + add, MinFov, MaxFov) : minFov;
            maxFov = maxFov > 0f ? Math.Clamp(maxFov + add, MinFov, MaxFov) : maxFov;
        }

        self.SetCombatFOV(minFov, minEnemies, maxFov, maxEnemies, enemies);
    }

    public override void Draw(Canvas c)
    {
        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null)
            return;

        float real = cam.CameraCache.POV.FOV;
        float game = cam.FinalCameraFOVAngle;
        Pawn? pawn = World.Pawn;
        float dist = pawn != null ? Vector3.Distance(cam.CameraCache.POV.Location, pawn.Location) : 0f;
        string state = StateName(cam);
        if (state != _stateName)
        {
            _stateName = state;
            _stateCat = Category(state);
        }

        string mode = _stateCat switch { CatGlide => "GLIDE", CatCar => "BATMOBILE", CatAim => "AIM", _ => "ON FOOT" };
        string line1 = "FOV " + real.ToString("0.0", CultureInfo.InvariantCulture) + "  ON SCREEN";
        string line2 = "GAME " + game.ToString("0.0", CultureInfo.InvariantCulture) + "  " + mode + "  " + state.ToUpperInvariant();
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
