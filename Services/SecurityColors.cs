using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// EVE's own security colour ramp, keyed on the security value rounded to one decimal — the
/// same rounding the client uses, so 0.45 reads as 0.5 and counts as high sec.
///
/// Shared rather than per-view-model: a security value coloured one way on the map and another
/// way in a grid is a bug the eye catches before the code does.
/// </summary>
public static class SecurityColors
{
    /// <summary>Public so the map legend can group the same stops it colours by.</summary>
    public static readonly (double Sec, Color Color)[] Ramp =
    [
        (1.0, Color.Parse("#2FEFEF")), (0.9, Color.Parse("#48F0C0")),
        (0.8, Color.Parse("#00EF47")), (0.7, Color.Parse("#00F000")),
        (0.6, Color.Parse("#8FEF2F")), (0.5, Color.Parse("#EFEF00")),
        (0.4, Color.Parse("#D77700")), (0.3, Color.Parse("#F06000")),
        (0.2, Color.Parse("#F04800")), (0.1, Color.Parse("#D73000")),
        (0.0, Color.Parse("#F00000")),
    ];

    public static Color Of(double security)
    {
        var s = Math.Round(security, 1, MidpointRounding.AwayFromZero);
        foreach (var (sec, color) in Ramp)
            if (s >= sec) return color;
        return Ramp[^1].Color;   // null sec and below all share the 0.0 colour
    }

    /// <summary>For the string-valued Foreground bindings the grids use.</summary>
    public static string Hex(double security) => Of(security).ToString();

    /// <summary>
    /// One decimal, floored rather than rounded, matching the client: 0.45 is displayed as 0.4
    /// even though it colours as high sec.
    /// </summary>
    public static string Text(double security) => (Math.Floor(security * 10) / 10).ToString("0.0");
}
