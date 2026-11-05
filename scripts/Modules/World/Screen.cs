using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Post processing presets. Works with <see cref="Atmosphere"/>, which needs parts of it left on.</summary>
internal sealed class Screen : Module
{
    public static readonly Screen I = new();

    private const double RefreshSeconds = 2.0;

    private readonly record struct EffectState(bool UseWorld, bool LightingOnly);

    private readonly Dictionary<string, EffectState> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _volumes = new(StringComparer.Ordinal);
    private Choice _choice = null!;
    private int _mode;
    private bool _withAtmo;
    private Every _refresh;

    /// <summary>A sky look or a PostFx row needs the world's settings, so REDUCED and OFF keep them with the grading neutralised.</summary>
    private static bool WorldLook => Atmosphere.I.Wanted || PostFx.I.Wanted;

    public override void Build()
    {
        _choice = AddChoice("post_process", "POST PROCESSING", "REDUCED strips the colour grading. OFF also flattens the lighting. Both work together with the sky & fog looks.",
            new[] { "vanilla", "reduced", "off" }, new[] { "VANILLA", "REDUCED", "OFF" }, Apply);
    }

    public override void LeftGame()
    {
        if (_effects.Count == 0 && _volumes.Count == 0)
            return;

        _mode = 0;
        Restore();
    }

    public override void Shutdown()
    {
        _mode = 0;
        Safe.Run("post process restore", Restore);
    }

    public override void Tick(float dt)
    {
        if (_choice.Index != 0 && _withAtmo != WorldLook)
            Apply(_choice.Index);

        if (_mode != 0 && _refresh.Elapsed(RefreshSeconds))
            Write();
    }

    private void Apply(int mode)
    {
        _mode = Math.Clamp(mode, 0, 2);
        _withAtmo = _mode != 0 && WorldLook;
        Atmosphere.I.SetGradeOff(_withAtmo);
        Write();
    }

    private void Write()
    {
        if (_mode == 0)
        {
            Restore();
            return;
        }

        // Only the grading, bloom and lighting chain (RockOn, RockAtmos, Uber and the DOF/bloom effects). The
        // material slots are the game's overlays (damage, stun, detective mode, menu blur) and the game shows and
        // hides ambient occlusion itself for detective vision, so those are never touched, and nothing is forced visible.
        bool lightingOnly = _mode == 2;
        foreach (PostProcessEffect e in World.All<PostProcessEffect>())
        {
            if (e is not DOFEffect)
                continue;

            string key = e.GetPathName(null);
            if (!_effects.ContainsKey(key))
                _effects[key] = new EffectState(e.bUseWorldSettings, e.bAffectsLightingOnly);

            bool rock = e is RockAtmos || e is RockOn;
            bool useWorld = _withAtmo && (rock || e is UberPostProcessEffect);
            bool light = lightingOnly && !(_withAtmo && rock);
            if (e.bUseWorldSettings != useWorld)
                e.bUseWorldSettings = useWorld;
            if (e.bAffectsLightingOnly != light)
                e.bAffectsLightingOnly = light;
        }

        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
        {
            string key = v.GetPathName(null);
            if (!_volumes.ContainsKey(key))
                _volumes[key] = v.bEnabled;
            if (v.bEnabled != _withAtmo)
                v.bEnabled = _withAtmo;
        }
    }

    private void Restore()
    {
        if (_effects.Count == 0 && _volumes.Count == 0)
            return;

        foreach (PostProcessEffect e in World.All<PostProcessEffect>())
        {
            if (!_effects.TryGetValue(e.GetPathName(null), out EffectState o))
                continue;

            e.bUseWorldSettings = o.UseWorld;
            e.bAffectsLightingOnly = o.LightingOnly;
        }

        foreach (PostProcessVolume v in World.All<PostProcessVolume>())
        {
            if (_volumes.TryGetValue(v.GetPathName(null), out bool on))
                v.bEnabled = on;
        }

        _effects.Clear();
        _volumes.Clear();
    }
}
