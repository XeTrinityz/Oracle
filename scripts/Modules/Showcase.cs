using BmSDK;
using BmSDK.BmGame;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CeZeY.Oracle;

/// <summary>The game's Showcase: the unlockable skins both skin pickers list, named and gated the way it does.</summary>
internal static class Showcase
{
    private const string PlayablePrefix = "Playable_";

    /// <summary>A skin name without its "Playable_" prefix: "Playable_Batman_Std" and "Batman_Std" are the same skin.</summary>
    public static string Unprefixed(string? skin)
    {
        skin ??= "";
        return skin.StartsWith(PlayablePrefix, StringComparison.OrdinalIgnoreCase) ? skin.Substring(PlayablePrefix.Length) : skin;
    }

    /// <summary>Same rule as RGameInfo.ShowcaseGetTotalCollected.</summary>
    public static bool IsUnlocked(RPersistentOptions options, RGameInfo.FShowcaseItem item, string key)
    {
        if (item.Unlocked)
            return true;

        string prerequisite = item.Prerequisite.ToString() ?? "";
        return options.IsOptionsFlagSet(new FString(prerequisite.Length == 0 ? "Showcase_" + key + "_Unlocked" : prerequisite));
    }

    /// <summary>An item's name as the Showcase shows it, or one made from its skin when the game has none.</summary>
    public static string Label(string key, string skin)
    {
        string label = GameObject.GetLocalisedString(new FString("GFxUI"), new FString("Showcase"), new FString(key), false).ToString() ?? "";
        if (label.Length == 0 || label.StartsWith('?'))
            label = Unprefixed(skin).Replace('_', ' ');
        return label.Trim().ToUpperInvariant();
    }

    /// <summary>Reads one of RGameRI's loaded-player slots, which throws while nothing is loaded there.</summary>
    public static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }
}

/// <summary>
/// A skin change that waits on the game: the pick is saved at once, then put on over the following ticks. The suit
/// and Batmobile skin pickers share this and differ only in what they wait for.
/// </summary>
internal abstract class SkinSwap : Module
{
    private const double LoadTimeout = 20.0;

    protected readonly Stopwatch Timer = new();
    protected string Pending = "";

    /// <summary>Title of its notices, like "SUIT".</summary>
    protected abstract string Title { get; }

    /// <summary>Start of its log lines, like "Suit".</summary>
    protected abstract string LogName { get; }

    /// <summary>Added to the cancel log line, to say whether the pick is still saved.</summary>
    protected virtual string CancelNote => "";

    /// <summary>True while a change is waiting or loading.</summary>
    protected abstract bool Busy { get; }

    protected bool TimedOut => Timer.Elapsed.TotalSeconds > LoadTimeout;

    /// <summary>Fills the picker with what this save has unlocked.</summary>
    protected abstract void RefreshChoices(RGameInfo info, RGameRI game, RPersistentOptions options);

    /// <summary>Moves a waiting change on by one tick.</summary>
    protected abstract void Step();

    public override void WorldChanged() => Reset();

    public override void Shutdown() => Reset();

    public override void MenuOpening()
    {
        if (World.InGame && World.Info is { } info && World.Ri is { PersistentOptions: { IsValid: true } options } game)
            RefreshChoices(info, game, options);
    }

    public override void Tick(float dt)
    {
        if (!Busy)
            return;

        try
        {
            Step();
        }
        catch (Exception ex)
        {
            // A throwing tick would repeat every frame, so fail the change once instead.
            Fail("unexpected error: " + ex.Message);
        }
    }

    protected void Notice(string state, bool ok = true) => Overlay.Notice(Title, state, ok);

    protected void Note(string text) => Log.Info(LogName + ": " + text);

    protected void CancelPending()
    {
        if (!Busy)
        {
            Notice("NOTHING PENDING");
            return;
        }

        Note("change to " + Pending + " cancelled" + CancelNote + ".");
        Reset();
        Notice("CHANGE CANCELLED");
    }

    protected void Fail(string reason)
    {
        Note(reason + ".");
        Reset();
        Notice("CHANGE FAILED - SEE LOG", false);
    }

    protected virtual void Reset()
    {
        Pending = "";
        Timer.Stop();
    }
}
