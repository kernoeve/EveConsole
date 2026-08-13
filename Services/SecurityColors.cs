using Avalonia.Media;

namespace EveConsole.Services;

/// <summary>
/// How a system's security is displayed and coloured, in one place.
///
/// Two numbers matter and they are not interchangeable. <b>True security</b> is the raw value —
/// Pemene is 0.4502, Neziel is 0.4489 — and it drives spawn tables, ore quality and bounty
/// scaling. <b>Security level</b> is that value rounded to one decimal, and it is what decides
/// high / low / null, which is the question most people are actually asking. Verified against
/// dotlan: Pemene (0.4502) shows 0.5, Neziel (0.4489) shows 0.4 — the boundary sits exactly at
/// 0.45 and rounds half away from zero.
///
/// So the displayed value is the rounded one and the colour is derived from the same rounded
/// value, always. Anything showing a number in one convention and a colour from the other tells
/// the reader two different things in one cell — which is the bug this type exists to prevent.
/// True security is offered alongside as a tooltip, never as the headline.
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

    /// <summary>
    /// The security level: true security to one decimal, half away from zero. Every other member
    /// derives from this, so the number shown and the colour beside it can never disagree.
    /// </summary>
    public static double Rounded(double trueSecurity) =>
        Math.Round(trueSecurity, 1, MidpointRounding.AwayFromZero);

    public static Color Of(double trueSecurity)
    {
        var s = Rounded(trueSecurity);
        foreach (var (sec, color) in Ramp)
            if (s >= sec) return color;
        return Ramp[^1].Color;   // null sec and below all share the 0.0 colour
    }

    /// <summary>For the string-valued Foreground bindings the grids use.</summary>
    public static string Hex(double trueSecurity) => Of(trueSecurity).ToString();

    /// <summary>The headline number. Negative values clamp to 0.0, as the client shows them.</summary>
    public static string Text(double trueSecurity)
    {
        var s = Rounded(trueSecurity);
        return (s < 0 ? 0 : s).ToString("0.0");
    }

    /// <summary>
    /// True security. Kept signed — a −0.99 system and a −0.19 system are very different places,
    /// and the rounded headline flattens both to 0.0.
    ///
    /// One to four decimals, trailing zeros dropped. Two is the tidier convention but it fails
    /// at precisely the boundary this exists to explain: Pemene (0.4502) and Neziel (0.4489) both
    /// print "0.45" at two decimals, yet one is high sec and the other is low. A tooltip that
    /// cannot tell those apart is not worth showing.
    /// </summary>
    public static string TrueText(double trueSecurity) => trueSecurity.ToString("0.0###");

    /// <summary>Band name, from the rounded value, for tooltips and grouping.</summary>
    public static string Band(double trueSecurity) => Rounded(trueSecurity) switch
    {
        >= 0.5 => "High sec",
        > 0.0  => "Low sec",
        _      => "Null sec",
    };

    /// <summary>
    /// Tooltip text pairing the two: "High sec · true security 0.45". This is where true
    /// security belongs — available on demand, not competing with the headline.
    /// </summary>
    public static string Tip(double trueSecurity) =>
        $"{Band(trueSecurity)} · true security {TrueText(trueSecurity)}";
}
