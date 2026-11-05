namespace CeZeY.Oracle;

/// <summary>True while something has the player hidden behind a fade.</summary>
internal static class Transition
{
    public static bool Busy => Teleport.I.Busy || Characters.I.Busy;

    /// <summary>Refuses, with a reason the player sees, while a fade is running or the world is still loading.</summary>
    public static void RequireIdle()
    {
        if (Busy)
            throw new InvalidOperationException("already teleporting or switching");
        if (!Catalog.Ready)
            throw new InvalidOperationException("the game is still loading");
    }
}
