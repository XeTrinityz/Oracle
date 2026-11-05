namespace CeZeY.Oracle;

internal readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Mix(Rgba a, Rgba b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Rgba((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    }

    /// <summary>A colour of a hue in degrees at full brightness, as 0..1 channels. Saturation 1 is pure colour.</summary>
    public static (float R, float G, float B) Hue(float degrees, float saturation)
    {
        float h = ((degrees % 360f) + 360f) % 360f / 60f;
        float f = h - MathF.Floor(h);
        float p = 1f - saturation;
        float q = 1f - saturation * f;
        float t = 1f - saturation * (1f - f);
        return ((int)MathF.Floor(h) % 6) switch
        {
            0 => (1f, t, p),
            1 => (q, 1f, p),
            2 => (p, 1f, t),
            3 => (p, q, 1f),
            4 => (t, p, 1f),
            _ => (1f, p, q),
        };
    }

    public static Rgba From(float r, float g, float b) =>
        new((byte)(Math.Clamp(r, 0f, 1f) * 255f), (byte)(Math.Clamp(g, 0f, 1f) * 255f), (byte)(Math.Clamp(b, 0f, 1f) * 255f));
}

/// <summary>The palette and the selected accent colour.</summary>
internal static class Theme
{
    public static readonly Rgba Panel = new(7, 10, 13, 240);
    public static readonly Rgba Hi = new(240, 246, 244);
    public static readonly Rgba Mid = new(176, 192, 188);
    public static readonly Rgba Low = new(112, 128, 126);
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba Off = new(46, 56, 58);
    public static readonly Rgba Warn = new(255, 96, 82);

    public static readonly string[] Keys = { "emerald", "cyan", "blue", "violet", "magenta", "red", "orange", "gold", "white", "rainbow" };
    public static readonly string[] Names = { "EMERALD", "CYAN", "BLUE", "VIOLET", "MAGENTA", "RED", "ORANGE", "GOLD", "WHITE", "RAINBOW" };

    private static readonly Rgba[] Colors =
    {
        new(54, 232, 162), new(64, 206, 255), new(84, 140, 255), new(158, 112, 255), new(240, 92, 220),
        new(255, 76, 76), new(255, 146, 48), new(255, 204, 64), new(226, 234, 238),
    };

    private const int Rainbow = 9;
    private const float RainbowPeriod = 9f;
    private const float LowEndStep = 1f / 15f;
    private const float WheelSaturation = 0.73f;

    public static int Index;
    public static float RainbowSpeed = 1f;
    public static float MenuSize = 0.75f;
    public static bool LowEnd;

    private static float _hue;
    private static float _stepLeft;
    private static Rgba[]? _wheel;

    public static Rgba Accent { get; private set; } = Colors[0];

    public static void Update(float dt)
    {
        if (Index != Rainbow)
        {
            Accent = Colors[Math.Clamp(Index, 0, Colors.Length - 1)];
            return;
        }

        _hue = (_hue + dt * RainbowSpeed / RainbowPeriod) % 1f;
        if (LowEnd)
        {
            _stepLeft -= dt;
            if (_stepLeft > 0f)
                return;
            _stepLeft = LowEndStep;
        }

        Accent = Wheel(_hue);
    }

    public static Rgba Swatch(int pick) => pick == Rainbow ? Wheel((float)(Gfx.Time / 9.0 % 1.0)) : Colors[Math.Clamp(pick, 0, Colors.Length - 1)];

    private static Rgba Wheel(float hue)
    {
        _wheel ??= BuildWheel();
        return _wheel[(int)(hue * _wheel.Length) % _wheel.Length];
    }

    private static Rgba[] BuildWheel()
    {
        var wheel = new Rgba[360];
        for (int i = 0; i < wheel.Length; i++)
        {
            (float r, float g, float b) = Rgba.Hue(i, WheelSaturation);
            wheel[i] = Rgba.From(r, g, b);
        }

        return wheel;
    }
}
