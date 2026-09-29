using System.Globalization;
using System.Text;

namespace EveConsole.Localization;

/// <summary>
/// How the game client names a planet, a moon, an asteroid belt and an NPC station, in each of its
/// eight languages: its own formats — the <c>UI/Locations/*Formatter</c> messages in the installed
/// client's localisation tables — and not a translation of the English. The word order and the
/// punctuation are the client's, per language, down to the dash.
///
/// <para>⚠️ Neither ESI nor the SDE carries an NPC station's name in another language: ESI answers
/// English whatever language it is asked in, and the SDE has no station name at all. The client
/// builds one from parts the SDE does translate — what the station orbits, its owner, its operation
/// — so the SDE import builds them here the same way, and stores them as
/// <see cref="EveConsole.Models.SdeNameKind.Station"/>. It also rebuilds every station's English with this code
/// and logs how many equal ESI's: nothing else checks these formats, and the other languages are
/// right only when that count is.</para>
///
/// <para>Language keys are the SDE's: de, en, es, fr, ja, ko, ru, zh. One this does not know is
/// written as the English is. Pure functions: safe from any thread.</para>
/// </summary>
public static class LocationNames
{
    /// <summary>A planet: <c>{system} {roman}</c>, the same in every language.</summary>
    /// <param name="system">The solar system's name in <paramref name="lang"/>.</param>
    /// <param name="celestialIndex">The planet's place from the star, written as a Roman numeral.</param>
    public static string Planet(string lang, string system, int celestialIndex) =>
        $"{system} {Roman(celestialIndex)}";

    /// <summary>A moon, numbered by its orbitIndex around the planet.</summary>
    /// <param name="celestialIndex">The PLANET's celestialIndex, which a moon carries as its own.</param>
    public static string Moon(string lang, string system, int celestialIndex, int orbitIndex)
    {
        var planet = Planet(lang, system, celestialIndex);
        var n      = Number(orbitIndex);
        return lang switch
        {
            "es" => $"{planet} — Luna {n}",      // an em dash
            "fr" => $"{planet} - Lune {n}",
            "ja" => $"{planet} ―衛星{n}",        // U+2015, and no space between it and the number
            "ko" => $"{planet} - 위성 {n}",
            "zh" => $"{planet} - 卫星 {n}",
            // ⚠️ de and ru keep the English word. That is the client, not a gap here.
            _    => $"{planet} - Moon {n}",
        };
    }

    /// <summary>An asteroid belt, numbered like a moon and called by its own type's name.</summary>
    /// <param name="beltType">The name of the belt's type in <paramref name="lang"/> — "Asteroid
    /// Belt" in English, for type 15.</param>
    public static string Belt(string lang, string system, int celestialIndex, string beltType, int orbitIndex)
    {
        var planet = Planet(lang, system, celestialIndex);
        return lang == "es"
            ? $"{planet} — {beltType} {Number(orbitIndex)}"
            : $"{planet} - {beltType} {Number(orbitIndex)}";
    }

    /// <summary>
    /// An NPC station: what it orbits, then its owner, and its operation ("Assembly Plant") where
    /// the station's useOperationName says to name it.
    /// </summary>
    /// <param name="orbit">The name of what the station orbits, in <paramref name="lang"/> — built
    /// by the methods above, or a celestial's own name where it has one.</param>
    /// <param name="operation">The operation's name, or null for a station named without it.</param>
    public static string Station(string lang, string orbit, string corporation, string? operation) =>
        (lang, operation) switch
        {
            ("es", null) => $"{orbit} — {corporation}",
            ("es", _)    => $"{orbit} — {corporation}, {operation}",
            // ⚠️ Russian names the operation FIRST.
            ("ru", null) => $"{orbit} - {corporation}",
            ("ru", _)    => $"{orbit} - {operation} {corporation}",
            (_, null)    => $"{orbit} - {corporation}",
            _            => $"{orbit} - {corporation} {operation}",
        };

    /// <summary>
    /// A planet's celestialIndex as the client writes it, in every language. Zero or less — which
    /// no planet has — is written as the number.
    /// </summary>
    public static string Roman(int n)
    {
        if (n <= 0) return Number(n);
        var sb = new StringBuilder();
        foreach (var (value, numeral) in Numerals)
            while (n >= value) { sb.Append(numeral); n -= value; }
        return sb.ToString();
    }

    private static readonly (int Value, string Numeral)[] Numerals =
        [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
         (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
