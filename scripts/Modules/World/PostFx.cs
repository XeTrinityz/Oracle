using System.Globalization;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using PPSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;

namespace CeZeY.Oracle;

/// <summary>
/// Exposure, bloom, lens effects and fog placement, written into every post process volume and the world's
/// default settings. Atmosphere owns the colour and density fields of the same structs; these are separate
/// fields, so the two never write the same value. Values are written whether or not a volume overrides them:
/// one that doesn't override ignores it, and the world default it falls back to gets the same change.
/// </summary>
internal sealed class PostFx : Module
{
    public static readonly PostFx I = new();

    private const string OriginalsKey = "PostFxFields";
    private const string DefaultSettingsKey = "__WorldDefault__";
    private const double ScanSeconds = 1.0;
    private const float DefaultAperture = 8f;

    private enum F
    {
        Exposure, BloomOn, Bloom, BlurOn, Blur, Grain, DofOn, Focus, Aperture, D1Start, D1End, D2Start, D2End, H1Pos, H2Pos, Count,
    }

    private Slider _exposure = null!;
    private Slider _bloom = null!;
    private Slider _blur = null!;
    private Slider _grain = null!;
    private Slider _focus = null!;
    private Slider _dofBlur = null!;
    private Slider _fogDistance = null!;
    private Slider _fogHeight = null!;
    private Every _scan;
    private bool _dirty;

    // Plain doubles so the originals outlive a hot reload, which would orphan any type of ours.
    private static Dictionary<string, double[]> Originals => Shared.Keep(OriginalsKey, () => new Dictionary<string, double[]>());

    public bool Wanted => !Vanilla;

    private bool Vanilla => _exposure.IsDefault && _bloom.IsDefault && _blur.IsDefault && _grain.IsDefault && _focus.IsDefault && _dofBlur.IsDefault
        && _fogDistance.IsDefault && _fogHeight.IsDefault;

    public override void Build()
    {
        _exposure = Add(AddSlider("fx_exposure", "EXPOSURE", "Brightens or darkens the whole picture, in camera stops.", 0f, -3f, 3f, 0.25f, 1f, Stops));
        _bloom = Add(AddSlider("fx_bloom", "BLOOM", "The glow around neon signs, lamps and bright lights. OFF removes it.", 1f, 0f, 3f, 0.25f, 2f, OffOr(Fmt.Times)));
        _blur = Add(AddSlider("fx_motion_blur", "MOTION BLUR", "The smear when the camera or Batman moves fast. OFF gives a sharp picture.", 1f, 0f, 3f, 0.25f, 0f, OffOr(Fmt.Times)));
        _grain = Add(AddSlider("fx_grain", "FILM GRAIN", "Adds film grain to the picture. GAME leaves the game's own.", 0f, 0f, 1f, 0.05f, 0.4f, v => v <= 0f ? "GAME" : Fmt.Percent(v)));
        _focus = Add(AddSlider("fx_dof_focus", "DEPTH OF FIELD", "Keeps things this far from the camera sharp and blurs what's nearer and further, like a photo lens. OFF leaves the game's own.",
            0f, 0f, 100f, 2f, 10f, v => v <= 0f ? "OFF" : Fmt.Metres(v)));
        _dofBlur = Add(AddSlider("fx_dof_blur", "DEPTH OF FIELD BLUR", "How strongly the depth of field blurs what's out of focus.", 1f, 0.25f, 4f, 0.25f, 2f, Fmt.Times));
        _fogDistance = Add(AddSlider("fx_fog_distance", "FOG DISTANCE", "Moves where the fog starts and how far off it thickens. Higher keeps the streets around you clear and pushes the fog to the skyline.",
            1f, 0.25f, 4f, 0.25f, 2f, Fmt.Times));
        _fogHeight = Add(AddSlider("fx_fog_height", "FOG HEIGHT", "Raises or lowers the layer of low fog that hangs over the streets.", 0f, -100f, 100f, 5f, 30f, v => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + Fmt.Metres(v)));
    }

    private Slider Add(Slider s)
    {
        s.Changed = _ => _dirty = true;
        return s;
    }

    private static Func<float, string> OffOr(Func<float, string> fmt) => v => v <= 0f ? "OFF" : fmt(v);

    private static string Stops(float v) =>
        MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + v.ToString("0.00", CultureInfo.InvariantCulture) + " STOPS";

    public override void WorldChanged()
    {
        _dirty = true;
        _scan.Force();
    }

    public override void Shutdown() => Safe.Run("post fx restore", () => WriteAll(restore: true));

    public override void Tick(float dt)
    {
        bool vanilla = Vanilla;
        if (vanilla && Originals.Count == 0)
            return;

        if (!_dirty && !_scan.Elapsed(ScanSeconds))
            return;

        _dirty = false;
        WriteAll(restore: vanilla);
    }

    private void WriteAll(bool restore)
    {
        Dictionary<string, double[]> originals = Originals;
        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
            Apply(ref v.Settings, v.GetPathName(null), originals, restore);

        if (World.WorldInfo is { } wi)
            Apply(ref wi.DefaultPostProcessSettings, DefaultSettingsKey, originals, restore);

        if (restore)
            originals.Clear();
    }

    private void Apply(ref PPSettings st, string key, Dictionary<string, double[]> originals, bool restore)
    {
        if (restore)
        {
            if (originals.TryGetValue(key, out double[]? back))
                Write(ref st, back);
            return;
        }

        if (!originals.TryGetValue(key, out double[]? o))
        {
            o = Read(ref st);
            originals[key] = o;
        }

        double[] n = (double[])o.Clone();
        n[(int)F.Exposure] = o[(int)F.Exposure] + _exposure.Value;

        float bloom = _bloom.Value;
        n[(int)F.BloomOn] = bloom <= 0f ? 0 : o[(int)F.BloomOn];
        n[(int)F.Bloom] = o[(int)F.Bloom] * Math.Max(bloom, 0f);

        float blur = _blur.Value;
        if (blur <= 0f)
        {
            n[(int)F.BlurOn] = 0;
        }
        else if (!_blur.IsDefault)
        {
            n[(int)F.BlurOn] = 1;
            n[(int)F.Blur] = (o[(int)F.Blur] > 0.001 ? o[(int)F.Blur] : 1.0) * blur;
        }

        if (_grain.Value > 0f)
            n[(int)F.Grain] = _grain.Value;

        if (_focus.Value > 0f)
        {
            n[(int)F.DofOn] = 1;
            n[(int)F.Focus] = _focus.Value * 100.0;
        }

        if (!_dofBlur.IsDefault)
            n[(int)F.Aperture] = (o[(int)F.Aperture] > 0.001 ? o[(int)F.Aperture] : DefaultAperture) / _dofBlur.Value;

        float d = _fogDistance.Value;
        foreach (F f in new[] { F.D1Start, F.D1End, F.D2Start, F.D2End })
            n[(int)f] = o[(int)f] * d;

        double lift = _fogHeight.Value * 100.0;
        n[(int)F.H1Pos] = o[(int)F.H1Pos] + lift;
        n[(int)F.H2Pos] = o[(int)F.H2Pos] + lift;

        Write(ref st, n);
    }

    private static double[] Read(ref PPSettings st)
    {
        var a = new double[(int)F.Count];
        a[(int)F.Exposure] = st.ExposureBaseOffset;
        a[(int)F.BloomOn] = st.bEnableBloom ? 1 : 0;
        a[(int)F.Bloom] = st.BloomOverload;
        a[(int)F.BlurOn] = st.bEnableMotionBlur ? 1 : 0;
        a[(int)F.Blur] = st.MotionBlur_Amount;
        a[(int)F.Grain] = st.Scene_ImageGrainScale;
        a[(int)F.DofOn] = st.bEnableDOF ? 1 : 0;
        a[(int)F.Focus] = st.DOF_FocusDistance;
        a[(int)F.Aperture] = st.DOF_ApertureStop;
        a[(int)F.D1Start] = st.AtmosD1_DistanceStart;
        a[(int)F.D1End] = st.AtmosD1_DistanceEnd;
        a[(int)F.D2Start] = st.AtmosD2_DistanceStart;
        a[(int)F.D2End] = st.AtmosD2_DistanceEnd;
        a[(int)F.H1Pos] = st.AtmosH1_GradientPosition;
        a[(int)F.H2Pos] = st.AtmosH2_GradientPosition;
        return a;
    }

    private static void Write(ref PPSettings st, double[] a)
    {
        st.ExposureBaseOffset = (float)a[(int)F.Exposure];
        st.bEnableBloom = a[(int)F.BloomOn] > 0.5;
        st.BloomOverload = (float)a[(int)F.Bloom];
        st.bEnableMotionBlur = a[(int)F.BlurOn] > 0.5;
        st.MotionBlur_Amount = (float)a[(int)F.Blur];
        st.Scene_ImageGrainScale = (float)a[(int)F.Grain];
        st.bEnableDOF = a[(int)F.DofOn] > 0.5;
        st.DOF_FocusDistance = (float)a[(int)F.Focus];
        st.DOF_ApertureStop = (float)a[(int)F.Aperture];
        st.AtmosD1_DistanceStart = (float)a[(int)F.D1Start];
        st.AtmosD1_DistanceEnd = (float)a[(int)F.D1End];
        st.AtmosD2_DistanceStart = (float)a[(int)F.D2Start];
        st.AtmosD2_DistanceEnd = (float)a[(int)F.D2End];
        st.AtmosH1_GradientPosition = (float)a[(int)F.H1Pos];
        st.AtmosH2_GradientPosition = (float)a[(int)F.H2Pos];
    }
}
