using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>What a spawn is, for the SPAWNED list and for how it is cleaned up.</summary>
internal enum SpawnKind
{
    Character,
    Vehicle,
    Object,
    Weapon,
}

/// <summary>
/// SPAWNER: puts new characters, vehicles, objects and weapons into the world. Everything is built the way the game
/// builds its own (the population manager's SpawnPawn, archetype spawns, the weapon drop a disarm leaves), from what
/// the game has loaded right now, so nothing is spawned that the area can't draw. Every spawn is tracked: the SPAWNED
/// tab lists them, hands one to the world editor, and deletes them. Nothing the game placed is ever deleted here.
/// </summary>
internal sealed partial class Spawner : Module
{
    public static readonly Spawner I = new();

    private const string NoticeTitle = "SPAWNER";
    private const float Metre = 100f;
    private const float CrosshairReach = 30000f;
    private const float GroundReach = 6000f;
    private const float GroundAbove = 300f;
    private const double UpkeepSeconds = 0.5;
    private const double SteerDelay = 0.6;
    private const int ListMax = 60;

    private static readonly FName Reason = new("OracleSpawner");

    private sealed class Spawn
    {
        public required Actor Actor;
        public required nint Ptr;
        public required nint ClassPtr;
        public required SpawnKind Kind;
        public required string Label;
        public required int Number;
        public Vector3 Origin;
        public double Born;
        public bool Settling;
        public bool Passive;
        public bool Bodyguard;
        public double DownSince = -1;

        // A vehicle's behaviour and side, applied once its own start-up has run (see Steer).
        public bool Pending;
        public string Behaviour = "";
        public bool Ally;

        /// <summary>Still the same live object: a freed pointer can be reused, so the class is checked too.</summary>
        public bool Alive => Actor is { IsValid: true, bDeleteMe: false } && Actor.Class is { } c && c.Ptr == ClassPtr;
    }

    private readonly record struct Spot(Vector3 Floor, Rotator Facing);

    private readonly List<Spawn> _spawns = new();
    private int _numbered;
    private Every _upkeep;

    // ---- placement, shared by every tab
    private Choice _where = null!;
    private Slider _distance = null!;
    private Slider _count = null!;
    private Slider _spread = null!;
    private Choice _facing = null!;

    // ---- the SPAWNED tab
    private Choice _list = null!;
    private Toggle _selectNew = null!;
    private Slider _max = null!;
    private Toggle _clearFallen = null!;
    private Slider _clearAfter = null!;
    private Slider _clearFar = null!;

    public override void Build()
    {
        BuildCharacters();
        BuildVehicles();
        BuildObjects();
        BuildWeapons();
        BuildMarkers();

        _where = AddChoice("spawn_where", "WHERE", "IN FRONT puts spawns on the ground ahead of you, DISTANCE away. CROSSHAIR puts them where you're aiming, on the floor there or against the wall.",
            new[] { "front", "crosshair" }, new[] { "IN FRONT", "CROSSHAIR" }).Pref();
        _distance = AddSlider("spawn_distance", "DISTANCE", "How far in front of you spawns appear when WHERE is IN FRONT.", 6f, 2f, 40f, 1f, 15f, Fmt.Metres).Pref();
        _count = AddSlider("spawn_count", "HOW MANY", "How many copies each SPAWN makes. They stand in a block, SPACING apart.", 1f, 1f, 12f, 1f, 5f, Fmt.Count).Pref();
        _spread = AddSlider("spawn_spread", "SPACING", "The gap between copies when HOW MANY is more than one. Big things are kept apart by their size anyway.", 2f, 1f, 15f, 0.5f, 5f, v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " M").Pref();
        _facing = AddChoice("spawn_facing", "FACING", "Which way spawns face: toward you, the way you're facing, or any way.",
            new[] { "you", "same", "random" }, new[] { "TOWARD YOU", "YOUR WAY", "RANDOM" }).Pref();

        AddCommand("spawn_copy", "SPAWN COPY OF SELECTED", "Spawns copies of whatever the world editor has selected: the same character, vehicle or object, placed the way this tab says. Select something with SELECT AIMED first.", SpawnCopy);

        _list = AddChoice("spawned_pick", "SPAWNED", "Everything you've spawned that's still in the world, newest first. Hold to scroll.",
            new[] { "" }, new[] { "NOTHING YET" }).Pref().Unsaved();
        _list.HoldToScroll = true;
        AddCommand("spawned_edit", "EDIT IT", "Hands the spawn above to the world editor, selected: GRAB, nudge, freeze or restyle it there.", EditPicked);
        AddCommand("spawned_go", "GO TO IT", "Puts you beside the spawn above.", GoToPicked).Moves();
        AddCommand("spawned_delete", "DELETE IT", "Takes the spawn above out of the world for good.", DeletePicked);
        AddCommand("spawned_delete_last", "DELETE LAST", "Takes your most recent spawn out of the world.", DeleteLast);
        AddCommand("spawned_delete_all", "DELETE EVERYTHING SPAWNED", "Takes everything you've spawned out of the world. Only spawns: nothing the game placed is touched.", DeleteAll);
        _selectNew = AddToggle("spawn_select_new", "SELECT WHAT YOU SPAWN", "The newest spawn becomes the world editor's selection, ready to GRAB or edit.", on: true).Pref();
        _clearFallen = AddToggle("spawn_clear_fallen", "CLEAR THE FALLEN", "Knocked-out characters and destroyed vehicles you spawned are taken away AFTER this long, so they don't pile up.", on: true).Pref();
        _clearAfter = AddSlider("spawn_clear_after", "AFTER", "How long a fallen spawn stays before CLEAR THE FALLEN takes it away.", 30f, 5f, 180f, 5f, 10f, Fmt.Secs).Pref();
        _clearFar = AddSlider("spawn_clear_far", "CLEAR FAR AWAY", "Spawns this far from you are taken away, so you can spawn and move on. Bodyguards never are: they follow you.", 300f, 0f, 1000f, 50f, 150f, Fmt.MetresOrOff).Pref();
        _max = AddSlider("spawn_max", "MOST AT ONCE", "The most spawns kept in the world at once. Lots of characters and vehicles slow the game down; SPAWN refuses past this.", 40f, 5f, 150f, 5f, 80f, Fmt.Count).Pref();
    }

    // ---- lifecycle

    public override void WorldChanged() => Forget();

    public override void LeftGame() => Forget();

    /// <summary>A new world: everything spawned went with the old one.</summary>
    private void Forget()
    {
        _spawns.Clear();
        _guardPtrs.Clear();
        ForgetCatalogs();
        RefreshList();
    }

    public override void Shutdown() => Safe.Run("spawner cleanup", () => DeleteWhere(_ => true));

    public const string PageTitle = "SPAWNER";

    /// <summary>The menu is showing one of the SPAWNER tabs, so its lists should be current.</summary>
    private static bool Showing => Menu.IsOpen && !Menu.Searching && Menu.Top.Title == PageTitle;

    public override void MenuOpening()
    {
        Prune();
        RefreshList();
    }

    public override void Tick(float dt)
    {
        if (!World.InGame || !_upkeep.Elapsed(UpkeepSeconds))
            return;

        // A list is read when its tab is opened (see RefreshTab), not on every menu open or on a timer.
        string tab = Showing ? Menu.Current.Title : "";
        if (tab != _shownTab)
        {
            _shownTab = tab;
            if (tab.Length > 0)
                Safe.Run("spawner lists", () => RefreshTab(tab));
        }
        if (_spawns.Count == 0)
            return;

        Prune();
        foreach (Spawn s in _spawns)
        {
            if (s.Pending && Clock.Now - s.Born >= SteerDelay && s.Actor is RVehicleNPC v0)
            {
                s.Pending = false;
                Safe.Run("spawned vehicle steer", () => Steer(v0, s.Behaviour, s.Ally));
            }
            if (s.Settling)
                Safe.Run("spawned settle", () => Settle(s));
            if (s.Passive && s.Actor is RPawnVillain v)
                Safe.Run("spawned calm", () => KeepCalm(v));
        }

        Safe.Run("spawn clear-up", ClearUp);
    }

    /// <summary>Knocked out, or a destroyed vehicle. Objects and weapons never fall.</summary>
    private static bool Fallen(Spawn s) => s.Actor switch
    {
        RPawnVillain v => v.Health <= 0 || v.bGoodAsDead,
        Pawn p => p.Health <= 0,
        _ => false,
    };

    /// <summary>
    /// CLEAR THE FALLEN and CLEAR FAR AWAY: spawns down for AFTER seconds, or further from you than the limit
    /// (bodyguards excepted), are deleted the way DELETE IT does it.
    /// </summary>
    private void ClearUp()
    {
        Pawn? me = World.Pawn;
        double now = Clock.Now;
        float far = _clearFar.Value * Metre;
        var gone = new List<Spawn>();
        int fallen = 0;
        foreach (Spawn s in _spawns)
        {
            if (!s.Alive)
                continue;

            if (_clearFallen.On && Fallen(s))
            {
                if (s.DownSince < 0)
                    s.DownSince = now;
                else if (now - s.DownSince >= _clearAfter.Value)
                {
                    gone.Add(s);
                    fallen++;
                }

                continue;
            }

            s.DownSince = -1;
            if (me != null && far > 0f && !s.Bodyguard && Vector3.DistanceSquared(s.Actor.Location, me.Location) > far * far)
                gone.Add(s);
        }

        if (gone.Count == 0)
            return;

        foreach (Spawn s in gone)
        {
            Safe.Run("spawn clear-up delete", () => Delete(s));
            _spawns.Remove(s);
        }

        RebuildGuards();
        RefreshList();
        Log.Info("Spawner: cleared " + gone.Count + " spawn" + (gone.Count == 1 ? "" : "s") + " (" + fallen + " fallen, " + (gone.Count - fallen) + " far away).");
    }

    /// <summary>The game re-riles a calmed pawn when it's hit or sees a fight; a PASSIVE spawn is calmed again.</summary>
    private static void KeepCalm(RPawnVillain v)
    {
        if (v.Health > 0 && !v.bGoodAsDead && v.IsHostile())
            v.SetHostile(false, Reason);
    }

    private void Prune()
    {
        int before = _spawns.Count;
        _spawns.RemoveAll(s => !s.Alive);
        if (_spawns.Count == before)
            return;
        RebuildGuards();
        if (Menu.IsOpen)
            RefreshList();
    }

    // ---- shared checks

    /// <summary>Refuses where a spawn could break something: menus, cutscenes, loading, a teleport underway.</summary>
    private static Pawn RequireReady()
    {
        Transition.RequireIdle();
        Pawn me = World.RequirePawn();
        if (World.Pc is { } pc && CameraRig.InCinematic(pc))
            throw new InvalidOperationException("not during a cutscene");
        return me;
    }

    /// <summary>How many of <paramref name="want"/> fit under MOST AT ONCE.</summary>
    private int Room(int want)
    {
        Prune();
        int room = (int)_max.Value - _spawns.Count;
        if (room <= 0)
            throw new InvalidOperationException("at the limit of " + (int)_max.Value + ": delete some on the SPAWNED tab or raise MOST AT ONCE");
        return Math.Min(want, room);
    }

    private int Wanted => Math.Max(1, (int)_count.Value);

    // ---- placement

    private static Vector3 Flat(Vector3 v)
    {
        v.Z = 0f;
        return v.LengthSquared() < 0.0001f ? Vector3.UnitX : Vector3.Normalize(v);
    }

    /// <summary>
    /// Where each copy goes: a block centred on the chosen point, rows across your view, each set on the floor under it.
    /// <paramref name="footprint"/> is roughly how wide one copy is, so big things don't overlap whatever SPACING says.
    /// </summary>
    private List<Spot> Spots(Pawn me, int count, float footprint)
    {
        if (!WorldView.ReadCamera())
            throw new InvalidOperationException("the camera can't be read right now");

        Vector3 eye = WorldView.Eye;
        Vector3 fwd = Flat(WorldView.Forward);
        Vector3 right = Flat(WorldView.Right);
        Vector3 centre;
        if (_where.Index == 1)
        {
            if (!Teleport.Ray(me, eye, eye + WorldView.Forward * CrosshairReach, out Vector3 hit, out Vector3 normal))
                throw new InvalidOperationException("aim at the ground or a wall");
            centre = normal.Z > 0.5f ? hit : hit + normal * (footprint + 50f);
        }
        else
        {
            centre = me.Location + fwd * (_distance.Value * Metre + footprint);
        }

        int cols = (int)MathF.Ceiling(MathF.Sqrt(count));
        int rows = (int)MathF.Ceiling(count / (float)cols);
        float gap = MathF.Max(_spread.Value * Metre, footprint * 2f + 40f);
        var spots = new List<Spot>(count);
        for (int i = 0; i < count; i++)
        {
            float depth = (i / cols - (rows - 1) * 0.5f) * gap;
            float across = (i % cols - (cols - 1) * 0.5f) * gap;
            Vector3 put = centre + fwd * depth + right * across;
            var top = new Vector3(put.X, put.Y, put.Z + GroundAbove);
            Vector3 floor = Teleport.Ray(me, top, top - Vector3.UnitZ * GroundReach, out Vector3 under, out _) ? under : put;
            spots.Add(new Spot(floor, Facing(me, floor)));
        }

        return spots;
    }

    private Rotator Facing(Pawn me, Vector3 at)
    {
        float yaw = _facing.Index switch
        {
            0 => Rotator.FromDirection(Flat(me.Location - at)).Yaw,
            1 => me.Rotation.Yaw,
            _ => Random.Shared.NextSingle() * 360f,
        };
        return new Rotator(0f, yaw, 0f);
    }

    // ---- tracking

    private Spawn Track(Actor a, SpawnKind kind, string label, Vector3 origin)
    {
        var s = new Spawn
        {
            Actor = a,
            Ptr = a.Ptr,
            ClassPtr = a.Class?.Ptr ?? 0,
            Kind = kind,
            Label = label,
            Number = ++_numbered,
            Origin = origin,
            Born = Clock.Now,
        };
        _spawns.Add(s);
        return s;
    }

    /// <summary>Ends a spawn command: selects the newest in the editor if asked, refreshes the list, says what happened.</summary>
    private void Finish(List<Spawn> made, string label, int failed, string why)
    {
        if (made.Count == 0)
            throw new InvalidOperationException(why);

        // Bodyguards have their own marker, so the editor's selection goes to the newest spawn that isn't one.
        RebuildGuards();
        if (_selectNew.On && made.LastOrDefault(m => !m.Bodyguard) is { } newest)
            Safe.Run("spawn select", () => WorldEditor.I.Adopt(newest.Actor));

        RefreshList();
        string text = (made.Count == 1 ? label : made.Count + " x " + label) + " SPAWNED" + (failed > 0 ? ", " + failed + " FAILED" : "");
        Overlay.Notice(NoticeTitle, text, true);
        if (failed > 0)
            Log.Info("Spawner: " + failed + " of " + (made.Count + failed) + " x " + label + " failed: " + why + ".");
    }

    /// <summary>Logged before the game builds anything, so a crash inside its spawn code leaves a trail.</summary>
    private static void Starting(int n, string label, string detail) =>
        Log.Info("Spawner: spawning " + n + " x " + label + " (" + detail + ").");

    private void RefreshList()
    {
        if (_list == null)
            return;

        Vector3 me = World.Pawn?.Location ?? Vector3.Zero;
        var keys = new List<string>();
        var labels = new List<string>();
        for (int i = _spawns.Count - 1; i >= 0 && keys.Count < ListMax; i--)
        {
            Spawn s = _spawns[i];
            if (!s.Alive)
                continue;
            keys.Add(s.Ptr.ToString("X"));
            labels.Add("#" + s.Number + " " + s.Label + "  " + Fmt.Metres(Vector3.Distance(me, s.Actor.Location) / Metre));
        }

        if (keys.Count == 0)
        {
            keys.Add("");
            labels.Add("NOTHING YET");
        }

        _list.SetOptions(keys.ToArray(), labels.ToArray());
    }

    private Spawn RequirePicked()
    {
        Prune();
        string key = _list.CurrentKey;
        return _spawns.Find(s => s.Ptr.ToString("X") == key && s.Alive)
            ?? (_spawns.Count == 0 ? throw new InvalidOperationException("nothing spawned yet") : throw new InvalidOperationException("pick one in the SPAWNED list"));
    }

    private void EditPicked()
    {
        Spawn s = RequirePicked();
        WorldEditor.I.Adopt(s.Actor);
        Overlay.Notice(NoticeTitle, s.Label + " SELECTED IN THE EDITOR", true);
    }

    private void GoToPicked()
    {
        Spawn s = RequirePicked();
        WorldEditor.I.Adopt(s.Actor);
        WorldEditor.I.GoToSelected();
    }

    private void DeletePicked()
    {
        Spawn s = RequirePicked();
        Delete(s);
        _spawns.Remove(s);
        RebuildGuards();
        RefreshList();
        Overlay.Notice(NoticeTitle, s.Label + " DELETED", true);
    }

    private void DeleteLast()
    {
        Prune();
        Spawn s = _spawns.Count > 0 ? _spawns[^1] : throw new InvalidOperationException("nothing spawned");
        Delete(s);
        _spawns.Remove(s);
        RebuildGuards();
        RefreshList();
        Overlay.Notice(NoticeTitle, s.Label + " DELETED", true);
    }

    private void DeleteAll()
    {
        int n = DeleteWhere(_ => true);
        if (n == 0)
            throw new InvalidOperationException("nothing spawned");
        Overlay.Notice(NoticeTitle, Fmt.Plural(n, "SPAWN") + " DELETED", true);
    }

    private int DeleteWhere(Func<Spawn, bool> which)
    {
        int n = 0;
        foreach (Spawn s in _spawns.Where(which).ToArray())
        {
            if (s.Alive)
            {
                Safe.Run("spawn delete", () => Delete(s));
                n++;
            }
            _spawns.Remove(s);
        }

        RebuildGuards();
        RefreshList();
        return n;
    }

    /// <summary>
    /// Takes a spawn out the way the game takes out its own: a character through DestroyOrReturnToPool (which also
    /// removes its controller and weapon), anything else with Destroy. The editor lets go of it first.
    /// </summary>
    private static void Delete(Spawn s)
    {
        if (!s.Alive)
            return;

        WorldEditor.I.Forsake(s.Actor);
        Npcs.I.Forget(s.Ptr);
        switch (s.Actor)
        {
            case RBMPawnAI p:
                if (p.CurrWeapon is { IsValid: true })
                    p.DisappearDisarm();
                p.DestroyOrReturnToPool(true, false);
                if (p.IsValid && !p.bDeleteMe)
                {
                    Controller? c = p.Controller;
                    p.Destroy();
                    if (c is { IsValid: true, bDeleteMe: false })
                        c.Destroy();
                }
                break;
            case Vehicle v:
                v.Destroy();
                break;
            default:
                s.Actor.Destroy();
                break;
        }
    }

    // ---- copying the editor's selection

    private void SpawnCopy()
    {
        Thing t = WorldEditor.I.Selected ?? throw new InvalidOperationException("nothing selected in the world editor: use SELECT AIMED first");
        switch (t.Actor)
        {
            case RBMPawnAI p:
                SpawnCharacters(SpawnCatalog.RecipeOf(p) ?? throw new InvalidOperationException("that character can't be rebuilt (a story character, or part of them isn't readable)"));
                break;
            case RVehicleNPC v:
                SpawnVehicles(SpawnCatalog.RecipeOf(v) ?? throw new InvalidOperationException("that vehicle was placed by its level, so there's no template to copy"));
                break;
            case RBMCombatThrownObject thrown when thrown.GetWeaponType() is { IsValid: true } cls:
                SpawnWeapons(WeaponRecipeOf(cls));
                break;
            default:
                SpawnObjects(MeshRecipeOf(t.Actor) ?? throw new InvalidOperationException("only characters, vehicles, weapons and mesh objects can be copied: try SPAWN WHAT YOU AIM AT"));
                break;
        }
    }
}
