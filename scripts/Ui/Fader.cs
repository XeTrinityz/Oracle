namespace CeZeY.Oracle;

/// <summary>Screen fades shared by anything that moves the player out of sight.</summary>
internal static class Fader
{
    private enum Mode
    {
        Clear,
        Out,
        Dark,
        In,
    }

    private static Mode _mode;
    private static float _time;
    private static float _length;
    private static bool _gentle;
    private static string _title = "";
    private static string _result = "";
    private static bool _ok;

    public static float Level { get; private set; }

    public static string Caption { get; private set; } = "";

    public static bool IsDark => _mode == Mode.Dark;

    public static bool IsClear => _mode == Mode.Clear;

    /// <summary>Fades to black. <paramref name="gentle"/> eases in slowly instead of a quick smooth cut.</summary>
    public static void Out(float seconds, bool gentle, string caption)
    {
        _mode = Mode.Out;
        _time = 0f;
        _length = seconds;
        _gentle = gentle;
        Caption = caption;
    }

    /// <summary>Goes black at once, for paths that must hide the screen immediately.</summary>
    public static void Cut(string caption)
    {
        _mode = Mode.Dark;
        Level = 1f;
        Caption = caption;
    }

    /// <summary>Fades back in, then shows the given result as a toast.</summary>
    public static void In(float seconds, string title = "", string result = "", bool ok = true)
    {
        _mode = Mode.In;
        _time = 0f;
        _length = seconds;
        _title = title;
        _result = result;
        _ok = ok;
    }

    public static void Clear()
    {
        _mode = Mode.Clear;
        Level = 0f;
        Caption = "";
    }

    public static void Advance(float dt)
    {
        switch (_mode)
        {
            case Mode.Out:
                _time += dt;
                Level = _gentle ? Ease.In(_time / _length) : Ease.Smooth(_time / _length);
                if (_time >= _length)
                {
                    Level = 1f;
                    _mode = Mode.Dark;
                }

                break;

            case Mode.In:
                _time += dt;
                Level = 1f - Ease.Smooth(_time / _length);
                if (_time >= _length)
                {
                    Clear();
                    if (_result.Length > 0)
                        Overlay.Toast(_title, _result, _ok);
                }

                break;
        }
    }
}
