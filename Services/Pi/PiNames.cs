using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.Services.Pi;

/// <summary>
/// Planet, system, type and schematic names as the PI screens and tasks show them: in the
/// interface language where the SDE has one, otherwise in English.
/// </summary>
public static class PiNames
{
    /// <summary>
    /// A planet: its system's name in the interface language and its Roman numeral, which is how
    /// the client writes a planet in every language (<see cref="LocationNames.Planet"/>). Built
    /// from the SDE's English name, which is the system's English name and the numeral.
    /// </summary>
    public static string Planet(int planetId, string englishPlanet, int systemId, string englishSystem)
    {
        if (englishPlanet.Length == 0) return string.Format(PiText.PlanetWithId, planetId);
        if (englishSystem.Length > 0 && englishPlanet.StartsWith(englishSystem + " ", StringComparison.Ordinal))
            return SdeNames.SolarSystem(systemId, englishSystem) + englishPlanet[englishSystem.Length..];
        return englishPlanet;
    }

    public static string Planet(PiColonyStatus c) => Planet(c.PlanetId, c.PlanetName, c.SolarSystemId, c.SystemName);

    public static string System(int systemId, string english) => SdeNames.SolarSystem(systemId, english);

    /// <summary>
    /// The planet's type — "Barren", "Gas" — as the client names it in the interface language: the
    /// part in brackets of its SDE type's name ("Planet (Barren)"). Without the SDE, ESI's own word.
    /// </summary>
    public static string PlanetType(int planetTypeId, string englishTypeName, string esiWord)
    {
        var name = planetTypeId > 0 && englishTypeName.Length > 0 ? SdeNames.Type(planetTypeId, englishTypeName) : "";
        if (name.Length > 0)
        {
            var open  = name.IndexOfAny(['(', '（']);
            var close = name.LastIndexOfAny([')', '）']);
            return open >= 0 && close > open + 1 ? name[(open + 1)..close].Trim() : name;
        }
        return esiWord.Length > 0 ? char.ToUpperInvariant(esiWord[0]) + esiWord[1..] : "";
    }

    /// <summary>An item type, by the English name the caller read from the SDE.</summary>
    public static string Type(int typeId, string? english)
        => SdeNames.Type(typeId, string.IsNullOrEmpty(english) ? string.Format(PiText.TypeWithId, typeId) : english);

    public static string Schematic(int schematicId, string english)
        => SdeNames.Get(SdeNameKind.PlanetSchematic, schematicId, english);
}

/// <summary>Durations and moments as the PI screens and tasks write them.</summary>
public static class PiFormat
{
    /// <summary>"3d 4h", "5h 20m" or "12m"; the sign is the caller's ("in …", "… ago").</summary>
    public static string Duration(TimeSpan span)
    {
        var d = span.Duration();
        return d.TotalDays >= 1
            ? string.Format(PiText.DurationDaysHours, (int)d.TotalDays, d.Hours)
            : string.Format(CommonText.DurationHoursMinutes, d.Hours, d.Minutes);
    }

    /// <summary>"in 5h 20m" ahead of now, "3d 4h ago" behind it.</summary>
    public static string Relative(DateTimeOffset at, DateTimeOffset now)
        => at > now ? string.Format(PiText.InDuration, Duration(at - now))
                    : string.Format(PiText.AgoDuration, Duration(now - at));

    /// <summary>A moment in local time; with its year when it is not this year's.</summary>
    public static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString(CommonText.DateMonthDayTime)
            : local.ToString(CommonText.DateMonthDayYearTime);
    }
}
