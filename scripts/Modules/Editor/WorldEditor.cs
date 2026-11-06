using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// WORLD/EDITOR and WORLD/ESP: marks objects in the world (ESP), selects one, and edits it. Every edit is journalled in
/// <see cref="Edits"/>, so RESTORE puts it back, and REMOVE only hides: nothing is ever destroyed except through
/// the game's own damage (DESTROY), the same way the player could. The editor rows show the selected
/// object's state, so they are never saved.
/// </summary>
internal sealed partial class WorldEditor : Module
{
    public static readonly WorldEditor I = new();

    public const string PageTitle = "EDITOR";
    private const string NoticeTitle = "EDITOR";
    private const float Metre = 100f;
    private const double ScanSeconds = 0.4;
    private const float PickCone = 6f;
    private const int ListMax = 40;
    private const float BringGap = 200f;
    private const float GroundReach = 5000f;
    private const float CrosshairReach = 20000f;
    private const float StandOff = 150f;
    private const float LaunchUp = 1800f;
    private const float LaunchForward = 500f;
    private const float FlipLift = 150f;
    private const float ThrowMax = 4000f;
    private const float GrabMin = 150f;
    private const int Lethal = 1_000_000;
    private const int ScanMax = 600;
    private const float TraceReach = 30000f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Toggle[] _show = new Toggle[Things.KindCount];
    private Toggle _boxes = null!;
    private Toggle _tracers = null!;
    private Toggle _details = null!;
    private Slider _range = null!;
    private Slider _max = null!;
    private Choice _pickFrom = null!;
    private Choice _target = null!;
    private Toggle _grab = null!;
    private Slider _grabDistance = null!;
    private Slider _forward = null!;
    private Slider _side = null!;
    private Slider _up = null!;
    private Slider _step = null!;
    private Slider _yaw = null!;
    private Slider _pitch = null!;
    private Slider _roll = null!;
    private Slider _scale = null!;
    private Toggle _freeze = null!;
    private Toggle _remove = null!;
    private Toggle _ghost = null!;

    private readonly List<Thing> _things = new();
    private readonly bool[] _wanted = new bool[Things.KindCount];
    private readonly HashSet<nint> _keep = new();
    private Every _scan;
    private Thing? _selected;

    // ---- grab
    private float _holdDistance;
    private float _holdYaw;
    private Vector3 _holdOffset;
    private Vector3 _lastHold;
    private Vector3 _handVelocity;
    private bool _holding;

    // ---- what the view reads

    public IReadOnlyList<Thing> Visible => _things;

    public Thing? Selected => _selected is { Alive: true } s ? s : null;

    public bool Shows(ThingKind k) => (int)k < _show.Length && _show[(int)k].On;

    /// <summary>The most objects the ESP marks. Selection isn't limited by it.</summary>
    public int MostShown => (int)_max.Value;

    public bool Boxes => _boxes.On;

    public bool Tracers => _tracers.On;

    public bool Details => _details.On;

    public bool Grabbing => _grab.On;

    /// <summary>The menu is on WORLD/EDITOR, so the aim bracket and centre dot show what SELECT AIMED would take.</summary>
    public bool Picking => _modeOn ? !Menu.IsOpen && !Radial.IsLive : Menu.IsOpen && !Menu.Searching && Menu.Current.Title == PageTitle;

    private bool AnyShown
    {
        get
        {
            foreach (Toggle t in _show)
            {
                if (t.On)
                    return true;
            }

            return false;
        }
    }

    public override bool HasHud => World.InGame && (AnyShown || Selected != null || _grab.On || Picking || _modeOn);

    public override bool HudWithMenu => true;

    public override void Build()
    {
        string[] ids = { "esp_enemies", "esp_friendlies", "esp_vehicles", "esp_turrets", "esp_props", "esp_movers", "esp_pickups" };
        string[] help =
        {
            "Marks every enemy near you, through walls, with their name, distance and health.",
            "Marks police, fire crews, crowds and an AI partner near you.",
            "Marks militia tanks, drones, cars and the Batmobile near you.",
            "Marks sentry guns and turrets near you.",
            "Marks loose props near you: crates, barrels, and breakable signs, lamp posts and trees.",
            "Marks movable scenery near you: level pieces the game can move, such as winch-driven parts, and set dressing you can grab and move.",
            "Marks pickups near you, including Riddler trophies and dropped weapons.",
        };
        for (int i = 0; i < Things.KindCount; i++)
            _show[i] = AddToggle(ids[i], "SHOW " + Things.Plural[i], help[i]);

        _boxes = AddToggle("esp_boxes", "BOXES", "Draws a box around each marked object.", on: true).Pref();
        _tracers = AddToggle("esp_tracers", "TRACERS", "Draws a line from the top of the screen to each marked object.").Pref();
        _details = AddToggle("esp_details", "DETAILS", "Shows distance and health under each name, for objects within 60 m.", on: true).Pref();
        _range = AddSlider("esp_range", "RANGE", "How far away the editor sees objects, for marking them and for the SELECTED list.", 100f, 10f, 500f, 10f, 250f, Fmt.Metres).Pref();
        _max = AddSlider("esp_max", "MOST SHOWN", "The most objects marked at once, nearest first. Fewer is lighter on a slow PC.", 60f, 10f, 200f, 10f, 120f, Fmt.Count).Pref();

        // ---- selection
        AddCommand("ed_pick_aimed", "SELECT AIMED", "Selects the object nearest the middle of the screen. While this tab is open a white bracket shows what it would take.", PickAimed);
        _target = AddChoice("ed_target", "SELECTED", "The object every row on this tab works on. Scroll through everything nearby, nearest first. A * marks one you've changed.",
            new[] { "" }, new[] { "NOTHING" }, _ => OnTarget()).Pref().Unsaved();
        _target.HoldToScroll = true;
        _pickFrom = AddChoice("ed_pick_from", "SELECT FROM", "Which kinds SELECT AIMED and the SELECTED list choose from.",
            new[] { "any" }.Concat(Things.Labels.Select(l => l.ToLowerInvariant())).ToArray(),
            new[] { "ANYTHING" }.Concat(Things.Plural).ToArray(), _ => { Scan(); RefreshList(); }).Pref();
        AddCommand("ed_clear", "CLEAR SELECTION", "Deselects the object. Its changes stay; RESTORE undoes them.", () => Select(null));
        AddCommand("ed_goto", "GO TO IT", "Puts you beside the selected object, facing it.", GoTo).Moves();
        AddCommand("ed_bring", "BRING IT TO ME", "Puts the selected object on the ground in front of you.", Bring);

        // ---- move
        _grab = AddToggle("ed_grab", "GRAB", "The selected object floats in front of you and follows where you look. Turn GRAB off to let go: turn quickly as you do to throw it. With nothing selected it grabs what you're aiming at. A frozen object stays where you let go.",
            OnGrab).Unsaved();
        _grabDistance = AddSlider("ed_grab_distance", "GRAB DISTANCE", "How far in front of you a grabbed object floats. KEEP holds it as far away as it was when you grabbed it.",
            0f, 0f, 50f, 1f, 5f, v => v <= 0f ? "KEEP" : Fmt.Metres(v)).Pref();
        AddCommand("ed_crosshair", "PLACE AT CROSSHAIR", "Puts the selected object where you're aiming, standing on the floor there or against the wall.", PlaceAtCrosshair);
        AddCommand("ed_ground", "DROP TO GROUND", "Sets the selected object down on the floor beneath it.", DropToGround);

        _step = AddSlider("ed_nudge_step", "MOVE STEP", "How far each press of the MOVE rows moves the object.", 0.5f, 0.1f, 5f, 0.1f, 1f, v => v.ToString("0.0", Inv) + " M", SetStep).Pref();
        _forward = AddNudge("ed_nudge_forward", "MOVE FORWARD", "Moves the selected object away from where you're looking, or toward you below zero.", e => e.Forward, (e, v) => e.Forward = v, () => Flat(WorldView.Forward));
        _side = AddNudge("ed_nudge_side", "MOVE RIGHT", "Moves the selected object to your right, or left below zero.", e => e.Side, (e, v) => e.Side = v, () => Flat(WorldView.Right));
        _up = AddNudge("ed_nudge_up", "MOVE UP", "Lifts the selected object, or lowers it below zero.", e => e.Up, (e, v) => e.Up = v, () => Vector3.UnitZ);

        _yaw = AddTurn("ed_yaw", "TURN", "Turns the selected object left or right.", e => e.Yaw, (e, v) => e.Yaw = v, (r, d) => new Rotator(r.Pitch, r.Yaw + d, r.Roll));
        _pitch = AddTurn("ed_pitch", "TILT", "Tips the selected object forward or back.", e => e.Pitch, (e, v) => e.Pitch = v, (r, d) => new Rotator(r.Pitch + d, r.Yaw, r.Roll));
        _roll = AddTurn("ed_roll", "ROLL", "Rolls the selected object onto its side.", e => e.Roll, (e, v) => e.Roll = v, (r, d) => new Rotator(r.Pitch, r.Yaw, r.Roll + d));
        _scale = AddSlider("ed_scale", "SIZE", "Shrinks or grows the selected object. On characters it's only how they look.", 1f, 0.1f, 5f, 0.1f, 2f, Fmt.Times, SetScale).Pref().Unsaved();

        // ---- modify
        _freeze = AddToggle("ed_freeze", "FREEZE", "The selected object stops dead where it is: characters stop thinking and moving, vehicles and props hang in place.",
            on => Change(_freeze, () => Edits.SetFrozen(Edit(), on), () => _freeze.On = Has(e => e.Frozen))).Pref().Unsaved();
        _remove = AddToggle("ed_remove", "REMOVE", "Takes the selected object out of the world: hidden, no collision, not moving or thinking. Nothing is deleted, so missions can't break; turn it off or use RESTORE to bring it back.",
            on => Change(_remove, () => Edits.SetRemoved(Edit(), on), () => _remove.On = Has(e => e.Removed))).Pref().Unsaved();
        _ghost = AddToggle("ed_ghost", "NO COLLISION", "You and everything else pass straight through the selected object. It stays visible.",
            on => Change(_ghost, () => Edits.SetGhost(Edit(), on), () => _ghost.On = Has(e => e.Ghost))).Pref().Unsaved();

        AddCommand("ed_launch", "LAUNCH", "Throws the selected character, vehicle or prop into the air.", Launch);
        AddCommand("ed_flip", "FLIP OVER", "Turns the selected vehicle or prop upside down.", Flip);
        AddCommand("ed_heal", "HEAL OR REPAIR", "Gives the selected character or vehicle full health.", Heal);
        AddCommand("ed_destroy", "DESTROY OR KNOCK OUT", "The game's own damage: knocks out a character, destroys a vehicle or sentry gun, breaks a prop. Turrets are switched off, as a disruptor does. RESTORE can't undo this.", Destroy);
        AddCommand("ed_disarm", "DISARM", "The selected character drops the weapon in their hands.", Disarm);
        AddCommand("ed_revive", "REVIVE", "The selected character gets back up with full health, the way a medic revives them.", Revive);
        AddCommand("ed_turn", "TURN TO YOUR SIDE", "EXPERIMENTAL. The selected enemy fights the other enemies instead of you, like TURN NEAREST ENEMY. RELEASE TURNCOATS on the ENEMIES tab undoes it.", Turn);

        BuildMode();

        AddCommand("ed_restore", "RESTORE SELECTED", "Puts the selected object back where and how it was before you changed it.", RestoreSelected);
        AddCommand("ed_restore_all", "RESTORE EVERYTHING", "Puts every object you've changed back the way it was.", RestoreAll);
    }

    public override void Started() => SetStep(_step.Value);

    private Slider AddNudge(string id, string label, string help, Func<Edit, float> get, Action<Edit, float> set, Func<Vector3> axis)
    {
        Slider row = null!;
        row = AddSlider(id, label, help, 0f, -100f, 100f, 0.5f, 1f, SignedMetres, v => Change(row, () => Nudge(v, get, set, axis), () => row.Value = Current(get, 0f)));
        return row.Pref().Unsaved();
    }

    private Slider AddTurn(string id, string label, string help, Func<Edit, float> get, Action<Edit, float> set, Func<Rotator, float, Rotator> turn)
    {
        Slider row = null!;
        row = AddSlider(id, label, help, 0f, -180f, 180f, 15f, 90f, SignedDegrees, v => Change(row, () => Rotate(v, get, set, turn), () => row.Value = Current(get, 0f)));
        return row.Pref().Unsaved();
    }

    private static string SignedMetres(float v) => MathF.Abs(v) < 0.001f ? "0 M" : (v > 0f ? "+" : "") + v.ToString("0.0", Inv) + " M";

    private static string SignedDegrees(float v) => MathF.Abs(v) < 0.001f ? "0 DEG" : (v > 0f ? "+" : "") + v.ToString("0", Inv) + " DEG";

    private void SetStep(float v)
    {
        foreach (Slider s in new[] { _forward, _side, _up })
        {
            if (s != null)
                s.Stride = v;
        }
    }

    // ---- lifecycle

    public override void WorldChanged() => Reset();

    public override void LeftGame() => Reset();

    /// <summary>A new world: every object is gone, so the journal is dropped without trying to put anything back.</summary>
    private void Reset()
    {
        _modeOn = false;
        _mode.On = false;
        _grab.On = false;
        _holding = false;
        _selected = null;
        _things.Clear();
        Edits.Forget();
        Things.Forget();
        SyncRows();
        _target.Saved = "";
        _target.SetOptions(new[] { "" }, new[] { "NOTHING" });
        _scan.Force();
    }

    public override void Shutdown()
    {
        Safe.Run("editor mode", () => LeaveMode(true));
        Safe.Run("editor grab", () => Release(false));
        Safe.Run("editor restore", () => Edits.RestoreAll());
    }

    public override void MenuOpening()
    {
        Scan();
        RefreshList();
    }

    public override void Tick(float dt)
    {
        if (!World.InGame)
            return;

        if (_selected != null && !_selected.Alive)
        {
            if (_grab.On)
                _grab.On = false;
            _holding = false;
            _selected = null;
            SyncRows();
        }

        // Cutscenes spawn and tear down actors fastest, which is where a half-destroyed one crashed a scan, and
        // nothing in the list can be used during one anyway.
        bool cinematic = Safe.Try("editor cinematic check", () => World.Pc is { } pc && CameraRig.InCinematic(pc), fallback: true);
        if (!cinematic && (AnyShown || _selected != null || _grab.On || _modeOn || Menu.IsOpen || Edits.HasChanges) && _scan.Elapsed(ScanSeconds))
        {
            Scan();
            if (Menu.IsOpen)
                RefreshList();
        }

        if (_modeOn)
            RunMode(dt);
        if (_grab.On)
            Safe.Run("editor grab", () => HoldGrabbed(dt));
        Safe.Run("editor hold", Edits.Hold);
    }

    public override void Draw(Canvas c) => EditorView.Draw(this, c);

    // ---- scanning and the list

    private bool PicksKind(ThingKind k) => _pickFrom.Index == 0 || _pickFrom.Index - 1 == (int)k;

    private void Scan()
    {
        _scan.Force();
        _scan.Elapsed(ScanSeconds);
        Pawn? me = World.Pawn;
        if (me == null)
        {
            _things.Clear();
            return;
        }

        // Kinds that are marked, plus whatever selecting can choose from while the editor is in use.
        bool editing = Menu.IsOpen || _selected != null || _grab.On || _modeOn;
        for (int k = 0; k < Things.KindCount; k++)
            _wanted[k] = _show[k].On || (editing && PicksKind((ThingKind)k));

        _keep.Clear();
        if (_selected != null)
            _keep.Add(_selected.Ptr);
        foreach (nint p in Edits.Pointers)
            _keep.Add(p);

        Things.Scan(_things, me.Location, _range.Value * Metre, _wanted, _keep, ScanMax);
    }

    private void RefreshList()
    {
        var keys = new List<string> { "" };
        var labels = new List<string> { "NOTHING" };
        foreach (Thing t in _things)
        {
            if (keys.Count > ListMax)
                break;
            if (PicksKind(t.Kind) && t.Alive)
            {
                keys.Add(t.Key);
                labels.Add(ListLabel(t));
            }
        }

        if (Selected is { } s && !keys.Contains(s.Key))
        {
            keys.Insert(1, s.Key);
            labels.Insert(1, ListLabel(s));
        }

        _target.Saved = Selected?.Key ?? "";
        _target.SetOptions(keys.ToArray(), labels.ToArray());
    }

    private static string ListLabel(Thing t) =>
        t.Name + "  " + Fmt.Metres(t.Distance / Metre) + (Edits.Find(t) is { Changed: true } ? "  *" : "");

    private void OnTarget()
    {
        string key = _target.CurrentKey;
        Select(key.Length == 0 ? null : _things.Find(t => t.Key == key && t.Alive));
    }

    // ---- selection

    private void Select(Thing? t)
    {
        if (_grab.On && t != _selected)
        {
            Release(false);
            _grab.On = false;
        }

        _selected = t;
        SyncRows();
        RefreshList();
    }

    /// <summary>Shows the selected object's state on the editor rows.</summary>
    private void SyncRows()
    {
        Edit? e = _selected == null ? null : Edits.Find(_selected);
        _forward.Value = e?.Forward ?? 0f;
        _side.Value = e?.Side ?? 0f;
        _up.Value = e?.Up ?? 0f;
        _yaw.Value = e?.Yaw ?? 0f;
        _pitch.Value = e?.Pitch ?? 0f;
        _roll.Value = e?.Roll ?? 0f;
        _scale.Value = e?.Scale ?? 1f;
        _freeze.On = e?.Frozen ?? false;
        _remove.On = e?.Removed ?? false;
        _ghost.On = e?.Ghost ?? false;
    }

    private bool Has(Func<Edit, bool> get) => _selected != null && Edits.Find(_selected) is { } e && get(e);

    private float Current(Func<Edit, float> get, float fallback) =>
        _selected != null && Edits.Find(_selected) is { } e ? get(e) : fallback;

    /// <summary>
    /// What the crosshair is on: the object whose silhouette is nearest the middle of the screen. Inside a
    /// silhouette the nearest object wins, so a crate in front of a tank is picked over the tank.
    /// </summary>
    public Thing? Aimed()
    {
        if (!WorldView.Ready)
            return null;

        Thing? best = Nearest(out float bestDist, out bool inside);
        Thing? hit = Traced(out float hitDist);
        if (hit == null)
            return best;
        if (best == null || best == hit)
            return hit;

        // The crosshair's ray is exact; the silhouette pick is forgiving. The silhouette wins only when the crosshair
        // is inside its outline and it's in front of what the ray hit, so a crate that doesn't block the ray (no
        // collision) is still picked, but one beside the crosshair doesn't steal the car the ray is on.
        return inside && bestDist <= hitDist + best.Radius ? best : hit;
    }

    /// <summary>
    /// Whatever the crosshair's ray hits first, of any kind: doors, scenery, a vehicle the scan didn't reach.
    /// Level geometry (BSP, which reports as WorldInfo) and volumes are skipped.
    /// </summary>
    private Thing? Traced(out float dist)
    {
        dist = float.MaxValue;
        Pawn? me = World.Pawn;
        if (me == null)
            return null;

        Vector3 eye = WorldView.Eye;
        Actor? a = me.Trace(out Vector3 at, out _, eye + WorldView.Forward * TraceReach, eye, Component.ECollisionFilter.COF_TraceAll, Vector3.Zero, out _, 0);
        if (a is not { IsValid: true, bDeleteMe: false } || a is WorldInfo or Brush || a.Ptr == me.Ptr || a.Ptr == (World.Player?.Ptr ?? 0))
            return null;

        Thing t = Things.Wrap(a);
        if (!PicksKind(t.Kind))
            return null;

        dist = Vector3.Distance(eye, at);
        return t;
    }

    private Thing? Nearest(out float bestDist, out bool inside)
    {
        bestDist = float.MaxValue;
        inside = false;

        Thing? best = null;
        float bestScore = float.MaxValue;
        foreach (Thing t in _things)
        {
            if (!PicksKind(t.Kind) || !t.Alive)
                continue;

            Vector3 c = t.Centre;
            float dist = Vector3.Distance(c, WorldView.Eye);
            if (dist < 1f)
                continue;

            float half = MathF.Max(t.Radius, (t.Max.Z - t.Min.Z) * 0.5f);
            float size = MathF.Atan2(half, dist) * 180f / MathF.PI;
            float outside = WorldView.AngleTo(c) - size;
            if (outside > PickCone)
                continue;

            float score = MathF.Max(0f, outside) * 100f + dist / Metre;
            if (score < bestScore)
            {
                bestScore = score;
                best = t;
                bestDist = dist;
                inside = outside <= 0f;
            }
        }

        return best;
    }

    private void PickAimed()
    {
        WorldView.ReadCamera();
        Scan();
        Thing t = Aimed() ?? throw new InvalidOperationException(_pickFrom.Index == 0 ? "nothing there" : "no " + _pickFrom.State.ToLowerInvariant() + " there");
        Select(t);
        Overlay.Notice(NoticeTitle, t.Name + " SELECTED", true);
    }

    private Thing RequireSelected() => Selected ?? throw new InvalidOperationException("nothing selected: use SELECT AIMED or the SELECTED list");

    private Thing RequireMovable()
    {
        Thing t = RequireSelected();
        return t.Fixed ? throw new InvalidOperationException("it's part of the level and can't move") : t;
    }

    private Edit Edit() => Edits.Begin(RequireSelected());

    /// <summary>
    /// Runs a row's change on the selected object. A row can't refuse through the menu the way a command does, so a
    /// failure shows its reason and the row goes back to what the object really is.
    /// </summary>
    private static void Change(Feature row, Action change, Action revert)
    {
        try
        {
            change();
        }
        catch (Exception ex)
        {
            Safe.Run("editor revert", revert);
            Log.Info(row.Label + " failed: " + ex.Message);
            Overlay.Notice(row.Label, ex.Message.ToUpperInvariant(), false);
        }
    }

    // ---- moving

    private static Vector3 Flat(Vector3 v)
    {
        v.Z = 0f;
        return v.LengthSquared() < 0.0001f ? Vector3.UnitX : Vector3.Normalize(v);
    }

    /// <summary>Moves the selected object and records it, keeping a frozen object frozen at its new spot.</summary>
    private static void Move(Thing t, Edit e, Vector3 at, Rotator rot)
    {
        e.Moved = true;
        Edits.Put(t.Actor, at, rot);
        if (e.Frozen)
        {
            e.FrozenAt = at;
            e.FrozenRotation = rot;
        }
    }

    private void Nudge(float value, Func<Edit, float> get, Action<Edit, float> set, Func<Vector3> axis)
    {
        Thing t = RequireMovable();
        Edit e = Edits.Begin(t);
        float delta = value - get(e);
        if (MathF.Abs(delta) < 0.0001f)
            return;

        WorldView.ReadCamera();
        Move(t, e, t.Actor.Location + axis() * delta * Metre, t.Actor.Rotation);
        set(e, value);
    }

    private void Rotate(float value, Func<Edit, float> get, Action<Edit, float> set, Func<Rotator, float, Rotator> turn)
    {
        Thing t = RequireMovable();
        Edit e = Edits.Begin(t);
        float delta = value - get(e);
        if (MathF.Abs(delta) < 0.0001f)
            return;

        Move(t, e, t.Actor.Location, turn(t.Actor.Rotation, delta));
        set(e, value);
    }

    private void SetScale(float v) => Change(_scale, () =>
    {
        Thing t = RequireSelected();
        Edit e = Edits.Begin(t);
        t.Actor.SetDrawScale(e.DrawScale * v);
        e.Scale = v;
        t.BoxAt = double.MinValue;
    }, () => _scale.Value = Current(e => e.Scale, 1f));

    /// <summary>
    /// Sets an object down with the bottom of its box on the floor under <paramref name="spot"/>, searching down from
    /// <paramref name="fromZ"/>. The box is centred on the spot. With no floor found it goes to the spot as it is.
    /// </summary>
    private static void Land(Thing t, Vector3 spot, float fromZ, Rotator rot)
    {
        Pawn tracer = World.RequirePawn();
        Vector3 centre = (t.Min + t.Max) * 0.5f;
        var top = new Vector3(spot.X, spot.Y, fromZ);
        Vector3 at = Teleport.Ray(tracer, top, top - Vector3.UnitZ * GroundReach, out Vector3 floor, out _)
            ? new Vector3(spot.X, spot.Y, floor.Z - t.Min.Z + 2f)
            : new Vector3(spot.X, spot.Y, spot.Z - centre.Z);
        at.X -= centre.X;
        at.Y -= centre.Y;
        Move(t, Edits.Begin(t), at, rot);
    }

    private void GoTo()
    {
        Thing t = RequireSelected();
        Pawn mover = Teleport.RequireMover();
        Vector3 c = t.Centre;
        Vector3 away = mover.Location - c;
        away.Z = 0f;
        Vector3 dir = away.LengthSquared() > 1f ? Vector3.Normalize(away) : -Flat(Geo.Facing(mover.Rotation.Yaw));
        Vector3 spot = c + dir * (t.Radius + StandOff);
        float bottom = t.Actor.Location.Z + t.Min.Z;
        float lift = Teleport.Lift(mover);
        var top = new Vector3(spot.X, spot.Y, bottom + 200f);
        spot = Teleport.Ray(mover, top, top - Vector3.UnitZ * GroundReach, out Vector3 floor, out _)
            ? floor + Vector3.UnitZ * lift
            : new Vector3(spot.X, spot.Y, bottom + lift);
        Teleport.I.Hop(spot, new Rotator(0f, Rotator.FromDirection(-dir).Yaw, 0f));
    }

    private void Bring()
    {
        Thing t = RequireMovable();
        Pawn me = World.RequirePawn();
        WorldView.ReadCamera();
        Vector3 spot = me.Location + Flat(WorldView.Forward) * (t.Radius + me.GetCollisionRadius() + BringGap);
        Land(t, spot, me.Location.Z + 200f, t.Actor.Rotation);
        Overlay.Notice(NoticeTitle, t.Name + " BROUGHT", true);
    }

    private void PlaceAtCrosshair()
    {
        Thing t = RequireMovable();
        Pawn me = World.RequirePawn();
        WorldView.ReadCamera();
        Vector3 eye = WorldView.Eye;
        if (!Teleport.Ray(me, eye, eye + WorldView.Forward * CrosshairReach, out Vector3 hit, out Vector3 normal))
            throw new InvalidOperationException("aim at the ground or a wall");

        // A floor takes the object standing on it; a wall takes it in front, then down to the floor there.
        Vector3 spot = normal.Z > 0.5f ? hit : hit + normal * (t.Radius + 20f);
        Land(t, spot, spot.Z + 50f, t.Actor.Rotation);
    }

    private void DropToGround()
    {
        Thing t = RequireMovable();
        Land(t, t.Centre, t.Actor.Location.Z + t.Min.Z - 5f, t.Actor.Rotation);
    }

    // ---- grab

    private void OnGrab(bool on)
    {
        if (!on)
        {
            Release(true);
            return;
        }

        Change(_grab, () =>
        {
            WorldView.ReadCamera();
            if (Selected == null)
            {
                Scan();
                Select(Aimed() ?? throw new InvalidOperationException("nothing to grab: aim at something or select it first"));
                _grab.On = true;
            }

            Thing t = RequireMovable();
            Edit e = Edits.Begin(t);
            if (e.Removed)
                throw new InvalidOperationException("it's removed");

            e.Moved = true;
            Vector3 c = t.Centre;
            _holdOffset = c - t.Actor.Location;
            _holdDistance = MathF.Max(GrabMin, Vector3.Distance(WorldView.Eye, c));
            _holdYaw = t.Actor.Rotation.Yaw - WorldView.Rotation.Yaw;
            _handVelocity = Vector3.Zero;
            _holding = false;
            Overlay.Notice(NoticeTitle, "GRABBED " + t.Name, true);
        }, () => _grab.On = false);
    }

    /// <summary>
    /// Holds the grabbed object at a point in front of the camera every tick, turning with the camera. A character is
    /// kept frozen while held, so its AI and walking don't fight the hold. The hand's speed is tracked for the throw.
    /// </summary>
    private void HoldGrabbed(float dt)
    {
        Thing? t = Selected;
        if (t == null || dt <= 0f || !WorldView.ReadCamera())
        {
            if (t == null)
                _grab.On = false;
            return;
        }

        Actor a = t.Actor;
        float dist = !_modeOn && _grabDistance.Value > 0f ? _grabDistance.Value * Metre : _holdDistance;
        Vector3 at = WorldView.Eye + WorldView.Forward * dist - _holdOffset;
        var rot = new Rotator(a.Rotation.Pitch, WorldView.Rotation.Yaw + _holdYaw, a.Rotation.Roll);

        if (_holding)
            _handVelocity = Vector3.Lerp(_handVelocity, (at - _lastHold) / dt, 0.35f);
        _lastHold = at;
        _holding = true;

        if (a is Pawn p && Edits.IsCharacter(a))
            Npcs.SetFrozen(p, true);
        Move(t, Edits.Begin(t), at, rot);
    }

    /// <summary>Lets go. A frozen object stays put; anything else falls, flying off at the hand's speed if thrown.</summary>
    private void Release(bool throwIt)
    {
        bool held = _holding;
        _holding = false;
        Thing? t = Selected;
        if (!held || t == null)
            return;

        Actor a = t.Actor;
        Edit? e = Edits.Find(t);
        if (e is { Frozen: true } || e is { Removed: true })
            return;

        if (a is Pawn p && Edits.IsCharacter(a))
            Npcs.SetFrozen(p, false);

        Vector3 v = throwIt ? _handVelocity : Vector3.Zero;
        if (v.Length() > ThrowMax)
            v = Vector3.Normalize(v) * ThrowMax;
        if (Edits.IsCharacter(a) || Edits.IsRigid(a))
            Edits.Fling(a, v, Vector3.Zero);
    }

    // ---- actions

    private void Launch()
    {
        Thing t = RequireMovable();
        Edit e = Edits.Begin(t);
        if (e.Frozen || e.Removed)
            throw new InvalidOperationException(e.Frozen ? "it's frozen" : "it's removed");

        WorldView.ReadCamera();
        var spin = new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f) * 6f;
        Edits.Fling(t.Actor, Vector3.UnitZ * LaunchUp + Flat(WorldView.Forward) * LaunchForward, spin);
        e.Moved = true;
    }

    private void Flip()
    {
        Thing t = RequireMovable();
        if (!Edits.IsRigid(t.Actor))
            throw new InvalidOperationException("only vehicles and loose props flip");

        Actor a = t.Actor;
        Rotator r = a.Rotation;
        Move(t, Edits.Begin(t), a.Location + Vector3.UnitZ * FlipLift, new Rotator(r.Pitch, r.Yaw, r.Roll + 180f));
    }

    private void Heal()
    {
        if (RequireSelected().Actor is not Pawn p)
            throw new InvalidOperationException("it has no health");
        if (p.Health <= 0)
            throw new InvalidOperationException(p is Vehicle ? "it's destroyed" : "they're knocked out: use REVIVE");

        int full = Math.Max(p.HealthMax, p.Health);
        if (p.Health >= full)
            throw new InvalidOperationException("already at full health");
        p.Health = full;
        Overlay.Notice(NoticeTitle, "HEALED", true);
    }

    /// <summary>
    /// Each kind goes the way the game itself destroys it, so its death, debris and save state all run. Parked or
    /// sleeping tanks are refused: lethal damage left them frozen at 0 health without their death sequence (see
    /// DESTROY ALL TANKS).
    /// </summary>
    private void Destroy()
    {
        Thing t = RequireSelected();
        Actor a = t.Actor;
        Controller? by = World.Pc;
        switch (a)
        {
            case RVehicleBatmobileBase:
                throw new InvalidOperationException("not the Batmobile");
            case RVehicleTank tank when tank is RVehicleStaticTank || tank.CurrentBehaviour is not { IsValid: true }:
                throw new InvalidOperationException("that tank is parked or held by a mission and would break");
            case RSentryGunBase gun:
                if (gun.bGoneRagdoll)
                    throw new InvalidOperationException("already destroyed");
                gun.Explode(true);
                if (!gun.bGoneRagdoll)
                    gun.GoPhysicsAndTriggerEvent();
                break;
            case RTurretBase turret:
                if (turret.bDisabled)
                    throw new InvalidOperationException("already switched off");
                turret.Disrupted();
                break;
            case RSmashableProp prop:
                prop.BreakProp();
                break;
            case Pawn p:
                if (p.Health <= 0 || p is RPawnVillain { bGoodAsDead: true })
                    throw new InvalidOperationException("already down");
                p.TakeDamage(Lethal, by!, p.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass(), default, null!);
                break;
            default:
                a.TakeDamage(Lethal, by!, a.Location, Vector3.UnitZ * 1000f, RDmgType_DebugKill.StaticClass(), default, null!);
                break;
        }

        Log.Info("Editor: destroyed " + t.Name + " (" + a.Class.Name + ").");
    }

    private RBMPawnAI RequireCharacter() =>
        RequireSelected().Actor as RBMPawnAI ?? throw new InvalidOperationException("only characters can do that");

    private void Disarm()
    {
        RBMPawnAI ai = RequireCharacter();
        if (ai.CurrWeapon is not { IsValid: true })
            throw new InvalidOperationException("they aren't holding a weapon");
        ai.ForceDisarm();
    }

    private void Revive()
    {
        RBMPawnAI ai = RequireCharacter();
        if (ai.Health > 0 && ai is not RPawnVillain { bGoodAsDead: true })
            throw new InvalidOperationException("they aren't knocked out");
        ai.WakeFromDeadWithDefaults();
    }

    private void Turn()
    {
        if (RequireSelected().Actor is not RPawnVillain v)
            throw new InvalidOperationException("only enemies can be turned");
        Npcs.I.Turn(v);
    }

    // ---- undo

    private void RestoreSelected()
    {
        Thing t = RequireSelected();
        Edit e = Edits.Find(t) is { Changed: true } found ? found : throw new InvalidOperationException("it hasn't been changed");
        if (_grab.On)
        {
            _holding = false;
            _grab.On = false;
        }

        Edits.Restore(e);
        SyncRows();
        RefreshList();
        Overlay.Notice(NoticeTitle, t.Name + " RESTORED", true);
    }

    private void RestoreAll()
    {
        if (_grab.On)
        {
            _holding = false;
            _grab.On = false;
        }

        int n = Edits.RestoreAll();
        SyncRows();
        RefreshList();
        if (n == 0)
            throw new InvalidOperationException("nothing has been changed");
        Overlay.Notice(NoticeTitle, Fmt.Plural(n, "OBJECT") + " RESTORED", true);
    }
}
