namespace CeZeY.Oracle;

/// <summary>
/// State that outlives a hot reload. A reload loads a new assembly, so statics reset and Oracle's own types
/// change identity: store only BCL and BmSDK types here, or the cast silently fails after a reload.
/// </summary>
internal static class Shared
{
    private const string Prefix = "CeZeY.Oracle.";

    // Claimed by whichever mod's overlay menu is open, so two menus never fight over the screen.
    private const string MenuOwnerKey = "CeZeY.ModMenuOwner";

    public static T? Get<T>(string key) where T : class => AppDomain.CurrentDomain.GetData(Prefix + key) as T;

    public static T GetValue<T>(string key, T fallback) where T : struct =>
        AppDomain.CurrentDomain.GetData(Prefix + key) is T value ? value : fallback;

    public static void Set(string key, object? value) => AppDomain.CurrentDomain.SetData(Prefix + key, value);

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
