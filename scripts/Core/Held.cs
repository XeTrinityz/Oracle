using BmSDK;

namespace CeZeY.Oracle;

/// <summary>
/// One game value Oracle overrides on one object. The first override remembers what the game had, moving to
/// another object puts the last one back, and <see cref="Restore"/> leaves the game exactly as it was found.
/// </summary>
internal sealed class Held<TObj, TVal> where TObj : GameObject
{
    private readonly string _what;
    private readonly Func<TObj, TVal> _get;
    private readonly Action<TObj, TVal> _set;
    private TObj? _obj;
    private TVal _original = default!;

    public Held(string what, Func<TObj, TVal> get, Action<TObj, TVal> set)
    {
        _what = what;
        _get = get;
        _set = set;
    }

    /// <summary>Sets the value, worked out from the game's own one (a scale, say).</summary>
    public void Set(TObj obj, Func<TVal, TVal> fromOriginal)
    {
        if (_obj is not { IsValid: true } held || held.Ptr != obj.Ptr)
        {
            Restore();
            _obj = obj;
            _original = _get(obj);
        }

        TVal want = fromOriginal(_original);
        if (!EqualityComparer<TVal>.Default.Equals(_get(obj), want))
            _set(obj, want);
    }

    public void Set(TObj obj, TVal value) => Set(obj, _ => value);

    /// <summary>Reads the same value off any object, without holding it: what another character has there, say.</summary>
    public TVal Peek(TObj obj) => _get(obj);

    /// <summary>True while this object is the one being held.</summary>
    public bool Holds(TObj obj) => _obj is { IsValid: true } held && held.Ptr == obj.Ptr;

    public void Restore()
    {
        TObj? obj = _obj;
        _obj = null;
        if (obj is { IsValid: true })
            Safe.Run(_what + " restore", () => _set(obj, _original));
    }
}
