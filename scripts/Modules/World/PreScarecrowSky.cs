using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// The sky half of PRE-SCARECROW ERA. The Gordon section draws its own sky: the dome mesh XFX_Merter.dome.Dome_Rise
/// (sky material DOME_R_M plus cloud layers) on RStaticMeshToggleable_3, a sun sphere using sun_M in the chapter's
/// StaticMeshCollectionActor, and a LensFlareSource for the sun's glow. All are cooked only into that chapter's map
/// package, CityZ_10_Ch2C. This loads the package into memory without adding it to the world (so its scripts and
/// characters never start), then spawns Oracle's own copies at the same place; going off destroys them.
/// No menu row of its own; <see cref="PreScarecrowEra"/> switches it.
/// </summary>
internal sealed class PreScarecrowSky : Module
{
    public static readonly PreScarecrowSky I = new();

    private const string Package = "CityZ_10_Ch2C";
    private const string DomeMesh = "XFX_Merter.dome.Dome_Rise";
    private const string SunMaterial = "XFX_Merter.dome.sun_M";
    private const string SunCollection = "StaticMeshCollectionActor_7042";

    private readonly List<Actor> _spawned = new();
    private bool _on;
    private bool _dirty;
    private bool _failed;

    public void Set(bool on)
    {
        if (_on == on)
            return;
        _on = on;
        _failed = false;
        _dirty = true;
    }

    public override void WorldChanged()
    {
        // The world is going; our actors go with it. Spawn again once the next one is up.
        _spawned.Clear();
        _dirty = true;
    }

    public override void Shutdown() => Safe.Run("era sky remove", Remove);

    public override void Tick(float dt)
    {
        if (!_dirty)
            return;
        _dirty = false;

        if (!_on)
        {
            Remove();
            return;
        }

        if (_failed || _spawned.Any(a => a.IsValid))
            return;

        Safe.Run("era sky", Spawn);
    }

    private void Remove()
    {
        foreach (Actor a in _spawned)
        {
            if (a.IsValid)
                a.Destroy();
        }

        _spawned.Clear();
    }

    private void Spawn()
    {
        StaticMesh? dome = Game.FindObject<StaticMesh>(DomeMesh);
        if (dome == null)
        {
            Log.Info("PRE-SCARECROW ERA sky: loading " + Package + " for its sky.");
            Game.LoadPackage(Package);
            dome = Game.FindObject<StaticMesh>(DomeMesh);
        }

        MaterialInterface? sun = Game.FindObject<MaterialInterface>(SunMaterial);
        if (dome == null)
        {
            Fail("the dusk sky couldn't be loaded");
            return;
        }

        StaticMeshComponent? domeSource = null;
        StaticMeshComponent? sunSource = null;
        foreach (StaticMeshComponent c in Game.FindObjects<StaticMeshComponent>())
        {
            if (!c.IsValid || c.IsClassDefaultObject)
                continue;

            // The cheap reads rule out nearly every component in the city before its path (an UnrealScript call) is asked.
            bool isDome = c.StaticMesh is { IsValid: true } m && m.Ptr == dome.Ptr;
            if (!isDome && c.Outer is not StaticMeshCollectionActor)
                continue;

            string path = c.GetPathName(null);
            if (!path.StartsWith(Package + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            if (isDome)
                domeSource = c;
            else if (path.Contains(SunCollection, StringComparison.OrdinalIgnoreCase))
                sunSource = c; // the sun sphere; its material override isn't readable until attached, so match by place
        }

        if (domeSource == null)
        {
            Fail("the dusk sky's placement wasn't found");
            return;
        }

        Copy("dome", domeSource);
        if (sunSource != null)
        {
            Copy("sun", sunSource);

            // The sphere's own material is a fog shader; the chapter overrides it with sun_M (read offline).
            if (sun != null && _spawned.LastOrDefault() is DynamicSMActor sphere && sphere.StaticMeshComponent is { IsValid: true } sc)
                sc.SetMaterial(0, sun);
        }
        else
        {
            Log.Info("PRE-SCARECROW ERA sky: no sun piece found (sun material " + (sun == null ? "not loaded" : "loaded") + ").");
        }

        Safe.Run("era sky flare", Flare);
    }

    // The sun's glow: a LensFlareSource far out along the sun's direction (it reuses the Batmobile brake-light flare).
    private void Flare()
    {
        LensFlareSource? src = null;
        foreach (LensFlareSource f in Game.FindObjects<LensFlareSource>())
        {
            if (f.IsValid && !f.IsClassDefaultObject && f.GetPathName(null).StartsWith(Package + ".", StringComparison.OrdinalIgnoreCase))
                src = f;
        }

        if (src?.LensFlareComp is not { IsValid: true } comp || comp.Template is not { IsValid: true } template)
        {
            Log.Info("PRE-SCARECROW ERA sky: no sun flare found.");
            return;
        }

        if (Game.SpawnActor(LensFlareSource.StaticClass(), default, src.Location, src.Rotation) is not LensFlareSource flare || flare.LensFlareComp is not { IsValid: true } dst)
        {
            Log.Info("PRE-SCARECROW ERA sky: the sun flare couldn't be spawned (the class may not allow it).");
            return;
        }

        dst.SetTemplate(template, true);
        dst.SetIsActive(true);
        flare.SetDrawScale(src.DrawScale);
        flare.SetDrawScale3D(src.DrawScale3D);
        _spawned.Add(flare);
    }

    private void Fail(string why)
    {
        _failed = true;
        Log.Info("PRE-SCARECROW ERA sky: " + why + ".");
        Overlay.Notice("PRE-SCARECROW ERA", why.ToUpperInvariant(), false);
    }

    // A level's own components carry their placement either through their actor, or (in a StaticMeshCollectionActor)
    // as a cached matrix of their own.
    private void Copy(string what, StaticMeshComponent src)
    {
        Vector3 location;
        Rotator rotation;
        Vector3 scale;

        // A package loaded into memory but not into the world never attaches its components, so Owner and the cached
        // matrices are empty. The placement is still in the saved fields: an ordinary actor carries it (the dome's
        // RStaticMeshToggleable_3: DrawScale 2 x 1.76), while a StaticMeshCollectionActor sits at the origin and each
        // of its components carries its own world placement in Translation / Rotation / Scale3D (the sun: 19.74).
        Actor? owner = src.Owner is { IsValid: true } o ? o : src.Outer as Actor;
        if (owner is { IsValid: true } and not StaticMeshCollectionActor)
        {
            location = owner.Location + src.Translation;
            rotation = owner.Rotation;
            scale = owner.DrawScale3D * owner.DrawScale * src.Scale3D * src.Scale;
        }
        else
        {
            location = src.Translation;
            rotation = src.Rotation;
            scale = src.Scale3D * src.Scale;
        }

        if (Game.SpawnActor(DynamicSMActor_Spawnable.StaticClass(), default, location, rotation) is not DynamicSMActor actor)
        {
            Log.Info("PRE-SCARECROW ERA sky: the " + what + " couldn't be spawned.");
            return;
        }

        actor.SetCollision(false, false, false);
        actor.SetDrawScale3D(scale);

        StaticMeshComponent dst = actor.StaticMeshComponent;
        dst.CastShadow = false;
        dst.SetStaticMesh(src.StaticMesh, true);
        int count = Math.Max(0, src.GetNumElements());
        for (int i = 0; i < count; i++)
        {
            MaterialInterface mat = src.GetMaterial(i);
            if (mat is { IsValid: true })
                dst.SetMaterial(i, mat);
        }

        _spawned.Add(actor);
    }
}
