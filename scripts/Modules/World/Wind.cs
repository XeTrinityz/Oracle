using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using WindConfig = BmSDK.BmGame.RGameInfo.FRWindConfig;

namespace CeZeY.Oracle;

/// <summary>
/// The city's wind: the global config RGameInfo ticks natively (cape, debris, flags) and the local wind volumes.
/// The game rewrites the global config from Kismet (RSeqAct_ChangeWind), so any value Oracle didn't write is
/// taken as the game's new original and the override is worked out from that.
/// </summary>
internal sealed class Wind : Module
{
    public static readonly Wind I = new();

    // Used when the game's wind is still: a direction and speed to scale from. Calibrate from the log.
    private const float BaseSpeed = 600f;
    private const float BaseGust = 300f;

    private sealed class Slot
    {
        public WindConfig Original;
        public WindConfig Written;
        public bool Holding;
    }

    private readonly Slot _global = new();
    private readonly Dictionary<string, Slot> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float S1, float S2, float W1, float W2)> _sources = new(StringComparer.Ordinal);
    private Slider _strength = null!;
    private Slider _direction = null!;
    private Slider _gusts = null!;

    private bool Vanilla => _strength.IsDefault && _direction.IsDefault && _gusts.IsDefault;

    public override void Build()
    {
        _strength = AddSlider("wind_strength", "WIND STRENGTH", "How hard the wind blows: Batman's cape, loose paper, flags and debris. CALM stops it.",
            1f, 0f, 5f, 0.25f, 3f, v => v <= 0f ? "CALM" : Fmt.Times(v), _ => Apply());
        _strength.Maintain(Apply);
        _direction = AddSlider("wind_direction", "WIND DIRECTION", "Turns the wind away from the way the game has it blowing.",
            0f, -180f, 180f, 15f, 180f, v => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + Fmt.Degrees(v), _ => Apply());
        _direction.Maintain(Apply);
        _gusts = AddSlider("wind_gusts", "GUSTS", "How much the wind changes and gusts around its usual strength.", 1f, 0f, 5f, 0.25f, 3f, Fmt.Times, _ => Apply());
        _gusts.Maintain(Apply);
    }

    public override void WorldChanged()
    {
        _volumes.Clear();
        _sources.Clear();
        _global.Holding = false;
    }

    public override void Shutdown() => Safe.Run("wind restore", Restore);

    private void Apply()
    {
        if (Vanilla)
        {
            Restore();
            return;
        }

        if (World.Info is { } gi)
            Override(_global, ref gi.CurrentWindConfig);

        foreach (RWindVolume v in World.All<RWindVolume>())
        {
            string key = v.GetPathName(null);
            if (!_volumes.TryGetValue(key, out Slot? slot))
                _volumes[key] = slot = new Slot();
            Override(slot, ref v.GeneratedWindConfig);
        }

        // The cape follows these: confirmed in game 2026-11-05. The global config above was zero on that save.
        ScaleSources();
    }

    /// <summary>The engine's directional wind sources (foliage, cloth, particles), scaled by WIND STRENGTH and put back after.</summary>
    private void ScaleSources()
    {
        float k = Math.Max(0f, _strength.Value);
        foreach (WindDirectionalSourceComponent w in World.All<WindDirectionalSourceComponent>())
        {
            string key = w.GetPathName(null);
            if (!_sources.TryGetValue(key, out (float S1, float S2, float W1, float W2) o) || (w.Wind1Strength != o.W1 || w.Wind2Strength != o.W2))
            {
                // A value Oracle didn't write is the game's own: take it as the new original.
                o = (w.Wind1Strength, w.Wind2Strength, 0f, 0f);
            }

            float s1 = o.S1 * k, s2 = o.S2 * k;
            if (w.Wind1Strength != s1)
                w.Wind1Strength = s1;
            if (w.Wind2Strength != s2)
                w.Wind2Strength = s2;
            _sources[key] = (o.S1, o.S2, s1, s2);
        }

    }

    private void RestoreSources()
    {
        if (_sources.Count == 0)
            return;

        foreach (WindDirectionalSourceComponent w in World.All<WindDirectionalSourceComponent>())
        {
            if (!_sources.TryGetValue(w.GetPathName(null), out (float S1, float S2, float W1, float W2) o))
                continue;
            if (w.Wind1Strength == o.W1)
                w.Wind1Strength = o.S1;
            if (w.Wind2Strength == o.W2)
                w.Wind2Strength = o.S2;
        }

        _sources.Clear();
    }

    private void Override(Slot slot, ref WindConfig live)
    {
        if (!slot.Holding || !live.Equals(slot.Written))
            slot.Original = live;

        WindConfig want = Transform(slot.Original);
        if (!live.Equals(want))
            live = want;

        slot.Written = want;
        slot.Holding = true;
    }

    private WindConfig Transform(WindConfig o)
    {
        WindConfig n = o;
        float strength = _strength.Value;
        if (strength <= 0f)
        {
            n.IsApplyWind = false;
            n.WindVelocityCentre = Vector3.Zero;
            n.WindVelocityRadius = 0f;
            n.MaxWindAccelerationRadius = 0f;
            return n;
        }

        Vector3 centre = o.WindVelocityCentre;
        float speed = centre.Length();

        // A still night stays still unless the strength is raised: turning or gusting no wind is nothing.
        if (speed < 1f && strength <= 1f)
            return o;

        Vector3 dir = speed < 1f ? Vector3.UnitX : centre / speed;
        if (speed < 1f)
        {
            speed = BaseSpeed;
            strength -= 1f;
        }

        float turn = _direction.Value * MathF.PI / 180f;
        float c = MathF.Cos(turn), s = MathF.Sin(turn);
        dir = new Vector3(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c, dir.Z);

        float gusts = _gusts.Value;
        n.IsApplyWind = true;
        n.WindVelocityCentre = dir * speed * strength;
        n.WindVelocityRadius = (o.WindVelocityRadius < 1f ? BaseGust : o.WindVelocityRadius) * gusts * MathF.Sqrt(strength);
        n.MaxWindAccelerationRadius = (o.MaxWindAccelerationRadius < 1f ? BaseGust : o.MaxWindAccelerationRadius) * gusts * MathF.Sqrt(strength);
        return n;
    }

    private void Restore()
    {
        RestoreSources();
        if (_global.Holding && World.Info is { } gi)
        {
            ref WindConfig live = ref gi.CurrentWindConfig;
            if (live.Equals(_global.Written))
                live = _global.Original;
        }

        _global.Holding = false;

        if (_volumes.Count == 0)
            return;

        foreach (RWindVolume v in World.All<RWindVolume>())
        {
            if (!_volumes.TryGetValue(v.GetPathName(null), out Slot? slot) || !slot.Holding)
                continue;

            ref WindConfig live = ref v.GeneratedWindConfig;
            if (live.Equals(slot.Written))
                live = slot.Original;
        }

        _volumes.Clear();
    }
}
