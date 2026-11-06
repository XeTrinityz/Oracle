using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD > ATMOSPHERE > SCENE > PRE-SCARECROW ERA: the whole environment of the Gordon section before the
/// Scarecrow attack, in one switch. Worked out from the game's own data (looks/era_snapshot_20261106_000537):
/// <list type="bullet">
/// <item>Look: CityZ_10_Ch2C PostProcessVolume_0 with LUT CityY_Lut_02 (SCENE LOOK's PRE-SCARECROW).</item>
/// <item>Sky: the chapter's dusk dome, sun and sun flare (DUSK SKY).</item>
/// <item>Rain: off. The story weather level BatEntry__City_ChK1_Static_LOD2 runs RSeqAct_Rain_2 (density 0, rain
/// map unchanged, so the streets stay wet) once Ace_A4_CounterAgent_Done is set and until Ace_A4_Ch2_Gordon_Story_Done,
/// and again on CH02_Gordon_Flashback_Copter_NoRain.</item>
/// <item>Lightning: off. With Chapter2_Start and CH2_CityZ_10_GordonFlashback set, it toggles RLightningActor_0
/// off, which only sets bRandomFlashesDisabled (RLightningActor.OnToggle).</item>
/// <item>The city fog mesh (VISTA_FogVol_Optimisation) is identical in every story state, so it is left alone.</item>
/// </list>
/// </summary>
internal sealed class PreScarecrowEra : Module
{
    public static readonly PreScarecrowEra I = new();

    private const double CheckSeconds = 1.0;

    // RSeqAct_Rain_2 in ChK1, the game's "no rain" for the Gordon section.
    private static readonly RainValues GordonRain = new(0f, 1f, 0f, 800f, 14, false, 1f, 0.8f, 0.9f, 1f, 1f, 0.05f, 0.2f, 14f, 100f);

    private readonly record struct RainValues(
        float Density, float Width, float FearGas, float ProximityRadius, int ProximityRange, bool Static,
        float Wetness, float MinDamp, float DampThreshold, float Puddle, float Darkening,
        float WakeFrequency, float WakeAmount, float WakeSpeed, float WakeVariation);

    private readonly Held<RLightningActor, bool> _noLightning =
        new("era lightning", a => a.bRandomFlashesDisabled, (a, v) => a.bRandomFlashesDisabled = v);

    // Per rain volume: the game's values, and what Oracle last wrote.
    private readonly Dictionary<string, (RainValues Original, RainValues Written)> _rain = new();

    // Density 0 alone leaves the rain sound bed playing (found for Rain.cs), so the era silences it too.
    private readonly Dictionary<string, AkEvent?> _beds = new();

    private bool _on;
    private Every _check;

    public override void Build()
    {
        AddToggle("pre_scarecrow_era", "PRE-SCARECROW ERA",
            "The whole Gordon-section environment from before the Scarecrow attack, in one switch: its look and colour grading, the dusk sky with its sun and sun glow, no rain (the streets stay wet) and no lightning, exactly as the game sets them. Turning it off gives the story's weather back.",
            on =>
            {
                _on = on;
                LookPresets.I.SetEra(on);
                DuskSky.I.SetEra(on);
                Rain.I.SetEraSilence(on);
                _check.Force();
                _domeHidden = false;
                if (!on)
                    Restore();
            });
    }

    // The night sky dome lives in BatEntry__City_LOD2_Vista, whose Kismet hides it on the remote event Hide_Dome and
    // shows it on UNHide_Dome. The Gordon chapter fires Hide_Dome during the flashback (seen running in the
    // 00:15:40 snapshot), so the era does the same.
    private bool _domeHidden;
    private bool _domeFired;

    public override void WorldChanged()
    {
        _rain.Clear();
        _beds.Clear();
        _domeHidden = false; // a fresh Vista level shows its dome again
        _check.Force();
    }

    private void Dome(bool hide)
    {
        if (World.Info is not { } gi)
            return;
        gi.ActivateRemoteEvent(new BmSDK.FName(hide ? "Hide_Dome" : "UNHide_Dome"));
        Log.Info("PRE-SCARECROW ERA: " + (hide ? "Hide_Dome" : "UNHide_Dome") + " fired.");
    }

    public override void Shutdown() => Safe.Run("era restore", Restore);

    public override void Tick(float dt)
    {
        // The chapter's own weather script can switch rain and lightning back on (story events), so keep checking.
        if (!_on || !_check.Elapsed(CheckSeconds))
            return;

        if (!_domeHidden)
        {
            _domeHidden = true;
            _domeFired = true;
            Safe.Run("era dome", () => Dome(true));
        }

        Safe.Run("era lightning", () =>
        {
            if (World.Info?.LightningEventListener is { IsValid: true } l)
                _noLightning.Set(l, true);
        });
        Safe.Run("era rain", ApplyRain);
    }

    private void Restore()
    {
        _noLightning.Restore();
        if (_domeFired)
        {
            _domeFired = false;
            Safe.Run("era dome", () => Dome(false));
        }
        foreach (RainVolume v in LiveRain())
        {
            string key = v.GetPathName(null);
            if (!_rain.TryGetValue(key, out var saved) || v.RainComponent is not { IsValid: true } rc)
                continue;

            // Only put back what is still Oracle's: if the story changed the rain since, its values stand.
            if (Read(rc, v.RainMap) == saved.Written)
                Write(rc, v.RainMap, saved.Original);

            if (_beds.Remove(key, out AkEvent? bed))
            {
                ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
                if (s.RainSoundBed is not { IsValid: true } && bed is { IsValid: true })
                    s.RainSoundBed = bed;
                rc.EnableRainSound();
            }

            v.SetRainSettings();
        }

        if (_rain.Count > 0)
            Log.Info("PRE-SCARECROW ERA: story weather back on " + _rain.Count + " rain volume(s).");
        _rain.Clear();
        _beds.Clear();
    }

    private void Silence(string key, RRainComponent rc)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        if (!_beds.ContainsKey(key))
            _beds[key] = s.RainSoundBed is { IsValid: true } bed ? bed : null;
        if (s.RainSoundBed is { IsValid: true })
        {
            rc.DisableRainSound();
            s.RainSoundBed = null!;
        }
    }

    private void ApplyRain()
    {
        foreach (RainVolume v in LiveRain())
        {
            if (v.RainComponent is not { IsValid: true } rc)
                continue;

            string key = v.GetPathName(null);
            RainValues now = Read(rc, v.RainMap);
            if (!_rain.TryGetValue(key, out var saved))
            {
                _rain[key] = saved = (now, GordonRain);
                Log.Info($"PRE-SCARECROW ERA: rain off on {key} (was density {now.Density:0.##}).");
            }
            else if (now != saved.Written)
            {
                // The story changed the rain since: that's the value to give back later.
                _rain[key] = saved = (now, GordonRain);
            }

            bool loud = rc.RainSettings.RainSoundBed is { IsValid: true };
            if (now == GordonRain && !loud)
                continue;

            Write(rc, v.RainMap, GordonRain);
            Silence(key, rc);
            v.SetRainSettings();
            _rain[key] = (saved.Original, Read(rc, v.RainMap));
        }
    }

    // The volumes in the story weather level the game is running, not the copies other features load into memory.
    private static IEnumerable<RainVolume> LiveRain()
    {
        string? live = World.Info?.LightningEventListener is { IsValid: true } l ? Package(l.GetPathName(null)) : null;
        foreach (RainVolume v in World.All<RainVolume>())
        {
            string pkg = Package(v.GetPathName(null));
            if (live != null ? pkg.Equals(live, StringComparison.OrdinalIgnoreCase) : !pkg.Equals("CityZ_10_Ch2C", StringComparison.OrdinalIgnoreCase))
                yield return v;
        }
    }

    private static string Package(string path)
    {
        int dot = path.IndexOf('.');
        return dot > 0 ? path.Substring(0, dot) : path;
    }

    private static RainValues Read(RRainComponent rc, RainMapComponent map)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        bool m = map is { IsValid: true };
        return new RainValues(s.Density, s.Width, s.FearGasAmount, s.RainProximityRadius, s.RainProximityRange, s.StaticRainVolume,
            m ? map.WetnessGlobalScale : 0f, m ? map.MinimumDampAmount : 0f, m ? map.DampThreshold : 0f, m ? map.PuddleThreshold : 0f,
            m ? map.Darkening : 0f, m ? map.RainWakeFrequency : 0f, m ? map.RainWakeAmount : 0f, m ? map.RainWakeSpeed : 0f,
            m ? map.RainWakeVariation : 0f);
    }

    private static void Write(RRainComponent rc, RainMapComponent map, RainValues n)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        s.Density = n.Density;
        s.Width = n.Width;
        s.FearGasAmount = n.FearGas;
        s.RainProximityRadius = n.ProximityRadius;
        s.RainProximityRange = n.ProximityRange;
        s.StaticRainVolume = n.Static;
        if (map is not { IsValid: true })
            return;

        map.WetnessGlobalScale = n.Wetness;
        map.MinimumDampAmount = n.MinDamp;
        map.DampThreshold = n.DampThreshold;
        map.PuddleThreshold = n.Puddle;
        map.Darkening = n.Darkening;
        map.RainWakeFrequency = n.WakeFrequency;
        map.RainWakeAmount = n.WakeAmount;
        map.RainWakeSpeed = n.WakeSpeed;
        map.RainWakeVariation = n.WakeVariation;
    }
}
