using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>What a blueprint on screen is: not a print at all, an original or a copy.</summary>
public enum PrintKind { None, Original, Copy }

/// <summary>
/// How a blueprint is named and drawn wherever one is shown: a copy with the light-blue copy
/// picture and "(Copy)" after its name, an original with the dark-blue picture and "(Original)".
///
/// <para>⚠️ One rule in one place. A trip's manifest said "Large Capacitor Control Circuit I
/// Blueprint ×1" for a copy, beside a hangar holding the original as well, and read as an order to
/// move the original. A name alone cannot say which; the picture and the suffix together do.</para>
///
/// <para>Reaction formulas are always originals and are left as they are: the caller passes
/// <see cref="PrintKind.None"/> for one, as for anything that is not a blueprint.</para>
/// </summary>
public static class PrintLabel
{
    /// <summary>The type's name as the screen shows it, with the suffix for its kind.</summary>
    public static string Name(long typeId, string english, PrintKind kind) =>
        Suffix(SdeNames.Type(typeId, english), kind);

    /// <summary>A name already as the screen shows it, with the suffix for its kind.</summary>
    public static string Suffix(string shown, PrintKind kind) => kind switch
    {
        PrintKind.Copy     => string.Format(CommonText.PrintCopyName, shown),
        PrintKind.Original => string.Format(CommonText.PrintOriginalName, shown),
        _                  => shown,
    };

    /// <summary>The image server's picture for it: "bpc" for a copy, "bp" for an original.</summary>
    public static string Picture(PrintKind kind) => kind == PrintKind.Copy ? "bpc" : "bp";

    public static PrintKind Of(bool isOriginal) => isOriginal ? PrintKind.Original : PrintKind.Copy;
}
