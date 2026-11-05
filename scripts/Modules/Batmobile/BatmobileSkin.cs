using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>Replaces the Batmobile with an unlocked Showcase skin, which the game can only swap while the car is free.</summary>
internal sealed class BatmobileSkin : SkinSwap
{
    public static readonly BatmobileSkin I = new();

    private const int SettleTicks = 120;
    private const string SkinPrefix = "Playable_Batmobile";
    private const string JokerSkin = "Playable_BatmobileJoker";

    private enum Swap
    {
        Idle,
        WaitingForFree,
        Loading,
        WaitingForReplacement,
    }

    private Choice _skin = null!;
    private Swap _swap;
    private int _settle;
    private int _damage;

    protected override string Title => "BATMOBILE SKIN";

    protected override string LogName => "Batmobile skin";

    protected override bool Busy => _swap != Swap.Idle;

    public override void Build()
    {
        _skin = AddChoice("batmobile_skin", "BATMOBILE SKIN", "Shows the currently loaded skin. Choose another, then run APPLY BATMOBILE SKIN.", new[] { "" }, new[] { "OPEN MENU IN GAME" }).Pref();
        AddCommand("apply_batmobile_skin", "APPLY BATMOBILE SKIN", "Applies the selected skin. It waits until the Batmobile is free, then replaces it; leave the car first if you are driving.", Apply);
        AddCommand("cancel_batmobile_skin", "CANCEL SKIN CHANGE", "Cancels a skin change that is still waiting for the Batmobile to become free.", CancelPending);
    }

    private static bool TryLoaded(RGameRI game, out RGameRI.FLoadedPlayerBatmobile loaded) =>
        Showcase.TryRead(() => game.PlayerBatmobiles[0], out loaded);

    private static string ActiveSkin(RPersistentOptions options) =>
        options.GetActivePlayerSkin(new FString("Batmobile"), true, false, true).ToString() ?? "";

    protected override void RefreshChoices(RGameInfo info, RGameRI game, RPersistentOptions options)
    {
        info.ShowcaseValidateSorting();
        var keys = new List<string>();
        var labels = new List<string>();
        foreach (RGameInfo.FShowcaseItem item in info.ShowcaseItems)
        {
            string skin = item.Skin.ToString() ?? "";
            string key = item.Item.ToString() ?? "";
            if (!skin.StartsWith(SkinPrefix, StringComparison.OrdinalIgnoreCase) || key.Length == 0 || !Showcase.IsUnlocked(options, item, key))
                continue;
            if (keys.Exists(k => k.Equals(skin, StringComparison.OrdinalIgnoreCase)))
                continue;

            keys.Add(skin);
            labels.Add(Showcase.Label(key, skin));
        }

        if (keys.Count == 0)
        {
            _skin.SetOptions(new[] { "" }, new[] { "NO UNLOCKED SKINS" });
            return;
        }

        // Prefer the player's last pick, then whatever is loaded, then the saved active skin.
        string[] keyArray = keys.ToArray();
        int fallback = -1;
        if (TryLoaded(game, out RGameRI.FLoadedPlayerBatmobile loaded))
        {
            fallback = Index(keyArray, loaded.BatmobileName.ToString());
            if (fallback < 0)
                fallback = Index(keyArray, loaded.BatmobileDataName.ToString());
        }

        if (fallback < 0)
            fallback = Index(keyArray, ActiveSkin(options));

        _skin.SetOptions(keyArray, labels.ToArray(), Math.Max(0, fallback));
    }

    private static int Index(string[] keys, string? name) => name == null ? -1 : Array.FindIndex(keys, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void Apply()
    {
        string requested = _skin.CurrentKey;
        if (requested.Length == 0)
            throw new InvalidOperationException("no unlocked Batmobile skin is selected");

        RGameRI? game = World.Ri;
        RPlayerController? pc = World.Pc;
        if (game == null || game.PersistentOptions is not { IsValid: true } options || pc == null)
            throw new InvalidOperationException("player game state unavailable");

        options.SetActivePlayerSkin(new FString(requested));
        pc.SaveGameOptionsToMemory();

        string resolved = ActiveSkin(options);
        if (resolved.Length == 0)
            throw new InvalidOperationException("the game could not resolve the selected Batmobile skin");
        if (!TryLoaded(game, out RGameRI.FLoadedPlayerBatmobile current))
            throw new InvalidOperationException("no loaded Batmobile data available");

        string currentName = current.BatmobileName.ToString() ?? "";
        if (currentName.Equals(resolved, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            Notice(_skin.State);
            return;
        }

        if (currentName.Equals(JokerSkin, StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            Note("saved; the story's Jokerized Batmobile can't be replaced here.");
            Notice("SAVED FOR NEXT BATMOBILE");
            return;
        }

        Pending = resolved;
        _damage = current.DamageLevel;
        _settle = 0;
        _swap = Swap.WaitingForFree;
        Timer.Restart();
        Note("waiting until the Batmobile is free before changing to " + resolved + ".");

        if (World.Pawn is RVehicleBatmobileBase)
        {
            Menu.ReleaseMovement();
            Notice("EXIT VEHICLE TO APPLY");
        }
        else
            Notice(IsFree(pc) ? "APPLYING SKIN" : "WAITING FOR BATMOBILE");
    }

    private static bool IsFree(RPlayerController pc)
    {
        RGameInfo? info = World.Info;
        if (info == null)
            return false;

        RVehicleBatmobileBase? vehicle = info.GetBatmobile();
        return vehicle is not { IsValid: true } || (pc.Pawn?.Ptr != vehicle.Ptr && vehicle.Driver == null);
    }

    protected override void Step()
    {
        RPlayerController? pc = World.Pc;
        if (pc == null)
        {
            Fail("player controller unavailable");
            return;
        }

        switch (_swap)
        {
            case Swap.WaitingForFree:
                _settle = IsFree(pc) ? _settle + 1 : 0;
                if (_settle < SettleTicks)
                    return;

                if (World.Info is not { } info)
                {
                    Fail("game info unavailable");
                    return;
                }

                info.LoadPB(new FName(Pending), _damage);
                _swap = Swap.Loading;
                Timer.Restart();
                Note("loading " + Pending + ".");
                return;

            case Swap.Loading:
                bool ready = false;
                if (World.Ri is { } game && TryLoaded(game, out RGameRI.FLoadedPlayerBatmobile loaded))
                    ready = loaded.BatmobileData != null && ((loaded.MeshName.ToString() ?? "").Length == 0 || loaded.MeshData != null);

                if (!ready)
                {
                    if (TimedOut)
                        Fail("timed out loading " + Pending);
                    return;
                }

                World.Info!.PersistentData.SetPlayableBatmobile(new FName(Pending));
                _swap = Swap.WaitingForReplacement;
                _settle = 0;
                Timer.Restart();
                return;
        }

        _settle = IsFree(pc) ? _settle + 1 : 0;
        if (_settle < SettleTicks)
        {
            if (TimedOut)
                Fail("the Batmobile stayed occupied; the selected skin loads the next time it is called");
            return;
        }

        string applied = Pending;
        pc.CheatBatmobileSkinRepossess();
        if (World.Ri is { } after && TryLoaded(after, out RGameRI.FLoadedPlayerBatmobile now))
        {
            string mesh = now.MeshName.ToString() ?? "";
            if (mesh.Length > 0)
                after.ClearOutRootSetBatmobiles(new FString(mesh));
        }

        // The old vehicle is gone, so drop every cached reference to it.
        Batmobile.I.Forget();
        Reset();
        Note("replaced with " + applied + ".");
        Notice("CHANGED");
    }

    protected override void Reset()
    {
        _swap = Swap.Idle;
        _settle = 0;
        base.Reset();
    }
}
