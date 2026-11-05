using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

internal sealed class TimeControl : Module
{
    public static readonly TimeControl I = new();

    public override void Build()
    {
        Slider speed = AddSlider("game_speed", "GAME SPEED", "Speeds up or slows down the whole game.", 1f, 0.2f, 2f, 0.05f, 0.5f, Fmt.Times, Apply);
        speed.Maintain(() => Apply(speed.Value));
    }

    public override void Shutdown() => Safe.Run("game speed restore", () => Apply(1f));

    private static void Apply(float v)
    {
        RGameInfo? g = World.Info;
        if (g != null && MathF.Abs(g.DebugGameSpeed - v) > 0.001f)
            g.SetDebugGameSpeed(v);
    }
}
