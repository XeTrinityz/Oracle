using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// The city's lightning. RLightningActor runs natively; its script functions only raise flags the native tick
/// reads, so a manual strike is a flag write plus the next strike's intensity.
/// </summary>
internal sealed class Lightning : Module
{
    public static readonly Lightning I = new();

    private const string NoticeTitle = "LIGHTNING";
    private const float DefaultStrikeDist = 12288f;
    private const float TraceReach = 30000f;

    private readonly Held<RLightningActor, float> _interval = new("lightning interval", a => a.MaxTimeBetweenStrikes, (a, v) => a.MaxTimeBetweenStrikes = v);
    private readonly Held<RLightningActor, float> _brightness = new("lightning brightness", a => a.MaxLightningBrightness, (a, v) => a.MaxLightningBrightness = v);
    private readonly Held<RLightningActor, float> _strength = new("lightning strength", a => a.MaxLightningStrength, (a, v) => a.MaxLightningStrength = v);
    private readonly Held<RLightningActor, bool> _noRandom = new("lightning off", a => a.bRandomFlashesDisabled, (a, v) => a.bRandomFlashesDisabled = v);
    private Slider _frequency = null!;
    private Slider _bright = null!;
    private bool _reported;

    public override void Build()
    {
        AddCommand("lightning_strike", "LIGHTNING STRIKE", "A bolt of lightning lights up the sky in front of you, with thunder.", () => Strike(null));
        AddCommand("lightning_aim", "STRIKE AT CROSSHAIR", "Lightning strikes whatever the crosshair is on: an enemy, a car, or the street or rooftop you're aiming at.", StrikeAimed);

        _frequency = AddSlider("lightning_frequency", "LIGHTNING FREQUENCY", "How often lightning strikes on its own. 1.00x is the game's storm, OFF stops random lightning (manual strikes still work).",
            1f, 0f, 10f, 0.5f, 4f, v => v <= 0f ? "OFF" : Fmt.Times(v), _ => Apply());
        _frequency.Maintain(Apply);
        _bright = AddSlider("lightning_brightness", "LIGHTNING BRIGHTNESS", "How brightly each flash lights the city.", 1f, 0.25f, 3f, 0.25f, 2f, Fmt.Times, _ => Apply());
        _bright.Maintain(Apply);
    }

    public override void WorldChanged() => _reported = false;

    public override void Shutdown() => Safe.Run("lightning restore", Restore);

    private static RLightningActor? Actor()
    {
        RLightningActor? a = World.Info?.LightningEventListener;
        if (a is { IsValid: true })
            return a;

        foreach (RLightningActor found in World.All<RLightningActor>())
            return found;

        return null;
    }

    private static RLightningActor RequireActor() =>
        Actor() ?? throw new InvalidOperationException("there's no lightning in this area");

    private void Apply()
    {
        bool vanilla = _frequency.IsDefault && _bright.IsDefault;
        if (vanilla)
        {
            Restore();
            return;
        }

        if (Actor() is not { } a)
            return;

        if (!_reported)
        {
            _reported = true;
            Log.Info("Lightning: game values interval " + a.MaxTimeBetweenStrikes + " s, brightness " + a.MaxLightningBrightness + ", strength " + a.MaxLightningStrength + ".");
        }

        // Only a changed frequency overrides the game's own switch, so a brightness change alone keeps a calm night calm.
        float f = _frequency.Value;
        if (f <= 0f || f > 1f)
            _noRandom.Set(a, f <= 0f);
        else
            _noRandom.Restore();

        if (f > 0f && !_frequency.IsDefault)
        {
            _interval.Set(a, o => Math.Max(0.5f, o / f));
            // A shorter interval only matters from the next strike, so pull a far-off one in now.
            if (a.TimeToNextStrike > a.MaxTimeBetweenStrikes)
                a.TimeToNextStrike = a.MaxTimeBetweenStrikes * Random.Shared.NextSingle();
        }
        else
        {
            _interval.Restore();
        }

        float b = _bright.Value;
        _brightness.Set(a, o => o * b);
        _strength.Set(a, o => o * b);
    }

    private void Restore()
    {
        _interval.Restore();
        _brightness.Restore();
        _strength.Restore();
        _noRandom.Restore();
    }

    /// <summary>A strike in front of the camera, at a distance (the game's own default when null).</summary>
    private static void Strike(float? dist)
    {
        RLightningActor a = RequireActor();
        float d = dist ?? (World.Info is { } gi && gi.onScreenJokerfy_LightningDist > 0f ? gi.onScreenJokerfy_LightningDist : DefaultStrikeDist);
        a.ManualTriggerLightingInFrontOfCamera(d);
        Overlay.Toast(NoticeTitle, "STRIKE", true);
    }

    private static void StrikeAimed()
    {
        RLightningActor a = RequireActor();
        Pawn me = World.RequirePawn();
        if (!WorldView.ReadCamera())
            throw new InvalidOperationException("the camera isn't ready");

        Vector3 eye = WorldView.Eye;
        Actor? hit = me.Trace(out Vector3 at, out _, eye + WorldView.Forward * TraceReach, eye, Component.ECollisionFilter.COF_TraceAll, Vector3.Zero, out _, 0);
        if (hit is Pawn { IsValid: true, bDeleteMe: false } target && target.Ptr != me.Ptr && target.Ptr != (World.Player?.Ptr ?? 0))
        {
            a.NextStrikeIntensity = 1f;
            a.ManualTriggerLightningTargetActor(target);
            Overlay.Toast(NoticeTitle, "STRIKE", true);
            return;
        }

        // Scenery and the street: a strike in front of the camera at the distance of whatever was hit.
        Strike(hit != null ? Math.Max(500f, Vector3.Distance(eye, at)) : null);
    }
}
