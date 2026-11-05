using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using FColor = BmSDK.GameObject.FColor;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using PPSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;

namespace CeZeY.Oracle;

/// <summary>Recolours the world by rewriting post process settings, blending between looks over a fraction of a second.</summary>
internal sealed class Atmosphere : Module
{
    public static readonly Atmosphere I = new();

    private const string OriginalsKey = "PostProcessFields";
    private const string DefaultSettingsKey = "__WorldDefault__";
    private const double ScanSeconds = 1.0;
    private const float BlendRate = 5f;
    private const int Custom = 7;
    private const float CustomSaturation = 0.85f;

    private readonly record struct Look(float R, float G, float B, float Strength, float Fog, float Desat)
    {
        public static readonly Look None = new(1f, 1f, 1f, 0f, 1f, 0f);

        public bool IsNone => Strength < 0.001f && MathF.Abs(Fog - 1f) < 0.001f && Desat < 0.001f;

        public static Look Lerp(Look a, Look b, float t) => new(
            a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t,
            a.Strength + (b.Strength - a.Strength) * t, a.Fog + (b.Fog - a.Fog) * t, a.Desat + (b.Desat - a.Desat) * t);

        public float Distance(Look o) => Math.Max(Math.Max(MathF.Abs(R - o.R), Math.Max(MathF.Abs(G - o.G), MathF.Abs(B - o.B))),
            Math.Max(MathF.Abs(Strength - o.Strength), Math.Max(MathF.Abs(Fog - o.Fog), MathF.Abs(Desat - o.Desat))));
    }

    private sealed record Preset(string Key, string Label, float R, float G, float B, float Desat);

    private static readonly Preset[] Presets =
    {
        new("vanilla", "VANILLA", 1f, 1f, 1f, 0f),
        new("blood_moon", "BLOOD MOON", 1f, 0.1f, 0.06f, 0f),
        new("fear_toxin", "FEAR TOXIN", 0.3f, 1f, 0.25f, 0f),
        new("arctic", "ARCTIC", 0.45f, 0.85f, 1f, 0.3f),
        new("golden_hour", "GOLDEN HOUR", 1f, 0.5f, 0.06f, 0f),
        new("joker", "JOKER", 0.65f, 0.25f, 1f, 0f),
        new("noir", "NOIR", 1f, 1f, 1f, 0.9f),
        new("custom", "CUSTOM", 1f, 1f, 1f, 0f),
    };

    private struct Fields
    {
        public FColor Gradient, H1, H2, D1, D2;
        public FLinearColor High, Mid, Shadow;
        public float Desat, H1Den, H2Den, D1Den, D2Den, Haze;
        public nint Lut;
    }

    private readonly List<(PostProcessVolume Vol, string Key)> _volumes = new();
    private readonly HashSet<string> _touched = new();
    private readonly Dictionary<nint, Texture> _luts = new();
    private Choice _choice = null!;
    private Slider _hue = null!;
    private Slider _strength = null!;
    private Slider _fog = null!;
    private Slider _desat = null!;
    private Look _now = Look.None;
    private Look _applied = Look.None;
    private Every _scan;
    private bool _force;
    private bool _gradeOff;

    private static Dictionary<string, double[]> Originals => Shared.Keep(OriginalsKey, () => new Dictionary<string, double[]>());

    public bool Wanted => !Target().IsNone;

    public override void Build()
    {
        _choice = AddChoice("atmosphere", "SKY & FOG LOOK", "Recolours the sky, fog and lighting. CUSTOM uses the hue below.",
            Presets.Select(p => p.Key).ToArray(), Presets.Select(p => p.Label).ToArray(), _ => _force = true);
        _choice.Swatch = Swatch;
        _hue = AddSlider("atmo_hue", "CUSTOM HUE", "Colour for the CUSTOM look. Moving it switches the look to CUSTOM.", 200f, 0f, 360f, 5f, 0f,
            Fmt.Degrees, _ => PickCustom());
        _strength = AddSlider("atmo_strength", "LOOK STRENGTH", "How strongly the chosen look tints the world.", 0.6f, 0.1f, 1f, 0.05f, 1f, Fmt.Percent, _ => _force = true);
        _fog = AddSlider("atmo_fog", "FOG DENSITY", "Thins out or thickens the city's fog and haze. Works with any look.", 1f, 0f, 3f, 0.1f, 2f, Fmt.Times, _ => _force = true);
        _desat = AddSlider("atmo_desat", "DESATURATION", "Drains colour from the whole picture. Works with any look.", 0f, 0f, 1f, 0.05f, 0.6f, Fmt.Percent, _ => _force = true);
    }

    public override void WorldChanged()
    {
        _volumes.Clear();
        _touched.Clear();
        _force = true;
        _scan.Force();
    }

    public override void Shutdown()
    {
        Safe.Run("atmosphere restore", () =>
        {
            _gradeOff = false;
            _now = Look.None;
            ScanVolumes();
            WriteAll(changed: true);
        });
    }

    public void SetGradeOff(bool off)
    {
        if (_gradeOff == off)
            return;

        _gradeOff = off;
        _force = true;
    }

    public override void Tick(float dt)
    {
        Look target = Target();
        float t = dt <= 0f ? 1f : Ease.Follow(BlendRate, dt);
        _now = Look.Lerp(_now, target, t);
        if (_now.Distance(target) < 0.002f)
            _now = target;

        bool rescan = _scan.Elapsed(ScanSeconds);
        bool changed = _force || _now.Distance(_applied) > 0.0005f;
        if (!changed && !rescan)
            return;

        if (_now.IsNone && _touched.Count == 0 && Originals.Count == 0 && !_force)
        {
            _applied = _now;
            return;
        }

        if (rescan || _volumes.Count == 0)
            ScanVolumes();

        _force = false;
        _applied = _now;
        WriteAll(changed);
    }

    private void WriteAll(bool changed)
    {
        Dictionary<string, double[]> originals = Originals;
        foreach ((PostProcessVolume vol, string key) in _volumes)
        {
            if (!vol.IsValid || (!changed && _touched.Contains(key)))
                continue;
            ApplyLook(ref vol.Settings, key, originals);
        }

        if (World.WorldInfo is { } wi && (changed || !_touched.Contains(DefaultSettingsKey)))
            ApplyLook(ref wi.DefaultPostProcessSettings, DefaultSettingsKey, originals);
    }

    private void PickCustom()
    {
        _force = true;
        if (_choice.Index == Custom)
            return;

        _choice.Index = Custom;
        _choice.Saved = _choice.CurrentKey;
        Settings.MarkDirty();
    }

    private Look Target()
    {
        float fog = _fog.Value;
        float desat = _desat.Value;
        int pick = _choice.Index;
        if (pick == 0)
            return new Look(1f, 1f, 1f, 0f, fog, desat);

        float k = _strength.Value;
        Preset preset = Presets[pick];
        (float r, float g, float b) = pick == Custom ? Rgba.Hue(_hue.Value, CustomSaturation) : (preset.R, preset.G, preset.B);
        return new Look(r, g, b, k, fog, Math.Clamp(desat + preset.Desat * k, 0f, 1f));
    }

    private Rgba Swatch(int pick)
    {
        if (pick == Custom)
        {
            var c = Rgba.Hue(_hue.Value, CustomSaturation);
            return Rgba.From(c.R, c.G, c.B);
        }

        Preset p = Presets[Math.Clamp(pick, 0, Presets.Length - 1)];
        return pick == 0 ? Rgba.From(0.45f, 0.62f, 0.85f) : Rgba.From(p.R, p.G, p.B);
    }

    private void ScanVolumes()
    {
        _volumes.Clear();
        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
            _volumes.Add((v, v.GetPathName(null)));

        if (_touched.Count > 4 * _volumes.Count + 64)
            _touched.Clear();
    }

    private void ApplyLook(ref PPSettings st, string key, Dictionary<string, double[]> originals)
    {
        if (_now.IsNone && !_gradeOff)
        {
            if (originals.TryGetValue(key, out double[]? back))
            {
                Write(ref st, Unpack(back));
                originals.Remove(key);
            }

            _touched.Remove(key);
            return;
        }

        if (!originals.TryGetValue(key, out double[]? packed))
        {
            packed = Pack(Read(ref st));
            originals[key] = packed;
        }

        Fields o = Unpack(packed);
        if (_gradeOff)
        {
            o.Lut = 0;
            o.High = new FLinearColor { R = 1f, G = 1f, B = 1f, A = 1f };
            o.Mid = new FLinearColor { R = 0.5f, G = 0.5f, B = 0.5f, A = 1f };
            o.Shadow = new FLinearColor { R = 0f, G = 0f, B = 0f, A = 1f };
            o.Desat = 0f;
        }

        Look l = _now;
        float k = l.Strength;
        float avg = Math.Max(0.05f, (l.R + l.G + l.B) / 3f);

        Fields n = o;
        n.Gradient = Tint(o.Gradient, l, k);
        n.H1 = Tint(o.H1, l, k);
        n.H2 = Tint(o.H2, l, k);
        n.D1 = Tint(o.D1, l, k);
        n.D2 = Tint(o.D2, l, k);
        n.High = Gain(Neutral(o.High, k), l, k * 0.5f, avg);
        n.Mid = Gain(o.Mid, l, k * 0.2f, avg);
        n.Shadow = new FLinearColor { R = o.Shadow.R + l.R * k * 0.025f, G = o.Shadow.G + l.G * k * 0.025f, B = o.Shadow.B + l.B * k * 0.025f, A = o.Shadow.A };
        n.Desat = Math.Clamp(o.Desat + l.Desat, 0f, 1f);
        n.H1Den = o.H1Den * l.Fog;
        n.H2Den = o.H2Den * l.Fog;
        n.D1Den = o.D1Den * l.Fog;
        n.D2Den = o.D2Den * l.Fog;
        n.Haze = Math.Clamp(o.Haze * l.Fog, 0f, 1f);
        Write(ref st, n);
        _touched.Add(key);
    }

    private static FColor Tint(FColor o, Look l, float k)
    {
        float m = Math.Max(o.R, Math.Max(o.G, o.B));
        float top = Math.Max(l.R, Math.Max(l.G, l.B));
        float s = top <= 0f ? 0f : m / top;
        static byte Ch(float from, float to, float k) => (byte)Math.Clamp(MathF.Round(from + (to - from) * k), 0f, 255f);
        return new FColor { R = Ch(o.R, l.R * s, k), G = Ch(o.G, l.G * s, k), B = Ch(o.B, l.B * s, k), A = o.A };
    }

    private static FLinearColor Neutral(FLinearColor o, float k)
    {
        float m = (o.R + o.G + o.B) / 3f;
        return new FLinearColor { R = o.R + (m - o.R) * k, G = o.G + (m - o.G) * k, B = o.B + (m - o.B) * k, A = o.A };
    }

    private static FLinearColor Gain(FLinearColor o, Look l, float amount, float avg)
    {
        float G(float c) => Math.Clamp(1f + amount * (c / avg - 1f), 0.35f, 2.2f);
        return new FLinearColor { R = o.R * G(l.R), G = o.G * G(l.G), B = o.B * G(l.B), A = o.A };
    }

    private Fields Read(ref PPSettings st)
    {
        Texture lut = st.ColorGrading_LookupTable;
        return new Fields
        {
            Gradient = st.AtmosGlobal_Gradient_Colour, H1 = st.AtmosH1_Colour, H2 = st.AtmosH2_Colour, D1 = st.AtmosD1_Colour, D2 = st.AtmosD2_Colour,
            High = st.Scene_HighLights, Mid = st.Scene_MidTones, Shadow = st.Scene_Shadows,
            Desat = st.Scene_Desaturation, H1Den = st.AtmosH1_Density, H2Den = st.AtmosH2_Density, D1Den = st.AtmosD1_Density, D2Den = st.AtmosD2_Density,
            Haze = st.AtmosHazeWeight, Lut = Remember(lut),
        };
    }

    private void Write(ref PPSettings st, Fields f)
    {
        st.AtmosGlobal_Gradient_Colour = f.Gradient;
        st.AtmosH1_Colour = f.H1;
        st.AtmosH2_Colour = f.H2;
        st.AtmosD1_Colour = f.D1;
        st.AtmosD2_Colour = f.D2;
        st.Scene_HighLights = f.High;
        st.Scene_MidTones = f.Mid;
        st.Scene_Shadows = f.Shadow;
        st.Scene_Desaturation = f.Desat;
        st.AtmosH1_Density = f.H1Den;
        st.AtmosH2_Density = f.H2Den;
        st.AtmosD1_Density = f.D1Den;
        st.AtmosD2_Density = f.D2Den;
        st.AtmosHazeWeight = f.Haze;

        Texture current = st.ColorGrading_LookupTable;
        nint now = current is { IsValid: true } ? current.Ptr : 0;
        if (now == f.Lut)
            return;

        Texture? lut = f.Lut == 0 ? null : LutFor(f.Lut);
        if (f.Lut == 0 || lut != null)
            st.ColorGrading_LookupTable = lut!;
    }

    private nint Remember(Texture? lut)
    {
        if (lut is not { IsValid: true })
            return 0;

        _luts[lut.Ptr] = lut;
        return lut.Ptr;
    }

    private Texture? LutFor(nint ptr)
    {
        if (_luts.TryGetValue(ptr, out Texture? known) && known.IsValid)
            return known;

        foreach (Texture tex in World.All<Texture>())
        {
            if (tex.Ptr == ptr)
            {
                _luts[ptr] = tex;
                return tex;
            }
        }

        return null;
    }

    // Stored as plain doubles because originals outlive a hot reload, which would orphan any type of ours.
    private static double PackColor(FColor c) => (uint)(c.R | (c.G << 8) | (c.B << 16)) | ((uint)c.A << 24);

    private static FColor UnpackColor(double d)
    {
        long v = (long)d;
        return new FColor { R = (byte)v, G = (byte)(v >> 8), B = (byte)(v >> 16), A = (byte)(v >> 24) };
    }

    private static double[] Pack(Fields f) => new double[]
    {
        PackColor(f.Gradient), PackColor(f.H1), PackColor(f.H2), PackColor(f.D1), PackColor(f.D2),
        f.High.R, f.High.G, f.High.B, f.High.A, f.Mid.R, f.Mid.G, f.Mid.B, f.Mid.A, f.Shadow.R, f.Shadow.G, f.Shadow.B, f.Shadow.A,
        f.Desat, f.H1Den, f.H2Den, f.D1Den, f.D2Den, f.Haze, f.Lut,
    };

    private static Fields Unpack(double[] a) => new()
    {
        Gradient = UnpackColor(a[0]), H1 = UnpackColor(a[1]), H2 = UnpackColor(a[2]), D1 = UnpackColor(a[3]), D2 = UnpackColor(a[4]),
        High = new FLinearColor { R = (float)a[5], G = (float)a[6], B = (float)a[7], A = (float)a[8] },
        Mid = new FLinearColor { R = (float)a[9], G = (float)a[10], B = (float)a[11], A = (float)a[12] },
        Shadow = new FLinearColor { R = (float)a[13], G = (float)a[14], B = (float)a[15], A = (float)a[16] },
        Desat = (float)a[17], H1Den = (float)a[18], H2Den = (float)a[19], D1Den = (float)a[20], D2Den = (float)a[21], Haze = (float)a[22],
        Lut = (nint)(long)a[23],
    };
}
