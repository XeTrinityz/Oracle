using System.Diagnostics;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// SYSTEM > GAME > BUILDING DETAIL. AK's MassiveLOD swaps whole buildings for low-detail stand-ins (the city's
/// _LOD1 / _LOD2 levels) once the camera is past each object's MassiveLODDistance. Scaling that distance keeps
/// the real meshes from further away, as long as their district is loaded. The distance lives on every primitive,
/// so all loaded primitives are scanned, and scanned again now and then for districts that stream in.
/// The game's values live only in memory, so nothing is left behind if the game closes.
/// </summary>
internal sealed class BuildingDetail : Module
{
    public static readonly BuildingDetail I = new();

    private const double SettleSeconds = 0.4;
    // A full scan measured 2 ms over 23k primitives (2026-11-05), so a frequent rescan is cheap.
    private const double MinRescanSeconds = 5.0;

    private sealed class Entry
    {
        public required PrimitiveComponent Comp;
        public float Original;
        public float Current;
    }

    private static readonly Dictionary<nint, Entry> Held = new();
    private static float _scale;
    private static bool _dirty;
    private static double _changedAt;
    private static double _nextScan;
    private static int _scans;

    public override void Build()
    {
        AddSlider("building_detail", "BUILDING DETAIL",
            "Keeps buildings at their full models from further away, instead of the rough stand-ins the city swaps in at a distance. Only works where the district is loaded; costs frame rate and a short stutter when changed.",
            0f, 0f, 8f, 0.5f, 3f, v => v <= 0f ? "GAME" : Fmt.Times(v), v =>
            {
                _scale = v;
                _dirty = true;
                _changedAt = Clock.Now;
            }).Setting();
    }

    public override void WorldChanged()
    {
        // Every component is about to be destroyed; nothing to put back.
        Held.Clear();
        _dirty = true;
    }

    public override void Tick(float dt)
    {
        double now = Clock.Now;
        if (_dirty)
        {
            if (now - _changedAt < SettleSeconds)
                return;
            _dirty = false;
            Scan();
        }
        else if (_scale > 0f && now >= _nextScan)
        {
            Scan();
        }
    }

    public override void Shutdown()
    {
        _scale = 0f;
        Safe.Run("building detail restore", Scan);
    }

    private static void Scan()
    {
        var watch = Stopwatch.StartNew();
        int seen = 0, withLod = 0, parents = 0, replaced = 0, changed = 0;
        var distances = new List<float>();

        foreach (PrimitiveComponent p in Game.FindObjects<PrimitiveComponent>())
        {
            if (!p.IsValid || p.IsClassDefaultObject)
                continue;

            seen++;
            float d = p.MassiveLODDistance;
            if (d <= 0f)
                continue;

            withLod++;
            if (_scans == 0)
            {
                distances.Add(d);
                if (p.MassiveLODAttachedPrimitives > 0)
                    parents++;
                if (p.ReplacementPrimitive is { IsValid: true })
                    replaced++;
            }

            // A pointer seen before but holding a value Oracle didn't set is a new object (or the game changed it).
            if (!Held.TryGetValue(p.Ptr, out Entry? e) || MathF.Abs(d - e.Current) > 0.5f)
            {
                e = new Entry { Comp = p, Original = d, Current = d };
                Held[p.Ptr] = e;
            }

            float want = _scale > 0f ? e.Original * _scale : e.Original;
            if (MathF.Abs(d - want) <= 0.5f)
                continue;

            p.MassiveLODDistance = want;
            e.Current = want;
            if (p.bAttached)
                p.ForceUpdate(false);
            changed++;
        }

        if (_scale <= 0f)
            Held.Clear();

        double ms = watch.Elapsed.TotalMilliseconds;
        _nextScan = Clock.Now + Math.Max(MinRescanSeconds, ms / 1000.0 * 200.0);

        if (_scans == 0 && distances.Count > 0)
        {
            distances.Sort();
            Log.Info($"BUILDING DETAIL: {seen} primitives, {withLod} with a MassiveLOD distance ({parents} stand-ins with children, {replaced} with a stand-in); " +
                     $"distance min {distances[0]:0} median {distances[distances.Count / 2]:0} max {distances[^1]:0}.");
        }

        if (_scans == 0 || changed > 0)
            Log.Info($"BUILDING DETAIL: scale {(_scale > 0f ? _scale.ToString("0.0#") : "GAME")}, changed {changed} in {ms:0} ms; next scan in {_nextScan - Clock.Now:0} s.");

        _scans++;
    }
}
