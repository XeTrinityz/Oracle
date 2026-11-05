using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SYSTEM > GAME: engine settings the game's own options menu doesn't offer. Engine flags are set directly and
/// re-checked each second; render settings go through <see cref="RenderSettings"/>, sent only once a slider has
/// stopped moving (each change can make the renderer rebuild the scene), and again after a load or the pause
/// menu closes, since the game's graphics menu may write its own values back.
/// </summary>
internal sealed class GameTweaks : Module
{
    public static readonly GameTweaks I = new();

    private const double SettleSeconds = 0.4;

    // The game ships bPauseOnLossOfFocus=TRUE in BmEngine.ini (the engine default is FALSE). The native focus-loss
    // handler reads it each time the window loses focus, so clearing it at runtime is enough.
    private static readonly Held<_Engine, bool> FocusPause =
        new("pause on focus loss", e => e.bPauseOnLossOfFocus, (e, v) => e.bPauseOnLossOfFocus = v);

    private static readonly Held<_Engine, bool> PadUnfocused =
        new("controller without focus", e => e.AllowControllerInputWithoutFocus, (e, v) => e.AllowControllerInputWithoutFocus = v);

    // The engine's frame smoothing caps the frame rate alongside the MaxFPS render setting, so both are set.
    private static readonly Held<_Engine, float> SmoothCap =
        new("smoothed frame cap", e => e.MaxSmoothedFrameRate, (e, v) => e.MaxSmoothedFrameRate = v);

    private static readonly Held<_Engine, bool> Smoothing =
        new("frame smoothing", e => e.bSmoothFrameRate, (e, v) => e.bSmoothFrameRate = v);

    private static readonly string[] CapKeys = { "game", "120", "144", "165", "240", "uncapped" };
    private static readonly string[] CapNames = { "GAME", "120", "144", "165", "240", "UNCAPPED" };
    private const float UncappedFps = 1000f;

    private static bool _runUnfocused;
    private static bool _padUnfocused;
    private static float _characterDetail;
    private static int _frameCap;

    private static bool _renderDirty;
    private static double _changedAt;
    private static bool _wasPaused;
    private static Every _engineCheck;
    private static Every _renderCheck;

    public override void Build()
    {
        AddToggle("run_unfocused", "KEEP RUNNING IN BACKGROUND",
            "Stops the game pausing and opening the pause menu when you click or alt-tab to another window. Oracle's keys still only work while the game is in front.",
            on => { _runUnfocused = on; ApplyEngine(); }).Setting();
        AddToggle("pad_unfocused", "CONTROLLER IN BACKGROUND",
            "Lets a controller keep playing the game while another window is in front. Best with KEEP RUNNING IN BACKGROUND on.",
            on => { _padUnfocused = on; ApplyEngine(); }).Setting();

        AddSlider("character_detail", "CHARACTER DETAIL",
            "Keeps characters at their detailed models from further away. The game uses 0.80x. Higher costs frame rate in crowds.",
            0f, 0f, 4f, 0.25f, 2f, v => v <= 0f ? "GAME" : Fmt.Times(v), v => { _characterDetail = v; RenderChanged(); }).Setting();

        AddChoice("frame_cap", "FRAME RATE CAP",
            "Frame rate limits past the game's 30 / 60 / 90. GAME leaves the limit set in the game's own graphics menu.",
            CapKeys, CapNames, i => { _frameCap = i; ApplyEngine(); RenderChanged(); }).Setting();
    }

    private static void RenderChanged()
    {
        _renderDirty = true;
        _changedAt = Clock.Now;
    }

    public override void Started() => Safe.Run("render settings load", RenderSettings.Load);

    public override void WorldChanged() => _renderDirty = true;

    public override void Frame()
    {
        if (_engineCheck.Elapsed(1.0))
            ApplyEngine();

        if (!_renderCheck.Elapsed(0.25))
            return;

        bool paused = World.Paused;
        if (_wasPaused && !paused)
            _renderDirty = true;
        _wasPaused = paused;

        if (_renderDirty && Clock.Now - _changedAt >= SettleSeconds && RenderSettings.Ready)
        {
            _renderDirty = false;
            ApplyRender();
        }
    }

    private static void ApplyEngine()
    {
        _Engine? engine = World.GameEngine;

        Keep(FocusPause, engine, _runUnfocused, false);
        Keep(PadUnfocused, engine, _padUnfocused, true);

        float? cap = Cap();
        Keep(Smoothing, engine, cap == UncappedFps, false);
        if (cap is { } fps && fps < UncappedFps)
            Keep(SmoothCap, engine, true, fps);
        else
            SmoothCap.Restore();
    }

    private static void Keep<T>(Held<_Engine, T> held, _Engine? engine, bool on, T value)
    {
        if (!on)
            held.Restore();
        else if (engine != null)
            held.Set(engine, value);
    }

    private static float? Cap() => _frameCap switch
    {
        <= 0 => null,
        _ when CapKeys[Math.Clamp(_frameCap, 0, CapKeys.Length - 1)] == "uncapped" => UncappedFps,
        _ => float.Parse(CapKeys[Math.Clamp(_frameCap, 0, CapKeys.Length - 1)], System.Globalization.CultureInfo.InvariantCulture),
    };

    // Settings Oracle no longer changes. MaxDrawDistanceScale did nothing even with every object re-attached:
    // the open world draws through its own streaming and LOD scenery. A crash while one was on leaves it live
    // (the ini is already repaired at start), so put it back.
    private static readonly string[] Retired = { "MaxDrawDistanceScale" };

    private static void ApplyRender()
    {
        Render("SkeletalMeshDisplayFactorScale", _characterDetail > 0f ? _characterDetail : null);
        Render("MaxFPS", Cap());

        foreach (string name in Retired)
            Render(name, null);
    }

    private static void Render(string name, float? value)
    {
        if (value is { } v)
            Safe.Run("render " + name, () => RenderSettings.Override(name, v));
        else
            Safe.Run("render " + name + " restore", () => RenderSettings.Restore(name));
    }

    public override void Shutdown()
    {
        FocusPause.Restore();
        PadUnfocused.Restore();
        SmoothCap.Restore();
        Smoothing.Restore();
        Safe.Run("render restore", RenderSettings.RestoreAll);
    }
}
