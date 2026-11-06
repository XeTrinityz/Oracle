using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Batman's Joker hallucinations in the open world: billboards that turn into Joker, street thugs
/// who wear his face, and how often the game does it by itself. All of it is cosmetic.
/// </summary>
internal sealed class JokerVisions : Module
{
    public static readonly JokerVisions I = new();

    private const string NoticeTitle = "JOKER";
    private const double SweepSeconds = 2.0;
    private const double PairSeconds = 15.0;
    private const string DesignPackage = "City_BillBoard_Joker.Materials.";
    private const int ThugLimit = 6;
    private const float ThugRange = 6000f;
    private const double JokerSeconds = 10.0;
    private const float NeverSeconds = 1e7f;

    private readonly List<RJokerBillboard> _swapped = new();
    private readonly HashSet<nint> _swappedPtrs = new();
    private readonly HashSet<nint> _keepFlag = new();
    private readonly Dictionary<(nint Comp, int Slot), MaterialInterface> _painted = new();
    private readonly Dictionary<StaticMesh, bool> _billboardMesh = new(ReferenceEqualityComparer.Instance);
    private readonly List<(RBMPawnAI Pawn, RRiotBrain Brain, double Until)> _jokers = new();
    private readonly Held<RGameInfo, float> _ch3 = Chance("Ch3", g => g.jokerSwappableChance_Ch3, (g, v) => g.jokerSwappableChance_Ch3 = v);
    private readonly Held<RGameInfo, float> _ch5 = Chance("Ch5", g => g.jokerSwappableChance_Ch5, (g, v) => g.jokerSwappableChance_Ch5 = v);
    private readonly Held<RGameInfo, float> _ch7 = Chance("Ch7", g => g.jokerSwappableChance_Ch7, (g, v) => g.jokerSwappableChance_Ch7 = v);
    private readonly Held<RGameInfo, float> _ch9 = Chance("Ch9", g => g.jokerSwappableChance_Ch9, (g, v) => g.jokerSwappableChance_Ch9 = v);
    private readonly Held<RGameInfo, float> _interval = new("joker interval", g => g.onScreenJokerfy_MinInterval, (g, v) => g.onScreenJokerfy_MinInterval = v);
    private Dictionary<nint, MaterialInterface> _pairs = new();
    private Slider _sightings = null!;
    private Every _sweep;
    private Every _repair;

    private static Held<RGameInfo, float> Chance(string ch, Func<RGameInfo, float> get, Action<RGameInfo, float> set) => new("joker chance " + ch, get, set);

    public override void Build()
    {
        AddToggle("joker_city", "JOKERFY THE CITY", "Every billboard and poster around you that has a Joker version turns into it and stays that way. Statues and the animated screens aren't included (the screens have no Joker version): raise JOKER SIGHTINGS for statues. Switch it off to put the billboards back.",
            on => { if (!on) Restore(); }).Maintain(KeepJokerfied);
        AddCommand("joker_thugs", "JOKER THUGS", "The thugs you can see, up to 6 within 60 m, wear Joker's face for 10 seconds, the way they do in Batman's hallucinations.", JokerThugs);
        _sightings = AddSlider("joker_sightings", "JOKER SIGHTINGS", "How often billboards and statues turn into Joker by themselves, and how soon a lightning flash can reveal one again. NONE stops them.",
            1f, 0f, 5f, 0.25f, 4f, v => v <= 0f ? "NONE" : Fmt.Times(v), _ => ApplySightings());
        _sightings.Maintain(ApplySightings);
    }

    public override void Tick(float dt)
    {
        if (_jokers.Count > 0)
            ReleaseJokers(all: false);
    }

    public override void WorldChanged()
    {
        _jokers.Clear();
        _swapped.Clear();
        _swappedPtrs.Clear();
        _keepFlag.Clear();
        _painted.Clear();
        _pairs = new();
        _billboardMesh.Clear();
    }

    public override void Shutdown()
    {
        Safe.Run("joker billboards", Restore);
        Safe.Run("joker thugs", () => ReleaseJokers(all: true));
        Safe.Run("joker sightings", RestoreSightings);
    }

    // ---- billboards

    /// <summary>
    /// Billboards go through the game's own unforced swap (SwapToJokerVersion(false), the call its hallucinations
    /// make), with bUnswapAutomatically cleared so the game doesn't turn them back about half a second after
    /// they're seen. The game still swaps some back now and then (its random offscreen swaps, a lightning reveal
    /// undoing its pick), so every swapped billboard is checked each quarter second and swapped again.
    ///
    /// History (2026-11-05), so none of it is tried again: forcing the swap (SwapToJokerVersion(true)) froze the
    /// game four times (Application Hang), apparently because the game kept trying to undo a swap that refused.
    /// Setting the Joker material directly on an RJokerBillboard was undone by the game within half a second, so
    /// it never showed. Painting by material pair is kept only for billboard meshes the game doesn't manage. The
    /// animated screens (RTakeoverVideoScreen, City_Lightbox materials) have no Joker version.
    /// </summary>
    private void KeepJokerfied()
    {
        for (int i = _swapped.Count - 1; i >= 0; i--)
        {
            RJokerBillboard b = _swapped[i];
            if (!b.IsValid || b.bDeleteMe)
            {
                _swappedPtrs.Remove(b.Ptr);
                _swapped.RemoveAt(i);
                continue;
            }

            if (!b.bCurrentlySwapped && !b.bCurrentlyForced)
                Swap(b);
        }

        if (!_sweep.Elapsed(SweepSeconds))
            return;

        foreach (RJokerBillboard b in World.All<RJokerBillboard>())
        {
            // One the game forced itself belongs to a story beat: leave it be.
            if (b.bDeleteMe || b.bCurrentlyForced || _swappedPtrs.Contains(b.Ptr) || b.AlternateMaterial is not { IsValid: true }
                || b.StaticMeshComponent is not { IsValid: true })
                continue;

            if (!b.bCurrentlySwapped)
                Swap(b);
            _swapped.Add(b);
            _swappedPtrs.Add(b.Ptr);
        }

        PaintOthers();
    }

    private void Swap(RJokerBillboard b)
    {
        if (b.bUnswapAutomatically)
        {
            b.bUnswapAutomatically = false;
            _keepFlag.Add(b.Ptr);
        }

        b.SwapToJokerVersion(false);
    }

    /// <summary>
    /// Billboard meshes outside the game's Joker billboards: any slot showing a design's normal material
    /// (City_Billboard_Joker_[Design]A_INST) gets its loaded Joker partner (.Joker.City_Billboard_Joker_[Design]B_INST).
    /// </summary>
    private void PaintOthers()
    {
        if (_pairs.Count == 0 || _repair.Elapsed(PairSeconds))
            _pairs = Pairs();
        if (_pairs.Count == 0)
            return;

        foreach (StaticMeshComponent c in World.All<StaticMeshComponent>())
        {
            if (c.Owner is RJokerBillboard || c.StaticMesh is not { IsValid: true } mesh || !IsBillboard(mesh))
                continue;

            int n = Math.Min(c.GetNumElements(), 8);
            for (int i = 0; i < n; i++)
            {
                if (c.GetMaterial(i) is not { IsValid: true } now || !_pairs.TryGetValue(now.Ptr, out MaterialInterface? joker))
                    continue;

                c.SetMaterial(i, joker);
                _painted[(c.Ptr, i)] = now;
            }
        }
    }

    /// <summary>
    /// Whether a mesh is a billboard, remembered per mesh: thousands of components share a few meshes, and reading
    /// every component's mesh name each sweep built a string per component in the city.
    /// </summary>
    private bool IsBillboard(StaticMesh mesh)
    {
        if (!_billboardMesh.TryGetValue(mesh, out bool yes))
            _billboardMesh[mesh] = yes = mesh.Name.ToString().Contains("Billboard", StringComparison.OrdinalIgnoreCase);
        return yes;
    }

    /// <summary>Normal design material (A) to its Joker partner (B), for the pairs whose both halves are loaded.</summary>
    private static Dictionary<nint, MaterialInterface> Pairs()
    {
        var byName = new Dictionary<string, MaterialInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialInterface m in World.All<MaterialInterface>())
        {
            string path = m.GetPathName(null);
            if (path.StartsWith(DesignPackage, StringComparison.OrdinalIgnoreCase))
                byName[path] = m;
        }

        var pairs = new Dictionary<nint, MaterialInterface>();
        foreach ((string path, MaterialInterface normal) in byName)
        {
            string name = path[(path.LastIndexOf('.') + 1)..];
            if (!name.EndsWith("A_INST", StringComparison.OrdinalIgnoreCase) || path.Contains(".Joker.", StringComparison.OrdinalIgnoreCase))
                continue;

            if (byName.TryGetValue(DesignPackage + "Joker." + name[..^"A_INST".Length] + "B_INST", out MaterialInterface? joker))
                pairs[normal.Ptr] = joker;
        }

        return pairs;
    }

    private void Restore()
    {
        _sweep.Force();
        foreach (RJokerBillboard b in _swapped)
        {
            if (!b.IsValid || b.bDeleteMe)
                continue;

            if (b.bCurrentlySwapped && !b.bCurrentlyForced)
                b.SwapToOriginalVersion(false);
            if (_keepFlag.Contains(b.Ptr))
                b.bUnswapAutomatically = true;
        }

        if (_painted.Count > 0)
        {
            foreach (StaticMeshComponent c in World.All<StaticMeshComponent>())
            {
                if (c.Owner is RJokerBillboard)
                    continue;

                int n = Math.Min(c.GetNumElements(), 8);
                for (int i = 0; i < n; i++)
                {
                    if (_painted.TryGetValue((c.Ptr, i), out MaterialInterface? normal) && normal.IsValid)
                        c.SetMaterial(i, normal);
                }
            }
        }

        _swapped.Clear();
        _swappedPtrs.Clear();
        _keepFlag.Clear();
        _painted.Clear();
        _pairs = new();
    }

    // ---- thugs

    /// <summary>
    /// Puts Joker's face on the thugs you can see, through each thug's own RRiotBrain.BecomeJoker, and turns them
    /// back after a while. The game's SpawnJokersFromWanderers only takes non-militia wandering thugs seen in the
    /// last 0.3 s, so it found none on a late-story save full of militia. BecomeJoker hides the thug's own mesh even
    /// when its JokerDefine has no mesh to show, so only thugs with a Joker mesh are picked.
    /// </summary>
    private void JokerThugs()
    {
        Transition.RequireIdle();
        if (World.Pawn is not { } me || World.WorldInfo is not { } wi)
            throw new InvalidOperationException("the game is still loading");

        // BecomeNormalStatic strips as many extra meshes as the class default's JokerDefine has, so only a thug
        // whose own JokerDefine adds that many can be turned back cleanly.
        int meshes = RRiotBrain.DefaultObject is { IsValid: true } def && def.JokerDefine is { IsValid: true } dj ? dj.CharacterMeshes.Count : -1;
        if (meshes <= 0)
            throw new InvalidOperationException("the Joker look isn't loaded here");

        float now = wi.TimeSeconds;
        var picks = World.All<RBMPawnAI>()
            .Where(p => !p.bDeleteMe && !p.bHidden && p.Health > 0 && p is not RPawnVillain { bGoodAsDead: true }
                && p.Controller is RBMAIController { RiotBrain: { IsValid: true, bIsJokerfied: false } brain }
                && brain.JokerDefine is { IsValid: true } joker && joker.CharacterMeshes.Count == meshes
                && p.Mesh is { IsValid: true } mesh && now - mesh.LastRenderTime < 0.5f
                && Vector3.DistanceSquared(p.Location, me.Location) < ThugRange * ThugRange)
            .OrderBy(p => Vector3.DistanceSquared(p.Location, me.Location))
            .Take(ThugLimit)
            .ToList();

        if (picks.Count == 0)
            throw new InvalidOperationException("no thugs in view");

        foreach (RBMPawnAI p in picks)
        {
            if (p.Controller is not RBMAIController { RiotBrain: { IsValid: true } brain })
                continue;

            brain.BecomeJoker(p);
            _jokers.Add((p, brain, Clock.Now + JokerSeconds));
        }

        Overlay.Toast(NoticeTitle, "HA HA HA", true);
    }

    /// <summary>Turns the Joker thugs back once their time is up, and all of them on shutdown.</summary>
    private void ReleaseJokers(bool all)
    {
        double now = Clock.Now;
        for (int i = _jokers.Count - 1; i >= 0; i--)
        {
            (RBMPawnAI p, RRiotBrain brain, double until) = _jokers[i];
            if (!all && now < until)
                continue;

            _jokers.RemoveAt(i);
            if (p.IsValid && brain.IsValid && brain.bIsJokerfied)
                Safe.Run("joker thug back", () => brain.BecomeNormal(p));
        }
    }

    // ---- sightings

    private void ApplySightings()
    {
        if (_sightings.IsDefault)
        {
            RestoreSightings();
            return;
        }

        if (World.Info is not { } gi)
            return;

        float v = _sightings.Value;
        foreach (Held<RGameInfo, float> h in new[] { _ch3, _ch5, _ch7, _ch9 })
            h.Set(gi, o => Math.Clamp(o * v, 0f, 1f));
        _interval.Set(gi, o => v <= 0f ? NeverSeconds : o / v);
    }

    private void RestoreSightings()
    {
        _ch3.Restore();
        _ch5.Restore();
        _ch7.Restore();
        _ch9.Restore();
        _interval.Restore();
    }
}
