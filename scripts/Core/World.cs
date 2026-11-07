using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Validated view of the player and game objects. Each Game.TryGet* runs UnrealScript, so everything reads the
/// cached references here instead. Getters re-check IsValid so a destroyed wrapper is never handed out: a call on
/// one throws, and a property read faults.
/// </summary>
internal static class World
{
    private const double RefindSeconds = 0.25;

    private static RPlayerController? _pc;
    private static RGameInfo? _info;
    private static Every _refind;
    private static nint _checkedPawn;
    private static bool _frontend;

    public static RPlayerController? Pc => _pc is { IsValid: true } ? _pc : null;

    public static RGameInfo? Info => _info is { IsValid: true } ? _info : null;

    public static Pawn? Pawn
    {
        get
        {
            Pawn? pawn = Pc?.Pawn;
            return pawn is { IsValid: true } ? pawn : null;
        }
    }

    public static nint PawnPtr => Pawn?.Ptr ?? 0;

    /// <summary>The player's character, including while it is not the possessed pawn (driving, for example).</summary>
    public static RPawnPlayer? Player
    {
        get
        {
            RPlayerController? pc = Pc;
            if (pc == null)
                return null;
            if (pc.Pawn is RPawnPlayer owned && owned.IsValid)
                return owned;

            RPawnPlayer combat = pc.CombatPawn;
            return combat is { IsValid: true } ? combat : null;
        }
    }

    /// <summary>True in a playable world, false in the main menu or while nothing is possessed.</summary>
    public static bool InGame { get; private set; }

    public static bool InFrontend => _frontend;

    /// <summary>
    /// Whether the game is paused, asked once per tick or frame: IsPaused runs UnrealScript, and the overlay, pad,
    /// radial and music all want it every frame.
    /// </summary>
    public static bool Paused
    {
        get
        {
            if (_paused == 0)
                _paused = Safe.Try("pause check", () => Pc is { } pc && pc.IsPaused()) ? 2 : 1;
            return _paused == 2;
        }
    }

    private static int _paused; // 0 not asked since the last refresh, 1 running, 2 paused

    public static void Refresh()
    {
        _paused = 0;
        if (Pc == null || Info == null || _refind.Elapsed(RefindSeconds))
            Refind();

        Pawn? pawn = Pawn;
        if (pawn == null)
        {
            InGame = false;
            return;
        }

        if (pawn.Ptr != _checkedPawn)
        {
            _checkedPawn = pawn.Ptr;
            _frontend = pawn.GetPathName(null).StartsWith("frontend.", StringComparison.OrdinalIgnoreCase);
        }

        InGame = !_frontend;
    }

    public static void Forget()
    {
        _pc = null;
        _info = null;
        _checkedPawn = 0;
        _paused = 0;
        _refind.Force();
        InGame = false;
    }

    private static void Refind()
    {
        _pc = Game.TryGetPlayerController(out RPlayerController? pc) ? pc : null;
        _info = _pc != null && Game.TryGetGameInfo(out RGameInfo? info) ? info : null;
    }

    /// <summary>The game's replication info: streaming, flags and the loaded player data. Null when unavailable.</summary>
    public static RGameRI? Ri => Game.TryGetGameRI(out RGameRI? ri) ? ri : null;

    public static WorldInfo? WorldInfo => Game.TryGetWorldInfo(out WorldInfo? wi) ? wi : null;

    /// <summary>The global engine object, which lives for the whole session (main menu included).</summary>
    public static _Engine? GameEngine => Game.TryGetEngine(out GameEngine? engine) ? engine : null;

    /// <summary>The cheat manager, which Game creates on first use. Null without a player controller.</summary>
    public static RCheatManager? Cheats => Fetch(Game.GetCheatManager);

    /// <summary>The cheat manager, or a refusal a command can show.</summary>
    public static RCheatManager RequireCheats() => Cheats ?? throw new InvalidOperationException("cheat manager unavailable");

    /// <summary>The possessed pawn, or a refusal a command can show.</summary>
    public static Pawn RequirePawn() => Pawn ?? throw new InvalidOperationException("the game is still loading");

    /// <summary>A Game.Get* object, valid or null: those calls throw instead of returning null when it's missing.</summary>
    private static T? Fetch<T>(Func<T> get) where T : GameObject
    {
        try
        {
            T found = get();
            return found is { IsValid: true } ? found : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Enumerates live objects of a type. FindObjects is a per-type list, so this is cheap for rare types.</summary>
    public static IEnumerable<T> All<T>() where T : GameObject
    {
        foreach (T obj in Game.FindObjects<T>())
        {
            if (obj is { IsValid: true } && !obj.IsClassDefaultObject)
                yield return obj;
        }
    }
}
