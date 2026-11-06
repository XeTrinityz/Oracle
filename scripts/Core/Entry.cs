namespace CeZeY.Oracle;

/// <summary>
/// The only type BmSDK talks to. An exception escaping any of these callbacks exits the game
/// (Loader.RunGuarded calls Environment.Exit), so each one goes straight through <see cref="Safe"/>.
/// </summary>
[Script]
public sealed class OracleMod : Script
{
    public override void Main() => Start();

    public override void OnLoad() => Start();

    public override void OnEnterGame() => Safe.Run("enter game", App.EnterWorld);

    public override void OnEnterMenu() => Safe.Run("enter menu", App.EnterMenu);

    public override void OnTick() => Safe.Run("tick", App.SdkTick);

    /// <summary>One rendered frame, from the viewport: keeps running when game ticks stop (pause, cutscenes).</summary>
    public override void OnDraw(BmSDK.Engine.Canvas canvas) => Safe.Run("frame", () => App.Frame(canvas));

    public override void OnKeyDown(Keys key) => Safe.Run("key", () => App.SdkKey(key));

    public override void OnUnload() => Safe.Run("unload", App.Stop);

    private void Start()
    {
        string dir = Mod.DirectoryPath;
        Safe.Run("start", () => App.Start(dir));
    }
}
