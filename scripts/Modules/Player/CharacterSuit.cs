using BmSDK;
using BmSDK.BmGame;
using static CeZeY.Oracle.Catalog;

namespace CeZeY.Oracle;

/// <summary>
/// Changes the played character's suit to any unlocked Showcase suit for them. The pick is recorded the way the
/// Showcase does it (SetActivePlayerSkin). A character the game loaded itself is re-meshed in place: LoadPC
/// streams the suit in and SetupPlayerMesh puts it on. A character Oracle switched to T-poses when re-meshed, so
/// it is switched in again instead, through Characters, and arrives in the new suit.
/// </summary>
internal sealed class CharacterSuit : SkinSwap
{
    public static readonly CharacterSuit I = new();

    private const string ShowcasePage = "Players";

    private enum Swap
    {
        Idle,
        WaitingOnFoot,
        Loading,
    }

    private Choice _suit = null!;
    private Swap _swap;
    private string _pendingId = "";

    protected override string Title => "SUIT";

    protected override string LogName => "Suit";

    protected override string CancelNote => "; it is still the saved pick";

    protected override bool Busy => _swap != Swap.Idle;

    public override void Build()
    {
        _suit = AddChoice("suit", "SUIT", "Shows the suit your character is wearing. Choose another unlocked Showcase suit for them, then run APPLY SUIT.", new[] { "" }, new[] { "OPEN MENU IN GAME" }).Pref();
        AddCommand("apply_suit", "APPLY SUIT", "Puts the selected suit on right where you stand. In the Batmobile it waits until you get out. A character you switched to with PLAY AS is switched in again, wearing it.", Apply);
        AddCommand("cancel_suit", "CANCEL SUIT CHANGE", "Cancels a suit change that is still waiting to go on.", CancelPending);
    }

    public override void LeftGame() => Reset();

    private static bool TryLoaded(RGameRI game, out RGameRI.FLoadedPlayerCharacter loaded) =>
        Showcase.TryRead(() => game.PlayerCharacters[0], out loaded);

    private static bool SameSkin(string? a, string? b) =>
        string.Equals(Showcase.Unprefixed(a), Showcase.Unprefixed(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The skin id ("batman", "nightwing", "dlcredhood", ...) a skin or character name belongs to.</summary>
    private static string SkinId(RPersistentOptions options, string skinOrCharacter) =>
        options.GetActivePlayerSkinId(new FString(skinOrCharacter), false).ToString() ?? "";

    /// <summary>Skin id of the character being played, or "" when it can't be read.</summary>
    private static string PlayedId(RGameRI game)
    {
        if (game.PersistentOptions is not { IsValid: true } options || !TryLoaded(game, out RGameRI.FLoadedPlayerCharacter loaded))
            return "";

        return SkinId(options, loaded.CharacterName.ToString() ?? "");
    }

    /// <summary>On foot and alive, not driving or remote-controlling anything.</summary>
    private static bool OnFoot(RPlayerController pc)
    {
        RPawnPlayer? pawn = pc.CombatPawn;
        return pawn is { IsValid: true, bDeleteMe: false } && pawn.Health > 0 && pc.Pawn?.Ptr == pawn.Ptr;
    }

    protected override void RefreshChoices(RGameInfo info, RGameRI game, RPersistentOptions options)
    {
        string played = PlayedId(game);
        if (played.Length == 0)
        {
            _suit.SetOptions(new[] { "" }, new[] { "NO CHARACTER LOADED" });
            return;
        }

        // One row per Showcase item, in the Showcase's order. Some installs list an item several times with
        // different skins (filler rows pointing at another item's skin); for those, prefer the skin no other item uses.
        info.ShowcaseValidateSorting();
        var order = new List<string>();
        var itemSkins = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var skinItems = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (RGameInfo.FShowcaseItem item in info.ShowcaseItems)
        {
            string skin = item.Skin.ToString() ?? "";
            string key = item.Item.ToString() ?? "";
            if (skin.Length == 0 || key.Length == 0
                || !string.Equals(item.Page.ToString(), ShowcasePage, StringComparison.OrdinalIgnoreCase)
                || !SkinId(options, skin).Equals(played, StringComparison.OrdinalIgnoreCase)
                || !Showcase.IsUnlocked(options, item, key))
                continue;

            if (!itemSkins.TryGetValue(key, out List<string>? skins))
            {
                itemSkins[key] = skins = new List<string>();
                order.Add(key);
            }

            if (!skins.Exists(s => s.Equals(skin, StringComparison.OrdinalIgnoreCase)))
                skins.Add(skin);

            if (!skinItems.TryGetValue(skin, out HashSet<string>? items))
                skinItems[skin] = items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            items.Add(key);
        }

        var keys = new List<string>();
        var labels = new List<string>();
        foreach (string key in order)
        {
            List<string> skins = itemSkins[key];
            string skin = skins.Find(s => skinItems[s].Count == 1) ?? skins[0];
            if (keys.Exists(k => k.Equals(skin, StringComparison.OrdinalIgnoreCase)))
                continue;

            keys.Add(skin);
            labels.Add(Showcase.Label(key, skin));
        }

        if (keys.Count == 0)
        {
            _suit.SetOptions(new[] { "" }, new[] { "NO UNLOCKED SUITS" });
            return;
        }

        // Keep the last pick if it belongs to this character, otherwise show what they're wearing.
        string[] keyArray = keys.ToArray();
        string wearing = TryLoaded(game, out RGameRI.FLoadedPlayerCharacter loaded) ? loaded.MeshName.ToString() ?? "" : "";
        if (wearing.Length == 0)
            wearing = ChosenSkin(options, played);

        _suit.SetOptions(keyArray, labels.ToArray(), Math.Max(0, Array.FindIndex(keyArray, k => SameSkin(k, wearing))));
        _suit.Saved = _suit.CurrentKey;
    }

    /// <summary>The suit picked for a character, as the Showcase shows it.</summary>
    private static string ChosenSkin(RPersistentOptions options, string id)
    {
        foreach (var entry in options.ActivePlayerSkins)
        {
            if (string.Equals(entry.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
                return entry.Skin.ToString() ?? "";
        }

        return "Playable_" + id + "_Std";
    }

    private void Apply()
    {
        string requested = _suit.CurrentKey;
        if (requested.Length == 0)
            throw new InvalidOperationException("no unlocked suit is selected");
        Transition.RequireIdle();

        RGameRI? game = World.Ri;
        RPlayerController? pc = World.Pc;
        if (game == null || game.PersistentOptions is not { IsValid: true } options || pc == null)
            throw new InvalidOperationException("player game state unavailable");

        string played = PlayedId(game);
        if (played.Length == 0 || !SkinId(options, requested).Equals(played, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("that suit is for another character, reopen the menu");

        if (TryLoaded(game, out RGameRI.FLoadedPlayerCharacter current) && SameSkin(current.MeshName.ToString(), requested))
        {
            Reset();
            Notice(_suit.State);
            return;
        }

        // A switched character can't be re-meshed in place, and switching needs them on foot, so check before
        // recording anything.
        if (Characters.I.PlayingOther && World.Pawn is not RPawnPlayer)
            throw new InvalidOperationException("get out of the Batmobile first");

        options.SetActivePlayerSkin(new FString(requested));
        pc.SaveGameOptionsToMemory();
        Note("chose " + requested + " for " + played + ".");

        if (Characters.I.PlayingOther)
        {
            Reset();
            Characters.I.Reload();
            return;
        }

        Pending = requested;
        _pendingId = played;
        _swap = Swap.WaitingOnFoot;
        if (OnFoot(pc))
        {
            // Streams in while the menu is open, so it goes on as the game resumes.
            StartLoad(game);
            Notice("APPLYING SUIT");
            return;
        }

        Menu.ReleaseMovement();
        Notice("GET ON FOOT TO APPLY");
        Note("waiting until you are on foot.");
    }

    private void StartLoad(RGameRI game)
    {
        if (World.Info is not { } info || !TryLoaded(game, out RGameRI.FLoadedPlayerCharacter current))
            throw new InvalidOperationException("no loaded character data available");

        info.LoadPC(current.CharacterName, current.DamageLevel);
        _swap = Swap.Loading;
        Timer.Restart();
    }

    protected override void Step()
    {
        RGameRI? game = World.Ri;
        RPlayerController? pc = World.Pc;
        if (game == null || pc == null)
        {
            Fail("player game state unavailable");
            return;
        }

        if (Characters.I.PlayingOther || !PlayedId(game).Equals(_pendingId, StringComparison.OrdinalIgnoreCase))
        {
            Note("the character changed, " + Pending + " goes on the next time " + _pendingId + " is played.");
            Reset();
            return;
        }

        if (Transition.Busy)
            return;

        if (_swap == Swap.WaitingOnFoot)
        {
            if (OnFoot(pc))
                StartLoad(game);
            return;
        }

        if (!TryLoaded(game, out RGameRI.FLoadedPlayerCharacter c))
        {
            Fail("no loaded character data available");
            return;
        }

        string mesh = c.MeshName.ToString() ?? "";
        if (c.CharacterData == null || (mesh.Length > 0 && c.MeshData == null))
        {
            if (TimedOut)
                Fail("timed out loading " + Pending);
            return;
        }

        if (!OnFoot(pc))
        {
            // Got into a vehicle while it streamed in: put it on once back on foot.
            _swap = Swap.WaitingOnFoot;
            return;
        }

        pc.CombatPawn!.SetupPlayerMesh(true);
        Note("now wearing " + mesh + " (" + Timer.Elapsed.TotalSeconds.ToString("0.00") + " s to load).");
        Reset();
        Notice("CHANGED");
    }

    protected override void Reset()
    {
        _swap = Swap.Idle;
        _pendingId = "";
        base.Reset();
    }
}
