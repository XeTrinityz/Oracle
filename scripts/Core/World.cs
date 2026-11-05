using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace CeZeY.Oracle;

/// <summary>
/// Validated view of the player and game objects. Game.Get* throw when an object is missing and each runs
/// UnrealScript, so everything reads the cached references here instead. Getters re-check IsValid because
/// touching a destroyed wrapper dereferences a null pointer, which no try/catch can recover from.
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

    public static bool Paused => Safe.Try("pause check", () => Pc is { } pc && pc.IsPaused());

    public static void Refresh()
    {
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
        _refind.Force();
        InGame = false;
    }

    private static void Refind()
    {
        try
        {
            _pc = Game.GetPlayerController(0);
        }
        catch
        {
            _pc = null;
        }

        try
        {
            _info = _pc == null ? null : Game.GetGameInfo();
        }
        catch
        {
            _info = null;
        }
    }

    /// <summary>The game's replication info: streaming, flags and the loaded player data. Null when unavailable.</summary>
    public static RGameRI? Ri => Fetch(Game.GetGameRI);

    public static WorldInfo? WorldInfo => Fetch(Game.GetWorldInfo);

    /// <summary>The global engine object, which lives for the whole session (main menu included).</summary>
    public static _Engine? GameEngine => Fetch(Game.GetEngine);

    public static RCheatManager? Cheats => Fetch(Game.GetCheatManager);

    /// <summary>The cheat manager, or a refusal a command can show.</summary>
    public static RCheatManager RequireCheats() => Cheats ?? throw new InvalidOperationException("cheat manager unavailable");

    /// <summary>The possessed pawn, or a refusal a command can show.</summary>
    public static Pawn RequirePawn() => Pawn ?? throw new InvalidOperationException("the game is still loading");

    /// <summary>A Game.Get* singleton, valid or null: those calls throw instead of returning null when it's missing.</summary>
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
