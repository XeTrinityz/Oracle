namespace CeZeY.Oracle;

/// <summary>
/// State that outlives a hot reload, kept in BmSDK's per-mod store (<see cref="Mod.State"/>). A reload loads a new
/// assembly, so statics reset and Oracle's own types change identity: store only BCL and BmSDK types here, or the
/// cast silently fails after a reload. <see cref="Bind"/> runs before anything else touches Oracle's state.
/// </summary>
internal static class Shared
{
    // Claimed by whichever mod's overlay menu is open, so two menus never fight over the screen. Other mods read it
    // too, so it stays in the process-wide store rather than Oracle's own.
    private const string MenuOwnerKey = "CeZeY.ModMenuOwner";

    private static IDictionary<string, object?> _state = new Dictionary<string, object?>();

    /// <summary>Switches to the mod's store. Anything written before (static initialisers) carries over.</summary>
    public static void Bind(Mod mod)
    {
        if (ReferenceEquals(_state, mod.State))
            return;

        foreach ((string key, object? value) in _state)
            mod.State.TryAdd(key, value);
        _state = mod.State;
    }

    public static T? Get<T>(string key) where T : class => _state.TryGetValue(key, out object? value) ? value as T : null;

    public static T GetValue<T>(string key, T fallback) where T : struct =>
        _state.TryGetValue(key, out object? value) && value is T held ? held : fallback;

    public static void Set(string key, object? value) => _state[key] = value;

    public static T Keep<T>(string key, Func<T> make) where T : class
    {
        T? held = Get<T>(key);
        if (held != null)
            return held;

        T made = make();
        Set(key, made);
        return made;
    }

    public static string? MenuOwner
    {
        get => AppDomain.CurrentDomain.GetData(MenuOwnerKey) as string;
        set => AppDomain.CurrentDomain.SetData(MenuOwnerKey, value);
    }
}
