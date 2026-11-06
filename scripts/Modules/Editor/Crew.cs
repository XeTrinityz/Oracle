using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Who is aboard a vehicle, as the game itself keeps it: the driver, and for NPC vehicles the four passenger seats
/// (RVehicleNPC.Passengers). The ESPs use it to fold a crew into their vehicle's marker instead of drawing boxes
/// inside boxes. Only memory reads: no script calls.
/// </summary>
internal static class Crew
{
    /// <summary>Fills <paramref name="into"/> with everyone conscious aboard, each once. Returns how many.</summary>
    public static int Of(Actor a, List<Pawn> into)
    {
        into.Clear();
        if (a is not Vehicle { IsValid: true, bDeleteMe: false } v)
            return 0;

        try
        {
            Add(v.Driver, into);
            if (v is RVehicleNPC n)
            {
                Add(n.Passengers_0.Pawn, into);
                Add(n.Passengers_1.Pawn, into);
                Add(n.Passengers_2.Pawn, into);
                Add(n.Passengers_3.Pawn, into);
            }
        }
        catch (Exception ex)
        {
            if (Log.Due("crew read"))
                Log.Info("Crew: couldn't read who is aboard " + a.Name + ": " + Log.Why(ex));
        }

        return into.Count;
    }

    private static void Add(Pawn? p, List<Pawn> into)
    {
        if (p is not { IsValid: true, bDeleteMe: false } || p.Health <= 0 || p is RPawnVillain { bGoodAsDead: true })
            return;
        if (p.Ptr == World.PawnPtr || p.Ptr == (World.Player?.Ptr ?? 0))
            return;
        foreach (Pawn q in into)
        {
            if (q.Ptr == p.Ptr)
                return;
        }

        into.Add(p);
    }

    /// <summary>Enemies are red, anyone else green, as the editor's ESP colours them.</summary>
    public static bool Hostile(Pawn p) => p is RPawnVillain;

    /// <summary>
    /// The crew's pips: a row of small diamonds centred on <paramref name="cx"/>, enemies first. Queued into Lens in
    /// <paramref name="queue"/>; <see cref="FlushPips"/> draws them all, one batch per colour.
    /// </summary>
    public static void Pips(IReadOnlyList<Pawn> crew, float cx, float cy, List<(float X, float Y, bool Hostile)> queue)
    {
        int n = Math.Min(crew.Count, 6);
        float step = Lens.L(9f);
        float x = cx - step * (n - 1) * 0.5f;
        int i = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (Pawn p in crew)
            {
                if (i >= n || Hostile(p) != (pass == 0))
                    continue;
                queue.Add((x + step * i, cy, pass == 0));
                i++;
            }
        }
    }

    /// <summary>Draws every queued pip: one batch per colour.</summary>
    public static void FlushPips(List<(float X, float Y, bool Hostile)> queue)
    {
        float r = MathF.Max(2.5f, Lens.L(3.5f));
        for (int pass = 0; pass < 2; pass++)
        {
            bool any = false;
            foreach (var q in queue)
            {
                if (q.Hostile != (pass == 0))
                    continue;
                Lens.Diamond(q.X, q.Y, r);
                any = true;
            }

            if (any)
                Lens.Flush(Things.Colors[pass == 0 ? (int)ThingKind.Enemy : (int)ThingKind.Friendly]);
        }

        queue.Clear();
    }

    /// <summary>The crew as card lines: a count, then each one's name and health in their colour.</summary>
    public static void Lines(IReadOnlyList<Pawn> crew, List<(string, Rgba)> lines)
    {
        if (crew.Count == 0)
            return;

        int hostile = 0;
        foreach (Pawn p in crew)
            hostile += Hostile(p) ? 1 : 0;
        string count = hostile == crew.Count ? (hostile == 1 ? "1 ENEMY" : hostile + " ENEMIES") : crew.Count + " ABOARD" + (hostile > 0 ? ", " + hostile + " HOSTILE" : "");
        lines.Add(("CREW  " + count, Theme.Hi));
        foreach (Pawn p in crew)
        {
            string hp = p.HealthMax > 0 ? "   " + Math.Max(0, p.Health) + " / " + p.HealthMax + " HP" : "";
            lines.Add(("  " + Things.Wrap(p).Name + hp, Things.Colors[Hostile(p) ? (int)ThingKind.Enemy : (int)ThingKind.Friendly]));
        }
    }
}
