using System.Globalization;
using System.Numerics;

namespace CeZeY.Oracle;

/// <summary>Value formats shared by menu rows, notices and the log.</summary>
internal static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Times(float v) => v.ToString("0.00", Inv) + "x";

    public static string Multiplier(float v) => Whole(v) + "x";

    public static string Percent(float v) => Whole(v * 100f) + "%";

    public static string Count(float v) => Whole(v);

    public static string Secs(float v) => Whole(v) + " s";

    public static string Tenths(float v) => v.ToString("0.0", Inv) + " s";

    public static string Degrees(float v) => Whole(v) + " DEG";

    public static string Metres(float v) => Whole(v) + " M";

    public static string MetresOrOff(float v) => v <= 0f ? "OFF" : Metres(v);

    public static string Signed(float v) => MathF.Abs(v) < 0.01f ? "DEFAULT" : (v > 0 ? "+" : "") + Whole(v);

    public static string Side(float v)
    {
        if (MathF.Abs(v) < 0.01f)
            return "CENTRED";

        string pct = Percent(MathF.Abs(v));
        return v < 0f ? "LEFT " + pct : "RIGHT " + pct;
    }

    /// <summary>"1 TANK", "3 TANKS".</summary>
    public static string Plural(int n, string one) => n + " " + one + (n == 1 ? "" : "S");

    /// <summary>A world position in whole units: "1200, -350, 80".</summary>
    public static string Position(Vector3 v) => string.Format(Inv, "{0:0}, {1:0}, {2:0}", v.X, v.Y, v.Z);

    private static string Whole(float v) => v.ToString("0", Inv);
}
