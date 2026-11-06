using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// The marker over each spawned bodyguard, in the ORACLE LENS look (see <see cref="Lens"/>): a chevron above the head
/// with a segmented health meter, coloured by what they're doing (with you, fighting, knocked out), their name and
/// state close by, a card when you aim at one, and an arrow at the screen edge pointing to one that's off screen.
/// Bodyguards get this instead of the world editor's ESP box, unless you select one in the editor yourself.
/// </summary>
internal sealed partial class Spawner
{
    private const float MarkerAbove = 28f;
    private const float MarkerNear = 1200f;
    private const float MarkerFar = 15000f;
    private const float EdgeRange = 12000f;
    private const float LabelRange = 3000f;

    private static readonly Rgba GuardIdle = new(84, 230, 140);
    private static readonly Rgba GuardFighting = new(255, 186, 64);
    private static readonly Rgba GuardDown = Lens.Dormant;

    private readonly HashSet<nint> _guardPtrs = new();
    private Toggle _markers = null!;

    private void BuildMarkers() =>
        _markers = AddToggle("guard_markers", "BODYGUARD MARKERS", "A marker over each bodyguard you spawned, with their health, and an arrow at the screen edge when one is out of view. Green: with you. Amber: fighting. Grey: knocked out.", on: true).Pref();

    /// <summary>A spawned bodyguard: the editor leaves these to this marker.</summary>
    public bool IsBodyguard(nint ptr) => _guardPtrs.Contains(ptr);

    public override bool HasHud => _markers.On && _guardPtrs.Count > 0 && World.InGame;

    /// <summary>Kept under the open menu, so the markers don't seem to vanish while you set bodyguards up.</summary>
    public override bool HudWithMenu => true;

    private void RebuildGuards()
    {
        _guardPtrs.Clear();
        foreach (Spawn s in _spawns)
        {
            if (s.Bodyguard)
                _guardPtrs.Add(s.Ptr);
        }
    }

    public override void Draw(Canvas c)
    {
        WorldView.Begin(c);
        if (!WorldView.Ready || World.Pawn is not { } me)
            return;
        if (World.Pc is { } pc && CameraRig.InCinematic(pc))
            return;

        Alpha = 1f;
        Lens.Discard();
        Thing? selected = WorldEditor.I.Selected;
        Spawn? focus = null;
        Vector2 focusAt = default;
        foreach (Spawn s in _spawns)
        {
            if (!s.Bodyguard || !s.Alive || s.Actor is not RBMPawnAI p || p.bPendingDelete || (selected != null && selected.Ptr == s.Ptr))
                continue;
            if (Marker(s, p, me, out Vector2 at))
            {
                focus = s;
                focusAt = at;
            }
        }

        if (focus is { Actor: RBMPawnAI fp } f)
            FocusCard(f, fp, me, focusAt);
    }

    private static bool Down(RBMPawnAI p) => p.Health <= 0 || p is RPawnVillain { bGoodAsDead: true };

    private static Rgba GuardColour(RBMPawnAI p, bool down) => down ? GuardDown : Npcs.I.IsFighting(p.Ptr) ? GuardFighting : GuardIdle;

    private static string GuardState(RBMPawnAI p, bool down) => down ? "DOWN" : Npcs.I.IsFighting(p.Ptr) ? "FIGHTING" : "WITH YOU";

    /// <summary>
    /// A chevron over the head with a segmented health meter, its name and state close by, and an arrow at the screen
    /// edge once it's out of view. True when it has the focus, so its card is drawn last.
    /// </summary>
    private static bool Marker(Spawn s, RBMPawnAI p, Pawn me, out Vector2 at)
    {
        bool down = Down(p);
        Rgba col = GuardColour(p, down);
        float height = p.CylinderComponent is { IsValid: true } cyl ? cyl.CollisionHeight : 90f;
        Vector3 head = p.Location + Vector3.UnitZ * (height * p.DrawScale + MarkerAbove);
        float dist = Vector3.Distance(head, WorldView.Eye);
        float away = Vector3.Distance(p.Location, me.Location);

        if (!WorldView.ToScreen(head, out at) || !WorldView.OnScreen(at, -S(12)))
        {
            if (!down && away <= EdgeRange)
                Lens.Edge(head, col, s.Label + "  " + Lens.Metres(away));
            return false;
        }

        // Big up close, small far off, and a slow bob so it reads as a marker rather than part of the world.
        float near = Math.Clamp((MarkerFar - dist) / (MarkerFar - MarkerNear), 0f, 1f);
        float settle = Lens.Settle(Lens.Age(p.Ptr));
        float size = Lens.L(6f + 6f * near) * (0.6f + 0.4f * settle);
        float bob = MenuView.Effects ? MathF.Sin((float)Time * 3f + p.Ptr % 7) * S(2f) : 0f;
        float y = at.Y - size * 0.4f + bob;
        Lens.Offer(p.Ptr, at.X - size * 1.5f, y - size * 3f, at.X + size * 1.5f, y + size);
        Lens.Chevron(at.X, y, size, col);

        float frac = down || p.HealthMax <= 0 ? 0f : Math.Clamp(p.Health / (float)p.HealthMax, 0f, 1f);
        float mh = MathF.Max(3f, Lens.L(3.5f));
        float mw = MathF.Max(size * 2.6f, Lens.L(30f));
        float my = y - size * 1.1f - Lens.L(6f) - mh;
        Lens.Meter(at.X - mw * 0.5f, my, mw, mh, frac, Lens.Trail(p.Ptr, frac), col, 5);

        Lens.Tier tier = Lens.TierOf(p.Ptr, dist, LabelRange, MarkerFar);
        if (tier == Lens.Tier.Card)
            return true;
        if (tier == Lens.Tier.Full)
            Lens.Plate(at.X, my - Lens.L(3f), s.Label, GuardState(p, down), col, settle);
        else if (tier == Lens.Tier.Name)
            Lens.Caption(at.X, y + Lens.L(5f), Lens.Metres(away), Rgba.Mix(col, Theme.Hi, 0.4f));
        return false;
    }

    private static readonly List<(string, Rgba)> _cardLines = new();

    private static void FocusCard(Spawn s, RBMPawnAI p, Pawn me, Vector2 at)
    {
        bool down = Down(p);
        Rgba col = GuardColour(p, down);
        float r = Lens.L(14f);
        Lens.Lock(at.X - r, at.Y - r * 2f, at.X + r, at.Y + r * 0.5f, Theme.Accent, Lens.FocusAge);

        _cardLines.Clear();
        string where = Lens.Metres(Vector3.Distance(p.Location, me.Location)) + " AWAY";
        if (p.HealthMax > 0)
            where += "   " + Math.Max(0, p.Health) + " / " + p.HealthMax + " HP";
        _cardLines.Add((where, Theme.Mid));
        _cardLines.Add((GuardState(p, down), down ? Theme.Low : col));

        float frac = down || p.HealthMax <= 0 ? 0f : Math.Clamp(p.Health / (float)p.HealthMax, 0f, 1f);
        Lens.Card(at.X, at.Y, at.X + r, at.Y - r * 2f, "BODYGUARD", s.Label, _cardLines, col, frac, Lens.Trail(p.Ptr, frac), Lens.FocusAge);
    }
}
