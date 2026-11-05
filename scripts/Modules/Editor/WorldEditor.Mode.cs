using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// EDITOR MODE: fly around and edit with the mouse and a few keys, without the menu. The game's own debug camera
/// swaps the player controller out, which would leave Oracle with no player, so this keeps the real camera instead:
/// Batman turns invisible, loses his collision and physics, and is flown by Oracle, with the game's camera following
/// him and the mouse still turning it. The game's bindings on the keys and buttons used here are muted the same
/// way the menu mutes its keys, and Oracle's own hotkeys on those keys wait until the mode ends.
/// </summary>
internal sealed partial class WorldEditor
{
    private const float FastFly = 4f;
    private const float GrabPush = 1.5f;
    private const double HoldToGrab = 0.2;
    private const float TurnStep = 15f;
    private const float SizeStep = 0.1f;

    // Virtual-key codes, read straight from the keyboard each tick.
    private const int VkLeftMouse = 0x01, VkRightMouse = 0x02, VkShift = 0x10, VkControl = 0x11, VkSpace = 0x20, VkDelete = 0x2E;

    /// <summary>Every key the mode reads. Oracle hotkeys on these wait while the mode is on.</summary>
    private static readonly int[] ModeKeys =
    {
        'W', 'A', 'S', 'D', 'Q', 'E', 'Z', 'X', 'F', 'G', 'R', VkSpace, VkControl, VkShift, VkDelete, VkLeftMouse, VkRightMouse,
    };

    private Toggle _mode = null!;
    private Slider _flySpeed = null!;
    private Toggle _stay = null!;
    private bool _modeOn;
    private readonly bool[] _was = new bool[256];
    private double _clickAt = -1;
    private bool _clickGrab;

    // ---- how the player was, to put back
    private nint _modePawn;
    private bool _modeInput;
    private Vector3 _modeStart;
    private Rotator _modeStartRot;
    private Vector3 _flyAt;
    private bool _wasHidden, _wasCollide, _wasBlock, _wasIgnore, _wasTickOff, _wasAnimsPaused;

    public bool ModeOn => _modeOn;

    private void BuildMode()
    {
        _mode = AddToggle("ed_mode", "EDITOR MODE",
            "Fly around and edit without the menu. WASD fly, SPACE and CTRL go up and down, SHIFT is faster. Left click selects, hold it to grab and let go to drop or throw; W and S push and pull what you hold. Right click deselects. Q E turn, Z X size, F freeze, G drop to ground, DELETE removes, R restores. Batman goes back where he was when it ends.",
            OnMode).Unsaved();
        _flySpeed = AddSlider("ed_fly_speed", "FLY SPEED", "How fast EDITOR MODE flies. Hold SHIFT for four times this.", 10f, 1f, 50f, 1f, 20f, v => Fmt.Count(v) + " M/S").Pref();
        _stay = AddToggle("ed_mode_stay", "STAY WHERE YOU FLEW", "When EDITOR MODE ends, Batman lands on the ground under where you flew to, instead of going back to where he started.").Pref();
    }

    /// <summary>An Oracle hotkey on a key editor mode uses waits, unless it's the key that turns the mode off.</summary>
    public bool Claims(Keys key, IReadOnlyList<Feature> features) =>
        _modeOn && !Menu.IsOpen && Array.IndexOf(ModeKeys, (int)key & 0xFF) >= 0 && !features.Contains(_mode);

    private void OnMode(bool on)
    {
        if (on)
            Change(_mode, EnterMode, () => _mode.On = false);
        else
            Change(_mode, () => LeaveMode(false), () => { });
    }

    private void EnterMode()
    {
        Transition.RequireIdle();
        if (World.Pawn is not RPawnPlayer p || World.Pc is not { } pc)
            throw new InvalidOperationException("get out of the Batmobile first");

        _modePawn = p.Ptr;
        _modeStart = p.Location;
        _modeStartRot = p.Rotation;
        _flyAt = p.Location;
        _wasHidden = p.bHidden;
        _wasCollide = p.bCollideActors;
        _wasBlock = p.bBlockActors;
        _wasIgnore = p.bIgnoreEncroachers;
        _wasTickOff = p.bTickIsDisabled;
        _wasAnimsPaused = p.Mesh is { IsValid: true } mesh && mesh.bPauseAnims;

        p.SetCollision(false, false, _wasIgnore);
        p.SetPhysics(Actor.EPhysics.PHYS_None, false);
        p.Velocity = Vector3.Zero;
        p.SetHidden(true);
        Still(p);
        pc.IgnoreMoveInput(true);
        _modeInput = true;

        Array.Clear(_was);
        // Keys already down when the mode starts (the hotkey, a held click) count only once they're let go.
        foreach (int vk in ModeKeys)
            _was[vk] = Native.GameInFront() && (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        _clickAt = -1;
        _clickGrab = false;
        _modeOn = true;
        MuteKeys();
        Overlay.Notice(NoticeTitle, "EDITOR MODE", true);
    }

    /// <summary>
    /// Puts Batman back as he was. <paramref name="quiet"/> is for a load or unload: the pawn may already be gone,
    /// so he isn't moved, only made visible and solid again if he's still there.
    /// </summary>
    public void LeaveMode(bool quiet)
    {
        if (!_modeOn)
            return;

        _modeOn = false;
        _mode.On = false;
        if (_grab.On)
        {
            _grab.On = false;
            Release(false);
        }

        PlayerController? pc = World.Pc;
        if (!Menu.IsOpen && !Radial.IsLive)
            Safe.Run("editor keys", () => GameKeys.Unmute(pc));
        if (_modeInput && pc != null)
            Safe.Run("editor input", () => pc.IgnoreMoveInput(false));
        _modeInput = false;

        if (World.Pawn is not RPawnPlayer p || p.Ptr != _modePawn)
            return;

        p.SetHidden(_wasHidden);
        p.SetCollision(_wasCollide, _wasBlock, _wasIgnore);
        if (p.bTickIsDisabled != _wasTickOff)
            p.SetTickIsDisabled(_wasTickOff);
        if (p.Mesh is { IsValid: true } mesh && mesh.bPauseAnims != _wasAnimsPaused)
            mesh.bPauseAnims = _wasAnimsPaused;
        if (!quiet)
        {
            Vector3 at = _modeStart;
            if (_stay.On)
            {
                var top = _flyAt + Vector3.UnitZ * 50f;
                if (Teleport.Ray(p, top, top - Vector3.UnitZ * GroundReach, out Vector3 floor, out _))
                    at = floor + Vector3.UnitZ * Teleport.Lift(p);
            }

            Teleport.Place(p, at, _modeStartRot);
        }

        p.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
        p.Velocity = Vector3.Zero;
    }

    /// <summary>The menu and radial unmute every key when they close, so the mode takes its keys back after them.</summary>
    private static void MuteKeys()
    {
        if (!GameKeys.IsMuted && World.Pc is { } pc)
            GameKeys.Mute(pc, editor: true);
    }

    private bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>True on the tick a key goes down.</summary>
    private bool Pressed(int vk, bool down)
    {
        bool was = _was[vk];
        _was[vk] = down;
        return down && !was;
    }

    /// <summary>
    /// Runs the mode's tick. If it ever fails, the mode ends and Batman is given back at once: he's hidden, frozen
    /// and out of physics while it runs, so a mode that silently stops working would leave him stuck.
    /// </summary>
    private void RunMode(float dt)
    {
        try
        {
            ModeTick(dt);
        }
        catch (Exception ex)
        {
            Log.Info("Editor mode stopped: " + Log.Why(ex));
            Safe.Run("editor mode", () => LeaveMode(false));
            Overlay.Notice("EDITOR MODE", "STOPPED: " + ex.Message.ToUpperInvariant(), false);
        }
    }

    private void ModeTick(float dt)
    {
        if (World.Pawn is not RPawnPlayer p || p.Ptr != _modePawn)
        {
            LeaveMode(true);
            return;
        }

        // Keep Batman out of the way: the game puts walking physics back when it thinks he has landed.
        if (p.Physics != Actor.EPhysics.PHYS_None)
            p.SetPhysics(Actor.EPhysics.PHYS_None, false);
        if (!p.bHidden)
            p.SetHidden(true);
        if (p.bCollideActors)
            p.SetCollision(false, false, _wasIgnore);
        Still(p);

        bool listening = !Menu.IsOpen && !Radial.IsLive && !World.Paused && Native.GameInFront();
        if (!listening)
        {
            // Nothing pressed while the menu had the keys may count once it closes.
            foreach (int vk in ModeKeys)
                _was[vk] = true;
            Hover(p);
            return;
        }

        MuteKeys();
        WorldView.ReadCamera();
        bool held = _grab.On && _clickGrab;

        // ---- flying
        var move = Vector3.Zero;
        if (!held)
        {
            if (Down('W')) move += WorldView.Forward;
            if (Down('S')) move -= WorldView.Forward;
        }
        else
        {
            float push = (Down('W') ? 1f : 0f) - (Down('S') ? 1f : 0f);
            _holdDistance = Math.Clamp(_holdDistance + push * _flySpeed.Value * Metre * GrabPush * dt, GrabMin, CrosshairReach);
        }

        if (Down('D')) move += Flat(WorldView.Right);
        if (Down('A')) move -= Flat(WorldView.Right);
        if (Down(VkSpace)) move += Vector3.UnitZ;
        if (Down(VkControl)) move -= Vector3.UnitZ;
        if (move.LengthSquared() > 0.0001f)
            _flyAt += Vector3.Normalize(move) * _flySpeed.Value * Metre * (Down(VkShift) ? FastFly : 1f) * dt;
        Hover(p);

        // ---- mouse: click selects, holding grabs, letting go drops or throws
        bool left = Down(VkLeftMouse);
        if (Pressed(VkLeftMouse, left))
        {
            Scan();
            Thing? aimed = Aimed();
            if (aimed != _selected)
                Select(aimed);
            _clickAt = aimed != null ? Clock.Now : -1;
        }
        else if (left && _clickAt >= 0 && !_grab.On && Clock.Now - _clickAt >= HoldToGrab && Selected is { Fixed: false })
        {
            _grab.On = true;
            OnGrab(true);
            _clickGrab = _grab.On;
        }
        else if (!left)
        {
            _clickAt = -1;
            if (_clickGrab)
            {
                _clickGrab = false;
                if (_grab.On)
                {
                    _grab.On = false;
                    OnGrab(false);
                }
            }
        }

        if (Pressed(VkRightMouse, Down(VkRightMouse)) && !_grab.On)
            Select(null);

        // ---- edit keys
        if (Pressed('Q', Down('Q')))
            Turn(-TurnStep);
        if (Pressed('E', Down('E')))
            Turn(TurnStep);
        if (Pressed('Z', Down('Z')))
            Resize(-SizeStep);
        if (Pressed('X', Down('X')))
            Resize(SizeStep);
        if (Pressed('F', Down('F')))
            Flip(_freeze);
        if (Pressed(VkDelete, Down(VkDelete)))
            Flip(_remove);
        if (Pressed('G', Down('G')))
            Try("DROP TO GROUND", DropToGround);
        if (Pressed('R', Down('R')))
            Try("RESTORE", RestoreSelected);

        // Keys not read above still need their state kept, so a press is seen once.
        foreach (int vk in new[] { 'W', 'A', 'S', 'D', VkSpace, VkControl, VkShift })
            _was[vk] = Down(vk);
    }

    /// <summary>
    /// Stops Batman's own update and animation. AK ignores IgnoreMoveInput (as it does IgnoreButtonInput), so WASD
    /// still walked him in place: footsteps, and a camera reacting to his walk. The controller keeps ticking, so the
    /// mouse still turns the camera.
    /// </summary>
    private static void Still(RPawnPlayer p)
    {
        if (!p.bTickIsDisabled)
            p.SetTickIsDisabled(true);
        if (p.Mesh is { IsValid: true } mesh && !mesh.bPauseAnims)
            mesh.bPauseAnims = true;
    }

    /// <summary>Holds Batman at the fly position, which the camera follows.</summary>
    private void Hover(RPawnPlayer p)
    {
        if (Vector3.DistanceSquared(p.Location, _flyAt) > 1f)
            p.SetLocation(_flyAt);
        p.Velocity = Vector3.Zero;
    }

    private void Turn(float step)
    {
        if (_grab.On)
        {
            _holdYaw += step;
            return;
        }

        Try("TURN", () =>
        {
            float v = _yaw.Value + step;
            if (v > 180f)
                v -= 360f;
            if (v < -180f)
                v += 360f;
            Rotate(v, e => e.Yaw, (e, x) => e.Yaw = x, (r, d) => new Rotator(r.Pitch, r.Yaw + d, r.Roll));
            _yaw.Value = v;
        });
    }

    private void Resize(float step)
    {
        if (Selected == null)
        {
            Overlay.Notice(NoticeTitle, "NOTHING SELECTED", false);
            return;
        }

        _scale.Value = MathF.Round(Math.Clamp(_scale.Value + step, _scale.Min, _scale.Max), 2);
        SetScale(_scale.Value);
    }

    /// <summary>Flips a state row as if it had been changed in the menu.</summary>
    private static void Flip(Toggle row)
    {
        row.On = !row.On;
        row.Changed?.Invoke(row.On);
        Overlay.Toast(row.Label, row.State, row.On);
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Overlay.Notice(what, ex.Message.ToUpperInvariant(), false);
        }
    }
}
