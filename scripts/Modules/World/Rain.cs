using System.Reflection;
using BmSDK.AkAudio;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using FLinearColor = BmSDK.GameObject.FLinearColor;

namespace CeZeY.Oracle;

internal sealed class Rain : Module
{
    public static readonly Rain I = new();

    private const double RefreshSeconds = 2.0;
    private const string DensityParam = "Ww_Parameters.RainDensity";

    private sealed record Look(string Key, string Label, float Density, float Width, float Wet, bool Sound);

    private static readonly Look[] Looks =
    {
        new("vanilla", "VANILLA", 1f, 1f, 1f, true),
        new("off", "NO RAIN", 0f, 1f, 0f, false),
        new("drizzle", "DRIZZLE", 0.3f, 0.8f, 0.7f, true),
        new("heavy", "HEAVY", 1.9f, 1.15f, 1.3f, true),
        new("storm", "STORM", 3.2f, 1.4f, 1.6f, true),
    };

    /// <summary>Every value Oracle writes on one rain volume, compared whole to tell its own writes from the game's.</summary>
    private readonly record struct Values(float Density, float Width, float Box, float Wet, float Damp, float Dark, float Wake,
        float Gas, FLinearColor Sludge, float Spatter, bool FarRain, bool FarHidden, float Puddle, float DampTh);

    private readonly record struct State(Values V, AkEvent? Bed);

    private static Values Read(RainVolume v, RRainComponent rc, RainMapComponent map)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        bool hasMap = map is { IsValid: true };
        StaticMeshComponent far = v.FarRainMesh;
        return new Values(s.Density, s.Width, rc.BoxSize,
            hasMap ? map.WetnessGlobalScale : 1f, hasMap ? map.MinimumDampAmount : 0f, hasMap ? map.Darkening : 0f, hasMap ? map.RainWakeAmount : 0f,
            s.FearGasAmount, s.FearGasSludgeColor, v.RainCameraSpatter, v.bDrawFarRainMesh, far is { IsValid: true } && far.HiddenGame, hasMap ? map.PuddleThreshold : 0f,
            hasMap ? map.DampThreshold : 0f);
    }

    private static void Write(RainVolume v, RRainComponent rc, RainMapComponent map, Values n)
    {
        ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
        s.Density = n.Density;
        s.Width = n.Width;
        rc.BoxSize = n.Box;
        s.FearGasAmount = n.Gas;
        s.FearGasSludgeColor = n.Sludge;
        v.RainCameraSpatter = n.Spatter;
        v.bDrawFarRainMesh = n.FarRain;
        if (v.FarRainMesh is { IsValid: true } far && far.HiddenGame != n.FarHidden)
            far.SetHidden(n.FarHidden);
        if (map is { IsValid: true })
        {
            map.WetnessGlobalScale = n.Wet;
            map.MinimumDampAmount = n.Damp;
            map.Darkening = n.Dark;
            map.RainWakeAmount = n.Wake;
            map.PuddleThreshold = n.Puddle;
            map.DampThreshold = n.DampTh;
            // The rain map's values reach the renderer when the component is rebuilt.
            map.QueueReattach(false);
        }
    }

    private static FLinearColor Tint(Toxin t, FLinearColor game) => new() { R = t.R, G = t.G, B = t.B, A = game.A <= 0f ? 1f : game.A };

    private sealed record Toxin(string Key, string Label, float R, float G, float B);

    private static readonly Toxin[] Toxins =
    {
        new("game", "GAME", 0f, 0f, 0f),
        new("green", "FEAR GREEN", 0.25f, 1f, 0.1f),
        new("yellow", "SICKLY YELLOW", 1f, 0.85f, 0.1f),
        new("orange", "ORANGE", 1f, 0.42f, 0.05f),
        new("red", "BLOOD RED", 1f, 0.05f, 0.03f),
        new("purple", "JOKER PURPLE", 0.6f, 0.12f, 1f),
        new("blue", "COLD BLUE", 0.15f, 0.5f, 1f),
    };

    private static readonly PropertyInfo[] SurfaceSounds = typeof(PhysicalMaterial).GetProperties()
        .Where(p => p.Name.StartsWith("SurfaceRainSound", StringComparison.Ordinal) && p.PropertyType == typeof(AkEvent) && p.CanWrite)
        .ToArray();

    private readonly Dictionary<string, State> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Values> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AkEvent?[]> _surfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitters = new(StringComparer.Ordinal);

    // Surfaces and emitters MuteAudio has already looked at in this world, by wrapper: one wrapper is one live object,
    // so neither is read again every refresh. Cleared whenever the sounds go back.
    private readonly HashSet<PhysicalMaterial> _surfacesSeen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AkEmitter> _dryEmitters = new(ReferenceEqualityComparer.Instance);
    private AkParameterName? _param;
    private bool _paramSet;
    private float _resyncLeft = -1f;
    private readonly HashSet<string> _mistOff = new(StringComparer.Ordinal);
    private int _look;
    private float _wet = 1f;
    private float _box = 1f;
    private float _toxin;
    private int _toxinColour;
    private float _puddles = 1f;
    private bool _noMist;
    private bool _noFarRain;
    private Every _refresh;
    private bool _dirty;

    private bool Vanilla => _look == 0 && MathF.Abs(_wet - 1f) < 0.001f && MathF.Abs(_box - 1f) < 0.001f && _toxin <= 0f && _toxinColour == 0
        && MathF.Abs(_puddles - 1f) < 0.001f && !_noMist && !_noFarRain;

    private bool Changed => _originals.Count > 0 || _surfaces.Count > 0 || _emitters.Count > 0 || _paramSet;

    public override void Build()
    {
        AddChoice("rain", "RAIN", "Changes the rain: none, a drizzle, heavy rain or a storm. NO RAIN also dries the streets and silences the rain. Works together with the sky & fog looks and post processing.",
            Looks.Select(l => l.Key).ToArray(), Looks.Select(l => l.Label).ToArray(), i => { _look = Math.Clamp(i, 0, Looks.Length - 1); _dirty = true; });
        AddSlider("rain_wetness", "WET STREETS", "How wet and shiny the streets, rooftops and suits look. 0% = completely dry.", 1f, 0f, 2f, 0.1f, 1.6f, Fmt.Percent, v => { _wet = v; _dirty = true; });
        AddSlider("rain_area", "RAIN AREA", "Size of the rain box around you. Smaller packs the drops close around the camera, bigger spreads them further out and thinner.", 1f, 0.25f, 3f, 0.25f, 2f, Fmt.Percent, v => { _box = v; _dirty = true; });

        AddSlider("rain_toxin", "TOXIN RAIN", "Mixes Scarecrow's fear toxin into the rain, the sludgy coloured rain from under the toxin cloud. Only shows while the toxin cloud hangs over the city. 0% = clean rain.", 0f, 0f, 1f, 0.1f, 0.8f, Fmt.Percent,
            v => { _toxin = v; _dirty = true; WarnNoCloud(v); });
        Choice colour = AddChoice("rain_toxin_colour", "TOXIN COLOUR", "Colour of the toxin in the rain. GAME keeps the game's own.",
            Toxins.Select(t => t.Key).ToArray(), Toxins.Select(t => t.Label).ToArray(), i => { _toxinColour = Math.Clamp(i, 0, Toxins.Length - 1); _dirty = true; });
        colour.Swatch = i => i <= 0 ? Rgba.From(0.35f, 0.8f, 0.25f) : Rgba.From(Toxins[i].R, Toxins[i].G, Toxins[i].B);

        AddSlider("rain_puddles", "PUDDLES", "How readily puddles form on wet ground. 0% = none at all.", 1f, 0f, 2f, 0.1f, 1.8f, Fmt.Percent, v => { _puddles = v; _dirty = true; });
        AddToggle("rain_no_mist", "NO RAIN MIST", "Removes the spray of mist that hangs around you in the rain.", on => { _noMist = on; _dirty = true; });
        AddToggle("rain_no_far", "NO DISTANT RAIN", "Removes the sheets of rain drawn far off across the city, so the skyline stays sharp.", on => { _noFarRain = on; _dirty = true; });
    }

    /// <summary>
    /// The puddle and damp thresholds as wetness levels: the game's are 1 (2026-11-05 log), the top. The first
    /// version could only go drier from there, so below 100% did nothing; now 0% raises it past the top and 200%
    /// drops it to nothing.
    /// </summary>
    private static float Puddle(float o, float v)
    {
        float top = Math.Max(o, 1f);
        return v <= 1f ? o + (top * 2f - o) * (1f - v) : o * (2f - v);
    }

    /// <summary>
    /// Uses the script function IsGasActive (the story's toxin side chapter, or the GasActiveInOverworld flag), an
    /// ordinary call on the game info. Never the static natives IsFearGasActive/IsLocationInFearGas/
    /// GetFearGasHeightAtLocation: BmSDK runs a static function on the class default object, which these natives
    /// evidently can't work from, and IsFearGasActive crashed the game in coreclr twice on 2026-11-05, which no
    /// try/catch can stop.
    /// </summary>
    private static void WarnNoCloud(float v)
    {
        if (v <= 0f || World.Info is not { } gi)
            return;

        if (!Safe.Try("gas check", () => gi.IsGasActive()))
            Overlay.Notice("TOXIN RAIN", "NEEDS SCARECROW'S TOXIN CLOUD", false);
    }

    private bool _eraSilence;

    /// <summary>
    /// PRE-SCARECROW ERA: silences every rain sound (the Wwise rain density, surface rain sounds and rain emitters)
    /// without touching the rain volumes, which the era sets itself. The rain volume's own sound bed is the era's.
    /// </summary>
    public void SetEraSilence(bool on)
    {
        if (_eraSilence == on)
            return;
        _eraSilence = on;
        _dirty = true;
        if (!on)
            Safe.Run("rain audio restore", RestoreAudio);
    }

    public override void WorldChanged() => Forget();

    public override void LeftGame()
    {
        if (Changed)
            Forget();
    }

    public override void Shutdown() => Safe.Run("rain restore", Restore);

    private void Forget()
    {
        Safe.Run("rain audio restore", () =>
        {
            if (_paramSet || _surfaces.Count > 0)
            {
                _emitters.Clear();
                RestoreAudio();
            }
        });

        _originals.Clear();
        _written.Clear();
        _muted.Clear();
        _mistOff.Clear();
        _surfaces.Clear();
        _emitters.Clear();
        _surfacesSeen.Clear();
        _dryEmitters.Clear();
        _param = null;
        _paramSet = false;
        _dirty = true;
    }

    public override void Tick(float dt)
    {
        if (_resyncLeft >= 0f)
        {
            _resyncLeft -= dt;
            if (_resyncLeft < 0f)
                ResyncParam();
        }

        if (Vanilla)
        {
            if (_eraSilence)
            {
                if (_dirty || _refresh.Elapsed(RefreshSeconds))
                {
                    _dirty = false;
                    MuteAudio();
                }

                return;
            }

            if (Changed)
                Restore();
            return;
        }

        if (!_dirty && !_refresh.Elapsed(RefreshSeconds))
            return;

        _dirty = false;
        Look look = Looks[_look];
        float wet = look.Wet * _wet;
        float damp = Math.Min(wet, 1f);

        foreach (RainVolume v in World.All<RainVolume>())
        {
            RRainComponent rc = v.RainComponent;
            RainMapComponent map = v.RainMap;
            if (rc is not { IsValid: true })
                continue;

            string key = v.GetPathName(null);
            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            Values live = Read(v, rc, map);

            // Anything Oracle didn't write is the game's own change (a story weather beat), so follow it.
            bool known = _originals.TryGetValue(key, out State o);
            if (!known || !_written.TryGetValue(key, out Values w) || live != w)
            {
                AkEvent? bed = s.RainSoundBed is { IsValid: true } b ? b : known ? o.Bed : null;
                o = new State(live, bed);
                _originals[key] = o;
            }

            Values g = o.V;
            Values want = g with
            {
                Density = g.Density * look.Density,
                Width = g.Width * look.Width,
                Box = g.Box * _box,
                Wet = g.Wet * wet,
                Damp = g.Damp * damp,
                Dark = g.Dark * damp,
                Wake = g.Wake * Math.Min(look.Density, 1f),
                Puddle = Puddle(g.Puddle, _puddles),
                DampTh = Puddle(g.DampTh, _puddles),
                Gas = Math.Max(g.Gas, _toxin),
                Sludge = _toxinColour > 0 ? Tint(Toxins[_toxinColour], g.Sludge) : g.Sludge,
                FarRain = g.FarRain && !_noFarRain,
                FarHidden = g.FarHidden || _noFarRain,
            };

            bool changed = want != live;
            if (changed)
                Write(v, rc, map, want);

            SetMist(v, key, _noMist);

            bool muted = _muted.Contains(key);
            if (!look.Sound && !muted)
            {
                rc.DisableRainSound();
                s.RainSoundBed = null!;
                _muted.Add(key);
                changed = true;
            }
            else if (look.Sound && muted)
            {
                s.RainSoundBed = o.Bed!;
                rc.EnableRainSound();
                _muted.Remove(key);
                changed = true;
            }

            if (changed)
                v.SetRainSettings();

            // Read back rather than trusting what was written, in case the native side clamps a value.
            _written[key] = Read(v, rc, map);
        }

        if (look.Sound && !_eraSilence)
            RestoreAudio();
        else
            MuteAudio();

        if (wet < 0.001f)
            DryPawns();
    }

    private void MuteAudio()
    {
        _param ??= Game.FindObject<AkParameterName>(DensityParam);
        if (_param is { IsValid: true })
        {
            AkWwise.SetGlobalAudioParameter(_param, 0f);
            _paramSet = true;
        }

        foreach (PhysicalMaterial m in World.All<PhysicalMaterial>())
        {
            if (!_surfacesSeen.Add(m))
                continue;

            string key = m.GetPathName(null);
            if (_surfaces.ContainsKey(key))
                continue;

            var saved = new AkEvent?[SurfaceSounds.Length];
            bool any = false;
            for (int i = 0; i < SurfaceSounds.Length; i++)
            {
                saved[i] = SurfaceSounds[i].GetValue(m) as AkEvent;
                if (saved[i] == null)
                    continue;

                any = true;
                SurfaceSounds[i].SetValue(m, null);
            }

            if (any)
                _surfaces[key] = saved;
        }

        foreach (AkEmitter e in World.All<AkEmitter>())
        {
            if (!e.bEmitterEnabled || _dryEmitters.Contains(e))
                continue;
            if (!PlaysRain(e))
            {
                _dryEmitters.Add(e);
                continue;
            }

            e.DisableEmitter();
            _emitters.Add(e.GetPathName(null));
        }
    }

    private void ResyncParam()
    {
        if (_paramSet || Looks[_look].Density <= 0f)
            return;

        _param ??= Game.FindObject<AkParameterName>(DensityParam);
        if (_param is { IsValid: true })
            AkWwise.SetGlobalAudioParameter(_param, AkSoundDirector.LocationRainDensity(AkWwise.GetActiveListenerPosition()));
    }

    private static bool PlaysRain(AkEmitter e)
    {
        var events = e.EmitterEvents;
        for (int i = 0; i < events.Count; i++)
        {
            AkEvent ev = events[i];
            if (ev is { IsValid: true } && ev.Name.ToString().Contains("Rain", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void RestoreAudio()
    {
        if (_paramSet)
        {
            _paramSet = false;
            _resyncLeft = 1f;
        }

        if (_surfaces.Count > 0)
        {
            foreach (PhysicalMaterial m in World.All<PhysicalMaterial>())
            {
                if (!_surfaces.TryGetValue(m.GetPathName(null), out AkEvent?[]? saved))
                    continue;

                for (int i = 0; i < SurfaceSounds.Length; i++)
                {
                    if (saved[i] is { IsValid: true })
                        SurfaceSounds[i].SetValue(m, saved[i]);
                }
            }

            _surfaces.Clear();
        }

        _surfacesSeen.Clear();
        _dryEmitters.Clear();
        if (_emitters.Count > 0)
        {
            foreach (AkEmitter e in World.All<AkEmitter>())
            {
                if (!e.bEmitterEnabled && _emitters.Contains(e.GetPathName(null)))
                    e.EnableEmitter();
            }

            _emitters.Clear();
        }
    }

    internal static void DryPawns()
    {
        foreach (RPawnCharacter p in World.All<RPawnCharacter>())
        {
            if (p.WetnessAccumulationAlpha > 0f)
                p.WetnessAccumulationAlpha = 0f;
            if (p.RainAccumulationAlpha > 0f)
                p.RainAccumulationAlpha = 0f;
        }

        if (World.Pawn is RVehicle car)
        {
            car.WetnessAccumulationAlpha = 0f;
            car.RainAccumulationAlpha = 0f;
        }
    }

    private void Restore()
    {
        foreach (RainVolume v in World.All<RainVolume>())
        {
            string key = v.GetPathName(null);
            RRainComponent rc = v.RainComponent;
            if (!_originals.TryGetValue(key, out State o) || rc is not { IsValid: true })
                continue;

            // Only put back what is still Oracle's: if the game has changed the volume since, its values stand.
            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            RainMapComponent map = v.RainMap;
            if (!_written.TryGetValue(key, out Values w) || Read(v, rc, map) == w)
                Write(v, rc, map, o.V);
            SetMist(v, key, false);

            if (_muted.Remove(key))
            {
                s.RainSoundBed = o.Bed!;
                rc.EnableRainSound();
            }

            v.SetRainSettings();
        }

        RestoreAudio();
        _originals.Clear();
        _written.Clear();
        _muted.Clear();
        _mistOff.Clear();
    }

    /// <summary>Stops or restarts the mist that follows the camera, remembering which ones Oracle stopped.</summary>
    private void SetMist(RainVolume v, string key, bool off)
    {
        ParticleSystemComponent mist = v.NearMist;
        if (mist is not { IsValid: true })
            return;

        if (off && !_mistOff.Contains(key))
        {
            if (!mist.bIsActive)
                return;

            mist.DeactivateSystem();
            mist.SetHidden(true);
            _mistOff.Add(key);
        }
        else if (!off && _mistOff.Remove(key))
        {
            mist.SetHidden(false);
            mist.ActivateSystem(false, false);
        }
    }
}
