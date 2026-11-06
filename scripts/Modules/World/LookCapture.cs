using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using PPSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD > ATMOSPHERE > CAPTURE LOOK: writes everything that makes up the current look to a text file in
/// Oracle's looks folder: the blended post process the camera is seeing (and the volume it came from), the
/// world default, the volumes around the camera, the sun, moon and sky lights, height fog, and the sky meshes'
/// material parameters. Read-only; the captures are what look presets are built from.
/// </summary>
internal sealed class LookCapture : Module
{
    public static readonly LookCapture I = new();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] SkyWords = { "sky", "moon", "cloud", "star", "horizon", "atmos" };

    public override void Build()
    {
        AddCommand("look_capture", "CAPTURE LOOK",
            "Saves everything that makes up the look you're seeing right now (fog, colour grading, sun, moon, sky) to a file in Oracle's looks folder, so it can be turned into a look preset.",
            Capture);
    }

    private static void Capture()
    {
        RPlayerController pc = World.Pc ?? throw new InvalidOperationException("the game is still loading");
        pc.GetPlayerViewPoint(out Vector3 eye, out Rotator _);

        var sb = new StringBuilder();
        sb.AppendLine("[info]");
        sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
        sb.AppendLine("map=" + Read(() => pc.GetPMapName().ToString() ?? "", "?"));
        sb.AppendLine("chapter=" + Read(() => World.Info is { } gi ? gi.GetChapterNumber() + "." + gi.GetSubChapterNumber() : "?", "?"));
        sb.AppendLine("camera=" + Value(eye));

        int sections = 0;
        if (pc.Player is LocalPlayer lp && lp.IsValid)
        {
            PostProcessVolume used = lp.CurrentPPInfo.LastVolumeUsed;
            sb.AppendLine("volume_used=" + Value(used));
            Settings(sb, "blended", lp.CurrentPPInfo.LastSettings);
            sections++;
        }

        if (World.WorldInfo is { } wi)
        {
            Settings(sb, "world_default", wi.DefaultPostProcessSettings);
            sections++;
        }

        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
        {
            if (!Safe.Try("volume check", () => v.EncompassesPoint(eye)))
                continue;

            sb.AppendLine();
            sb.AppendLine("[volume " + v.GetPathName(null) + "]");
            sb.AppendLine("enabled=" + Value(v.bEnabled));
            sb.AppendLine("priority=" + Value(v.Priority));
            Fields(sb, v.Settings);
            sections++;
        }

        foreach (LightComponent l in World.All<LightComponent>())
        {
            if (l is not (DirectionalLightComponent or SkyLightComponent))
                continue;

            sb.AppendLine();
            sb.AppendLine("[light " + l.GetPathName(null) + "]");
            sb.AppendLine("class=" + l.GetType().Name);
            sb.AppendLine("owner=" + Value(l.Owner));
            if (l.Owner is { IsValid: true } owner)
                sb.AppendLine("owner_rotation=" + Value(owner.Rotation));
            sb.AppendLine("enabled=" + Value(l.bEnabled));
            sb.AppendLine("brightness=" + Value(l.Brightness));
            sb.AppendLine("color=" + Value(l.LightColor));
            sb.AppendLine("bloom_scale=" + Value(l.BloomScale));
            sb.AppendLine("light_shafts=" + Value(l.bRenderLightShafts));
            sb.AppendLine("cast_dynamic_shadows=" + Value(l.CastDynamicShadows));
            if (l is SkyLightComponent sky)
            {
                sb.AppendLine("lower_brightness=" + Value(sky.LowerBrightness));
                sb.AppendLine("lower_color=" + Value(sky.LowerColor));
            }

            sections++;
        }

        foreach (ExponentialHeightFogComponent f in World.All<ExponentialHeightFogComponent>())
        {
            sb.AppendLine();
            sb.AppendLine("[exp_fog " + f.GetPathName(null) + "]");
            sb.AppendLine($"enabled={Value(f.bEnabled)}\nheight={Value(f.FogHeight)}\ndensity={Value(f.FogDensity)}\nfalloff={Value(f.FogHeightFalloff)}\nmax_opacity={Value(f.FogMaxOpacity)}");
            sb.AppendLine($"start={Value(f.StartDistance)}\ninscatter_brightness={Value(f.LightInscatteringBrightness)}\ninscatter_color={Value(f.LightInscatteringColor)}");
            sb.AppendLine($"opposite_brightness={Value(f.OppositeLightBrightness)}\nopposite_color={Value(f.OppositeLightColor)}");
            sections++;
        }

        foreach (HeightFogComponent f in World.All<HeightFogComponent>())
        {
            sb.AppendLine();
            sb.AppendLine("[height_fog " + f.GetPathName(null) + "]");
            sb.AppendLine($"enabled={Value(f.bEnabled)}\nheight={Value(f.Height)}\ndensity={Value(f.Density)}\nlight_brightness={Value(f.LightBrightness)}");
            sb.AppendLine($"light_color={Value(f.LightColor)}\nextinction={Value(f.ExtinctionDistance)}\nstart={Value(f.StartDistance)}");
            sections++;
        }

        sections += SkyMeshes(sb);
        sections += BigMeshes(sb, eye);
        sections += ChapterMeshes(sb);

        string dir = Path.Combine(App.Dir, "looks");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".txt");
        File.WriteAllText(path, sb.ToString());

        Log.Info("CAPTURE LOOK: " + sections + " sections written to " + path);
        Overlay.Notice("LOOK CAPTURED", Path.GetFileName(path).ToUpperInvariant(), true);
    }

    private static T Read<T>(Func<T> get, T fallback)
    {
        try
        {
            return get();
        }
        catch
        {
            return fallback;
        }
    }

    private static int SkyMeshes(StringBuilder sb)
    {
        int n = 0;
        foreach (StaticMeshComponent c in World.All<StaticMeshComponent>())
        {
            StaticMesh mesh = c.StaticMesh;
            if (mesh is not { IsValid: true })
                continue;

            string meshPath = mesh.GetPathName(null);
            if (!SkyWords.Any(w => meshPath.Contains(w, StringComparison.OrdinalIgnoreCase)))
                continue;

            sb.AppendLine();
            sb.AppendLine("[sky_mesh " + c.GetPathName(null) + "]");
            sb.AppendLine("mesh=" + meshPath);
            sb.AppendLine("hidden=" + Value(c.HiddenGame));
            if (c.Owner is { IsValid: true } owner)
                sb.AppendLine("owner_rotation=" + Value(owner.Rotation));

            int count = Read(() => c.GetNumElements(), 0);
            for (int i = 0; i < count; i++)
            {
                MaterialInterface m = c.GetMaterial(i);
                sb.AppendLine($"material{i}=" + Value(m));
                if (m is MaterialInstanceConstant mic && mic.IsValid)
                    Parameters(sb, $"material{i}", mic);
            }

            n++;
        }

        return n;
    }

    // Sky domes and cloud layers have no telling names, but they're the largest meshes in the world by far.
    private static int BigMeshes(StringBuilder sb, Vector3 eye)
    {
        var all = new List<(MeshComponent Comp, float Radius)>();
        foreach (MeshComponent c in World.All<MeshComponent>())
        {
            float r = Read(() => c.Bounds.SphereRadius, 0f);
            if (r > 20000f)
                all.Add((c, r));
        }

        int n = 0;
        foreach ((MeshComponent c, float r) in all.OrderByDescending(x => x.Radius).Take(25))
        {
            sb.AppendLine();
            sb.AppendLine("[big_mesh " + c.GetPathName(null) + "]");
            sb.AppendLine("class=" + c.GetType().Name);
            if (c is StaticMeshComponent sm)
                sb.AppendLine("mesh=" + Value(sm.StaticMesh));
            sb.AppendLine("radius=" + Value(r));
            sb.AppendLine("distance=" + Value(Vector3.Distance(eye, c.Bounds.Origin)));
            sb.AppendLine("hidden=" + Value(c.HiddenGame));
            Materials(sb, c, 3);
            n++;
        }

        return n;
    }

    // Map pieces that exist only for one stretch of the story (names like CityZ_10_Ch2C) carry what makes it look different.
    private static int ChapterMeshes(StringBuilder sb)
    {
        var byLevel = new Dictionary<string, Dictionary<string, int>>();
        foreach (MeshComponent c in World.All<MeshComponent>())
        {
            string path = c.GetPathName(null);
            int dot = path.IndexOf('.');
            if (dot <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(path.Substring(0, dot), @"_Ch\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            string level = path.Substring(0, dot);
            string what = c is StaticMeshComponent sm ? Value(sm.StaticMesh) : c.GetType().Name;
            MaterialInterface m = Read(() => c.GetMaterial(0), null!);
            what += " | " + Value(m);

            if (!byLevel.TryGetValue(level, out Dictionary<string, int>? counts))
                byLevel[level] = counts = new Dictionary<string, int>();
            counts[what] = counts.GetValueOrDefault(what) + 1;
        }

        foreach ((string level, Dictionary<string, int> counts) in byLevel)
        {
            sb.AppendLine();
            sb.AppendLine("[chapter_level " + level + "]");
            foreach ((string what, int count) in counts.OrderByDescending(p => p.Value).Take(60))
                sb.AppendLine(count + "x " + what);
        }

        return byLevel.Count;
    }

    private static void Materials(StringBuilder sb, MeshComponent c, int max)
    {
        int count = Math.Min(max, Read(() => c.GetNumElements(), 0));
        for (int i = 0; i < count; i++)
        {
            MaterialInterface m = c.GetMaterial(i);
            sb.AppendLine($"material{i}=" + Value(m));
            if (m is MaterialInstanceConstant mic && mic.IsValid)
                Parameters(sb, $"material{i}", mic);
        }
    }

    private static void Parameters(StringBuilder sb, string prefix, MaterialInstanceConstant mic)
    {
        sb.AppendLine(prefix + ".parent=" + Value(mic.Parent));

        TArray<MaterialInstanceConstant.FScalarParameterValue> scalars = mic.ScalarParameterValues;
        for (int i = 0; i < scalars.Count; i++)
            sb.AppendLine($"{prefix}.scalar.{scalars[i].ParameterName}={Value(scalars[i].ParameterValue)}");

        TArray<MaterialInstanceConstant.FVectorParameterValue> vectors = mic.VectorParameterValues;
        for (int i = 0; i < vectors.Count; i++)
            sb.AppendLine($"{prefix}.vector.{vectors[i].ParameterName}={Value(vectors[i].ParameterValue)}");

        TArray<MaterialInstanceConstant.FTextureParameterValue> textures = mic.TextureParameterValues;
        for (int i = 0; i < textures.Count; i++)
            sb.AppendLine($"{prefix}.texture.{textures[i].ParameterName}={Value(textures[i].ParameterValue)}");
    }

    private static void Settings(StringBuilder sb, string name, PPSettings st)
    {
        sb.AppendLine();
        sb.AppendLine("[" + name + "]");
        Fields(sb, st);
    }

    // Every plain field of the settings struct, by reflection, so nothing that shapes the look is missed.
    private static void Fields(StringBuilder sb, PPSettings st)
    {
        object boxed = st;
        foreach (PropertyInfo p in typeof(PPSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Type t = p.PropertyType;
            if (t.IsByRef || t == typeof(nint) || !p.CanRead || p.GetIndexParameters().Length > 0 || t.Name.StartsWith("TArray") || t.Name == "FLUTBlender")
                continue;

            string text;
            try
            {
                text = Value(p.GetValue(boxed));
            }
            catch (Exception e)
            {
                text = "<" + e.GetType().Name + ">";
            }

            sb.AppendLine(p.Name + "=" + text);
        }
    }

    internal static string Value(object? v) => v switch
    {
        null => "None",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.######", Inv),
        double d => d.ToString("0.######", Inv),
        int i => i.ToString(Inv),
        byte by => by.ToString(Inv),
        Vector3 v3 => $"({v3.X.ToString("0.###", Inv)},{v3.Y.ToString("0.###", Inv)},{v3.Z.ToString("0.###", Inv)})",
        GameObject g => g.IsValid ? g.GetPathName(null) : "None",
        System.Enum e => e.ToString(),
        _ when v.GetType().IsValueType => Members(v),
        _ => v.ToString() ?? "",
    };

    // Small structs (colours, rotators, names) as (Field=value, ...).
    internal static string Members(object v)
    {
        Type t = v.GetType();
        if (t.Name == "FName")
            return v.ToString() ?? "";

        var parts = new List<string>();
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            parts.Add(f.Name + "=" + Value(f.GetValue(v)));
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.PropertyType.IsByRef && p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum))
                parts.Add(p.Name + "=" + Value(p.GetValue(v)));
        }

        return parts.Count == 0 ? v.ToString() ?? "" : "(" + string.Join(",", parts) + ")";
    }
}
