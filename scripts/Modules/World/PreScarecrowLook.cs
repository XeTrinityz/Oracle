using System.Globalization;
using System.Numerics;
using BmSDK.Engine;
using FColor = BmSDK.GameObject.FColor;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using PPSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;

namespace CeZeY.Oracle;

/// <summary>
/// The look half of PRE-SCARECROW ERA: replaces the city's whole look (fog layers, sky gradient, haze, exposure,
/// bloom, tone and colour grading) with the Gordon section's exact values, written into every post process volume
/// and the world default. Each volume's own values are kept in memory and written back when the era goes off.
/// No menu row of its own; <see cref="PreScarecrowEra"/> switches it.
/// </summary>
internal sealed class PreScarecrowLook : Module
{
    public static readonly PreScarecrowLook I = new();

    private const string DefaultKey = "__WorldDefault__";
    private const double ScanSeconds = 1.0;

    // Chapter 2.3, playing as Gordon before the Scarecrow attack: CityZ_10_Ch2C PostProcessVolume_0, the chapter's
    // top-priority volume (about 8.5e24) and the look the camera saw there.
    private const string PreScarecrow = """
        bAtmosD1=true
        AtmosD1_Colour=(B=113,G=170,R=227,A=255)
        AtmosD1_Density=0
        AtmosD1_DistanceStart=500
        AtmosD1_DistanceEnd=10000
        bAtmosD2=true
        AtmosD2_Colour=(B=113,G=170,R=227,A=255)
        AtmosD2_Density=0
        AtmosD2_DistanceStart=500
        AtmosD2_DistanceEnd=10000
        bAtmosH1=true
        AtmosH1_Colour=(B=128,G=128,R=128,A=80)
        AtmosH1_Density=0.3
        AtmosH1_GradientSize=35000
        AtmosH1_GradientPosition=15000
        bAtmosH2=true
        AtmosH2_Colour=(B=178,G=186,R=197,A=2)
        AtmosH2_Density=0.05
        AtmosH2_GradientSize=8000
        AtmosH2_GradientPosition=50000
        AtmosGlobal_Gradient_Colour=(B=20,G=150,R=240,A=255)
        AtmosGlobal_Gradient_Direction=(-20,80,0)
        AtmosGlobal_Gradient_Density=1.5
        AtmosGlobal_Gradient_Cosine=0
        AtmosHazeWeight=0.05
        AtmosHazeNear=0
        AtmosHazeFar=800
        AtmosAmbientD1=(R=0,G=0,B=0,A=1)
        AtmosAmbientD2=(R=0,G=0,B=0,A=1)
        AtmosAmbientH1=(R=0.1,G=0.03,B=0,A=1)
        AtmosAmbientH2=(R=0,G=0,B=0,A=1)
        AtmosNoiseD1=0
        AtmosNoiseD2=0
        AtmosNoiseH1=0
        AtmosNoiseH2=0
        AtmosHeightMapModD1=(0,0,1)
        AtmosHeightMapModD2=(0,0,1)
        AtmosHeightMapModH1=(0,0,1)
        AtmosHeightMapModH2=(0,0,1)
        ExposureAutoBracketing=4
        ExposureBaseOffset=1
        BloomOverload=0.02
        BloomLowerCut=0.01
        Scene_Desaturation=0
        Scene_Colorize=(1.2,1.2,1)
        Scene_HighLights=(R=1.2,G=1.2,B=1,A=1)
        Scene_MidTones=(R=0.42,G=0.4,B=0.4,A=1)
        Scene_Shadows=(R=0,G=0,B=0,A=1)
        ColorGrading_LookupTable=Postprocess_Level.ColorGrading.CityY_Lut_02
        """;

    private const string LutField = "ColorGrading_LookupTable";

    private delegate string Getter(ref PPSettings s);
    private delegate void Setter(ref PPSettings s, string v);

    private sealed record Field(string Name, Getter Get, Setter Set);

    private static readonly Field[] Fields = BuildFields();
    private static readonly Dictionary<string, Field> ByName = Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string>? _look;
    private Texture? _lut;
    private bool _force;
    private Every _scan;

    // Per volume (by path), the game's own value of every field written there.
    private readonly Dictionary<string, Dictionary<string, string>> _originals = new();

    public override void WorldChanged()
    {
        // The volumes are about to go; their originals go with them.
        _originals.Clear();
        _lut = null;
        _force = true;
    }

    public override void Shutdown()
    {
        _look = null;
        Safe.Run("era look restore", ApplyAll);
    }

    public void Set(bool on)
    {
        if ((_look != null) == on)
            return;
        _look = on ? Parse(PreScarecrow) : null;
        _lut = null;
        _force = true;
    }

    public override void Tick(float dt)
    {
        if (!_force && !_scan.Elapsed(ScanSeconds))
            return;
        if (_look == null && _originals.Count == 0)
        {
            _force = false;
            return;
        }

        _force = false;
        ApplyAll();
    }

    private void ApplyAll()
    {
        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
            Apply(ref v.Settings, v.GetPathName(null));

        if (World.WorldInfo is { } wi)
            Apply(ref wi.DefaultPostProcessSettings, DefaultKey);

        if (_look == null)
            _originals.Clear();
    }

    private void Apply(ref PPSettings st, string key)
    {
        if (_look == null)
        {
            if (_originals.Remove(key, out Dictionary<string, string>? back))
                Write(ref st, back);
            return;
        }

        if (!_originals.TryGetValue(key, out Dictionary<string, string>? orig))
        {
            orig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in _look.Keys)
            {
                if (ByName.TryGetValue(name, out Field? f))
                    orig[name] = f.Get(ref st);
            }

            _originals[key] = orig;
        }

        Write(ref st, _look);
    }

    private void Write(ref PPSettings st, Dictionary<string, string> values)
    {
        foreach ((string name, string value) in values)
        {
            if (name.Equals(LutField, StringComparison.OrdinalIgnoreCase))
            {
                WriteLut(ref st, value);
                continue;
            }

            if (!ByName.TryGetValue(name, out Field? f))
                continue;

            try
            {
                if (f.Get(ref st) != Normalise(f, value))
                    f.Set(ref st, value);
            }
            catch (Exception e)
            {
                Log.Throttled("era look " + name, "PRE-SCARECROW ERA: couldn't set " + name + " to " + value + ": " + e.Message);
            }
        }
    }

    // Compare as the getter would print it, so an unchanged field isn't rewritten every second.
    private static string Normalise(Field f, string value)
    {
        var tmp = default(PPSettings);
        f.Set(ref tmp, value);
        return f.Get(ref tmp);
    }

    private void WriteLut(ref PPSettings st, string path)
    {
        Texture? want = path is "" or "None" ? null : _lut is { IsValid: true } known && known.GetPathName(null) == path ? known : FindLut(path);
        if (want == null && path is not ("" or "None"))
            return;

        Texture current = st.ColorGrading_LookupTable;
        nint now = current is { IsValid: true } ? current.Ptr : 0;
        if (now != (want?.Ptr ?? 0))
            st.ColorGrading_LookupTable = want!;
    }

    private Texture? FindLut(string path)
    {
        foreach (Texture t in World.All<Texture>())
        {
            if (t.GetPathName(null).Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                _lut = t;
                return t;
            }
        }

        Log.Throttled("era look lut " + path, "PRE-SCARECROW ERA: colour grading " + path + " isn't loaded here, so the look runs without it.");
        return null;
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string name = line.Substring(0, eq).Trim();
            if (ByName.ContainsKey(name) || name.Equals(LutField, StringComparison.OrdinalIgnoreCase))
                d[name] = line.Substring(eq + 1).Trim();
        }

        return d;
    }

    // ---- field table

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static float PF(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Parts(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in s.Trim('(', ')').Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
                d[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
        }

        return d;
    }

    private static string Color(FColor c) => $"(B={c.B},G={c.G},R={c.R},A={c.A})";

    private static FColor ParseColor(string s)
    {
        Dictionary<string, string> p = Parts(s);
        return new FColor { R = byte.Parse(p["R"]), G = byte.Parse(p["G"]), B = byte.Parse(p["B"]), A = byte.Parse(p["A"]) };
    }

    private static string Linear(FLinearColor c) => $"(R={F(c.R)},G={F(c.G)},B={F(c.B)},A={F(c.A)})";

    private static FLinearColor ParseLinear(string s)
    {
        Dictionary<string, string> p = Parts(s);
        return new FLinearColor { R = PF(p["R"]), G = PF(p["G"]), B = PF(p["B"]), A = PF(p["A"]) };
    }

    private static string Vec(Vector3 v) => $"({F(v.X)},{F(v.Y)},{F(v.Z)})";

    private static Vector3 ParseVec(string s)
    {
        string[] p = s.Trim('(', ')').Split(',');
        return new Vector3(PF(p[0]), PF(p[1]), PF(p[2]));
    }

    private delegate T Get<T>(ref PPSettings s);
    private delegate void Put<T>(ref PPSettings s, T v);

    private static Field Num(string n, Get<float> g, Put<float> p) => new(n, (ref PPSettings s) => F(g(ref s)), (ref PPSettings s, string v) => p(ref s, PF(v)));

    private static Field Flag(string n, Get<bool> g, Put<bool> p) =>
        new(n, (ref PPSettings s) => g(ref s) ? "true" : "false", (ref PPSettings s, string v) => p(ref s, v.Equals("true", StringComparison.OrdinalIgnoreCase)));

    private static Field Col(string n, Get<FColor> g, Put<FColor> p) => new(n, (ref PPSettings s) => Color(g(ref s)), (ref PPSettings s, string v) => p(ref s, ParseColor(v)));

    private static Field Lin(string n, Get<FLinearColor> g, Put<FLinearColor> p) => new(n, (ref PPSettings s) => Linear(g(ref s)), (ref PPSettings s, string v) => p(ref s, ParseLinear(v)));

    private static Field V3(string n, Get<Vector3> g, Put<Vector3> p) => new(n, (ref PPSettings s) => Vec(g(ref s)), (ref PPSettings s, string v) => p(ref s, ParseVec(v)));

    private static Field[] BuildFields() => new[]
    {
        Flag("bAtmosD1", (ref PPSettings s) => s.bAtmosD1, (ref PPSettings s, bool v) => s.bAtmosD1 = v),
        Col("AtmosD1_Colour", (ref PPSettings s) => s.AtmosD1_Colour, (ref PPSettings s, FColor v) => s.AtmosD1_Colour = v),
        Num("AtmosD1_Density", (ref PPSettings s) => s.AtmosD1_Density, (ref PPSettings s, float v) => s.AtmosD1_Density = v),
        Num("AtmosD1_DistanceStart", (ref PPSettings s) => s.AtmosD1_DistanceStart, (ref PPSettings s, float v) => s.AtmosD1_DistanceStart = v),
        Num("AtmosD1_DistanceEnd", (ref PPSettings s) => s.AtmosD1_DistanceEnd, (ref PPSettings s, float v) => s.AtmosD1_DistanceEnd = v),
        Flag("bAtmosD2", (ref PPSettings s) => s.bAtmosD2, (ref PPSettings s, bool v) => s.bAtmosD2 = v),
        Col("AtmosD2_Colour", (ref PPSettings s) => s.AtmosD2_Colour, (ref PPSettings s, FColor v) => s.AtmosD2_Colour = v),
        Num("AtmosD2_Density", (ref PPSettings s) => s.AtmosD2_Density, (ref PPSettings s, float v) => s.AtmosD2_Density = v),
        Num("AtmosD2_DistanceStart", (ref PPSettings s) => s.AtmosD2_DistanceStart, (ref PPSettings s, float v) => s.AtmosD2_DistanceStart = v),
        Num("AtmosD2_DistanceEnd", (ref PPSettings s) => s.AtmosD2_DistanceEnd, (ref PPSettings s, float v) => s.AtmosD2_DistanceEnd = v),
        Flag("bAtmosH1", (ref PPSettings s) => s.bAtmosH1, (ref PPSettings s, bool v) => s.bAtmosH1 = v),
        Col("AtmosH1_Colour", (ref PPSettings s) => s.AtmosH1_Colour, (ref PPSettings s, FColor v) => s.AtmosH1_Colour = v),
        Num("AtmosH1_Density", (ref PPSettings s) => s.AtmosH1_Density, (ref PPSettings s, float v) => s.AtmosH1_Density = v),
        Num("AtmosH1_GradientSize", (ref PPSettings s) => s.AtmosH1_GradientSize, (ref PPSettings s, float v) => s.AtmosH1_GradientSize = v),
        Num("AtmosH1_GradientPosition", (ref PPSettings s) => s.AtmosH1_GradientPosition, (ref PPSettings s, float v) => s.AtmosH1_GradientPosition = v),
        Flag("bAtmosH2", (ref PPSettings s) => s.bAtmosH2, (ref PPSettings s, bool v) => s.bAtmosH2 = v),
        Col("AtmosH2_Colour", (ref PPSettings s) => s.AtmosH2_Colour, (ref PPSettings s, FColor v) => s.AtmosH2_Colour = v),
        Num("AtmosH2_Density", (ref PPSettings s) => s.AtmosH2_Density, (ref PPSettings s, float v) => s.AtmosH2_Density = v),
        Num("AtmosH2_GradientSize", (ref PPSettings s) => s.AtmosH2_GradientSize, (ref PPSettings s, float v) => s.AtmosH2_GradientSize = v),
        Num("AtmosH2_GradientPosition", (ref PPSettings s) => s.AtmosH2_GradientPosition, (ref PPSettings s, float v) => s.AtmosH2_GradientPosition = v),
        Col("AtmosGlobal_Gradient_Colour", (ref PPSettings s) => s.AtmosGlobal_Gradient_Colour, (ref PPSettings s, FColor v) => s.AtmosGlobal_Gradient_Colour = v),
        V3("AtmosGlobal_Gradient_Direction", (ref PPSettings s) => s.AtmosGlobal_Gradient_Direction, (ref PPSettings s, Vector3 v) => s.AtmosGlobal_Gradient_Direction = v),
        Num("AtmosGlobal_Gradient_Density", (ref PPSettings s) => s.AtmosGlobal_Gradient_Density, (ref PPSettings s, float v) => s.AtmosGlobal_Gradient_Density = v),
        Num("AtmosGlobal_Gradient_Cosine", (ref PPSettings s) => s.AtmosGlobal_Gradient_Cosine, (ref PPSettings s, float v) => s.AtmosGlobal_Gradient_Cosine = v),
        Num("AtmosHazeWeight", (ref PPSettings s) => s.AtmosHazeWeight, (ref PPSettings s, float v) => s.AtmosHazeWeight = v),
        Num("AtmosHazeNear", (ref PPSettings s) => s.AtmosHazeNear, (ref PPSettings s, float v) => s.AtmosHazeNear = v),
        Num("AtmosHazeFar", (ref PPSettings s) => s.AtmosHazeFar, (ref PPSettings s, float v) => s.AtmosHazeFar = v),
        Lin("AtmosAmbientD1", (ref PPSettings s) => s.AtmosAmbientD1, (ref PPSettings s, FLinearColor v) => s.AtmosAmbientD1 = v),
        Lin("AtmosAmbientD2", (ref PPSettings s) => s.AtmosAmbientD2, (ref PPSettings s, FLinearColor v) => s.AtmosAmbientD2 = v),
        Lin("AtmosAmbientH1", (ref PPSettings s) => s.AtmosAmbientH1, (ref PPSettings s, FLinearColor v) => s.AtmosAmbientH1 = v),
        Lin("AtmosAmbientH2", (ref PPSettings s) => s.AtmosAmbientH2, (ref PPSettings s, FLinearColor v) => s.AtmosAmbientH2 = v),
        Num("AtmosNoiseD1", (ref PPSettings s) => s.AtmosNoiseD1, (ref PPSettings s, float v) => s.AtmosNoiseD1 = v),
        Num("AtmosNoiseD2", (ref PPSettings s) => s.AtmosNoiseD2, (ref PPSettings s, float v) => s.AtmosNoiseD2 = v),
        Num("AtmosNoiseH1", (ref PPSettings s) => s.AtmosNoiseH1, (ref PPSettings s, float v) => s.AtmosNoiseH1 = v),
        Num("AtmosNoiseH2", (ref PPSettings s) => s.AtmosNoiseH2, (ref PPSettings s, float v) => s.AtmosNoiseH2 = v),
        V3("AtmosHeightMapModD1", (ref PPSettings s) => s.AtmosHeightMapModD1, (ref PPSettings s, Vector3 v) => s.AtmosHeightMapModD1 = v),
        V3("AtmosHeightMapModD2", (ref PPSettings s) => s.AtmosHeightMapModD2, (ref PPSettings s, Vector3 v) => s.AtmosHeightMapModD2 = v),
        V3("AtmosHeightMapModH1", (ref PPSettings s) => s.AtmosHeightMapModH1, (ref PPSettings s, Vector3 v) => s.AtmosHeightMapModH1 = v),
        V3("AtmosHeightMapModH2", (ref PPSettings s) => s.AtmosHeightMapModH2, (ref PPSettings s, Vector3 v) => s.AtmosHeightMapModH2 = v),
        Num("ExposureAutoBracketing", (ref PPSettings s) => s.ExposureAutoBracketing, (ref PPSettings s, float v) => s.ExposureAutoBracketing = v),
        Num("ExposureBaseOffset", (ref PPSettings s) => s.ExposureBaseOffset, (ref PPSettings s, float v) => s.ExposureBaseOffset = v),
        Num("BloomOverload", (ref PPSettings s) => s.BloomOverload, (ref PPSettings s, float v) => s.BloomOverload = v),
        Num("BloomLowerCut", (ref PPSettings s) => s.BloomLowerCut, (ref PPSettings s, float v) => s.BloomLowerCut = v),
        Num("Scene_Desaturation", (ref PPSettings s) => s.Scene_Desaturation, (ref PPSettings s, float v) => s.Scene_Desaturation = v),
        V3("Scene_Colorize", (ref PPSettings s) => s.Scene_Colorize, (ref PPSettings s, Vector3 v) => s.Scene_Colorize = v),
        Lin("Scene_HighLights", (ref PPSettings s) => s.Scene_HighLights, (ref PPSettings s, FLinearColor v) => s.Scene_HighLights = v),
        Lin("Scene_MidTones", (ref PPSettings s) => s.Scene_MidTones, (ref PPSettings s, FLinearColor v) => s.Scene_MidTones = v),
        Lin("Scene_Shadows", (ref PPSettings s) => s.Scene_Shadows, (ref PPSettings s, FLinearColor v) => s.Scene_Shadows = v),
    };
}
