using System.Numerics;
using System.Reflection;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using CamConfig = BmSDK.BmGame.R3rdPersonCamera.FFreeCameraConfig;

namespace CeZeY.Oracle;

/// <summary>
/// The CAMERA page. This file rewrites the camera's per-state framing values (distance, height, shoulder, pivot,
/// glide and Batmobile distance, speed kicks, sway); <c>CameraRig.View.cs</c> changes the finished picture each
/// frame (field of view, tilt, first person, HUD, bars).
///
/// The game changes the framing values too, and many states save the live values back into their own config
/// when they end (Walk, AgilityWalk, the combat cameras). So every write is recorded, and when a value no longer
/// matches, only the parts the game really changed become the new vanilla baseline: a field still holding what
/// Oracle wrote keeps its old original. A value that turns up as an exact copy of what Oracle wrote to a sibling
/// (the Batmobile's three camera sets, the alternative walk camera) takes that sibling's original. Without this,
/// each save-back would be taken as vanilla and offset again, and the camera would creep further every time.
/// </summary>
internal sealed partial class CameraRig : Module
{
    public static readonly CameraRig I = new();

    private const double RecheckSeconds = 1.0;
    private const float Tolerance = 0.001f;
    private const string MemoryKey = "CameraMemory.v2";
    private const string OldMemoryKey = "CameraMemory";

    private delegate ref CamConfig CfgRef(R3rdPersonCamera cam);
    private delegate ref Vector3 VecRef(R3rdPersonCamera cam);

    private readonly record struct Rig(float Height, float Shoulder, float Pull, float Pivot, float GlideKick, float GlideDist, float CarKick, float CarDist, bool Steady, bool NoShake);

    private static readonly Rig Vanilla = new(0f, 1f, 0f, 0f, 1f, 1f, 1f, 1f, false, false);

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

    private static readonly string[] GlideKicks = { "GlidingCameraExtraFOVAngle" };
    private static readonly string[] CarKicks = { "DrivingCamMaxExtraFOV", "DrivingCamMaxExtraFOV1", "DrivingCamMaxExtraFOV2" };

    /// <summary>
    /// Hip sway and the running camera's overlay animation (RunLoop, played at RunOverlayStrength when a run
    /// starts), which is the handheld bob while running.
    /// </summary>
    private static readonly string[] SwayFloats = { "PelvisSway", "DefaultPelvisSway", "RunStatePelvisSway", "CurrentPelvisSway", "RunOverlayStrength" };

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

    private static readonly FName DrivingState = new("DrivingCamera");
    private static readonly FName RunLoop = new("RunLoop");

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
            new long[2], new float[] { 0f, 1f, 0f, 0f, 1f, 1f, 1f, 1f, 0f, 0f },
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

        public Rig Applied
        {
            get
            {
                float[] a = Values;
                return new Rig(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8] > 0.5f, a[9] > 0.5f);
            }
            set
            {
                float[] a = Values;
                (a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]) = (value.Height, value.Shoulder, value.Pull, value.Pivot, value.GlideKick, value.GlideDist, value.CarKick, value.CarDist);
                (a[8], a[9]) = (value.Steady ? 1f : 0f, value.NoShake ? 1f : 0f);
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
            Applied = Vanilla;
        }
    }

    private readonly Dictionary<string, (Func<R3rdPersonCamera, float> Get, Action<R3rdPersonCamera, float> Set)?> _floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CfgRef?> _cfgs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VecRef?> _vecs = new(StringComparer.Ordinal);
    private Memory _mem = null!;
    private Every _recheck;

    private Slider _height = null!, _shoulder = null!, _pull = null!, _pivot = null!;
    private Slider _glideKick = null!, _glideDist = null!, _carKick = null!, _carDist = null!;
    private Toggle _steady = null!, _noShake = null!;
    private Choice _preset = null!, _carView = null!;

    // ---- BATMOBILE VIEW: applied once each time the driving camera starts
    private bool _carViewDone;
    private int _carViewTries;
    private Every _carViewStep;

    private Rig Wanted => new(_height.Value, _shoulder.Value, _pull.Value, _pivot.Value, _glideKick.Value, _glideDist.Value, _carKick.Value, _carDist.Value, _steady.On, _noShake.On);

    public override void Build()
    {
        BuildView();

        _height = AddSlider("cam_height", "CAMERA HEIGHT", "Raises or lowers where the camera sits behind your character when on foot.", 0f, -60f, 120f, 5f, 30f, Fmt.Signed);
        _shoulder = AddSlider("cam_shoulder", "SHOULDER OFFSET", "How far the camera sits to the side. CENTRED puts your character in the middle, negative values swap to the left shoulder.", 1f, -1.5f, 2f, 0.1f, 0f, Fmt.Side);
        _pull = AddSlider("cam_distance", "CAMERA DISTANCE", "Pulls the camera further back from your character on foot, or closer in with negative values. FOV READOUT shows the distance live.", 0f, -60f, 400f, 10f, 150f, Fmt.Signed);
        _pivot = AddSlider("cam_pivot", "PIVOT HEIGHT", "Moves the point the camera orbits around and looks at up or down. Higher looks over your character's head.", 0f, -50f, 100f, 5f, 30f, Fmt.Signed);
        AddCommand("cam_swap_shoulder", "SWAP SHOULDER", "Moves the camera over your other shoulder. Bind it to a key to swap mid-fight.", SwapShoulder);

        _glideKick = AddSlider("glide_kick", "DIVE SPEED KICK", "How much the view widens as you pick up speed while gliding. 0% keeps it steady.", 1f, 0f, 3f, 0.25f, 2f, Fmt.Percent);
        _glideDist = AddSlider("glide_distance", "GLIDE CAMERA DISTANCE", "How far behind you the camera trails while gliding.", 1f, 0.5f, 2.5f, 0.1f, 1.5f, Fmt.Times);

        _carKick = AddSlider("car_kick", "SPEED FOV KICK", "How much the view widens at high speed and when boosting.", 1f, 0f, 3f, 0.25f, 2f, Fmt.Percent);
        _carDist = AddSlider("car_distance", "BATMOBILE CAMERA DISTANCE", "How far behind the Batmobile the camera sits, in car and battle mode.", 1f, 0.5f, 2.5f, 0.1f, 1.4f, Fmt.Times);
        _carView = AddChoice("car_view", "BATMOBILE VIEW", "Which of the game's driving cameras to start in each time you get in: its two chase cameras or the low bonnet camera. GAME leaves the game's choice. The game's own key (C, or RB on a pad) still cycles them, and the game leaves the bonnet camera in deep water.",
            new[] { "game", "chase1", "chase2", "bonnet" }, new[] { "GAME", "CHASE 1", "CHASE 2", "BONNET" }, _ => RestartCarView());
        AddCommand("car_view_next", "NEXT BATMOBILE VIEW", "Cycles the driving camera, like the game's own key. Only works while driving.", NextCarView);

        _preset = AddChoice("fov_preset", "CAMERA PRESET", "Sets every camera slider at once. Tweak them afterwards as you like. VANILLA puts everything back to the game's camera.",
            Presets.Select(p => p.Key).ToArray(), Presets.Select(p => p.Label).ToArray(), ApplyPreset).Pref();
        _steady = AddToggle("cam_steady", "STEADY CAMERA", "Stops the camera swaying with your character's hips and the handheld bob while running.");
        _noShake = AddToggle("cam_no_shake", "NO SCREEN SHAKE", "Turns off camera shakes from explosions, landings, gliding and the Batmobile.");
        AddCommand("cam_reset", "RESET CAMERA", "Puts every setting on the CAMERA pages back to the game's default. FREE CAMERA options are kept.", ResetAll);
    }

    public override void Started()
    {
        // An Oracle from before the field of view moved to the final picture kept its record under the old key.
        // Its own unload put every value back, so the record is only dropped here.
        Shared.Set(OldMemoryKey, null);
        _mem = new Memory(Shared.Keep(MemoryKey, Memory.Make));
    }

    public override void LeftGame()
    {
        if (_mem.Ptr != 0)
            _mem.Clear();
        LeftGameView();
    }

    public override void Shutdown()
    {
        Safe.Run("camera restore", () =>
        {
            R3rdPersonCamera? cam = PlayerCam();
            if (cam != null && cam.Ptr == _mem.Ptr && (_mem.Live || _mem.Applied != Vanilla))
                Apply(cam, Vanilla);
        });
        ShutdownView();
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

    private void SwapShoulder()
    {
        float v = _shoulder.Value;
        if (MathF.Abs(v) < 0.05f)
            throw new InvalidOperationException("the camera is centred");

        Set(_shoulder, -v);
    }

    private void ResetAll()
    {
        foreach (Slider f in new[] { _fov, _height, _shoulder, _pull, _pivot, _aim, _glideFov, _glideKick, _glideDist, _carFov, _carKick, _carDist, _tilt, _fpForward })
            Set(f, f.Default);

        foreach (Toggle t in new[] { _steady, _noShake, _collision, _firstPerson, _hideHud })
            t.On = false;

        _preset.ResetValue();
        _carView.ResetValue();
        _bars.ResetValue();
        Settings.MarkDirty();
    }

    internal static R3rdPersonCamera? PlayerCam() =>
        World.Pc?.PlayerCamera is R3rdPersonCamera { IsValid: true, IsClassDefaultObject: false } cam ? cam : null;

    public override void Tick(float dt)
    {
        R3rdPersonCamera? cam = PlayerCam();
        if (cam == null)
            return;

        if (cam.Ptr != _mem.Ptr)
        {
            _mem.Clear();
            _mem.Ptr = cam.Ptr;
        }

        Rig want = Wanted;
        Rig applied = _mem.Applied;
        bool recheck = _recheck.Elapsed(RecheckSeconds);
        if ((want != applied || recheck) && !(want == Vanilla && applied == Vanilla && !_mem.Live))
            Apply(cam, want);

        CarViewTick(cam);
    }

    private void Apply(R3rdPersonCamera cam, Rig s)
    {
        Rig old = _mem.Applied;

        foreach (string n in GlideKicks)
            MapFloat(cam, n, GlideKicks, v => v * s.GlideKick);
        foreach (string n in CarKicks)
            MapFloat(cam, n, CarKicks, v => v * s.CarKick);
        foreach (string n in SwayFloats)
            MapFloat(cam, n, SwayFloats, v => s.Steady ? 0f : v);
        foreach (string n in GlideOffsets)
            MapVec(cam, n, GlideOffsets, v => v * s.GlideDist);

        // The run bob is an overlay animation started with the run, so one already playing is stopped here.
        if (s.Steady && !old.Steady)
            Safe.Run("run camera bob", () => cam.OverlayPlayer?.StopAnim(RunLoop, 0.3f));

        ApplyShake(cam, s.NoShake, old.NoShake);

        CamConfig current = cam.CurrentCameraConfig;
        CamConfig? live = null;
        foreach (string n in FootCfgs)
        {
            if (MapConfig(cam, n, FootCfgs, c => FootConfig(c, s), current, out CamConfig made) && live == null)
                live = made;
        }

        foreach (string n in CarCfgs)
        {
            if (MapConfig(cam, n, CarCfgs, c => CarConfig(c, s), current, out CamConfig made) && live == null)
                live = made;
        }

        if (live is { } config)
            cam.SetFreeCameraConfig(config);

        _mem.Applied = s;
        _mem.Live = s != Vanilla;
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

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;

    private static bool SameConfig(in CamConfig a, in CamConfig b)
    {
        return Near(a.StateFreeCameraSitOffsetMin, b.StateFreeCameraSitOffsetMin) && Near(a.StateFreeCameraSitOffsetMax, b.StateFreeCameraSitOffsetMax)
            && Near(a.StateFreeCameraSitOffsetUp, b.StateFreeCameraSitOffsetUp) && Near(a.CameraPivotOffset, b.CameraPivotOffset)
            && Near(a.StateFreeCamera43SitOffsetMin, b.StateFreeCamera43SitOffsetMin) && Near(a.StateFreeCamera43SitOffsetMax, b.StateFreeCamera43SitOffsetMax)
            && Near(a.StateFreeCamera43SitOffsetLookUp, b.StateFreeCamera43SitOffsetLookUp)
            && Near(a.MinFreeCameraDistance, b.MinFreeCameraDistance) && Near(a.MaxFreeCameraDistance, b.MaxFreeCameraDistance)
            && MathF.Abs(a.maxPitch - b.maxPitch) < 0.5f && MathF.Abs(a.ShortCamSpringConst - b.ShortCamSpringConst) < 0.01f;
    }

    /// <summary>
    /// A config the game changed, as its new vanilla: the game's values, except fields Oracle rewrites that still
    /// hold exactly what Oracle wrote there. Those came back in a save-back of the live camera, not from the game.
    /// </summary>
    private static CamConfig Merge(CamConfig now, in CamConfig written, in CamConfig orig)
    {
        if (Near(now.StateFreeCameraSitOffsetMin, written.StateFreeCameraSitOffsetMin))
            now.StateFreeCameraSitOffsetMin = orig.StateFreeCameraSitOffsetMin;
        if (Near(now.StateFreeCameraSitOffsetMax, written.StateFreeCameraSitOffsetMax))
            now.StateFreeCameraSitOffsetMax = orig.StateFreeCameraSitOffsetMax;
        if (Near(now.StateFreeCameraSitOffsetUp, written.StateFreeCameraSitOffsetUp))
            now.StateFreeCameraSitOffsetUp = orig.StateFreeCameraSitOffsetUp;
        if (Near(now.StateFreeCamera43SitOffsetMin, written.StateFreeCamera43SitOffsetMin))
            now.StateFreeCamera43SitOffsetMin = orig.StateFreeCamera43SitOffsetMin;
        if (Near(now.StateFreeCamera43SitOffsetMax, written.StateFreeCamera43SitOffsetMax))
            now.StateFreeCamera43SitOffsetMax = orig.StateFreeCamera43SitOffsetMax;
        if (Near(now.StateFreeCamera43SitOffsetLookUp, written.StateFreeCamera43SitOffsetLookUp))
            now.StateFreeCamera43SitOffsetLookUp = orig.StateFreeCamera43SitOffsetLookUp;
        if (Near(now.CameraPivotOffset, written.CameraPivotOffset))
            now.CameraPivotOffset = orig.CameraPivotOffset;
        if (Near(now.MinFreeCameraDistance, written.MinFreeCameraDistance))
            now.MinFreeCameraDistance = orig.MinFreeCameraDistance;
        if (Near(now.MaxFreeCameraDistance, written.MaxFreeCameraDistance))
            now.MaxFreeCameraDistance = orig.MaxFreeCameraDistance;
        return now;
    }

    /// <summary>
    /// Writes one config, worked out from its vanilla value. True when this config is the one the camera is using
    /// right now (its content before this write matches the live config), so the caller pushes it live.
    /// </summary>
    private bool MapConfig(R3rdPersonCamera cam, string name, string[] group, Func<CamConfig, CamConfig> map, in CamConfig current, out CamConfig made)
    {
        made = default;
        CfgRef? get = Bind(_cfgs, name);
        if (get == null)
            return false;

        ref CamConfig slot = ref get(cam);
        CamConfig now = slot;
        if (!_mem.CfgWritten.TryGetValue(name, out CamConfig written))
            _mem.CfgOrig[name] = now;
        else if (!SameConfig(now, written))
            _mem.CfgOrig[name] = CopiedConfig(group, name, now) ?? Merge(now, written, _mem.CfgOrig[name]);

        made = map(_mem.CfgOrig[name]);
        bool changed = !SameConfig(now, made);
        if (changed)
            slot = made;
        _mem.CfgWritten[name] = made;

        return changed && SameConfig(current, now);
    }

    private CamConfig? CopiedConfig(string[] group, string name, in CamConfig now)
    {
        foreach (string other in group)
        {
            if (other != name && _mem.CfgWritten.TryGetValue(other, out CamConfig w) && SameConfig(now, w)
                && _mem.CfgOrig.TryGetValue(other, out CamConfig o) && !SameConfig(w, o))
                return o;
        }

        return null;
    }

    private void MapVec(R3rdPersonCamera cam, string name, string[] group, Func<Vector3, Vector3> map)
    {
        VecRef? get = Bind(_vecs, name);
        if (get == null)
            return;

        ref Vector3 slot = ref get(cam);
        Vector3 now = slot;
        if (!_mem.VecWritten.TryGetValue(name, out Vector3 written) || !Near(now, written))
            _mem.VecOrig[name] = CopiedVec(group, name, now) ?? now;

        Vector3 v = map(_mem.VecOrig[name]);
        if (!Near(now, v))
            slot = v;
        _mem.VecWritten[name] = v;
    }

    private Vector3? CopiedVec(string[] group, string name, Vector3 now)
    {
        foreach (string other in group)
        {
            if (other != name && _mem.VecWritten.TryGetValue(other, out Vector3 w) && Near(now, w)
                && _mem.VecOrig.TryGetValue(other, out Vector3 o) && !Near(w, o))
                return o;
        }

        return null;
    }

    private void MapFloat(R3rdPersonCamera cam, string name, string[] group, Func<float, float> map)
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
        if (!_mem.FloatWritten.TryGetValue(name, out float written) || MathF.Abs(now - written) > Tolerance)
            _mem.FloatOrig[name] = CopiedFloat(group, name, now) ?? now;

        float v = map(_mem.FloatOrig[name]);
        if (MathF.Abs(now - v) > 0.0001f)
            a.Set(cam, v);
        _mem.FloatWritten[name] = v;
    }

    private float? CopiedFloat(string[] group, string name, float now)
    {
        foreach (string other in group)
        {
            if (other != name && _mem.FloatWritten.TryGetValue(other, out float w) && MathF.Abs(now - w) <= Tolerance
                && _mem.FloatOrig.TryGetValue(other, out float o) && MathF.Abs(w - o) > Tolerance)
                return o;
        }

        return null;
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

    // ---- BATMOBILE VIEW

    private void RestartCarView()
    {
        _carViewDone = false;
        _carViewTries = 0;
    }

    // Whether the camera's state is a driving one, remembered per state: the answer only changes with the state,
    // and asking the game (IsChildState) every tick is a script call.
    private int _drivingState = -1;
    private bool _driving;

    private bool Driving(R3rdPersonCamera cam)
    {
        FName state = cam.GetStateName();
        if (state.Index == _drivingState)
            return _driving;

        try
        {
            _driving = cam.IsChildState(state, DrivingState);
            _drivingState = state.Index;
        }
        catch (Exception ex)
        {
            Log.Error("driving camera", "driving camera", ex);
            return false;
        }

        return _driving;
    }

    /// <summary>
    /// Steps the driving camera to the chosen one with the game's own toggle, once per drive, so the game's key
    /// and its deep-water switch still win afterwards.
    /// </summary>
    private void CarViewTick(R3rdPersonCamera cam)
    {
        int want = _carView.Index - 1;
        if (want < 0 || !Driving(cam))
        {
            RestartCarView();
            return;
        }

        if (_carViewDone)
            return;

        if ((int)cam.DrivingCameraSetup == want || _carViewTries >= 3)
        {
            _carViewDone = true;
            return;
        }

        if (!cam.CanToggleDrivingCam || !_carViewStep.Elapsed(0.4))
            return;

        _carViewTries++;
        cam.ToggleDrivingCam();
    }

    private void NextCarView()
    {
        R3rdPersonCamera cam = PlayerCam() ?? throw new InvalidOperationException("no camera yet");
        if (!Driving(cam))
            throw new InvalidOperationException("get in the Batmobile first");
        if (!cam.CanToggleDrivingCam)
            throw new InvalidOperationException("the game won't switch cameras right now");

        cam.ToggleDrivingCam();
        _carViewDone = true;
    }
}
