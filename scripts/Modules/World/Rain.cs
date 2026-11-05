using System.Reflection;
using BmSDK.AkAudio;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

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

    private readonly record struct State(float Density, float Width, float Box, AkEvent? Bed, float Wet, float Damp, float Dark, float Wake);

    private static readonly PropertyInfo[] SurfaceSounds = typeof(PhysicalMaterial).GetProperties()
        .Where(p => p.Name.StartsWith("SurfaceRainSound", StringComparison.Ordinal) && p.PropertyType == typeof(AkEvent) && p.CanWrite)
        .ToArray();

    private readonly Dictionary<string, State> _originals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AkEvent?[]> _surfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitters = new(StringComparer.Ordinal);
    private AkParameterName? _param;
    private bool _paramSet;
    private float _resyncLeft = -1f;
    private int _look;
    private float _wet = 1f;
    private float _box = 1f;
    private Every _refresh;
    private bool _dirty;

    private bool Changed => _originals.Count > 0 || _surfaces.Count > 0 || _emitters.Count > 0 || _paramSet;

    public override void Build()
    {
        AddChoice("rain", "RAIN", "Changes the rain: none, a drizzle, heavy rain or a storm. NO RAIN also dries the streets and silences the rain. Works together with the sky & fog looks and post processing.",
            Looks.Select(l => l.Key).ToArray(), Looks.Select(l => l.Label).ToArray(), i => { _look = Math.Clamp(i, 0, Looks.Length - 1); _dirty = true; });
        AddSlider("rain_wetness", "WET STREETS", "How wet and shiny the streets, rooftops and suits look. 0% = completely dry.", 1f, 0f, 2f, 0.1f, 1.6f, Fmt.Percent, v => { _wet = v; _dirty = true; });
        AddSlider("rain_area", "RAIN AREA", "Size of the rain box around you. Smaller packs the drops close around the camera, bigger spreads them further out and thinner.", 1f, 0.25f, 3f, 0.25f, 2f, Fmt.Percent, v => { _box = v; _dirty = true; });
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
        _muted.Clear();
        _surfaces.Clear();
        _emitters.Clear();
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

        bool vanilla = _look == 0 && MathF.Abs(_wet - 1f) < 0.001f && MathF.Abs(_box - 1f) < 0.001f;
        if (vanilla)
        {
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

            bool hasMap = map is { IsValid: true };
            string key = v.GetPathName(null);
            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            if (!_originals.TryGetValue(key, out State o))
            {
                o = hasMap
                    ? new State(s.Density, s.Width, rc.BoxSize, s.RainSoundBed, map.WetnessGlobalScale, map.MinimumDampAmount, map.Darkening, map.RainWakeAmount)
                    : new State(s.Density, s.Width, rc.BoxSize, s.RainSoundBed, 1f, 0f, 0f, 0f);
                _originals[key] = o;
            }

            s.Density = o.Density * look.Density;
            s.Width = o.Width * look.Width;
            rc.BoxSize = o.Box * _box;
            if (hasMap)
            {
                map.WetnessGlobalScale = o.Wet * wet;
                map.MinimumDampAmount = o.Damp * damp;
                map.Darkening = o.Dark * damp;
                map.RainWakeAmount = o.Wake * Math.Min(look.Density, 1f);
            }

            bool muted = _muted.Contains(key);
            if (!look.Sound && !muted)
            {
                rc.DisableRainSound();
                s.RainSoundBed = null!;
                _muted.Add(key);
            }
            else if (look.Sound && muted)
            {
                s.RainSoundBed = o.Bed!;
                rc.EnableRainSound();
                _muted.Remove(key);
            }

            v.SetRainSettings();
        }

        if (look.Sound)
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
            if (!e.bEmitterEnabled || !PlaysRain(e))
                continue;

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

    private static void DryPawns()
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

            ref RRainComponent.FRockRainSettings s = ref rc.RainSettings;
            s.Density = o.Density;
            s.Width = o.Width;
            rc.BoxSize = o.Box;
            RainMapComponent map = v.RainMap;
            if (map is { IsValid: true })
            {
                map.WetnessGlobalScale = o.Wet;
                map.MinimumDampAmount = o.Damp;
                map.Darkening = o.Dark;
                map.RainWakeAmount = o.Wake;
            }

            if (_muted.Remove(key))
            {
                s.RainSoundBed = o.Bed!;
                rc.EnableRainSound();
            }

            v.SetRainSettings();
        }

        RestoreAudio();
        _originals.Clear();
        _muted.Clear();
    }
}
