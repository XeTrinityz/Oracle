using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD > ATMOSPHERE > CAPTURE > ERA SNAPSHOT: research for the one-click pre-Scarecrow era. The city's story
/// weather lives in the BatEntry__City_ChK*_Static_LOD2 packages (ChK1 is the start of the story) and the Gordon
/// section's look and sky in CityZ_10_Ch2C. This loads both into memory (without adding them to the world) and
/// writes every rain, lightning, fog and sky value, plus the whole weather Kismet graph with its links, next to the
/// same objects in the chapter you're playing. Read-only apart from loading the two packages.
/// </summary>
internal sealed class EraSnapshot : Module
{
    public static readonly EraSnapshot I = new();

    private static readonly string[] Packages = { "BatEntry__City_ChK1_Static_LOD2", "CityZ_10_Ch2C" };

    // Native strides confirmed in game (see the Kismet notes in memory): FSeqOpOutputLink 64, its targets 16, FSeqVarLink 96.
    private const int OutputLinkStride = 64;
    private const int OutputTargetStride = 16;
    private const int VarLinkStride = 96;
    private const int MaxLinks = 64;

    public override void Build()
    {
        AddCommand("era_snapshot", "ERA SNAPSHOT",
            "Research for the pre-Scarecrow era: loads the story's first weather and the Gordon chapter into memory and saves every rain, lightning, fog and sky value and the weather scripts to Oracle's looks folder.",
            Snapshot);
    }

    private static bool Interesting(string path) =>
        path.StartsWith("BatEntry__City_ChK", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("CityZ_10_Ch2C.", StringComparison.OrdinalIgnoreCase);

    private static void Snapshot()
    {
        foreach (string p in Packages)
        {
            Log.Info("ERA SNAPSHOT: loading " + p);
            Game.LoadPackage(p);
        }

        var sb = new StringBuilder();
        sb.AppendLine("[info]");
        sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("chapter=" + (World.Info is { } gi ? gi.GetChapterNumber() + "." + gi.GetSubChapterNumber() : "?"));

        int actors = 0;
        foreach (Actor a in Game.FindObjects<Actor>())
        {
            if (!a.IsValid || a.IsClassDefaultObject)
                continue;
            string path = a.GetPathName(null);
            bool live = a is RainVolume or RLightningActor;
            if (!Interesting(path) && !live)
                continue;
            if (a is not (RainVolume or RLightningActor or StaticMeshCollectionActor or PostProcessVolume or LensFlareSource or RStaticMeshToggleable or DirectionalLight))
                continue;

            sb.AppendLine();
            sb.AppendLine($"[actor {path}]");
            sb.AppendLine("class=" + a.GetType().Name);
            sb.AppendLine("location=" + LookCapture.Value(a.Location));
            sb.AppendLine("rotation=" + LookCapture.Value(a.Rotation));
            sb.AppendLine("hidden=" + LookCapture.Value(a.bHidden));
            Safe.Run("era actor " + path, () => DumpActor(sb, a));
            actors++;
        }

        int ops = 0;
        var byPtr = new Dictionary<nint, string>();
        var all = new List<SequenceObject>();
        foreach (SequenceObject o in Game.FindObjects<SequenceObject>())
        {
            if (!o.IsValid || o.IsClassDefaultObject || !Interesting(o.GetPathName(null)))
                continue;
            all.Add(o);
            byPtr[o.Ptr] = Short(o);
        }

        foreach (SequenceObject o in all.OrderBy(x => x.GetPathName(null)))
        {
            sb.AppendLine();
            sb.AppendLine($"[kismet {o.GetPathName(null)}]");
            sb.AppendLine("class=" + o.GetType().Name);
            Safe.Run("era kismet " + Short(o), () => DumpKismet(sb, o, byPtr));
            ops++;
        }

        string dir = Path.Combine(App.Dir, "looks");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "era_snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        File.WriteAllText(file, sb.ToString());
        Log.Info($"ERA SNAPSHOT: {actors} actors and {ops} Kismet objects written to {file}");
        Overlay.Notice("ERA SNAPSHOT", "SAVED", true);
    }

    private static string Short(GameObject o)
    {
        string p = o.GetPathName(null);
        int pkg = p.IndexOf('.');
        string package = pkg > 0 ? p.Substring(0, pkg) : "";
        int last = Math.Max(p.LastIndexOf('.'), p.LastIndexOf(':'));
        return (package.Length > 0 ? package.Replace("BatEntry__City_", "").Replace("_Static_LOD2", "") + "/" : "") + p.Substring(last + 1);
    }

    private static void DumpActor(StringBuilder sb, Actor a)
    {
        switch (a)
        {
            case RainVolume rain:
                Line(sb, "far_rain_mesh_drawn", rain.bDrawFarRainMesh);
                Line(sb, "camera_spatter", rain.RainCameraSpatter);
                Line(sb, "near_mist", rain.NearMist is { IsValid: true } mist ? mist.Template : null);
                Line(sb, "far_rain_mesh", rain.FarRainMesh is { IsValid: true } far ? far.StaticMesh : null);
                Line(sb, "far_atmos_mesh", rain.FarAtmosMesh is { IsValid: true } atm ? atm.StaticMesh : null);
                if (rain.RainComponent is { IsValid: true } rc)
                {
                    Line(sb, "rain.box_size", rc.BoxSize);
                    Line(sb, "rain.particle_count", rc.ParticleCount);
                    Line(sb, "rain.camera_culling_distance", rc.CameraCullingDistance);
                    Line(sb, "rain.hidden", rc.HiddenGame);
                    sb.AppendLine("rain.settings=" + LookCapture.Members(rc.RainSettings));
                    sb.AppendLine("rain.volume=" + LookCapture.Members(rc.VolumeSettings));
                }

                if (rain.RainMap is { IsValid: true } map)
                    Declared(sb, "rainmap", map, typeof(RainMapComponent));
                break;

            case RLightningActor l:
                Declared(sb, "lightning", l, typeof(RLightningActor));
                if (l.LightningComponent is { IsValid: true } lc)
                {
                    Line(sb, "lightning.light_brightness", lc.Brightness);
                    sb.AppendLine("lightning.light_color=" + LookCapture.Value(lc.LightColor));
                    Line(sb, "lightning.light_enabled", lc.bEnabled);
                }

                Line(sb, "lightning.strike_vfx", l.LightningStrikeVFX is { IsValid: true } s ? s.Template : null);
                Line(sb, "lightning.vertical_vfx", l.VerticalLightningStrikeVFX is { IsValid: true } vert ? vert.Template : null);
                break;

            case StaticMeshCollectionActor smca:
                TArray<StaticMeshComponent> comps = smca.StaticMeshComponents;
                for (int i = 0; i < comps.Count; i++)
                {
                    StaticMeshComponent c = comps[i];
                    if (c is not { IsValid: true })
                        continue;
                    sb.AppendLine($"smc{i}={LookCapture.Value(c.StaticMesh)} hidden={LookCapture.Value(c.HiddenGame)} radius={c.Bounds.SphereRadius:0}");
                    Mats(sb, $"smc{i}", c);
                }

                break;

            case PostProcessVolume v:
                Line(sb, "enabled", v.bEnabled);
                Line(sb, "priority", v.Priority);
                Line(sb, "lut", v.Settings.ColorGrading_LookupTable);
                break;

            case RStaticMeshToggleable t:
                if (t.StaticMeshComponent is { IsValid: true } tc)
                {
                    Line(sb, "mesh", tc.StaticMesh);
                    Line(sb, "mesh_hidden", tc.HiddenGame);
                    Mats(sb, "mesh", tc);
                }

                break;

            case LensFlareSource f:
                if (f.LensFlareComp is { IsValid: true } fc)
                {
                    Line(sb, "flare", fc.Template);
                    Line(sb, "flare_hidden", fc.HiddenGame);
                }

                break;

            case DirectionalLight d:
                if (d.LightComponent is { IsValid: true } dl)
                {
                    Line(sb, "brightness", dl.Brightness);
                    sb.AppendLine("color=" + LookCapture.Value(dl.LightColor));
                    Line(sb, "enabled", dl.bEnabled);
                    Line(sb, "light_shafts", dl.bRenderLightShafts);
                }

                break;
        }
    }

    private static void Mats(StringBuilder sb, string prefix, MeshComponent c)
    {
        int n = Math.Min(4, c.GetNumElements());
        for (int i = 0; i < n; i++)
        {
            MaterialInterface m = c.GetMaterial(i);
            sb.AppendLine($"{prefix}.material{i}={LookCapture.Value(m)}");
            if (m is not MaterialInstanceConstant mic || !mic.IsValid)
                continue;
            TArray<MaterialInstanceConstant.FScalarParameterValue> sc = mic.ScalarParameterValues;
            for (int k = 0; k < sc.Count; k++)
                sb.AppendLine($"{prefix}.material{i}.{sc[k].ParameterName}={LookCapture.Value(sc[k].ParameterValue)}");
            TArray<MaterialInstanceConstant.FVectorParameterValue> ve = mic.VectorParameterValues;
            for (int k = 0; k < ve.Count; k++)
                sb.AppendLine($"{prefix}.material{i}.{ve[k].ParameterName}={LookCapture.Value(ve[k].ParameterValue)}");
        }
    }

    private static void DumpKismet(StringBuilder sb, SequenceObject o, Dictionary<nint, string> byPtr)
    {
        switch (o)
        {
            case RSeqAct_Rain rain:
                sb.AppendLine("rain.settings=" + LookCapture.Members(rain.RainSettings));
                sb.AppendLine("rain.map=" + LookCapture.Members(rain.RainMap));
                break;
            case RSeqAct_TriggerLightning tl:
                Line(sb, "intensity", tl.LightningIntensity);
                sb.AppendLine("direction=" + LookCapture.Value(tl.LightningDirection));
                break;
            case SeqEvent_RemoteEvent re:
                Line(sb, "event", re.EventName.ToString());
                break;
            case SeqAct_ActivateRemoteEvent ae:
                Line(sb, "event", ae.EventName.ToString());
                break;
            case RSeqVar_GlobalFlag gf:
                Line(sb, "flag", gf.FlagNameAsString.ToString());
                Line(sb, "value", gf.bValue);
                break;
            case SeqVar_Bool b:
                Line(sb, "value", b.bValue);
                break;
            case SeqVar_Object so:
                Line(sb, "object", so.ObjValue);
                break;
        }

        Declared(sb, "prop", o, typeof(SequenceOp));

        if (o is not SequenceOp op)
            return;

        Line(sb, "activate_count", op.ActivateCount);
        Outputs(sb, op.OutputLinks.Ptr, byPtr);
        Variables(sb, op.VariableLinks.Ptr, byPtr);
    }

    private static void Outputs(StringBuilder sb, nint header, Dictionary<nint, string> byPtr)
    {
        if (!Header(header, OutputLinkStride, out nint data, out int count))
            return;

        for (int i = 0; i < count; i++)
        {
            nint link = data + i * OutputLinkStride;
            string desc = FString(link + 16);
            var targets = new List<string>();
            if (Header(link, OutputTargetStride, out nint tdata, out int tcount))
            {
                for (int j = 0; j < tcount; j++)
                {
                    nint target = Marshal.ReadIntPtr(tdata + j * OutputTargetStride);
                    int input = Marshal.ReadInt32(tdata + j * OutputTargetStride + 8);
                    targets.Add((byPtr.TryGetValue(target, out string? name) ? name : "?" + target.ToString("X")) + ".in" + input);
                }
            }

            sb.AppendLine($"out[{desc}] -> {(targets.Count == 0 ? "-" : string.Join(", ", targets))}");
        }
    }

    private static void Variables(StringBuilder sb, nint header, Dictionary<nint, string> byPtr)
    {
        if (!Header(header, VarLinkStride, out nint data, out int count))
            return;

        for (int i = 0; i < count; i++)
        {
            nint link = data + i * VarLinkStride;
            string desc = FString(link + 24);
            var vars = new List<string>();
            if (Header(link + 8, 8, out nint vdata, out int vcount))
            {
                for (int j = 0; j < vcount; j++)
                {
                    nint v = Marshal.ReadIntPtr(vdata + j * 8);
                    vars.Add(byPtr.TryGetValue(v, out string? name) ? name : "?" + v.ToString("X"));
                }
            }

            if (vars.Count > 0)
                sb.AppendLine($"var[{desc}] = {string.Join(", ", vars)}");
        }
    }

    private static bool Header(nint header, int stride, out nint data, out int count)
    {
        data = 0;
        count = 0;
        if (header == 0)
            return false;
        data = Marshal.ReadIntPtr(header);
        count = Marshal.ReadInt32(header + 8);
        int max = Marshal.ReadInt32(header + 12);
        return data != 0 && count > 0 && count <= max && count <= MaxLinks;
    }

    private static string FString(nint at)
    {
        nint chars = Marshal.ReadIntPtr(at);
        int n = Marshal.ReadInt32(at + 8);
        return chars == 0 || n <= 1 || n > 256 ? "" : Marshal.PtrToStringUni(chars, n - 1);
    }

    private static void Line(StringBuilder sb, string key, object? v) => sb.AppendLine(key + "=" + LookCapture.Value(v));

    // Every plain property a class (and its parents, down to but not including the given base) declares.
    private static void Declared(StringBuilder sb, string prefix, GameObject o, Type stopAt)
    {
        for (Type? t = o.GetType(); t != null && t != stopAt.BaseType && t != typeof(GameObject); t = t.BaseType)
        {
            if (t == typeof(SequenceOp) || t == typeof(SequenceObject) || t == typeof(Actor) || t == typeof(ActorComponent))
                break;

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Type pt = p.PropertyType;
                if (pt.IsByRef || pt == typeof(nint) || pt.Name.StartsWith("TArray") || !p.CanRead || p.GetIndexParameters().Length > 0)
                    continue;
                if (!(pt.IsPrimitive || pt.IsEnum || pt == typeof(Vector3) || pt.Name is "FName" or "FString" || typeof(GameObject).IsAssignableFrom(pt)))
                    continue;

                string text;
                try
                {
                    text = LookCapture.Value(p.GetValue(o));
                }
                catch (Exception e)
                {
                    text = "<" + e.GetType().Name + ">";
                }

                sb.AppendLine($"{prefix}.{p.Name}={text}");
            }

            if (t == stopAt)
                break;
        }
    }
}
