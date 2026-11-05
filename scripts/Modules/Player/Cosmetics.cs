using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class Cosmetics : Module
{
    public static readonly Cosmetics I = new();

    public override void Build()
    {
        AddToggle("hide_cape", "HIDE CAPE", "Hides the cape of Batman and other caped characters.", ApplyCape).Maintain(() => ApplyCape(true));
        Slider size = AddSlider("batman_size", "CHARACTER SIZE", "Shrinks or grows your character. Purely visual, the collision stays the same.", 1f, 0.6f, 1.6f, 0.05f, 1.2f, Fmt.Times, ApplySize);
        size.Maintain(() => ApplySize(size.Value));
    }

    private static void ApplySize(float v)
    {
        RPawnPlayer? p = World.Player;
        if (p != null && MathF.Abs(p.DrawScale - v) > 0.001f)
            p.SetDrawScale(v);
    }

    private static void ApplyCape(bool hidden)
    {
        if (World.Player is RPawnPlayerBmBase bm)
        {
            RCapeComponent cape = bm.CapeComponent;
            if (cape is { IsValid: true } && cape.IsCapeHiddenGame != hidden)
                cape.CapeSetHiddenGame(hidden);
        }
    }
}
