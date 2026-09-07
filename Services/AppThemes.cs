using Avalonia.Styling;

namespace EveConsole.Services;

/// <summary>
/// The theme variants beyond plain light and dark.
///
/// <para>⚠️ Each one INHERITS from Light or Dark, and that is the whole design rather than a
/// shortcut. A variant only has to define what it changes; everything it leaves out resolves
/// through its parent. So these tint the neutrals — surfaces, lines, text — and the accent, and
/// say nothing at all about the colours that carry meaning.</para>
///
/// <para>That is deliberate. Good, bad, warning and informational are tuned to stay legible and
/// distinct on their parent's surfaces, and they are the colours a chart line, a status word or a
/// row tint is drawn in. Letting a theme restate them is how a green "profit" ends up
/// indistinguishable from a teal background — the exact blending risk that a palette of six
/// tints invites. A tint changes the room; it does not change what the signals mean.</para>
///
/// <para>⚠️ The parent also decides how anything NOT yet themed reads. Several colours are still
/// literals — chart series, EVE's security ramp, the slot bands — and they were chosen against a
/// dark ground or a light one. A variant that inherits Dark keeps them on a dark ground, so they
/// stay as legible as they are today rather than becoming a new problem per tint.</para>
/// </summary>
public static class AppThemes
{
    public static readonly ThemeVariant BlueDark   = new("BlueDark",   ThemeVariant.Dark);
    public static readonly ThemeVariant BlueLight  = new("BlueLight",  ThemeVariant.Light);
    public static readonly ThemeVariant PinkDark   = new("PinkDark",   ThemeVariant.Dark);
    public static readonly ThemeVariant PinkLight  = new("PinkLight",  ThemeVariant.Light);
    public static readonly ThemeVariant BeigeDark  = new("BeigeDark",  ThemeVariant.Dark);
    public static readonly ThemeVariant BeigeLight = new("BeigeLight", ThemeVariant.Light);
}
