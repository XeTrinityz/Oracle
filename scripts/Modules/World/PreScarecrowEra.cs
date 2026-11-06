using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD > ATMOSPHERE > SCENE > PRE-SCARECROW ERA: the whole environment of the Gordon section before the
/// Scarecrow attack, in one switch. Worked out from the game's own data, read live from the chapter packages and
/// from inside the Gordon level (2026-11-06):
/// <list type="bullet">
/// <item>Look: CityZ_10_Ch2C PostProcessVolume_0 with LUT CityY_Lut_02 (<see cref="PreScarecrowLook"/>).</item>
/// <item>Sky: the chapter's dusk dome, sun and sun flare (<see cref="PreScarecrowSky"/>), with the night dome
/// hidden by the game's own Hide_Dome event.</item>
/// <item>Rain: off, streets dry. The story weather level BatEntry__City_ChK1_Static_LOD2 runs RSeqAct_Rain_2
/// (density 0) once Ace_A4_CounterAgent_Done is set and until Ace_A4_Ch2_Gordon_Story_Done, and again on
/// CH02_Gordon_Flashback_Copter_NoRain. Every rain sound is silenced through <see cref="Rain"/>.</item>
/// <item>Lightning: off. With Chapter2_Start and CH2_CityZ_10_GordonFlashback set, it toggles RLightningActor_0
/// off, which only sets bRandomFlashesDisabled (RLightningActor.OnToggle).</item>
/// <item>The city fog mesh (VISTA_FogVol_Optimisation) is identical in every story state, so it is left alone.</item>
/// </list>
/// </summary>
internal sealed class PreScarecrowEra : Module
{
    public static readonly PreScarecrowEra I = new();

    private const double CheckSeconds = 1.0;

    // RSeqAct_Rain_2 in ChK1, the game's "no rain" for the Gordon section, with a dry city: the action leaves the
    // rain map at full wetness, but the user saw dry streets there (2026-11-06). Wetness, damp, darkening and the
    // rain wake go to 0, the values Rain.cs's tested WET STREETS 0% writes.
    private static readonly RainValues GordonRain = new(0f, 1f, 0f, 800f, 14, false, 0f, 0f, 0.9f, 1f, 0f, 0.05f, 0f, 14f, 100f);

    private readonly record struct RainValues(
        float Density, float Width, float FearGas, float ProximityRadius, int ProximityRange, bool Static,
        float Wetness, float MinDamp, float DampThreshold, float Puddle, float Darkening,
        float WakeFrequency, float WakeAmount, float WakeSpeed, float WakeVariation);

    private readonly Held<RLightningActor, bool> _noLightning =
        new("era lightning", a => a.bRandomFlashesDisabled, (a, v) => a.bRandomFlashesDisabled = v);

    // The game's rain per volume (by path) and its rain sound bed, kept as plain doubles and paths so they outlive
    // a hot reload. They didn't at first: a reload while the era was on lost them, the next load took the era's
    // own density 0 as the game's, and turning the era off then left the city with no rain (2026-11-06).
    private static Dictionary<string, double[]> SavedRain => Shared.Keep("EraRainOriginals", () => new Dictionary<string, double[]>());
    private static Dictionary<string, string> SavedBeds => Shared.Keep("EraRainBeds", () => new Dictionary<string, string>());

    // Used only when the game's own rain was never seen: the rain this save's story weather (ChK2a) runs, density
    // 0.7, with the rain map every RSeqAct_Rain sets and the ChK1 rain bed.
    private static readonly RainValues StoryRain = new(0.7f, 1f, 0f, 800f, 14, false, 1f, 0.8f, 0.9f, 1f, 1f, 0.05f, 0.2f, 14f, 100f);
    private const string StoryBed = "Ww_WEA_Rain01.Play_Rain01";

    private static bool IsEraRain(RainValues v) => v.Density <= 0.001f;

    private bool _on;
    private Every _check;

    public override void Build()
    {
        AddToggle("pre_scarecrow_era", "PRE-SCARECROW ERA",
            "The whole Gordon-section environment from before the Scarecrow attack, in one switch: its look and colour grading, the dusk sky with its sun and sun glow, no rain, dry streets and no lightning, exactly as the game sets them. Turning it off gives the story's weather back.",
            on =>
            {
                _on = on;
                PreScarecrowLook.I.Set(on);
                PreScarecrowSky.I.Set(on);
                Rain.I.SetEraSilence(on);
                _check.Force();
                _domeHidden = false;
                if (!on)
                    Restore();
            });
    }

    // The night sky dome lives in BatEntry__City_LOD2_Vista, whose Kismet hides it on the remote event Hide_Dome and
    // shows it on UNHide_Dome. The Gordon chapter fires Hide_Dome during the flashback, so the era does the same.
    private bool _domeHidden;
    private bool _domeFired;

    public override void WorldChanged()
    {
        // A load builds the rain volumes afresh from the story, so the remembered values are stale.
        SavedRain.Clear();
        SavedBeds.Clear();
        _domeHidden = false; // a fresh Vista level shows its dome again
        _check.Force();
    }

    private void Dome(bool hide)
    {
        if (World.Info is not { } gi)
            return;
        gi.ActivateRemoteEvent(new BmSDK.FName(hide ? "Hide_Dome" : "UNHide_Dome"));
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
        Safe.Run("era dry", Rain.DryPawns); // characters and the car keep their own wetness build-up
    }

    private void Restore()
    {
        _noLightning.Restore();
        if (_domeFired)
        {
            _domeFired = false;
            Safe.Run("era dome", () => Dome(false));
        }
        int restored = 0;
        foreach (RainVolume v in LiveRain())
        {
            string key = v.GetPathName(null);
            if (!SavedRain.Remove(key, out double[]? packed) || v.RainComponent is not { IsValid: true } rc)
                continue;

            // Only put back what is still Oracle's: if the story turned the rain on since, its values stand.
            if (IsEraRain(Read(rc, v.RainMap)))
                Write(rc, v.RainMap, Unpack(packed));

            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            if (s.RainSoundBed is not { IsValid: true } && Bed(key) is { } bed)
                s.RainSoundBed = bed;
            rc.EnableRainSound();
            v.SetRainSettings();
            restored++;
        }

        if (restored > 0)
            Log.Info("PRE-SCARECROW ERA: story weather back on " + restored + " rain volume(s).");
        SavedRain.Clear();
        SavedBeds.Clear();
    }

    private static AkEvent? Bed(string key)
    {
        string path = SavedBeds.Remove(key, out string? saved) ? saved : StoryBed;
        return Game.FindObject<AkEvent>(path) is { IsValid: true } bed ? bed : Game.FindObject<AkEvent>(StoryBed);
    }

    private static void Silence(string key, RRainComponent rc)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        if (s.RainSoundBed is not { IsValid: true } bed)
            return;

        SavedBeds.TryAdd(key, bed.GetPathName(null));
        rc.DisableRainSound();
        s.RainSoundBed = null!;
    }

    private void ApplyRain()
    {
        foreach (RainVolume v in LiveRain())
        {
            if (v.RainComponent is not { IsValid: true } rc)
                continue;

            string key = v.GetPathName(null);
            RainValues now = Read(rc, v.RainMap);
            if (!SavedRain.ContainsKey(key))
            {
                // Never take the era's own no-rain for the game's: that's how the rain went missing before.
                RainValues game = IsEraRain(now) ? StoryRain : now;
                SavedRain[key] = Pack(game);
            }
            else if (!IsEraRain(now))
            {
                // The story turned the rain back on (a weather beat): that's the rain to give back later.
                SavedRain[key] = Pack(now);
            }

            bool loud = rc.RainSettings.RainSoundBed is { IsValid: true };
            if (now == GordonRain && !loud)
                continue;

            Write(rc, v.RainMap, GordonRain);
            Silence(key, rc);
            v.SetRainSettings();
        }
    }

    private static double[] Pack(RainValues v) => new double[]
    {
        v.Density, v.Width, v.FearGas, v.ProximityRadius, v.ProximityRange, v.Static ? 1 : 0, v.Wetness, v.MinDamp, v.DampThreshold,
        v.Puddle, v.Darkening, v.WakeFrequency, v.WakeAmount, v.WakeSpeed, v.WakeVariation,
    };

    private static RainValues Unpack(double[] a) => new(
        (float)a[0], (float)a[1], (float)a[2], (float)a[3], (int)a[4], a[5] > 0.5, (float)a[6], (float)a[7], (float)a[8],
        (float)a[9], (float)a[10], (float)a[11], (float)a[12], (float)a[13], (float)a[14]);

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
