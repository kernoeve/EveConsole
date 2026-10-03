using System.Globalization;
using System.Resources;

namespace EveConsole.Localization;

/// <summary>
/// Counted phrases, in the form each language needs for the number.
///
/// <para>⚠️ Not "{0} item(s)", and not a one-or-many test. English has two forms, Chinese, Japanese
/// and Korean one, French counts zero as singular, and Russian has three: 1 предмет, 2 предмета,
/// 5 предметов — and 21 is singular again. So a counted phrase is a family of entries named for
/// the forms, as CLDR names them: <c>ItemsOne</c> and <c>ItemsOther</c> in English, plus
/// <c>ItemsFew</c> and <c>ItemsMany</c> in Russian, and <c>ItemsOther</c> alone in Chinese. The
/// number is {0}.</para>
/// </summary>
public static class Plurals
{
    /// <summary>
    /// The phrase for <paramref name="n"/>, formatted: <c>Plurals.Format(OrdersText.ResourceManager,
    /// nameof(OrdersText.ItemsOther), count)</c>. Naming the family by its Other entry through
    /// nameof is what makes a mistyped family a build error.
    /// </summary>
    /// <param name="more">Further values, from {1} on.</param>
    public static string Format(ResourceManager text, string otherKey, long n, params object?[] more)
    {
        const string other = "Other";
        if (!otherKey.EndsWith(other, StringComparison.Ordinal))
            throw new ArgumentException($"A plural family is named by its Other entry, not \"{otherKey}\".", nameof(otherKey));

        var stem = otherKey[..^other.Length];
        var form = text.GetString(stem + Category(n, CultureInfo.CurrentUICulture))
                ?? text.GetString(otherKey)
                ?? otherKey;
        return string.Format(CultureInfo.CurrentCulture, form, [n, .. more]);
    }

    /// <summary>
    /// CLDR's plural category of a whole number in a language: "One", "Few", "Many" or "Other".
    /// The eight EVE languages; anything else is treated like English.
    /// </summary>
    public static string Category(long n, CultureInfo language)
    {
        n = Math.Abs(n);
        switch (language.TwoLetterISOLanguageName)
        {
            case "zh" or "ja" or "ko":
                return "Other";
            case "fr":
                return n is 0 or 1 ? "One" : "Other";
            case "ru" or "uk":
                var tens = n % 100;
                return (n % 10) switch
                {
                    1 when tens != 11                           => "One",
                    >= 2 and <= 4 when tens is < 12 or > 14     => "Few",
                    _                                           => "Many",
                };
            default:
                return n == 1 ? "One" : "Other";
        }
    }
}
