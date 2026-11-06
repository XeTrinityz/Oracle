using System.Numerics;
using BmSDK.BmGame;
using BmSDK.Engine;
using static CeZeY.Oracle.Catalog;
using static CeZeY.Oracle.Gfx;

namespace CeZeY.Oracle;

/// <summary>
/// The marker over each spawned bodyguard: a chevron above the head with a health bar, coloured by what he's doing
/// (with you, fighting, knocked out), and an arrow at the screen edge pointing to one that's off screen. Bodyguards
/// get this instead of the world editor's ESP box, unless you select one in the editor yourself.
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
    private static readonly Rgba GuardDown = new(120, 130, 132);
    internal static readonly Rgba MarkerEdge = new(8, 14, 18);

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
        Thing? selected = WorldEditor.I.Selected;
        foreach (Spawn s in _spawns)
        {
            if (!s.Bodyguard || !s.Alive || s.Actor is not RBMPawnAI p || p.bPendingDelete || (selected != null && selected.Ptr == s.Ptr))
                continue;
            Marker(p, me);
        }
    }

    private static void Marker(RBMPawnAI p, Pawn me)
    {
        bool down = p.Health <= 0 || p is RPawnVillain { bGoodAsDead: true };
        Rgba col = down ? GuardDown : Npcs.I.IsFighting(p.Ptr) ? GuardFighting : GuardIdle;
        float height = p.CylinderComponent is { IsValid: true } cyl ? cyl.CollisionHeight : 90f;
        Vector3 head = p.Location + Vector3.UnitZ * (height * p.DrawScale + MarkerAbove);
        float dist = Vector3.Distance(head, WorldView.Eye);

        if (WorldView.ToScreen(head, out Vector2 at) && WorldView.OnScreen(at, -S(12)))
        {
            // Big up close, small far off, and a slow bob so it reads as a marker rather than part of the world.
            float near = Math.Clamp((MarkerFar - dist) / (MarkerFar - MarkerNear), 0f, 1f);
            float size = S(7f + 7f * near);
            float bob = MathF.Sin((float)Time * 3f + p.Ptr % 7) * S(2.5f);
            float y = at.Y - size * 0.4f + bob;
            Chevron(at.X, y, size, col);
            Health(p, at.X, y - size * 1.25f - S(6), size, col, down);
            if (dist > LabelRange)
                TextCentre(at.X, y + S(3), Fmt.Metres(Vector3.Distance(p.Location, me.Location) / Metre), col, 0.45f);
            return;
        }

        if (!down && Vector3.Distance(p.Location, me.Location) <= EdgeRange)
            EdgeArrow(head, col);
    }

    /// <summary>A downward chevron, its tip just above the head, with a dark rim so it reads against bright sky.</summary>
    internal static void Chevron(float x, float tipY, float size, Rgba col)
    {
        float w = size, h = size * 1.15f, rim = S(2f);
        Shapes.Triangle(x - w - rim, tipY - h - rim * 0.6f, x + w + rim, tipY - h - rim * 0.6f, x, tipY + rim * 1.4f, MarkerEdge, 0.9f);
        Shapes.Triangle(x - w, tipY - h, x + w, tipY - h, x, tipY, col, 1f);

        // The notch makes it a chevron rather than a plain triangle.
        float notch = h * 0.45f;
        Shapes.Triangle(x - w * 0.42f, tipY - h - S(0.5f), x + w * 0.42f, tipY - h - S(0.5f), x, tipY - h + notch, MarkerEdge, 0.9f);
    }

    private static void Health(RBMPawnAI p, float x, float y, float size, Rgba col, bool down)
    {
        float w = size * 2.2f, h = MathF.Max(2f, S(3f));
        float part = down || p.HealthMax <= 0 ? 0f : Math.Clamp(p.Health / (float)p.HealthMax, 0f, 1f);
        Rect(x - w * 0.5f - S(1), y - S(1), w + S(2), h + S(2), MarkerEdge, 0.85f);
        Rect(x - w * 0.5f, y, w, h, Theme.White, 0.12f);
        if (part > 0f)
            Rect(x - w * 0.5f, y, w * part, h, col, 1f);
    }

    /// <summary>
    /// For a bodyguard off screen or behind you: an arrow on an oval just inside the screen edge, pointing his way.
    /// The direction comes from the camera's own right and up axes, so it's right even when he's behind the camera.
    /// </summary>
    internal static void EdgeArrow(Vector3 where, Rgba col)
    {
        Vector3 d = where - WorldView.Eye;
        float rx = Vector3.Dot(d, WorldView.Right);
        float uy = Vector3.Dot(d, WorldView.Up);
        if (MathF.Abs(rx) < 0.001f && MathF.Abs(uy) < 0.001f)
            return;

        float a = MathF.Atan2(-uy, rx);
        float cx = WorldView.Width * 0.5f, cy = WorldView.Height * 0.5f;
        float ax = cx + MathF.Cos(a) * (cx - S(48)), ay = cy + MathF.Sin(a) * (cy - S(48));
        float size = S(10);
        float fx = MathF.Cos(a), fy = MathF.Sin(a);
        float sx = -fy, sy = fx;
        float rim = S(2f);
        Shapes.Triangle(ax + fx * (size + rim), ay + fy * (size + rim), ax - fx * (size * 0.6f + rim) + sx * (size + rim), ay - fy * (size * 0.6f + rim) + sy * (size + rim),
            ax - fx * (size * 0.6f + rim) - sx * (size + rim), ay - fy * (size * 0.6f + rim) - sy * (size + rim), MarkerEdge, 0.9f);
        Shapes.Triangle(ax + fx * size, ay + fy * size, ax - fx * size * 0.6f + sx * size, ay - fy * size * 0.6f + sy * size,
            ax - fx * size * 0.6f - sx * size, ay - fy * size * 0.6f - sy * size, col, 1f);
    }
}
